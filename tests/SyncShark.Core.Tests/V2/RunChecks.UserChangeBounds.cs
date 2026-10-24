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
            for (var index = 0; index < 1100; index++)
            { fx.A.Seed("Recent-" + index, "original"); fx.B.Seed("Recent-" + index, "original"); }
            await FirstSyncAsync(fx.Run(), fx);
            SeedPriorityBacklog(store, fx.Pair); // Existing files now have discovery-owned gap holds.
            var connection = PriorityConnection(store);
            var writes = Convert.ToInt64(StoreSchema.Scalar(connection, null, "SELECT total_changes();"));
            var idleGaps = (ILocalGapStore)store;
            Require(idleGaps.GapHashWorkPage(fx.Pair.A.RootId, idleGaps.GapEpoch(fx.Pair.A.RootId)!, 16, includeBulk: false).Count == 0 &&
                Convert.ToInt64(StoreSchema.Scalar(connection, null, "SELECT total_changes();")) == writes,
                "paused polling without urgent work does not write or sweep bulk orphans");
            for (var index = 0; index < 1100; index++) fx.A.Seed("Recent-" + index, "edited");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(store.UrgentPending(fx.Pair.PairId) == 2200, "only actual delta entities acquire user provenance, never bulk intake");
            var io = new UrgentCheckIo(fx.A);
            var gaps = (ILocalGapStore)store;
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            var selected = gaps.GapHashWorkPage(fx.Pair.A.RootId, epoch, 128, includeBulk: false);
            Require(selected.Count == 64 && selected.All(work => work.Urgent), "store selection enforces the 64-item urgent ceiling");
            StoreSchema.Execute(PriorityConnection(store), null,
                "UPDATE planning_deferred SET version=CASE path WHEN 'Recent-1099' THEN 1 ELSE 2 END WHERE path IN ('Recent-1099','Recent-1098');");
            var mixed = gaps.GapHashWorkPage(fx.Pair.A.RootId, epoch, 128, includeBulk: true);
            Require(mixed.Count == 128 && mixed.Take(64).All(work => work.Urgent) && mixed.Skip(64).All(work => !work.Urgent) &&
                mixed[0].Item.Path == "Recent-1099" && mixed[1].Item.Path == "Recent-1098",
                "unpaused hashing selects oldest urgent versions before bulk, not object-id order");
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
                admitted.Count(row => row.Reason == SqliteStateStore.UserChangeReason) == 128 &&
                admitted.Where(row => row.Reason == SqliteStateStore.UserChangeReason).Sum(row => row.Paths.Count) <= 512,
                "planning reserves half of both budgets for bulk while retaining the total caps");
            Require(store.UrgentPending(fx.Pair.PairId) == 2200, "rotation cannot erase urgency");
            await worker.HashPage(fx.Pair.A, epoch, default);
            Require(io.Attempts.Count == 1040, "planning rotation moves the bounded root window forward");
        }
        finally { DeleteFixture(folder); }
    }
}
