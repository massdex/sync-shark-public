namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task CachedIdentityRetryAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "item.txt", "original");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        { WalkCheckpointForTest = () => (checkpoint, false), JournalReadForTest = NoJournalChanges,
            GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => true) };
        var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(snapshot.Gen, checkpoint.Cursor, true);
        var original = store.Live(root.RootId, ["item.txt"]).Single();
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
            isStateDamage: StorageFailureClassifier.IsStateDamage));
        await CachedIdentityPersistentAsync(fx, pair, root, inventory, store, original);
        File.Move(Path.Combine(directory, original.Path), Path.Combine(folder, "retained.txt"));
        LocalChecks.Seed(directory, original.Path, "replacement");
        store.RequestSyncRetryNow(pair.PairId, original.Path);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(store.Live(root.RootId, [original.Path]).Single().ObjectId != original.ObjectId,
            "an empty journal still consumes the durable guarded identity refresh");
        Require(!store.OpenNotices(pair.PairId).Any(n => n.Path == original.Path && n.ReasonKey == "sync-retry") &&
            store.SyncRetryAt(pair.PairId, original.Path) is not { Kind: "scan" } &&
            !store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey?.StartsWith(SqliteStateStore.IndexFailurePrefix) == true) &&
            File.ReadAllText(Path.Combine(directory, original.Path)) == "replacement",
            "real run retires only observed refresh debt without an indexing notice or source delete");

        // One more than the existing 256-scope admission cap: unvisited debt is neither
        // silently retired nor materialized as an artificial scan-failure notice.
        var current = store.Live(root.RootId, [original.Path]).Single();
        for (var i = 0; i < 257; i++)
            CachedIdentityRefresh.TryQueue(store, root, current with { Path = "queued-" + i }, "open-failed(87)");
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        var pending = store.ReadSyncRetries(pair.PairId).Where(CachedIdentityRefresh.Pending).ToArray();
        Require(pending.Length == 1 && store.RetryProtected(pair.PairId, pending[0].Path,
            new HashSet<string>(), descendants: false) && !store.OpenNotices(pair.PairId).Any(notice =>
            notice.Path.StartsWith("queued-", StringComparison.Ordinal)),
            "bounded rediscovery leaves the unvisited suffix durable and quiet");
    }
}
