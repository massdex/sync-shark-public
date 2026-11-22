using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Resolves engine symbol uses and checks them against the owning module’s dependency rule.</summary>
public static class DependencyChecks
{
    public static Task RunAsync(CheckArguments input)
    {
        var engine = Path.Combine(input.Root, "src", "SyncShark.Engine");
        var folders = new[] { engine, Path.Combine(input.Root, "apps", "SyncShark"), Path.Combine(input.Root, "apps", "SyncShark.Cli") };
        var sources = folders.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)).ToList();
        var implicitUsings = CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks; global using System.Net.Http;");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.Location).Append(typeof(SQLitePCL.raw).Assembly.Location)
            .Where(path => !string.Equals(path, typeof(Text).Assembly.Location, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Layout", sources.Append(implicitUsings), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Check.Require(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(x => x.ToString())));
        foreach (var tree in sources)
        {
            var path = Path.GetRelativePath(input.Root, tree.FilePath).Replace(Path.DirectorySeparatorChar, '/');
            var parts = path.Split('/');
            var module = parts[0] == "apps" ? parts[1] : parts.Length == 3 ? "Contracts" : parts[2];
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(node).Symbol;
                var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                if (type == null) continue;
                var name = type.ContainingNamespace.ToDisplayString();
                const string prefix = "Massdex.SyncShark.Engine";
                if (name != prefix && !name.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
                var other = name == prefix ? "Contracts" : name[(prefix.Length + 1)..];
                bool allowed = module == other || other == "Contracts" || (module, other) switch
                {
                    ("Executor", "Planner") => true,
                    ("Run", "Planner" or "Executor") => true,
                    ("Edge", "Run") => true,
                    ("SyncShark", _) => true,
                    ("SyncShark.Cli", "Edge") => type.Name == "Cli",
                    ("SyncShark.Cli", "Local") => type.Name == "LogFiles",
                    _ => false
                };
                Check.Require(allowed, $"{path}: {module} references {type.ToDisplayString()}");
            }
        }
        return Task.CompletedTask;
    }
}
