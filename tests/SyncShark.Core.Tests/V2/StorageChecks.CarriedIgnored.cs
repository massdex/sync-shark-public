namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void CommitFolderDeleteRetiresCarriedRows(string root)
    {
        using var store = SqliteStateStore.Open(Fresh(root, "commit-carried-rows"), Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var folder = D("docs", "id:folder");
        var child = F("docs/desktop.ini", "id:ignored", "11");
        var nested = D("docs/cache", "id:cache");
        var nestedChild = F("docs/cache/item", "id:nested", "22");
        var sibling = F("docs2/desktop.ini", "id:sibling", "33");
        var clash = F("DOCS/desktop.ini", "id:clash", "44");
        var gen = store.BeginGeneration("rb", "full");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "c", [folder, child, nested, nestedChild, sibling, clash], []));
        // Keep a baseline for the ignored child: another pair's future observation still needs real absence evidence.
        SeedBaseline(store, "p1", "ignored", child with { ObjectId = "id:a-child" }, child);
        var action = new SyncAction("delete-folder", 6, ActionKind.Delete, Side.B, folder.Path,
            null, null, folder, false, null);
        foreach (var outcome in new[] { Outcome.Failed, Outcome.Skipped, Outcome.Uncertain })
        {
            store.CommitResult("p1", gen, action, new ActionResult(action.ActionId, outcome, null, null, null, null));
            Require(store.Live("rb").Count == 6, "non-Done folder results retain all live children");
        }
        store.CommitResult("p1", gen, action, new ActionResult(action.ActionId, Outcome.Done, null, null, null, null));
        RequireLive(store, "rb", sibling, clash);
        Require(store.Tombstones("rb").Any(row => row.ObjectId == child.ObjectId),
            "carried baselined child retains real deletion evidence");
        store.CommitResult("p1", gen, action, new ActionResult(action.ActionId, Outcome.Done, null, null, null, null));
        RequireLive(store, "rb", sibling, clash);

        var replacement = folder with { ObjectId = "id:replacement" };
        // Use a distinct child name: upserting desktop.ini would intentionally displace the sole
        // case-variant DOCS/desktop.ini row under the store's case-insensitive replacement rules.
        var replacementChild = F("docs/new.ini", "id:replacement-child", "55");
        store.WriteDelta(new Delta("rb", Side.B, gen, "c", "c", [
            new Change(ChangeKind.Upsert, replacement.ObjectId, replacement.Path, replacement, null),
            new Change(ChangeKind.Upsert, replacementChild.ObjectId, replacementChild.Path, replacementChild, null)], false, []));
        RequireLive(store, "rb", sibling, clash, replacement, replacementChild);
        store.CommitResult("p1", gen, action, new ActionResult(action.ActionId, Outcome.Done, null, null, null, null));
        RequireLive(store, "rb", sibling, clash, replacement, replacementChild);
    }
}
