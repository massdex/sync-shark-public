namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Reconcile_AllPagesAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reconcile-pages-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        const int count = 600;
        var writer = new ExcludedDeleteJobWriter(fx.WriterB) { Completed = true };
        fx.Provider.AddWriter(fx.Pair.B, writer);
        for (var i = 0; i < count; i++)
        {
            var path = "deleted-" + i;
            var source = fx.A.Seed(path, "same");
            var victim = fx.B.Seed(path, "same");
            var adopt = new SyncAction("adopt-" + i, 3, ActionKind.Adopt, Side.B, path, null,
                source, victim, false, "key-" + i);
            store.CommitResult(fx.Pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, source, victim, null, null));
            var deletion = new SyncAction("delete-" + i, 6, ActionKind.Delete, Side.B, path, null,
                null, victim, false, "key-" + i)
            {
                ProviderEffect = new("delete-job", "job-" + i, DateTimeOffset.UtcNow),
                DeletionSourceObjectId = source.ObjectId
            };
            await fx.A.DeleteAsync(deletion with { Target = Side.A, ExpectedTarget = source }, default);
            await fx.B.DeleteAsync(deletion, default);
            store.RecordIntent(fx.Pair.PairId, deletion);
        }
        var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(writer.Checks == count && store.UnresolvedIntents(fx.Pair.PairId).Count == 0 && report.Uncertain == 0,
            "all 600 saved delete jobs settle in one run, crossing both 256-entry page boundaries");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "reconciliation observes jobs without replaying deletes");
        Console.WriteLine($"PASS reconcile pages: {writer.Checks} saved jobs settled in one run");
    }
}
