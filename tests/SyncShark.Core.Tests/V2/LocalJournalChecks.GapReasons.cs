using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task GapDiscoveryReasonChecks(string folder)
    {
        await GapReasonFailureChecks(folder);
        await GapNullHashFailureChecks(folder);
        await GapShutdownResumeChecks(folder);
        var product = Path.Combine(folder, "gap-reasons-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        const string cursor = "0000000000000001:200";
        string[] reasons = ["proved-unchanged", "usn>=boundary", "metadata-mismatch", "journal-mismatch", "open-failed(87)", "ioctl-failed(1)"];
        var rows = reasons.Select((reason, i) => new ItemState(reason, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)i + 10), null, new string('a', 64), 3,
            DateTimeOffset.UtcNow, ItemFlags.None)).ToArray();
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var remote = new RootDef("probe-remote", SyncProviders.Dropbox, "account", "/remote", "id:remote");
            var pair = new PairDef("probe-pair", "Pair A", root, remote, [], [], true);
            store.SavePair(pair);
            store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, cursor, 100, true);
            var epoch = gaps.GapEpoch(root.RootId)!;
            var io = new ReasonGapIo();
            await using (var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage)) worker.Discover(root, epoch, default);
            LocalChecks.Require(store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [rows[0].Path])[0].Covered,
                "diagnostic unchanged proof remains protected until next run");
            gaps.PromoteGapDiscovery(root.RootId);
            LocalChecks.Require(gaps.GapHashPage(root.RootId, epoch, 128).Count == 3 &&
                store.OpenNotices(pair.PairId).Count == 1 && gaps.GapProgress(root.RootId).Contains("open-failed(87)") &&
                store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, rows.Skip(4).Select(row => row.Path).ToArray()).All(hit => hit.Covered),
                "paused hashing still exposes one root notice and exact native probe reason; failed entities stay protected");
            store.RequestIndexRetry(root.RootId);
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
        using (var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
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
        using var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, [item], []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, cursor, 100, true);
        using var db = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(product) + ";Pooling=False");
        db.Open();
        // Test-owned fault: only the optional diagnostic write fails, not durable work/accounting.
        StoreSchema.Execute(db, null, "CREATE TRIGGER fail_gap_reason BEFORE UPDATE OF discovery_reason ON local_gap_item " +
            "BEGIN SELECT RAISE(ABORT,'synthetic diagnostic write failure'); END;");
        await using (var worker = new LocalGapWorker(gaps, new ReasonGapIo(), StorageFailureClassifier.IsStateDamage))
            LocalChecks.Require(worker.Discover(root, gaps.GapEpoch(root.RootId)!, default),
                "optional diagnostic failure does not escape discovery");
        LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(db, null,
            "SELECT failure_count FROM local_gap_item;")) == 1 &&
            gaps.GapEpoch(root.RootId)!.DiscoveryDone && gaps.HasGapProtection(root.RootId),
            "poison accounting precedes diagnostics, discovery completes and the failed entity remains protected");
    }

    private static async Task GapNullHashFailureChecks(string folder)
    {
        var product = Path.Combine(folder, "gap-null-hash-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        const string cursor = "0000000000000001:200";
        var item = new ItemState("offline-file", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 10),
            null, new string('a', 64), 3, DateTimeOffset.UtcNow, ItemFlags.None);
        using var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor,
            [item, item with { Path = "directory", ObjectId = SyncLocalEndpointStore.ObjectId(1, 11), Kind = ItemKind.Folder },
             item with { Path = "link", ObjectId = SyncLocalEndpointStore.ObjectId(1, 12), Flags = ItemFlags.Link }], []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        var io = new NullGapIo();
        await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
        worker.Discover(root, epoch, default);
        gaps.PromoteGapDiscovery(root.RootId);
        LocalChecks.Require(gaps.GapHashPage(root.RootId, epoch, 16).Count == 1,
            "directories and reparse links never enter the content hash queue");
        foreach (var outcome in new[] { LocalGapHashOutcome.Paused, LocalGapHashOutcome.Disabled })
        {
            io.Outcome = outcome; // Live Paused is already false when the result reaches the worker.
            await worker.HashPage(root, epoch, default);
            using var db = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(product) + ";Pooling=False");
            db.Open();
            LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(db, null,
                "SELECT failure_count FROM local_gap_item;")) == 0 && gaps.HasGapProtection(root.RootId),
                "IO-owned pause/disabled outcome is not reinterpreted as failure after pause ends: " + outcome);
        }
        var disabled = new LocalGapIo(store, StorageChecks.Fast with { LocalGapBytesPerSecond = 0 }, new HashSet<string>(), () => false);
        LocalChecks.Require((await disabled.HashAsync(root, item, default)).Outcome == LocalGapHashOutcome.Disabled,
            "production IO reports disabled byte rate without trying a file proof");
        io.Outcome = LocalGapHashOutcome.Unverifiable;
        var beforeFailures = io.Reads;
        for (var attempt = 0; attempt < 10; attempt++) await worker.HashPage(root, epoch, default);
        LocalChecks.Require(io.Reads > beforeFailures && io.Reads < beforeFailures + 10 && gaps.GapHashPage(root.RootId, epoch, 16).Count == 0 &&
            gaps.HasGapProtection(root.RootId) && store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [item.Path])[0].Covered &&
            gaps.GapProgress(root.RootId).Contains("make offline/cloud content available", StringComparison.Ordinal),
            "unsupported proof has bounded attempts, explains retry, and retains path protection");
        var beforeRetry = io.Reads;
        io.Reason = "open-failed(87)";
        gaps.RetryGapFailures(root.RootId);
        await worker.HashPage(root, epoch, default);
        await worker.HashPage(root, epoch, default);
        LocalChecks.Require(io.Reads > beforeRetry && io.Reads <= beforeRetry + 2 && gaps.HasGapProtection(root.RootId),
            "explicit next-run retry allows one new attempt without reopening a hot loop");
        LocalChecks.Require(gaps.GapProgress(root.RootId).Contains("open-failed(87)") &&
            store.ByObject(root.RootId, item.ObjectId) is not null,
            "hash failure retains the IO-owned native reason and never interprets 87 as deletion");
    }

    private static async Task GapShutdownResumeChecks(string folder)
    {
        foreach (var count in new[] { 40, 270 })
            await GapShutdownResumeCase(folder, count);
    }

    private static async Task GapShutdownResumeCase(string folder, int count)
    {
        var product = Path.Combine(folder, "gap-shutdown-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        const string cursor = "0000000000000001:200";
        var rows = Enumerable.Range(10, count).Select(n => new ItemState("file-" + n, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)n), null, new string('a', 64), 3,
            DateTimeOffset.UtcNow, ItemFlags.None)).OrderBy(x => x.ObjectId, StringComparer.Ordinal).ToArray();
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, cursor, 100, true);
            using var io = new ShutdownGapIo();
            var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
            worker.Start(root);
            try
            {
                await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var shutdown = worker.DisposeAsync().AsTask(); // Cancels while probe seven is in flight.
                io.Release.Set();
                await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
                LocalChecks.Require(io.Seen.Count == 7 && gaps.GapEpoch(root.RootId) is { DiscoveryDone: false } epoch &&
                    epoch.AfterObject == rows[6].ObjectId,
                    "shutdown joins only in-flight work and persists the partial-page frontier");
            }
            finally { io.Release.Set(); await worker.DisposeAsync(); }
        }
        using (var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var gaps = (ILocalGapStore)reopened;
            gaps.PromoteGapDiscovery(root.RootId);
            LocalChecks.Require(gaps.HasGapProtection(root.RootId) && reopened.ProtectionBatch(root.RootId,
                ItemFlags.NeedsRehash, [rows[7].Path, rows[^1].Path]).All(x => x.Covered),
                "reopen and promotion preserve protection for the whole unvisited suffix");
            using var io = new ShutdownGapIo { Block = false };
            await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
            while (!gaps.GapEpoch(root.RootId)!.DiscoveryDone)
                worker.Discover(root, gaps.GapEpoch(root.RootId)!, default);
            LocalChecks.Require(io.Seen.SequenceEqual(rows.Skip(7).Select(x => x.ObjectId)),
                "next run resumes exactly after durable frontier without repeating processed items or pages");
            gaps.PromoteGapDiscovery(root.RootId);
            LocalChecks.Require(!gaps.HasGapProtection(root.RootId) && reopened.ProtectionBatch(root.RootId,
                ItemFlags.NeedsRehash, [rows[^1].Path]).All(hit => !hit.Covered),
                "one invocation drains the entire proved suffix and releases its protection");
        }
    }

    private sealed class ShutdownGapIo : ILocalGapIo, IDisposable
    {
        public bool Block = true;
        public List<string> Seen { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool Paused => true;
        public LocalGapProof? Probe(RootDef root, ItemState item)
        {
            Seen.Add(item.ObjectId);
            if (Block && Seen.Count == 7)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture probe release missing");
            }
            var id = LocalItems.ParseObjectId(item.ObjectId);
            return new("0000000000000001:200", new(id.Volume, id.FileId, 99, item.Size, item.Mtime.UtcDateTime.ToFileTimeUtc()));
        }
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Paused hash lane must not read contents.");
        public void Dispose() => Release.Dispose();
    }

    private sealed class NullGapIo : ILocalGapIo, ILocalGapProbe
    {
        public LocalGapHashOutcome Outcome = LocalGapHashOutcome.Unverifiable;
        public int Reads;
        public string? Reason;
        public bool Paused => false;
        public string? LastFailureReason => "metadata-unsupported";
        public ILocalGapProbe OpenProbes(RootDef root) => this;
        public LocalGapProof? Probe(RootDef root, ItemState item) => Probe(item);
        public LocalGapProof? Probe(ItemState item)
        {
            var identity = LocalItems.ParseObjectId(item.ObjectId);
            return new(new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor,
                new(identity.Volume, identity.FileId, 100, item.Size, item.Mtime.UtcDateTime.ToFileTimeUtc()));
        }
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new LocalGapHashResult(Outcome, Reason: Reason));
        }
        public void Dispose() { }
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
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Discovery must never read contents.");
        public void Dispose() { }
    }
}
