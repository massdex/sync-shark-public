using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Pair removal checks: a disabled pair is archived into a standalone database and only its catalog and
/// pair-owned rows leave the live store. Root inventories, directories, recovery payloads and every other
/// pair are untouched, and a failed removal leaves the whole pair intact.
/// </summary>
internal static partial class StorageChecks
{
    private static void PairRemovalChecks(string root)
    {
        PairRemoval_ArchivesThenRemovesOnlyTheSelectedPair(root);
        PairRemoval_TriggerFailureRollsBackEveryRow(root);
        PairRemoval_CallbackFailureLeavesEveryRow(root);
        PairRemoval_Refusals(root);
        PairRemoval_ReleasesNestedPairRoot(root);
        PairRemoval_UnfinishedRequiresExplicitConsent(root);
        PairRemoval_SharedRootAndArchiveFailure(root);
    }

    // The main case: every pair-owned table carries rows for p1 and p2; only p1's rows move to the archive.
    private static void PairRemoval_ArchivesThenRemovesOnlyTheSelectedPair(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "pair-removal-archive");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        var removed = DisabledPair("p1");
        var kept = OtherPair("p2");
        store.SavePair(removed);
        store.SavePair(kept);

        var payload = SeedRemovalRows(store, folder, removed);
        store.MarkFirstSyncDone(removed.PairId);
        SeedOtherPairRows(store, folder, kept);
        Dictionary<string, string> removedQueries, keptQueries;
        Dictionary<string, string[]> keptRows;
        string expectedRetry;
        using (var live = OpenRaw(folder))
        {
            removedQueries = PairRemovalOwnedQueries(live, removed.PairId);
            keptQueries = PairRemovalOwnedQueries(live, kept.PairId);
            keptRows = keptQueries.ToDictionary(entry => entry.Key, entry => PairRemovalRows(live, entry.Value));
            expectedRetry = PairRemovalRows(live, removedQueries["sync_retry"]).Single();
        }

        var archiveDirectory = Path.Combine(folder, "archive");
        var result = store.ArchiveAndRemovePair("p1", archiveDirectory, dropUnfinished: true);

        Require(result.PairId == "p1" && File.Exists(result.ArchivePath) && File.Exists(result.ManifestPath),
            "a disabled pair is archived and its result names the archive and manifest");
        Require(result.OrphanedRoots.SequenceEqual(new[] { "ra", "rb" }),
            "both roots of the removed pair are reported as orphaned when no other pair uses them");
        RequireRowCounts(result.RowCounts, ("pair", 1), ("root", 2), ("file_state", 6), ("generation", 3),
            ("tombstone", 1), ("dir", 3), ("sync_point", 2), ("pending_effect", 2), ("notice", 1),
            ("recovery", 1), ("deletion_evidence", 1), ("sync_retry", 1), ("planning_deferred", 0),
            ("prepare_header", 0), ("prepare_row", 0), ("prepare_cursor", 0),
            ("removal_review", 0), ("choice_victim", 1), ("choice_restore_receipt", 1), ("choice_merge", 1));

