using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Behaviour checks for the SQLite state store, run against both the real and in-memory stores.</summary>
internal static partial class StorageChecks
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>Settings for disposable test databases: no per-commit fsync, so the suite stays fast.</summary>
    internal static readonly V2Settings Fast = V2Settings.Default with { StoreDurableCommits = false };

    public static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        IntegratedSchemaChecks(root);
        await JournalSizingChecks.RunAsync(root);
        OpenFreshReopen(root);
        OpenDamagedRoots(root);
        PrepareStorageChecks(root);
        Both(root, "round-trip", SnapshotRoundTrip);
        SnapshotUnchangedWritesNothing(root);
        SnapshotStreamingChecks(root);
        Both(root, "absent-close", SnapshotAbsentClosed);
        Both(root, "excluded-snapshot", SnapshotExcludedRowsSurvive);
        Both(root, "excluded-delta", DeltaExcludedRowsSurvive);
        SnapshotHashCarryForward(root);
        SnapshotDuplicatePathThrows(root);
        Both(root, "folder-delete", DeltaFolderDeleteThenUpsert);
        Both(root, "move", DeltaMoveByObject);
        DeltaCursorInvalidThrows(root);
        DeltaReplayIdempotent(root);
        DeltaProportionalWrites(root);
        Tombstone_SnapshotRemoveAndReadd(root);
        Tombstone_DeltaDeleteAndMoveSuppression(root);
        Tombstone_RootScopedResolution(root);
        Tombstone_NotAgePruned(root);
        Move_RemovalAndInsertionAtomic(root);
        Tombstone_ReplacementKeepsDisplacedEvidence(root);
        Tombstone_PathReuseKeepsUnresolved(root);
        Tombstone_OnlyForBaselined(root);
        Tombstone_ExecutorDeletionObserved(root);
        Tombstone_ReplacementResolved(root);
        Tombstone_SharedByTwoBaselines(root);
        GenerationRules(root);
        Both(root, "pairs", PairsRoundTrip);
        Both(root, "forget-cursor", ForgetCursorKeepsBaselinesAndLive);
        ForgetCursor_ChangesOnlyTheCursor(root);
        PairRollbackDoesNotCacheRoot(root);
        Both(root, "live-under", LiveUnderSelfAndDescendants);
        Both(root, "case-insensitive", PathLookupsIgnoreCase);
        Both(root, "batched-lookups", BatchedPathLookups);
        Both(root, "two-roots-case", PathLookups_TwoRootsDifferentCase);
        RunHeldPerformanceChecks(root);
        Both(root, "non-ascii", PathLookups_NonAscii);
        Both(root, "case-destination", Upsert_CaseInsensitiveDestination);
        Both(root, "long-s", PathLookups_LongS);
        Both(root, "case-clash", CaseClash_KeptUntilEvidence);
        Both(root, "clash-replay", CaseClash_DeleteReplayKeepsOthers);
        Both(root, "clash-move", CaseClash_DeleteMoveKeepsOthers);
        Both(root, "clash-idless-replay", CaseClash_ReplayWithoutIdentityKeepsSurvivors);
        Both(root, "identity-exact", Delete_IdentityBoundToExactPath);
        Both(root, "clash-same-spelling", CaseClash_DeleteUpsertSameSpellingKeepsSibling);
        Both(root, "live-spellings", LiveSpellings_KeyOnly);
        Both(root, "incomplete-subtree", Snapshot_IncompleteSubtreeIsSelfOrUnder);
        Both(root, "subtree-replay", FolderDelete_ReplayAfterPartial);
        FolderDelete_CrashBetweenBatches(root);
        Both(root, "touching", BaselinesTouchingChecks);
        Both(root, "commit-copy", CommitDoneBaselineAndIntent);
        Both(root, "commit-delete", CommitDeleteDoneRemovesBaseline);
        CommitFolderDeleteRetiresCarriedRows(root);
        Both(root, "commit-uncertain", CommitUncertainAndFailedUnresolved);
        CommitHoldThrows(root);
        CommitSizeMismatchThrows(root);
        CommitResultOtherPairThrows(root);
        await Executor_FolderDeleteExpansion_RealStore(root);
        await Executor_MoveThenUpdate_RealStore(root);
        Intents_ResolvedDeletedAtCommit(root);
        Both(root, "held-classification", IntentHolds_ClassificationCountAndPaging);
        Both(root, "held-upsert", IntentHolds_RecordIntentsUpsert);
        Both(root, "held-batch", IntentHolds_BatchClassification);
        Both(root, "held-ownership", IntentHolds_OwnershipEnforced);
        Both(root, "held-batch-rollback", IntentHolds_BatchOwnershipRollsBack);
        IntentOrderIndex_RecreatedOnOpen(root);
        Both(root, "intent-stream-horizon", IntentStream_RowidHorizonIsMutationStable);
        Notices_OnePerProblem(root);
        FreeSpacePresentationRoundTrip(root);
        NoticeVersionChecks(root);
        Both(root, "recovery", CommitRecoveryRecorded);
        Both(root, "intent-json", IntentJsonRoundTrip);
        ProviderCheckpoint_ReopenUncertainDone(root);
        Both(root, "notices", NoticesIdempotent);
        PruneRemovesHistory(root);
        RunHistoryChecks(root);
        PairRemovalChecks(root);
        ShortIdDeterministic();
        StorageChecksScale.Run(root);
        Console.WriteLine("v2 storage checks passed.");
    }

    // ---- open ----

    private static void OpenFreshReopen(string root)
    {
        var folder = Fresh(root, "fresh-reopen");
        // The production default commits durably (synchronous=FULL); the other checks use Fast (NORMAL).
        using (var store = SqliteStateStore.Open(folder, V2Settings.Default, new TestClock()))
            Require(store.Cursor("x") is null && store.SynchronousMode() == 2,
                "a fresh store has no cursor and commits with synchronous=FULL by default");
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
            Require(store.Cursor("x") is null && store.SynchronousMode() == 1,
                "a reopened store has no cursor (and its schema verifies); StoreDurableCommits=false is NORMAL");
    }

    private static void OpenDamagedRoots(string root)
    {
        var version = Fresh(root, "wrong-version");
        using (var store = SqliteStateStore.Open(version, Fast, new TestClock())) { }
        StorageDamage.PokeUserVersion(DbPath(version), StoreSchema.Version + 1);
        ExpectThrows<StateDamagedException>(() =>
        {
            using var store = SqliteStateStore.Open(version, Fast, new TestClock());
        });
        Require(true, "a newer user_version is rejected as damage");

        // Pre-v3 schemas still require a fresh database; supported v3/v4 upgrades are additive.
        var older = Fresh(root, "older-version");
        using (var store = SqliteStateStore.Open(older, Fast, new TestClock())) { }
        StorageDamage.PokeUserVersion(DbPath(older), 2);
        Exception? outdated = null;
        try
        {
            using var store = SqliteStateStore.Open(older, Fast, new TestClock());
        }
        catch (Exception e)
        {
            outdated = e;
        }
        Require(outdated is StateNeedsFreshDatabaseException { FoundVersion: 2 } &&
            outdated.Message.Contains("needs a fresh database", StringComparison.Ordinal),
            "an unsupported pre-v3 schema needs a fresh database, not damage recovery: " + outdated?.GetType().Name);

        // The existing v3 upgrade remains supported alongside v4's additive failure metadata.
        var migrated = Fresh(root, "migrate-version");
        using (var store = SqliteStateStore.Open(migrated, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            var gen = store.BeginGeneration("r1", "full");
            store.WriteSnapshot(new Snapshot("r1", Side.A, gen, "v3-cursor",
                [new ItemState("preserved.txt", ItemKind.File, "v3-id", "v3-rev", "aabbccdd", 7,
                    T0, ItemFlags.None)], []));
            store.CompleteGeneration(gen, "v3-cursor", true);
        }
        using (var connection = new SqliteConnection($"Data Source={DbPath(migrated)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = DropPrepareSchema + "DROP TABLE deletion_evidence; PRAGMA user_version=3;";
            command.ExecuteNonQuery();
        }
        var deletedAt = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        using (var store = SqliteStateStore.Open(migrated, Fast, new TestClock()))
        {
            Require(store.Cursor("r1") == "v3-cursor" &&
                store.Live("r1", ["preserved.txt"]).Single().ObjectId == "v3-id" && store.Pair("p1") is not null,
                "a real populated v3 database preserves its generation, file and pair rows during migration");
            store.SavePair(Pair("p1"));
            store.BeginGeneration("r1", "full");
            store.StoreDeletionEvidence("p1", "r1",
                [new DeletionEvidenceRecord("a.txt", deletedAt, ContentHash: "hash-a"),
                 new DeletionEvidenceRecord("b.txt", null)]);
            var read = store.DeletionEvidenceRows("p1", "r1").ToDictionary(row => row.Path);
            Require(read.Count == 2 && read["a.txt"].DeletedUtc == deletedAt &&
                read["a.txt"].ContentHash == "hash-a" && read["b.txt"].DeletedUtc is null &&
                read["b.txt"].ContentHash is null && !read.ContainsKey("c.txt"),
                "stored deletion evidence round-trips its time and last-revision hash; an unprobed path is absent");
        }
        using (var reopened = SqliteStateStore.Open(migrated, Fast, new TestClock()))
        {
            var again = reopened.DeletionEvidenceRows("p1", "r1").Single(row => row.Path == "a.txt");
            Require(again.DeletedUtc == deletedAt && again.ContentHash == "hash-a",
                "deletion evidence and its last-revision hash survive a reopen");
        }

        using (var connection = new SqliteConnection($"Data Source={DbPath(migrated)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = DropPrepareSchema + "ALTER TABLE deletion_evidence DROP COLUMN basis_cursor;" +
                "ALTER TABLE deletion_evidence DROP COLUMN content_hash;" +
                "ALTER TABLE deletion_evidence DROP COLUMN failure_json; PRAGMA user_version=4;";
            command.ExecuteNonQuery();
        }
        using (var upgradedPreview = SqliteStateStore.Open(migrated, Fast, new TestClock()))
        {
            Require(upgradedPreview.DeletionEvidenceRows("p1", "r1")
                    .All(row => row.BasisCursor is null && row.ContentHash is null),
                "an existing v4 preview gains nullable cursor and content-hash columns without fabricating validity");
            Require(upgradedPreview.Live("r1", ["preserved.txt"]).Count == 1,
                "upgrading a v4 preview preserves existing file rows");
            upgradedPreview.StoreDeletionEvidence("p1", "r1", [new DeletionEvidenceRecord("failed.txt", null,
                Failure: new(3, T0, 42, true))]);
        }
        using (var reopened = SqliteStateStore.Open(migrated, Fast, new TestClock()))
        {
            Require(reopened.DeletionEvidenceRows("p1", "r1").Single(row => row.Path == "failed.txt").Failure ==
                new DeletionEvidenceFailure(3, T0, 42, true),
                "failure attempts, age, generation and released hold survive SQLite reopen");
        }

        var extra = Fresh(root, "extra-table");
        using (var store = SqliteStateStore.Open(extra, Fast, new TestClock())) { }
        StorageDamage.AddTable(DbPath(extra), "extra");
        ExpectThrows<StateDamagedException>(() =>
        {
            using var store = SqliteStateStore.Open(extra, Fast, new TestClock());
        });
        Require(true, "an extra table is rejected");

        var garbage = Path.Combine(root, "garbage");
        var dbPath = DbPath(garbage);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath, Enumerable.Repeat((byte)0x5A, 4096).ToArray());
        ExpectThrows<StateDamagedException>(() =>
        {
            using var store = SqliteStateStore.Open(garbage, Fast, new TestClock());
        });
        Require(true, "a garbage file is rejected");

        // The schema verifies, so the corruption is first met by Load, which must still be inside Open's guard.
        var load = Fresh(root, "load-corrupt");
        using (var store = SqliteStateStore.Open(load, Fast, new TestClock())) store.SavePair(Pair("p1"));
        StorageDamage.CorruptTable(DbPath(load), "root");
        Exception? caught = null;
        try
        {
            using var store = SqliteStateStore.Open(load, Fast, new TestClock());
        }
        catch (Exception e)
        {
            caught = e;
        }
        Require(caught is StateDamagedException { InnerException: SqliteException { SqliteErrorCode: 11 } },
            "corruption first met while loading roots and pairs is reported as damage: " + caught?.GetType().Name);
    }

    // ---- snapshots ----

    private static void SnapshotRoundTrip(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1",
        [
            F("a.txt", "id:a", "AABB01"),
            F("d/e/f/g.txt", "id:g", "ccdd02", 9, T0.AddTicks(7), ItemFlags.Locked),
            D("d/e/f", "id:f"),
        ], []));
        store.CompleteGeneration(gen, "c1", true);
        RequireLive(store, "root", F("a.txt", "id:a", "AABB01"),
            F("d/e/f/g.txt", "id:g", "ccdd02", 9, T0.AddTicks(7), ItemFlags.Locked), D("d/e/f", "id:f"));
    }

    private static void SnapshotUnchangedWritesNothing(string root)
    {
        var folder = Fresh(root, "unchanged");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var items = Enumerable.Range(0, 1000).Select(i => F($"f{i:D4}.txt", $"id:{i:D4}", i.ToString("x8"))).ToArray();
        var gen1 = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen1, "c1", items, []));
        store.CompleteGeneration(gen1, "c1", true);
        var gen2 = store.BeginGeneration("root", "snapshot");
        var before = store.ChangeCount();
        store.WriteSnapshot(new Snapshot("root", Side.A, gen2, "c2", items, []));
        Require(store.ChangeCount() == before, "an unchanged snapshot writes no rows");
        store.CompleteGeneration(gen2, "c2", true);
    }

    private static void SnapshotAbsentClosed(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        var all = new[] { F("a", "id:a", "11"), F("docs/b", "id:b", "22"), F("docs/c", "id:c", "33") };
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1", all, []));
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1", [all[0]], ["docs"]));
        RequireLive(store, "root", all);
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1", [all[0]], [""]));
        RequireLive(store, "root", all);
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1", [all[0]], []));
        RequireLive(store, "root", all[0]);
    }

    // RQ-021: pruning an excluded subtree is not evidence that its stored rows disappeared.
    private static void SnapshotExcludedRowsSurvive(IStateStore store)
    {
        var excluded = F("d/private/keep.txt", "id:keep", "11");
        var folder = D("d/private", "id:private");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1",
            [D("d", "id:d"), folder, excluded, F("d/gone.txt", "id:gone", "22")], []));
        bool Include(string path) => !PairPath.IsSelfOrUnder(path, "d/private");
        var next = store.BeginGeneration("root", "snapshot");
        var snapshot = new Snapshot("root", Side.A, next, "c2",
            [excluded with { Hash = "ff" }, F("d/private/new.txt", "id:new", "33")], [])
            { IncludesPath = Include };
        store.WriteSnapshot(snapshot);
        RequireLive(store, "root", folder, excluded);
        store.WriteSnapshot(snapshot with { Gen = store.BeginGeneration("root", "snapshot"), Items = [] });
        RequireLive(store, "root", folder, excluded);
        Require(store.ByObject("root", excluded.ObjectId)?.Path == excluded.Path,
            "an excluded row remains addressable after a full snapshot and replay");
    }

    private static void DeltaExcludedRowsSurvive(IStateStore store)
    {
        var folder = D("d/private", "id:private");
        var excluded = F("d/private/keep.txt", "id:keep", "11");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c1",
            [D("d", "id:d"), folder, excluded, F("d/gone.txt", "id:gone", "22")], []));
        bool Include(string path) => !PairPath.IsSelfOrUnder(path, "d/private");
        var moved = excluded with { Path = "visible.txt" };
        var delta = new Delta("root", Side.A, store.BeginGeneration("root", "delta"), "c1", "c2",
            [new Change(ChangeKind.Delete, "id:d", "d", null, T0),
             new Change(ChangeKind.Upsert, excluded.ObjectId, excluded.Path, excluded with { Hash = "ff" }, null),
             new Change(ChangeKind.Upsert, "id:new", "d/private/new.txt", F("d/private/new.txt", "id:new", "33"), null),
             new Change(ChangeKind.Upsert, moved.ObjectId, moved.Path, moved, null)], false, [])
            { IncludesPath = Include };
        store.WriteDelta(delta);
        RequireLive(store, "root", folder, excluded, moved);
        store.WriteDelta(delta with { Gen = store.BeginGeneration("root", "delta") });
        RequireLive(store, "root", folder, excluded, moved);
        // Identity-less replay still expands the ancestor after its own row has already disappeared.
        store.WriteDelta(delta with { Gen = store.BeginGeneration("root", "delta"),
            Changes = [new Change(ChangeKind.Delete, "", "d", null, T0)] });
        RequireLive(store, "root", folder, excluded, moved);
    }

    private static void SnapshotHashCarryForward(string root)
    {
        var folder = Fresh(root, "carry");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("a.txt", "id:a", "11", 5)], []));
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [NoHash("a.txt", "id:a", 5)], []));
        Require(store.Live("root")[0].Hash == "11", "a metadata-only inventory keeps the known hash");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [NoHash("a.txt", "id:a", 6)], []));
        Require(store.Live("root")[0].Hash is null, "a size change drops the carried hash");
    }

    private static void SnapshotDuplicatePathThrows(string root)
    {
        var folder = Fresh(root, "duplicate");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var gen = store.BeginGeneration("root", "snapshot");
        ExpectThrows<ArgumentException>(() => store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c",
            [F("a.txt", "id:a", "11"), F("a.txt", "id:b", "22")], [])));
    }

    // ---- deltas ----

    private static void DeltaFolderDeleteThenUpsert(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c",
            [D("d", "id:d"), F("d/x", "id:x", "44"), F("d/e/y", "id:y", "55")], []));
        var delta = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, delta, "c", "d", [
            new Change(ChangeKind.Upsert, "id:z", "d/z", F("d/z", "id:z", "66"), null),
            new Change(ChangeKind.Delete, "id:d", "d", null, T0),
        ], false, []));
        RequireLive(store, "root", F("d/z", "id:z", "66"));
    }

    private static void DeltaMoveByObject(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("a", "id1", "11")], []));
        var delta = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, delta, "c", "d",
            [new Change(ChangeKind.Upsert, "id1", "b", F("b", "id1", "11"), null)], false, []));
        RequireLive(store, "root", F("b", "id1", "11"));
        Require(store.ByObject("root", "id1")?.Path == "b", "the moved object resolves to its new path");
    }

    private static void DeltaCursorInvalidThrows(string root)
    {
        var folder = Fresh(root, "cursor-invalid");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.BeginGeneration("root", "snapshot");
        var gen = store.BeginGeneration("root", "delta");
        ExpectThrows<InvalidOperationException>(() => store.WriteDelta(new Delta("root", Side.A, gen, "c", "c",
            [], true, [])));
    }

    private static void DeltaReplayIdempotent(string root)
    {
        var folder = Fresh(root, "replay");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var gen1 = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen1, "c", [F("a", "id1", "11")], []));
        var changes = new[]
        {
            new Change(ChangeKind.Upsert, "id1", "a", F("a", "id1", "22"), null),
            new Change(ChangeKind.Delete, "id2", "b", null, T0),
        };
        var delta1 = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, delta1, "c", "d", changes, false, []));
        var delta2 = store.BeginGeneration("root", "delta");
        var before = store.ChangeCount();
        store.WriteDelta(new Delta("root", Side.A, delta2, "d", "e", changes, false, []));
        Require(store.ChangeCount() == before, "replaying a delta in a new generation writes nothing");
    }

    private static void DeltaProportionalWrites(string root)
    {
        var folder = Fresh(root, "proportional");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var items = Enumerable.Range(0, 1000).Select(i => F($"f{i:D4}", $"id:{i:D4}", i.ToString("x8"))).ToArray();
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", items, []));
        var delta = store.BeginGeneration("root", "delta");
        var changes = new List<Change>
        {
            new(ChangeKind.Upsert, "id:0000", "f0000", F("f0000", "id:0000", "beef0000"), null),
            new(ChangeKind.Upsert, "id:0001", "f0001", F("f0001", "id:0001", "beef0001"), null),
            new(ChangeKind.Upsert, "id:0002", "f0002", F("f0002", "id:0002", items[2].Hash!), null),
            new(ChangeKind.Delete, "id:0003", "f0003", null, T0),
        };
        var before = store.ChangeCount();
        store.WriteDelta(new Delta("root", Side.A, delta, "c", "d", changes, false, []));
        var written = store.ChangeCount() - before;
        // The deleted row has no baseline, so it leaves no tombstone .
        Require(written == 5, $"a four-row delta wrote {written} rows, expected 5 (2 edits, 1 delete, 1 no-op)");
        var touched = store.Live("root", ["f0000", "f0001", "f0002"]).ToDictionary(item => item.Path, StringComparer.Ordinal);
        Require(touched.Count == 3 && touched["f0000"].Hash == "beef0000" && touched["f0001"].Hash == "beef0001" &&
            SameItem(touched["f0002"], items[2]),
            "the delta kept the three touched rows, two edited and one unchanged");
        Require(store.Live("root").Count == 999, "the delta removed exactly one live row");
    }

    // ---- tombstones ----

    private static void Tombstone_SnapshotRemoveAndReadd(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "tombstone-snapshot");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        Track(store, "root", "id:a");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("a.txt", "id:a", "11")], []));
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [], []));
        var tombstones = store.Tombstones("root");
        Require(tombstones.Count == 1 && tombstones[0].Path == "a.txt" && tombstones[0].ObjectId == "id:a" &&
            tombstones[0].DeletedAt == clock.UtcNow, "a snapshot removal records one tombstone with the removal time");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("a.txt", "id:a", "11")], []));
        Require(store.Tombstones("root").Count == 0, "a snapshot reappearance clears the tombstone");
    }

    private static void Tombstone_DeltaDeleteAndMoveSuppression(string root)
    {
        var folder = Fresh(root, "tombstone-delta");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        Track(store, "root", "id1", "id2");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c",
            [F("a.txt", "id1", "11"), F("b.txt", "id2", "22")], []));
        var delta = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, delta, "c", "d", [
            new Change(ChangeKind.Delete, "id1", "a.txt", null, T0.AddMinutes(1)),
            new Change(ChangeKind.Upsert, "id2", "c.txt", F("c.txt", "id2", "22"), null),
        ], false, []));
        var tombstones = store.Tombstones("root");
        Require(tombstones.Count == 1 && tombstones[0].ObjectId == "id1",
            "a delta delete records a tombstone and a move in the same delta does not");
        RequireLive(store, "root", F("c.txt", "id2", "22"));
    }

    private static void Tombstone_RootScopedResolution(string root)
    {
        var folder = Fresh(root, "tombstone-roots");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("a.txt", "id:a", "11"), F("b.txt", "id:b", "11"));
        Track(store, "other", "id:a");
        Remove(store, "ra", "a.txt", "id:a");
        Remove(store, "other", "a.txt", "id:a");
        Remove(store, "rb", "b.txt", "id:b");
        var delete = new SyncAction("a-del", 6, ActionKind.Delete, Side.B, "a.txt", null, null, F("b.txt", "id:b", "11"), true, "k1");
        store.CommitResult("p1", 6, delete, new ActionResult("a-del", Outcome.Done, null, null, null, null));
        Require(store.Tombstones("ra").Count == 0 && store.Tombstones("rb").Count == 0,
            "resolving a baseline clears only its own roots' tombstones");
        Require(store.Tombstones("other").Count == 1,
            "a tombstone with the same object id on another root survives");
    }

    private static void Tombstone_NotAgePruned(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "tombstone-prune");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        Track(store, "root", "id:a");
        Remove(store, "root", "a.txt", "id:a");
        Require(store.Tombstones("root").Count == 1, "the tombstone exists before pruning");
        clock.UtcNow = clock.UtcNow.AddDays(31);
        store.Prune();
        Require(store.Tombstones("root").Count == 1, "prune keeps a tombstone until its baseline is resolved");
    }

    // ---- generations ----

    private static void GenerationRules(string root)
    {
        var folder = Fresh(root, "generation");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        var first = store.BeginGeneration("root", "snapshot");
        Require(store.Cursor("root") is null, "the cursor only moves on complete");
        store.CompleteGeneration(first, "c1", true);
        Require(store.Cursor("root") == "c1", "the cursor round-trips when the generation is complete");

        var partial = store.BeginGeneration("root", "snapshot");
        store.CompleteGeneration(partial, "c2", false);
        Require(store.Cursor("root") == "c1", "an incomplete generation does not advance the cursor");

        ExpectThrows<InvalidOperationException>(() => store.CompleteGeneration(first, "c3", true));
        var abandoned = store.BeginGeneration("root", "snapshot");
        store.BeginGeneration("root", "snapshot");
        ExpectThrows<InvalidOperationException>(() => store.CompleteGeneration(abandoned, "c4", true));
        var other = store.BeginGeneration("other", "snapshot");
        ExpectThrows<InvalidOperationException>(() =>
            store.WriteSnapshot(new Snapshot("root", Side.A, other, "c", [F("a", "id:a", "11")], [])));
    }

    // ---- pairs and reads ----

    private static void PairsRoundTrip(IStateStore store)
    {
        var pair = Pair("p1");
        store.SavePair(pair);
        var read = store.Pair("p1");
        Require(read is not null && SamePair(read, pair) && store.Pairs().Count == 1, "a pair round-trips");
        Require(!store.FirstSyncDone("p1"), "a fresh pair has not finished its first sync");
        Require(store.Pair("missing") is null, "an unknown pair is null");
        ExpectThrows<InvalidOperationException>(() => store.MarkFirstSyncDone("missing"));
        store.MarkFirstSyncDone("p1");
        Require(store.FirstSyncDone("p1"), "the first-sync marker round-trips");
        store.SavePair(pair with { Enabled = false });
        Require(store.FirstSyncDone("p1"), "re-saving a pair keeps its first-sync marker");
        var genA = store.BeginGeneration(pair.A.RootId, "full");
        var genB = store.BeginGeneration(pair.B.RootId, "full");
        store.CompleteGeneration(genA, "a-covered", true);
        store.CompleteGeneration(genB, "b-covered", true);
        store.SavePair(pair with { Name = "renamed" });
        Require(store.Cursor(pair.A.RootId) == "a-covered", "a display-only edit keeps inventory coverage");
        store.SavePair(pair with { Excluded = ["private"] });
        Require(store.Cursor(pair.A.RootId) == "a-covered" && store.Cursor(pair.B.RootId) == "b-covered",
            "narrowing exclusions retains both sides' execution cursors and coverage");
        store.CompleteGeneration(store.BeginGeneration(pair.A.RootId, "full"), "excluded-covered", true);
        store.SavePair(pair);
        Require(store.Cursor(pair.A.RootId) is null && store.Cursor(pair.B.RootId) is null,
            "re-including a subtree invalidates both execution cursors for safe coverage recovery");
    }

    // The full rescan a `pair rescan` forces reads no journal delete, so it must keep the baselines it detects
    // deletions from; only the stored cursor goes.
    private static void ForgetCursorKeepsBaselinesAndLive(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var item = F("a.txt", "id:a", "77");
        SeedBaseline(store, "p1", "k1", item, F("a.txt", "id:b", "77"));
        var gen = store.BeginGeneration("ra", "full");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "cursor-a", [item], []));
        store.CompleteGeneration(gen, "cursor-a", true);
        var peer = store.BeginGeneration("rb", "full");
        store.WriteSnapshot(new Snapshot("rb", Side.B, peer, "cursor-b", [], []));
        store.CompleteGeneration(peer, "cursor-b", true);
        Require(store.Cursor("ra") == "cursor-a", "a completed generation stores its cursor");

        store.ForgetCursor("ra");

        Require(store.Cursor("ra") is null, "ForgetCursor clears the root's stored cursor so its next scan is full");
        RequireLive(store, "ra", item);
        var baselines = store.Baselines("p1");
        Require(baselines.Count == 1 && baselines[0].ObjectKey == "k1",
            "ForgetCursor leaves the baseline the full rescan detects deletions from");
        store.ForgetCursor("unknown-root");
        Require(store.Cursor("rb") == "cursor-b", "ForgetCursor on an unknown root changes nothing");
    }

    // The storage primitive must move nothing but the cursor: every row a full rescan relies on (baselines, live
    // rows, tombstones, generations, intents) is read straight from the database before and after and compared.
    private static void ForgetCursor_ChangesOnlyTheCursor(string root)
    {
        var folder = Fresh(root, "forget-cursor-untouched");
        using var store = SqliteStateStore.Open(folder, V2Settings.Default, new TestClock());
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("x.txt", "id:a", "11"), F("x.txt", "id:b", "11"));
        SeedBaseline(store, "p1", "k2", F("gone.txt", "id:t", "22"), F("gone.txt", "id:t2", "22"));
        var gen = store.BeginGeneration("ra", "full");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "cursor-a",
            [F("x.txt", "id:a", "11"), F("gone.txt", "id:t", "22")], []));
        // Closing a baselined object leaves deletion evidence, so the fixture holds a tombstone too.
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "cursor-a", [F("x.txt", "id:a", "11")], []));
        store.CompleteGeneration(gen, "cursor-a", true);
        var peer = store.BeginGeneration("rb", "full");
        store.WriteSnapshot(new Snapshot("rb", Side.B, peer, "cursor-b", [F("x.txt", "id:b", "11")], []));
        store.CompleteGeneration(peer, "cursor-b", true);
        store.RecordIntent("p1", new SyncAction("i1", 1, ActionKind.CopyNew, Side.B, "x.txt", null,
            F("x.txt", "id:a", "11"), null, false, "k1"));
        Require(store.Tombstones("ra").Count == 1 && store.UnresolvedIntents("p1").Count == 1,
            "the fixture holds a tombstone and an unresolved intent");

        var before = UntouchedRows(folder);
        store.ForgetCursor("ra");
        var after = UntouchedRows(folder);

        Require(before.Count > 0 && before.SequenceEqual(after),
            "ForgetCursor leaves every baseline, live row, tombstone, generation and intent unchanged");
        Require(store.Cursor("ra") is null && store.Cursor("rb") == "cursor-b",
            "ForgetCursor clears only the named root's cursor");
    }

    // The five tables a full rescan depends on, dumped in a stable order straight from the database (tombstones
    // and generations have no IStateStore reader).
    private static IReadOnlyList<string> UntouchedRows(string dataRoot) =>
    [
        .. RawRows(dataRoot, "SELECT root_no, object_id, dir_no, name, deleted_at FROM tombstone ORDER BY root_no, object_id"),
        .. RawRows(dataRoot, "SELECT gen, root_no, kind, started, ifnull(finished, -1), ifnull(complete, -1) FROM generation ORDER BY gen"),
        .. RawRows(dataRoot, "SELECT action_id, pair_no, state, attempts, updated, action_json FROM pending_effect ORDER BY action_id"),
        .. RawRows(dataRoot, "SELECT root_no, dir_no, name_key, name, object_id, ifnull(hex(hash), ''), size, mtime FROM file_state ORDER BY root_no, dir_no, name_key, name"),
        .. RawRows(dataRoot, "SELECT pair_no, object_key, gen, kind, a_object_id, b_object_id, ifnull(b_dir, -1), ifnull(b_name, '') FROM sync_point ORDER BY pair_no, object_key"),
    ];

    private static IReadOnlyList<string> RawRows(string dataRoot, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath(dataRoot), Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values.Select(value => value?.ToString() ?? string.Empty)));
        }
        return rows;
    }

    private static void PairRollbackDoesNotCacheRoot(string root)
    {
        var folder = Fresh(root, "rollback");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        ExpectThrows<SqliteException>(() => store.SavePair(Pair("p1") with { PairId = null! }));
        Require(store.Pair("p1") is null, "a failed save rolls back its pair and roots");
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [F("a.txt", "id:a", "11")], []));
        store.CompleteGeneration(gen, "c1", true);
        Require(store.Cursor("ra") == "c1", "a rolled-back transaction did not poison the root cache");
    }

    private static void LiveUnderSelfAndDescendants(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c",
        [
            F("a", "id:a", "77"), D("d", "id:d"), F("d/x", "id:x", "77"), F("d/e/y", "id:y", "77"),
            F("dd/z", "id:z", "77"),
        ], []));
        var under = store.LiveUnder("root", ["d"]).Select(item => item.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Require(under.SequenceEqual(["d", "d/e/y", "d/x"]), $"LiveUnder('d') = [{string.Join(", ", under)}]");
        Require(store.LiveUnder("root", [""]).Count == 5, "LiveUnder with the empty prefix returns every row");
    }

    private static void BatchedPathLookups(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        var items = Enumerable.Range(0, 300).Select(i => F($"Docs/{i:D4}.txt", $"id:{i}", "77")).ToArray();
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", items, []));
        var requested = items.Select(item => item.Path.ToUpperInvariant()).Reverse()
            .Concat(items.Select(item => item.Path)).Concat(new[] { "", "missing" });
        var actual = store.Live("root", requested);
        Require(actual.Count == items.Length && actual.Select(item => item.ObjectId).ToHashSet().SetEquals(items.Select(item => item.ObjectId)),
            "bounded batches retain every distinct requested entity across batch boundaries");
        Require(actual.All(item => item.Path.StartsWith("Docs/", StringComparison.Ordinal)),
            "batched lookup preserves stored spelling");
    }

    private static void PathLookupsIgnoreCase(IStateStore store)
    {
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c",
            [F("Docs/child.txt", "id:c", "77"), F("Docs/Other.txt", "id:o", "77")], []));
        var under = store.LiveUnder("root", ["docs"]);
        Require(under.Count == 2 && under.All(item => item.Path.StartsWith("Docs/", StringComparison.Ordinal)),
            "LiveUnder(['docs']) returns the stored 'Docs/child.txt' and 'Docs/Other.txt'");
        var exact = store.Live("root", ["docs/child.TXT"]);
        Require(exact.Count == 1 && exact[0].Path == "Docs/child.txt",
            "Live matches ignoring case and keeps the stored spelling");
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("Docs/child.txt", "id:a", "77"), F("Docs/child.txt", "id:b", "77"));
        Require(store.BaselinesTouching("p1", Side.A, [], ["docs"]).Count == 1,
            "BaselinesTouching matches a baseline path ignoring case");
    }

    private static void BaselinesTouchingChecks(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("a.txt", "id:a", "77"), F("a.txt", "id:b", "77"));
        SeedBaseline(store, "p1", "k2", F("d/x.txt", "id:x", "77"), F("d/y.txt", "id:y", "77"));
        Require(store.BaselinesTouching("p1", Side.A, ["id:a"], []).Count == 1, "touched by an A object id");
        Require(store.BaselinesTouching("p1", Side.A, ["id:b"], []).Count == 0, "an object id must match its own side");
        var byPath = store.BaselinesTouching("p1", Side.B, [], ["d"]);
        Require(byPath.Count == 1 && byPath[0].ObjectKey == "k2", "touched by a B path under a prefix");
        var byItem = store.BaselinesTouching("p1", Side.A, [], ["a.txt"]);
        Require(byItem.Count == 1 && byItem[0].ObjectKey == "k1", "touched by the prefix item itself");
        Require(store.BaselinesTouching("p1", Side.A, ["id:zzz"], ["nope"]).Count == 0, "no match returns empty");
    }

    private static void CommitDoneBaselineAndIntent(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var source = F("a.txt", "id:a", "77");
        var action = new SyncAction("a-copy", 1, ActionKind.CopyNew, Side.B, "a.txt", null, source, null, false, "k1");
        store.RecordIntent("p1", action);
        var newB = F("a.txt", "id:b", "77");
        store.CommitResult("p1", 5, action, new ActionResult("a-copy", Outcome.Done, null, newB, null, null));
        Require(store.UnresolvedIntents("p1").Count == 0, "a done result resolves its intent");
        var baselines = store.Baselines("p1");
        Require(baselines.Count == 1 && SameItem(baselines[0].A, source) && SameItem(baselines[0].B, newB),
            "a done CopyNew forms the baseline A=Source, B=NewB");
    }

    private static void CommitDeleteDoneRemovesBaseline(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("a.txt", "id:a", "77"), F("a.txt", "id:b", "77"));
        Require(store.Baselines("p1").Count == 1, "the baseline exists before the delete");
        var delete = new SyncAction("a-del", 6, ActionKind.Delete, Side.B, "a.txt", null, null, F("a.txt", "id:b", "77"), true, "k1");
        store.CommitResult("p1", 6, delete, new ActionResult("a-del", Outcome.Done, null, null, null, null));
        Require(store.Baselines("p1").Count == 0, "a done Delete removes its baseline");
    }

    private static void CommitUncertainAndFailedUnresolved(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var a = F("a.txt", "id:a", "77");
        var b = F("a.txt", "id:b", "77");
        SeedBaseline(store, "p1", "k1", a, b);
        var update = new SyncAction("a-upd", 2, ActionKind.Update, Side.B, "a.txt", null, a, b, false, "k1");
        store.RecordIntent("p1", update);
        store.CommitResult("p1", 2, update, new ActionResult("a-upd", Outcome.Uncertain, null, null, null, "?"));
        Require(store.UnresolvedIntents("p1").Any(i => i.ActionId == "a-upd"), "an uncertain intent stays unresolved");
        store.CommitResult("p1", 2, update, new ActionResult("a-upd", Outcome.Failed, null, null, null, "boom"));
        Require(store.UnresolvedIntents("p1").Any(i => i.ActionId == "a-upd"), "a failed intent stays unresolved");
        store.CommitResult("p1", 2, update, new ActionResult("a-upd", Outcome.Done, a, b, null, null));
        Require(store.UnresolvedIntents("p1").Count == 0, "a later done result resolves the intent");
    }

    private static void CommitHoldThrows(string root)
    {
        var folder = Fresh(root, "hold");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var hold = new SyncAction("a-hold", 29, ActionKind.Hold, Side.B, "x", null, null, null, false, "k1");
        ExpectThrows<InvalidOperationException>(() =>
            store.CommitResult("p1", 1, hold, new ActionResult("a-hold", Outcome.Done, null, null, null, null)));
    }

    private static void CommitSizeMismatchThrows(string root)
    {
        var folder = Fresh(root, "size-mismatch");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var action = new SyncAction("a-copy", 1, ActionKind.CopyNew, Side.B, "a.txt", null, F("a.txt", "id:a", "77", 4), null, false, "k1");
        ExpectThrows<InvalidOperationException>(() => store.CommitResult("p1", 1, action,
            new ActionResult("a-copy", Outcome.Done, null, F("a.txt", "id:b", "77", 9), null, null)));
    }

    private static void CommitResultOtherPairThrows(string root)
    {
        var folder = Fresh(root, "other-pair");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock());
        store.SavePair(Pair("p1"));
        store.SavePair(Pair("p2"));
        var action = new SyncAction("a-shared", 2, ActionKind.Update, Side.B, "a.txt", null, null, null, false, "k1");
        store.RecordIntent("p1", action);
        ExpectThrows<InvalidOperationException>(() => store.CommitResult("p2", 1, action,
            new ActionResult("a-shared", Outcome.Failed, null, null, null, "boom")));
        Require(store.UnresolvedIntents("p1").Any(i => i.ActionId == "a-shared"),
            "the intent of the owning pair is untouched");
    }

    private static void CommitRecoveryRecorded(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var victim = F("x.txt", "id:a", "77");
        var action = new SyncAction("a1", 6, ActionKind.Delete, Side.B, "x.txt", null, null, victim, true, "k1");
        store.RecordIntent("p1", action);
        var recovery = new RecoveryRef(Side.B, "F:\\recovery\\op1", victim.VersionId, victim.Hash!, victim.Size);
        var result = new ActionResult("a1", Outcome.Failed, null, null, recovery, "boom");
        store.CommitResult("p1", 1, action, result);
        var records = store.Recoveries("p1");
        Require(records.Count == 1 && records[0].PairId == "p1" && records[0].ActionId == "a1" &&
            records[0].Side == Side.B && records[0].Location == "F:\\recovery\\op1" &&
            records[0].VersionId == victim.VersionId && records[0].Hash == victim.Hash && records[0].Size == victim.Size,
            "the recorded recovery row carries the copy's identity");
        store.CommitResult("p1", 1, action, result);
        Require(store.Recoveries("p1").Count == 1, "a repeated commit keeps one recovery record");
        Require(store.Recoveries("other").Count == 0, "an unknown pair has no recoveries");
    }

    private static void IntentJsonRoundTrip(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var a = F("a.txt", "id:a", "77");
        var action = new SyncAction("a1", 2, ActionKind.Update, Side.B, "a.txt", "src", a, a, false, "k1");
        store.RecordIntent("p1", action);
        var intents = store.UnresolvedIntents("p1");
        Require(intents.Count == 1 && intents[0] == action, "an intent round-trips by value");
    }

    private static void NoticesIdempotent(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var notice = new Notice("p1", 4, NoticeKind.AutoConflict, "x.txt", "conflict", "auto-conflict");
        store.AddNotices([notice]);
        store.AddNotices([notice]);
        var open = store.OpenNotices("p1");
        Require(open.Count == 1 && open[0].NoticeId is not null, "re-raising an open notice is idempotent");
        Require(store.OpenNotices(null).Count == 1, "OpenNotices with no pair returns every open notice");
        store.CloseNotice(open[0].NoticeId!, "keep-both");
        Require(store.OpenNotices("p1").Count == 0, "a closed notice leaves the open list");
        store.AddNotices([notice]);
        var reopened = store.OpenNotices("p1");
        Require(reopened.Count == 1 && reopened[0].NoticeId == open[0].NoticeId,
            "re-raising a closed notice for the same problem reopens it");
    }

    // ---- prune ----

    private static void PruneRemovesHistory(string root)
    {
        var clock = new TestClock();
        var folder = Fresh(root, "prune");
        using var store = SqliteStateStore.Open(folder, Fast, clock);
        var gen1 = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen1, "c1",
            [F("keep.txt", "id:k", "77"), F("orphan/x.txt", "id:o", "77")], []));
        store.CompleteGeneration(gen1, "c1", true);
        var gen2 = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen2, "c2", [F("keep.txt", "id:k", "77")], []));
        store.CompleteGeneration(gen2, "c2", true);

        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("keep.txt", "id:a", "77"), F("keep.txt", "id:b", "77"), 100);
        var done = new SyncAction("a-done", 1, ActionKind.CopyNew, Side.B, "keep.txt", null, F("keep.txt", "id:a", "77"), null, false, "k2");
        store.RecordIntent("p1", done);
        store.CommitResult("p1", 100, done,
            new ActionResult("a-done", Outcome.Done, null, F("keep.txt", "id:b", "77"), null, null));
        var failed = new SyncAction("a-failed", 2, ActionKind.Update, Side.B, "keep.txt", null, null, null, false, "k3");
        store.RecordIntent("p1", failed);
        store.CommitResult("p1", 100, failed, new ActionResult("a-failed", Outcome.Failed, null, null, null, "boom"));
        store.AddNotices([new Notice("p1", 4, NoticeKind.AutoConflict, "keep.txt", "conflict", "auto-conflict")]);
        store.CloseNotice(store.OpenNotices("p1")[0].NoticeId!, null);

        clock.UtcNow = clock.UtcNow.AddDays(31);
        store.Prune();

        RequireLive(store, "root", F("keep.txt", "id:k", "77"));
        var unresolved = store.UnresolvedIntents("p1");
        Require(unresolved.Any(i => i.ActionId == "a-failed"), "prune keeps a failed intent until a later attempt");
        Require(!unresolved.Any(i => i.ActionId == "a-done"), "a resolved intent is not unresolved");
        Require(store.OpenNotices("p1").Count == 0, "a closed notice stays closed after prune");
    }

    private static void ShortIdDeterministic()
    {
        Require(ShortId.Hex16("a", "b") == ShortId.Hex16("a", "b"), "Hex16 is deterministic");
        Require(ShortId.Make("n", "p1").StartsWith("n", StringComparison.Ordinal) && ShortId.Make("n", "p1").Length == 17,
            "Make prefixes a 16-hex id");
        ExpectThrows<ArgumentException>(() => ShortId.Make("BAD", "x"));
    }

    // ---- helpers ----

    private static void Both(string root, string name, Action<IStateStore> check)
    {
        using (var sqlite = OpenSqlite(root, name, new TestClock()))
            check(sqlite);
        using IStateStore memory = new InMemoryStateStore();
        check(memory);
    }

    // tombstones exist only for baselined objects, so tombstone checks first give the object a
    // baseline on that root (side A of a pair of its own).
    private static void Track(IStateStore store, string rootId, params string[] objectIds)
    {
        var pairId = "track-" + rootId;
        store.SavePair(new PairDef(pairId, pairId, new RootDef(rootId, "Local", null, "C:\\a", "id:local:1"),
            new RootDef(rootId + "-peer", "Dropbox", "acc", "/b", "id:dbx:1"), [], [], true));
        foreach (var id in objectIds)
            SeedBaseline(store, pairId, "k-" + id, F("t/" + id, id, "77"), F("t/" + id, "peer-" + id, "77"));
    }

    private static void Remove(IStateStore store, string rootId, string path, string objectId)
    {
        var gen = store.BeginGeneration(rootId, "snapshot");
        store.WriteSnapshot(new Snapshot(rootId, Side.A, gen, "c", [F(path, objectId, "77")], []));
        store.WriteSnapshot(new Snapshot(rootId, Side.A, gen, "c", [], []));
    }

    private static SqliteStateStore OpenSqlite(string root, string name, TimeProvider clock)
    {
        var folder = Fresh(root, name);
        return SqliteStateStore.Open(folder, Fast, clock);
    }

    private static string Fresh(string root, string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string DbPath(string dataRoot) => Path.Combine(dataRoot, "v2", "state.db");

    private static void SeedBaseline(IStateStore store, string pairId, string key, ItemState a, ItemState b, long gen = 1)
    {
        var action = new SyncAction("seed-" + key, 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, key);
        store.RecordIntent(pairId, action);
        store.CommitResult(pairId, gen, action, new ActionResult(action.ActionId, Outcome.Done, a, b, null, null));
    }

    private static ItemState F(string path, string objectId, string hash, long size = 1, DateTimeOffset? mtime = null,
        ItemFlags flags = ItemFlags.None) =>
        new(path, ItemKind.File, objectId, "v1", hash, size, mtime ?? T0, flags);

    private static ItemState NoHash(string path, string objectId, long size) =>
        new(path, ItemKind.File, objectId, "v1", null, size, T0, ItemFlags.None);

    private static ItemState D(string path, string objectId) =>
        new(path, ItemKind.Folder, objectId, null, null, 0, T0, ItemFlags.None);

    private static PairDef Pair(string id) => new(id, id,
        new RootDef("ra", "Local", null, "C:\\a", "id:local:1"),
        new RootDef("rb", "Dropbox", "acc", "/b", "id:dbx:1"),
        Array.Empty<string>(), Array.Empty<string>(), true);

    private static void RequireLive(IStateStore store, string rootId, params ItemState[] expected)
    {
        var live = store.Live(rootId);
        Require(live.Count == expected.Length && expected.All(item => live.Any(candidate => SameItem(candidate, item))),
            $"live rows equal {expected.Length} expected item(s)");
    }

    private static bool SameItem(ItemState x, ItemState y) =>
        string.Equals(x.Path, y.Path, StringComparison.Ordinal) && x.Kind == y.Kind &&
        string.Equals(x.ObjectId, y.ObjectId, StringComparison.Ordinal) &&
        string.Equals(x.VersionId, y.VersionId, StringComparison.Ordinal) &&
        string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase) && x.Size == y.Size &&
        x.Mtime.UtcTicks == y.Mtime.UtcTicks && x.Flags == y.Flags &&
        string.Equals(x.IdentityJson, y.IdentityJson, StringComparison.Ordinal);

    private static bool SamePair(PairDef x, PairDef y) =>
        string.Equals(x.PairId, y.PairId, StringComparison.Ordinal) && x.Name == y.Name && x.Enabled == y.Enabled &&
        SameRoot(x.A, y.A) && SameRoot(x.B, y.B) && x.Excluded.SequenceEqual(y.Excluded) &&
        x.IgnoreRules.SequenceEqual(y.IgnoreRules);

    private static bool SameRoot(RootDef x, RootDef y) =>
        string.Equals(x.RootId, y.RootId, StringComparison.Ordinal) && x.Provider == y.Provider &&
        x.AccountId == y.AccountId && x.RootPath == y.RootPath && x.RootObjectId == y.RootObjectId;

    private static void ExpectThrows<TException>(Action operation) where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
