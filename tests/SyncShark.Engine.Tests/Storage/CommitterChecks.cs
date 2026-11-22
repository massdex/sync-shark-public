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
        try { await commits.CommitAsync(db,"p",saved,saved with { LocalKey="WRONG" },null,"BAD",default); }
        catch (InvalidDataException)
        {
            sql.CommandText="SELECT (SELECT count(*) FROM synced_entry WHERE local_path='left/z') - (SELECT count(*) FROM discovery_deleted_delta WHERE path_key='BAD')";
            if ((long)sql.ExecuteScalar()! == 1) return;
        }
        throw new InvalidDataException("Failed publication did not roll back.");
    }
}
