using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;
/// <summary>Exercises acquisition-bound deletion evidence against SQLite.</summary>
public static class DeletedNamesChecks
{
    internal static async Task<SqliteConnection> OpenAsync()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync();
        db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
        using var setup = db.CreateCommand();
        setup.CommandText = Schema.Create + " ATTACH ':memory:' AS scan_state; " + Schema.CreateScan + " INSERT INTO pair(pair_id,def_json) VALUES('p','{}');";
        await setup.ExecuteNonQueryAsync();
        return db;
    }

    // Runs independently of the packed v16 codecs supplied by the store module.
    internal static async Task VerifyAcquisitionAsync()
    {
        var root = CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root;
        foreach (var file in new[]
        {
            "ScanTable",
            "DeletedNames"
        }

        )
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, "src", "SyncShark.Engine", "Storage", file + ".cs"));
            ValidateDocumentation(source,file);
            if(file=="DeletedNames")
                foreach(var snippet in new[]{"", "/// <summary></summary>", "/// <summary>Returns the current time.</summary>","/// <summary>Reads a file.</summary>"})
                {
                    var mutant=snippet.Length==0?"class Test {}":"class Test {"+Environment.NewLine+snippet+Environment.NewLine+" public Task<bool> ContainsAsync(SqliteConnection db,string pairId,Guid? scanId,string key,bool isFolder,CancellationToken ct)=>throw null; }";
                    try{ValidateDocumentation(mutant,file);}catch(InvalidDataException){continue;}
                    throw new InvalidDataException("Missing or non-operative kind-aware declaration accepted");
                }
        }
        using var db = await OpenAsync();
        var scan = new ScanTable();
        var id = Guid.NewGuid();
        static async IAsyncEnumerable<Listed> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        await scan.AddScanAsync(db, "p", id, Side.Dropbox, Empty(), () => "old", 1, default);
        using var edit = db.CreateCommand();
        edit.CommandText = "UPDATE pair SET def_json=json_set(def_json,'$.LocalRoot','new')";
        await edit.ExecuteNonQueryAsync();
        try
        {
            await scan.History.PublishAsync(db, "p", id, default);
            throw new InvalidDataException("Superseded acquisition published against a new root");
        }
        catch (InvalidOperationException)
        {
        }

        await scan.ClearScanAsync(db, "p", id, default);
        var next = Guid.NewGuid();
        await scan.AddScanAsync(db, "p", next, Side.Dropbox, Empty(), () => "new", 1, default);
        await scan.History.PublishAsync(db, "p", next, default);
        Check.Require(await scan.History.ReadStateAsync(db, "p", default) == ("new", false), "Current empty history did not publish");
    }

    private static void ValidateDocumentation(string source,string file)
    {
        var tree=CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach(var method in tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m=>m.Modifiers.Any(t=>t.Text=="public")))
        {
            var doc=method.GetLeadingTrivia().ToFullString();
            Check.Require(doc.Contains("<summary>",StringComparison.Ordinal) && !doc.Contains("operation on the supplied facts",StringComparison.OrdinalIgnoreCase),"Missing operation-specific summary: "+method.Identifier.Text);
        }
    }
    internal static async Task VerifyUnreadableHistoryAsync()
    {
        using var db=await OpenAsync();var table=new ScanTable();var partial=Guid.NewGuid();var stale=Guid.NewGuid();var key=PathNames.Key("gone");
        static async IAsyncEnumerable<Listed> History(bool complete){await Task.CompletedTask;yield return new("gone",null);if(!complete)throw new IOException("listing interrupted");}
        try{await table.AddScanAsync(db,"p",partial,Side.Dropbox,History(false),()=>"end",1,default);}catch(IOException){}
        var badPartial=await table.History.ContainsAsync(db,"p",partial,key,false,default) || await table.History.ContainsAsync(db,"p",partial,key+"/child",true,default);
        await table.AddScanAsync(db,"p",stale,Side.Dropbox,History(true),()=>"end",1,default);
        using var edit=db.CreateCommand();edit.CommandText="UPDATE pair SET def_json=json_set(def_json,'$.DropboxRoot','different')";await edit.ExecuteNonQueryAsync();
        var badStale=await table.History.ContainsAsync(db,"p",stale,key,false,default) || await table.History.ContainsAsync(db,"p",stale,key+"/child",true,default);
        Check.Require(!badPartial && !badStale,$"Incomplete or obsolete acquisition authorized deletion: partial={badPartial} stale={badStale}");
    }
    /// <summary>A history cursor and names must become durable together and never revive spent evidence.</summary>
    public static async Task RunAsync()
    {
        using var db = await OpenAsync();
        var table = new ScanTable();
        var names = table.History;
        var scan = Guid.NewGuid();
        static async IAsyncEnumerable<Listed> History()
        {
            await Task.CompletedTask;
            yield return new("old", null);
        }

        await table.AddScanAsync(db, "p", scan, Side.Dropbox, History(), () => "cursor", 1, default);
        var key = PathNames.Key("old");
        Check.Require(await names.ContainsAsync(db, "p", scan, key, default) && !await names.ContainsAsync(db, "p", null, key, default), "Unpublished deletion evidence escaped acquisition");
        Check.Require(!await names.ContainsAsync(db, "p", Guid.NewGuid(), key, default), "History was visible to a different acquisition");
        Check.Require(await names.ContainsAsync(db, "p", scan, key + "/child", true, default) && !await names.ContainsAsync(db, "p", scan, key + "/child", false, default), "Only folders inherit an ancestor tombstone");
        try
        {
            await names.PublishAsync(db, "p", scan, "different", default);
            throw new InvalidDataException("Wrong terminal cursor published");
        }
        catch (InvalidOperationException)
        {
        }

        await names.PublishAsync(db, "p", scan, default);
        await table.ClearScanAsync(db, "p", scan, default);
        Check.Require(await names.ContainsAsync(db, "p", null, key, default) && await names.ReadStateAsync(db, "p", default) == ("cursor", false), "Acquisition names or cursor not published");
        using var spent = db.CreateCommand();
        spent.CommandText = "INSERT INTO discovery_deleted_delta VALUES(1,$k,1)";
        spent.Parameters.AddWithValue("$k", key);
        await spent.ExecuteNonQueryAsync();
        Check.Require(!await names.ContainsAsync(db, "p", null, key, default), "Spent name revived from a compacted block");
        var failed = Guid.NewGuid();
        static async IAsyncEnumerable<Listed> Incomplete()
        {
            await Task.CompletedTask;
            yield return new("partial", null);
            throw new IOException("Interrupted listing");
        }

        var cursorRead = false;
        try
        {
            await table.AddScanAsync(db, "p", failed, Side.Dropbox, Incomplete(), () =>
            {
                cursorRead = true;
                return "partial";
            }, 1, default);
            throw new InvalidDataException("Listing failure was ignored");
        }
        catch (IOException error)when (error.Message == "Interrupted listing")
        {
        }

        try
        {
            await names.PublishAsync(db, "p", failed, default);
            throw new InvalidDataException("Partial history published");
        }
        catch (InvalidOperationException)
        {
        }

        Check.Require(!cursorRead && await names.ReadStateAsync(db, "p", default) == ("cursor", false), "Interrupted listing advanced the cursor");
    }
}
