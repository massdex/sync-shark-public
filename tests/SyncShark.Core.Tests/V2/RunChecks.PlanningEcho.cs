namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task PlanningEchoRetainsLaterObservationAsync()
    {
        await PlanningEchoRetainsLaterObservationAsync(changed: false);
        await PlanningEchoRetainsLaterObservationAsync(changed: true);
    }

    private static async Task PlanningEchoRetainsLaterObservationAsync(bool changed)
    {
        using var store = SqliteStateStore.Open(FixtureFolder("planning-echo-later-observation"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.ContentProofStore = store;
        var original = fx.A.Seed("file.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed(original.Path, "first upload");
        var root = fx.Pair.A.RootId;
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root, new NtfsUsnCheckpoint(2, 0, 200, 0).Cursor, 100, false, protect: false);
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 64) }, TimeProvider.System);
        var puts = fx.WriterB.Puts;
        long externalVersion = 0;
        run.PlanningAcknowledgementForTest = () =>
        {
            var current = store.ByObject(root, original.ObjectId)!;
            if (externalVersion != 0 || fx.WriterB.Puts <= puts || current.Hash is null) return;
            if (changed)
            {
                var later = fx.A.Seed(original.Path, "later user change");
                Require(store.DeferIndexObservation(root, Side.A, later, current, "test later observation"),
                    "external user edit is observed after upload before planning acknowledgement");
            }
            else store.RetainPlanningObservations(root, [current], default);
            externalVersion = store.DeferredPlanningDisplayPage(fx.Pair.PairId)
                .Single(row => row.RootId == root && row.ObjectId == original.ObjectId).Version;
        };
        await run.ReconcileScopeAsync(fx.Pair.PairId,
            StateMonitor.FromGap(gaps.GapEpoch(root)!) with { RootId = root }, new RecordingProgress(), default);
        Require(externalVersion != 0 && store.DeferredPlanningDisplayPage(fx.Pair.PairId)
            .Any(row => row.RootId == root && row.ObjectId == original.ObjectId && row.Version == externalVersion),
            changed ? "a later external user edit keeps its observation planning debt" :
                "a later external equal observation keeps its exact planning version despite settled content");
    }
}
