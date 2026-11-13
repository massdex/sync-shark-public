using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Operations;

internal sealed record Cut(string Route, string Path, int First, int Last, string Hash, bool Gui);
internal sealed record Sentinel(string Type, string Profile);
internal sealed record EdgeCheck(string Route, string Target);
internal sealed record Config(Cut[] Cuts, Dictionary<string, string> InteropFiles, Sentinel[] Sentinels, EdgeCheck[] EdgeChecks);
internal sealed record Edge(string Target, string Path, int Position, string Reason);
internal sealed class TypeNode(string id, string project, INamedTypeSymbol symbol, bool product)
{
    internal string Id = id, Project = project;
    internal INamedTypeSymbol Symbol = symbol;
    internal bool Product = product, Unknown;
    internal HashSet<string> Files = [], Risks = [], Roots = [];
    internal HashSet<Edge> Edges = [];
}

// One graph owner: symbols, references and the conservative whole-type closure share identity here.
internal sealed class Analysis(string root, Config config)
{
    internal readonly Dictionary<string, TypeNode> Types = [];
    internal readonly Dictionary<string, List<TypeNode>> Files = [];
    internal readonly Dictionary<string, Compilation> Compilations = [];
    internal readonly List<string> Diagnostics = [];
    internal readonly HashSet<string> SemanticInterop = [], WpfHelpers = [];
    internal readonly Dictionary<string, HashSet<string>> Components = [];
    internal string[] Tracked = [];
    internal readonly string Root = root;
    internal readonly Config Config = config;
    readonly Dictionary<string, List<string>> identities = [];
    readonly Dictionary<Cut, Microsoft.CodeAnalysis.Text.TextSpan> spans = [];
    readonly Dictionary<SyntaxTree, int[]> carriageReturns = [];
    readonly Dictionary<Type, System.Reflection.PropertyInfo[]> operationSymbols = [];
    int Position(SyntaxNode node)
    {
        if (!carriageReturns.TryGetValue(node.SyntaxTree, out var offsets))
            carriageReturns[node.SyntaxTree] = offsets = node.SyntaxTree.GetText().ToString().Select((c, i) => (c, i)).Where(x => x.c == '\r').Select(x => x.i).ToArray();
        var index = Array.BinarySearch(offsets, node.SpanStart);
        return node.SpanStart - (index < 0 ? ~index : index);
    }
    internal string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))))[..12].ToLowerInvariant();
    static string SymbolKey(INamedTypeSymbol symbol) => symbol.ContainingAssembly.Identity + "|" + symbol.OriginalDefinition.GetDocumentationCommentId();

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected repository and report directory.");
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(args[1]);
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(args[0], "tools/ci/reachability-config.json")))!;
        var graph = new Analysis(Path.GetFullPath(args[0]), config);
        try
        {
            await graph.LoadAsync();
            WpfHelper.Validate(graph);
            graph.Bind();
            new KeepRules(graph).Apply();
            WpfHelper.Close(graph);
            new Report(graph).Write(args[1], clock);
            return 0;
        }
        catch (Exception error)
        {
            // Never publish candidate rows after a failed completeness gate.
            var message = error.ToString().Replace(graph.Root, "<repo>", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(args[1], "failure.json"), JsonSerializer.Serialize(new {
                valid = false, diagnostic = message, elapsedSeconds = clock.Elapsed.TotalSeconds,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
            Console.Error.WriteLine(message);
            return 1;
        }
    }

    async Task LoadAsync()
    {
        foreach (var cut in Config.Cuts)
        {
            var text = Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(Path.Combine(Root, cut.Path)).Replace("\r\n", "\n"));
            var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(text.Lines[cut.First - 1].Start, text.Lines[cut.Last - 1].End);
            if (Hash(text.ToString(span)) != cut.Hash) throw new InvalidOperationException($"Cut drift: {cut.Route} {cut.Path}");
            spans.Add(cut, span);
        }
        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> {
            ["Configuration"] = Environment.GetEnvironmentVariable("CONFIGURATION") ?? throw new InvalidOperationException("Missing CONFIGURATION"),
            ["SyncSharkBuildRoot"] = Path.Combine(Root, "build/ci"),
            ["SyncSharkZigPath"] = Environment.GetEnvironmentVariable("ZIG_EXE") ?? throw new InvalidOperationException("Missing ZIG_EXE") });
        var failures = new List<string>();
        workspace.WorkspaceFailed += (_, e) => failures.Add(e.Diagnostic.Message);
        await workspace.OpenSolutionAsync(Path.Combine(Root, "SyncShark.sln"));
        await workspace.OpenProjectAsync(Path.Combine(Root, "tests/SyncShark.FilesOnDemand.Tests/SyncShark.FilesOnDemand.Tests.csproj"));
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            var compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException($"No compilation: {project.Name}");
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join("\n", errors.Select(d => d.ToString())));
            var projectPath = Relative(project.FilePath!);
            Compilations.Add(projectPath, compilation);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var declaration in tree.GetRoot().DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                {
                    if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol) continue;
                    var key = SymbolKey(symbol);
                    var id = projectPath + "|" + key;
                    if (!Types.TryGetValue(id, out var node))
                    {
                        Types[id] = node = new TypeNode(id, projectPath, symbol, projectPath.StartsWith("src/") || projectPath.StartsWith("apps/"));
                        if (!identities.TryGetValue(key, out var ids)) identities[key] = ids = [];
                        ids.Add(id);
                    }
                    var file = Relative(tree.FilePath);
                    node.Files.Add(file);
                    if (!Files.TryGetValue(file, out var nodes)) Files[file] = nodes = [];
                    if (!nodes.Contains(node)) nodes.Add(node);
                }
            }
            if (project.Name is "SyncShark" or "SyncShark.UiFixture")
            {
                var directory = Path.GetDirectoryName(project.FilePath!)!;
                foreach (var xaml in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories).Where(p => !p.Contains("\\obj\\") && !p.Contains("\\bin\\")))
                {
                    var generated = Path.GetFileNameWithoutExtension(xaml) + ".g.cs";
                    if (!compilation.SyntaxTrees.Any(t => string.Equals(Path.GetFileName(t.FilePath), generated, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"Missing generated XAML document: {Relative(xaml)}");
                }
            }
        }
        if (failures.Count != 0) throw new InvalidOperationException("WorkspaceFailed: " + string.Join("\n", failures));
    }

    internal IEnumerable<TypeNode> Resolve(INamedTypeSymbol symbol)
    {
        // Compiler-only signature placeholders have no assembly and cannot name a source declaration.
        if (symbol.ContainingAssembly is null)
        {
            if (symbol.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                throw new InvalidOperationException("Source declaration without assembly: " + symbol.ToDisplayString());
            yield break;
        }
        if (identities.TryGetValue(SymbolKey(symbol), out var ids))
            foreach (var id in ids) yield return Types[id];
    }
    internal void Reference(TypeNode from, ISymbol? symbol, string path, int position, string reason = "reference")
    {
        // Control-flow labels have no type dependency; Roslyn deliberately rejects their containment API.
        if (symbol is null or ILabelSymbol) return;
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        var owner = symbol.ContainingType?.ToDisplayString();
        if (from.Product && !WpfHelpers.Contains(from.Id) && ((owner == "System.Type" && symbol.Name is "GetType" or "MakeGenericType" or "InvokeMember") ||
            (owner == "System.Reflection.Assembly" && (symbol.Name.StartsWith("Load") || symbol.Name == "GetTypes"))))
            throw new InvalidOperationException($"Unmodelled reflection: {path} {owner}.{symbol.Name}");
        if (from.Product && (symbol is ITypeSymbol { TypeKind: TypeKind.Dynamic } ||
            owner == "System.Activator" && symbol.Name == "CreateInstance" || owner == "System.Type" && symbol.Name.StartsWith("GetTypeFrom") ||
            owner == "System.Runtime.InteropServices.Marshal" && symbol.Name == "GetFunctionPointerForDelegate" ||
            symbol is INamedTypeSymbol attribute && attribute.ContainingNamespace?.ToDisplayString() == "System.Runtime.InteropServices" &&
            attribute.Name is "DllImportAttribute" or "ComImportAttribute" or "ComVisibleAttribute" or "GuidAttribute" or "StructLayoutAttribute" or nameof(System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute)))
            SemanticInterop.Add(path);
        if (symbol is IArrayTypeSymbol array) { Reference(from, array.ElementType, path, position, reason); return; }
        if (symbol is IPointerTypeSymbol pointer) { Reference(from, pointer.PointedAtType, path, position, reason); return; }
        if (symbol is IMethodSymbol method)
        {
            foreach (var argument in method.TypeArguments) Reference(from, argument, path, position, reason);
            if (method.ReducedFrom is { } extension) Reference(from, extension.ContainingType, path, position, reason);
        }
        if (symbol is INamedTypeSymbol named)
            foreach (var argument in named.TypeArguments.Where(t => t is not ITypeParameterSymbol)) Reference(from, argument, path, position, reason);
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.ContainingType)
            foreach (var target in Resolve(type)) from.Edges.Add(new Edge(target.Id, path, position, reason));
    }

    void Bind()
    {
        foreach (var (project, compilation) in Compilations)
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var path = Relative(tree.FilePath);
            var text = tree.GetText();
            var owners = new Dictionary<SyntaxNode, TypeNode>();
            foreach (var syntax in tree.GetRoot().DescendantNodes())
            {
                if (syntax is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && model.GetDeclaredSymbol(syntax) is INamedTypeSymbol declared)
                    owners[syntax] = Types[project + "|" + SymbolKey(declared)];
                var declaration = syntax.AncestorsAndSelf().FirstOrDefault(owners.ContainsKey);
                if (declaration is null) continue;
                var from = owners[declaration];
                // Text positions use LF so reviewed cuts survive Git's Windows checkout conversion.
                var position = Position(syntax);
                if (syntax is ExpressionSyntax or AttributeSyntax or ConstructorInitializerSyntax)
                {
                    var info = model.GetSymbolInfo(syntax);
                    Reference(from, info.Symbol, path, position);
                    foreach (var candidate in info.CandidateSymbols) Reference(from, candidate, path, position, "candidate-symbol");
                    if (syntax is ExpressionSyntax)
                    {
                        var type = model.GetTypeInfo(syntax);
                        Reference(from, type.Type, path, position);
                        Reference(from, type.ConvertedType, path, position);
                    }
                }
                if (syntax is CommonForEachStatementSyntax loop)
                {
                    var info = model.GetForEachStatementInfo(loop);
                    foreach (var symbol in new ISymbol?[] { info.GetEnumeratorMethod, info.MoveNextMethod, info.CurrentProperty, info.DisposeMethod, info.ElementType, info.ElementConversion.MethodSymbol, info.CurrentConversion.MethodSymbol })
                        Reference(from, symbol, path, position, "foreach-pattern");
                }
                if (syntax is AwaitExpressionSyntax awaitExpression)
                {
                    var info = model.GetAwaitExpressionInfo(awaitExpression);
                    foreach (var symbol in new ISymbol?[] { info.GetAwaiterMethod, info.GetResultMethod, info.IsCompletedProperty }) Reference(from, symbol, path, position, "await-pattern");
                }
                if (syntax is AssignmentExpressionSyntax assignment && assignment.Left is TupleExpressionSyntax)
                    Deconstruction(model.GetDeconstructionInfo(assignment), from, path, position);
                if (syntax is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or EqualsValueClauseSyntax or ArrowExpressionClauseSyntax)
                {
                    var operation = model.GetOperation(syntax);
                    if (operation is not null) Operations(operation, from, path, position);
                }
                if (syntax is ForEachVariableStatementSyntax variableLoop)
                    Deconstruction(model.GetDeconstructionInfo(variableLoop), from, path, position);
            }
        }
        foreach (var type in Types.Values)
        {
            if (type.Symbol.ContainingType is { } parent)
            {
                Reference(type, parent, "", -1, "containing-type");
                foreach (var container in Resolve(parent)) container.Edges.Add(new(type.Id, "", -1, "nested-type"));
            }
            if (!type.Product) continue;
            var bases = type.Symbol.AllInterfaces.Cast<INamedTypeSymbol>().ToList();
            for (var baseType = type.Symbol.BaseType; baseType is not null; baseType = baseType.BaseType) bases.Add(baseType);
            foreach (var baseType in bases)
            foreach (var source in Resolve(baseType)) source.Edges.Add(new(type.Id, "", -1, "possible-implementation"));
            if (type.Symbol.AllInterfaces.Any(i => !Resolve(i).Any())) type.Risks.Add("external-interface");
        }
    }

    void Deconstruction(DeconstructionInfo info, TypeNode from, string path, int position)
    {
        Reference(from, info.Method, path, position, "deconstruction-pattern");
        Reference(from, info.Conversion?.MethodSymbol, path, position, "deconstruction-conversion");
        foreach (var nested in info.Nested) Deconstruction(nested, from, path, position);
    }
    void Operations(IOperation operation, TypeNode from, string path, int position)
    {
        // Inspect Roslyn's public operation contracts, not product objects: includes implicit operators/Add/handlers.
        if (!operationSymbols.TryGetValue(operation.GetType(), out var properties))
            operationSymbols[operation.GetType()] = properties = operation.GetType().GetInterfaces().Where(t => typeof(IOperation).IsAssignableFrom(t)).SelectMany(t => t.GetProperties()).Where(p => typeof(ISymbol).IsAssignableFrom(p.PropertyType)).ToArray();
        foreach (var property in properties) Reference(from, property.GetValue(operation) as ISymbol, path, position, "operation-target");
        foreach (var child in operation.ChildOperations) Operations(child, from, path, Position(child.Syntax));
    }
    internal IEnumerable<Cut> CutsFor(Edge edge) => spans.Where(s => s.Key.Path == edge.Path && s.Value.Contains(edge.Position)).Select(s => s.Key);
    internal bool Allowed(Edge edge, string profile) => profile == "ALL" || !CutsFor(edge).Any(c => !(profile == "GUI" && c.Gui));
    internal Dictionary<string, (string? Parent, string Reason)> Walk(string profile, IEnumerable<string>? extraRoots = null, bool ignoreWpfHelpers = false)
    {
        var reached = new Dictionary<string, (string?, string)>();
        var queue = new Queue<string>();
        foreach (var node in Types.Values.Where(t => t.Product && (t.Symbol.ToDisplayString() == "Massdex.SyncShark.ProcessEntry" || profile == "ALL" && t.Roots.Count != 0)))
            if (reached.TryAdd(node.Id, (null, node.Roots.FirstOrDefault() ?? "entry"))) queue.Enqueue(node.Id);
        foreach (var id in extraRoots ?? []) if (reached.TryAdd(id, (null, "route"))) queue.Enqueue(id);
        while (queue.TryDequeue(out var id))
        foreach (var edge in Types[id].Edges.Where(e => Allowed(e, profile) && !(ignoreWpfHelpers && WpfHelpers.Contains(id))))
            if (Types[edge.Target].Product && reached.TryAdd(edge.Target, (id, CutsFor(edge).Select(c => "route:" + c.Route).FirstOrDefault() ?? reached[id].Item2))) queue.Enqueue(edge.Target);
        return reached;
    }
}
