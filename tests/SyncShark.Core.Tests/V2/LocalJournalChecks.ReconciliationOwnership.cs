namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task ReconciliationOwnsGapWorkAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var (root, _) = SaveIndexRoot(store, folder, "ownership-root", "ownership-pair", ["Parent/file.txt"]);
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(1, 0, 300, 0).Cursor, 100, false);
        var epoch = gaps.GapEpoch(root.RootId)!;
        var item = store.Live(root.RootId).Single();
        gaps.GapDiscoveredBatch(root.RootId, epoch, [(item, true)]);
        var before = gaps.GapEpoch(root.RootId);
        var io = new ForbiddenGapIo();
        await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
        var passes = 0;
        await using var indexing = new LocalIndexingRuns(store, (r, _) =>
        {
            passes++;
            return Task.FromResult(CompleteIndexWork(store, r.RootId));
        }, worker, io, _ => throw new InvalidOperationException("rehash lane must not read a gap checkpoint"));
        LocalChecks.Require(await indexing.RunAsync(root, default) == "complete" && passes == 1 &&
            store.RehashPendingCount(root.RootId) == 0,
            "independent indexing still verifies and publishes explicit rehash debt alongside a legacy gap");
        LocalChecks.Require(gaps.GapEpoch(root.RootId) == before && gaps.HasGapProtection(root.RootId) &&
            gaps.GapObligations(root.RootId, [item.ObjectId]).Count == 1 && io.Calls == 0,
            "indexing does not discover, hash, promote or acknowledge reconciliation-owned gap work");
        LocalChecks.Require(await indexing.RunAsync(root, default) == "complete" && passes == 1,
            "gap-only debt cannot schedule another independent indexing pass");
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
