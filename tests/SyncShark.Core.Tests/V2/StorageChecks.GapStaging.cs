using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // Store-only: runs on any filesystem; no NTFS journal, content IO, or real roots.
    private static void GapStagingChecks(string folder)
    {
        var product = Fresh(folder, "gap-staging");
        const string root = "staging-root";
        const string cursor = "0000000000000001:200";
        var rows = Enumerable.Range(1, 600).Select(n => new ItemState("folder/" + n,
            ItemKind.File, "object-" + n.ToString("D4"), null, new string('a', 64), 3,
            DateTimeOffset.UnixEpoch.AddDays(1), ItemFlags.None)).ToArray();
        LocalGapEpoch original;
        using (var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            store.WriteSnapshot(new(root, Side.A, store.BeginGeneration(root, "full"), cursor, rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root, cursor, 100, true);
            original = gaps.GapEpoch(root)!;
            foreach (var batch in rows.Chunk(128))
                gaps.GapDiscoveredBatch(root, original, batch.Select(item => (item, false)).ToArray());
            gaps.GapDiscoveryDone(root, original);
            Require(gaps.GapEpoch(root)!.DiscoveryDone && gaps.GapDiscoveryPage(root, original, 128).Count == 0,
                "worker progress is durable and does not rescan staged unchanged entities");
            Require(gaps.GapHashPage(root, original, 128).Count == 0 && store.RehashWatermark(root) == 0,
                "metadata staging creates neither content work nor foreground debt");
            Require(store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[0].Path, rows[^1].Path]).All(hit => hit.Covered),
                "metadata discovery and worker completion never shrink current-run protection");
            using var fault = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False");
            fault.Open();
            StoreSchema.Execute(fault, null,
                "CREATE TRIGGER fail_metadata_promotion BEFORE DELETE ON local_gap_item " +
                "WHEN OLD.object_id='object-0002' BEGIN SELECT RAISE(ABORT,'synthetic promotion failure'); END;");
            LocalGapWorker.PromoteDiscovery(gaps, new(root, SyncProviders.Local, null, product, "root-object"), default);
            Require(gaps.GapProgress(root).Contains("metadata promotion deferred", StringComparison.Ordinal) &&
                (long?)StoreSchema.Scalar(fault, null, "SELECT COUNT(*) FROM local_gap_item;") == 600,
                "unknown promotion failure rolls back all discharges and reports retry rather than aborting run");
            StoreSchema.Execute(fault, null, "DROP TRIGGER fail_metadata_promotion;");
        }
        using (var db = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False"))
        {
            db.Open();
            Require((long?)StoreSchema.Scalar(db, null,
                "SELECT discovery_done FROM local_gap;") == 0 &&
                (string?)StoreSchema.Scalar(db, null, "SELECT after_object FROM local_gap;") == "",
                "published frontier stays frozen while separate worker frontier advances");
            Require((long?)StoreSchema.Scalar(db, null, "SELECT COUNT(*) FROM local_gap_item WHERE metadata_ready=1;") == 600,
                "all unchanged entities keep explicit staged protection rows");
            // A one-tick observation change must not disappear through JSON date rounding.
            StoreSchema.Execute(db, null, "UPDATE file_state SET mtime=mtime+1 WHERE object_id=$o;", ("$o", rows[0].ObjectId));
        }
        using (var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            var gaps = (ILocalGapStore)store;
            Require(gaps.GapEpoch(root)!.DiscoveryDone && gaps.HasGapProtection(root),
                "restart preserves both worker progress and unpublished protection");
            gaps.PromoteGapDiscovery(root);
            var held = store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[0].Path, rows[1].Path, rows[^1].Path]);
            using (var measured = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False"))
            {
                measured.Open();
                Require((long?)StoreSchema.Scalar(measured, null, "SELECT COUNT(*) FROM local_gap_item;") == 345 &&
                    (long?)StoreSchema.Scalar(measured, null, "SELECT COUNT(*) FROM local_gap_item WHERE metadata_ready=1;") == 344 &&
                    (long?)StoreSchema.Scalar(measured, null, "SELECT COUNT(*) FROM local_gap_item WHERE metadata_ready=0;") == 1,
                    "approved 256 cap leaves measured 345 obligations: 344 staged and one changed");
            }
            Require(held.All(hit => hit.Covered),
                "capped promotion keeps the unpublished suffix and changed observation protected");
            for (var pass = 0; pass < 3; pass++) gaps.PromoteGapDiscovery(root);
            held = store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[0].Path, rows[1].Path, rows[^1].Path]);
            Require(held[0].Covered && !held[1].Covered && !held[2].Covered,
                "later bounded passes drain the metadata remainder and preserve the changed observation");
            Require(gaps.GapHashPage(root, original, 128).Single().ObjectId == rows[0].ObjectId,
                "invalid metadata proof returns to normal hash retry lane");
            Require(store.Live(root).All(item => item.Hash == rows[0].Hash) && store.RehashWatermark(root) == 0,
                "metadata promotion does not rewrite hashes or create foreground debt");

            // Epoch replacement cannot publish a previous gap's staged discharge.
            gaps.GapDiscovered(root, original, rows[1], false);
            gaps.BeginGap(root, "0000000000000001:300", 100, true);
            gaps.PromoteGapDiscovery(root);
            Require(store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[1].Path])[0].Covered,
                "replacement epoch rejects old metadata discharge");
            var next = gaps.GapEpoch(root)!;
            gaps.GapDiscovered(root, next, rows[1], false);
            store.RequireRehash(root, rows[1], "authoritative write invalidates metadata proof");
            gaps.PromoteGapDiscovery(root);
            Require(store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[1].Path])[0].Covered,
                "fresh authoritative write invalidates even metadata-identical staged discharge");

            // Retirement from the content lane is also staged, not a live DELETE.
            gaps.GapDiscovered(root, next, rows[2], true);
            gaps.RetireGapItem(root, next, rows[2]);
            Require(store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[2].Path])[0].Covered,
                "content-worker retirement preserves current-run entity hold");
        }
        using (var db = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False"))
        {
            db.Open();
            Require((long?)StoreSchema.Scalar(db, null,
                "SELECT metadata_ready FROM local_gap_item WHERE object_id=$o;", ("$o", rows[2].ObjectId)) == 1,
                "retirement persists a staged discharge across store shutdown");
            var plans = StoreSchema.QueryPlan(db, "metadata-promotion",
                "DELETE FROM local_gap_item INDEXED BY gap_metadata WHERE root_no=$r AND epoch=$e AND metadata_ready=1;", "$r", "$e");
            Require(plans.Any(plan => plan.Contains("gap_metadata", StringComparison.Ordinal)),
                "metadata promotion uses indexed entity staging, never a whole-root managed list");
            StoreSchema.Execute(db, null, "DELETE FROM file_state WHERE object_id=$o;", ("$o", rows[2].ObjectId));
        }
        using (var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            ((ILocalGapStore)store).PromoteGapDiscovery(root);
            using var db = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False");
            db.Open();
            Require((long?)StoreSchema.Scalar(db, null,
                "SELECT metadata_ready FROM local_gap_item WHERE object_id=$o;", ("$o", rows[2].ObjectId)) == 0,
                "changed presence invalidates prior metadata retirement instead of deleting its obligation");
        }
        GapPromotionPageFailureChecks(folder, rows);
        Console.WriteLine("PASS store-only gap metadata staging and paged next-run promotion");
    }

    private static void GapPromotionPageFailureChecks(string folder, ItemState[] rows)
    {
        var product = Fresh(folder, "gap-promotion-pages");
        const string root = "paged-root";
        const string cursor = "0000000000000001:200";
        using var store = SqliteStateStore.Open(product, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.WriteSnapshot(new(root, Side.A, store.BeginGeneration(root, "full"), cursor, rows, []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root, cursor, 100, true);
        var epoch = gaps.GapEpoch(root)!;
        foreach (var batch in rows.Chunk(128))
            gaps.GapDiscoveredBatch(root, epoch, batch.Select(item => (item, false)).ToArray());
        gaps.GapDiscoveryDone(root, epoch);
        using var db = new SqliteConnection($"Data Source={DbPath(product)};Pooling=False");
        db.Open();
        StoreSchema.Execute(db, null,
            "CREATE TRIGGER fail_second_promotion_page BEFORE DELETE ON local_gap_item " +
            "WHEN OLD.object_id='object-0066' BEGIN SELECT RAISE(ABORT,'synthetic second page failure'); END;");
        LocalGapWorker.PromoteDiscovery(gaps, new(root, SyncProviders.Local, null, product, "root-object"), default);
        Require((long?)StoreSchema.Scalar(db, null, "SELECT COUNT(*) FROM local_gap_item;") == 536 &&
            (string?)StoreSchema.Scalar(db, null, "SELECT after_object FROM local_gap;") == "" &&
            (long?)StoreSchema.Scalar(db, null, "SELECT discovery_done FROM local_gap;") == 0,
            "second-page failure preserves first-page commit, rolls back its own page and leaves frontier unpublished");
        Require(store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[0].Path, rows[^1].Path]).All(hit => hit.Covered),
            "partial promotion retains conservative protection until a later complete promotion");
        StoreSchema.Execute(db, null, "DROP TRIGGER fail_second_promotion_page;");
        // Simulate an observation change between a committed page and the next attempt.
        StoreSchema.Execute(db, null, "UPDATE file_state SET mtime=mtime+1 WHERE object_id=$o;", ("$o", rows[^1].ObjectId));
        for (var pass = 0; pass < 3; pass++) gaps.PromoteGapDiscovery(root);
        Require((long?)StoreSchema.Scalar(db, null, "SELECT COUNT(*) FROM local_gap_item;") == 1 &&
            (long?)StoreSchema.Scalar(db, null, "SELECT discovery_done FROM local_gap;") == 1,
            "retry drains all remaining pages and publishes the captured frontier only after completion");
        var held = store.ProtectionBatch(root, ItemFlags.NeedsRehash, [rows[0].Path, rows[^1].Path]);
        Require(!held[0].Covered && held[1].Covered,
            "observation changes between promotion attempts retain their entity protection");
    }
}
