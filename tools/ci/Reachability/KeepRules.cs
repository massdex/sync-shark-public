using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed class KeepRules(Analysis graph)
{
    internal static readonly Regex Interop = new(@"\b(DllImport|ComImport|ComVisible|StructLayout|UnmanagedFunctionPointer|extern|dynamic)\b|\[Guid\(|GetFunctionPointerForDelegate|Activator\s*\.\s*CreateInstance|Type\s*\.\s*GetTypeFrom", RegexOptions.Compiled);
    static readonly Regex Reflection = new(@"\b(Type\s*\.\s*GetType\s*\(|Assembly\s*\.\s*Load\w*|MakeGenericType|InvokeMember|GetTypes\s*\()", RegexOptions.Compiled);
    static readonly Regex Identifier = new(@"[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)*", RegexOptions.Compiled);

    internal void Apply()
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = graph.Root, RedirectStandardOutput = true };
        start.ArgumentList.Add("ls-files");
        using var process = System.Diagnostics.Process.Start(start)!;
        graph.Tracked = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Tracked-file inventory failed.");
        var names = graph.Types.Values.Where(t => t.Product).SelectMany(t => new[] { (Name: t.Symbol.Name, Node: t), (Name: t.Symbol.ToDisplayString(), Node: t) })
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Select(t => t.Node).Distinct().ToArray());
        void TextRoots(string text, string reason)
        {
            foreach (var name in Identifier.Matches(text).Select(m => m.Value).Distinct())
            foreach (var token in new[] { name }.Concat(name.Split('.')))
                if (names.TryGetValue(token, out var nodes)) foreach (var node in nodes) { node.Roots.Add(reason); node.Risks.Add("name-in-text"); }
        }
        var textKeepExclusions = graph.Config.TextKeepExclusions.Values.SelectMany(paths => paths).ToHashSet(StringComparer.Ordinal);
        foreach (var path in graph.Tracked)
        {
            var full = Path.Combine(graph.Root, path);
            if (!File.Exists(full)) throw new InvalidOperationException($"Missing tracked file: {path}");
            if (graph.Files.TryGetValue(path, out var nodes))
            {
                var source = File.ReadAllText(full);
                var product = nodes.Where(n => n.Product).ToArray();
                if (product.Length != 0)
                {
                    var root = CSharpSyntaxTree.ParseText(source).GetRoot();
                    if (Reflection.IsMatch(source)) throw new InvalidOperationException($"Unmodelled reflection: {path}");
                    if (Interop.IsMatch(source) || graph.SemanticInterop.Contains(path))
                    {
                        // Directives keep the strict source hash so inactive code and conditional compilation cannot drift unseen.
                        var reviewed = root.ContainsDirectives ? source : string.Join(" ", root.DescendantTokens().Select(t => t.Text));
                        if (!graph.Config.InteropFiles.TryGetValue(path, out var hash) || hash != Analysis.Hash(reviewed)) throw new InvalidOperationException($"Unreviewed interop file/site: {path}");
                        foreach (var node in product)
                        {
                            node.Roots.Add("component:interop");
                            node.Risks.Add("interop-neighbour");
                            foreach (var other in product) node.Edges.Add(new(other.Id, "", -1, "component:interop"));
                        }
                    }
                    foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(s => s.IsKind(SyntaxKind.StringLiteralExpression)))
                        TextRoots(literal.Token.ValueText, "component:text:" + path);
                    if (Regex.IsMatch(source, @"GetManifestResource(Stream|Names)|Application\.(GetResourceStream|LoadComponent)|pack://"))
                        foreach (var node in product) node.Roots.Add("component:resource-loader");
                }
                var tests = nodes.Where(n => !n.Product).ToArray();
                if (tests.Length != 0)
                foreach (var marker in Regex.Matches(source, @"\b(Process|Assembly|Activator|Type|BindingFlags|CSharpCompilation|CSharpSyntaxTree|MSBuildWorkspace)\b|\.Get(Type|Property|Method|Field|Constructor)\(|\.cs\b|\.csproj\b|\.xaml\b|ReadAll(Text|Lines)|ReadAllBytes").Select(m => m.Value).Distinct())
                {
                    var reason = marker switch {
                        "Process" => "test:subprocess",
                        "CSharpCompilation" or "CSharpSyntaxTree" or "MSBuildWorkspace" => "test:compiler-inspection",
                        "Assembly" or "Activator" or "Type" or "BindingFlags" => "test:reflection",
                        _ => marker.StartsWith(".Get") ? "test:reflection" : "test:source-or-file-inspection" };
                    foreach (var test in tests) test.UnknownReasons.Add(reason);
                }
            }
            else if (!path.StartsWith("docs/") && !textKeepExclusions.Contains(path))
            {
                var bytes = File.ReadAllBytes(full);
                if (!bytes.Contains((byte)0)) TextRoots(System.Text.Encoding.UTF8.GetString(bytes), "component:text:" + path);
            }
        }
        foreach (var path in graph.Tracked.Where(p => p.EndsWith(".xaml"))) Xaml(path);
        // Source inputs can feed several binaries; keep their union, never assign one owner by first match.
        var app = XDocument.Load(Path.Combine(graph.Root, "apps/SyncShark/SyncShark.csproj"));
        foreach (var target in app.Descendants("Target").Where(t => new[] { "BuildSyncSharkExplorerCommand", "BuildSyncSharkDiscovery", "BuildSyncSharkUpdater" }.Contains((string?)t.Attribute("Name"))))
        foreach (var input in ((string?)target.Attribute("Inputs") ?? "").Split(';'))
        {
            var path = input.Replace("$(MSBuildProjectDirectory)", Path.Combine(graph.Root, "apps/SyncShark"));
            var inputs = path.Contains("@(SyncSharkUpdaterSetupSources)") ? graph.Tracked.Where(p => p.StartsWith("tools/SyncShark.Updater") && p.EndsWith(".cs")) :
                path.Contains("$(") ? [] : new[] { graph.Relative(Path.GetFullPath(path)) };
            foreach (var file in inputs)
            {
                if (!graph.Components.TryGetValue(file, out var consumers)) graph.Components[file] = consumers = [];
                consumers.Add((string)target.Attribute("Name")!);
            }
        }
        foreach (var file in graph.Tracked.Where(p => p.StartsWith("Installer/") || p.StartsWith("tools/SyncShark.") || p == "tools/Prepare-SyncSharkRelease.ps1"))
        {
            if (!graph.Components.TryGetValue(file, out var consumers)) graph.Components[file] = consumers = [];
            consumers.Add(file.StartsWith("Installer/") ? "setup-host" : "release-script");
        }
        foreach (var node in graph.Types.Values.Where(t => t.Product && t.Files.Any(f => f.StartsWith("tools/") || f.StartsWith("Installer/"))))
        {
            node.Roots.Add("component:external");
            node.Risks.Add("component-edge");
        }
        foreach (var node in graph.Types.Values.Where(t => t.Product && t.Symbol.Name is "ExplorerRequest" or "PilotDirectoryLease"))
        {
            node.Roots.Add("component:external");
            node.Risks.Add("component-edge");
        }
        graph.Diagnostics.Add("Non-C# assets, native/script components and uncompiled source are kept; no deletion output exists.");
    }

    void Xaml(string path)
    {
        var xml = XDocument.Load(Path.Combine(graph.Root, path));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var className = (string?)xml.Root?.Attribute(x + "Class");
        if (className is null) throw new InvalidOperationException($"XAML without class anchor: {path}");
        var owners = graph.Types.Values.Where(t => t.Symbol.ToDisplayString() == className).ToArray();
        if (owners.Length == 0) throw new InvalidOperationException($"Unresolved XAML class: {path} {className}");
        foreach (var owner in owners) owner.Roots.Add("component:xaml");
        void Name(string uri, string local)
        {
            if (!uri.StartsWith("clr-namespace:")) return;
            var parts = uri["clr-namespace:".Length..].Split(';');
            var assembly = parts.Skip(1).FirstOrDefault(p => p.StartsWith("assembly="))?[9..];
            var name = parts[0] + "." + local.Split('.')[0];
            foreach (var owner in owners)
            {
                var compilation = graph.Compilations[owner.Project];
                var candidates = new[] { compilation.Assembly }.Concat(compilation.SourceModule.ReferencedAssemblySymbols)
                    .Where(a => assembly is null || a.Name == assembly)
                    .Select(a => a.GetTypeByMetadataName(name) ?? a.GetTypeByMetadataName(name + "Extension")).OfType<INamedTypeSymbol>().ToArray();
                if (candidates.Length == 0) throw new InvalidOperationException($"Unresolved XAML name: {path} {name}");
                foreach (var type in candidates)
                {
                    graph.Reference(owner, type, path, -1, "xaml");
                    foreach (var target in graph.Resolve(type)) target.Roots.Add("component:xaml");
                }
            }
        }
        foreach (var element in xml.Descendants())
        {
            Name(element.Name.NamespaceName, element.Name.LocalName);
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                Name(attribute.Name.NamespaceName, attribute.Name.LocalName);
                foreach (Match match in Regex.Matches(attribute.Value, @"(?<prefix>[A-Za-z_]\w*):(?<name>[A-Za-z_]\w*(?:\.\w+)?)"))
                {
                    var ns = element.GetNamespaceOfPrefix(match.Groups["prefix"].Value);
                    if (ns is not null) Name(ns.NamespaceName, match.Groups["name"].Value);
                }
            }
        }
    }
}
