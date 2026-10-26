namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static void CachedIdentityReviewChecks(string folder)
    {
        var quiet = new SyncRetry("item", "scan", 0, CachedIdentityRefresh.PendingReason, DateTimeOffset.UtcNow);
        var specific = quiet with { LastError = "Access denied on the folder" };
        foreach (var reason in new string?[] { null, "", " " })
        {
            LocalChecks.Require(CachedIdentityRefresh.RereadFailure(quiet, reason) == CachedIdentityRefresh.RereadReason,
                "a blank CursorInvalid or incomplete reason converts attempted quiet work into a failure");
            LocalChecks.Require(CachedIdentityRefresh.RereadFailure(specific, reason) is null,
                "a missing detail preserves the previous specific failure reason");
        }
        LocalChecks.Require(CachedIdentityRefresh.RereadFailure(quiet, "specific failure") == "specific failure",
            "real reread diagnostics always take precedence");
        CachedPromotionProgress(folder);
        CachedRehearsalReason(folder);
    }

    private static void CachedPromotionProgress(string folder)
    {
        using var store = SqliteStateStore.Open(Path.Combine(folder, "staged-store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = new RootDef("local", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        store.SavePair(new PairDef("pair", "Pair A", root, root, [], [], true));
        var cp = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var rows = Enumerable.Range(0, 513).Select(i => new ItemState("item-" + i, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)i + 2), null, new string('a', 64), 3,
            DateTimeOffset.UtcNow, ItemFlags.None)).ToArray();
        store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cp.Cursor, rows, []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, cp.Cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        var io = new CachedPromotionIo(rows[^1].ObjectId, cp.Cursor);
        foreach (var row in rows)
        {
            gaps.GapDiscovered(root.RootId, epoch, row, true);
            gaps.StageGapHash(root.RootId, epoch, row, new string('b', 64), io.Proof(row));
        }
        gaps.GapDiscoveryDone(root.RootId, epoch);
        var originalWatermark = gaps.GapResultWatermark(root.RootId);
        for (var pass = 0; pass < 2; pass++)
            LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId), cp,
                _ => true, _ => { }, default, StorageFailureClassifier.IsStateDamage);
        LocalChecks.Require(store.ByObject(root.RootId, rows[^1].ObjectId)!.Hash == new string('b', 64) &&
            gaps.GapResults(root.RootId, originalWatermark, 128).Count == 0 &&
            gaps.HasGapProtection(root.RootId) && store.OpenNotices("pair").Count == 0,
            "512 stale staged results leave the head, allowing the newer valid result to promote quietly");
    }

    private static void CachedRehearsalReason(string folder)
    {
        using var overlay = new SqliteRehearsalOverlay(Path.Combine(folder, "overlay.db"));
        var store = new InMemoryStateStore();
        var root = new RootDef("local", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        var item = new ItemState("real", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 2), null, null, 1,
            DateTimeOffset.UtcNow, ItemFlags.None);
        overlay.Save(root.RootId, "virtual", null);
        using var probes = new RehearsalGapIo(new RetiredIdProbe(), overlay, store).OpenProbes(root);
        LocalChecks.Require(probes.Probe(item) is null && probes.LastOpenError == 87,
            "real rehearsal failure carries its native open code");
        LocalChecks.Require(probes.Probe(item with { Path = "virtual" }) is null &&
            probes.LastFailureReason is null && probes.LastOpenError is null,
            "virtual mismatch never inherits the previous item's native failure");
    }

    private sealed class CachedPromotionIo(string validId, string cursor) : ILocalGapIo, ILocalGapProbe
    {
        public bool Paused => false;
        public int? LastOpenError { get; private set; }
        public ILocalGapProbe OpenProbes(RootDef root) => this;
        public LocalGapProof Proof(ItemState item)
        {
            var id = LocalItems.ParseObjectId(item.ObjectId);
            return new(cursor, new(id.Volume, id.FileId, 99, item.Size, item.Mtime.UtcDateTime.ToFileTimeUtc()));
        }
        public LocalGapProof? Probe(ItemState item)
        {
            LastOpenError = item.ObjectId == validId ? null : 87;
            return LastOpenError is null ? Proof(item) : null;
        }
        public LocalGapProof? Probe(RootDef root, ItemState item) => Probe(item);
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Promotion must not hash contents.");
        public void Dispose() { }
    }
}
