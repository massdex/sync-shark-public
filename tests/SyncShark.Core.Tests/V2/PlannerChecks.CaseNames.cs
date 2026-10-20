namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void CaseEquivalentMoves_AdoptOrUpdate()
    {
        foreach (var changed in new[] { Side.A, Side.B })
        foreach (var exact in new[] { false, true })
        foreach (var folder in new[] { false, true })
        {
            var a = folder ? D("Child/Folders", "a") : F("Child/Folders", "a", "h1");
            var b = folder ? D("Child/folders", "b") : F("Child/folders", "b", "h1");
            if (exact) b = b with { Path = a.Path };
            var baseline = Base("case", changed == Side.A ? a with { Path = "Child/FOLDERS" } : a,
                changed == Side.B ? b with { Path = "Child/FOLDERS" } : b);
            var plan = Decide(Row(baseline, a, b));
            var adopt = plan.Actions.Single();
            Require(adopt.Kind == ActionKind.Adopt && adopt.Target == Side.B &&
                adopt.Source == a && adopt.ExpectedTarget == b, "case-only adopt preserves A/B identities and spellings");
            Require(plan.Notices.Count == (exact ? 0 : 1), "only a remaining case difference needs a notice");
            if (!exact) Require(plan.Notices.Single().Detail == "names differ only in letter case", "case notice wording");
            AssertEmpty(Decide(Row(Base("case", a, b), a, b)));
            if (folder) continue;
            var editedA = changed == Side.A ? a with { Hash = "h2" } : a;
            var editedB = changed == Side.B ? b with { Hash = "h2" } : b;
            var edit = Decide(Row(baseline, editedA, editedB)).Actions.Single();
            Require(edit.Kind == ActionKind.Update && edit.Target == Opposite(changed) &&
                edit.Path == (changed == Side.A ? b.Path : a.Path), "case-only move and edit updates actual target without moving");
        }
    }

    /// <summary>
    /// The Child/Folders shape: the folder was renamed by case only and every child baseline still carries
    /// the old spelling. Only the topmost differing folder is reported; a child whose own leaf is unchanged and a
    /// nested child that also changed case both stay silent.
    /// </summary>
    private static void CaseEquivalentChildren_OnlyTopmostFolderNotices()
    {
        var folderA = D("Child/Folders", "fa");
        var folderB = D("Child/folders", "fb");
        var folderBase = Base("folder", folderA with { Path = "Child/folders" }, folderB);
        var childA = F("Child/Folders/child.txt", "ca", "h1");
        var childB = F("Child/folders/child.txt", "cb", "h1");
        var childBase = Base("child", childA with { Path = "Child/folders/child.txt" }, childB);
        var nestedA = F("Child/Folders/Notes.txt", "na", "h1");
        var nestedB = F("Child/folders/notes.txt", "nb", "h1");
        var nestedBase = Base("nested", nestedA with { Path = "Child/folders/notes.txt" }, nestedB);

        var plan = Decide(false, Row(folderBase, folderA, folderB), Row(childBase, childA, childB),
            Row(nestedBase, nestedA, nestedB));
        Require(plan.Actions.Count == 3 && plan.Actions.All(x => x.Kind == ActionKind.Adopt),
            "case-equivalent folder and children adopt without a move");
        Require(plan.Notices.Count == 1 && plan.Notices.Single().Path == folderB.Path &&
            plan.Notices.Single().Detail == "names differ only in letter case",
            "only the topmost differing folder raises the case notice");
    }
}
