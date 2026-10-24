using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static SqliteConnection PriorityConnection(SqliteStateStore store) =>
        (SqliteConnection)typeof(SqliteStateStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static IReadOnlyList<SqliteStateStore.DeferredPlanning> PriorityRows(SqliteStateStore store, string pair) =>
        store.AdmitDeferredPlanning(pair, long.MaxValue, rotate: false).Where(row => row.Reason == SqliteStateStore.UserChangeReason).ToArray();

    private static void SeedPriorityBacklog(SqliteStateStore store, PairDef pair)
    {
        var connection = PriorityConnection(store);
        StoreSchema.Execute(connection, null, """
            WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<5000)
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
            SELECT (SELECT root_no FROM root WHERE root_id=$root),(SELECT dir_no FROM dir WHERE path=''),
              printf('bulk-%05d',i),printf('bulk-%05d',i),0,'bulk:'||i,1,$mtime,0 FROM n;
            WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<50000)
            INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version)
            SELECT (SELECT root_no FROM root WHERE root_id=$root),(CASE WHEN i<=5000 THEN 'bulk:' ELSE 'debt:' END)||i,
              (SELECT pair_no FROM pair WHERE pair_id=$pair),printf('bulk-%05d',i),i FROM n;
            INSERT OR IGNORE INTO planning_deferred(root_no,object_id,pair_no,path,version)
            SELECT f.root_no,f.object_id,(SELECT pair_no FROM pair WHERE pair_id=$pair),f.name,1
              FROM file_state f WHERE f.root_no=(SELECT root_no FROM root WHERE root_id=$root);
            """, ("$root", pair.A.RootId), ("$pair", pair.PairId), ("$mtime", DateTimeOffset.UtcNow.UtcTicks));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(pair.A.RootId, PriorityCheckpoint.Cursor, 100, false);
        var epoch = gaps.GapEpoch(pair.A.RootId)!;
        foreach (var page in store.Live(pair.A.RootId).Chunk(128))
            gaps.GapDiscoveredBatch(pair.A.RootId, epoch, page.Select(item => (item, true)).ToArray());
    }

    private static void AssertUrgentFailure(SqliteStateStore store, string root, string path)
    {
        var item = store.Live(root, [path]).Single();
        var connection = PriorityConnection(store);
        var failed = Convert.ToInt64(StoreSchema.Scalar(connection, null,
            "SELECT COUNT(*) FROM local_gap_item g JOIN root r ON r.root_no=g.root_no WHERE r.root_id=$root AND g.object_id=$id " +
            "AND g.last_attempt>0 AND g.failure_count=1 AND g.retry_after>$now;",
            ("$root", root), ("$id", item.ObjectId), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        Require(failed == 1, "failed urgent attempt retains positive order and the shared retry deadline");
        Require(store.OpenNotices("p1").Count(notice => notice.ReasonKey.StartsWith(SqliteStateStore.IndexFailurePrefix, StringComparison.Ordinal)) == 1,
            "failed urgent hash has exactly one existing-owner notice");
    }

    private static void AssertPriorityQueryPlans(SqliteStateStore store)
    {
        var connection = PriorityConnection(store);
        CheckPriorityPlan(connection, SqliteStateStore.UrgentGapSql,
            [("$r", 1L), ("$e", "epoch"), ("$now", long.MaxValue), ("$user", SqliteStateStore.UserChangeReason),
                ("$candidates", SqliteStateStore.UrgentCandidateCap), ("$limit", SqliteStateStore.UrgentHashCap)], "planning_deferred_user_rv");
        CheckPriorityPlan(connection, SqliteStateStore.UrgentPlanningIntakeSql,
            [("$p", 1L), ("$before", long.MaxValue), ("$user", SqliteStateStore.UserChangeReason), ("$limit", 256)], "planning_deferred_user_pv");
        Require(SqliteStateStore.UrgentCandidateCap == 1024 &&
            SqliteStateStore.UrgentGapSql.IndexOf("LIMIT $candidates", StringComparison.Ordinal) <
            SqliteStateStore.UrgentGapSql.IndexOf("CROSS JOIN", StringComparison.Ordinal),
            "candidate LIMIT bounds input to DISTINCT and the gap join, not only returned matches");
    }

    private static void CheckPriorityPlan(SqliteConnection connection, string sql, (string, object)[] parameters, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Require(plan.Any(line => line.Contains(index, StringComparison.Ordinal)) &&
            !plan.Any(line => line.Contains("B-TREE FOR ORDER BY", StringComparison.Ordinal)),
            "urgent page uses its ordered partial index without sorting the population");
    }

    private static void AssertPriorityRollback(SqliteStateStore store)
    {
        var connection = PriorityConnection(store);
        var legacy = ((string[])typeof(StoreSchema).GetField("ExpectedNames", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
            .Where(name => name is not ("planning_deferred_user_pv" or "planning_deferred_user_rv")).ToHashSet(StringComparer.Ordinal);
        StoreSchema.Execute(connection, null, "DROP INDEX IF EXISTS planning_deferred_user_pv; DROP INDEX IF EXISTS planning_deferred_user_rv;");
        using var command = StoreSchema.Command(connection, null,
            "SELECT name FROM sqlite_master WHERE type IN ('table','index') AND name NOT LIKE 'sqlite_%';");
        using var reader = command.ExecuteReader();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) actual.Add(reader.GetString(0));
        actual.Remove("gap_retry");
        Require(actual.SetEquals(legacy), "after rollback drops, the previous exact-name schema check accepts the store");
    }
}
