namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationBacklogKeepsGapMarker()
    {
        var folder = FixtureFolder("reconcile-backlog-gap");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("existing.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            ((ILocalGapStore)store).BeginGap(fx.Pair.A.RootId, new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor, 100, true);
            Require(store.GapInventoryPending(fx.Pair.A.RootId), "fixture has a durable gap inventory obligation");
            await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.PlanningBacklog), new RecordingProgress(), default);
            Require(store.GapInventoryPending(fx.Pair.A.RootId),
                "planning-only selection cannot discharge a root inventory obligation");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task PruneAtReconciliationEnd()
    {
        var folder = FixtureFolder("reconcile-prune");
        try
        {
            var clock = new CompletionClock();
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, clock, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            foreach (var path in new[] { "a.txt", "b.txt", "c.txt" }) fx.A.Seed(path, "before");
            await FirstSyncAsync(fx.Run(), fx);
            foreach (var path in new[] { "a.txt", "b.txt", "c.txt" }) fx.A.Seed(path, "after");
            clock.Now += V2Settings.StorePruneInterval;
            var before = store.PrunePasses;
            fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, () =>
            {
                Require(store.PrunePasses == before, "selection and bounded batches never prune the whole store");
                return Task.CompletedTask;
            }));
            var run = new SyncRun(store, fx.Executor, fx.Provider,
                Settings with { ReconciliationBatch = new(1, 1024) }, clock);
            var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
            Require(report.Failed == 0 && store.PrunePasses == before + 1,
                "one throttled prune runs after every reconciliation batch finishes");
        }
        finally { DeleteFixture(folder); }
    }

    private static Task ReconciliationClearsSettleEquivalent() => ReconcilePlanningDebt(false);
    private static Task ReconciliationClearsNonUrgentIntake() => ReconcilePlanningDebt(true);

    private static async Task ReconcilePlanningDebt(bool missingBaseline)
    {
        var folder = FixtureFolder("reconcile-planning-debt");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var original = fx.A.Seed("existing.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            var item = missingBaseline ? fx.A.Seed("queued.txt", "queued content") : original;
            if (missingBaseline) SelectionSnapshot(store, fx.Pair.A, Side.A, [original, item]);
            store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, item)]);
            fx.A.HashedPaths.Clear();
            var writes = fx.B.WriteCalls;
            var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                new(null, ReconciliationScope.Root, ReconciliationTrigger.PlanningBacklog),
                new RecordingProgress(), default);
            Require(!report.CompletionFailed && report.Failed == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                "one reconciliation drains non-urgent planning debt without AutoSync intake");
            Require((await fx.B.ObserveAsync(item.Path, default))?.Hash == item.Hash,
                "the selected planning item reaches its current counterpart");
            if (!missingBaseline)
                Require(fx.B.WriteCalls == writes && fx.A.HashedPaths.Count == 0,
                    "settle-equivalent work uses verified equality without provider effects or hashing");
        }
        finally { DeleteFixture(folder); }
    }
}
