namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    // D72/D75: manual CLI calls share SyncRun's pair-owned admission, not a runtime-wide gate.
    private static async Task Cli_Admission_UnrelatedRunsOverlapSamePairQueuesAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var other = new PairDef("p2", "Pair B",
                new RootDef("rc", SyncProviders.Local, null, "C:\\other", "id:rc"),
                new RootDef("rd", SyncProviders.Dropbox, "acc", "/other", "id:rd"), [], [], true);
            store.SavePair(other);
            store.MarkFirstSyncDone(other.PairId);
            var otherA = new FakeEndpoint(Side.A, other.A.RootId);
            var otherB = new FakeEndpoint(Side.B, other.B.RootId);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstGate = new GatedInventory(fx.A) { Block = release };
            var otherGate = new GatedInventory(otherA);
            fx.Provider.AddInventory(fx.Pair.A, firstGate)
                .Add(other.A, otherA).Add(other.B, otherB).AddInventory(other.A, otherGate);
            var runtime = new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = runtime.RunPairAsync(fx.Pair.PairId, _ => { }, stop.Token);
            Task<SyncCliRunData>? queued = null;
            Task<SyncCliRunData>? independent = null;
            try
            {
                await firstGate.Entered.WaitAsync(stop.Token);
                var reads = firstGate.Reads;
                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                queued = runtime.RunPairAsync(fx.Pair.PairId, line =>
                {
                    if (line.Contains("Waiting for the Sync Shark data lock", StringComparison.Ordinal))
                        waiting.TrySetResult();
                }, stop.Token);
                await waiting.Task.WaitAsync(stop.Token);
                Require(!release.Task.IsCompleted && !queued.IsCompleted,
                    "a queued same-pair CLI run announces its admission wait before the predecessor releases");
                independent = runtime.RunPairAsync(other.PairId, _ => { }, stop.Token);
                await otherGate.Entered.WaitAsync(stop.Token);
                await independent.WaitAsync(stop.Token);
                var rescan = await runtime.RescanPairAsync(other.PairId, null, stop.Token);
                Require(rescan.Roots.Count == 2 && store.Cursor(other.A.RootId) is null && store.Cursor(other.B.RootId) is null,
                    "an unrelated rescan completes through nested pair admission while the first CLI run remains blocked");
                Require(!first.IsCompleted && !queued.IsCompleted && firstGate.Reads == reads,
                    "an unrelated manual CLI run completes while the first is blocked and its same-pair successor stays queued");
                release.TrySetResult();
                await Task.WhenAll(first, queued).WaitAsync(stop.Token);
                Require(firstGate.Reads > reads,
                    "the same-pair manual successor scans only after its predecessor releases admission");
            }
            finally
            {
                release.TrySetResult();
                await stop.CancelAsync();
                try { await Task.WhenAll(new[] { first, queued, independent }.OfType<Task<SyncCliRunData>>()); }
                catch (OperationCanceledException) { }
            }
        }
        finally { Cleanup(root); }
    }
}
