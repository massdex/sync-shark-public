using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static void SettleWindowMillion()
    {
        var folder = FixtureFolder("settle-million");
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(folder)};Pooling=False");
        connection.Open();
        SeedSettleMillion(connection, fx.Pair);
        AssertSettleQueryPlan(connection);
        AssertSettleCursorPersistence(store, connection, folder, fx.Pair);
        var total = 0;
        var eligible = 0;
        var maximumMs = 0d;
        var maximumBytes = 0L;
        for (var page = 0; total < 1000000; page++)
        {
            var bytes = GC.GetTotalAllocatedBytes(true);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = store.SettlePlanningCandidates(fx.Pair.PairId, long.MaxValue, 8192, []);
            watch.Stop();
            maximumMs = Math.Max(maximumMs, watch.Elapsed.TotalMilliseconds);
            maximumBytes = Math.Max(maximumBytes, GC.GetTotalAllocatedBytes(true) - bytes);
            Require(store.SettleExaminedRows is > 0 and <= SqliteStateStore.SettleExaminationCap, "examined rows bounded at one million");
            if (total + store.SettleExaminedRows <= 500000) Require(rows.Count == 0, "ineligible prefix does not scan for a match");
            total += store.SettleExaminedRows;
            eligible += rows.Count;
            // In a dense region the reader stops at the candidate cap, so a run examines the 8,192
            // rows it admits instead of the whole 16,384-row window; draining the tail takes ~61 pages.
            Require(page < 100, "cursor makes forward progress through sparse eligibility");
        }
        Require(total == 1000000 && eligible > 0, "bounded windows reach eligible tail");
        store.Dispose();
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        Require(reopened.SettlePlanningCandidates(fx.Pair.PairId, long.MaxValue, 8192, []).Count == 0 &&
            reopened.SettleExaminedRows == SqliteStateStore.SettleExaminationCap, "durable cursor wraps after reopen");
        Console.WriteLine($"million settle examined={total} cap={SqliteStateStore.SettleExaminationCap} maxGateMs={maximumMs:F2} maxAllocatedBytes={maximumBytes} workingSetBytes={Environment.WorkingSet}");
    }

    private static void AssertSettleCursorPersistence(SqliteStateStore store, SqliteConnection connection,
        string folder, PairDef pair)
    {
        store.SettlePlanningCandidates(pair.PairId, long.MaxValue, 8192, []);
        store.SavePair(pair with { Name = "Pair A" });
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        reopened.SettlePlanningCandidates(pair.PairId, long.MaxValue, 8192, []);
        var cursor = StoreSchema.Scalar(connection, null,
            "SELECT json_extract(def_json,'$.PlanningSettleCursor.ObjectId') FROM pair;") as string;
        Require(cursor == "entity-0032767", "reopened owner continues after examined prefix and pair save preserves position");
        reopened.SettlePlanningCandidates(pair.PairId, long.MaxValue, 8192, [], advance: false);
        Require((string?)StoreSchema.Scalar(connection, null,
            "SELECT json_extract(def_json,'$.PlanningSettleCursor.ObjectId') FROM pair;") == cursor,
            "read-only planning does not advance scheduling state");
        StoreSchema.Execute(connection, null, "UPDATE pair SET def_json=json_remove(def_json,'$.PlanningSettleCursor');");
    }

    private static void SeedSettleMillion(SqliteConnection connection, PairDef pair)
    {
        StoreSchema.Execute(connection, null, "INSERT OR IGNORE INTO dir(path,path_key) VALUES('','');");
        for (var start = 0; start < 1000000; start += 10000)
        {
            using var tx = connection.BeginTransaction();
            StoreSchema.Execute(connection, tx, """
                WITH RECURSIVE n(i) AS (SELECT $start UNION ALL SELECT i+1 FROM n WHERE i<$start+9999)
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,hash,size,mtime,flags)
                SELECT r.root_no,d.dir_no,printf('FILE%07d',i),printf('File%07d',i),0,
                  printf('entity-%07d',i),CASE WHEN i<500000 AND r.root_id=$b THEN x'02' ELSE x'01' END,1,1,0
                FROM n CROSS JOIN root r CROSS JOIN dir d WHERE d.path='' AND r.root_id IN ($a,$b);
                INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,reason)
                SELECT f.root_no,f.object_id,p.pair_no,f.name,1,$reason FROM file_state f
                JOIN root r ON r.root_no=f.root_no CROSS JOIN pair p
                WHERE r.root_id=$a AND f.object_id>=printf('entity-%07d',$start)
                  AND f.object_id<printf('entity-%07d',$start+10000);
                """, ("$start", start), ("$a", pair.A.RootId), ("$b", pair.B.RootId),
                ("$reason", SqliteStateStore.ObservationPlanningReason));
            tx.Commit();
        }
    }
}
