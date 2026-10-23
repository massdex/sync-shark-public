namespace Massdex.SyncShark.V2;

/// <summary>Replay-safety checks: a delta written again after a crash must leave the same live set (B-126..B-128).</summary>
internal static partial class StorageChecks
{
    // B-126: after Delete(X, Docs/a.txt) committed but the cursor did not move, the inventory replays the same
    // deleted spelling without an object id (the exact row is gone); it must never reach Docs/A.txt (Y) or sub (g).
    private static void CaseClash_ReplayWithoutIdentityKeepsSurvivors(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", ClashSet, []));
        var first = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, first, "c", "d", [
            new Change(ChangeKind.Delete, "id:x", "Docs/a.txt", null, T0),
            new Change(ChangeKind.Delete, "id:f", "Sub", null, T0),
        ], false, []));
        var replay = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, replay, "c", "d", [
            new Change(ChangeKind.Delete, "", "Docs/a.txt", null, T0),
            new Change(ChangeKind.Delete, "", "Sub", null, T0),
        ], false, []));
        RequireLive(store, "ra", ClashSet[1], ClashSet[4], ClashSet[5]);
    }

    // B-126: a Delete naming an object acts only where that object still is at the exact path; the same id at
    // another path, or another object at the path, is a different incarnation and stays.
    private static void Delete_IdentityBoundToExactPath(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c",
            [F("a.txt", "id:1", "11"), F("b.txt", "id:2", "22"), D("Dir", "id:d"), F("Dir/c.txt", "id:3", "33")], []));
        var stale = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, stale, "c", "d", [
            new Change(ChangeKind.Delete, "id:1", "b.txt", null, T0),
            new Change(ChangeKind.Delete, "id:2", "old/b.txt", null, T0),
            new Change(ChangeKind.Delete, "id:1", "Dir", null, T0),
        ], false, []));
        RequireLive(store, "ra", F("a.txt", "id:1", "11"), F("b.txt", "id:2", "22"), D("Dir", "id:d"),
            F("Dir/c.txt", "id:3", "33"));
        var real = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, real, "d", "e", [
            new Change(ChangeKind.Delete, "id:1", "a.txt", null, T0),
            new Change(ChangeKind.Delete, "id:d", "Dir", null, T0),
        ], false, []));
        RequireLive(store, "ra", F("b.txt", "id:2", "22"));
    }

    // B-128: Delete(X, Docs/a.txt) + Upsert(X, Docs/a.txt) is X staying put; Y at Docs/A.txt must survive.
    private static void CaseClash_DeleteUpsertSameSpellingKeepsSibling(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [ClashSet[0], ClashSet[1]], []));
        var delta = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delta, "c", "d", [
            new Change(ChangeKind.Delete, "id:x", "Docs/a.txt", null, T0),
            new Change(ChangeKind.Upsert, "id:x", "Docs/a.txt", ClashSet[0], null),
        ], false, []));
        RequireLive(store, "ra", ClashSet[0], ClashSet[1]);
    }

    // The keyed candidate lookup behind a deleted path's other spellings: every spelling of the path, no subtree.
    private static void LiveSpellings_KeyOnly(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", ClashSet, []));
        Require(store.LiveSpellings("ra", "docs/A.TXT").Select(i => i.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(["Docs/A.txt", "Docs/a.txt"]), "a file key returns both stored spellings");
        Require(store.LiveSpellings("ra", "SUB").Select(i => i.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(["Sub", "sub"]), "a folder key returns its spellings without their children");
        Require(store.LiveSpellings("ra", "none").Count == 0 && store.LiveSpellings("zz", "Sub").Count == 0,
            "an unknown path or root returns nothing");
    }

    // Standards audit B2: an incomplete subtree keeps rows self-or-under it ignoring case (PairPath), never a
    // sibling that merely shares the prefix; "" keeps the whole root.
    private static void Snapshot_IncompleteSubtreeIsSelfOrUnder(IStateStore store)
    {
        ItemState[] live = [D("Docs", "id:d"), F("docs/x.txt", "id:x", "11"), F("Docsier/z.txt", "id:z", "22")];
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", live, []));
        var partial = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, partial, "c", [], ["DOCS"]));
        RequireLive(store, "ra", live);
        store.WriteSnapshot(new Snapshot("ra", Side.A, partial, "c", [], ["DOCS"])
            { VerifyAbsence = _ => FileAbsence.Gone });
        RequireLive(store, "ra", live[0], live[1]);
        var whole = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, whole, "c", [], [""]));
        RequireLive(store, "ra", live[0], live[1]);
    }

    private static ItemState[] SubtreeSet =>
        [D("Dir", "id:d"), F("Dir/n1", "id:1", "11"), F("Dir/n2", "id:2", "22"), D("Dir/Sub", "id:s"),
            F("Dir/Sub/n4", "id:4", "44"), F("Dir/n5", "id:5", "55"), F("keep.txt", "id:k", "66")];

    // B-127: a folder Delete replayed after some of its rows went (a crash between batches) removes the rest; the
    // folder row is the last to go, so the replay still finds it.
    private static void FolderDelete_ReplayAfterPartial(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", SubtreeSet, []));
        var partial = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, partial, "c", "d", [
            new Change(ChangeKind.Delete, "id:1", "Dir/n1", null, T0),
            new Change(ChangeKind.Delete, "id:4", "Dir/Sub/n4", null, T0),
        ], false, []));
        var replay = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, replay, "c", "d",
            [new Change(ChangeKind.Delete, "id:d", "Dir", null, T0)], false, []));
        RequireLive(store, "ra", F("keep.txt", "id:k", "66"));
    }

    // B-127 on the real store: two rows per transaction and a crash at the last descendant. The committed batches
    // must leave the folder live, so the replayed Delete strands nothing.
    private static void FolderDelete_CrashBetweenBatches(string root)
    {
        var folder = Fresh(root, "subtree-crash");
        using var store = SqliteStateStore.Open(folder, Fast with { StoreBatchRows = 2 }, new TestClock());
        // One folder, so its rows go in key order: n1..n4 commit in two batches before n5 fails.
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c",
            [D("Dir", "id:d"), .. Enumerable.Range(1, 5).Select(i => F($"Dir/n{i}", $"id:{i}", "11")),
                F("keep.txt", "id:k", "66")], []));
        StorageDamage.FailDeleteOf(DbPath(folder), "n5");
        Change[] delete = [new Change(ChangeKind.Delete, "id:d", "Dir", null, T0)];
        var crashed = store.BeginGeneration("ra", "delta");
        ExpectThrows<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.WriteDelta(new Delta("ra", Side.A, crashed, "c", "d", delete, false, [])));
        Require(store.Live("ra", ["Dir"]).Count == 1, "the folder row outlives a crash inside its subtree delete");
        StorageDamage.ClearDeleteFault(DbPath(folder));
        var replay = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, replay, "c", "d", delete, false, []));
        RequireLive(store, "ra", F("keep.txt", "id:k", "66"));
    }
}
