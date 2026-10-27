namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task AutoSyncLeavesPlanningBacklogAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("autosync-backlog-owner"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("fresh.txt", "before");
        fx.A.Seed("urgent.txt", "before");
        await FirstSyncAsync(fx.Run(), fx);
        var urgent = fx.A.Seed("urgent.txt", "after");
        var backlog = Enumerable.Range(0, 5000).Select(index => fx.A.Seed($"queued-{index:D5}.txt", "same")).ToArray();
        foreach (var item in backlog) fx.B.Seed(item.Path, "same");
        foreach (var (root, endpoint) in new[] { (fx.Pair.A, fx.A), (fx.Pair.B, fx.B) })
        {
            var gen = store.BeginGeneration(root.RootId, "fixture");
            var snapshot = await endpoint.FullAsync(gen, _ => true, SilentProgress, default);
            store.WriteSnapshot(snapshot);
            store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        }
        // Snapshot publication itself queues both roots. Finish that fixture-owned handoff
        // before seeding exactly the independent backlog this check measures.
        while (store.AdmitDeferredPlanning(fx.Pair.PairId, long.MaxValue, rotate: false) is { Count: > 0 } observed)
            store.AcknowledgeDeferredPlanning(fx.Pair.PairId, observed, _ => true);
        store.DeferPlanning(fx.Pair.PairId, backlog.Select(item => (fx.Pair.A.RootId, item)).ToArray());
        store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, urgent)], urgent: true);
        Require(store.CountDeferredPlanning(fx.Pair.PairId) == backlog.Length + 1,
            "fixture owns exactly 5000 bulk entities and one urgent handoff before AutoSync");
        fx.A.Seed("fresh.txt", "after");
        var writes = fx.B.WriteCalls;
        var run = fx.Run();
        var report = await ((ISyncRun)run).AutoSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Uploaded == 2 && fx.B.WriteCalls == writes + 2 && report.ChangedPaths == 2 &&
            (await fx.B.ObserveAsync("fresh.txt", default))?.Hash == (await fx.A.ObserveAsync("fresh.txt", default))?.Hash &&
            (await fx.B.ObserveAsync("urgent.txt", default))?.Hash == urgent.Hash,
            "AutoSync applies fresh delta and checkpointed urgent work without admitting the 5000-row bulk backlog");
        Require(store.CountDeferredPlanning(fx.Pair.PairId) == backlog.Length && report.PlanningBacklog == backlog.Length,
            "the untouched backlog remains durable and visible in the AutoSync status report");
        var repaired = await run.ReconcileScopeAsync(fx.Pair.PairId, StateMonitor.PlanningBacklog(), new RecordingProgress(), default);
        Require(repaired.Failed == 0 && !repaired.CompletionFailed && store.CountDeferredPlanning(fx.Pair.PairId) == 0,
            "reconciliation, not AutoSync intake, later settles the retained backlog");
    }
}
