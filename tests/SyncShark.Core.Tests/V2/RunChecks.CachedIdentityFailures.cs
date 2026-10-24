namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task CachedIdentityPersistentAsync(Fixture fx, PairDef pair, RootDef root,
        LocalInventory inventory, SqliteStateStore store, ItemState original)
    {
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, store.Cursor(root.RootId)!, 100, true);
        var io = new CachedOpenFailureIo(store);
        await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
        worker.Discover(root, gaps.GapEpoch(root.RootId)!, default);
        await worker.HashPage(root, gaps.GapEpoch(root.RootId)!, default);
        Require(io.HashCalls == 1 && CachedIdentityRefresh.Pending(store.SyncRetryAt(pair.PairId, original.Path)!),
            "discovery and hashing, not direct queue calls, persist the identity refresh");
        Require(store.GapInventoryPending(root.RootId), "persistent-87 fixture must take full gap recovery");
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        var notices = store.OpenNotices(pair.PairId).Where(n => n.Path == original.Path && n.ReasonKey == "sync-retry").ToArray();
        Require(notices.Length == 1, "full gap recovery must produce exactly one persistent-87 retry notice; found " + notices.Length);
        var notice = notices[0];
        Require(store.SyncRetryAt(pair.PairId, original.Path) is { Attempts: 1, LastError: "open-failed(87)" } &&
            notice.Detail.Contains("87") && notice.Detail.Contains("Retry") && notice.Detail.Contains("Ignore") &&
            store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [original.Path])[0].Covered,
            "unchanged identity raises one actionable notice and retains the unsynced hold");
        store.RequestSyncRetryNow(pair.PairId, original.Path);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(store.OpenNotices(pair.PairId).Count(n => n.Path == original.Path && n.ReasonKey == "sync-retry") == 1 &&
            store.OpenNotices(pair.PairId).Any(n => n.NoticeId == notice.NoticeId),
            "persistent 87 across due rereads keeps the same deduplicated notice");
        await CachedIdentityProofRecoversAsync(fx, pair, root, store, original);
        // A later cached open supplies fresh quiet work for the failed-reread check.
        await io.HashAsync(root, store.ByObject(root.RootId, original.ObjectId)!, default);
        inventory.BeforeTargetedRefreshLeaseForTest = () => throw new IOException("synthetic root unavailable");
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        inventory.BeforeTargetedRefreshLeaseForTest = null;
        Require(store.SyncRetryAt(pair.PairId, original.Path) is { Attempts: 1 } retry && !CachedIdentityRefresh.Pending(retry) &&
            store.OpenNotices(pair.PairId).Count(n => n.Path == original.Path && n.ReasonKey == "sync-retry") == 1,
            "failed guarded reread must notify, not merely retain the quiet row");
    }

    private static async Task CachedIdentityProofRecoversAsync(Fixture fx, PairDef pair, RootDef root,
        SqliteStateStore store, ItemState original)
    {
        var gaps = (ILocalGapStore)store;
        var id = LocalItems.ParseObjectId(original.ObjectId);
        var proof = new LocalGapProof(store.Cursor(root.RootId)!, new(id.Volume, id.FileId, 99,
            original.Size, original.Mtime.UtcDateTime.ToFileTimeUtc()));
        var hash = DropboxContentHash.Compute(File.ReadAllBytes(Path.Combine(root.RootPath, original.Path)));
        gaps.StageGapHash(root.RootId, gaps.GapEpoch(root.RootId)!, original, hash, proof);
        var result = gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 1).Single();
        Require(gaps.AcceptGapResult(root.RootId, result), "later valid content proof settles the original identity");
        store.RequestSyncRetryNow(pair.PairId, original.Path);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(store.ByObject(root.RootId, original.ObjectId) is not null &&
            store.SyncRetryAt(pair.PairId, original.Path) is null &&
            !store.OpenNotices(pair.PairId).Any(n => n.Path == original.Path && n.ReasonKey == "sync-retry"),
            "later successful verification of the same identity closes its notice and retry");
    }

    private sealed class CachedOpenFailureIo(IStateStore store) : ILocalGapIo, ILocalGapProbe
    {
        public int HashCalls { get; private set; }
        public bool Paused => false;
        public string? LastFailureReason => CachedIdentityRefresh.OpenReason;
        public int? LastOpenError => 87;
        public ILocalGapProbe OpenProbes(RootDef root) => this;
        public LocalGapProof? Probe(RootDef root, ItemState item) => null;
        public LocalGapProof? Probe(ItemState item) => null;
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct)
        {
            HashCalls++;
            return Task.FromResult(CachedIdentityRefresh.OpenFailure(store, root, item, 87));
        }
        public void Dispose() { }
    }

    internal static async Task CachedIdentityFairnessAsync(string folder)
    {
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("real.txt", "before");
        var clock = new CompletionClock { Now = DateTimeOffset.UtcNow };
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        await FirstSyncAsync(run, fx);
        fx.A.Seed("real.txt", "after");
        fx.B.FailNextWrites = 1;
        Require((await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default)).Failed == 1,
            "fixture creates a real failed transfer");
        var retry = store.SyncRetryAt(fx.Pair.PairId, "real.txt")!;
        store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = clock.Now.AddSeconds(-1) });
        var item = store.Live(fx.Pair.A.RootId, ["real.txt"]).Single();
        for (var i = 0; i < 6900; i++)
            CachedIdentityRefresh.TryQueue(store, fx.Pair.A, item with { Path = "quiet-" + i }, 87);
        clock.Now = DateTimeOffset.UtcNow.AddSeconds(1);
        Require(store.DueSyncRetries(fx.Pair.PairId, clock.Now, 256).First().Path == "real.txt",
            "6900 due quiet rows do not displace the older real retry");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Done == 1 && store.SyncRetryAt(fx.Pair.PairId, "real.txt") is null,
            "real failed retry runs and settles with 6900 quiet rows due");
        await MirrorAsync(fx);
    }
}
