namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal bool RetireStorageForResync(StorageSettlement job, PairDef pair, string path, string reason) => InStore(() =>
    {
        if (!IsCurrentStorageSettlement(job)) return false;
        var prior = SyncRetryAt(pair.PairId, path);
        var roots = (prior?.ReadRoots ?? []).Concat(new[] { pair.A.RootId, pair.B.RootId }).Distinct().ToArray();
        SaveSyncRetry(pair.PairId, prior is null
            ? new SyncRetry(path, "scan", 0, reason, DateTimeOffset.MinValue, ReadRoots: roots)
            : prior with { ReadRoots = roots }); // Preserve existing reviewed choices and their due time.
        return RetireStorageSettlement(job);
    });

    // Native pointer refresh runs outside the run gate. Publish its result only while the
    // exact common baseline and local/remote inventory still match what it observed.
    internal bool CommitStoragePointerBaseline(PairDef pair, Baseline expected, SyncAction action,
        ActionResult result) => InStore(() =>
    {
        var current = BaselinesAt(pair.PairId, Side.A, [expected.A.Path])
            .FirstOrDefault(point => point.ObjectKey == expected.ObjectKey);
        if (current != expected || Pair(pair.PairId) is not { } binding ||
            binding.A != pair.A || binding.B != pair.B) return false;
        var local = ByObject(pair.A.RootId, expected.A.ObjectId);
        var remote = ByObject(pair.B.RootId, expected.B.ObjectId);
        if (local != action.ExpectedTarget || remote?.VersionId != result.NewB?.VersionId ||
            remote?.Hash != result.NewB?.Hash || remote?.Path != result.NewB?.Path) return false;
        // RecordIntent preceded the native effect. Re-recording is idempotent and documents
        // that CommitResult is never a synthetic result with no durable intent.
        RecordIntent(pair.PairId, action);
        CommitResult(pair.PairId, expected.Gen, action, result);
        return true;
    });
}
