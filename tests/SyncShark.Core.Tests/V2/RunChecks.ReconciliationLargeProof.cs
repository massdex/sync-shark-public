namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationLargeProofConverges(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "large.txt", "old-content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        {
            WalkCheckpointForTest = () => (checkpoint, false), JournalReadForTest = NoJournalChanges,
            DiscoveryReadForTest = (_, _) => Task.FromResult<SyncDiscoveryResult?>(null),
            GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => false)
        };
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
            isStateDamage: StorageFailureClassifier.IsStateDamage));
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        await using var owner = new GapRecoveryRun(new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(10, 2) }, TimeProvider.System));
        await owner.Run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        await VerifyLargeProofRecovery(store, root, pair, inventory, checkpoint, owner.Run, fx.B, directory);
    }

    private static async Task VerifyLargeProofRecovery(SqliteStateStore store, RootDef root, PairDef pair,
        LocalInventory inventory, NtfsUsnCheckpoint checkpoint, SyncRun run, FakeEndpoint cloud, string directory)
    {
        var old = store.Live(root.RootId, ["large.txt"]).Single();
        LocalChecks.Seed(directory, old.Path, "new-content");
        File.SetLastWriteTimeUtc(Path.Combine(directory, old.Path), old.Mtime.UtcDateTime);
        store.RequireRehash(root.RootId, old, "Content was unreadable; verification required");
        await run.ReconcileScopeAsync(pair.PairId, StateMonitor.PlanningBacklog(), new RecordingProgress(), default);
        Require(store.ByObject(root.RootId, old.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash) &&
            (await cloud.ObserveAsync(old.Path, default))?.Hash == old.Hash &&
            store.UnresolvedNotices(pair.PairId).Any(notice => notice.Path == old.Path && notice.Detail.Length > 0),
            "a file larger than the entire batch cap retains proof debt and a visible item reason, never metadata settlement");
        await IndexRecoveredGapAsync(store, root, inventory, () => checkpoint);
        Require(!store.ByObject(root.RootId, old.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash),
            "independent indexing proves the oversized file and retires its rehash debt");
        await run.ReconcileScopeAsync(pair.PairId, StateMonitor.PlanningBacklog(), new RecordingProgress(), default);
        Require((await cloud.ObserveAsync(old.Path, default))?.Hash ==
            DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("new-content")) &&
            store.CountDeferredPlanning(pair.PairId) == 0 &&
            !store.UnresolvedNotices(pair.PairId).Any(notice => notice.Path == old.Path),
            "a later reconciliation converges oversized content after indexing and clears the item debt and notice");
    }
}
