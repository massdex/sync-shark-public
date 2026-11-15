namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void FolderMoveBaselineChecks(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var a = new ItemState("Parent/old", ItemKind.Folder, "folder-a", null, null, 0, T0, ItemFlags.None);
        var b = a with { ObjectId = "folder-b" };
        var ca = new ItemState("Parent/old/child", ItemKind.File, "child-a", "version-a", new string('a', 64), 7, T0, ItemFlags.None);
        var cb = ca with { ObjectId = "child-b", VersionId = "version-b" };
        var independentA = ca with { Path = "Elsewhere/child", ObjectId = "independent-a" };
        var independentB = cb with { Path = "Parent/old/independent", ObjectId = "independent-b" };
        Seed("folder", a, b);
        Seed("child", ca, cb);
        Seed("independent", independentA, independentB);
        var movedA = a with { Path = "Parent/OLD" };
        var movedB = b with { Path = "Parent/OLD" };
        var move = new SyncAction("move", 25, ActionKind.Move, Side.B, movedB.Path, b.Path, movedA, b, false, "folder");
        foreach (var outcome in new[] { Outcome.Failed, Outcome.Uncertain })
        {
            store.RecordIntent("p1", move);
            store.CommitResult("p1", 2, move, new(move.ActionId, outcome, null, null, null, "not confirmed"));
            Require(store.Baselines("p1").Single(x => x.ObjectKey == "child").B.Path == cb.Path,
                "unconfirmed parent move never advances descendant baseline paths");
        }
        store.RecordIntent("p1", move);
        store.CommitResult("p1", 3, move, new(move.ActionId, Outcome.Done, movedA, movedB, null, null));
        var child = store.Baselines("p1").Single(x => x.ObjectKey == "child");
        Require(child.A.Path == "Parent/OLD/child" && child.B.Path == "Parent/OLD/child",
            "successful parent move rebases both linked descendant spellings");
        Require(child.A.ObjectId == ca.ObjectId && child.B.ObjectId == cb.ObjectId &&
            child.A.Hash == ca.Hash && child.B.Hash == cb.Hash && child.A.VersionId == ca.VersionId &&
            child.B.VersionId == cb.VersionId && child.A.Mtime == ca.Mtime && child.B.Size == cb.Size,
            "folder move changes paths only, never adopts unobserved descendant content or versions");
        var independent = store.Baselines("p1").Single(x => x.ObjectKey == "independent");
        Require(independent.A.Path == independentA.Path && independent.B.Path == "Parent/OLD/independent",
            "independently located opposite child is not rebased by unrelated parent path");

        void Seed(string key, ItemState left, ItemState right)
        {
            var adopt = new SyncAction("seed-" + key, 16, ActionKind.Adopt, Side.B, right.Path, null, left, right, false, key);
            store.RecordIntent("p1", adopt);
            store.CommitResult("p1", 1, adopt, new(adopt.ActionId, Outcome.Done, left, right, null, null));
        }
    }
}
