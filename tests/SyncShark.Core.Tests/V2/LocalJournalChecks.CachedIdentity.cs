namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task CachedIdentityAsync(string folder)
    {
        var rootPath = Path.Combine(folder, "root");
        LocalChecks.Seed(rootPath, "item.txt", "original");
        var root = LocalChecks.RootOf(rootPath, "root-local");
        var remote = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "id:remote");
        var pair = new PairDef("cached-pair", "Pair A", root, remote, [], [], true);
        var product = Path.Combine(folder, "store");
        var cursor = new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor;
        ItemState original;
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var inventory = LocalChecks.Inventory(rootPath, store, TimeProvider.System);
            var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
            store.WriteSnapshot(snapshot with { CursorAfter = cursor });
            original = store.Live(root.RootId, ["item.txt"]).Single();
            // Keep all fixture bytes. Model a program replacing a file, then supply the retired-ID
            // OpenFileById response at the probe boundary; no user data or fixture is hard-deleted.
            File.Move(Path.Combine(rootPath, original.Path), Path.Combine(folder, "retained-original.txt"));
            LocalChecks.Seed(rootPath, original.Path, "replacement");
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, cursor, 100, true);
            await using var worker = new LocalGapWorker(gaps, new RetiredIdProbe(), StorageFailureClassifier.IsStateDamage);
            worker.Discover(root, gaps.GapEpoch(root.RootId)!, default);
            LocalChecks.Require(store.OpenNotices(pair.PairId).Count == 0 &&
                store.SyncRetryAt(pair.PairId, original.Path) is { } retry && CachedIdentityRefresh.Pending(retry),
                "retired cached ID queues durable guarded rediscovery without indexing notice");
            LocalChecks.Require(gaps.HasGapProtection(root.RootId) && store.ByObject(root.RootId, original.ObjectId) is not null,
                "error 87 neither verifies nor removes the old entity");
            await using (var hashWorker = new LocalGapWorker(gaps, new ChangedIdentityHash(), StorageFailureClassifier.IsStateDamage))
                await hashWorker.HashPage(root, gaps.GapEpoch(root.RootId)!, default);
            LocalChecks.Require(store.OpenNotices(pair.PairId).Count == 0 && gaps.HasGapProtection(root.RootId),
                "content identity change is not a successful proof or an indexing failure");
            var id = LocalItems.ParseObjectId(original.ObjectId);
            var stagedProof = new LocalGapProof(cursor, new(id.Volume, id.FileId, 99, original.Size,
                original.Mtime.UtcDateTime.ToFileTimeUtc()));
            gaps.StageGapHash(root.RootId, gaps.GapEpoch(root.RootId)!, original, new string('a', 64), stagedProof);
            LocalGapWorker.Promote(gaps, store, new RetiredIdProbe(), root,
                gaps.GapResultWatermark(root.RootId), new NtfsUsnCheckpoint(1, 0, 200, 0),
                _ => true, _ => { }, default, StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(gaps.HasGapProtection(root.RootId) && store.OpenNotices(pair.PairId).Count == 0,
                "retired identity during staged-proof validation stays protected without a failure notice");
            // A failed guarded reread observes nothing and must keep both protection and retry.
            inventory.BeforeTargetedRefreshLeaseForTest = () => throw new IOException("synthetic root unavailable");
            var failed = await inventory.ChangesForPathsAsync([original.Path], cursor,
                store.BeginGeneration(root.RootId, "retry"), _ => true, default);
            LocalChecks.Require(failed.CursorInvalid && failed.Changes.Count == 0 &&
                store.SyncRetryAt(pair.PairId, original.Path) is not null, "failed rediscovery cannot become absence");
        }
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var retry = store.SyncRetryAt(pair.PairId, original.Path)!;
            LocalChecks.Require(CachedIdentityRefresh.Pending(retry), "rediscovery survives reopening the store");
            var inventory = LocalChecks.Inventory(rootPath, store, TimeProvider.System);
            var delta = await inventory.ChangesForPathsAsync([retry.Path], cursor,
                store.BeginGeneration(root.RootId, "retry"), _ => true, default);
            LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0 &&
                delta.Changes.All(change => change.Kind != ChangeKind.Delete), "replacement rediscovery never emits a delete");
            store.WriteDelta(delta);
            CachedIdentityRefresh.Observed(store, pair.PairId, retry.Path, root.RootId);
            var replacement = store.Live(root.RootId, [original.Path]).Single();
            LocalChecks.Require(replacement.ObjectId != original.ObjectId &&
                store.SyncRetryAt(pair.PairId, original.Path) is null && store.OpenNotices(pair.PairId).Count == 0 &&
                File.ReadAllText(Path.Combine(rootPath, original.Path)) == "replacement",
                "guarded inventory installs new identity with no notice or destructive effect");
            LocalChecks.Require(!CachedIdentityRefresh.TryQueue(store, root, replacement, "open-failed(5)") &&
                !CachedIdentityRefresh.TryQueue(store, root, replacement, "ioctl-failed(87)"),
                "access denied and non-open error 87 remain with the shared failure owner");
            var held = new SyncRetry(replacement.Path, "reviewed-removal", 2, "retain authority", DateTimeOffset.MaxValue);
            store.SaveSyncRetry(pair.PairId, held);
            CachedIdentityRefresh.TryQueue(store, root, replacement, "open-failed(87)");
            var preserved = store.SyncRetryAt(pair.PairId, replacement.Path)!;
            LocalChecks.Require(preserved.Kind == held.Kind && preserved.Attempts == held.Attempts &&
                preserved.RetryAfter == held.RetryAfter && preserved.LastError == held.LastError,
                "identity refresh never replaces reviewed authority or failure scheduling");
        }
        await RetiredNativeIdentityAsync(Path.Combine(folder, "native"));
        await ExecutorChecks.CachedIdentityDeleteRefusalAsync();
        await RunChecks.CachedIdentityRetryAsync(Path.Combine(folder, "run"));
        Console.WriteLine("PASS cached identity replacement, durable guarded rediscovery, and delete refusal");
    }

    private sealed class ChangedIdentityHash : ILocalGapIo
    {
        public bool Paused => false;
        public LocalGapProof? Probe(RootDef root, ItemState item) => null;
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.IdentityChanged, Reason: CachedIdentityRefresh.PendingReason));
    }

    private sealed class RetiredIdProbe : ILocalGapIo, ILocalGapProbe
    {
        public bool Paused => true;
        public string? LastFailureReason => "open-failed(87)";
        public ILocalGapProbe OpenProbes(RootDef root) => this;
        public LocalGapProof? Probe(RootDef root, ItemState item) => null;
        public LocalGapProof? Probe(ItemState item) => null;
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Paused discovery must not read contents.");
        public void Dispose() { }
    }
}
