namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task CachedIdentityReplayAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "item.txt", "original");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        { WalkCheckpointForTest = () => (checkpoint, false), JournalReadForTest = NoJournalChanges };
        var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(snapshot.Gen, checkpoint.Cursor, true);
        store.ForgetCursor(root.RootId);
        store.SaveInventoryObservation(InventoryCoverage.For(pair, Side.A, [pair], []), checkpoint.Cursor);
        var original = store.Live(root.RootId, ["item.txt"]).Single();
        CachedIdentityRefresh.TryQueue(store, root, original, 87);
        for (var i = 0; i < V2Settings.ScanRetryScopeLimit; i++)
            store.SaveSyncRetry(pair.PairId, new("a" + i.ToString("D3"), "scan", 1, "read pending", DateTimeOffset.MinValue,
                ReadRoots: [root.RootId]));
        var checkpoints = 0;
        var rereads = 0;
        inventory.WalkCheckpointForTest = () => { checkpoints++; return (checkpoint, false); };
        inventory.BeforeTargetedRefreshLeaseForTest = () => rereads++;
        fx.Provider.AddInventory(root, inventory);
        await fx.Run().PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), default);
        Require(checkpoints == 1 && rereads == 4, "first-sync replay admits four ordinary retry chunks, not a walk or the quiet suffix");
        Require(store.SyncRetryAt(pair.PairId, original.Path) is { } retry && CachedIdentityRefresh.Pending(retry) &&
            !store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "sync-retry"),
            "full planning coverage from journal replay does not escalate unvisited quiet work");
    }
}
