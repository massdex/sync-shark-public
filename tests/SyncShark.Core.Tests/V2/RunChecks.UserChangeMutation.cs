namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task UrgentMutationAsync()
    {
        var folder = FixtureFolder("urgent-mutation");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Autosave", "original"); fx.B.Seed("Autosave", "original");
            await FirstSyncAsync(fx.Run(), fx);
            SeedPriorityBacklog(store, fx.Pair);
            fx.A.Seed("Autosave", "first edit");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var io = new UrgentCheckIo(fx.A) { ChangingPath = "Autosave" };
            var gaps = (ILocalGapStore)store;
            var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
            await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
            for (var save = 0; save < 3; save++)
            {
                await worker.HashPage(fx.Pair.A, epoch, default);
                var count = Convert.ToInt64(StoreSchema.Scalar(PriorityConnection(store), null,
                    "SELECT COUNT(*) FROM local_gap_item WHERE defer_count=$n AND failure_count=0 AND hash IS NULL AND retry_after>$now;",
                    ("$n", save + 1), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
                Require(count == 1 && store.OpenNotices(fx.Pair.PairId).All(n => !n.ReasonKey.StartsWith(SqliteStateStore.IndexFailurePrefix, StringComparison.Ordinal)),
                    "repeated urgent saves defer without failure counts or notices");
                var attempts = io.Attempts.Count;
                await worker.HashPage(fx.Pair.A, epoch, default);
                Require(io.Attempts.Count == attempts, "a concurrent save obeys its retry deadline");
                await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                store.RequestIndexRetry(fx.Pair.A.RootId);
            }
            io.ChangingPath = null;
            await worker.HashPage(fx.Pair.A, epoch, default);
            Require(gaps.GapResults(fx.Pair.A.RootId, gaps.GapResultWatermark(fx.Pair.A.RootId), 128).Count == 1,
                "the stable urgent file produces one later verified result");
            Require(store.OpenNotices(fx.Pair.PairId).All(n => !n.ReasonKey.StartsWith(SqliteStateStore.IndexFailurePrefix, StringComparison.Ordinal)),
                "successful retry leaves no false indexing notice");
        }
        finally { DeleteFixture(folder); }
    }
}
