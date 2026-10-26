namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationUserChangePriorityAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("reconciliation-priority"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        foreach (var path in new[] { "Edit", "Rename", "Move", "Remote", "bulk-a", "bulk-b", "bulk-c" })
            fx.A.Seed(path, "original");
        await FirstSyncAsync(fx.Run(), fx);
        foreach (var path in new[] { "bulk-a", "bulk-b", "bulk-c" }) fx.A.Seed(path, "gap change");
        var cursor = (await fx.A.FullAsync(0, _ => true, SilentProgress, default)).CursorAfter;
        ((ILocalGapStore)store).BeginGap(fx.Pair.A.RootId, PriorityCheckpoint.Cursor, 100, false, protect: false);
        // The missing interval is reconciliation work, not part of the retained provider delta.
        var generation = store.BeginGeneration(fx.Pair.A.RootId, "fixture-retained-replay");
        store.CompleteGeneration(generation, cursor, true, settleInventory: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var autoFinished = false;
        fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, async () =>
        {
            var read = ++reads;
            if (read == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            if (read == 3)
            {
                Require(autoFinished, "fresh user changes complete before the next admitted gap batch reads");
                nextBatch.TrySetResult();
            }
        }));
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        try
        {
            Require(await run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default),
                "the monitor starts reconciliation for the durable journal gap");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var auto = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
            {
                await MakePriorityChangesAsync(fx);
                var fullA = fx.A.FullCalls;
                var fullB = fx.B.FullCalls;
                var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(fx.A.FullCalls == fullA && fx.B.FullCalls == fullB,
                    "fresh priority changes use provider deltas rather than another bulk inventory");
                await AssertPriorityAppliedAsync(fx, report);
                Require(store.PendingGapInventory(fx.Pair.A.RootId) &&
                    (await fx.B.ObserveAsync("bulk-b", default))?.Hash != (await fx.A.ObserveAsync("bulk-b", default))?.Hash,
                    "fresh user changes settle while later gap work remains pending");
                autoFinished = true;
                return report;
            }, default);
            release.TrySetResult();
            await auto.WaitAsync(TimeSpan.FromSeconds(20));
            await nextBatch.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await FinishedAsync();
            await MirrorAsync(fx);
        }
        finally
        {
            release.TrySetResult();
            await run.StopAsync();
        }

        async Task FinishedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (store.PendingGapInventory(fx.Pair.A.RootId)) await Task.Delay(20, deadline.Token);
        }
    }
}
