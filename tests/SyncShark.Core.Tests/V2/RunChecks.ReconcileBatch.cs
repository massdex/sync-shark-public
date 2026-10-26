namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static ReconcileBatch BatchFor(params ItemState[] items) => new("batch", items.Select(i => i.Path).ToArray(),
        items.Select(i => i.ObjectId).ToHashSet(StringComparer.Ordinal), new HashSet<string>(), (_, _) => false);

    private static async Task ReconcileBatchPlansCurrentState()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "batch-current-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var selected = fx.A.Seed("selected.txt", "selected version");
        store.MarkFirstSyncDone(fx.Pair.PairId);
        SelectionSnapshot(store, fx.Pair.A, Side.A, [selected]);
        var current = fx.A.Seed(selected.Path, "current provider version");
        fx.A.Seed("unrelated.txt", "not selected");
        var before = store.Cursor(fx.Pair.A.RootId);
        var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = BatchFor(selected) });
        Require(report.Failed == 0 && report.Uncertain == 0 && (await fx.B.ObserveAsync(selected.Path, default))?.Hash == current.Hash,
            "batch re-reads current provider state rather than applying selection metadata");
        Require(await fx.B.ObserveAsync("unrelated.txt", default) is null && store.Cursor(fx.Pair.A.RootId) == before,
            "batch does not plan unrelated items or advance the delta cursor");
    }

    private static async Task ReconcileBatchKeepsOtherScanRetries()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "batch-retry-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var selected = fx.A.Seed("selected.txt", "current");
        var unrelated = new SyncRetry("other.txt", "scan", 1, "other read failed", DateTimeOffset.MinValue);
        store.SaveSyncRetry(fx.Pair.PairId, unrelated);
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = BatchFor(selected) });
        Require(store.SyncRetryAt(fx.Pair.PairId, unrelated.Path) == unrelated,
            "a batch does not retire an unrelated scan retry");
    }

    private static async Task ReconcileBatchForgetsAfterRecovery()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "batch-recovery-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var a = fx.A.Seed("gone.txt", "same");
        var b = fx.B.Seed(a.Path, "same");
        SelectionBaseline(store, fx.Pair, a, b);
        var deletion = new SyncAction("delete", 6, ActionKind.Delete, Side.B, b.Path, null, null, b, false, a.ObjectId)
        { ProviderEffect = new("delete-job", "completed-job"), DeletionSourceObjectId = a.ObjectId };
        await fx.A.DeleteAsync(deletion with { Target = Side.A, ExpectedTarget = a }, default);
        await fx.B.DeleteAsync(deletion, default);
        store.RecordIntent(fx.Pair.PairId, deletion);
        var writer = new ExcludedDeleteJobWriter(fx.WriterB) { Completed = true };
        fx.Provider.AddWriter(fx.Pair.B, writer);
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = BatchFor(a) });
        Require(writer.Checks == 1 && store.UnresolvedIntents(fx.Pair.PairId).Count == 0 &&
            !store.Baselines(fx.Pair.PairId).Any(), "batch recovers the completed delete before settling absence");
    }

    private static Task ReconcileBatchRecoversWholeGroupPlansSelected() =>
        ReconcileFolderPagesAsync(false, true, selectedMemberOnly: true, throughBatch: true);
}
