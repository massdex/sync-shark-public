using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks the published store contract and its open-existing boundary.</summary>
public static class StoreChecks
{
    /// <summary>Every Store operation documents its behavior, and probing an absent image cannot create it.</summary>
    public static async Task RunAsync()
    {
        var root = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SyncShark.sln"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Source checkout not found.");
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "SyncShark.Engine", "Storage", "Store.cs"));
        var violations = new List<string>();
        var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>();
        var type=methods.First().Ancestors().OfType<TypeDeclarationSyntax>().First();
        if (Summary(type)==null) violations.Add("Public Store type has no behavior summary");
        var contract = File.ReadAllText(Path.Combine(root.FullName,"src","SyncShark.Engine","IStore.cs"));
        foreach (var method in methods.Where(m => m.Modifiers.Any(t => t.RawKind == (int)SyntaxKind.PublicKeyword)))
        {
            var text = Summary(method);
            if (text == null) violations.Add("#6 missing behavior summary: " + method.GetFirstToken().GetLocation().GetLineSpan().StartLinePosition.Line);
            if (method is MethodDeclarationSyntax { Identifier.ValueText:"FinishRepairAsync" } && method.ParameterList.Parameters.Any(x => x.Identifier.ValueText=="completedRepairJson"))
            {
                var spec = CSharpSyntaxTree.ParseText(contract).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText=="FinishRepairAsync" && x.ParameterList.Parameters.Any(t=>t.Identifier.ValueText=="completedRepairJson"));
                if (text==null || !spec.GetLeadingTrivia().ToFullString().Contains(text,StringComparison.Ordinal) || !text.Contains("first_repair_done",StringComparison.Ordinal)) violations.Add("#6 full completion lacks IStore publication rule");
            }
        }
        foreach(var example in new[]{"// <summary>not documentation</summary>","/// <summary>  </summary>","/// <summary>TODO</summary>","/// <summary>Not implemented.</summary>","/// <summary>X operation on the supplied facts.</summary>"})
        {
            var fixture=CSharpSyntaxTree.ParseText(string.Join(Environment.NewLine,"class Probe {",example,"public void Run() {} }")).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
            if (Summary(fixture)!=null) violations.Add("#6 rejected-documentation fixture accepted");
        }
        if(source.Contains("ForeignKeys = true",StringComparison.Ordinal)) violations.Add("#5 mutable connection configuration before version acceptance");
        var ingest=methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="IngestAsync");
        var add=ingest.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(x=>x.Expression.ToString()=="scan.AddScanAsync");
        if(!add.ArgumentList.Arguments.Any(a=>a.Expression.ToString()=="gate")) violations.Add("#2 no shared writer admission for scan batches");
        var mark=typeof(Store).GetMethod("TouchChange",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<string,SyncedEntry?,SyncedEntry?,Action<string,Side,string,bool>>>();
        var folder = new SyncedEntry("A","a","B","b",true,"pc",0,0,"cloud",string.Empty);
        var touches = new HashSet<(Side Side,string Path,bool Subtree)>();
        void CheckMove(SyncedEntry? after, params (Side,string,bool)[] expected)
        {
            touches.Clear(); mark("p",folder,after,(_,side,path,subtree)=>touches.Add((side,path,subtree)));
            if (!touches.SetEquals(expected)) violations.Add("Root commit touched the wrong folder footprints: " + string.Join(", ",touches));
        }
        CheckMove(folder with {LocalPath="A"},(Side.Local,"a",true),(Side.Local,"A",true),(Side.Dropbox,"b",false));
        CheckMove(folder with {DropboxKey="C",DropboxPath="c"},(Side.Local,"a",false),(Side.Dropbox,"b",true),(Side.Dropbox,"c",true));
        CheckMove(null,(Side.Local,"a",true),(Side.Dropbox,"b",true));
        CheckMove(folder with {LocalId="replacement",LocalPath="A"},(Side.Local,"a",true),(Side.Local,"A",true),(Side.Dropbox,"b",true));
        if(violations.Count!=0) throw new InvalidDataException(string.Join("; ",violations));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        foreach (var readOnly in new[] { true, false })
        {
            try { using var store = new Store(path, readOnly, null!); }
            catch (SqliteException) when (!File.Exists(path)) { continue; }
            throw new InvalidDataException("Opening an absent store did not fail without creating it.");
        }
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            if (await Store.OpenAsync(directory.FullName, false, null!, default) != null || directory.EnumerateFileSystemInfos().Any())
                throw new InvalidDataException("An ordinary open created store files.");
            var image = Path.Combine(directory.FullName, "sample.sqlite");
            var cs = new SqliteConnectionStringBuilder { DataSource = image, Pooling = false }.ToString();
            foreach (var other in new[] { Schema.Version - 1, Schema.Version + 1 })
            {
                using (var original=new SqliteConnection(cs)) { original.Open(); using var seed=original.CreateCommand(); seed.CommandText=$"PRAGMA journal_mode=DELETE; PRAGMA user_version={other};"; seed.ExecuteNonQuery(); }
                var bytes = File.ReadAllBytes(image);
                foreach (var ro in new[]{false,true})
                {
                    try { using var wrong = new Store(image,ro,null!); throw new Exception("Accepted incompatible version"); } catch(InvalidDataException) { }
                    if (!File.ReadAllBytes(image).SequenceEqual(bytes) || File.Exists(image+"-wal")) throw new InvalidDataException("Rejected store changed");
                }
                using var read = new SqliteConnection(cs); read.Open(); using var mode=read.CreateCommand(); mode.CommandText="PRAGMA journal_mode";
                if (!Equals(mode.ExecuteScalar(),"delete")) throw new InvalidDataException("Changed rejected journal mode");
            }
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                db.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic:true);
                using var sql = db.CreateCommand();
                sql.CommandText = Schema.Create + $"PRAGMA user_version={Schema.Version};INSERT INTO pair(pair_id,def_json) VALUES('p','{{}}');";
                sql.ExecuteNonQuery();
            }
            using (var store = new Store(image, false, SettingsParser.Load()))
                await Task.WhenAll(new[] { "A", "B" }.Select(key => Task.Run(() => store.CommitAsync("p",null,null,null,key,default))));
            using var inspect = new SqliteConnection(cs);
            inspect.Open();
            inspect.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic:true);
            using var count = inspect.CreateCommand();
            count.CommandText = "SELECT count(*) FROM discovery_deleted_delta WHERE spent=1";
            if ((long)count.ExecuteScalar()! != 2) throw new InvalidDataException("Concurrent acknowledgements lost.");
        }
        finally { directory.Delete(recursive:true); }
    }
    private static string? Summary(MemberDeclarationSyntax method)
    {
        var docs=method.GetLeadingTrivia().Select(t=>t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>();
        var summaries=docs.SelectMany(d=>d.Content.OfType<XmlElementSyntax>()).Where(x=>x.StartTag.Name.ToString()=="summary").ToArray();
        if (summaries.Length!=1) return null;
        var text=string.Concat(summaries[0].Content.Select(c=>c.ToString())).Trim();
        return text.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<2 ||
            text.TrimEnd('.').Equals("Not implemented",StringComparison.OrdinalIgnoreCase) ||
            text.Contains("operation on the supplied facts",StringComparison.OrdinalIgnoreCase) ||
            text.Contains("selected Store operation",StringComparison.OrdinalIgnoreCase) ? null : text;
    }
}
