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
            (Task)queue.Invoke(commits,[db,gate,window,"p",prior,next,null,key,null,notify ?? (()=>{}),CancellationToken.None])!;
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
        // A keep flag on a bound row survives a relink and a keep-both replacement of its surviving identity (check-1 witness).
        var kept = new SyncedEntry("KEEP","keep","KEEP","keep",false,"pcK",1,1,"dbK","opaque");
        await commits.CommitAsync(db,"p",null,kept,null,null,default);
        sql.CommandText="UPDATE synced_entry SET keep='[\"\"]' WHERE local_path='keep'"; sql.ExecuteNonQuery();
        var movedKeep = kept with { LocalPath="k2", LocalKey="K2" };
        await commits.CommitAsync(db,"p",kept,movedKeep,null,null,default);
        sql.CommandText="SELECT keep FROM synced_entry WHERE local_path='k2'";
        if(!Equals(sql.ExecuteScalar() as string,"[\"\"]")) failures.Add("A relink dropped the keep flag");
        var sibling = new SyncedEntry("SIB","sib","SIB","sib",false,"pcS",1,1,"dbS","opaque");
        await commits.CommitAsync(db,"p",null,sibling,null,null,default);
        sql.CommandText="UPDATE synced_entry SET keep='[\"u/x\"]' WHERE local_path='sib'"; sql.ExecuteNonQuery();
        var copyKeep = movedKeep with { DropboxId="dbK2", DropboxPath="k3", DropboxKey="K3" };
        await commits.CommitKeepBothAsync(db,"p",movedKeep,copyKeep,null,new(0,NoticeKind.Info,TextKey.InfoKeptBoth,"k3",new([],null),DateTimeOffset.UnixEpoch,false),default);
        sql.CommandText="SELECT keep FROM synced_entry WHERE local_id='pcK'";
        if(!Equals(sql.ExecuteScalar() as string,"[\"\"]")) failures.Add("Keep-both dropped the keep flag of the surviving identity");
        sql.CommandText="SELECT keep FROM synced_entry WHERE local_path='sib'";
        if(!Equals(sql.ExecuteScalar() as string,"[\"u/x\"]")) failures.Add("Keep-both disturbed a sibling's keep flag");
        // a confirmed fresh folder link retires only an undated (timeless) spend at its own Dropbox key.
        sql.CommandText="INSERT INTO pair(pair_id,def_json) VALUES('fold','{}'); INSERT INTO pair(pair_id,def_json) VALUES('foldother','{}');";
        sql.ExecuteNonQuery();
        var datedTicks=DateTimeOffset.UnixEpoch.AddDays(3).UtcTicks;
        void SeedSpend(string pair,string key,bool spent,long? ticks)
        {
            sql.CommandText="INSERT OR REPLACE INTO discovery_deleted_delta(pair_id,path_key,spent,deleted_ticks) VALUES((SELECT id FROM pair WHERE pair_id=$p),$k,$s,$t)";
            sql.Parameters.Clear(); sql.Parameters.AddWithValue("$p",pair); sql.Parameters.AddWithValue("$k",key);
            sql.Parameters.AddWithValue("$s",spent?1:0); sql.Parameters.AddWithValue("$t",(object?)ticks ?? DBNull.Value);
            sql.ExecuteNonQuery(); sql.Parameters.Clear();
        }
        long RowCount(string pair,string key)
        {
            sql.CommandText="SELECT count(*) FROM discovery_deleted_delta WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND path_key=$k";
            sql.Parameters.Clear(); sql.Parameters.AddWithValue("$p",pair); sql.Parameters.AddWithValue("$k",key);
            var n=(long)sql.ExecuteScalar()!; sql.Parameters.Clear(); return n;
        }
        long LinkCount(string pair,string localPath)
        {
            sql.CommandText="SELECT count(*) FROM synced_entry WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND local_path=$path";
            sql.Parameters.Clear(); sql.Parameters.AddWithValue("$p",pair); sql.Parameters.AddWithValue("$path",localPath);
            var n=(long)sql.ExecuteScalar()!; sql.Parameters.Clear(); return n;
        }
        long? DatedValue(string pair,string key)
        {
            sql.CommandText="SELECT deleted_ticks FROM discovery_deleted_delta WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND path_key=$k";
            sql.Parameters.Clear(); sql.Parameters.AddWithValue("$p",pair); sql.Parameters.AddWithValue("$k",key);
            var v=sql.ExecuteScalar(); sql.Parameters.Clear(); return v is null || v is DBNull ? null : Convert.ToInt64(v);
        }
        static SyncedEntry Folder(string local,string dropbox,string id)=>new(PathNames.Key(local),local,PathNames.DropboxKey(dropbox),dropbox,true,id,0,0,id+"r","");
        static SyncedEntry FileLink(string local,string dropbox,string id)=>new(PathNames.Key(local),local,PathNames.DropboxKey(dropbox),dropbox,false,id,1,1,id+"r","hash");
        var keepNotice=new Notice(0,NoticeKind.Info,TextKey.InfoKeptBoth,"copy",new([],null),DateTimeOffset.UnixEpoch,false);
        // S1 fail-first: a timeless spend at a fresh folder's key is gone after the normal binding write.
        SeedSpend("fold","SP/X",true,null);
        await commits.CommitAsync(db,"fold",null,Folder("sp/x","SP/X","s1"),null,null,default);
        if (RowCount("fold","SP/X")!=0) failures.Add("S1: a fresh folder binding left its timeless spend");
        // S2: a dated file-deletion marker at the folder's key survives with its exact server time.
        SeedSpend("fold","SD/1",true,datedTicks);
        await commits.CommitAsync(db,"fold",null,Folder("sd/1","SD/1","s2"),null,null,default);
        if (RowCount("fold","SD/1")!=1 || DatedValue("fold","SD/1")!=datedTicks) failures.Add("S2: a fresh folder binding erased a dated file deletion");
        // S3: each Keep Both folder link clears its own key; a file link's key is untouched (folder in either position).
        SeedSpend("fold","KB/NEW",true,null); SeedSpend("fold","KB/OLDFILE",true,null);
        await commits.CommitKeepBothAsync(db,"fold",null,FileLink("kb/api-file","KB/OLDFILE","s3a"),Folder("kb/api","KB/NEW","s3b"),keepNotice,default);
        SeedSpend("fold","KB/NEW2",true,null); SeedSpend("fold","KB/OLDFILE2",true,null);
        await commits.CommitKeepBothAsync(db,"fold",null,Folder("kb/api2","KB/NEW2","s3c"),FileLink("kb/api2-file","KB/OLDFILE2","s3d"),keepNotice,default);
        if (RowCount("fold","KB/NEW")!=0 || RowCount("fold","KB/NEW2")!=0) failures.Add("S3: a Keep Both folder link left its timeless spend");
        if (RowCount("fold","KB/OLDFILE")!=1 || RowCount("fold","KB/OLDFILE2")!=1) failures.Add("S3: a Keep Both file link cleared a spend");
        // S4: an update (replacing) and a projection onto a spent key keep the marker.
        var upRow=Folder("up/one","UP/ONE","up");
        await commits.CommitAsync(db,"fold",null,upRow,null,null,default);
        SeedSpend("fold","UP/ONE",true,null);
        await commits.CommitAsync(db,"fold",upRow,upRow with { LocalModifiedTicks=7 },null,null,default);
        await commits.CommitAsync(db,"fold",null,Folder("prj/old","prj/old","pj"),null,null,default);
        await commits.CommitAsync(db,"fold",null,Folder("prj/old/c","prj/old/c","pjc"),null,null,default);
        SeedSpend("fold","PRJ/NEW/C",true,null);
        await commits.ProjectSubtreeAsync(db,"fold",new("prj/old","prj/old"),new("prj/new","prj/new"),default);
        if (RowCount("fold","UP/ONE")!=1) failures.Add("S4: an update cleared a spend");
        if (RowCount("fold","PRJ/NEW/C")!=1) failures.Add("S4: a projection cleared a spend");
        if (LinkCount("fold","prj/new/c")!=1 || LinkCount("fold","prj/old/c")!=0) failures.Add("S4: the projected folder child did not move to its destination");
        // S5: an unspent row, another key and another pair stay; a failed insert keeps the marker.
        SeedSpend("fold","UNSPENT",false,null);
        await commits.CommitAsync(db,"fold",null,Folder("unspent","UNSPENT","u1"),null,null,default);
        SeedSpend("fold","OTHER",true,null);
        await commits.CommitAsync(db,"fold",null,Folder("tgt","TGT","u2"),null,null,default);
        SeedSpend("foldother","SHARED",true,null);
        await commits.CommitAsync(db,"fold",null,Folder("shared","SHARED","u3"),null,null,default);
        await commits.CommitAsync(db,"fold",null,Folder("fFa","fFb","u4"),null,null,default);
        SeedSpend("fold","FFB",true,null);
        bool blocked=false;
        try { await commits.CommitAsync(db,"fold",null,Folder("fFc","fFb","u5"),null,null,default); } catch(ChangedUnderneathException) { blocked=true; }
        if (RowCount("fold","UNSPENT")!=1) failures.Add("S5: an unspent row was cleared");
        if (RowCount("fold","OTHER")!=1) failures.Add("S5: another key was cleared");
        if (RowCount("foldother","SHARED")!=1) failures.Add("S5: another pair's key was cleared");
        if (!blocked) failures.Add("S5: the conflicting fresh folder insert was not blocked");
        if (RowCount("fold","FFB")!=1) failures.Add("S5: a failed insert transaction cleared the marker");
        // S3b: Keep Both over an existing binding still retires the folder's own key (the guard must not be `before == null`).
        var prior=Folder("kb/prior","KB/PRIOR","kbp");
        await commits.CommitAsync(db,"fold",null,prior,null,null,default);
        SeedSpend("fold","KB/NEW3",true,null);
        await commits.CommitKeepBothAsync(db,"fold",prior,Folder("kb/api3","KB/NEW3","kbf3"),null,keepNotice,default);
        if (RowCount("fold","KB/NEW3")!=0) failures.Add("S3b: Keep Both over a prior binding left the folder's timeless spend");
        // S6: a failed second Keep Both leg rolls back the first leg's expiry and its link.
        await commits.CommitAsync(db,"fold",null,Folder("rb/pre","RB/PRE","rbp"),null,null,default);
        SeedSpend("fold","RB/NEW",true,null);
        bool rolledBack=false;
        try { await commits.CommitKeepBothAsync(db,"fold",null,Folder("rb/api","RB/NEW","rbf"),Folder("rb/x","RB/PRE","rbs"),keepNotice,default); }
        catch(ChangedUnderneathException) { rolledBack=true; }
        if (!rolledBack) failures.Add("S6: the colliding second Keep Both leg was not rejected");
        if (RowCount("fold","RB/NEW")!=1) failures.Add("S6: a rolled-back transaction cleared the first leg's marker");
        if (LinkCount("fold","rb/api")!=0) failures.Add("S6: a rolled-back transaction kept the first Keep Both link");
        // S7: the folder-spend DELETE is planned as a primary-key seek, never a table scan.
        sql.CommandText="EXPLAIN QUERY PLAN DELETE FROM discovery_deleted_delta WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND path_key=$k AND spent=1 AND deleted_ticks IS NULL";
        sql.Parameters.Clear(); sql.Parameters.AddWithValue("$p","fold"); sql.Parameters.AddWithValue("$k","SP/X");
        var planSteps=new List<string>();
        using(var plan=sql.ExecuteReader()){ int detail=plan.GetOrdinal("detail"); while(plan.Read()) planSteps.Add(plan.GetString(detail)); }
        sql.Parameters.Clear();
        if(!planSteps.Any(step=>step.Contains("discovery_deleted_delta",StringComparison.OrdinalIgnoreCase) && step.Contains("PRIMARY KEY",StringComparison.OrdinalIgnoreCase)) || planSteps.Any(step=>step.TrimStart().StartsWith("SCAN discovery_deleted_delta",StringComparison.OrdinalIgnoreCase)))
            failures.Add("S7: the folder-spend DELETE is not planned as a primary-key seek: " + string.Join("; ",planSteps));
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
