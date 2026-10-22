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
        Console.WriteLine("PASS PlanningDeferredReplaysNextRun");
    }
}
