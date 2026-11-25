using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;
/// <summary>Exercises acquisition-bound deletion evidence against SQLite.</summary>
public static class DeletedNamesChecks
{
    internal static async Task<SqliteConnection> OpenAsync(string connectionString="Data Source=:memory:")
    {
        var db = new SqliteConnection(connectionString);
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
            var targets=file=="DeletedNames" ? new[]{"Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.","Check the exact file tombstone; a folder may also match an ancestor. A spent candidate is never eligible by itself; a file spend that recorded its deletion time defers to the current history."} : new[]{"The deletion history and completed cursors belonging to this store, across its read and write connections."};
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
        foreach(var member in tree.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m=>m.Modifiers.Any(t=>t.Text=="public")))
        {
            var name=member switch { MethodDeclarationSyntax method=>method.Identifier.ValueText,PropertyDeclarationSyntax property=>property.Identifier.ValueText,TypeDeclarationSyntax type=>type.Identifier.ValueText,_=>throw new InvalidDataException("Undocumented declaration kind") };
            var summary=member.GetLeadingTrivia().Select(t=>t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().SelectMany(d=>d.Content).OfType<XmlElementSyntax>().SingleOrDefault(e=>e.StartTag.Name.LocalName.ValueText=="summary")?.Content.ToString().Trim()??"";
            string contract=name switch {
                "PublishAsync"=>"completed", "ContainsAsync" when member is MethodDeclarationSyntax lookup && lookup.ParameterList.Parameters.Any(p=>p.Identifier.Text=="isFolder")=>"exact file tombstone; a folder may also match an ancestor", "ContainsAsync"=>"exact deletion evidence for a file, respecting its spent mask", "ReadStateAsync"=>"deleted-history cursor and acquisition debt", "History"=>"completed cursors belonging to this store, across its read and write connections",
                "Begin"=>"before taking this acquisition's listing boundary", "HasActive"=>"original footprints for an active scan", "Touch"=>"already-canonical side key", "Touched"=>"descendant commits when a folder is being settled",
                "AddScanAsync" when member is MethodDeclarationSyntax m && m.ParameterList.Parameters.Any(p=>p.Identifier.Text=="writerGate")=>"without holding its gate while waiting for listing events",
                "AddScanAsync"=>"capture the history cursor only after successful enumeration", "ClearScanAsync"=>"acquisition rows, scratch names and pending terminal cursor", "CompareAsync"=>"including notice-only rows and independently retained put-back notices", "RenamesAsync"=>"unmatched identities ordered by the binding's shallowest move",
                "DeletedNames"=>"published Dropbox deletion history, including spent-name masks", "ScanTable"=>"compares its observed endpoints with saved bindings", _=>throw new InvalidDataException("No public summary contract: "+name) };
            Check.Require(summary.Contains(contract,StringComparison.OrdinalIgnoreCase),"Missing operation-specific summary: "+name);
            if(name=="PublishAsync")Check.Require(summary.Contains("only this completed history acquisition",StringComparison.OrdinalIgnoreCase) && summary.Contains("its own terminal cursor",StringComparison.OrdinalIgnoreCase) && summary.Contains("ordinary or incomplete scan cannot publish",StringComparison.OrdinalIgnoreCase),"Missing acquisition-bound publication rule");
            if(name=="Touch")Check.Require(!((MethodDeclarationSyntax)member).DescendantNodes().OfType<InvocationExpressionSyntax>().Any(n=>(n.Expression is MemberAccessExpressionSyntax access?access.Name.Identifier.ValueText:n.Expression is IdentifierNameSyntax simple?simple.Identifier.ValueText:null) is "KeyFor" or "Key" or "DropboxKey"),"ScanTable.Touch refolds the canonical key with a PathNames key operation");
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
    internal static async Task RestorePartitionAsync(int count)
    {
        using var db=await OpenAsync();var table=new ScanTable();var scan=Guid.NewGuid();using var sql=db.CreateCommand();
        sql.CommandText="UPDATE pair SET deleted_history_cursor='valid',deleted_history_owed=0";await sql.ExecuteNonQueryAsync();
        int blockReads=0, scratchReads=0;byte[]? token=null;
        SQLitePCL.strdelegate_trace traced=(_,text)=>{if(text.StartsWith("SELECT",StringComparison.OrdinalIgnoreCase) && text.Contains("FROM discovery_deleted_block",StringComparison.OrdinalIgnoreCase))blockReads++;if(text.StartsWith("SELECT 1 FROM scan_state.deleted_name",StringComparison.OrdinalIgnoreCase))scratchReads++;};
        SQLitePCL.raw.sqlite3_trace(db.Handle!,traced,null);
        try
        {
            var k=PathNames.Key("kept");Check.Require(!await table.History.ContainsAsync(db,"p",scan,k,false,default),"Empty restore selected deletion");
            // The real codec is external. Seed only its output partition after the empty restore;
            // then observe actual public lookups and scan cleanup, not a stand-in decoder.
            var tokenMethod=typeof(DeletedNames).GetMethod("RestoredToken",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            Check.Require(tokenMethod!=null,"No restored scratch partition for subsequent candidates");
            token=(byte[])tokenMethod!.Invoke(null,new object?[]{"p",scan})!;sql.CommandText="INSERT INTO scan_state.deleted_name VALUES($s,$k)";sql.Parameters.AddWithValue("$s",token);sql.Parameters.AddWithValue("$k",k);await sql.ExecuteNonQueryAsync();
            for(int i=0;i<count;i++)Check.Require(await table.History.ContainsAsync(db,"p",scan,k,false,default),"Lookup ignored restored names in indexed scratch");
            Check.Require(blockReads==1 && scratchReads==count+1,$"Restore was repeated or bypassed: {blockReads} block selects, {scratchReads} scratch probes for {count+1} candidates");
            Console.WriteLine($"Restored partition: {count+1} candidates, {blockReads} block selects, {scratchReads} indexed probes");
            await table.ClearScanAsync(db,"p",scan,default);sql.CommandText="SELECT count(*) FROM scan_state.deleted_name WHERE scan_id=$s";Check.Require(Convert.ToInt32(sql.ExecuteScalar())==0,"Clearing scan left verified names behind");
        }finally{SQLitePCL.raw.sqlite3_trace(db.Handle!,(SQLitePCL.strdelegate_trace)null!,null);}
    }
    internal static async Task InvalidationBoundaryAsync()
    {
        using var db=await OpenAsync();using var sql=db.CreateCommand();var token=Guid.NewGuid().ToByteArray();sql.Parameters.AddWithValue("$s",token);
        sql.CommandText="UPDATE pair SET deleted_history_cursor='old',deleted_history_owed=0;INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(1,'mask',1),(1,'stale',0);INSERT INTO scan_state.deleted_name VALUES($s,'partial')";await sql.ExecuteNonQueryAsync();
        using var tx=db.BeginTransaction();var handler=typeof(DeletedNames).GetMethod("InvalidateAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
        Check.Require(handler!=null,"No corruption transition to clear cursor and discard partially restored names");
        try{await (Task)handler!.Invoke(null,new object?[]{db,tx,"p",token,CancellationToken.None})!;}
        catch(NotImplementedException error) when(error.TargetSite?.DeclaringType==typeof(NoticeTable)){/* The real notice write and commit are F1. */}
        sql.Transaction=tx;sql.CommandText="SELECT (SELECT deleted_history_cursor IS NULL AND deleted_history_owed=1 FROM pair) AND NOT EXISTS(SELECT 1 FROM scan_state.deleted_name WHERE scan_id=$s) AND EXISTS(SELECT 1 FROM discovery_deleted_delta WHERE path_key='stale' AND spent=0) AND EXISTS(SELECT 1 FROM discovery_deleted_delta WHERE path_key='mask' AND spent=1)";
        Check.Require(Convert.ToInt32(sql.ExecuteScalar())==1,"Corruption must withdraw the cursor and partial scratch without discarding a valid delta or spent mask");
    }
    internal static async Task SavedLookupAsync()
    {
        using var db=await OpenAsync();using var sql=db.CreateCommand();sql.CommandText="DETACH scan_state; UPDATE pair SET deleted_history_cursor='ok',deleted_history_owed=0; INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(1,'kept',0)";await sql.ExecuteNonQueryAsync();
        var history=new DeletedNames();string? lookup=null;
        SQLitePCL.strdelegate_trace trace=(_,text)=>{if(text.StartsWith("SELECT",StringComparison.OrdinalIgnoreCase) && text.Contains("FROM discovery_deleted_block",StringComparison.OrdinalIgnoreCase))lookup=text;};
        SQLitePCL.raw.sqlite3_trace(db.Handle!,trace,null);
        try{Check.Require(await history.ContainsAsync(db,"p",null,"kept",default) && !await history.ContainsAsync(db,"p",null,"other",default),"Saved lookup lost its durable delta or selected a missing name");}
        catch(SqliteException e){throw new InvalidDataException("Saved lookup without a scan requires no scratch attachment",e);}
        finally{SQLitePCL.raw.sqlite3_trace(db.Handle!,(SQLitePCL.strdelegate_trace)null!,null);}
        Check.Require(lookup!=null,"Detached lookup skipped the saved-block seek");
        sql.CommandText="EXPLAIN QUERY PLAN "+lookup;sql.Parameters.AddWithValue("$p","p");sql.Parameters.AddWithValue("$k","other");
        using var plan=await sql.ExecuteReaderAsync();var steps=new List<string>();int detail=plan.GetOrdinal("detail");while(await plan.ReadAsync())steps.Add(plan.GetString(detail));
        bool Seek(string alias,char bound)=>steps.Any(step=>step.StartsWith("SEARCH "+alias+" USING ",StringComparison.OrdinalIgnoreCase) && step.Contains("pair_id=? AND last_key"+bound,StringComparison.OrdinalIgnoreCase));
        Check.Require(Seek("b",'>') && Seek("prior",'<'),"Detached lookup did not seek the candidate and predecessor by pair and last key: "+string.Join("; ",steps));
    }
    internal static async Task VerifyBeforeListingAsync()
    {
        using var db = await OpenAsync();
        var history = new DeletedNames();
        var scan = Guid.NewGuid();
        using var sql = db.CreateCommand();
        sql.CommandText = "UPDATE pair SET deleted_history_cursor='ok',deleted_history_owed=0";
        await sql.ExecuteNonQueryAsync();
        int count = 0;
        SQLitePCL.strdelegate_trace trace = (_, text) =>
        {
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && text.Contains("FROM discovery_deleted_block", StringComparison.OrdinalIgnoreCase)) count++;
        };
        SQLitePCL.raw.sqlite3_trace(db.Handle!, trace, null);
        try
        {
            var state = await history.ReadStateAsync(db, "p", scan, default);
            Check.Require(state == ("ok", false) && count == 1, "Saved history was not verified before choosing the listing");
            await history.ContainsAsync(db, "p", scan, "missing", default);
            Check.Require(count == 1, "Compare restored the already verified listing a second time");
        }
        finally { SQLitePCL.raw.sqlite3_trace(db.Handle!, (SQLitePCL.strdelegate_trace)null!, null); }
    }
    internal static async Task CompactWorkAsync(int count,bool corrupt,string? detachedFile=null)
    {
        var cs=detachedFile==null?"Data Source=:memory:":new SqliteConnectionStringBuilder{DataSource=detachedFile,Pooling=false}.ToString();
        using var db=await OpenAsync(cs);var table=new ScanTable();var scan=Guid.NewGuid();var history=table.History;
        var keys=Enumerable.Range(0,count).Select(i=>PathNames.Key($"old/{i:D4}")).ToArray();
        var bytes=Massdex.SyncShark.Engine.Storage.DeletedNamesCodec.EncodeDeletedNames(keys);if(corrupt)bytes[^1]^=1;
        using var seed=db.CreateCommand();seed.CommandText="UPDATE pair SET deleted_history_cursor='previous',deleted_history_owed=0;INSERT INTO discovery_deleted_block VALUES(1,$last,$count,$bytes);INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(1,'spent',1),(1,pathkey('append'),0)";
        seed.Parameters.AddWithValue("$last",keys[^1]);seed.Parameters.AddWithValue("$count",count);seed.Parameters.AddWithValue("$bytes",bytes);await seed.ExecuteNonQueryAsync();
        if(detachedFile!=null)
        {
            seed.CommandText="DETACH scan_state";await seed.ExecuteNonQueryAsync();
            Check.Require(!await history.ContainsAsync(db,"p",null,keys[0],false,default),"Detached lookup accepted a matching name before validating the corrupt trailer");
            db.Close();using var reopened=new SqliteConnection(cs);await reopened.OpenAsync();using var state=reopened.CreateCommand();
            state.CommandText="SELECT (SELECT deleted_history_cursor IS NULL AND deleted_history_owed=1 FROM pair WHERE id=1) AND (SELECT count(*) FROM issue WHERE pair_id=1 AND path='' AND kind=3 AND cause='DeletedHistoryCorrupt')=1 AND EXISTS(SELECT 1 FROM discovery_deleted_delta WHERE pair_id=1 AND spent=0) AND EXISTS(SELECT 1 FROM discovery_deleted_delta WHERE pair_id=1 AND spent=1)";
            Check.Require(Convert.ToInt32(await state.ExecuteScalarAsync())==1,"Detached corrupt lookup did not commit history invalidation and its notice");return;
        }
        if(corrupt)
        {
            (string? Cursor,bool Owed) verified;
            try{verified=await history.ReadStateAsync(db,"p",scan,default);}catch(InvalidDataException e){throw new InvalidDataException("Corrupt deletion history escaped pre-listing verification",e);}
            Check.Require(verified==(null,true) && await history.ReadStateAsync(db,"p",default)==(null,true),"Corrupt cursor was not withdrawn before the history-listing decision");
            Check.Require(!await history.ContainsAsync(db,"p",scan,keys[0],false,default),"Corrupt history selected deletion after invalidation");
            seed.CommandText="SELECT (SELECT count(*) FROM issue WHERE pair_id=1 AND path='' AND kind=3 AND cause='DeletedHistoryCorrupt')=1 AND EXISTS(SELECT 1 FROM discovery_deleted_delta WHERE spent=1)";
            Check.Require(Convert.ToInt32(seed.ExecuteScalar())==1,"Invalidation lost its notice or spent mask");
            static async IAsyncEnumerable<Listed> Again(){await Task.CompletedTask;yield return new("fresh",null);}
            var retry=scan;await table.AddScanAsync(db,"p",retry,Side.Dropbox,Again(),()=>"new",1,default);await history.PublishAsync(db,"p",retry,default);
            Check.Require(await history.ContainsAsync(db,"p",retry,PathNames.Key("fresh"),false,default) && await history.ContainsAsync(db,"p",retry,PathNames.Key("append"),false,default) && !await history.ContainsAsync(db,"p",retry,keys[0],false,default),"Same-scan reacquisition did not fold the independent delta into clean history");return;
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
        var dir=Directory.CreateTempSubdirectory();
        try{await CompactWorkAsync(32,true,Path.Combine(dir.FullName,"detached.sqlite"));}catch(InvalidDataException e){errors.Add(e.Message);}finally{dir.Delete(true);}
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
        spent.CommandText = "INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(1,$k,1)";
        spent.Parameters.AddWithValue("$k", key);
        await spent.ExecuteNonQueryAsync();
        Check.Require(!await names.ContainsAsync(db, "p", null, key, false, default), "Spent name revived from a compacted block");
        // A dated file spend defers to the current history; that history still lists this name, so the file stays eligible.
        await new Committer().CommitAsync(db, "p", null, null, null, key, default, DateTimeOffset.UnixEpoch);
        Check.Require(await names.ContainsAsync(db, "p", null, key, false, default) && !await names.ContainsAsync(db, "p", null, key, true, default),
            "A dated file spend hid the listed file name, or opened the name for a folder");
        // A spend without a deletion time keeps masking.
        await new Committer().CommitAsync(db, "p", null, null, null, key, default);
        Check.Require(!await names.ContainsAsync(db, "p", null, key, false, default), "A spend without a deletion time stopped masking");
        // A dated spend alone never admits a name the current history does not list.
        await new Committer().CommitAsync(db, "p", null, null, null, "unlisted", default, DateTimeOffset.UnixEpoch);
        Check.Require(!await names.ContainsAsync(db, "p", null, "unlisted", false, default), "A dated spend alone admitted an unlisted file name");
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
