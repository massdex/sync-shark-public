namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task AutoSyncLeavesPlanningBacklogAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("autosync-backlog-owner"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("fresh.txt", "before");
        await FirstSyncAsync(fx.Run(), fx);
        var backlog = Enumerable.Range(0, 5000).Select(index => fx.A.Seed($"queued-{index:D5}.txt", "same")).ToArray();
        foreach (var item in backlog) fx.B.Seed(item.Path, "same");
        foreach (var (root, endpoint) in new[] { (fx.Pair.A, fx.A), (fx.Pair.B, fx.B) })
        {
            var gen = store.BeginGeneration(root.RootId, "fixture");
            var snapshot = await endpoint.FullAsync(gen, _ => true, SilentProgress, default);
            store.WriteSnapshot(snapshot);
            store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        }
        store.DeferPlanning(fx.Pair.PairId, backlog.Select(item => (fx.Pair.A.RootId, item)).ToArray());
        fx.A.Seed("fresh.txt", "after");
        var writes = fx.B.WriteCalls;
        var run = fx.Run();
        var report = await ((ISyncRun)run).AutoSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Uploaded == 1 && fx.B.WriteCalls == writes + 1 && report.ChangedPaths == 1 &&
            (await fx.B.ObserveAsync("fresh.txt", default))?.Hash == (await fx.A.ObserveAsync("fresh.txt", default))?.Hash,
            "AutoSync applies only the fresh edit instead of admitting the 5000-row deferred backlog");
        Require(store.CountDeferredPlanning(fx.Pair.PairId) == backlog.Length && report.PlanningBacklog == backlog.Length,
            "the untouched backlog remains durable and visible in the AutoSync status report");
        var repaired = await run.ReconcileScopeAsync(fx.Pair.PairId, StateMonitor.PlanningBacklog(), new RecordingProgress(), default);
        Require(repaired.Failed == 0 && !repaired.CompletionFailed && store.CountDeferredPlanning(fx.Pair.PairId) == 0,
            "reconciliation, not AutoSync intake, later settles the retained backlog");
    }
}
