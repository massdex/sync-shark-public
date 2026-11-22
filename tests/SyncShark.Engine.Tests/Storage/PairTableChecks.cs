using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks publication against the actual SQLite transaction, not a store double.</summary>
public static class PairTableChecks
{
    internal static SqliteConnection Open()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        db.CreateFunction<string,string>("pathkey", PathNames.Key, isDeterministic: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;" + Schema.Create + "INSERT INTO pair(pair_id,def_json) VALUES('p','{}');";
        cmd.ExecuteNonQuery();
        return db;
    }

    /// <summary>Neither stale revision may publish first Repair; only the matched pair of revisions may publish it.</summary>
    public static async Task RunAsync()
    {
        using var db = Open();
        var table = new PairTable();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO source_state(pair_id,side,repair_revision,repair_reason) VALUES(1,0,3,'local'),(1,1,5,'cloud');";
        cmd.ExecuteNonQuery();
        foreach (var stale in new[] { (2L,5L), (3L,4L) })
        {
            if (await table.FinishRepairAsync(tx,"p",stale,"{}",default)) throw new InvalidOperationException("Stale Repair reported success");
            cmd.CommandText = "SELECT first_repair_done=0 AND last_repair_json IS NULL AND (SELECT count(repair_reason) FROM source_state)=2 FROM pair";
            if ((long)cmd.ExecuteScalar()! != 1) throw new InvalidOperationException("Stale Repair published or cleared debt");
        }
        if (!await table.FinishRepairAsync(tx,"p",(3L,5L),"{}",default)) throw new InvalidOperationException("Matched Repair was refused");
        cmd.CommandText = "SELECT first_repair_done=1 AND last_repair_json='{}' AND (SELECT count(repair_reason) FROM source_state)=0 FROM pair";
        if ((long)cmd.ExecuteScalar()! != 1) throw new InvalidOperationException("Matched Repair did not publish and settle together");
        cmd.CommandText = "INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks,ignored) VALUES(1,$kind,'FailChanged','x','{}',0,1)";
        cmd.Parameters.AddWithValue("$kind",(int)NoticeKind.PutBack);
        cmd.ExecuteNonQuery();
        if (await table.PublishAsync(tx,"p",null,[],default) != TextKey.PairPutBackOutstanding) throw new InvalidOperationException("Ignored, uncommitted PutBack did not protect its owner");
        tx.Commit();
    }
}
