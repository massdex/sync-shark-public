using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string VerificationWaitReason = "Waiting for content verification";
    internal const string UserChangeReason = "Waiting for verification of a recent change";
    internal const int UrgentHashCap = 64;
    internal const int UrgentCandidateCap = 1024;
    internal const string UserChangeIndexes =
        "CREATE INDEX IF NOT EXISTS planning_deferred_user_pv ON planning_deferred(pair_no,version,root_no,object_id) WHERE reason='" + UserChangeReason + "';" +
        "CREATE INDEX IF NOT EXISTS planning_deferred_user_rv ON planning_deferred(root_no,version,object_id) WHERE reason='" + UserChangeReason + "';";

    // DISTINCT is bounded by the inner LIMIT, including roots shared by several pairs.
    internal static readonly string UrgentGapSql = """
        SELECT g.object_id FROM (
          SELECT DISTINCT object_id FROM (
            SELECT object_id FROM planning_deferred INDEXED BY planning_deferred_user_rv
            WHERE root_no=$r AND reason=$user ORDER BY version LIMIT $candidates
          )
        ) u CROSS JOIN local_gap_item g
        WHERE g.root_no=$r AND g.object_id=u.object_id AND g.epoch=$e
          AND g.hash IS NULL AND g.retry_allowed=1 AND g.metadata_ready=0 AND g.retry_after<=$now
        LIMIT $limit;
        """.Replace("$user", "'" + UserChangeReason + "'");

    private List<string> SelectGapWork(long root, LocalGapEpoch epoch, int limit, bool includeBulk,
        out HashSet<string> urgent)
    {
        var ids = new List<string>();
        using (var cmd = StoreSchema.Command(_connection, null, UrgentGapSql))
        {
            cmd.Parameters.AddWithValue("$r", root); cmd.Parameters.AddWithValue("$e", epoch.Epoch);
            cmd.Parameters.AddWithValue("$user", UserChangeReason);
            cmd.Parameters.AddWithValue("$candidates", UrgentCandidateCap);
            cmd.Parameters.AddWithValue("$limit", Math.Min(limit, UrgentHashCap));
            cmd.Parameters.AddWithValue("$now", NowMs());
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        urgent = ids.ToHashSet(StringComparer.Ordinal);
        if (!includeBulk || ids.Count == limit) return ids;
        using var bulk = StoreSchema.Command(_connection, null,
            "SELECT object_id FROM local_gap_item INDEXED BY gap_ready WHERE root_no=$r AND epoch=$e " +
            "AND hash IS NULL AND retry_allowed=1 AND metadata_ready=0 AND retry_after<=$now " +
            "AND object_id NOT IN (SELECT value FROM json_each($selected)) ORDER BY last_attempt,object_id LIMIT $n;");
        bulk.Parameters.AddWithValue("$r", root); bulk.Parameters.AddWithValue("$e", epoch.Epoch);
        bulk.Parameters.AddWithValue("$now", NowMs()); bulk.Parameters.AddWithValue("$n", limit - ids.Count);
        bulk.Parameters.AddWithValue("$selected", System.Text.Json.JsonSerializer.Serialize(ids));
        using var rows = bulk.ExecuteReader();
        while (rows.Read()) ids.Add(rows.GetString(0));
        return ids;
    }

    // A delta may precede the bulk discovery frontier. Add only an explicit hold;
    // never advance that frontier or replace already staged evidence.
    private void RetainUrgentGap(SqliteTransaction tx, string rootId, ItemState item)
    {
        if (item.Kind != ItemKind.File) return;
        StoreSchema.Execute(_connection, tx,
            "INSERT OR IGNORE INTO local_gap_item(root_no,object_id,epoch,expected_json) " +
            "SELECT g.root_no,$id,g.epoch,$item FROM local_gap g JOIN root r ON r.root_no=g.root_no " +
            "WHERE r.root_id=$root AND EXISTS(SELECT 1 FROM file_state f WHERE f.root_no=g.root_no AND f.object_id=$id);",
            ("$id", item.ObjectId), ("$root", rootId), ("$item", System.Text.Json.JsonSerializer.Serialize(item)));
    }

    internal static readonly string UrgentPlanningIntakeSql = """
        SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason,
          d.alias_dir,d.alias_key,d.alias_name,d.alias_unsettled
        FROM planning_deferred d INDEXED BY planning_deferred_user_pv
        JOIN root r ON r.root_no=d.root_no
        WHERE d.pair_no=$p AND d.reason=$user AND d.version<$before
        ORDER BY d.version,d.root_no,d.object_id LIMIT $limit;
        """.Replace("$user", "'" + UserChangeReason + "'");

    private const string BulkPlanningIntakeSql = """
        SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason,
          d.alias_dir,d.alias_key,d.alias_name,d.alias_unsettled
        FROM planning_deferred d INDEXED BY planning_deferred_pair_version
        JOIN root r ON r.root_no=d.root_no
        WHERE d.pair_no=$p AND d.version<$before AND d.reason<>$user
        ORDER BY d.version,d.root_no,d.object_id LIMIT $limit;
        """;

    private List<DeferredPlanning> ReadPlanningIntake(SqliteTransaction tx, long pair, long before)
    {
        var rows = new List<DeferredPlanning>();
        ReadPlanningIntakePage(tx, pair, before, UrgentPlanningIntakeSql, rows);
        if (rows.Count < DeferredIntakeEntityCap)
            ReadPlanningIntakePage(tx, pair, before,
                BulkPlanningIntakeSql, rows);
        return rows;
    }

    private void ReadPlanningIntakePage(SqliteTransaction tx, long pair, long before, string sql,
        List<DeferredPlanning> rows)
    {
        using var command = StoreSchema.Command(_connection, tx, sql);
        command.Parameters.AddWithValue("$p", pair);
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$limit", DeferredIntakeEntityCap - rows.Count);
        command.Parameters.AddWithValue("$user", UserChangeReason);
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), [], true, reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9) != 0));
    }

    public long UrgentPending(string pairId) => InStore(() => Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        "SELECT COUNT(*) FROM planning_deferred INDEXED BY planning_deferred_user_pv WHERE pair_no=$p AND reason='" + UserChangeReason + "';",
        ("$p", PairNo(pairId)), ("$user", UserChangeReason))));
}
