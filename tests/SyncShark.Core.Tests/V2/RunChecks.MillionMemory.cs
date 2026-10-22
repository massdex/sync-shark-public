namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Explicit opt-in: not part of the ordinary suite. Run the same fixture on the base and candidate
    // builds in separate processes, with TEMP/TMP on F:, to compare sampled managed memory
    // and the OS process working-set peak. This synthetic in-memory fixture is not a live SQLite run.
    public static async Task MillionItemPrepareMemoryAsync()
    {
        const int count = 1_000_000;
        // About 1.3x the measured no-forced-GC candidate (1.60 GB managed / 1.79 GB working set).
        const long limit = 2_100_000_000;
        const long workingSetLimit = 2_350_000_000;
        var progress = new RecordingProgress();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var fx = Build();
        fx.Provider.AddInventory(fx.Pair.A, new MillionInventory(fx.Pair.A.RootId, Side.A, count));
        fx.Provider.AddInventory(fx.Pair.B, new MillionInventory(fx.Pair.B.RootId, Side.B, count));
        GC.Collect();
        long peak = GC.GetTotalMemory(false);
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var value = GC.GetTotalMemory(false);
                if (value > peak) Interlocked.Exchange(ref peak, value);
                try { await Task.Delay(10, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
            Require(plan.MatchedBothSides.Count == count, "million-item prepare captures every matched pair");
            Require(plan.Actions.All(action => action.Kind == ActionKind.Adopt), "equal million-item pair needs only adoption");
            Require(plan.Actions.Count == count, "million-item prepare produces all adoptions");
            Console.WriteLine($"prepare-wall-ms={watch.ElapsedMilliseconds}");
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Console.WriteLine($"prepare-completed-gen2-heap={GC.GetGCMemoryInfo().HeapSizeBytes}");
            GC.KeepAlive(plan);
        }
        finally
        {
            stop.Cancel();
            await sampler;
            peak = Math.Max(peak, GC.GetTotalMemory(false));
            fx.Store.Dispose();
        }
        process.Refresh();
        Console.WriteLine($"post-phase-working-set={process.WorkingSet64}; peak-working-set={process.PeakWorkingSet64}; private-bytes={process.PrivateMemorySize64}; working-set-bound={workingSetLimit}");
        Console.WriteLine($"million-item prepare: peak-managed-bytes={peak}; bound={limit}");
        Require(peak < limit, "million-item prepare stays below the measured managed-memory regression ceiling");
        Require(process.PeakWorkingSet64 < workingSetLimit, "million-item prepare stays below the measured working-set regression ceiling");
    }

    private sealed class MillionInventory(string rootId, Side side, int count) : IInventorySource
    {
        private static readonly string Hash = new('a', 64);
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress, CancellationToken ct)
        {
            var items = new ItemState[count];
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = $"file-{i:D7}.txt";
                items[i] = new ItemState(path, ItemKind.File, $"id:{side}:{i}", null,
                    side == Side.A && i == 0 ? null : Hash, 1, DateTimeOffset.UnixEpoch, ItemFlags.None);
            }
            return Task.FromResult(new Snapshot(rootId, side, gen, "million", items, []));
        }
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            throw new InvalidOperationException("The memory fixture is a first-sync prepare only.");
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(paths.ToDictionary(path => path, _ => Hash));
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());
    }
}
