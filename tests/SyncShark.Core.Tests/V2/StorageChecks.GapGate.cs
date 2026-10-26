using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private const int GapGateRows = 200_000;
    private const int GapGateAccepts = 256;
    private static readonly TimeSpan GapGateWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GapGateDrainLimit = TimeSpan.FromMinutes(2);
    private static readonly string GapGateHash = new('a', 64);

    private static async Task GapGateChecks(string root)
    {
        var folder = Fresh(root, "gap-gate");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        SeedGapGate(DbPath(folder));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap("ra", "journal", 1, true);
        var epoch = gaps.GapEpoch("ra")!;
        StageGapGate(DbPath(folder), epoch);
        var results = Enumerable.Range(GapGateRows - GapGateAccepts + 1, GapGateAccepts)
            .Select(n => GapGateResult(store, epoch, n)).ToArray();
        await AcceptWithForegroundReader(store, gaps, results);
        Require(store.RehashPendingCount("ra") == GapGateAccepts, "accepted identities retain durable planning obligations");
        Require(results.All(result => store.ByObject("ra", result.Expected.ObjectId)!.Hash == GapGateHash),
            "accepted identities publish hashes without dropping their planning handoff");
        GapAcceptRollbackChecks(store, gaps, DbPath(folder), GapGateResult(store, epoch, 1));
    }

    private static LocalGapResult GapGateResult(SqliteStateStore store, LocalGapEpoch epoch, int n) =>
        new(epoch.Epoch, store.ByObject("ra", n.ToString("D8"))!, GapGateHash,
            new("journal", new NtfsFileUsn(1, 1, 1, 1, 1)), n);

    private static async Task AcceptWithForegroundReader(SqliteStateStore store, ILocalGapStore gaps, LocalGapResult[] results)
    {
        var gate = (FairStoreGate)typeof(SqliteStateStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Task accepting;
        Task foreground;
        using (gate.Enter())
        {
            accepting = Task.Factory.StartNew(() =>
            {
                foreach (var result in results) Require(gaps.AcceptGapResult("ra", result), "staged gap result accepted");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 1, GapGateWaitLimit), "gap acceptance queued first");
            foreground = Task.Factory.StartNew(() =>
            {
                var wait = Stopwatch.StartNew();
                using (gate.Enter())
                {
                    Require(wait.Elapsed < GapGateWaitLimit, "foreground maximum gate wait below five seconds during gap acceptance");
                    Require(store.Pairs().Count == 1 && gaps.HasGapProtection("ra"), "foreground reads pair and protection during drain");
                    Require(store.RehashPendingCount("ra") == 1, "foreground admitted after exactly one atomic gap handoff");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 2, GapGateWaitLimit), "foreground queued behind gap acceptance");
        }
        try { await foreground.WaitAsync(GapGateWaitLimit); }
        finally { await accepting.WaitAsync(GapGateDrainLimit); }
    }

    private static void SeedGapGate(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        StoreSchema.Execute(db, null, "INSERT OR IGNORE INTO dir(path,path_key) VALUES('','');");
        StoreSchema.Execute(db, null, """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<$count)
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
            SELECT (SELECT root_no FROM root WHERE root_id='ra'),(SELECT dir_no FROM dir WHERE path=''),
              printf('%08d',n),printf('%08d',n),0,printf('%08d',n),1,$mtime,256 FROM numbers;
            """, ("$count", GapGateRows), ("$mtime", T0.UtcTicks));
        foreach (var sql in new[] { SqliteStateStore.GapAcceptMetadataSql, SqliteStateStore.GapAcceptPublishSql })
        {
            using var command = StoreSchema.Command(db, null, "EXPLAIN QUERY PLAN " + sql);
            foreach (var name in new[] { "$r", "$o", "$s", "$m", "$h" })
                if (sql.Contains(name, StringComparison.Ordinal)) command.Parameters.AddWithValue(name, 1);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Require(plan.Any(line => line.Contains("fs_obj (root_no=? AND object_id=?)")) &&
                !plan.Any(line => line.Contains("SCAN ") || line.Contains("PRIMARY KEY (root_no=?)")),
                "gap validation and publication seek the entity, never the entire root");
        }
    }

    private static void StageGapGate(string path, LocalGapEpoch epoch)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        StoreSchema.Execute(db, null, """
            INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json,hash,proof_json,result_seq)
            SELECT root_no,object_id,$epoch,'{}',$hash,'{}',CAST(object_id AS INTEGER) FROM file_state;
            UPDATE local_gap SET discovery_done=1;
            """, ("$epoch", epoch.Epoch), ("$hash", GapGateHash));
    }

    private static void GapAcceptRollbackChecks(SqliteStateStore store, ILocalGapStore gaps, string path, LocalGapResult result)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        StoreSchema.Execute(db, null, "CREATE TRIGGER fail_gap_accept BEFORE DELETE ON local_gap_item BEGIN SELECT RAISE(ABORT,'test acceptance failure'); END;");
        var failed = false;
        try { gaps.AcceptGapResult("ra", result); }
        catch (SqliteException) { failed = true; }
        finally { StoreSchema.Execute(db, null, "DROP TRIGGER fail_gap_accept;"); }
        Require(failed && store.ByObject("ra", result.Expected.ObjectId)!.Hash is null &&
            store.RehashPendingCount("ra") == GapGateAccepts, "failed gap retirement rolls back hash publication and rehash handoff atomically");
        Require(gaps.AcceptGapResult("ra", result), "failed atomic acceptance retains the staged proof for retry");
    }
}
