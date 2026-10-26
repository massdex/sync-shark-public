namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task ReconciliationOwnsGapWorkAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var (root, inventory, items) = await SaveOwnershipRootAsync(store, folder);
        var independent = items.Single(row => row.Path == "independent.txt");
        var item = items.Single(row => row.Path == "gap.txt");
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(1, 0, 300, 0).Cursor, 100, false);
        var epoch = gaps.GapEpoch(root.RootId)!;
        gaps.GapDiscoveredBatch(root.RootId, epoch, [(item, true)]);
        var before = gaps.GapEpoch(root.RootId);
        var io = new ForbiddenGapIo();
        await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
        var passes = 0;
        await using var indexing = new LocalIndexingRuns(store, (_, ct) =>
        {
            passes++;
            return inventory.VerifyIndexingPassAsync(ct);
        }, worker, io, _ => throw new InvalidOperationException("rehash lane must not read a gap checkpoint"));
        var pendingBefore = store.RehashPendingCount(root.RootId);
        LocalChecks.Require(await indexing.RunAsync(root, default) == "pending" && passes == 1 &&
            pendingBefore == 2 && store.RehashPendingCount(root.RootId) == pendingBefore - 1 &&
            store.ByObject(root.RootId, independent.ObjectId) is { Hash: not null } verified &&
            verified.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("content")) &&
            !verified.Flags.HasFlag(ItemFlags.NeedsRehash) && store.IndexPlanningWatermark(root.RootId) > 0,
            "independent indexing verifies and publishes only its own item while gap-owned debt remains pending");
        LocalChecks.Require(gaps.GapEpoch(root.RootId) == before && gaps.HasGapProtection(root.RootId) &&
            gaps.GapObligations(root.RootId, [item.ObjectId]).Count == 1 && io.Calls == 0,
            "indexing does not discover, hash, promote or acknowledge reconciliation-owned gap work");
        LocalChecks.Require(await indexing.RunAsync(root, default) == "pending" &&
            store.RehashPendingCount(root.RootId) == 1 &&
            store.ByObject(root.RootId, item.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash) && io.Calls == 0,
            "indexing never reports convergence or consumes the remaining gap-owned debt");
        await RunChecks.CompleteOwnershipReconciliationAsync(store, root, inventory, item, epoch, folder);
    }

    private static async Task<(RootDef Root, LocalInventory Inventory, ItemState[] Items)> SaveOwnershipRootAsync(
        SqliteStateStore store, string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "independent.txt", "content");
        LocalChecks.Seed(directory, "gap.txt", "content");
        var root = LocalChecks.RootOf(directory, "ownership-root");
        store.SavePair(new("ownership-pair", "Pair A", root,
            new("ownership-remote", SyncProviders.Dropbox, "account", "/Other", "id:other"), [], [], true));
        store.MarkFirstSyncDone("ownership-pair");
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 300, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast,
            TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage)
        {
            WalkCheckpointForTest = () => (checkpoint, false),
            DiscoveryReadForTest = (_, _) => Task.FromResult<SyncDiscoveryResult?>(null),
            GapIo = new LocalGapIo(store, StorageChecks.Fast, new HashSet<string>(), () => false)
                { CheckpointForCheck = _ => checkpoint }
        };
        var gen = store.BeginGeneration(root.RootId, "full");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, checkpoint.Cursor, true);
        var items = store.Live(root.RootId).ToArray();
        store.RequireRehashBatch(root.RootId, items, "content verification pending");
        return (root, inventory, items);
    }

    private sealed class ForbiddenGapIo : ILocalGapIo
    {
        internal int Calls { get; private set; }
        public bool Paused => false;
        public bool Excluded(RootDef root, ItemState item) { Calls++; return false; }
        public LocalGapProof? Probe(RootDef root, ItemState item)
        { Calls++; throw new InvalidOperationException("unexpected gap discovery"); }
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("unexpected gap hashing"); }
    }

    private static async Task FullWalkDoesNotCreateGapAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "file.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = LocalChecks.RootOf(directory, "walk-root");
        store.SavePair(new("walk-pair", "Pair A", root,
            new("walk-remote", SyncProviders.Dropbox, "account", "/Other", "id:other"), [], [], true));
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast,
            TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage)
        { WalkCheckpointForTest = () => (checkpoint, false) };
        var generation = store.BeginGeneration(root.RootId, "full");
        store.WriteSnapshot(await inventory.FullAsync(generation, _ => true, null!, default));
        store.CompleteGeneration(generation, checkpoint.Cursor, true);
        // A full walk is an observation, not a second owner that manufactures legacy proof debt.
        inventory.WalkCheckpointForTest = () => (null, true);
        generation = store.BeginGeneration(root.RootId, "full");
        var snapshot = await inventory.FullAsync(generation, _ => true, null!, default);
        LocalChecks.Require(snapshot.Items.Count == 1 && ((ILocalGapStore)store).GapEpoch(root.RootId) is null,
            "a full walk after journal loss does not manufacture a legacy gap epoch");
    }
}
