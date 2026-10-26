namespace Massdex.SyncShark.V2;

/// <summary>Round-3 checks: a move is one atomic write, and deletion evidence survives replacement and path reuse.</summary>
internal static partial class StorageChecks
{
    // With one row per transaction, a failure between a move's removal and its insertion must lose nothing.
    private static void Move_RemovalAndInsertionAtomic(string root)
    {
        var folder = Fresh(root, "move-atomic");
        using var store = SqliteStateStore.Open(folder, Fast with { StoreBatchRows = 1 }, new TestClock(), LocalItems.CaptureOptionalHash);
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("a", "id1", "11")], []));
        var delta = store.BeginGeneration("root", "delta");
        // The bad hash makes the insertion at the new path fail after the old path's removal was written.
        ExpectThrows<ArgumentException>(() => store.WriteDelta(new Delta("root", Side.A, delta, "c", "d",
            [new Change(ChangeKind.Upsert, "id1", "b", F("b", "id1", "zz"), null)], false, [])));
        RequireLive(store, "root", F("a", "id1", "11"));
        Require(store.Tombstones("root").Count == 0, "a failed move leaves the old row live and records no deletion");

        // The same move written as an explicit Delete(old) + Upsert(new) of the same object.
        var explicitMove = store.BeginGeneration("root", "delta");
        ExpectThrows<ArgumentException>(() => store.WriteDelta(new Delta("root", Side.A, explicitMove, "c", "d",
        [
            new Change(ChangeKind.Delete, "id1", "a", null, T0),
            new Change(ChangeKind.Upsert, "id1", "b", F("b", "id1", "zz"), null),
        ], false, [])));
        RequireLive(store, "root", F("a", "id1", "11"));
        Require(store.Tombstones("root").Count == 0,
            "a failed Delete+Upsert move leaves the old row live and records no deletion");
        var good = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, good, "c", "d",
        [
            new Change(ChangeKind.Delete, "id1", "a", null, T0),
            new Change(ChangeKind.Upsert, "id1", "b", F("b", "id1", "11"), null),
        ], false, []));
        RequireLive(store, "root", F("b", "id1", "11"));
    }

    private static void Tombstone_ReplacementKeepsDisplacedEvidence(string root)
    {
        var folder = Fresh(root, "tombstone-replace");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        Track(store, "root", "id1", "id2", "id3", "id4");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("p.txt", "id1", "11")], []));
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("p.txt", "id2", "22")], []));
        RequireTombstones(store, "root", "a snapshot replacing the object at a path tombstones the displaced one",
            ("p.txt", "id1"));

        var delta = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, delta, "c", "d",
            [new Change(ChangeKind.Upsert, "id3", "p.txt", F("p.txt", "id3", "33"), null)], false, []));
        RequireTombstones(store, "root", "a delta upsert replacing the object at a path tombstones the displaced one",
            ("p.txt", "id1"), ("p.txt", "id2"));

        var again = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, again, "c",
            [F("p.txt", "id4", "44"), F("q.txt", "id3", "33")], []));
        RequireTombstones(store, "root", "a displaced object that moved in the same snapshot is not a deletion",
            ("p.txt", "id1"), ("p.txt", "id2"));
    }

    // a tombstone stays until its baseline resolves, even when its path is reused.
    private static void Tombstone_PathReuseKeepsUnresolved(string root)
    {
        var folder = Fresh(root, "tombstone-reuse");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        Track(store, "root", "id1", "id2");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("p.txt", "id1", "11")], []));
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [], []));
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("p.txt", "id2", "22")], []));
        RequireTombstones(store, "root", "a new object at the path keeps the earlier tombstone", ("p.txt", "id1"));
        var delta = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, delta, "c", "d",
            [new Change(ChangeKind.Delete, "id2", "p.txt", null, T0)], false, []));
        RequireTombstones(store, "root", "deleting the reused path adds a second tombstone instead of overwriting",
            ("p.txt", "id1"), ("p.txt", "id2"));
    }

    private static void RequireTombstones(SqliteStateStore store, string rootId, string label,
        params (string Path, string ObjectId)[] expected)
    {
        var actual = store.Tombstones(rootId).Select(t => (t.Path, t.ObjectId))
            .OrderBy(t => t.ObjectId, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(expected.OrderBy(t => t.ObjectId, StringComparer.Ordinal)),
            $"{label} [{string.Join(", ", actual.Select(t => t.Path + ":" + t.ObjectId))}]");
    }

    // ---- tombstone lifecycle ----

    private static void Tombstone_OnlyForBaselined(string root)
    {
        using var store = OpenSqlite(root, "tombstone-baselined", new TestClock());
        Track(store, "root", "id2");
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [F("a", "id1", "11"), F("b", "id2", "22")], []));
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "c", [], []));
        RequireTombstones(store, "root", "only the baselined object leaves deletion evidence", ("b", "id2"));
    }

    // The executor deletes and commits first; the inventory reports the deletion afterwards. And the other order:
    // the inventory's evidence comes first and the Forget that resolves the baseline removes it.
    private static void Tombstone_ExecutorDeletionObserved(string root)
    {
        using var store = OpenSqlite(root, "tombstone-executor", new TestClock());
        store.SavePair(Pair("p1"));
        var genB = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, genB, "c", [F("x.txt", "id:b", "11")], []));
        var genA = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, genA, "c", [F("y.txt", "id:c", "22")], []));
        SeedBaseline(store, "p1", "k1", F("x.txt", "id:a", "11"), F("x.txt", "id:b", "11"));
        SeedBaseline(store, "p1", "k2", F("y.txt", "id:c", "22"), F("y.txt", "id:d", "22"));

        var delete = new SyncAction("del", 6, ActionKind.Delete, Side.B, "x.txt", null, null, F("x.txt", "id:b", "11"),
            true, "k1");
        store.CommitResult("p1", 1, delete, new ActionResult("del", Outcome.Done, null, null, null, null));
        var seen = store.BeginGeneration("rb", "delta");
        store.WriteDelta(new Delta("rb", Side.B, seen, "c", "d",
            [new Change(ChangeKind.Delete, "id:b", "x.txt", null, T0)], false, []));
        Require(store.Tombstones("rb").Count == 0,
            "an inventory observing an executor-completed deletion records no tombstone (the baseline is gone)");

        var gone = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, gone, "c", "d",
            [new Change(ChangeKind.Delete, "id:c", "y.txt", null, T0)], false, []));
        RequireTombstones(store, "ra", "a deletion of a baselined object records evidence", ("y.txt", "id:c"));
        var forget = new SyncAction("fg", 15, ActionKind.Forget, Side.B, "y.txt", null, null, null, false, "k2");
        store.CommitResult("p1", 1, forget, new ActionResult("fg", Outcome.Done, null, null, null, null));
        Require(store.Tombstones("ra").Count == 0, "forgetting the baseline removes its tombstone");
    }

    // A replacement leaves the displaced identity's evidence; a baseline that moves on to the new identity resolves it.
    private static void Tombstone_ReplacementResolved(string root)
    {
        using var store = OpenSqlite(root, "tombstone-replacement", new TestClock());
        store.SavePair(Pair("p1"));
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [F("p.txt", "id:x", "11")], []));
        SeedBaseline(store, "p1", "k1", F("p.txt", "id:x", "11"), F("p.txt", "id:b", "11"));
        var delta = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delta, "c", "d",
            [new Change(ChangeKind.Upsert, "id:y", "p.txt", F("p.txt", "id:y", "22"), null)], false, []));
        RequireTombstones(store, "ra", "the displaced baselined identity leaves evidence", ("p.txt", "id:x"));
        var adopt = new SyncAction("ad", 3, ActionKind.Adopt, Side.B, "p.txt", null, F("p.txt", "id:y", "22"),
            F("p.txt", "id:b2", "22"), false, "k1");
        store.CommitResult("p1", 1, adopt, new ActionResult("ad", Outcome.Done, F("p.txt", "id:y", "22"),
            F("p.txt", "id:b2", "22"), null, null));
        Require(store.Tombstones("ra").Count == 0,
            "a baseline that now names the new identity resolves the displaced identity's evidence");
    }

    // Two baselines name the same (root, object): resolving one keeps the evidence the other still relies on.
    private static void Tombstone_SharedByTwoBaselines(string root)
    {
        using var store = OpenSqlite(root, "tombstone-shared", new TestClock());
        store.SavePair(Pair("p1"));
        store.SavePair(Pair("p2") with { B = new RootDef("rc", "Dropbox", "acc", "/c", "id:dbx:2") });
        SeedBaseline(store, "p1", "k1", F("a.txt", "id:a", "11"), F("a.txt", "id:b", "11"));
        SeedBaseline(store, "p2", "k2", F("a.txt", "id:a", "11"), F("a.txt", "id:c", "11"));
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [F("a.txt", "id:a", "11")], []));
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [], []));
        RequireTombstones(store, "ra", "the shared object's deletion leaves one tombstone", ("a.txt", "id:a"));
        var forget = new SyncAction("fg1", 15, ActionKind.Forget, Side.B, "a.txt", null, null, null, false, "k1");
        store.CommitResult("p1", 1, forget, new ActionResult("fg1", Outcome.Done, null, null, null, null));
        RequireTombstones(store, "ra", "forgetting one baseline keeps the tombstone the other still names",
            ("a.txt", "id:a"));
        var adopt = new SyncAction("ad2", 3, ActionKind.Adopt, Side.B, "a.txt", null, F("a.txt", "id:z", "22"),
            F("a.txt", "id:c2", "22"), false, "k2");
        store.CommitResult("p2", 1, adopt, new ActionResult("ad2", Outcome.Done, F("a.txt", "id:z", "22"),
            F("a.txt", "id:c2", "22"), null, null));
        Require(store.Tombstones("ra").Count == 0, "once no baseline names the object, its tombstone goes");
    }}
