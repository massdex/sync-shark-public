namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DurableGapCompletionAsync(bool interrupt = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "durable-completion-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        var data = Path.Combine(folder, "store");
        Fixture fx;
        RootDef root;
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            LocalChecks.Seed(directory, "anchor.txt", "same");
            fx.B.Seed("anchor.txt", "same");
            root = LocalChecks.RootOf(directory, "ra");
            store.SavePair(fx.Pair with { A = root, IgnoreRules = ["FolderName:cache"] });
            var inventory = DurableInventory(root, store, checkpoint, () => { });
            fx.Provider.AddInventory(root, inventory);
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, fx.Pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default, isStateDamage: StorageFailureClassifier.IsStateDamage));
            await FirstSyncAsync(fx.Run(), fx);
            ((ILocalGapStore)store).BeginGap(root.RootId, checkpoint.Cursor, 100, true);
            Require(store.GapInventoryPending(root.RootId), "fixture BeginGap creates a durable inventory obligation");
            store.SaveSyncRetry(fx.Pair.PairId, new("", "run", 1, "prior completion debt", DateTimeOffset.MinValue));
            fx.B.Seed("download.txt", "durable content");
            var run = interrupt
                ? new SyncRun(store, new FailAfterApply(NewExecutor(store)), fx.Provider, Settings, TimeProvider.System)
                : fx.Run();
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(report.Done == 1 && report.Downloaded == 1 && report.CompletionFailed == interrupt &&
                store.GapInventoryPending(root.RootId) == interrupt &&
                (store.SyncRetryAt(fx.Pair.PairId, "") is not null) == interrupt,
                "SQLite root run retry preserves counted effects and settles inventory_pending unless interrupted");
        }
        using (var reopened = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var walks = 0;
            fx.Provider.AddInventory(root, DurableInventory(root, reopened, checkpoint, () => walks++));
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, fx.Pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default, isStateDamage: StorageFailureClassifier.IsStateDamage));
            var run = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, Settings, TimeProvider.System);
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(report.Downloaded == 0 && report.Uploaded == 0 && !report.CompletionFailed && walks <= (interrupt ? 1 : 0) &&
                !reopened.GapInventoryPending(root.RootId),
                "cold next run never repeats copies and settles after at most one required local walk");
            walks = 0;
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(walks == 0 && !reopened.GapInventoryPending(root.RootId),
                "subsequent run cannot repeat the settled full walk");
        }
    }

    private sealed class FailAfterApply(IExecutor inner) : IExecutor
    {
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var result in inner.ApplyAsync(plan, a, b, ct)) yield return result;
            throw new IOException("injected interruption after apply before gap settlement");
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(intents, gen, a, b, ct, progress);
    }

    private static LocalInventory DurableInventory(RootDef root, SqliteStateStore store,
        NtfsUsnCheckpoint checkpoint, Action walked)
    {
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage, pairId: "p1")
        {
            WalkCheckpointForTest = () => (checkpoint, false),
            DirectoryCompletedForTest = _ => walked()
        };
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        { changes = []; failure = null; kind = default; return true; };
        return inventory;
    }
}
