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
            var expectedNamespace = module switch
            {
                "SyncShark" => "Massdex.SyncShark", "SyncShark.Cli" => "Massdex.SyncShark.Cli",
                "Contracts" => Prefix, _ => Prefix + "." + module
            };
            var names = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToArray();
            Check.Require(names.Length==1 && names[0]==expectedNamespace, $"{path}: expected namespace {expectedNamespace}");
            var violation = Violations(compilation, tree, module, Path.GetFileNameWithoutExtension(path)).FirstOrDefault();
            Check.Require(violation is null, $"{path}: {violation}");
        }
        // Each probe is resolved by the same compiler and checked by the production traversal above.
        (string Module,string File,string Code,bool Allowed)[] probes = [
            ("Planner","Decide","_ = DateTimeOffset.UtcNow;",false),
            ("Planner","TimeRules","_ = DateTimeOffset.UnixEpoch;",true),
            ("Planner","Decide","_ = typeof(Massdex.SyncShark.Engine.SettingsParser);",false),
            ("Planner","Moves","_ = System.IO.File.Exists(null);",false),
            ("Planner","Moves","_ = typeof(Massdex.SyncShark.Engine.Executor.ItemSync);",false),
            ("Storage","Store","_ = typeof(Massdex.SyncShark.Engine.Dropbox.Routes);",false),
            ("Storage","Store","_ = typeof(Microsoft.Data.Sqlite.SqliteConnection);",true),
            ("Contracts","PairConfig","_ = typeof(Massdex.SyncShark.Engine.Storage.Store);",false),
            ("Local","LocalSide","_ = typeof(Microsoft.Data.Sqlite.SqliteConnection);",false),
            ("Local","LocalSide","_ = typeof(Massdex.SyncShark.Engine.Local.InUse);",true),
            ("Local","RecycleBin","_ = typeof(Massdex.SyncShark.Engine.Local.VolumeSpace);",false),
            ("Executor","Effects","_ = typeof(Massdex.SyncShark.Engine.Dropbox.DropboxSide);",false),
            ("Executor","Effects","_ = typeof(Massdex.SyncShark.Engine.Planner.Moves);",true),
            ("Run","Host","_ = typeof(Massdex.SyncShark.Engine.Planner.Moves);",false),
            ("Run","Host","_ = typeof(Massdex.SyncShark.Engine.Edge.Commands);",false),
            ("Run","Host","_ = typeof(Massdex.SyncShark.Engine.Storage.Store);",false),
            ("Run","RunLog","_ = System.IO.File.OpenRead(null!);",false),
            ("Run","RunLog","_ = typeof(Massdex.SyncShark.Engine.ILogFiles);",true),
            ("Local","LogFiles","_ = System.IO.File.OpenRead(null!);",true),
            ("Edge","Commands","_ = System.IO.File.Exists(null);",false),
            ("Run","RunLog","_ = new System.IO.StreamReader(string.Empty);",false),
            ("Run","RunLog","_ = new System.IO.StreamReader(Stream.Null);",true),
            ("Local","LocalSide","_ = new DriveInfo(string.Empty).AvailableFreeSpace;",false),
            ("Local","VolumeSpace","_ = new DriveInfo(string.Empty).AvailableFreeSpace;",true),
            ("Local","LocalSide","_ = Massdex.SyncShark.Engine.Local.InUse.Processes(string.Empty);",true),
            ("Local","RecycleBin","_ = Massdex.SyncShark.Engine.Local.InUse.Processes(string.Empty);",false),
            ("Planner","Decide","_ = Environment.TickCount64;",false),
            ("Run","Host","_ = typeof(Massdex.SyncShark.Engine.Dropbox.DropboxSide);",false),
            ("Edge","Commands","_ = typeof(Massdex.SyncShark.Engine.Run.Host);",true),
            ("SyncShark.Cli","Other","_ = typeof(Massdex.SyncShark.Engine.Edge.Cli);",false),
            ("Dropbox","FolderJobs","_ = new HttpClient().Send(new HttpRequestMessage());",false),
            ("Storage","Store","_ = new DriveInfo(string.Empty).AvailableFreeSpace;",false),
            ("Planner","Decide","_ = \"SELECT rowid FROM pair\";",false),
            ("Storage","Schema","_ = \"SELECT rowid FROM pair\";",true),
            ("Edge","Commands","_ = typeof(Massdex.SyncShark.Engine.Executor.Resolve);",false),
            ("Dropbox","SignIn","_ = new HttpClient().GetAsync(string.Empty);",false),
            ("Dropbox","DropboxApi","_ = new HttpClient().GetAsync(string.Empty);",true),
            ("Dropbox","FolderJobs","_ = typeof(Massdex.SyncShark.Engine.Planner.Moves);",false),
            ("SyncShark.Cli","Program","_ = typeof(Massdex.SyncShark.Engine.Local.LogFiles);",true),
            ("SyncShark.Cli","Program","_ = typeof(Massdex.SyncShark.Engine.Local.LocalSide);",false),
            ("SyncShark.Cli","Program","_ = typeof(Massdex.SyncShark.Engine.Edge.Commands);",false),
            ("SyncShark","Other","_ = typeof(Massdex.SyncShark.Engine.Storage.Store);",false),
            ("SyncShark","Program","_ = typeof(Massdex.SyncShark.Engine.Storage.StoreMove);",true),
            ("SyncShark","Program","_ = typeof(Massdex.SyncShark.Engine.Planner.Decide);",false)
        ];
        foreach(var (module,file,code,allowed) in probes)
        {
            var tree=CSharpSyntaxTree.ParseText("class Probe { static void Use() { "+code+" } }");
            var fixture=compilation.AddSyntaxTrees(tree);
            Check.Require(!fixture.GetSemanticModel(tree).GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error),"Invalid dependency fixture: "+code);
            Check.Require(!Violations(fixture,tree,module,file).Any()==allowed,$"{module}/{file}: wrong outcome for {code}");
        }
        return Task.CompletedTask;
    }

    private const string Prefix = "Massdex.SyncShark.Engine";
    private static IEnumerable<string> Violations(CSharpCompilation compilation,SyntaxTree tree,string module,string file)
    {
        var model=compilation.GetSemanticModel(tree);
        var root=tree.GetRoot();
        if(module!="Storage")
        {
            foreach(var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
                if(literal.Token.Value is string sql && System.Text.RegularExpressions.Regex.IsMatch(sql.TrimStart(), @"^(?:SELECT\s+.+\s+FROM\s+|INSERT\s+INTO\s+|UPDATE\s+\w+\s+SET\s+|DELETE\s+FROM\s+|(?:ALTER|CREATE|DROP)\s+(?:TABLE|INDEX|TRIGGER)\s+|PRAGMA\s+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) yield return "SQL outside Storage";
        }
        foreach(var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if(model.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor) continue;
            if(ctor.ContainingNamespace.ToDisplayString()=="System.IO" && ctor.ContainingType.Name is "StreamReader" or "StreamWriter" && ctor.Parameters.FirstOrDefault()?.Type.SpecialType==SpecialType.System_String && module is not ("Local" or "Storage") && (module,file)!=("Dropbox","Connections")) yield return "A path-based stream requires its I/O owner";
        }
        foreach(var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var owner=model.GetDeclaredSymbol(method); var native=owner?.GetDllImportData();
            if(native is null) continue;
            var entry=native.EntryPointName??owner!.Name;
            if(native.ModuleName?.StartsWith("rstrtmgr",StringComparison.OrdinalIgnoreCase)==true && (module,file)!=("Local","InUse")) yield return entry;
            if(entry.StartsWith("GetDiskFreeSpace",StringComparison.Ordinal) && (module,file)!=("Local","VolumeSpace")) yield return entry;
        }
        foreach(var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol=model.GetSymbolInfo(node).Symbol;
            if(symbol is IAliasSymbol alias) symbol=alias.Target;
            var type=symbol as INamedTypeSymbol ?? symbol?.ContainingType;
            if(type is null) continue;
            var space=type.ContainingNamespace.ToDisplayString(); var member=symbol!.Name; var name=type.Name;
            bool violates = false;
            if(space==Prefix || space.StartsWith(Prefix+".",StringComparison.Ordinal))
            {
                var other=space==Prefix?"Contracts":space[(Prefix.Length+1)..];
                bool allowed=module switch
                {
                    "SyncShark.Cli" => file=="Program" && (other,name) is ("Edge","Cli") or ("Local","LogFiles") or ("Contracts","ILogFiles"),
                    "SyncShark" => file=="Program" && (other,name) is ("Storage","Store" or "StoreMove") or ("Dropbox","Connections" or "DropboxSide" or "DropboxApi" or "CredentialStore" or "SignIn") or ("Local","LocalSide" or "RecycleBin" or "LogFiles") or ("Run","Host") or ("Edge","HostServer" or "Commands") or ("Contracts",_),
                    _ => module==other || other=="Contracts" || (module,other) is ("Executor","Planner") or ("Run","Planner" or "Executor") or ("Edge","Run")
                };
                violates=!allowed || (other,name)==("Planner","Moves") && module!="Executor" && (module,file)!=("Planner","Moves") ||
                    other=="Local" && name is "InUse" or "VolumeSpace" && file!=name && (module,file)!=("Local","LocalSide") ||
                    module=="Planner" && name=="SettingsParser";
            }
            else if(space.StartsWith("Microsoft.Data.Sqlite",StringComparison.Ordinal) || space=="SQLitePCL") violates=module!="Storage";
            else if(space=="System.Net.Http" && name is "HttpClient" or "HttpMessageInvoker" && (member.Contains("Async",StringComparison.Ordinal) || member=="Send")) violates=(module,file)!=("Dropbox","DropboxApi");
            else if(space=="System.IO" && name=="DriveInfo" && member is "AvailableFreeSpace" or "TotalFreeSpace") violates=(module,file)!=("Local","VolumeSpace");
            else if(module=="Planner" && name=="Environment" && member is "TickCount" or "TickCount64") violates=true;
            else if(space=="System.IO" && name is "File" or "Directory" or "FileInfo" or "DirectoryInfo" or "FileStream" or "DriveInfo" or "FileSystemWatcher")
                violates=module is not ("Local" or "Storage") && (module,file)!=("Dropbox","Connections");
            else if(module=="Planner" && (name=="TimeProvider" || name is "DateTime" or "DateTimeOffset" && member is "Now" or "UtcNow" or "Today" || name=="Stopwatch")) violates=true;
            if(violates) yield return type.ToDisplayString()+"."+member;
        }
    }
}
