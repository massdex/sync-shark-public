using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Checks settings defaults, provider URLs and user-visible messages at their source owners.</summary>
public static class LiteralsChecks
{
    public static Task RunAsync(CheckArguments input)
    {
        Check.Require(Offends("class A { void Fail() => throw new Exception(\"A failure occurred.\"); }", "A.cs"), "Unowned message not detected");
        Check.Require(!Offends("class A { void Fail() => throw new Exception(nameof(A)); }", "A.cs"), "Name expression rejected");
        foreach (var folder in new[] { "src", "apps" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(input.Root, folder), "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetRelativePath(input.Root,file).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")) continue;
                Check.Require(!Offends(File.ReadAllText(file), Path.GetFileName(file)), file);
            }
        return Task.CompletedTask;
    }
    private static bool Offends(string source, string file)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.Value is string text && (text.StartsWith("https://", StringComparison.Ordinal) ||
                text.Any(char.IsWhiteSpace) && literal.Ancestors().OfType<ObjectCreationExpressionSyntax>().Any(x => x.Type.ToString().EndsWith("Exception", StringComparison.Ordinal)))) return true;
            if (file == "EngineSettings.cs" && literal.Parent is EqualsValueClauseSyntax && literal.Token.Value is int or double) return true;
        }
        return false;
    }
}
