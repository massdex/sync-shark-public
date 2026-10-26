namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationStopDrainsBatch()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "reconcile-stop-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Parent/a.txt", "original");
        fx.A.Seed("Parent/b.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("Parent/a.txt", "changed");
        fx.A.Seed("Parent/b.txt", "changed");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, async () =>
        {
            if (Interlocked.Increment(ref reads) != 2) return;
            entered.TrySetResult();
            // Deliberately ignore cancellation until the admitted provider operation drains.
            await release.Task;
        }));
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        var decision = new StateMonitorDecision(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced);
        var repair = run.ReconcileScopeAsync(fx.Pair.PairId, decision, new RecordingProgress(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var admitted = false;
            var next = run.WithPairAdmissionAsync(fx.Pair.PairId, () =>
            {
                admitted = true;
                return Task.FromResult(true);
            }, default);
            Require(!admitted, "the active repair batch owns pair admission");
            var stop = run.StopAsync();
            Require(!stop.IsCompleted && !admitted, "StopAsync waits for the admitted batch to drain");
            var refused = false;
            try { _ = run.ReconcileScopeAsync(fx.Pair.PairId, decision, new RecordingProgress(), default); }
            catch (SyncRunRefusedException) { refused = true; }
            Require(refused, "active scope remains registered while its batch holds admission");
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            await next.WaitAsync(TimeSpan.FromSeconds(10));
            try { await repair; } catch (OperationCanceledException) { }
            Require(admitted && reads == 2, "stop releases admission without starting another repair batch");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            // A completed stop must remove the old scope, rather than refuse a later invocation.
            var later = run.ReconcileScopeAsync(fx.Pair.PairId, decision, new RecordingProgress(), cancelled.Token);
            try { await later; } catch (OperationCanceledException) { }
        }
        finally
        {
            release.TrySetResult();
            await run.StopAsync();
        }
    }
}
