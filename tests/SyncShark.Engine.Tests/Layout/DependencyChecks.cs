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
            var violation = Violations(compilation, tree, module, path).FirstOrDefault();
            Check.Require(violation is null, $"{path}: {violation}");
        }
        // Each probe is resolved by the same compiler and checked by the production traversal above.
        (string Module,string File,string Code,bool Allowed)[] probes = [
            ("Planner","Decide","_ = DateTimeOffset.UtcNow;",false),
            ("Planner","TimeRules","_ = DateTimeOffset.UnixEpoch;",true),
            ("Planner","Decide","_ = typeof(Massdex.SyncShark.Engine.SettingsParser);",false),
            ("Planner","Moves","_ = System.IO.File.Exists(null);",false),
            ("Planner","Moves","_ = Environment.GetEnvironmentVariable(nameof(Probe));",false),
            ("Planner","Moves","_ = Path.GetFileName(string.Empty);",true),
            ("Dropbox","nested/SignInCallback","_ = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);",false),
            ("Run","PairRunner","_ = Massdex.SyncShark.Engine.SyncedEntries.RenameTarget(null!,string.Empty);",true),
            ("Executor","Effects","_ = Massdex.SyncShark.Engine.Planner.ProbeInternal.Value();",false),
            ("Planner","Moves","_ = $\"SELECT {1}\\nFROM pair\";",false),
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
            ("Planner","Decide","_ = new System.Net.Sockets.TcpClient();",false),
            ("Dropbox","SignIn","System.Net.HttpWebRequest r=null!; _ = r.GetResponseAsync();",false),
            ("Dropbox","DropboxApi","System.Net.HttpWebRequest r=null!; _ = r.GetResponseAsync();",true),
            ("Planner","Decide","_ = Environment.TickCount64;",false),
            ("Run","Host","_ = typeof(Massdex.SyncShark.Engine.Dropbox.DropboxSide);",false),
            ("Edge","Commands","_ = typeof(Massdex.SyncShark.Engine.Run.Host);",true),
            ("SyncShark.Cli","Other","_ = typeof(Massdex.SyncShark.Engine.Edge.Cli);",false),
            ("Dropbox","FolderJobs","_ = new HttpClient().Send(new HttpRequestMessage());",false),
            ("Storage","Store","_ = new DriveInfo(string.Empty).AvailableFreeSpace;",false),
            ("Planner","Decide","_ = \"SELECT rowid FROM pair\";",false),
            ("Storage","Schema","_ = \"SELECT rowid FROM pair\";",false),
            ("Edge","Commands","_ = typeof(Massdex.SyncShark.Engine.Executor.Resolve);",false),
            ("SyncShark","Program","_ = new Massdex.SyncShark.Engine.Dropbox.Dispatcher(null!,null!);",true),
            ("SyncShark.Cli","Program","_ = new Massdex.SyncShark.Engine.Dropbox.Dispatcher(null!,null!);",false),
            ("Dropbox","SignInCallback","var l=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);l.Start(1);_ = l.AcceptTcpClientAsync();l.Stop();",true),
            ("Dropbox","SignInCallback","_ = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any,0);",false),
            ("Dropbox","SignInCallback","var c=new System.Net.Sockets.TcpClient();_ = c.ConnectAsync(System.Net.IPAddress.Loopback,1);",false),
            ("Dropbox","FolderJobs","var l=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);l.Start();",false),
            ("Dropbox","SignIn","_ = new HttpClient().GetAsync(string.Empty);",false),
            ("Dropbox","DropboxApi","_ = new HttpClient().GetAsync(string.Empty);",true),
            ("Dropbox","FolderJobs","_ = typeof(Massdex.SyncShark.Engine.Planner.Moves);",false),
            ("SyncShark.Cli","Program","_ = Massdex.SyncShark.Engine.ResourceNames.CliExecutable;",true),
            ("SyncShark.Cli","Program","_ = typeof(Massdex.SyncShark.Engine.Local.LogFiles);",true),
            ("SyncShark.Cli","Program","_ = typeof(Massdex.SyncShark.Engine.Local.LocalSide);",false),
            ("SyncShark.Cli","Program","_ = typeof(Massdex.SyncShark.Engine.Edge.Commands);",false),
            ("SyncShark","Other","_ = typeof(Massdex.SyncShark.Engine.Storage.Store);",false),
            ("SyncShark","subfolder/Program","_ = typeof(Massdex.SyncShark.Engine.Storage.Store);",false),
            ("SyncShark.Cli","subfolder/Program","_ = typeof(Massdex.SyncShark.Engine.Edge.Cli);",false),
            ("SyncShark","Program","_ = typeof(Massdex.SyncShark.Engine.Storage.StoreMove);",true),
            ("SyncShark","Program","_ = typeof(Massdex.SyncShark.Engine.Planner.Decide);",false)
        ];
        foreach(var (module,file,code,allowed) in probes)
        {
            var tree=CSharpSyntaxTree.ParseText("class Probe { static void Use() { "+code+" } }");
            var fixture=compilation.AddSyntaxTrees(tree,CSharpSyntaxTree.ParseText("namespace Massdex.SyncShark.Engine.Planner; internal static class ProbeInternal { internal static int Value() => 0; }"));
            Check.Require(!fixture.GetSemanticModel(tree).GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error),"Invalid dependency fixture: "+code);
            Check.Require(!Violations(fixture,tree,module,$"{(module.StartsWith("SyncShark",StringComparison.Ordinal)?"apps":"src/SyncShark.Engine")}/{module}/{file}.cs").Any()==allowed,$"{module}/{file}: wrong outcome for {code}");
        }
        // Inspect a declaration only; this fixture never opens a device or invokes native code.
        var nativeTree=CSharpSyntaxTree.ParseText("class Probe { [System.Runtime.InteropServices.DllImport(\"kernel32.dll\",EntryPoint=\"DeviceIoControl\")] internal static extern int Control(); }");
        var nativeFixture=compilation.AddSyntaxTrees(nativeTree);
        Check.Require(!nativeFixture.GetSemanticModel(nativeTree).GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error),"Invalid native ownership fixture");
        Check.Require(!Violations(nativeFixture,nativeTree,"Local","src/SyncShark.Engine/Local/UsnJournal.cs").Any() && Violations(nativeFixture,nativeTree,"Run","src/SyncShark.Engine/Run/Host.cs").Any(),"Native journal ownership");
        return Task.CompletedTask;
    }

    private const string Prefix = "Massdex.SyncShark.Engine";
    private static IEnumerable<string> Violations(CSharpCompilation compilation,SyntaxTree tree,string module,string path)
    {
        var file=Path.GetDirectoryName(path)?.Replace('\\','/')=="src/SyncShark.Engine/"+module ? Path.GetFileNameWithoutExtension(path) : string.Empty;
        var model=compilation.GetSemanticModel(tree);
        var root=tree.GetRoot();
        if(module=="Planner" && root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().Any(n=>model.GetTypeInfo(n).Type?.ContainingNamespace.ToDisplayString()=="System.IO" && model.GetTypeInfo(n).Type?.Name is "StreamReader" or "StreamWriter" or "FileStream")) yield return "Planner I/O";
        // shortcut: the four Storage files still holding SQL are swapped in part 2; drop this exemption with that swap.
        if(module!="Storage" || file is not ("Committer" or "PairTable" or "SchemaUpgrade" or "StoreImage"))
        {
            foreach(var expr in root.DescendantNodes().OfType<ExpressionSyntax>().Where(n=>n is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax))
            {
                var sql=expr switch { LiteralExpressionSyntax l=>l.Token.Value as string, InterpolatedStringExpressionSyntax i=>string.Concat(i.Contents.Select(c=>c is InterpolatedStringTextSyntax text?text.TextToken.ValueText:"?")), _=>null };
                if(sql!=null && System.Text.RegularExpressions.Regex.IsMatch(sql.TrimStart(),@"^(?:SELECT\s+.+\s+FROM\s+|INSERT\s+INTO\s+|UPDATE\s+\w+\s+SET\s+|DELETE\s+FROM\s+|(?:ALTER|CREATE|DROP)\s+(?:TABLE|INDEX|TRIGGER)\s+|PRAGMA\s+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.Singleline)) yield return "SQL in product C#";
            }
        }
        foreach(var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if(model.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor) continue;
            if(ctor.ContainingNamespace.ToDisplayString()=="System.Net.Sockets" && (module,file)!=("Dropbox","DropboxApi"))
            {
                var address=creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                var value=address==null ? null : model.GetSymbolInfo(address).Symbol;
                if((module,file)!=("Dropbox","SignInCallback") || ctor.ContainingType.Name!="TcpListener" || value?.ContainingType?.ToDisplayString()!="System.Net.IPAddress" || value.Name is not ("Loopback" or "IPv6Loopback")) yield return "Socket construction outside its owner or non-loopback return address";
            }
            if(ctor.ContainingNamespace.ToDisplayString()=="System.IO" && ctor.ContainingType.Name is "StreamReader" or "StreamWriter" && ctor.Parameters.FirstOrDefault()?.Type.SpecialType==SpecialType.System_String && module is not ("Local" or "Storage") && (module,file)!=("Dropbox","Connections")) yield return "A path-based stream requires its I/O owner";
        }
        foreach(var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var owner=model.GetDeclaredSymbol(method); var native=owner?.GetDllImportData();
            if(native is null) continue;
            var entry=native.EntryPointName??owner!.Name;
            if(native.ModuleName?.StartsWith("rstrtmgr",StringComparison.OrdinalIgnoreCase)==true && (module,file)!=("Local","InUse")) yield return entry;
            var library=Path.GetFileNameWithoutExtension(native.ModuleName??string.Empty);
            if(module!="Local" && (library.Equals("kernel32",StringComparison.OrdinalIgnoreCase) || library.Equals("kernelbase",StringComparison.OrdinalIgnoreCase) || library.Equals("shell32",StringComparison.OrdinalIgnoreCase)) &&
                System.Text.RegularExpressions.Regex.IsMatch(entry,@"^(?:DeviceIoControl|(?:CreateFile(?:2)?|MoveFile(?:Ex|WithProgress)?|DeleteFile|CopyFile(?:Ex|2)?|ReplaceFile|GetFileAttributes(?:Ex)?|SetFileAttributes|CreateDirectory(?:Ex)?|RemoveDirectory|SHFileOperation)[AW]?|OpenFileById|GetFileInformationByHandle(?:Ex)?|SetFileInformationByHandle)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) yield return entry;
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
                    "SyncShark.Cli" => path=="apps/SyncShark.Cli/Program.cs" && (other,name) is ("Edge","Cli") or ("Local","LogFiles") or ("Contracts","ILogFiles" or "ResourceNames"),
                    "SyncShark" => path=="apps/SyncShark/Program.cs" && (other,name) is ("Storage","Store" or "StoreMove") or ("Dropbox","Connections" or "DropboxSide" or "DropboxApi" or "Dispatcher" or "CredentialStore" or "SignIn") or ("Local","LocalSide" or "RecycleBin" or "LogFiles") or ("Run","Host") or ("Edge","HostServer" or "Commands") or ("Contracts",_),
                    _ => module==other || other=="Contracts" || (module,other) is ("Executor","Planner") or ("Run","Planner" or "Executor") or ("Edge","Run") || (module,file,other,name)==("Dropbox","DropboxSide","Local","LocalErrors")
                };
                violates=!allowed || other!=module && other!="Contracts" && (symbol.DeclaredAccessibility==Accessibility.Internal || type.DeclaredAccessibility==Accessibility.Internal) || (other,name)==("Planner","Moves") && module!="Executor" && (module,file)!=("Planner","Moves") ||
                    other=="Local" && name is "InUse" or "VolumeSpace" && file!=name && (module,file)!=("Local","LocalSide") ||
                    module=="Planner" && name=="SettingsParser";
            }
            else if(space.StartsWith("Microsoft.Data.Sqlite",StringComparison.Ordinal) || space=="SQLitePCL") violates=module!="Storage";
            else if(module=="Planner" && (space=="System.Net" || space.StartsWith("System.Net.",StringComparison.Ordinal))) violates=true;
            else if((space=="System.Net.Sockets" || space=="System.Net" && name is "HttpWebRequest" or "WebRequest" or "WebClient" or "HttpListener" or "Dns") && symbol is IMethodSymbol)
            {
                bool receive=(module,file)==("Dropbox","SignInCallback") && space=="System.Net.Sockets" && (name,member) is
                    ("TcpListener",".ctor" or "Start" or "AcceptTcpClientAsync" or "Stop" or "Dispose") or
                    ("TcpClient","GetStream" or "Dispose" or "Close") or
                    ("NetworkStream","ReadAsync" or "WriteAsync" or "FlushAsync" or "DisposeAsync" or "Dispose");
                violates=(module,file)!=("Dropbox","DropboxApi") && !receive;
            }
            else if(space=="System.Net.Http" && name is "HttpClient" or "HttpMessageInvoker" && (member.Contains("Async",StringComparison.Ordinal) || member=="Send")) violates=(module,file)!=("Dropbox","DropboxApi");
            else if(space=="System.IO" && name=="DriveInfo" && member is "AvailableFreeSpace" or "TotalFreeSpace") violates=(module,file)!=("Local","VolumeSpace");
            else if(module=="Planner" && name=="Environment") violates=true;
            else if(space=="System.IO" && name is "File" or "Directory" or "FileInfo" or "DirectoryInfo" or "FileStream" or "DriveInfo" or "FileSystemWatcher")
                violates=module is not ("Local" or "Storage") && (module,file)!=("Dropbox","Connections") && (module,file)!=("Dropbox","DropboxReplyShapes") && (module,file)!=("Dropbox","DropboxSide");
            else if(module=="Planner" && (name=="TimeProvider" || name is "DateTime" or "DateTimeOffset" && member is "Now" or "UtcNow" or "Today" || name=="Stopwatch")) violates=true;
            if(violates) yield return type.ToDisplayString()+"."+member;
        }
    }
}