        RequireArchive(result.ArchivePath, result.RowCounts);
        using (var archive = OpenArchive(result.ArchivePath))
        {
            Require(PairRemovalRows(archive, "SELECT * FROM sync_retry;").SequenceEqual([expectedRetry]),
                "archive preserves the exact failed-result retry row including its scheduling fields");
            Require(ArchiveCount(archive, "SELECT COUNT(*) FROM sync_retry WHERE path_key='B.TXT' " +
                    "AND json_extract(json,'$.Path')='b.txt' AND json_extract(json,'$.LastError')='boom';") == 1,
                "archived retry is the failed recovery action's b.txt debt, not another pair's retry");
            Require(ArchiveCount(archive, "SELECT first_sync_done FROM pair WHERE pair_id='p1';") == 1,
                "the archive retains the pair's first-sync completion flag");
            Require(ArchiveCount(archive, "SELECT COUNT(*) FROM pair WHERE pair_id='p1';") == 1 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM pair WHERE pair_id='p2';") == 0,
                "the archive holds the selected pair row and no other pair row");
            Require(ArchiveCount(archive, "SELECT COUNT(*) FROM root WHERE root_id IN ('ra','rb');") == 2 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM root WHERE root_id IN ('rc','rd');") == 0,
                "the archive holds both selected roots and no root of another pair");
            Require(ArchiveCount(archive, "SELECT COUNT(*) FROM sync_point WHERE object_key IN ('k-gone','k-live');") == 2 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM pending_effect WHERE action_id IN ('a-pending','a-recovery');") == 2 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM notice WHERE path='a.txt';") == 1 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM recovery WHERE action_id='a-recovery';") == 1 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM deletion_evidence WHERE path='gone.txt';") == 1,
                "the archive preserves the pair's baselines, intents, notice, recovery and deletion evidence");
            Require(ArchiveCount(archive, "SELECT COUNT(*) FROM file_state f JOIN root r ON r.root_no=f.root_no " +
                        "WHERE r.root_id IN ('ra','rb');") == 6 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM generation g JOIN root r ON r.root_no=g.root_no " +
                        "WHERE r.root_id IN ('ra','rb');") == 3 &&
                    ArchiveCount(archive, "SELECT COUNT(*) FROM tombstone t JOIN root r ON r.root_no=t.root_no " +
                        "WHERE r.root_id IN ('ra','rb');") == 1,
                "the archive holds both roots' live inventory, generations and tombstones");
            Require(ArchiveCount(archive, "SELECT COUNT(*) FROM dir WHERE path IN ('','sub','bsub');") == 3,
                "the archive holds every directory referenced by the selected roots and baselines");
        }

        using (var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath)))
        {
            var document = manifest.RootElement;
            Require(document.GetProperty("Archive").GetProperty("PairId").GetString() == "p1" &&
                    document.GetProperty("Archive").GetProperty("ArchivePath").GetString() == result.ArchivePath &&
                    document.GetProperty("RootDataRetained").GetBoolean() &&
                    document.GetProperty("RemovalStatus").GetString() is { Length: > 0 },
                "the prewritten manifest names the archive, the pair and the retained-root rule");
        }

        using (var live = OpenRaw(folder))
        {
            foreach (var (table, query) in removedQueries)
                Require(PairRemovalRows(live, query).Length == 0,
                    "removed pair has zero live pair-owned rows in " + table);
            foreach (var (table, query) in keptQueries)
                Require(PairRemovalRows(live, query).SequenceEqual(keptRows[table]),
                    "other pair retains every exact row in " + table);
        }
        RequireRemovalChoiceRows(folder, "p1", 0);
        RequireRemovalChoiceRows(folder, "p2", 1);
        Require(store.Pair("p1") is null && store.Pairs().Count == 1 && store.Pair("p2") is not null,
            "the selected pair row is gone and the other pair row is untouched");
        Require(RawCount(folder, "SELECT COUNT(*) FROM pair WHERE pair_id='p1';") == 0 &&
                RawCount(folder, "SELECT COUNT(*) FROM sync_point WHERE object_key IN ('k-gone','k-live');") == 0 &&
                RawCount(folder, "SELECT COUNT(*) FROM pending_effect WHERE action_id IN ('a-pending','a-recovery');") == 0 &&
                RawCount(folder, "SELECT COUNT(*) FROM notice WHERE path='a.txt';") == 0 &&
                RawCount(folder, "SELECT COUNT(*) FROM recovery WHERE action_id='a-recovery';") == 0 &&
                RawCount(folder, "SELECT COUNT(*) FROM deletion_evidence WHERE path='gone.txt';") == 0,
            "every pair-scoped table loses exactly the selected pair's rows");
        Require(RawCount(folder, "SELECT COUNT(*) FROM file_state WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('ra','rb'));") == 6 &&
                RawCount(folder, "SELECT COUNT(*) FROM generation WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('ra','rb'));") == 3 &&
                RawCount(folder, "SELECT COUNT(*) FROM tombstone WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('ra','rb'));") == 1 &&
                RawCount(folder, "SELECT COUNT(*) FROM root WHERE root_id IN ('ra','rb');") == 2,
            "the removed pair's root inventory, generations, tombstones and root rows are retained");
        Require(RawCount(folder, "SELECT COUNT(*) FROM dir WHERE path IN ('','sub','bsub');") == 3,
            "directories are global and are never deleted with a pair");
        Require(store.Pairs().Single().PairId == "p2" &&
                RawCount(folder, "SELECT COUNT(*) FROM pending_effect WHERE action_id='p2-pending';") == 1 &&
                RawCount(folder, "SELECT COUNT(*) FROM notice WHERE path='keep.txt';") == 1 &&
                RawCount(folder, "SELECT COUNT(*) FROM recovery WHERE action_id='p2-recovery';") == 1 &&
                RawCount(folder, "SELECT COUNT(*) FROM deletion_evidence WHERE path='old.txt';") == 1 &&
                RawCount(folder, "SELECT COUNT(*) FROM file_state WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('rc','rd'));") == 1,
            "another pair's rows in every pair-scoped and root-scoped table survive");
        Require(store.Cursor("rc") == "cursor-c" && store.Cursor("ra") == "cursor-a" && store.Cursor("rb") == "cursor-b",
            "removal leaves unrelated roots and the removed pair's own roots untouched");
        RequirePayloadUnchanged(payload);
    }

    // A BEFORE DELETE trigger aborts after the archive and manifest exist: the single removal transaction rolls back.
    private static void PairRemoval_TriggerFailureRollsBackEveryRow(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "pair-removal-trigger");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        var pair = DisabledPair("p1");
        store.SavePair(pair);
        var payload = SeedRemovalRows(store, folder, pair);
        var pairNo = RawCount(folder, "SELECT pair_no FROM pair WHERE pair_id='p1';");

        using (var connection = OpenRaw(folder))
            StoreSchema.Execute(connection, null,
                "CREATE TRIGGER block_pair_removal BEFORE DELETE ON pair BEGIN " +
                "SELECT RAISE(ABORT, 'pair removal blocked by test'); END;");

        var archiveDirectory = Path.Combine(folder, "archive-blocked");
        ExpectThrows<SqliteException>(() => store.ArchiveAndRemovePair("p1", archiveDirectory, dropUnfinished: true));

        var manifestPath = Path.Combine(archiveDirectory, "manifest.json");
        Require(File.Exists(manifestPath),
            "the archive and manifest are written and verified before the first catalog delete");
        using (var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            Require(manifest.RootElement.GetProperty("Archive").GetProperty("PairId").GetString() == "p1",
                "an aborted removal leaves the prewritten manifest for the selected pair");
        RequirePairRowsIntact(folder, pairNo);
        RequirePayloadUnchanged(payload);

        using (var connection = OpenRaw(folder))
            StoreSchema.Execute(connection, null, "DROP TRIGGER block_pair_removal;");
        var retried = store.ArchiveAndRemovePair("p1", Path.Combine(folder, "archive-retry"), dropUnfinished: true);
        Require(store.Pair("p1") is null && retried.OrphanedRoots.SequenceEqual(new[] { "ra", "rb" }),
            "a fresh archive directory after the trigger is removed completes the same removal");
    }

    // The runtime's pre-delete callback is inside the same transaction: its failure rolls every delete back.
    private static void PairRemoval_CallbackFailureLeavesEveryRow(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "pair-removal-callback");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        var pair = DisabledPair("p1");
        store.SavePair(pair);
        var payload = SeedRemovalRows(store, folder, pair);
        var pairNo = RawCount(folder, "SELECT pair_no FROM pair WHERE pair_id='p1';");

        var archiveDirectory = Path.Combine(folder, "archive-callback");
        ExpectThrows<InvalidOperationException>(() => store.ArchiveAndRemovePair("p1", archiveDirectory, true,
            () => throw new InvalidOperationException("prepare invalidation failed")));

        Require(File.Exists(Path.Combine(archiveDirectory, "manifest.json")),
            "a pre-delete callback failure keeps the prewritten archive and manifest");
        RequirePairRowsIntact(folder, pairNo);
        RequirePayloadUnchanged(payload);
        Require(store.Pair("p1") is not null && store.Pairs().Count == 1,
            "a pre-delete callback failure leaves the pair row and its catalog in place");
    }

    private static void PairRemoval_Refusals(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "pair-removal-refusals");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        var enabled = DisabledPair("p1") with { Enabled = true };
        store.SavePair(enabled);

        var refusal = CaptureThrown<InvalidOperationException>(() =>
            store.ArchiveAndRemovePair("p1", Path.Combine(folder, "archive-enabled")));
        Require(refusal.Message.Contains("pairs disable --pair p1 --yes", StringComparison.Ordinal) &&
                refusal.Message.Contains("retry pairs remove", StringComparison.Ordinal),
            "an enabled pair is refused with the exact disable command and a retry instruction");
        ExpectThrows<ArgumentException>(() => store.ArchiveAndRemovePair("missing", Path.Combine(folder, "archive-missing")));

        store.SavePair(enabled with { Enabled = false });
        var archiveDirectory = Path.Combine(folder, "archive-shared");
        store.ArchiveAndRemovePair("p1", archiveDirectory, dropUnfinished: true);
        var second = OtherPair("p2") with { Enabled = false };
        store.SavePair(second);
        ExpectThrows<IOException>(() => store.ArchiveAndRemovePair("p2", archiveDirectory));
        Require(store.Pair("p2") is not null,
            "an existing manifest is refused and the second pair is not removed");
    }

    // PairInclude hides a nested pair's root from the enclosing pair; once the nested pair is removed its
    // subtree is released without any note stored on the enclosing pair.
    private static void PairRemoval_ReleasesNestedPairRoot(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "pair-removal-nested");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        var outer = new PairDef("outer", "outer",
            new RootDef("outer-a", SyncProviders.Local, null, "C:\\outer", "id:outer-a"),
            new RootDef("outer-b", SyncProviders.Dropbox, "acc", "/outer", "id:outer-b"),
            Array.Empty<string>(), Array.Empty<string>(), true);
        var inner = new PairDef("inner", "inner",
            new RootDef("inner-a", SyncProviders.Local, null, "C:\\outer\\inner", "id:inner-a"),
            new RootDef("inner-b", SyncProviders.Dropbox, "acc", "/outer/inner", "id:inner-b"),
            Array.Empty<string>(), Array.Empty<string>(), false);
        store.SavePair(outer);
        store.SavePair(inner);
        foreach (var id in new[] { outer.A.RootId, outer.B.RootId })
        {
            var generation = store.BeginGeneration(id, "snapshot");
            store.CompleteGeneration(generation, "outer-cursor", true);
        }

        var before = PairInclude.For(outer, Side.A, store.Pairs(), []);
        Require(!before("inner/held.txt") && PairsContain(store, "inner"),
            "the enclosing pair hides the nested pair's root while that pair exists");
        var result = store.ArchiveAndRemovePair("inner", Path.Combine(folder, "archive"));
        var after = PairInclude.For(outer, Side.A, store.Pairs(), []);
        Require(store.Cursor(outer.A.RootId) is null && store.Cursor(outer.B.RootId) is null,
            "both enclosing roots forget their cursors to scan the newly admitted subtree");
        Require(after("inner/held.txt") && !PairsContain(store, "inner"),
            "removing the nested pair releases its exact subtree to the enclosing pair");
        Require(result.OrphanedRoots.SequenceEqual(new[] { "inner-a", "inner-b" }),
            "the removed nested pair's roots are reported as orphaned");
    }

    private static void PairRemoval_UnfinishedRequiresExplicitConsent(string root)
    {
        var folder = Fresh(root, "pair-removal-unfinished");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var pair = DisabledPair("p1");
        store.SavePair(pair);
        SeedRemovalRows(store, folder, pair);
        // Include an uncertain whole-folder-delete group; admission counts by state, never by action kind/id.
        store.RecordIntent(pair.PairId, new SyncAction("folder-delete-group", 73, ActionKind.Delete,
            Side.B, "folder", null, null, D("folder", "group-victim"), true, "group-key"));
        using (var connection = OpenRaw(folder))
            StoreSchema.Execute(connection, null,
                "UPDATE pending_effect SET state='uncertain' WHERE action_id='folder-delete-group';");
        var archiveDirectory = Path.Combine(folder, "archive");
        var error = CaptureThrown<InvalidOperationException>(() => store.ArchiveAndRemovePair("p1", archiveDirectory));
        Require(error.Message.Contains("3 unfinished", StringComparison.Ordinal) &&
                error.Message.Contains("run --pair p1 --yes", StringComparison.Ordinal) &&
                error.Message.Contains("pairs enable --pair p1 --yes", StringComparison.Ordinal) &&
                error.Message.Contains("pairs disable --pair p1 --yes", StringComparison.Ordinal) &&
                error.Message.Contains("--drop-unfinished", StringComparison.Ordinal) &&
                error.Message.Contains("cancel", StringComparison.Ordinal),
            "unfinished effects require explicit consent and explain all three decision-74 options");
        Require(store.Pair("p1") is not null && !Directory.Exists(archiveDirectory) &&
                RawCount(folder, "SELECT COUNT(*) FROM pending_effect;") == 3,
            "the default refusal changes neither catalog nor unfinished rows nor archive files");
        var result = store.ArchiveAndRemovePair("p1", archiveDirectory, dropUnfinished: true);
        using var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        Require(result.UnfinishedEffects == 3 && result.RowCounts["pending_effect"] == 3 &&
                manifest.RootElement.GetProperty("Archive").GetProperty("UnfinishedEffects").GetInt64() == 3 &&
                store.Pair("p1") is null,
            "explicit drop archives all unfinished rows and records their count in result and manifest");
    }

    private static void PairRemoval_SharedRootAndArchiveFailure(string root)
    {
        var folder = Fresh(root, "pair-removal-shared");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var removed = DisabledPair("p1");
        var kept = OtherPair("p2") with { A = removed.A };
        store.SavePair(removed);
        store.SavePair(kept);
        var payload = SeedRemovalRows(store, folder, removed);
        SeedBaseline(store, kept.PairId, "shared-kept", F("a.txt", "id:a1", "77"), F("other.txt", "id:other", "77"));
        var pairNo = RawCount(folder, "SELECT pair_no FROM pair WHERE pair_id='p1';");
        var failedArchive = Path.Combine(folder, "bad-archive");
        var error = CaptureThrown<IOException>(() => store.ArchiveAndRemovePair("p1", failedArchive, true,
            beforeRemoval: null, beforeArchiveVerification: path =>
            {
                using var archive = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                archive.Open();
                StoreSchema.Execute(archive, null, "DELETE FROM notice;");
            }));
        Require(error.Message.Contains("count mismatch", StringComparison.Ordinal) &&
                !File.Exists(Path.Combine(failedArchive, "manifest.json")),
            "a corrupt archive count fails verification before manifest publication or deletion");
        RequirePairRowsIntact(folder, pairNo);
        RequirePayloadUnchanged(payload);
        var result = store.ArchiveAndRemovePair("p1", Path.Combine(folder, "good-archive"), true);
        Require(result.OrphanedRoots.SequenceEqual(new[] { "rb" }) && store.Pair("p2") is not null &&
                store.Baselines("p2").Single().ObjectKey == "shared-kept" && store.Live("ra").Count == 3,
            "a shared root retains the other pair's definition, baseline and inventory and is not orphaned");
        using var copied = OpenArchive(result.ArchivePath);
        Require(ArchiveCount(copied, "SELECT COUNT(*) FROM root WHERE root_id='ra';") == 1,
            "a shared root is still included in the removed pair's standalone archive");
    }

    // ---- seeding ----

    private sealed record RemovalPayload(string Path, long Size, string Hash, byte[] Bytes);

    private static RemovalPayload SeedRemovalRows(SqliteStateStore store, string folder, PairDef pair)
    {
        var a = pair.A.RootId;
        var b = pair.B.RootId;
        var genA = store.BeginGeneration(a, "snapshot");
        store.WriteSnapshot(new Snapshot(a, Side.A, genA, "c-a",
        [
            F("a.txt", "id:a1", "77"), F("gone.txt", "id:gone-a", "77"), D("sub", "id:sub-a"),
            F("sub/inner.txt", "id:inner-a", "77"),
        ], []));
        store.CompleteGeneration(genA, "cursor-a", true);

        SeedBaseline(store, pair.PairId, "k-gone", F("gone.txt", "id:gone-a", "77"),
            F("gone.txt", "id:gone-b", "77"));
        SeedBaseline(store, pair.PairId, "k-live", F("a.txt", "id:a1", "77"), F("b.txt", "id:b1", "77"));

        var genA2 = store.BeginGeneration(a, "snapshot");
        store.WriteSnapshot(new Snapshot(a, Side.A, genA2, "c-a2",
        [
            F("a.txt", "id:a1", "77"), D("sub", "id:sub-a"), F("sub/inner.txt", "id:inner-a", "77"),
        ], []));
        store.CompleteGeneration(genA2, "cursor-a", true);

        var genB = store.BeginGeneration(b, "snapshot");
        store.WriteSnapshot(new Snapshot(b, Side.B, genB, "c-b",
        [
            F("b.txt", "id:b1", "77"), D("bsub", "id:sub-b"), F("bsub/inner.txt", "id:inner-b", "77"),
        ], []));
        store.CompleteGeneration(genB, "cursor-b", true);

        store.RecordIntent(pair.PairId, new SyncAction("a-pending", 2, ActionKind.Update, Side.B, "a.txt",
            null, null, null, false, "k-pending"));
        store.AddNotices([new Notice(pair.PairId, 4, NoticeKind.AutoConflict, "a.txt", "conflict",
            "auto-conflict")]);

        var payloadPath = Path.Combine(folder, "recovery", pair.PairId + "-payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        var bytes = Encoding.UTF8.GetBytes("recovery payload must survive pair removal");
        File.WriteAllBytes(payloadPath, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var victim = F("b.txt", "id:b1", "77");
        var recoveryAction = new SyncAction("a-recovery", 6, ActionKind.Delete, Side.B, "b.txt", null, null,
            victim, true, "k-recovery");
        store.RecordIntent(pair.PairId, recoveryAction);
        store.CommitResult(pair.PairId, genB, recoveryAction, new ActionResult("a-recovery", Outcome.Failed,
            null, null, new RecoveryRef(Side.B, payloadPath, "v1", hash, bytes.Length), "boom"));
        store.StoreDeletionEvidence(pair.PairId, a,
            [new DeletionEvidenceRecord("gone.txt", T0, "cursor-a", "77")]);
        SeedRemovalChoiceRows(folder, pair.PairId);
        return new RemovalPayload(payloadPath, bytes.Length, hash, bytes);
    }

    // A second pair carries a row in every pair-scoped table and its own root inventory.
    private static void SeedOtherPairRows(SqliteStateStore store, string folder, PairDef pair)
    {
        var a = pair.A.RootId;
        var gen = store.BeginGeneration(a, "snapshot");
        store.WriteSnapshot(new Snapshot(a, Side.A, gen, "c-c", [F("keep.txt", "id:keep", "77")], []));
        store.CompleteGeneration(gen, "cursor-c", true);
        store.RecordIntent(pair.PairId, new SyncAction("p2-pending", 2, ActionKind.Update, Side.B, "keep.txt",
            null, null, null, false, "k-p2"));
        store.AddNotices([new Notice(pair.PairId, 4, NoticeKind.AutoConflict, "keep.txt", "conflict",
            "auto-conflict")]);
        var recoveryAction = new SyncAction("p2-recovery", 6, ActionKind.Delete, Side.B, "keep.txt", null, null,
            F("keep.txt", "id:keep", "77"), true, "k-p2-recovery");
        store.RecordIntent(pair.PairId, recoveryAction);
        store.CommitResult(pair.PairId, 1, recoveryAction, new ActionResult("p2-recovery", Outcome.Failed, null,
            null, new RecoveryRef(Side.B, Path.Combine(folder, "recovery", "p2.recovery"), "v1", "77", 1), "boom"));
        store.StoreDeletionEvidence(pair.PairId, a,
            [new DeletionEvidenceRecord("old.txt", T0, "cursor-c", "77")]);
        SeedRemovalChoiceRows(folder, pair.PairId);
    }

    private static void SeedRemovalChoiceRows(string folder, string pairId)
    {
        using var connection = OpenRaw(folder);
        StoreSchema.Execute(connection, null, """
            INSERT INTO choice_victim(op_id,pair_no,state,depth,dir_path,name,kind,object_id,size,mtime,flags)
              SELECT pair_id,pair_no,0,0,'','victim.txt',0,'choice-victim',1,0,0 FROM pair WHERE pair_id=$id;
            INSERT INTO choice_restore_receipt(pair_no,action_id,item_json)
              SELECT pair_no,pair_id,'{}' FROM pair WHERE pair_id=$id;
            INSERT INTO choice_merge(notice_id,content)
              SELECT notice_id,X'010203' FROM notice WHERE pair_no=(SELECT pair_no FROM pair WHERE pair_id=$id);
            """, ("$id", pairId));
    }

    private static void RequireRemovalChoiceRows(string folder, string pairId, long expected)
    {
        Require(RawCount(folder, $"SELECT COUNT(*) FROM choice_victim WHERE op_id='{pairId}';") == expected &&
                RawCount(folder, $"SELECT COUNT(*) FROM choice_restore_receipt WHERE action_id='{pairId}';") == expected,
            "choice victims and restore receipts follow only their owning pair");
        Require(RawCount(folder, "SELECT COUNT(*) FROM choice_merge WHERE notice_id NOT IN (SELECT notice_id FROM notice);") == 0 &&
                RawCount(folder, "SELECT COUNT(*) FROM choice_merge WHERE notice_id IN (SELECT notice_id FROM notice " +
                    $"WHERE pair_no=(SELECT pair_no FROM pair WHERE pair_id='{pairId}'));") == expected,
            "merge content follows its owning notice without orphaning or removing another pair's content");
    }

    private static PairDef DisabledPair(string id) => new(id, id,
        new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:local:1"),
        new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:dbx:1"),
        Array.Empty<string>(), Array.Empty<string>(), false);

    private static PairDef OtherPair(string id) => new(id, id,
        new RootDef("rc", SyncProviders.Local, null, "C:\\c", "id:local:2"),
        new RootDef("rd", SyncProviders.Dropbox, "acc", "/d", "id:dbx:2"),
        Array.Empty<string>(), Array.Empty<string>(), true);

    // ---- verification helpers ----

    // Freeze ownership predicates before deleting the catalog row: post-removal joins
    // through pair/notice would hide orphan rows instead of proving they were removed.
    private static Dictionary<string, string> PairRemovalOwnedQueries(SqliteConnection connection, string pairId)
    {
        var number = (long)StoreSchema.Scalar(connection, null,
            "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pairId))!;
        var id = "'" + pairId.Replace("'", "''", StringComparison.Ordinal) + "'";
        var queries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in new[] { "pair", "planning_deferred", "sync_point", "pending_effect", "sync_retry",
                     "choice_victim", "choice_restore_receipt", "notice", "recovery", "deletion_evidence", "notice_legacy_fold" })
            queries[table] = $"SELECT * FROM {table} WHERE pair_no={number};";
        foreach (var table in new[] { "prepare_header", "removal_review" })
            queries[table] = $"SELECT * FROM {table} WHERE pair_id={id};";
        var notices = StoreSchema.Scalar(connection, null,
            "SELECT group_concat(quote(notice_id),',') FROM notice WHERE pair_no=$p;", ("$p", number)) as string ?? "NULL";
        queries["choice_merge"] = $"SELECT * FROM choice_merge WHERE notice_id IN ({notices});";
        var generations = StoreSchema.Scalar(connection, null,
            "SELECT group_concat(quote(generation),',') FROM (SELECT generation FROM prepare_header WHERE pair_id=$p " +
            "UNION SELECT generation FROM removal_review WHERE pair_id=$p);", ("$p", pairId)) as string ?? "NULL";
        foreach (var table in new[] { "prepare_row", "prepare_cursor" })
            queries[table] = $"SELECT * FROM {table} WHERE generation IN ({generations});";
        return queries;
    }

    private static string[] PairRemovalRows(SqliteConnection connection, string sql)
    {
        using var command = StoreSchema.Command(connection, null, sql);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.OrderBy(row => row, StringComparer.Ordinal).ToArray();
    }

    private static void RequireRowCounts(IReadOnlyDictionary<string, long> actual, params (string Table, long Rows)[] expected)
    {
        var same = actual.Count == expected.Length &&
            expected.All(item => actual.TryGetValue(item.Table, out var rows) && rows == item.Rows);
        Require(same, "the reported row counts cover every table exactly: " +
            string.Join(", ", actual.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Key + "=" + item.Value)));
    }

    private static void RequireArchive(string archivePath, IReadOnlyDictionary<string, long> counts)
    {
        using var archive = OpenArchive(archivePath);
        StoreSchema.Verify(archive);
        Require(true, "the archive is a standalone database with the full current StoreSchema");
        Require(counts.All(item => ArchiveCount(archive, "SELECT COUNT(*) FROM " + item.Key + ";") == item.Value),
            "every archived table holds exactly the rows reported by PairRemovalArchive.RowCounts");
    }

    private static void RequirePairRowsIntact(string dataRoot, long pairNo)
    {
        RequireRemovalChoiceRows(dataRoot, "p1", 1);
        Require(RawCount(dataRoot, "SELECT COUNT(*) FROM pair WHERE pair_id='p1';") == 1 &&
                RawCount(dataRoot, $"SELECT COUNT(*) FROM sync_point WHERE pair_no={pairNo};") == 2 &&
                RawCount(dataRoot, $"SELECT COUNT(*) FROM pending_effect WHERE pair_no={pairNo};") == 2 &&
                RawCount(dataRoot, $"SELECT COUNT(*) FROM notice WHERE pair_no={pairNo};") == 1 &&
                RawCount(dataRoot, $"SELECT COUNT(*) FROM recovery WHERE pair_no={pairNo};") == 1 &&
                RawCount(dataRoot, $"SELECT COUNT(*) FROM deletion_evidence WHERE pair_no={pairNo};") == 1,
            "every pair-scoped table survives the failed removal transaction");
        Require(RawCount(dataRoot, "SELECT COUNT(*) FROM file_state WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('ra','rb'));") == 6 &&
                RawCount(dataRoot, "SELECT COUNT(*) FROM generation WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('ra','rb'));") == 3 &&
                RawCount(dataRoot, "SELECT COUNT(*) FROM tombstone WHERE root_no IN " +
                    "(SELECT root_no FROM root WHERE root_id IN ('ra','rb'));") == 1,
            "root inventory, generations and tombstones survive the failed removal transaction");
    }

    private static void RequirePayloadUnchanged(RemovalPayload payload)
    {
        Require(File.Exists(payload.Path), "the recovery payload file is retained");
        var bytes = File.ReadAllBytes(payload.Path);
        Require(bytes.Length == payload.Size &&
                string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), payload.Hash, StringComparison.Ordinal),
            "the recovery payload file's exact bytes are unchanged");
    }

    private static bool PairsContain(SqliteStateStore store, string pairId) =>
        store.Pairs().Any(pair => string.Equals(pair.PairId, pairId, StringComparison.Ordinal));

    private static SqliteConnection OpenRaw(string dataRoot)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath(dataRoot), Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenArchive(string archivePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archivePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long RawCount(string dataRoot, string sql)
    {
        using var connection = OpenRaw(dataRoot);
        return ArchiveCount(connection, sql);
    }

    private static long ArchiveCount(SqliteConnection connection, string sql) =>
        (long)StoreSchema.Scalar(connection, null, sql)!;

    private static TException CaptureThrown<TException>(Action operation) where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException expected)
        {
            return expected;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
