namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    /// <summary>
    /// Production-run proof for queue audit V4's metadata planning handoff:
    /// <c>--v2-gap-promotion-handoff-checks</c>. A cloud change held by an unpublished local
    /// metadata proof must be planned by the next run from the existing deferred intake; metadata
    /// promotion marks the verified local entity planning-dirty without creating content-hash debt.
    /// </summary>
    public static async Task GapPromotionHandoffChecksAsync()
    {
        var folder = FixtureFolder("gap-promotion-handoff");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Held/File", "original");
            fx.B.Seed("Held/File", "original");
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);

            var local = store.Live(fx.Pair.A.RootId, ["Held/File"]).Single();
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "synthetic unpublished gap");
            var edited = fx.B.Seed("Held/File", "changed during gap");
            var held = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(held.Failures.Count == 0 && store.AdmitDeferredPlanning(fx.Pair.PairId, long.MaxValue, rotate: false)
                    .Any(row => row.ObjectId == edited.ObjectId),
                "the changed cloud entity is held while the local metadata proof is unpublished");
            var rehashBefore = store.RehashPendingCount(fx.Pair.A.RootId);

            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            gaps.GapDiscoveredBatch(fx.Pair.A.RootId, epoch, new[] { (local, false) });
            gaps.GapDiscoveryDone(fx.Pair.A.RootId, epoch);
            gaps.PromoteGapDiscovery(fx.Pair.A.RootId);
            Require(store.AdmitDeferredPlanning(fx.Pair.PairId, long.MaxValue, rotate: false).Any(row => row.ObjectId == local.ObjectId),
                "promotion marks the verified local entity planning-dirty for the pair using the root");
            Require(store.AdmitDeferredPlanning(fx.Pair.PairId, long.MaxValue, rotate: false).Any(row => row.ObjectId == edited.ObjectId),
                "the held cloud handoff is kept, not replaced, by metadata promotion");
            Require(store.RehashPendingCount(fx.Pair.A.RootId) == rehashBefore,
                "the planning handoff creates no content-hash debt");

            var replay = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var synced = await fx.A.ObserveAsync("Held/File", CancellationToken.None);
            Require(replay.Failures.Count == 0 && synced?.Hash == edited.Hash,
                "the next run plans the held cloud change from deferred intake");
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                "the consumed planning handoff settles after the replay run");
        }
        finally { DeleteFixture(folder); }
    }
}
