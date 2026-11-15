using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Source-bound string occurrences for the ratchet; LiteralsChecks owns every permission.</summary>
internal static class LiteralSites
{
    internal static void Write(string candidate, string release, string output)
    {
        using var stream = new StreamWriter(output, false, new UTF8Encoding(false));
        foreach (var (tree, root) in new[] { ("candidate", candidate), ("release", release) })
        foreach (var file in Files(Path.Combine(root, "src")))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            var source = File.ReadAllText(file).Replace("\r\n", "\n").Replace('\r', '\n');
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
            stream.WriteLine(JsonSerializer.Serialize(new { tree, path, hash, sites = Sites(source, path) }));
        }
    }

    private static IEnumerable<string> Files(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("literal artifact: source symlink");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Path.GetFileName(entry) is not ("bin" or "obj" or ".git"))
                    foreach (var child in Files(entry)) yield return child;
            }
            else if (Path.GetExtension(entry) == ".cs") yield return entry;
        }
    }

    internal static IEnumerable<object> Sites(string source, string path)
    {
        // The lexical inventory includes inactive branches, but not directive lines.
        var options = new CSharpParseOptions(LanguageVersion.CSharp12);
        var root = CSharpSyntaxTree.ParseText(source, options).GetRoot();
        if (root.ContainsDirectives)
        {
            var parsed = source.ToCharArray();
            foreach (var directive in root.DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.IsDirective))
                for (var i = directive.SpanStart; i < directive.Span.End; i++)
                    if (parsed[i] != '\n') parsed[i] = ' ';
            root = CSharpSyntaxTree.ParseText(new string(parsed), options).GetRoot();
        }
        SemanticModel? model = null;
        var forbidden = new HashSet<int>();
        foreach (var token in root.DescendantTokens())
            if (LiteralsChecks.StringViolation(token, path, ref model) is not null) forbidden.Add(token.SpanStart);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var line = 1;
        var previous = 0;
        foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>().Where(node =>
            node is InterpolatedStringExpressionSyntax || node is LiteralExpressionSyntax literal &&
            LiteralsChecks.IsStringLiteral(literal.Token)))
        {
            for (var i = previous; i < expression.SpanStart; i++)
                if (source[i] == '\n') { line++; ordinals.Clear(); }
            previous = expression.SpanStart;
            var text = source.Substring(expression.SpanStart, expression.Span.Length);
            var ordinal = ordinals.GetValueOrDefault(text);
            ordinals[text] = ordinal + 1;
            var permitted = !expression.DescendantTokens().Any(token => forbidden.Contains(token.SpanStart));
            yield return new { line, text, ordinal, permitted };
        }
    }
}
