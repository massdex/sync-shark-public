using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private const int RehashGateRows = 200_000;
    private static readonly TimeSpan RehashGateWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RehashDrainLimit = TimeSpan.FromMinutes(2);

    private static async Task RehashGateChecks(string root)
    {
        var folder = Fresh(root, "rehash-gate");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        SeedRehashGate(DbPath(folder));
        var matches = await DrainWithWalkWriter(store, null, 0);
        Require(matches.Count == RehashGateRows && matches.Select(item => item.ObjectId).SequenceEqual(
            matches.Select(item => item.ObjectId).Order(StringComparer.Ordinal)), "rehash drains all tied candidates in identity order");
        Require(matches.Select(item => item.ObjectId).Distinct().Count() == RehashGateRows, "rehash never repeats a rotated identity");
        var sparse = await DrainWithWalkWriter(store, false, 1);
        Require(sparse.Count == 0 && store.RehashPendingCount("ra") == RehashGateRows,
            "ineligible identities remain queued and sparse scans also yield the gate");
        Require(store.LocalWalkPageCount("ra") == 2, "concurrent walk writes both pages");
    }

    private static async Task<IReadOnlyList<ItemState>> DrainWithWalkWriter(SqliteStateStore store, bool? verification, int ordinal)
    {
        var gate = (FairStoreGate)typeof(SqliteStateStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var through = store.RehashWatermark("ra");
        Task<IReadOnlyList<ItemState>> draining;
        Task writer;
        using (gate.Enter())
        {
            draining = Task.Factory.StartNew(() => store.RehashCandidates("ra", RehashGateRows, through, verification),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 1, RehashGateWaitLimit), "rehash queued first");
            writer = Task.Factory.StartNew(() =>
            {
                var wait = Stopwatch.StartNew();
                using (gate.Enter())
                {
                    Require(wait.Elapsed < RehashGateWaitLimit, "maximum walk writer gate wait stays below five seconds");
                    Require(!draining.IsCompleted, "walk writer admitted before rehash drain completes");
                    if (verification is null) AssertOneRehashPageRotated(store, through);
                    store.SaveLocalWalkPages("ra", [(ordinal, "[]")]);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 2, RehashGateWaitLimit), "walk writer queued behind first rehash page");
        }
        try { await writer.WaitAsync(RehashGateWaitLimit); }
        finally { await draining.WaitAsync(RehashDrainLimit); }
        return draining.Result;
    }

    private static void AssertOneRehashPageRotated(SqliteStateStore store, long through)
    {
        var connection = (SqliteConnection)typeof(SqliteStateStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var rotated = Convert.ToInt64(StoreSchema.Scalar(connection, null,
            "SELECT COUNT(*) FROM local_rehash WHERE last_attempt>$through;", ("$through", through)));
        Require(rotated == Fast.StoreRehashPageRows, "FIFO admits walk writer after exactly one committed rehash page");
    }

    private static void SeedRehashGate(string db)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
        connection.Open();
        StoreSchema.Execute(connection, null, "INSERT OR IGNORE INTO dir(path,path_key) VALUES('','');");
        StoreSchema.Execute(connection, null, """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<$count)
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
            SELECT (SELECT root_no FROM root WHERE root_id='ra'),(SELECT dir_no FROM dir WHERE path=''),
              printf('%08d',n),printf('%08d',n),0,printf('%08d',n),1,$mtime,256 FROM numbers;
            INSERT INTO local_rehash(root_no,object_id,last_attempt,reason)
            SELECT root_no,object_id,1,'test' FROM file_state;
            """, ("$count", RehashGateRows), ("$mtime", T0.UtcTicks));
        foreach (var mode in new bool?[] { null, false, true })
        {
            var parameters = new List<(string, object?)> { ("$r", 1L), ("$through", long.MaxValue),
                ("$after", 0L), ("$id", string.Empty), ("$limit", 32) };
            if (mode is true) parameters.Add(("$now", 0L));
            using var command = StoreSchema.Command(connection, null, "EXPLAIN QUERY PLAN " + SqliteStateStore.RehashCandidatePageSql(mode));
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value!);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Require(plan.Any(line => line.Contains("rehash_retry")) && plan.Any(line => line.Contains("fs_obj")) &&
                !plan.Any(line => line.Contains("SCAN ") || line.Contains("TEMP B-TREE")), "rehash page uses ordered seeks and entity lookup indexes");
        }
    }
}
