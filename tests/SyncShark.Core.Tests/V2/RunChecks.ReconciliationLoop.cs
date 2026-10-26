namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static Task ReconciliationAdmitsAutoSyncBetweenBatches() => ReconciliationInterleave(false);
    private static Task ReconciliationStaleAdoptBetweenBatches() => ReconciliationInterleave(true);

    private static async Task ReconciliationInterleave(bool replaceSelected)
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "reconcile-loop-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("a.txt", "original");
        fx.A.Seed("z.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("a.txt", "changed a");
        fx.A.Seed("z.txt", "changed z");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var autoFinished = false;
        fx.Provider.AddInventory(fx.Pair.A, new ReconciliationBarrierInventory(fx.A, async () =>
        {
            var read = ++reads;
            if (read == 2)
            {
                entered.TrySetResult();
                await release.Task;
            }
            if (read == 3)
                Require(autoFinished && store.Baselines(fx.Pair.PairId).Any(basis => basis.A.Path == "z.txt"),
                    "next reconciliation batch sees the intervening AutoSync baseline");
        }));
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1024) }, TimeProvider.System);
        var repair = run.ReconcileScopeAsync(fx.Pair.PairId,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.JournalReplaced), new RecordingProgress(), default);
        await Task.WhenAny(entered.Task, repair);
        if (!entered.Task.IsCompleted) await repair;
        var auto = run.WithPairAdmissionAsync(fx.Pair.PairId, async () =>
        {
            if (replaceSelected) fx.A.Seed("z.txt", "latest after selection");
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            autoFinished = true;
            return report;
        }, default);
        release.TrySetResult();
        await Task.WhenAll(auto, repair);
        Require(autoFinished && reads >= 3, "AutoSync acquired admission between bounded reconciliation batches");
        await MirrorAsync(fx);
        Require(store.ReadRunHistory(fx.Pair.PairId, 10).Count(row => row.RunId.StartsWith("reconcile-", StringComparison.Ordinal)) == 1,
            "all reconciliation batches share one retained run identity");
    }

    private sealed class ReconciliationBarrierInventory(IInventorySource inner, Func<Task> beforeFull) : IInventorySource
    {
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
        { await beforeFull(); return await inner.FullAsync(gen, include, p, ct); }
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            inner.ChangesSinceAsync(cursor, gen, include, ct);
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.DeletionTimesAsync(paths, ct);
    }
}
