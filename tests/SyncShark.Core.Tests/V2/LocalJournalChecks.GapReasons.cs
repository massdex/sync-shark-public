using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task GapDiscoveryReasonChecks(string folder)
    {
        await GapReasonFailureChecks(folder);
        var product = Path.Combine(folder, "gap-reasons-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        const string cursor = "0000000000000001:200";
        string[] reasons = ["proved-unchanged", "usn>=boundary", "metadata-mismatch", "journal-mismatch", "open-failed(5)", "ioctl-failed(1)"];
        var rows = reasons.Select((reason, i) => new ItemState(reason, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)i + 10), null, new string('a', 64), 3,
            DateTimeOffset.UtcNow, ItemFlags.None)).ToArray();
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, cursor, 100, true);
            var epoch = gaps.GapEpoch(root.RootId)!;
            var io = new ReasonGapIo();
            await using (var worker = new LocalGapWorker(gaps, io)) worker.Discover(root, epoch, default);
            LocalChecks.Require(gaps.GapHashPage(root.RootId, epoch, 128).Count == 5 &&
                store.RehashWatermark(root.RootId) == 0 && !store.ProtectionBatch(root.RootId,
                    ItemFlags.NeedsRehash, [rows[0].Path])[0].Covered,
                "diagnostic categories do not change proof retirement or create foreground debt");
            // The independent hash lane may stage before the diagnostic update acquires the store gate.
            var candidate = rows[1];
            gaps.StageGapHash(root.RootId, epoch, candidate, new string('b', 64), io.Probe(root, candidate)!);
            LocalChecks.Require(gaps.GapHashPage(root.RootId, epoch, 128).Count == 4,
                "reason-update race fixture has a staged hash before diagnostics are persisted");
            gaps.SetGapDiscoveryReasons(root.RootId, epoch, [(candidate.ObjectId, "usn>=boundary")]);
        }
        using (var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
            LocalChecks.Require(((ILocalGapStore)reopened).HasGapProtection(root.RootId),
                "diagnostic persistence preserves unfinished protection across restart");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(product, "v2", "state.db") + ";Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT object_id,discovery_reason FROM local_gap_item ORDER BY object_id;";
        using var reader = command.ExecuteReader();
        var persisted = new Dictionary<string, string>();
        while (reader.Read()) persisted.Add(reader.GetString(0), reader.GetString(1));
        LocalChecks.Require(persisted.Count == 5 && rows.Skip(1).All(row => persisted[row.ObjectId] == row.Path),
            "each remaining entity retains its exact discovery category including native error after reopen and staging");
    }

    private static async Task GapReasonFailureChecks(string folder)
    {
        var product = Path.Combine(folder, "gap-reason-failure-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        const string cursor = "0000000000000001:200";
        var item = new ItemState("probe-exception", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 10),
            null, new string('a', 64), 3, DateTimeOffset.UtcNow, ItemFlags.None);
        using var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System);
        store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, [item], []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, cursor, 100, true);
        using var db = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(product) + ";Pooling=False");
        db.Open();
        // Test-owned fault: only the optional diagnostic write fails, not durable work/accounting.
        StoreSchema.Execute(db, null, "CREATE TRIGGER fail_gap_reason BEFORE UPDATE OF discovery_reason ON local_gap_item " +
            "BEGIN SELECT RAISE(ABORT,'synthetic diagnostic write failure'); END;");
        await using (var worker = new LocalGapWorker(gaps, new ReasonGapIo()))
            LocalChecks.Require(worker.Discover(root, gaps.GapEpoch(root.RootId)!, default),
                "optional diagnostic failure does not escape discovery");
        LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(db, null,
            "SELECT failure_count FROM local_gap_item;")) == 1 &&
            gaps.GapEpoch(root.RootId)!.DiscoveryDone && gaps.HasGapProtection(root.RootId),
            "poison accounting precedes diagnostics, discovery completes and the failed entity remains protected");
    }

    private sealed class ReasonGapIo : ILocalGapIo, ILocalGapProbe
    {
        public bool Paused => true;
        public string? LastFailureReason { get; private set; }
        public ILocalGapProbe OpenProbes(RootDef root) => this;
        public LocalGapProof? Probe(RootDef root, ItemState item) => Probe(item);
        public LocalGapProof? Probe(ItemState item)
        {
            LastFailureReason = null;
            if (item.Path == "probe-exception") throw new IOException("synthetic probe failure");
            if (item.Path.Contains("failed", StringComparison.Ordinal))
            {
                LastFailureReason = item.Path;
                return null;
            }
            var identity = LocalItems.ParseObjectId(item.ObjectId);
            return new(item.Path == "journal-mismatch" ? "0000000000000002:200" : "0000000000000001:200",
                new(identity.Volume, identity.FileId, item.Path == "usn>=boundary" ? 100 : 99,
                    item.Size + (item.Path == "metadata-mismatch" ? 1 : 0), item.Mtime.UtcDateTime.ToFileTimeUtc()));
        }
        public Task<(string Hash, LocalGapProof Proof)?> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Discovery must never read contents.");
        public void Dispose() { }
    }
}
