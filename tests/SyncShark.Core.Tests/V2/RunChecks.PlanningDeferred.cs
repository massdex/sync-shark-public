namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task PlanningDeferredReplaysNextRunAsync()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(),
            "planning-replay-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Held/File", "original");
        fx.B.Seed("Held/File", "original");
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
        await FirstSyncAsync(run, fx);
        var local = store.Live(fx.Pair.A.RootId, ["Held/File"]).Single();
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic unpublished gap");
        fx.B.Seed("Held/File", "changed during gap");
        var held = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var deferred = store.DeferredPlanningPage(fx.Pair.PairId);
        Require(deferred.Count == 1 && deferred[0].RootId == fx.Pair.B.RootId,
            "SyncRun retains only the changed cloud entity under the local gap");
        Require(held.Deferred == 0 && store.CountSyncRetries(fx.Pair.PairId) == 0,
            "verification deferrals alone never create scheduling debt");
        var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
        gaps.GapDiscoveredBatch(fx.Pair.A.RootId, epoch, new[] { (local, false) });
        gaps.GapDiscoveryDone(fx.Pair.A.RootId, epoch);
        gaps.PromoteGapDiscovery(fx.Pair.A.RootId);
        var replay = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(replay.Failures.Count == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0,
            "the next SyncRun drains published deferred work without another provider delta");
        Require(store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != RehashProtection.Reason),
            "verification summary closes in the successful replay run");
        await MirrorAsync(fx);
        await PlanningDeferredEmptyPathAsync();
        await DropboxChecks.PlanningGapFolderDeleteAsync(Path.Combine(Path.GetTempPath(), "planning-folder-" + Guid.NewGuid().ToString("N")));
        Console.WriteLine("PASS PlanningDeferredReplaysNextRun");
    }
    private static async Task PlanningDeferredEmptyPathAsync()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(),
            "planning-empty-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
        // BeginGap deliberately does nothing for an empty stored root.
        fx.A.Seed("Anchor", "same");
        fx.B.Seed("Anchor", "same");
        await FirstSyncAsync(run, fx);
        var a = fx.A.Seed("Gone", "new");
        var b = fx.B.Seed("Gone", "new");
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic gap");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.CountDeferredPlanning(fx.Pair.PairId) > 0 &&
            store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == RehashProtection.Reason),
            "new hidden work has a verification summary before disappearing");
        await fx.A.DeleteAsync(new("remove-a", 6, ActionKind.Delete, Side.A, a.Path, null, null, a, false, null), default);
        await fx.B.DeleteAsync(new("remove-b", 6, ActionKind.Delete, Side.B, b.Path, null, null, b, false, null), default);
        var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
        gaps.GapDiscoveredBatch(fx.Pair.A.RootId, epoch, store.Live(fx.Pair.A.RootId)
            .Where(item => item.Kind == ItemKind.File).Select(item => (item, false)).ToArray());
        gaps.GapDiscoveryDone(fx.Pair.A.RootId, epoch);
        gaps.PromoteGapDiscovery(fx.Pair.A.RootId);
        Require(!gaps.HasGapProtection(fx.Pair.A.RootId), "empty-path fixture completes gap discovery before replay");
        var complete = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(complete.Failed == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0 &&
            store.OpenNotices(fx.Pair.PairId).All(n => n.ReasonKey != RehashProtection.Reason),
            "complete run settles an empty touched path without a baseline and closes verification notice");
    }
}
