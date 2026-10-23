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
            var action = plan.Actions.Single();
            Require(exact
                ? action.Kind == ActionKind.Adopt && action.Source == a && action.ExpectedTarget == b
                : action.Kind == ActionKind.Move && action.Target == Opposite(changed) &&
                  action.Source == (changed == Side.A ? a : b) &&
                  action.ExpectedTarget == (changed == Side.A ? b : a) &&
                  action.Path == (changed == Side.A ? a.Path : b.Path),
                "case rename preserves identity and propagates exact spelling; equal spelling only adopts");
            Require(plan.Notices.Count == 0, "successful case propagation needs no difference notice");
            AssertEmpty(Decide(Row(Base("case", a, b), a, b)));
            if (folder) continue;
            var editedA = changed == Side.A ? a with { Hash = "h2" } : a;
            var editedB = changed == Side.B ? b with { Hash = "h2" } : b;
            var edits = Decide(Row(baseline, editedA, editedB)).Actions;
            var edit = edits.Single(x => x.Kind == ActionKind.Update);
            Require(edits.Count(x => x.Kind == ActionKind.Move) == (exact ? 0 : 1) &&
                edit.Target == Opposite(changed) &&
                edit.Path == (changed == Side.A ? a.Path : b.Path),
                "case-only move and edit renames then updates at the new spelling");
        }
    }

    /// <summary>
    /// The folder rename carries an unchanged child, while an independent child leaf-case
    /// rename remains a Move after its ancestor's spelling is rebased.
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
        Require(plan.Actions.Count == 2 && plan.Actions.All(x => x.Kind == ActionKind.Move),
            "folder move carries unchanged child; independent leaf rename remains");
        Require(plan.Actions.Any(x => x.Path == folderA.Path && x.FromPath == folderB.Path) &&
            plan.Actions.Any(x => x.Path == nestedA.Path && x.FromPath == "Child/Folders/notes.txt"),
            "child rename retains its new leaf spelling after the ancestor move");
        Require(plan.Notices.Count == 0, "case propagation raises no difference notice");
    }
}
