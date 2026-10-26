namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void CaseEquivalentMoves_AdoptOrUpdate()
    {
        AncestorDrift_OneSidedEdit();
        AncestorDrift_DeletionAndReplacement();
        foreach (var changed in new[] { Side.A, Side.B })
        foreach (var exact in new[] { false, true })
        foreach (var folder in new[] { false, true })
        {
            var a = folder ? D("Child/Folders", "a") : F("Child/Folders", "a", "h1");
            var b = folder ? D("child/Folders", "b") : F("child/Folders", "b", "h1");
            if (exact) b = b with { Path = a.Path };
            var baseline = Base("case", changed == Side.A ? a with { Path = "CHILD/Folders" } : a,
                changed == Side.B ? b with { Path = "CHILD/Folders" } : b);
            var plan = Decide(Row(baseline, a, b));
            var action = plan.Actions.Single();
            Require(action.Kind == ActionKind.Adopt && action.Source == a && action.ExpectedTarget == b,
                "case-equivalent locations adopt both actual spellings without a provider move");
            Require(plan.Notices.Count == 0, "successful case propagation needs no difference notice");
            AssertEmpty(Decide(Row(Base("case", a, b), a, b)));
            if (folder) continue;
            var unknown = Row(baseline, changed == Side.A ? a with { Hash = null } : a,
                changed == Side.B ? b with { Hash = null } : b);
            var pending = Decide(unknown);
            Require(pending.Actions.Count == 0 && pending.Notices.Any(n => n.ReasonKey == PlanNotices.ContentUnconfirmed.Key),
                "unknown case-only content cannot be adopted or moved from metadata alone");
            var hashes = Planner.HashRequests([unknown]);
            Require((changed == Side.A ? hashes.PathsA : hashes.PathsB).Count == 1,
                "case-only content verification is requested even when its baseline metadata is unchanged");
            var editedA = changed == Side.A ? a with { Hash = "h2" } : a;
            var editedB = changed == Side.B ? b with { Hash = "h2" } : b;
            var edits = Decide(Row(baseline, editedA, editedB)).Actions;
            var edit = edits.Single(x => x.Kind == ActionKind.Update);
            Require(edits.Count == 1 && edit.Target == Opposite(changed) &&
                edit.Path == (changed == Side.A ? b.Path : a.Path),
                "case-only move and edit updates at the existing target spelling without a move");
            var divergent = Decide(Row(baseline with { B = baseline.B with { Hash = "h2" } },
                a, b with { Hash = "h2" }));
            Require(divergent.Actions.Count == 1 && divergent.Actions.Single() is { Kind: ActionKind.Hold, Rule: 29 },
                "unchanged divergent baseline content requires a decision, never an arbitrary spelling winner");
        }
    }

    private static void AncestorDrift_DeletionAndReplacement()
    {
        foreach (var survivor in new[] { Side.A, Side.B })
        {
            var a = F("Parent/child/work/item.json", "a", "same");
            var b = F(a.Path, "b", "same");
            var baseline = Base("drift-delete", a, b);
            var current = (survivor == Side.A ? a : b) with { Path = "Parent/Child/work/item.json" };
            var row = Row(baseline, survivor == Side.A ? current : null, survivor == Side.B ? current : null,
                Ev(A: survivor == Side.B, B: survivor == Side.A));
            var plan = Planner.Decide(PairId, Gen, [row], Opt() with { ProviderA = SyncProviders.Local, ProviderB = SyncProviders.Local });
            Require(plan.Actions.Count == 1 && plan.Actions.Single() is { Kind: ActionKind.Delete, Rule: 6, RecoveryFirst: true } deletion &&
                deletion.Target == survivor && deletion.ExpectedTarget == current,
                "ancestor drift does not defeat evidenced deletion; both mirrors preserve the victim first");
            var unknown = Decide(row with { Ev = new Evidence(false, false, false, false) });
            Require(!unknown.Actions.Any(action => action.Kind == ActionKind.Delete), "unknown absence never authorizes a drifted survivor deletion");
            var replacement = D(a.Path, "replacement");
            var replaced = Decide(Row(baseline, survivor == Side.A ? current : replacement, survivor == Side.B ? current : replacement));
            Require(replaced.Actions.Count == 2 && replaced.Actions.All(action => action.Rule == 26 && action.Target == survivor) &&
                replaced.Actions.Any(action => action.Kind == ActionKind.Delete && action.RecoveryFirst && action.ExpectedTarget == current) &&
                replaced.Actions.Any(action => action.Kind == ActionKind.MkDir),
                "replacement against ancestor drift is rule 26 with recovery-first removal, in both directions");
        }
    }

    private static void AncestorDrift_OneSidedEdit()
    {
        foreach (var edited in new[] { Side.A, Side.B })
        foreach (var bothDrift in new[] { false, true })
        {
            var a = F("Parent/child/work/item.json", "a", "old");
            var b = F(a.Path, "b", "old");
            var baseline = Base("ancestor", a, b);
            var currentA = a with { Path = edited == Side.B || bothDrift ? "Parent/Child/work/item.json" : a.Path,
                Hash = edited == Side.A ? "new" : "old" };
            var currentB = b with { Path = edited == Side.A || bothDrift ? "PARENT/child/work/item.json" : b.Path,
                Hash = edited == Side.B ? "new" : "old" };
            var plan = Decide(Row(baseline, currentA, currentB));
            Require(plan.Actions.Count == 1 && plan.Actions.Single() is { Kind: ActionKind.Update } update &&
                update.Target == Opposite(edited) && update.Path == (edited == Side.A ? currentB.Path : currentA.Path) &&
                update.ObjectKey == baseline.ObjectKey && plan.Notices.Count == 0,
                "ancestor-only drift plus an opposite edit is one update to existing spelling, never a split");
            var twoEdits = Decide(Row(baseline, currentA with { Hash = "edit-a" }, currentB with { Hash = "edit-b" }));
            Require(twoEdits.Notices.Any(n => n.Rule == 27), "genuine two-sided edits still keep both under rule 27");
            var unknown = Decide(Row(baseline, currentA with { Hash = null, Mtime = a.Mtime.AddMinutes(1) },
                currentB with { Hash = null, Mtime = b.Mtime.AddMinutes(1) }));
            Require(!unknown.Actions.Any(x => x.Kind != ActionKind.Hold), "unknown content never authorizes an update or split");
            var leaf = Decide(Row(baseline, a with { Path = "Parent/child/work/Item.json" }, b with { Hash = "new" }));
            Require(leaf.Notices.Any(n => n.Rule == 27), "a real leaf rename plus an opposite edit remains a conflict");
        }
    }

    /// <summary>
    /// Ancestor-only changes do not create per-file moves; independent leaf renames still propagate.
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
        Require(plan.Actions.Count == 3 && plan.Actions.Count(x => x.Kind == ActionKind.Move) == 2 &&
            plan.Actions.Single(x => x.ObjectKey == "child").Kind == ActionKind.Adopt,
            "folder and independent leaf renames move; an ancestor-only child change adopts");
        Require(plan.Actions.Any(x => x.Source == folderA && x.ExpectedTarget == folderB) &&
            plan.Actions.Any(x => x.Kind == ActionKind.Move && x.Source == nestedA &&
                x.ExpectedTarget == nestedB with { Path = "Child/Folders/notes.txt" } &&
                x.FromPath == "Child/Folders/notes.txt" && x.Path == "Child/Folders/Notes.txt"),
            "parent move rebases the child origin without erasing its independently renamed leaf");
        Require(plan.Notices.Count == 0, "case propagation raises no difference notice");
    }
}
