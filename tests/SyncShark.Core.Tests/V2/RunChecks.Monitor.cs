namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task MonitorDerivesScopeAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "monitor-restart-" + Guid.NewGuid().ToString("N"));
        var cursor = new NtfsUsnCheckpoint(7, 100, 300, 100).Cursor;
        using (var original = SqliteStateStore.Open(directory, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var before = Build(suppliedStore: original);
            before.A.Seed("Parent/Child.txt", "original");
            await FirstSyncAsync(before.Run(), before);
            ((ILocalGapStore)original).BeginGap(before.Pair.A.RootId, cursor, 90, true,
                "journal history discontinuity", protect: false);
        }
        using var store = SqliteStateStore.Open(directory, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, async () =>
        {
            entered.TrySetResult();
            await release.Task;
        }));
        var run = fx.Run();
        try
        {
            var first = run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(first.IsCompletedSuccessfully && await first,
                "monitor returns after synchronous registration without waiting for selection");
            Require(!await run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default),
                "a second monitor sample cannot start another reconciliation");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = run.StopAsync();
            Require(!stop.IsCompleted, "StopAsync tracks the monitor-started selection task");
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Require(store.GapInventoryPending(fx.Pair.A.RootId),
                "cancelled selection retains the durable restart obligation");
        }
        finally
        {
            release.TrySetResult();
            await run.StopAsync();
        }
    }
}
