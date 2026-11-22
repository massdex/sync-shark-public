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
            var targets=file=="DeletedNames" ? new[]{"Publish the exact terminal cursor recorded by the completed listing for this pair and scan.","Check the exact file tombstone; a folder may also match an ancestor. A spent candidate is never eligible."} : new[]{"The deletion history and completed cursors belonging to this store, across its read and write connections."};
            var accepted=new List<string>();
            foreach(var target in targets)
            {
                var original="/// <summary>"+target+"</summary>";
                Check.Require(source.Contains(original,StringComparison.Ordinal),"Missing documentation mutation target: "+target);
                foreach(var (kind,replacement) in new[]{("missing",""),("empty","/// <summary></summary>"),("wrong","/// <summary>Reads a file.</summary>")})
                {
                    try { ValidateDocumentation(source.Replace(original,replacement,StringComparison.Ordinal),file); }
                    catch(InvalidDataException) { Console.WriteLine($"Rejected {kind} {file} summary: {target}");continue; }
                    accepted.Add(kind+" "+target);
                }
            }
            Check.Require(accepted.Count==0,"Documentation verifier accepted mutants: "+string.Join("; ",accepted));
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
        if(file=="DeletedNames")
        {
            var lookup=tree.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m=>m.Identifier.ValueText=="ContainsAsync" && m.Modifiers.Any(t=>t.Text=="public") && m.ParameterList.Parameters.Any(p=>p.Type?.ToString()=="bool" && p.Identifier.ValueText=="isFolder"));
            var summary=lookup?.GetLeadingTrivia().Select(t=>t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().SelectMany(d=>d.Content).OfType<XmlElementSyntax>().SingleOrDefault(e=>e.StartTag.Name.LocalName.ValueText=="summary")?.Content.ToString()??"";
            Check.Require(summary.Contains("Check the exact file tombstone; a folder may also match an ancestor.",StringComparison.Ordinal),"Missing exact-file / ancestor-folder lookup contract");
        }
        foreach(var method in tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m=>m.Modifiers.Any(t=>t.Text=="public")))
        {
            var doc=method.GetLeadingTrivia().ToFullString();
            Check.Require(doc.Contains("<summary>",StringComparison.Ordinal) && !doc.Contains("operation on the supplied facts",StringComparison.OrdinalIgnoreCase),"Missing operation-specific summary: "+method.Identifier.Text);
        }
    }
    internal static async Task PublishedHistoryAsync()
    {
        using var db=await OpenAsync();using var seed=db.CreateCommand();seed.CommandText="UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0";await seed.ExecuteNonQueryAsync();
        var table=new ScanTable();var scan=Guid.NewGuid();static async IAsyncEnumerable<Listed> Names(){await Task.CompletedTask;yield return new("gone",null);}
        await table.AddScanAsync(db,"p",scan,Side.Dropbox,Names(),()=>"finished",1,default);
        await table.History.PublishAsync(db,"p",scan,default);
        foreach(var token in new[]{scan,Guid.NewGuid()}){var key=PathNames.DropboxKey("gone");Check.Require(await table.History.ContainsAsync(db,"p",token,key,false,default) && await table.History.ContainsAsync(db,"p",token,key+"/child",true,default),"Published history was forgotten by the publishing or ordinary scan");}
    }
    internal static async Task VerifyUnreadableHistoryAsync()
    {
        using var db=await OpenAsync();var table=new ScanTable();var partial=Guid.NewGuid();var stale=Guid.NewGuid();var key=PathNames.Key("gone");
        static async IAsyncEnumerable<Listed> History(bool complete){await Task.CompletedTask;yield return new("gone",null);if(!complete)throw new IOException("listing interrupted");}
        try{await table.AddScanAsync(db,"p",partial,Side.Dropbox,History(false),()=>"end",1,default);}catch(IOException){}
        var badPartial=await table.History.ContainsAsync(db,"p",partial,key,false,default) || await table.History.ContainsAsync(db,"p",partial,key+"/child",true,default);
        await table.AddScanAsync(db,"p",stale,Side.Dropbox,History(true),()=>"end",1,default);
        var child=key+"/child";
        Check.Require(await table.History.ContainsAsync(db,"p",stale,key,false,default) && await table.History.ContainsAsync(db,"p",stale,key,true,default)
            && !await table.History.ContainsAsync(db,"p",stale,child,false,default) && await table.History.ContainsAsync(db,"p",stale,child,true,default),"File must match exactly; only a folder may inherit an ancestor tombstone");
        using var edit=db.CreateCommand();edit.CommandText="UPDATE pair SET def_json=json_set(def_json,'$.DropboxRoot','different')";await edit.ExecuteNonQueryAsync();
        var badStale=await table.History.ContainsAsync(db,"p",stale,key,false,default) || await table.History.ContainsAsync(db,"p",stale,key+"/child",true,default);
        Check.Require(!badPartial && !badStale,$"Incomplete or obsolete acquisition authorized deletion: partial={badPartial} stale={badStale}");
    }
    internal static async Task CompactWorkAsync(int count,bool corrupt)
    {
        using var db=await OpenAsync();var table=new ScanTable();var scan=Guid.NewGuid();var history=table.History;
        var keys=Enumerable.Range(0,count).Select(i=>PathNames.Key($"old/{i:D4}")).ToArray();
        var bytes=Massdex.SyncShark.Engine.Storage.Encoding.EncodeDeletedNames(keys);if(corrupt)bytes[^1]^=1;
        using var seed=db.CreateCommand();seed.CommandText="UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0;INSERT INTO discovery_deleted_block VALUES(1,$last,$count,$bytes);INSERT INTO discovery_deleted_delta VALUES(1,'spent',1)";
        seed.Parameters.AddWithValue("$last",keys[^1]);seed.Parameters.AddWithValue("$count",count);seed.Parameters.AddWithValue("$bytes",bytes);await seed.ExecuteNonQueryAsync();
        if(corrupt)
        {
            bool found;
            try {found=await history.ContainsAsync(db,"p",scan,keys[0],false,default);}catch(InvalidDataException e){throw new InvalidDataException("Corrupt deletion block escaped Compare instead of scheduling reacquisition",e);}
            Check.Require(!found && await history.ReadStateAsync(db,"p",default)==(null,true) && !await history.ContainsAsync(db,"p",scan,keys[^1],false,default),"Corrupt history remained deletion evidence or stalled a later candidate");
            seed.CommandText="SELECT (SELECT count(*) FROM issue WHERE pair_id=1 AND path='' AND kind=3 AND cause='DeletedHistoryCorrupt')=1 AND EXISTS(SELECT 1 FROM discovery_deleted_delta WHERE spent=1)";
            Check.Require(Convert.ToInt32(seed.ExecuteScalar())==1,"Invalidation lost its notice or spent mask");
            static async IAsyncEnumerable<Listed> Again(){await Task.CompletedTask;yield return new("fresh",null);}
            var retry=Guid.NewGuid();await table.AddScanAsync(db,"p",retry,Side.Dropbox,Again(),()=>"new",1,default);await history.PublishAsync(db,"p",retry,default);
            Check.Require(await history.ContainsAsync(db,"p",retry,PathNames.Key("fresh"),false,default) && !await history.ContainsAsync(db,"p",retry,keys[0],false,default),"Retry failed to reacquire clean deletion history");return;
        }
        int reads=0;SQLitePCL.strdelegate_trace trace=(_,sql)=>{if(sql.StartsWith("SELECT",StringComparison.OrdinalIgnoreCase)&&sql.Contains("FROM discovery_deleted_block",StringComparison.OrdinalIgnoreCase))reads++;};
        SQLitePCL.raw.sqlite3_trace(db.Handle!,trace,null);
        try {foreach(var key in keys)Check.Require(await history.ContainsAsync(db,"p",scan,key,false,default),"A restored compact name disappeared");}
        finally {SQLitePCL.raw.sqlite3_trace(db.Handle!,(SQLitePCL.strdelegate_trace)null!,null);}
        Console.WriteLine($"Compact history: {count} probes, one populated block, {reads} block statements");
        Check.Require(reads is >0 and <=2,$"Repeated compact block decode: {reads} block reads for {count} probes");
    }
    /// <summary>A history cursor and names must become durable together and never revive spent evidence.</summary>
    public static async Task RunAsync()
    {
        using var db = await OpenAsync();
        var table = new ScanTable();
        var errors=new List<string>();
        foreach(var test in new[]{(32,true),(32,false),(64,false)})
            try{await CompactWorkAsync(test.Item1,test.Item2);}catch(InvalidDataException e){errors.Add(e.Message);}
        Check.Require(errors.Count==0,string.Join("; ",errors));
        var names = table.History;
        var scan = Guid.NewGuid();
        static async IAsyncEnumerable<Listed> History()
        {
            await Task.CompletedTask;
            yield return new("old", null);
        }

        await table.AddScanAsync(db, "p", scan, Side.Dropbox, History(), () => "cursor", 1, default);
        var key = PathNames.Key("old");
        Check.Require(await names.ContainsAsync(db, "p", scan, key, false, default) && !await names.ContainsAsync(db, "p", null, key, false, default), "Unpublished deletion evidence escaped acquisition");
        Check.Require(!await names.ContainsAsync(db, "p", Guid.NewGuid(), key, false, default), "History was visible to a different acquisition");
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
        Check.Require(await names.ContainsAsync(db,"p",scan,key,false,default),"Published compact block lost on active Repair");
        await table.ClearScanAsync(db, "p", scan, default);
        Check.Require(await names.ContainsAsync(db, "p", null, key, false, default) && await names.ReadStateAsync(db, "p", default) == ("cursor", false), "Acquisition names or cursor not published");
        using var spent = db.CreateCommand();
        spent.CommandText = "INSERT INTO discovery_deleted_delta VALUES(1,$k,1)";
        spent.Parameters.AddWithValue("$k", key);
        await spent.ExecuteNonQueryAsync();
        Check.Require(!await names.ContainsAsync(db, "p", null, key, false, default), "Spent name revived from a compacted block");
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
