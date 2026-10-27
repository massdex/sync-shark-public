namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task MonitorPreparesEndpointsAsync()
    {
        var folder = FixtureFolder("monitor-preparation");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("file.txt", "before");
            await FirstSyncAsync(fx.Run(), fx);
            var provider = new OpeningEndpointProvider(fx.Provider);
            var run = new SyncRun(store, fx.Executor, provider, Settings, TimeProvider.System);
            var sampling = run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!sampling.IsCompleted && provider.Accesses == 0,
                "startup monitor awaits endpoint preparation before inventory access");
            provider.Release.TrySetResult();
            Require(!await sampling && provider.Accesses > 0 && provider.UnopenedAccesses == 0,
                "startup monitor samples only prepared endpoints");

            provider = new OpeningEndpointProvider(fx.Provider);
            provider.Release.TrySetResult();
            run = new SyncRun(store, fx.Executor, provider, Settings, TimeProvider.System);
            fx.A.Seed("file.txt", "after");
            var clock = new ManualClock();
            var auto = new AutoSync(store, run, new FakeChangeWatchSource(), Settings,
                _ => false, () => Settings.MinSafetyPoll, clock);
            try
            {
                auto.Start();
                await WaitForMonitorWakeAsync(() => auto.States.Any(state => state.Status == AutoStatus.Waiting) && clock.ActiveTimers > 0);
                clock.Advance(Settings.ChangeQuietWindow);
                await WaitForMonitorWakeAsync(() => provider.Preparations >= 2 && auto.States.Any(state => state.Status == AutoStatus.Idle));
                Require(provider.UnopenedAccesses == 0 && (await fx.B.ObserveAsync("file.txt", default))?.Hash == Hash("after"),
                    "PairLoop first wake prepares monitor then completes the incremental run without unopened access");
            }
            finally { await auto.StopAsync(); await run.StopAsync(); }
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task WaitForMonitorWakeAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }

    private sealed class OpeningEndpointProvider(IEndpointProvider inner) : IEndpointProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Preparations;
        public int Accesses;
        public int UnopenedAccesses;
        private bool _opened;
        public async Task PrepareAsync(PairDef pair, CancellationToken ct)
        {
            Interlocked.Increment(ref Preparations);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await inner.PrepareAsync(pair, ct);
            _opened = true;
        }
        public IInventorySource Inventory(RootDef root) { CheckOpened(); return inner.Inventory(root); }
        public IEndpointWriter Writer(RootDef root) { CheckOpened(); return inner.Writer(root); }
        private void CheckOpened()
        {
            Interlocked.Increment(ref Accesses);
            if (_opened) return;
            Interlocked.Increment(ref UnopenedAccesses);
            throw new SyncRunRefusedException("The endpoints for this pair were not opened before the run.");
        }
    }
}
