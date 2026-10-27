namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationDecisionBoundsAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("decision-byte-bound"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("oversized.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.InventoryWithoutHashes = true;
        fx.A.HashedPaths.Clear();
        store.RequireRehash(fx.Pair.A.RootId, item, "proof needed");
        var batch = BatchFor(item);
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(128, 2) }, TimeProvider.System);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default, new RunOptions { Batch = batch });
        Require(fx.A.HashedPaths.Count == 0 && batch.DecisionBytes == 0 &&
            store.ByObject(fx.Pair.A.RootId, item.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash),
            "oversized first comparison/proof request reads no content and retains indexing debt");
        var concurrent = BatchFor(item);
        concurrent.DecisionByteLimit = 1024;
        await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(() =>
            concurrent.AdmitDecisionHash(item with { Size = 31 }))));
        Require(concurrent.DecisionBytes == 1023,
            "both planner sides and retries share atomic reservations without exceeding the batch cap");
        Require(ReconcileBatch.Current.Value is null, "batch decision scope does not escape the admitted run");
    }

    private static async Task ReconciliationExactLocalFolderAsync()
    {
        var directory = FixtureFolder("exact-local-folder");
        LocalChecks.Seed(directory, "Parent/Child/item.txt", "original");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(directory, known, TimeProvider.System);
        var full = await inventory.FullAsync(1, _ => true, SilentProgress, default);
        known.WriteSnapshot(full);
        LocalChecks.Seed(directory, "Parent/Child/item.txt", "changed contents");
        var exact = await inventory.ChangesForPathsAsync(["Parent"], full.CursorAfter, 2, _ => true, default, exact: true);
        Require(!exact.CursorInvalid && exact.IncompleteSubtrees.Count == 0 && exact.Changes.Count > 0 &&
            exact.Changes.All(change => change.Path == "Parent"),
            "an exact local folder refresh enumerates and publishes no descendant rows");
        var recursive = await inventory.ChangesForPathsAsync(["Parent"], full.CursorAfter, 3, _ => true, default);
        Require(recursive.Changes.Any(change => change.Path == "Parent/Child/item.txt"),
            "the existing scan-retry folder refresh retains recursive semantics");
    }
}
