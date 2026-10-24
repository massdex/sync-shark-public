namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task UrgentBoundsAsync()
    {
        var folder = FixtureFolder("urgent-bounds");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Anchor", "same"); fx.B.Seed("Anchor", "same");
            await FirstSyncAsync(fx.Run(), fx);
            SeedPriorityBacklog(store, fx.Pair);
            for (var index = 0; index < 1100; index++) fx.A.Seed("Recent-" + index, "new");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(store.UrgentPending(fx.Pair.PairId) == 1100, "only actual delta entities acquire user provenance, never bulk intake");
            var io = new UrgentCheckIo(fx.A);
            var gaps = (ILocalGapStore)store;
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            var selected = gaps.GapHashWorkPage(fx.Pair.A.RootId, epoch, 128, includeBulk: false);
            Require(selected.Count == 64 && selected.All(work => work.Urgent), "store selection enforces the 64-item urgent ceiling");
            await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
            for (var pass = 0; pass < 64; pass++)
            {
                var before = io.Attempts.Count;
                await worker.HashPage(fx.Pair.A, epoch, default);
                Require(io.Attempts.Count - before == 16, "urgent hashing is capped at 64 per pass");
            }
            await worker.HashPage(fx.Pair.A, epoch, default);
            Require(io.Attempts.Count == 1024, "hashed candidates still consume the 1024-row window; later ready rows are not scanned");
            var admitted = fx.Run().AdmitPlanningPage(fx.Pair.PairId);
            Require(admitted.Count == 256 && admitted.Sum(row => row.Paths.Count) <= 1024 &&
                admitted.All(row => row.Reason == SqliteStateStore.UserChangeReason), "planning retains its 256-entity and 1024-alias caps");
            Require(store.UrgentPending(fx.Pair.PairId) == 1100, "rotation cannot erase urgency");
            await worker.HashPage(fx.Pair.A, epoch, default);
            Require(io.Attempts.Count == 1040, "planning rotation moves the bounded root window forward");
        }
        finally { DeleteFixture(folder); }
    }
}
