namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task InvalidationWaitsForAutoSync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("invalidation-admission"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("file.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auto = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
        {
            entered.SetResult();
            await release.Task;
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(!report.CompletionFailed, "active AutoSync finishes before the invalidation page");
            return true;
        }, default);
        await entered.Task;
        var page = run.WithPairAdmissionAsync(fx.Pair.PairId, () =>
        {
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(fx.Pair.A.RootId, PriorityCheckpoint.Cursor, 100, false, protect: false);
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            return Task.FromResult(store.InvalidateReconciliationPage(fx.Pair.A.RootId, epoch.Epoch, _ => true));
        }, default);
        Require(!page.IsCompleted && store.RehashPendingCount(fx.Pair.A.RootId) == 0,
            "invalidation waits behind active AutoSync without mutating proof");
        release.SetResult();
        await Task.WhenAll(auto, page).WaitAsync(TimeSpan.FromSeconds(30));
        Require(store.RehashPendingCount(fx.Pair.A.RootId) == 1,
            "admitted invalidation commits the page transaction after AutoSync releases admission");
    }
}
