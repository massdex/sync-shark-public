namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ExecutionProofReflagRetainsDebtAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("execution-proof-reflag"), StorageChecks.Fast,
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
        var reflagged = false;
        run.RehashAcknowledgementForTest = (_, _) =>
        {
            var current = store.ByObject(root, original.ObjectId)!;
            if (reflagged || fx.WriterB.Puts <= puts || current.Hash is null || current.Flags.HasFlag(ItemFlags.NeedsRehash)) return;
            reflagged = true;
            fx.A.Seed(original.Path, "later change");
            // Inject the same write-ahead invalidation that a fresh journal delta owns, after
            // the upload proof has been folded but before the strict acknowledgement CAS.
            store.RequireRehash(root, current, "test later journal write");
        };
        await run.ReconcileScopeAsync(fx.Pair.PairId,
            StateMonitor.FromGap(gaps.GapEpoch(root)!) with { RootId = root }, new RecordingProgress(), default);
        Require(reflagged && store.ByObject(root, original.ObjectId) is { Hash: null } item &&
            item.Flags.HasFlag(ItemFlags.NeedsRehash) && store.RehashPendingCount(root) == 1,
            "an edit reflagged after upload proof is never acknowledged by the execution watermark");
    }
}
