using Massdex.SyncShark.Engine.Storage;
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
        const int window=2;
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
        var called=0;
        var post=Admit(null,null,"POST",()=>{ ++called; throw new InvalidDataException(); });
        var extra=Enumerable.Range(0,window*3).Select(i=>Admit(null,null,"N"+i)).ToArray();
        lock(pending) if (((System.Collections.ICollection)pending).Count>window) failures.Add("#3 blocked writer retained more than its batch window");
        gate.Release();
        try { await bad; failures.Add("#4 failed second write was reported durable"); } catch(SqliteException e) when(e.SqliteErrorCode==19) { }
        try { await post; failures.Add("#4 lost post-commit callback failure"); } catch(InvalidDataException) { }
        await good; await Task.WhenAll(extra);
        if (called!=1) failures.Add("#4 post-commit error replayed a committed operation");
        sql.CommandText="SELECT local_path,dropbox_key_delta,dropbox_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash,local_published_journal,local_published_usn FROM synced_entry";
        using(var rd=sql.ExecuteReader()) if(!rd.Read() || Massdex.SyncShark.Engine.Storage.Encoding.ReadSynced(rd,"LEFT/Z",0)!=saved || rd.Read()) failures.Add("#4 complete original binding was not restored");
        sql.CommandText="SELECT count(*) FROM discovery_deleted_delta WHERE path_key='BAD'";
        if((long)sql.ExecuteScalar()! !=0 || partial==0) failures.Add("#4 rollback not proven after a real binding write");
        sql.CommandText="SELECT count(*) FROM discovery_deleted_delta WHERE path_key='GOOD' AND spent=1";
        if((long)sql.ExecuteScalar()! !=1) failures.Add("#4 independent neighbour did not settle");
        var root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        var source=File.ReadAllText(Path.Combine(root,"src","SyncShark.Engine","Storage","Committer.cs"));
        if(source.Contains("|| '0'",StringComparison.Ordinal)) failures.Add("#7 duplicate descendant range rule");
        if(failures.Count!=0) throw new InvalidDataException(string.Join("; ",failures));
    }
}
