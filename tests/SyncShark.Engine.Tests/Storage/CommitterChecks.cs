using Massdex.SyncShark.Engine.Storage;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises real binding transactions in a disposable memory image.</summary>
public static class CommitterChecks
{
    /// <summary>Checks publication inheritance, atomic failure, mapped descendant rebasing and spent-name publication.</summary>
    public static async Task RunAsync()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        db.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic:true);
        using var sql = db.CreateCommand();
        sql.CommandText = Schema.Create + "INSERT INTO pair(pair_id,def_json) VALUES('p','{}');";
        sql.ExecuteNonQuery();
        var commits = new Committer();
        var old = new SyncedEntry("A/X", "a/x", "B/Y", "b/y", false, "42", 4, 6, "remote", "opaque");
        var boundary = new JournalPosition(ulong.MaxValue,17);
        await commits.CommitAsync(db,"p",null,old,boundary,null,default);
        var moved = old with { LocalPath="a/z", LocalKey="A/Z", LocalPublished=boundary };
        await commits.CommitAsync(db,"p",old with { LocalPublished=boundary },moved,null,"SPENT",default);
        await commits.ProjectSubtreeAsync(db,"p",new("a","b"),new("left","right"),default);
        sql.CommandText="SELECT count(*) FROM synced_entry WHERE local_path='left/z' AND dropbox_path='right/y' AND local_published_journal=-1 AND local_published_usn=17 AND typeof(local_id)='integer'";
        if ((long)sql.ExecuteScalar()! != 1) throw new InvalidDataException("Mapped paths or publication evidence lost.");
        sql.CommandText="SELECT spent FROM discovery_deleted_delta WHERE path_key='SPENT'";
        if ((long)sql.ExecuteScalar()! != 1) throw new InvalidDataException("Deletion not acknowledged.");
        var saved=moved with { LocalKey="LEFT/Z",LocalPath="left/z",DropboxKey="RIGHT/Y",DropboxPath="right/y",LocalPublished=boundary };
        // Exercise the same group path Store uses without reaching any W05 publication callback.
        var window=(SettingsParser.Load() with { StoreBatchRows=2 }).StoreBatchRows;
        var queue=typeof(Committer).GetMethod("QueueAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        using var gate = new SemaphoreSlim(0,1);
        Task Admit(SyncedEntry? prior,SyncedEntry? next,string? key,Action? notify=null) =>
            (Task)queue.Invoke(commits,[db,gate,window,"p",prior,next,null,key,notify ?? (()=>{}),CancellationToken.None])!;
        var pending=typeof(Committer).GetField("pending",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(commits)!;
        int partial=0;
        db.CreateFunction("partial_seen", () => ++partial);
        sql.CommandText="""
            CREATE TRIGGER fail_second BEFORE INSERT ON discovery_deleted_delta WHEN new.path_key='BAD'
            BEGIN SELECT partial_seen() WHERE EXISTS(SELECT 1 FROM synced_entry WHERE local_size=777);
            SELECT RAISE(ABORT,'synthetic second write failure'); END;
            """;
        sql.ExecuteNonQuery();
        var bad=Admit(saved,saved with { LocalSize=777 },"BAD");
        var good=Admit(null,null,"GOOD");
        // The committed callback must never cause SQL to be replayed.
        var failures=new List<string>();
        var extra=Enumerable.Range(0,window*3).Select(i=>Admit(null,null,"N"+i)).ToArray();
        lock(pending) if (((System.Collections.ICollection)pending).Count!=window) failures.Add("#3 blocked writer must buffer exactly StoreBatchRows, not the extra submissions");
        if (extra.Any(task=>task.IsCompleted)) failures.Add("#3 enqueue completed before the blocked writer admitted it");
        gate.Release();
        try { await bad; failures.Add("#4 failed second write was reported durable"); } catch(SqliteException e) when(e.SqliteErrorCode==19) { }
        await good; await Task.WhenAll(extra);
        int postWrites=0, siblingWrites=0;
        db.CreateFunction("observe_post",() => ++postWrites);
        db.CreateFunction("observe_sibling",() => ++siblingWrites);
        sql.CommandText="""
            CREATE TRIGGER post_sql BEFORE INSERT ON discovery_deleted_delta WHEN new.path_key='POST' BEGIN SELECT observe_post(); END;
            CREATE TRIGGER sibling_sql BEFORE INSERT ON discovery_deleted_delta WHEN new.path_key='SIBLING' BEGIN SELECT observe_sibling(); END;
            """;
        sql.ExecuteNonQuery();
        await gate.WaitAsync();
        var siblingAtCallback=0;
        var post=Admit(null,null,"POST",()=>{ siblingAtCallback=siblingWrites; throw new InvalidDataException(); });
        var sibling=Admit(null,null,"SIBLING");
        lock(pending) if(((System.Collections.ICollection)pending).Count!=2) failures.Add("POST and sibling were not admitted together");
        gate.Release();
        try { await post; failures.Add("Lost post-commit callback failure"); } catch(InvalidDataException) { }
        await sibling;
        if (siblingAtCallback!=1 || postWrites!=1 || siblingWrites!=1) failures.Add($"Multi-item callback replay: SQL POST={postWrites}, sibling={siblingWrites}, sibling at callback={siblingAtCallback}");
        sql.CommandText="SELECT local_path,dropbox_key_delta,dropbox_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash,local_published_journal,local_published_usn FROM synced_entry";
        using(var rd=sql.ExecuteReader()) if(!rd.Read() || Massdex.SyncShark.Engine.Storage.Encoding.ReadSynced(rd,"LEFT/Z",0)!=saved || rd.Read()) failures.Add("#4 complete original binding was not restored");
        sql.CommandText="SELECT count(*) FROM discovery_deleted_delta WHERE path_key='BAD'";
        if((long)sql.ExecuteScalar()! !=0 || partial==0) failures.Add("#4 rollback not proven after a real binding write");
        sql.CommandText="SELECT count(*) FROM discovery_deleted_delta WHERE path_key='GOOD' AND spent=1";
        if((long)sql.ExecuteScalar()! !=1) failures.Add("#4 independent neighbour did not settle");
        // A local-only move must not even UPDATE a row reached only via the stationary Dropbox prefix.
        var stationary=old with { LocalPath="else/x", LocalKey="ELSE/X", DropboxPath="RiGhT/x", DropboxKey="RIGHT/X" };
        await commits.CommitAsync(db,"p",null,stationary,null,null,default);
        sql.CommandText="CREATE TEMP TABLE touched(path TEXT); CREATE TEMP TRIGGER projected AFTER UPDATE ON synced_entry BEGIN INSERT INTO touched VALUES(old.local_path); END;";
        sql.ExecuteNonQuery();
        await commits.ProjectSubtreeAsync(db,"p",new("left","right"),new("LEFT2","right"),default);
        sql.CommandText="SELECT count(*) FROM touched";
        if ((long)sql.ExecuteScalar()! != 1) failures.Add("Local-only move rewrote a stationary range");
        sql.CommandText="SELECT count(*) FROM synced_entry WHERE local_path='else/x' AND dropbox_path='RiGhT/x' OR local_path='LEFT2/z' AND dropbox_path='right/y'";
        if ((long)sql.ExecuteScalar()! != 2) failures.Add("Local-only move changed stationary spelling or missed the moved binding");
        sql.CommandText="DELETE FROM touched"; sql.ExecuteNonQuery();
        await commits.ProjectSubtreeAsync(db,"p",new("LEFT2","RiGhT"),new("LEFT2","right"),default);
        sql.CommandText="SELECT count(*) FROM synced_entry WHERE dropbox_path IN ('right/x','right/y')";
        if ((long)sql.ExecuteScalar()! != 2) failures.Add("Case-only Dropbox projection missed a descendant");
        // Observe the actual inserted columns even on a lane where the notice table has only its PREP stub.
        var inserted = new Dictionary<string,(long Journal,long Usn)>();
        db.CreateFunction("observe_boundary",(string id,long journal,long usn)=>{ inserted[id]=(journal,usn); return 1; });
        sql.CommandText="CREATE TEMP TRIGGER keep_boundary AFTER INSERT ON synced_entry BEGIN SELECT observe_boundary(new.dropbox_id,coalesce(new.local_published_journal,-1),coalesce(new.local_published_usn,-1)); END;";
        sql.ExecuteNonQuery();
        var notice=new Notice(0, NoticeKind.Info,TextKey.InfoKeptBoth,"copy",new([],null),DateTimeOffset.UnixEpoch,false);
        async Task ProbeCopy(string tag,bool both)
        {
            var copy=old with {LocalKey=PathNames.Key(tag),LocalPath=tag,LocalId=tag,DropboxId=tag,DropboxPath=tag,DropboxKey=PathNames.Key(tag),LocalPublished=new(31,91)};
            var other=old with {LocalKey="OTHER",LocalPath="other",LocalId="other",DropboxPath="other",DropboxKey="OTHER",DropboxId="other",LocalPublished=null};
            inserted.Clear();
            try { await commits.CommitKeepBothAsync(db,"p",null,both?other:copy,both?copy:null,notice,default); } catch(NotImplementedException) { }
            if (!inserted.TryGetValue(tag,out var seen) || seen!=(31L,91L) || both && (!inserted.TryGetValue("other",out var unproven) || unproven!=(-1L,-1L)))
                failures.Add("Confirmed keep-both publication lost or assigned to the wrong copy: " + tag);
        }
        await ProbeCopy("single",false); await ProbeCopy("double",true);
        var root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        var source=File.ReadAllText(Path.Combine(root,"src","SyncShark.Engine","Storage","Committer.cs"));
        if(source.Contains("|| '0'",StringComparison.Ordinal)) failures.Add("#7 duplicate descendant range rule");
        // A stationary side must supply null; assert the exact moved-side key/range expressions,
        // not just that their text appears somewhere, so an unconditional fallback is rejected.
        var project=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="ProjectSubtreeAsync");
        string? Init(string name)=>project.DescendantNodes().OfType<VariableDeclaratorSyntax>().SingleOrDefault(v=>v.Identifier.ValueText==name)?.Initializer?.Value.ToString();
        foreach(var (name,expected) in new[]{
            ("localKey","localMoved ? PathNames.Key(from.LocalPath) : null"),
            ("remoteKey","remoteMoved ? PathNames.DropboxKey(from.DropboxPath) : null"),
            ("local","localKey == null ? default : PathNames.DescendantBounds(localKey)"),
            ("remote","remoteKey == null ? default : PathNames.DescendantBounds(remoteKey)"),
            ("destKey","remoteMoved ? PathNames.DropboxKey(to.DropboxPath) : null")})
            if(Init(name)!=expected) failures.Add("Projection computes a stationary key or range: " + name + " = " + Init(name));
        var rebase=project.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(c=>c.Expression.ToString()=="StorageCommand.Create" && c.ArgumentList.Arguments.Count>3 && c.ArgumentList.Arguments[2].ToString()=="RebaseDescendantsBoth");
        var args=rebase.ArgumentList.Arguments.Skip(3).Select(a=>a.Expression).OfType<TupleExpressionSyntax>().ToDictionary(t=>t.Arguments[0].Expression.ToString().Trim('"'),t=>t.Arguments[1].Expression.ToString());
        foreach(var (name,expected) in new[]{("$newLocalKey","localMoved ? PathNames.Key(to.LocalPath) : null"),("$newDropboxKey","destKey"),("$oldLocalKey","localKey"),("$oldDropboxKey","remoteKey")})
            if(!args.TryGetValue(name,out var got) || got!=expected) failures.Add("Projection binds a stationary key: " + name + " = " + got);
        // The indexed collision lookup must run only for a moved Dropbox destination (destKey null otherwise).
        var clash=project.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(c=>c.ToString().Contains("ClashInDropboxSubtree",StringComparison.Ordinal));
        if(clash.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault()?.Condition.ToString()!="destKey != null")
            failures.Add("Dropbox collision query is not controlled by the moved destination key");
        if(failures.Count!=0) throw new InvalidDataException(string.Join("; ",failures));
    }
}
