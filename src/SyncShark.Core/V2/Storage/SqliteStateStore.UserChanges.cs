using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string VerificationWaitReason = "Waiting for content verification";
    // Persisted in partial-index predicates: changing this literal requires an index migration.
    internal const string UserChangeReason = "Waiting for verification of a recent change";
    internal const int UrgentHashCap = VerificationPolicy.UrgentHashCap;
    internal const int UrgentCandidateCap = 1024;
    internal const string UserChangeIndexes =
        "CREATE INDEX IF NOT EXISTS planning_deferred_user_pv ON planning_deferred(pair_no,version,root_no,object_id) WHERE reason='" + UserChangeReason + "';" +
        "CREATE INDEX IF NOT EXISTS planning_deferred_user_rv ON planning_deferred(root_no,version,object_id) WHERE reason='" + UserChangeReason + "';";

    // Grouping and the outer sort are bounded by the indexed candidate LIMIT, even across pairs.
    internal static readonly string UrgentGapSql = """
        SELECT g.object_id FROM (
          SELECT object_id, MIN(version) AS oldest FROM (
            SELECT object_id,version FROM planning_deferred INDEXED BY planning_deferred_user_rv
            WHERE root_no=$r AND reason=$user ORDER BY version LIMIT $candidates
          ) GROUP BY object_id
        ) u CROSS JOIN local_gap_item g
        WHERE g.root_no=$r AND g.object_id=u.object_id AND g.epoch=$e
          AND g.hash IS NULL AND g.retry_allowed=1 AND g.metadata_ready=0 AND g.retry_after<=$now
        ORDER BY u.oldest,u.object_id LIMIT $limit;
        """.Replace("$user", "'" + UserChangeReason + "'");

    private List<string> SelectGapWork(long root, LocalGapEpoch epoch, int limit, bool includeBulk,
        out HashSet<string> urgent)
    {
        var ids = new List<string>();
        using (var cmd = StoreSchema.Command(_connection, null, UrgentGapSql))
        {
            cmd.Parameters.AddWithValue("$r", root); cmd.Parameters.AddWithValue("$e", epoch.Epoch);
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

    private List<DeferredPlanning> ReadPlanningIntake(SqliteTransaction tx, long pair, long before, bool userOnly)
    {
        var admitted = new List<DeferredPlanning>();
        AddPage(UrgentPlanningIntakeSql, DeferredIntakeEntityCap / 2, DeferredIntakePathCap / 2);
        if (!userOnly) AddPage(BulkPlanningIntakeSql, DeferredIntakeEntityCap - admitted.Count,
            DeferredIntakePathCap - admitted.Sum(row => row.Paths.Count));
        return admitted;

        void AddPage(string sql, int limit, int paths)
        {
            var rows = ReadPlanningIntakePage(tx, pair, before, sql, limit);
            foreach (var row in rows)
            {
                if (paths <= 0) break;
                var materialized = MaterializeAliases(tx, row, paths);
                paths -= materialized.Paths.Count;
                admitted.Add(materialized);
            }
        }
    }

    private List<DeferredPlanning> ReadPlanningIntakePage(SqliteTransaction tx, long pair, long before, string sql, int limit)
    {
        var rows = new List<DeferredPlanning>();
        using var command = StoreSchema.Command(_connection, tx, sql);
        command.Parameters.AddWithValue("$p", pair);
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$limit", limit);
        if (sql == BulkPlanningIntakeSql) command.Parameters.AddWithValue("$user", UserChangeReason);
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), [], true, reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9) != 0));
        return rows;
    }

    private static string UrgentRehashSql => $"""
        WITH urgent AS MATERIALIZED (
          SELECT object_id FROM planning_deferred INDEXED BY planning_deferred_user_rv
          WHERE root_no=$r AND reason='{UserChangeReason}' ORDER BY version LIMIT $candidates
        )
        SELECT DISTINCT q.object_id FROM urgent u CROSS JOIN local_rehash q
        WHERE q.root_no=$r AND q.object_id=u.object_id AND q.last_attempt<=$through AND q.retry_after<=$now
          AND {NotGapOwnedSql} AND EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_obj
            WHERE f.root_no=q.root_no AND f.object_id=q.object_id
              AND {VerificationNeededSql} AND {VerificationEligibleSql}) LIMIT $limit;
        """;

    public bool IsUserChange(string rootId, string objectId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM planning_deferred WHERE root_no=(SELECT root_no FROM root WHERE root_id=$r) " +
            "AND object_id=$o AND reason=$reason LIMIT 1;",
            ("$r", rootId), ("$o", objectId), ("$reason", UserChangeReason)) is not null);

    public IReadOnlyList<ItemState> UrgentRehashCandidates(string rootId, int limit, long through) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || limit <= 0) return Array.Empty<ItemState>();
        using var tx = _connection.BeginTransaction();
        using var cmd = StoreSchema.Command(_connection, tx, UrgentRehashSql);
        cmd.Parameters.AddWithValue("$r", root.Value);
        cmd.Parameters.AddWithValue("$through", through);
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.Parameters.AddWithValue("$candidates", UrgentCandidateCap);
        cmd.Parameters.AddWithValue("$limit", Math.Min(limit, UrgentHashCap));
        var ids = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        var items = new List<ItemState>();
        foreach (var id in ids)
        {
            StoreSchema.Execute(_connection, tx,
                "UPDATE local_rehash SET last_attempt=$order WHERE root_no=$r AND object_id=$o;",
                ("$r", root.Value), ("$o", id), ("$order", NextRehashOrder(root.Value, tx, through)));
            items.Add(_reads.ByObject(root.Value, id, tx)!);
        }
        tx.Commit();
        return items.ToArray();
    });

    public long UrgentPending(string pairId) => InStore(() => Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        "SELECT COUNT(*) FROM planning_deferred INDEXED BY planning_deferred_user_pv WHERE pair_no=$p AND reason='" + UserChangeReason + "';",
        ("$p", PairNo(pairId)))));
}
