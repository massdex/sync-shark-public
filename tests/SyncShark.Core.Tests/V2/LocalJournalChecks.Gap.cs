namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task GapStoreAsync(string folder)
    {
        GapSchemaUpgradeChecks(folder);
        await SavedCursorWrapAsync(folder);
        await GapDiscoveryReasonChecks(folder);
        ProtectionBatchChecks.Run(Path.Combine(folder, "protection-" + Guid.NewGuid().ToString("N")));
        var product = Path.Combine(folder, "gap-" + Guid.NewGuid().ToString("N"));
        var stamp = DateTimeOffset.UtcNow;
        var oldHash = new string('a', 64); var newHash = new string('b', 64);
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        var rows = Enumerable.Range(1, 3).Select(n => new ItemState("folder/" + n, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)n + 10), null, oldHash, 3, stamp, ItemFlags.None)).ToArray();
        var alias = rows[2] with { Path = "alias/3" };
        const string cursor = "0000000000000001:200";
        LocalGapResult stale;
        long watermark;
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, [.. rows, alias], []));
            GapPerFileProofChecks(store, root, rows, stamp, oldHash, newHash);
            await GapReviewChecks(store, root, rows, stamp, oldHash, newHash);
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, cursor, 100, true);
            var epoch = gaps.GapEpoch(root.RootId)!;
            LocalChecks.Require(store.ProtectionIndex(root.RootId, ItemFlags.NeedsRehash) is null,
                "gap uses lazy bounded protection without materializing unvisited root");
            var held = store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, ["folder", rows[0].Path, "", "other"]);
            LocalChecks.Require(held[0].Contains && held[1].Covered && held[2].Contains && !held[3].Covered,
                "unvisited gap rows protect ancestors root and self only");
            var io = new GapCheckIo(stamp, cursor);
            await using (var worker = new LocalGapWorker(gaps, io))
            {
                io.IsPaused = true;
                worker.Discover(root, epoch, default);
                io.IsPaused = false;
                LocalChecks.Require(io.Probes == 3 && io.Reads == 0, "trusted discovery probes metadata only");
            }
            LocalChecks.Require(gaps.GapEpoch(root.RootId)!.DiscoveryDone && store.RehashWatermark(root.RootId) == 0,
                "gap discovery is durable and does not feed foreground debt");
            LocalChecks.Require(store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [""])[0].Contains,
                "discovered gap root containment is driven by remaining entity obligations");
            LocalChecks.Require(store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [rows[0].Path])[0].Covered,
                "unchanged metadata discovery stays protected until next-run promotion");
            gaps.PromoteGapDiscovery(root.RootId);
            var summary = new RehashProtection(store, new PairDef("gap-pair", "gap", root, root, [], [], true)).Summary("gap-pair");
            LocalChecks.Require(summary.Detail.Contains("hashed 0/2") && summary.Detail.Contains("Options:") &&
                summary.Detail.Contains("Sync Now"), "gap notice reports honest queue progress and existing-framework options");
            held = store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [rows[0].Path, rows[1].Path, alias.Path]);
            LocalChecks.Require(!held[0].Covered && held[1].Covered && held[2].Covered,
                "unchanged file retires progressively while boundary-equal and unknown files remain held");
            var cutoff = gaps.GapResultWatermark(root.RootId);
            var page = gaps.GapHashPage(root.RootId, epoch, 128);
            LocalChecks.Require(page.Count == 2, "candidate rows are entity-owned across aliases");
            foreach (var item in page) gaps.StageGapHash(root.RootId, epoch, item, newHash, io.Proof(item, 100));
            LocalChecks.Require(gaps.GapResults(root.RootId, cutoff, 128).Count == 0 &&
                store.Live(root.RootId).All(item => item.Hash == oldHash), "new staged results wait for later run and never mutate live state");
            watermark = gaps.GapResultWatermark(root.RootId);
            var results = gaps.GapResults(root.RootId, watermark, 128);
            stale = results.Single(result => result.Expected.ObjectId == rows[1].ObjectId);
            store.RequireRehash(root.RootId, rows[1], "fresh ordinary write");
            LocalChecks.Require(!gaps.AcceptGapResult(root.RootId, stale) &&
                gaps.GapResults(root.RootId, watermark, 128).Count == 1,
                "fresh ordinary write invalidates a queued gap result even before promotion");
            var completed = results.Single(result => result.Expected.ObjectId == rows[2].ObjectId);
            io.UsnOverride = 101;
            LocalGapWorker.Promote(gaps, store, io, root, watermark, new NtfsUsnCheckpoint(1, 0, 200, 0), path => path == completed.Expected.Path, _ => { }, default);
            LocalChecks.Require(store.Live(root.RootId, [alias.Path]).Single().Hash == oldHash &&
                gaps.GapResults(root.RootId, watermark, 128).Count == 0,
                "same-size same-mtime edit with changed file USN rejects stale staged content");
            gaps.StageGapHash(root.RootId, epoch, completed.Expected, newHash, io.Proof(completed.Expected, 100));
            completed = gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Single();
            io.UsnOverride = null;
            LocalChecks.Require(gaps.AcceptGapResult(root.RootId, completed) &&
                store.Live(root.RootId, [rows[2].Path, alias.Path]).All(item => item.Hash == newHash) &&
                store.RehashWatermark(root.RootId) > 0,
                "promotion atomically publishes all aliases and durable planning obligation");
            gaps.BeginGap(root.RootId, "0000000000000001:300", 200, true);
            var next = gaps.GapEpoch(root.RootId)!;
            LocalChecks.Require(next.Epoch != epoch.Epoch && next.AfterObject == "" && next.Boundary == 100 &&
                !gaps.AcceptGapResult(root.RootId, stale), "repeated gap restarts discovery preserving oldest unresolved boundary and invalidates proofs");
            gaps.BeginGap(root.RootId, "0000000000000002:400", 0, false);
            epoch = gaps.GapEpoch(root.RootId)!;
            var before = io.Probes;
            await using (var worker = new LocalGapWorker(gaps, io)) worker.Discover(root, epoch, default);
            LocalChecks.Require(io.Probes == before && gaps.GapHashPage(root.RootId, epoch, 128).Count == 3,
                "untrusted history queues every entity without treating metadata as proof");
            gaps.BeginGap(root.RootId, "0000000000000002:500", 400, true);
            LocalChecks.Require(!gaps.GapEpoch(root.RootId)!.TrustFileUsn,
                "new trustworthy gap cannot erase unfinished untrusted verification");
        }
        using (var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gaps = (ILocalGapStore)reopened;
            LocalChecks.Require(gaps.HasGapProtection(root.RootId) && gaps.GapResultWatermark(root.RootId) >= watermark &&
                !gaps.AcceptGapResult(root.RootId, stale), "restart preserves gap obligations monotonic cutoff and stale-proof rejection");
            var blocking = new GapCheckIo(stamp, cursor) { Block = true };
            await using (var worker = new LocalGapWorker(gaps, blocking))
            {
                worker.Start(root); worker.Start(root);
                await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                LocalChecks.Require(blocking.Reads == 1, "composition worker deduplicates same-root requests without awaiting hashing");
            }
            LocalChecks.Require(blocking.Cancelled, "worker cancellation joins interrupted IO before store disposal");
            var concurrentRoot = root with { RootId = "gap-concurrent" };
            reopened.WriteSnapshot(new Snapshot(concurrentRoot.RootId, Side.A, reopened.BeginGeneration(concurrentRoot.RootId, "full"), cursor, rows, []));
            gaps.BeginGap(concurrentRoot.RootId, cursor, 100, true);
            var epoch = gaps.GapEpoch(concurrentRoot.RootId)!;
            gaps.GapDiscovered(concurrentRoot.RootId, epoch, rows[0], true);
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var concurrent = new GapCheckIo(stamp, cursor) { Block = true, OnProbe = (r, _) =>
            {
                if (r.RootId != concurrentRoot.RootId) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("test discovery gate timed out");
            } };
            await using (var worker = new LocalGapWorker(gaps, concurrent))
            {
                try
                {
                    worker.Start(concurrentRoot); worker.Start(root); worker.Start(concurrentRoot);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await concurrent.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    LocalChecks.Require(concurrent.Reads == 2,
                        "blocked metadata discovery does not serialize same-root hashing or another root");
                }
                finally { release.Set(); }
            }
            var paused = false;
            var budget = new LocalGapIo(reopened, StorageChecks.Fast with { LocalGapBytesPerSecond = 1000 },
                new HashSet<string>(), () => paused);
            await budget.Budget(100, default);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await budget.Budget(100, default);
            LocalChecks.Require(clock.ElapsedMilliseconds >= 40, "shared configurable gap byte budget throttles content chunks");
            paused = true;
            LocalChecks.Require(!await budget.Budget(100, default), "activity or battery pause interrupts chunk admission");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var stopped = false;
            try { await budget.Budget(100, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            LocalChecks.Require(stopped, "throttle waits honor shutdown cancellation");
            if (OperatingSystem.IsWindows())
            {
                var file = Path.Combine(product, "sharing.bin");
                File.WriteAllBytes(file, [1, 2, 3]);
                var time = File.GetLastWriteTimeUtc(file);
                using var read = NtfsUsnJournal.OpenGapContent(file);
                using var metadata = NtfsUsnJournal.OpenGapMetadata(file);
                LocalChecks.Require(!read.IsInvalid && !metadata.IsInvalid, "test-owned F file opens through production content and metadata readers");
                using (var write = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    write.WriteByte(4);
                File.SetLastWriteTimeUtc(file, time);
                LocalChecks.Require(new FileInfo(file).Length == 3 && File.GetLastWriteTimeUtc(file) == time,
                    "full-sharing gap handle permits ordinary writer and same-size timestamp-preserving edit");
                var parent = Path.Combine(product, "file-id-parent");
                Directory.CreateDirectory(parent);
                var identityFile = Path.Combine(parent, "file.bin");
                File.WriteAllBytes(identityFile, [1, 2, 3]);
                using var metadataPath = NtfsUsnJournal.OpenGapMetadata(identityFile);
                LocalChecks.Require(PilotDirectoryLease.GetFileInformationByHandle(metadataPath, out var identity), "file-ID fixture identity available");
                metadataPath.Dispose(); // Production metadata probing holds only the ID handle, not a path-open handle.
                using var volume = NtfsUsnJournal.OpenGapVolume(parent);
                using var byId = NtfsUsnJournal.OpenGapMetadata(volume, ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow);
                LocalChecks.Require(!byId.IsInvalid && PilotDirectoryLease.GetFileInformationByHandle(byId, out var found) &&
                    found.VolumeSerialNumber == identity.VolumeSerialNumber && found.FileIndexLow == identity.FileIndexLow &&
                    found.FileIndexHigh == identity.FileIndexHigh, "full-sharing metadata file-ID open identifies the exact entity");
                Directory.Move(parent, parent + "-renamed");
                LocalChecks.Require(File.Exists(Path.Combine(parent + "-renamed", "file.bin")),
                    "file-ID metadata probe and volume hint do not block parent folder renames");
            }
        }
        Console.WriteLine("PASS isolated gap store and background lifecycle");
    }

    private static async Task SavedCursorWrapAsync(string folder)
    {
        var product = Path.Combine(folder, "gap-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("gap-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        var stamp = DateTimeOffset.UtcNow;
        const string cursor = "0000000000000001:100";
        var rows = Enumerable.Range(1, 2).Select(n => new ItemState("folder/" + n, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)n + 10), null, new string('a', 64), 3, stamp, ItemFlags.None)).ToArray();
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gen = store.BeginGeneration(root.RootId, "full");
            store.WriteSnapshot(new(root.RootId, Side.A, gen, cursor, rows, []));
            store.CompleteGeneration(gen, cursor, true);
        }
        using var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System);
        var saved = reopened.Cursor(root.RootId);
        LocalChecks.Require(saved == cursor && NtfsUsnCheckpoint.TryParseCursor(saved, out _, out _),
            "current-schema restart retains a readable next-unprocessed journal cursor");
        NtfsUsnCheckpoint.TryParseCursor(saved, out var journalId, out var next);
        // Retention advanced past the saved cursor; resizing alone is absent from this identity comparison.
        var through = new NtfsUsnCheckpoint(1, 150, 200, 150);
        LocalChecks.Require(through.RequiresFullReconciliation(saved) &&
            !new NtfsUsnCheckpoint(1, 0, 200, 0).RequiresFullReconciliation(saved),
            "same journal requires reconciliation only when retention passes the saved cursor, not on resize");
        var inventory = new LocalInventory(root, Side.A, reopened, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System);
        inventory.BeginJournalGap(root.RootId, journalId, next, through);
        inventory.BeginFullGap(root.RootId, through.Cursor, journalDeleted: false);
        var gaps = (ILocalGapStore)reopened;
        var epoch = gaps.GapEpoch(root.RootId)!;
        LocalChecks.Require(epoch is { TrustFileUsn: true, Boundary: 100 },
            "same-ID wrap and full fallback preserve the trusted saved boundary");
        var io = new GapCheckIo(stamp, through.Cursor);
        await using (var worker = new LocalGapWorker(gaps, io))
            worker.Discover(root, epoch, default);
        LocalChecks.Require(reopened.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [rows[0].Path])[0].Covered,
            "same-run unchanged USN proof remains held");
        gaps.PromoteGapDiscovery(root.RootId);
        var held = reopened.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [rows[0].Path, rows[1].Path]);
        LocalChecks.Require(io.Probes == 2 && io.Reads == 0 && reopened.RehashWatermark(root.RootId) == 0 &&
            !held[0].Covered && held[1].Covered && gaps.GapHashPage(root.RootId, epoch, 128).Count == 1,
            "saved-cursor wrap retires USN below boundary without content/debt/hold; equality alone remains a candidate");
        LocalChecks.Require(reopened.ByObject(root.RootId, rows[0].ObjectId)!.Hash == rows[0].Hash,
            "trusted unchanged entity retains its reusable content hash");
    }

    private static void GapSchemaUpgradeChecks(string folder)
    {
        var product = Path.Combine(folder, "schema-" + Guid.NewGuid().ToString("N"));
        const string root = "schema-root";
        var row = new ItemState("file", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 10), null,
            new string('a', 64), 1, DateTimeOffset.UtcNow, ItemFlags.None);
        using (var created = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            created.WriteSnapshot(new(root, Side.A, created.BeginGeneration(root, "full"), "0000000000000001:200", [row], []));
            var gaps = (ILocalGapStore)created;
            gaps.BeginGap(root, "0000000000000001:200", 100, true);
            gaps.GapDiscovered(root, gaps.GapEpoch(root)!, row, true);
        }
        using (var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gaps = (ILocalGapStore)reopened;
            gaps.RetryGapFailures(root);
            LocalChecks.Require(gaps.GapHashPage(root, gaps.GapEpoch(root)!, 128).Count == 1,
                "create close reopen verifies schema and exercises new gap indexes");
        }
        // Simulate the earlier branch schema at the SAME user_version, preserving all fixture work rows.
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(product, "v2", "state.db")))
        {
            db.Open();
            StoreSchema.Execute(db, null, "DROP INDEX gap_ready; DROP INDEX gap_failed; DROP INDEX gap_waiting; DROP INDEX gap_metadata; " +
                "UPDATE local_gap SET after_object=worker_after,discovery_done=worker_done; " +
                "ALTER TABLE local_gap DROP COLUMN worker_after; ALTER TABLE local_gap DROP COLUMN worker_done; " +
                "ALTER TABLE local_gap_item DROP COLUMN metadata_ready; ALTER TABLE local_gap_item DROP COLUMN metadata_stamp; " +
                "ALTER TABLE local_gap DROP COLUMN reason; " +
                "ALTER TABLE local_gap_item DROP COLUMN failure_count; ALTER TABLE local_gap_item DROP COLUMN failure_reason; " +
                "ALTER TABLE local_gap_item DROP COLUMN retry_allowed; ALTER TABLE local_gap_item DROP COLUMN defer_count; " +
                "ALTER TABLE local_gap_item DROP COLUMN defer_reason; ALTER TABLE local_gap_item DROP COLUMN discovery_reason; " +
                "CREATE INDEX IF NOT EXISTS gap_retry ON local_gap_item(root_no,epoch,last_attempt,object_id) WHERE hash IS NULL;");
        }
        using (var upgraded = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gaps = (ILocalGapStore)upgraded;
            gaps.RetryGapFailures(root);
            LocalChecks.Require(gaps.GapHashPage(root, gaps.GapEpoch(root)!, 128).Count == 1 &&
                upgraded.ByObject(root, row.ObjectId)!.Hash == row.Hash && gaps.GapEpoch(root)!.AfterObject == row.ObjectId,
                "previous branch gains columns and indexes preserving inventory, gap obligations and worker cursor");
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(product, "v2", "state.db")))
        {
            db.Open();
            // Immediate predecessor 54b39043: failure columns exist, but ready still uses failure_count<3.
            StoreSchema.Execute(db, null, "DROP INDEX gap_ready; DROP INDEX gap_waiting; " +
                "ALTER TABLE local_gap_item DROP COLUMN retry_allowed; ALTER TABLE local_gap_item DROP COLUMN defer_count; " +
                "ALTER TABLE local_gap_item DROP COLUMN defer_reason; " +
                "CREATE INDEX gap_ready ON local_gap_item(root_no,epoch,last_attempt,object_id) WHERE hash IS NULL AND failure_count<3;");
        }
        using (var immediateUpgrade = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gaps = (ILocalGapStore)immediateUpgrade;
            LocalChecks.Require(gaps.GapHashPage(root, gaps.GapEpoch(root)!, 128).Count == 1,
                "immediate previous branch schema upgrades its ready predicate and retry columns at the same version");
        }
        using var verifiedAgain = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System);
        LocalChecks.Require(((ILocalGapStore)verifiedAgain).HasGapProtection(root), "upgraded store reopens idempotently");
    }

    private static void GapPerFileProofChecks(SqliteStateStore store, RootDef original, ItemState[] rows,
        DateTimeOffset stamp, string oldHash, string newHash)
    {
        var gaps = (ILocalGapStore)store;
        foreach (var scenario in new[] { "stable", "wrapped-range", "replaced", "backward", "missing-start", "changed-usn" })
        {
            var root = original with { RootId = "per-file-" + scenario };
            store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"),
                "0000000000000001:200", rows, []));
            gaps.BeginGap(root.RootId, "0000000000000001:200", 100, true);
            var epoch = gaps.GapEpoch(root.RootId)!;
            foreach (var row in rows) gaps.GapDiscovered(root.RootId, epoch, row, true);
            var beforeStage = gaps.GapResultWatermark(root.RootId);
            var io = new GapCheckIo(stamp, "0000000000000001:200")
            {
                UsnOverride = scenario == "changed-usn" ? 101 : 100,
                ProbeCursor = "0000000000000001:300"
            };
            foreach (var row in rows) gaps.StageGapHash(root.RootId, epoch, row, newHash, io.Proof(row, 100));
            NtfsUsnCheckpoint? start = new(1, 0, 300, 0);
            if (scenario == "wrapped-range") start = start with { FirstUsn = 201 };
            if (scenario == "replaced") start = start with { JournalId = 2 };
            if (scenario == "backward") start = start with { NextUsn = 199 };
            if (scenario == "missing-start") start = null;
            LocalGapWorker.Promote(gaps, store, io, root, beforeStage, start, _ => true, _ => { }, default);
            LocalChecks.Require(io.Probes == 0 && store.Live(root.RootId).All(r => r.Hash == oldHash),
                "same-run staged results cannot pass next-run watermark eligibility: " + scenario);
            var cutoff = gaps.GapResultWatermark(root.RootId);
            LocalGapWorker.Promote(gaps, store, io, root, cutoff, start, _ => true, _ => { }, default);
            var published = store.Live(root.RootId).Count(r => r.Hash == newHash);
            var expected = scenario is "stable" or "wrapped-range" ? 3 : 0;
            LocalChecks.Require(published == expected,
                "next-run per-file proof, without any volume interval read: " + scenario);
            if (expected == 0)
                LocalChecks.Require(store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [rows[0].Path])[0].Covered,
                    "failed or identity-record proof retains destructive protection: " + scenario);
        }
    }

    private static async Task GapReviewChecks(SqliteStateStore store, RootDef original, ItemState[] rows,
        DateTimeOffset stamp, string oldHash, string newHash)
    {
        const string cursor = "0000000000000001:200";
        var gaps = (ILocalGapStore)store;
        var root = original with { RootId = "metadata-only" };
        var metadata = new[] { ItemFlags.Placeholder, ItemFlags.Link, ItemFlags.Ignored, (ItemFlags)128 }
            .Select((flag, n) => rows[0] with { Path = "blocked/" + n,
                ObjectId = SyncLocalEndpointStore.ObjectId(1, (ulong)n + 100), Flags = flag }).ToArray();
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, metadata, []));
        gaps.BeginGap(root.RootId, cursor, 0, false);
        var io = new GapCheckIo(stamp, cursor) { IsPaused = true };
        await using (var worker = new LocalGapWorker(gaps, io))
            worker.Discover(root, gaps.GapEpoch(root.RootId)!, default);
        LocalChecks.Require(gaps.HasGapProtection(root.RootId), "metadata-only worker stages rather than discharges protection");
        gaps.PromoteGapDiscovery(root.RootId);
        LocalChecks.Require(io.Probes == 0 && io.Reads == 0 && !gaps.HasGapProtection(root.RootId),
            "active user does not pause metadata-only settlement; placeholders links ignored quarantine never queue");

        root = original with { RootId = "excluded-results" };
        var many = Enumerable.Range(1, 130).Select(n => rows[0] with { Path = "path/" + n,
            ObjectId = SyncLocalEndpointStore.ObjectId(1, (ulong)n + 1000) }).ToArray();
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, many, []));
        gaps.BeginGap(root.RootId, cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        io = new GapCheckIo(stamp, cursor) { UsnOverride = 100 };
        foreach (var item in many)
        {
            gaps.GapDiscovered(root.RootId, epoch, item, true);
            gaps.StageGapHash(root.RootId, epoch, item, newHash, io.Proof(item, 100));
        }
        gaps.GapDiscoveryDone(root.RootId, epoch);
        gaps.PromoteGapDiscovery(root.RootId); // Simulate the next run's metadata entrypoint.
        var touched = new List<string>();
        LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId),
            new(1, 0, 200, 0), path => path == many[^1].Path, touched.Add, default);
        LocalChecks.Require(!gaps.HasGapProtection(root.RootId) && store.Live(root.RootId).All(item => item.Hash == newHash) &&
            touched.SequenceEqual(new[] { many[^1].Path }),
            "one pair's selection only filters planning touches: all 130 root-entity proofs promote in one run");
        LocalChecks.Require(store.ByObject(root.RootId, many[0].ObjectId)!.Hash == newHash && store.RehashWatermark(root.RootId) > 0,
            "another pair or re-inclusion sees verified content plus durable planning debt, not a purged stale hash");

        root = original with { RootId = "rotated-results" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, many, []));
        gaps.BeginGap(root.RootId, cursor, 100, true); epoch = gaps.GapEpoch(root.RootId)!;
        foreach (var item in many)
        {
            gaps.GapDiscovered(root.RootId, epoch, item, true);
            gaps.StageGapHash(root.RootId, epoch, item, newHash, io.Proof(item, 100));
        }
        io.Unavailable = item => item.Path != many[^1].Path;
        var watermark = gaps.GapResultWatermark(root.RootId);
        LocalGapWorker.Promote(gaps, store, io, root, watermark, new(1, 0, 200, 0), _ => true, _ => { }, default);
        var deferred = gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128);
        LocalChecks.Require(store.ByObject(root.RootId, many[^1].ObjectId)!.Hash == newHash &&
            gaps.GapResults(root.RootId, watermark, 128).Count == 0 && deferred.Count == 128 &&
            gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128, deferred[^1].Sequence).Count == 1,
            "129 failed probes rotate past the run watermark without losing hashes or blocking the final result");
        io.Unavailable = null;

        root = original with { RootId = "promotion-budget" };
        var budgetRows = Enumerable.Range(1, 600).Select(n => rows[0] with { Path = "budget/" + n,
            ObjectId = SyncLocalEndpointStore.ObjectId(1, (ulong)n + 5000) }).ToArray();
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, budgetRows, []));
        gaps.BeginGap(root.RootId, cursor, 100, true); epoch = gaps.GapEpoch(root.RootId)!;
        foreach (var item in budgetRows)
        {
            gaps.GapDiscovered(root.RootId, epoch, item, true);
            gaps.StageGapHash(root.RootId, epoch, item, newHash, io.Proof(item, 100));
        }
        gaps.GapDiscoveryDone(root.RootId, epoch);
        gaps.PromoteGapDiscovery(root.RootId); // Simulate the next run's metadata entrypoint.
        var beforeProbes = io.Probes;
        LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId), new(1, 0, 200, 0), _ => true, _ => { }, default);
        LocalChecks.Require(io.Probes - beforeProbes == 512 && gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Count == 88,
            "planning pass has fixed 512-probe budget and leaves remaining verified work staged");
        LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId), new(1, 0, 200, 0), _ => true, _ => { }, default);
        LocalChecks.Require(!gaps.HasGapProtection(root.RootId), "next pass completes remaining promotion work");

        root = original with { RootId = "defer-and-foreground" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
        gaps.BeginGap(root.RootId, cursor, 100, true); epoch = gaps.GapEpoch(root.RootId)!;
        var itemToVerify = rows[2];
        gaps.GapDiscovered(root.RootId, epoch, itemToVerify, true);
        gaps.StageGapHash(root.RootId, epoch, itemToVerify, newHash, io.Proof(itemToVerify, 100));
        io.UsnOverride = null; // id 13 has a transiently unavailable probe.
        LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId), new(1, 0, 200, 0), _ => true, _ => { }, default);
        LocalChecks.Require(gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Count == 1,
            "transient probe retains staged content rather than rereading a large file");
        for (var run = 0; run < 2; run++)
            LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId), new(1, 0, 200, 0), _ => true, _ => { }, default);
        LocalChecks.Require(!gaps.GapProgress(root.RootId).Contains("item verification failed"), "deferred proofs have separate state from hash failures");
        io.UsnOverride = 101;
        LocalGapWorker.Promote(gaps, store, io, root, gaps.GapResultWatermark(root.RootId), new(1, 0, 200, 0), _ => true, _ => { }, default);
        LocalChecks.Require(gaps.GapHashPage(root.RootId, epoch, 128).Count == 0, "rejected proof does not immediately enter background rehash in the same pass");
        gaps.RetryGapFailures(root.RootId);
        LocalChecks.Require(gaps.GapHashPage(root.RootId, epoch, 128).Count == 1, "rejected proof becomes eligible on the next run");
        store.RequireRehash(root.RootId, itemToVerify, "ordinary edit");
        LocalChecks.Require(store.CompleteRehash(root.RootId, itemToVerify, newHash) &&
            gaps.GapHashPage(root.RootId, epoch, 128).Count == 0, "foreground content verification retires same-entity gap obligation");
        root = original with { RootId = "read-failure-classification" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System);
        var through = new NtfsUsnCheckpoint(1, 0, 300, 0);
        var retry = inventory.JournalReadFailed(1, root.RootId, cursor, through, 200, "volume open failed");
        LocalChecks.Require(!retry.CursorInvalid && retry.CursorAfter == cursor && retry.Changes.Count == 0 &&
            retry.IncompleteSubtrees.SequenceEqual(new[] { "" }) && gaps.GapEpoch(root.RootId) is null,
            "transient journal IO retries unchanged cursor with unknown subtree and no gap pass");
        var overflow = inventory.JournalReadFailed(1, root.RootId, cursor, through, 200, "arbitrary diagnostic text", NtfsUsnReadFailure.OverLimit);
        LocalChecks.Require(overflow.CursorInvalid && overflow.Changes.Count == 0 &&
            gaps.GapEpoch(root.RootId) is { TrustFileUsn: true, Boundary: 200 } &&
            gaps.GapEpoch(root.RootId)!.JournalCursor == through.Cursor,
            "over-limit journal read requests metadata full rescan and trusted per-file gap at saved boundary");

        root = original with { RootId = "missing-saved-cursor" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), "", rows, []));
        inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System);
        await inventory.FullAsync(1, _ => true, null!, default);
        LocalChecks.Require(gaps.GapEpoch(root.RootId) is { TrustFileUsn: false } && gaps.HasGapProtection(root.RootId),
            "full scan with retained rows but no readable saved cursor starts untrusted gap verification");

        root = original with { RootId = "initial-resume" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), "", rows.Select(r => r with { Hash = null }).ToArray(), []));
        inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System);
        inventory.BeginFullGap(root.RootId, "", journalDeleted: true);
        LocalChecks.Require(store.Cursor(root.RootId) is null && store.Live(root.RootId).Count == rows.Length &&
            gaps.GapEpoch(root.RootId) is null, "interrupted initial metadata snapshot retains rows without cursor but does not blanket background-verify on resume");

        root = original with { RootId = "deleted-journal-epoch" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
        gaps.BeginGap(root.RootId, "", 0, false, "NTFS journal deleted or disabled");
        var deletedEpoch = gaps.GapEpoch(root.RootId)!;
        inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System);
        inventory.BeginFullGap(root.RootId, cursor, journalDeleted: true);
        inventory.BeginFullGap(root.RootId, cursor, journalDeleted: true);
        LocalChecks.Require(gaps.GapEpoch(root.RootId)!.Epoch == deletedEpoch.Epoch, "deleted journal delta and full fallback share one persistent gap epoch");

        root = original with { RootId = "discovery-outage" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
        gaps.BeginGap(root.RootId, cursor, 100, true); epoch = gaps.GapEpoch(root.RootId)!;
        io = new GapCheckIo(stamp, cursor) { PageUnavailable = "temporary journal outage" };
        await using (var worker = new LocalGapWorker(gaps, io))
        {
            LocalChecks.Require(!worker.Discover(root, epoch, default) && gaps.GapEpoch(root.RootId)!.AfterObject == "" &&
                gaps.GapHashPage(root.RootId, epoch, 128).Count == 0, "temporary discovery outage preserves cursor and queues no content page");
            io.PageUnavailable = null;
            LocalChecks.Require(worker.Discover(root, epoch, default), "metadata discovery resumes after journal recovers");
        }

        root = original with { RootId = "poison-item" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
        gaps.BeginGap(root.RootId, cursor, 100, true); epoch = gaps.GapEpoch(root.RootId)!;
        io = new GapCheckIo(stamp, cursor) { UsnOverride = 100,
            OnProbe = (_, item) => { if (item.Path == rows[0].Path) throw new ArgumentException("poison identity"); },
            OnHash = item => { if (item.Path == rows[0].Path) throw new ArgumentException("poison identity"); } };
        await using (var worker = new LocalGapWorker(gaps, io))
        {
            worker.Discover(root, epoch, default);
            for (var attempt = 0; attempt < 3; attempt++) await worker.HashPage(root, epoch, default);
        }
        LocalChecks.Require(gaps.GapEpoch(root.RootId)!.DiscoveryDone &&
            gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Count == 2 &&
            gaps.GapHashPage(root.RootId, epoch, 128).Count == 0 && gaps.GapProgress(root.RootId).Contains(rows[0].Path + ": poison identity"),
            "poison item stays protected with per-item notice after bounded retries while other files complete");
        gaps.RetryGapFailures(root.RootId);
        var retryPage = gaps.GapHashPage(root.RootId, epoch, 128);
        LocalChecks.Require(retryPage.Count == 1 && gaps.GapHashPage(root.RootId, epoch, 128).Count == 0 &&
            gaps.GapProgress(root.RootId).Contains("poison identity"), "later run grants one retry while cumulative failure notice persists");
        gaps.StageGapHash(root.RootId, epoch, retryPage.Single(), newHash, io.Proof(retryPage.Single(), 100));
        LocalChecks.Require(!gaps.GapProgress(root.RootId).Contains("item verification failed after 3 attempts"), "successful hash clears cumulative failure count and notice");

        root = original with { RootId = "worker-restart" };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), cursor, rows, []));
        gaps.BeginGap(root.RootId, cursor, 100, true);
        io = new GapCheckIo(stamp, cursor) { OnOpen = () => throw new ArgumentException("injected root failure") };
        await using (var worker = new LocalGapWorker(gaps, io))
        {
            worker.Start(root);
            for (var n = 0; n < 100 && !gaps.GapProgress(root.RootId).Contains("worker failed"); n++) await Task.Delay(10);
            LocalChecks.Require(gaps.GapProgress(root.RootId).Contains("worker failed"), "unexpected root exception becomes explanatory notice status");
            io.OnOpen = null;
            for (var n = 0; n < 100 && !gaps.GapEpoch(root.RootId)!.DiscoveryDone; n++) { worker.Start(root); await Task.Delay(10); }
            LocalChecks.Require(gaps.GapEpoch(root.RootId)!.DiscoveryDone, "failed worker restarts on subsequent run without restarting process");
        }
    }

    private sealed class GapCheckIo(DateTimeOffset stamp, string cursor) : ILocalGapIo
    {
        public bool IsPaused;
        public bool Paused => IsPaused;
        public int Probes; public int Reads; public bool Block; public bool Cancelled;
        public long? UsnOverride;
        public string? ProbeCursor;
        public string? PageUnavailable;
        private sealed class MissingPage(string reason) : ILocalGapProbe
        {
            public string? UnavailableReason => reason;
            public LocalGapProof? Probe(ItemState item) => null;
            public void Dispose() { }
        }
        public Action? OnOpen;
        public Action<ItemState>? OnHash;
        public Func<ItemState, bool>? Unavailable;
        public ILocalGapProbe OpenProbes(RootDef root)
        {
            OnOpen?.Invoke();
            if (PageUnavailable is { } why) return new MissingPage(why);
            return new DelegatingGapProbe(this, root);
        }
        public Action<RootDef, ItemState>? OnProbe;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LocalGapProof Proof(ItemState item, long usn) => new(cursor,
            new NtfsFileUsn(1, LocalItems.ParseObjectId(item.ObjectId).FileId, usn, item.Size, stamp.UtcDateTime.ToFileTimeUtc()));
        public LocalGapProof? Probe(RootDef root, ItemState item)
        {
            Interlocked.Increment(ref Probes); OnProbe?.Invoke(root, item);
            if (Unavailable?.Invoke(item) == true) return null;
            if (UsnOverride is { } usn) return Proof(item, usn) with { JournalCursor = ProbeCursor ?? cursor };
            var id = LocalItems.ParseObjectId(item.ObjectId).FileId;
            return id == 13 ? null : Proof(item, id == 11 ? 99 : 100);
        }
        public async Task<(string Hash, LocalGapProof Proof)?> HashAsync(RootDef root, ItemState item, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Reads) >= 2) BothStarted.TrySetResult();
            Started.TrySetResult();
            OnHash?.Invoke(item);
            if (Block)
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            return (new string('b', 64), Proof(item, 100));
        }
    }
}
