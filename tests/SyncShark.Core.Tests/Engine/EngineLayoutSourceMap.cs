namespace Massdex.SyncShark.EngineChecks;

// Filenames, not namespaces, own engine types (including legacy-namespaced moves).
internal sealed class EngineLayoutSourceMap
{
    internal sealed record Source(string Path, string Text);
    internal readonly Dictionary<Type, string> Owners = new();
    internal readonly List<(Source Source, string Module)> Sources = new();
    internal static readonly string[] Modules =
        ["Contracts", "Storage", "Local", "Dropbox", "Planning", "Execution", "Run", "Edge"];

    internal EngineLayoutSourceMap(IEnumerable<Source> sources, Type[] types, Action<string, string> error)
    {
        var top = types.Where(t => !t.IsNested).ToArray();
        var families = new Dictionary<string, List<(Source Source, string Module)>>(StringComparer.Ordinal);
        foreach (var source in sources.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            var path = source.Path.Replace('\\', '/');
            var parts = path.Split('/');
            var module = parts.Length == 1 ? "Contracts" : parts.Length == 2 ? parts[0] switch
            {
                "Storage" => "Storage", "Local" => "Local", "Dropbox" => "Dropbox",
                "Planner" => "Planning", "Executor" => "Execution", "Run" => "Run",
                "Adapters" => "Edge", _ => "Unknown"
            } : "Unknown";
            if (module == "Unknown") error("Ownership", $"{path}: unknown module folder");
            var name = System.IO.Path.GetFileNameWithoutExtension(path).Split('.')[0];
            if (!families.TryGetValue(name, out var family)) families[name] = family = new();
            family.Add((source, module));
            Sources.Add((source, module));
            var declarations = Declarations(source.Text).ToArray();
            if (declarations.Length != 1 || declarations[0] != name)
                error("Ownership", $"{path}: expected one top-level declaration named {name}; found {string.Join(", ", declarations)}");
        }
        foreach (var (name, family) in families)
        {
            var candidates = top.Where(t => t.Name == name).ToArray();
            if (candidates.Length != 1)
            {
                error("Ownership", $"{name}: expected one compiled top-level candidate, found {candidates.Length} (missing/excluded or ambiguous source)");
                continue;
            }
            var modules = family.Select(f => f.Module).Distinct().ToArray();
            if (modules.Length != 1) error("Ownership", $"{name}: partial family crosses folders: {string.Join(", ", modules)}");
            if (family.Count(f => System.IO.Path.GetFileName(f.Source.Path) == name + ".cs") != 1)
                error("Ownership", $"{name}: expected exactly one canonical {name}.cs");
            Owners[candidates[0]] = modules[0];
        }
    }

    internal static string FindRoot()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "SyncShark.sln"))) return dir.FullName;
        throw new InvalidOperationException("Engine layout checks require the source checkout (SyncShark.sln).");
    }

    internal static IEnumerable<string> Declarations(string text)
    {
        var tokens = Tokens(text);
        var depth = 0;
        var namespaceDepth = 0;
        var namespacePending = false;
        var parentheses = 0;
        var brackets = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "(") parentheses++;
            if (token == ")") parentheses--;
            if (token == "[") brackets++;
            if (token == "]") brackets--;
            // Contextual keywords in parameters or attributes are identifiers, not declarations.
            if (parentheses != 0 || brackets != 0) continue;
            if (token == "namespace") namespacePending = true;
            if (token == ";") namespacePending = false;
            if (token == "{") { depth++; if (namespacePending) { namespaceDepth = depth; namespacePending = false; } }
            if (token == "}") { if (depth == namespaceDepth) namespaceDepth = 0; depth--; }
            if (depth != namespaceDepth || i + 1 >= tokens.Count) continue;
            if (token is not ("class" or "struct" or "interface" or "record" or "enum" or "delegate")) continue;
            if (i > 0 && tokens[i - 1] == "record") continue;
            var n = i + 1;
            if (token == "record" && tokens[n] is "class" or "struct") n++;
            if (token == "delegate")
            {
                while (n + 1 < tokens.Count && tokens[n + 1] != "(") n++;
            }
            if (n < tokens.Count) yield return tokens[n];
        }
    }

    // Deliberately lexical: comments, literals and XML documentation cannot create edges.
    // Interpolation expressions remain code; literal portions are discarded.
    internal static List<string> Tokens(string text)
    {
        var result = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
            if (i + 1 < text.Length && c == '/' && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i + 1 < text.Length && c == '/' && text[i + 1] == '*')
            { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 2; continue; }
            var start = i;
            while (i < text.Length && text[i] is '$' or '@') i++;
            if (i < text.Length && text[i] is '\"' or '\'')
            {
                var quote = text[i++];
                var prefix = text[start..(i - 1)];
                var verbatim = prefix.Contains('@');
                var interpolated = prefix.Contains('$');
                var quotes = 1;
                if (quote == '\"') while (i < text.Length && text[i] == '\"') { quotes++; i++; }
                if (quotes == 2) continue;
                while (i < text.Length)
                {
                    if (quotes >= 3)
                    {
                        if (text.AsSpan(i).StartsWith(new string('\"', quotes))) { i += quotes; break; }
                    }
                    else if (text[i] == quote)
                    {
                        i++;
                        if (verbatim && i < text.Length && text[i] == quote) { i++; continue; }
                        break;
                    }
                    if (!verbatim && quotes < 3 && text[i] == '\\') { i = Math.Min(i + 2, text.Length); continue; }
                    if (interpolated && text[i] == '{')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '{') { i += 2; continue; }
                        var expression = ++i;
                        var braces = 1;
                        while (i < text.Length && braces > 0)
                        { if (text[i] == '{') braces++; if (text[i] == '}') braces--; if (braces > 0) i++; }
                        result.AddRange(Tokens(text[expression..i]));
                        if (i < text.Length) i++;
                        continue;
                    }
                    i++;
                }
                continue;
            }
            i = start;
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                result.Add(text[start..i].TrimStart('@'));
            }
            else { result.Add(c.ToString()); i++; }
        }
        return result;
    }
}
