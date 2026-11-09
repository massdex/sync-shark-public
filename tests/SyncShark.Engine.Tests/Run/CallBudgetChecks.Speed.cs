using System.Diagnostics;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static partial class CallBudgetChecks
{
    /// <summary>Normal-use elapsed measurements; no SQL trace or special-operation checks.</summary>
    public static void RunSpeed()
    {
        if (Environment.GetEnvironmentVariable("SPEED_MODE") == "auto")
        {
            SpeedAutoAsync().GetAwaiter().GetResult();
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Console.WriteLine("SPEED fixture=" + folder);
        try
        {
            foreach (var shape in new[] { SmallShape, LargeShape, new Shape("100k", 97844, 196) })
            {
                var local = new FakeSide(Side.Local);
                var remote = new FakeSide(Side.Dropbox);
                Seed(local, remote, shape, changedTimes: 0);
                using var rig = NewRig(Path.Combine(folder, shape.Name), local, remote);
                SeedSavedAsync(rig, local, remote).GetAwaiter().GetResult();
                SpeedRepair(rig, shape, "prepared-first");
                SpeedRepair(rig, shape, "clean");
                ChangeLocal(local, shape, shape.Items / 100);
                SpeedRepair(rig, shape, "changed");
                foreach (var index in ChangedIndices(shape, 51))
                {
                    var path = PathNames.LocalSpelling(PathOf(index, shape));
                    var entry = local.ReadOneAsync(path, default).GetAwaiter().GetResult()!;
                    var bytes = System.Text.Encoding.UTF8.GetBytes("fixed-change-" + index);
                    local.Seed(entry with { Size = bytes.Length, Modified = entry.Modified.AddHours(2) }, bytes);
                }
                SpeedRepair(rig, shape, "fixed51");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    private static async Task SeedSavedAsync(Rig rig, FakeSide local, FakeSide remote)
    {
        var saved = new List<SyncedEntry>();
        await foreach (var listed in local.ReadAllAsync("", new ListOptions(true, false), default))
        {
            var entry = listed.Entry!;
            var counterpart = await remote.ReadOneAsync(entry.Path, default);
            saved.Add(Massdex.SyncShark.Engine.Executor.SyncedEntries.From(entry, counterpart!));
        }
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = saved });
    }

    private static void SpeedRepair(Rig rig, Shape shape, string label)
    {
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        var store = new SpeedStore(rig.Store);
        var scanRig = new ScanPassChecks.Rig(rig.Pair, store, rig.Local, rig.Dropbox,
            rig.Recovery, rig.Admission, rig.Scope);
        rig.Local.Reset();
        rig.Dropbox.Reset();
        using var log = RunLog.Open(rig.Folder, rig.Pair.PairId, label, _ => { }, Settings.LogBufferCharacters);
        var result = ScanPassChecks.RunRepairAsync(scanRig, log, Settings).GetAwaiter().GetResult();
        timer.Stop();
        Check.Require(result.Completed && !log.HasFailed, "speed Repair completes without failure");
        var expectedUploads = label == "changed" ? shape.Items / 100 : label == "fixed51" ? 51 : 0;
        Check.Require(rig.Dropbox.Publishes == expectedUploads, "speed workload performs its exact expected uploads");
        Console.WriteLine($"SPEED repair mode={label} items={shape.Items} elapsed_ms={timer.Elapsed.TotalMilliseconds:F3} cpu_ms={(process.TotalProcessorTime-cpu).TotalMilliseconds:F3} local_listing_and_store_ms={store.Listing[0]:F3} dropbox_listing_and_store_ms={store.Listing[1]:F3} scan_store_ms={store.WriteMs:F3} settle_ms={store.SettleMs:F3} compare_effects_other_ms={timer.Elapsed.TotalMilliseconds-store.Listing.Sum()-store.SettleMs:F3} rows_ms={store.RowsMs:F3} row_context_ms={store.ContextMs:F3} pending_commit_ms={store.CommitMs:F3} local_reads={rig.Local.Reads} remote_reads={rig.Dropbox.Reads} uploads={rig.Dropbox.Publishes}");
    }

    private sealed class SpeedStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal double[] Listing { get; } = new double[2];
        internal double WriteMs;
        internal double SettleMs;
        internal double RowsMs;
        internal double CommitMs;
        internal double ContextMs;
        private long start;
        public override IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
        {
            using var rows = base.ScanRows(pairId, scanId, row =>
            {
                var before = Stopwatch.GetTimestamp();
                try { return context(row); }
                finally { ContextMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds; }
            }).GetEnumerator();
            while (true)
            {
                var before = Stopwatch.GetTimestamp();
                var next = rows.MoveNext();
                RowsMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                if (!next) yield break;
                yield return rows.Current;
            }
        }
        public override void SettlePending(string pairId, PendingEffect expected, StateChange change,
            Action<Issue>? committedIssue = null)
        {
            var before = Stopwatch.GetTimestamp();
            try { base.SettlePending(pairId, expected, change, committedIssue); }
            finally { CommitMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds; }
        }
        public override void SettlePending(string pairId, PendingEffect expected, StateChange change,
            WorkSelection? selection, Action<Issue>? committedIssue)
        {
            var before = Stopwatch.GetTimestamp();
            try { base.SettlePending(pairId, expected, change, selection, committedIssue); }
            finally { CommitMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds; }
        }
        public override IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows)
        {
            var sink = base.OpenScan(pairId, scanId, side, batchRows);
            if (side == Side.Local) start = Stopwatch.GetTimestamp();
            return new SpeedSink(sink, this, side);
        }
        public override bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration,
            Guid scanToken, LocalCatchUp local, DropboxCatchUp dropbox)
        {
            var watch = Stopwatch.StartNew();
            var committed = base.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox);
            SettleMs += watch.Elapsed.TotalMilliseconds;
            return committed;
        }
        private sealed class SpeedSink(IScanSink inner, SpeedStore owner, Side side) : IScanSink
        {
            public async Task AddAsync(Listed item, CancellationToken ct)
            {
                var before = Stopwatch.GetTimestamp();
                await inner.AddAsync(item, ct);
                owner.WriteMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            public async Task CompleteAsync()
            {
                var before = Stopwatch.GetTimestamp();
                await inner.CompleteAsync();
                owner.WriteMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                owner.Listing[side == Side.Local ? 0 : 1] = Stopwatch.GetElapsedTime(owner.start).TotalMilliseconds;
                owner.start = Stopwatch.GetTimestamp();
            }
            public Task AbortAsync() => inner.AbortAsync();
        }
    }

    private static async Task SpeedAutoAsync()
    {
        foreach (var remote in new[] { false, true })
        {
            using var scenario = Scenario.Begin(remote ? "speed-remote" : "speed-local");
            const string path = "ordinary.txt";
            scenario.LocalFile(path, new string('a', 1024 * 1024));
            scenario.RepairAsync();
            scenario.Start();
            for (var repeat = 0; repeat < 20; repeat++)
            {
                var content = new string((char)('b' + repeat), 1024 * 1024);
                var completion = scenario.ObserveCompletion(path, content);
                var calls = scenario.Account.Calls.Count;
                var started = Stopwatch.GetTimestamp();
                if (remote) scenario.EditRemoteFile(path, content);
                else File.WriteAllText(Path.Combine(scenario.LocalRoot, path), content);
                var closed = Stopwatch.GetTimestamp();
                if (remote)
                    await scenario.Host.AcquireAsync(scenario.Pair.PairId,
                        (registration, pair, generation, ct) => scenario.Host.CatchUpDropboxAsync(
                            registration, pair.PairId, generation, ct), Scenario.Cancellation);
                await completion.WaitAsync(TimeSpan.FromMinutes(2));
                var finished = Stopwatch.GetTimestamp();
                Console.WriteLine($"SPEED auto remote={remote} repeat={repeat} from_close_ms={Stopwatch.GetElapsedTime(closed,finished).TotalMilliseconds:F3} including_write_ms={Stopwatch.GetElapsedTime(started,finished).TotalMilliseconds:F3} calls={string.Join(',', scenario.Account.Calls.Skip(calls).GroupBy(x=>x).Select(g=>g.Key+"="+g.Count()))}");
            }
            if (!remote)
            {
                var content = new string('z', 1024 * 1024);
                var paths = Enumerable.Range(0, 5).Select(n => "batch-" + n + ".txt").ToArray();
                var completions = paths.Select(name => scenario.ObserveCompletion(name, content)).ToArray();
                var calls = scenario.Account.Calls.Count;
                var started = Stopwatch.GetTimestamp();
                foreach (var name in paths) File.WriteAllText(Path.Combine(scenario.LocalRoot, name), content);
                var closed = Stopwatch.GetTimestamp();
                await Task.WhenAll(completions).WaitAsync(TimeSpan.FromMinutes(2));
                Console.WriteLine($"SPEED batch files=5 bytes_each=1048576 from_last_close_ms={Stopwatch.GetElapsedTime(closed).TotalMilliseconds:F3} including_writes_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} calls={string.Join(',', scenario.Account.Calls.Skip(calls).GroupBy(x=>x).Select(g=>g.Key+"="+g.Count()))}");
            }
            await scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation);
        }
    }
}
