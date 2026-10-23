namespace Massdex.SyncShark.V2;

/// <summary>
/// prepare-matched planning plus Astra-review fixes of 96aa8b2: collapse provenance includes
/// NeedsReview, and the prepare-matched comparison runs before first-sync winners/choices.
/// </summary>
internal static partial class PlannerChecks
{
    private static void PostPrepare_Checks()
    {
        PostPrepare_MixedNeedsReviewChildKeepsFolderDeleteSeparate();
        PostPrepare_UnchangedSurvivorBeatsChoice();
        PostPrepare_HeldChildNeverCollapsedOver();
        PreparedLocalDelete_Checks();
    }

    // Fourth review finding 2: Dropbox deleted matched p/d/old.txt, p/d and p; local p/d/new.txt is held (hidden from
    // the rows). The rule-23 collapse must not run over a held descendant: old.txt's ordinary delete still runs, and
    // only the folders above the held path become Holds with a pending-evidence notice.
    private static void PostPrepare_HeldChildNeverCollapsedOver()
    {
        PlanRow Gone(ItemState local, ItemState remote) =>
            Row(Base("k:" + local.Path, local, remote), local, null, Ev(B: true));
        var rows = new[]
        {
            Gone(D("p", "loc:p"), D("p", "dbx:p")),
            Gone(D("p/d", "loc:d"), D("p/d", "dbx:d")),
            Gone(F("p/d/old.txt", "loc:old", "ho"), F("p/d/old.txt", "dbx:old", "ho")),
        };
        var options = Opt(true) with
        {
            HeldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p/d/new.txt" }
        };
        var plan = Planner.Decide(PairId, Gen, rows, options);
        Require(plan.Actions.Any(action => action is
            { Kind: ActionKind.Delete, Target: Side.A, Path: "p/d/old.txt", RecoveryFirst: false }),
            "the independently authorized child delete is kept: " +
            string.Join(", ", plan.Actions.Select(a => $"{a.Kind} {a.Target} '{a.Path}' rule={a.Rule}")));
        Require(plan.Actions.Count(action => action.Kind == ActionKind.Hold) == 2 &&
            plan.Actions.Any(action => action is { Kind: ActionKind.Hold, Path: "p/d" }) &&
            plan.Actions.Any(action => action is { Kind: ActionKind.Hold, Path: "p" }),
            "only the folders above the held path are held");
        Require(plan.Actions.All(action => action.Kind != ActionKind.Delete || action.Path == "p/d/old.txt") &&
            plan.Actions.All(action => action.Rule != 23), "no folder delete or collapse over the held path");
        Require(plan.Notices.Count(notice => notice.ReasonKey == PlanNotices.ProtectedChildPending.Key) == 2,
            "each held folder carries a neutral protected-child notice");
        var protectedIndex = PathProtectionIndex.TryCreate(["p/d/conflict.txt"])!;
        var callbackPlan = Planner.Decide(PairId, Gen, rows, Opt(false) with
        { ProtectedDescendants = path => protectedIndex.Touches(path, descendants: true) });
        Require(callbackPlan.Actions.Any(a => a.Kind == ActionKind.Delete && a.Path == "p/d/old.txt") &&
            callbackPlan.Actions.Count(a => a.Kind == ActionKind.Hold) == 2 &&
            callbackPlan.Notices.Where(n => n.ReasonKey == PlanNotices.ProtectedChildPending.Key)
                .All(n => !n.Detail.Contains("evidence", StringComparison.OrdinalIgnoreCase)),
            "query-backed protection keeps independent deletes and describes non-evidence child holds honestly");
        Console.WriteLine("PASS PostPrepare_HeldChildNeverCollapsedOver");
    }

    // Fix 1: an ordinary rule-6 folder delete must never absorb an unreviewed rule-19 child. Mixed provenance keeps
    // the folder its own DescendantsPlanned delete (only the emptied folder) and leaves the child's reviewed delete.
    private static void PostPrepare_MixedNeedsReviewChildKeepsFolderDeleteSeparate()
    {
        var folder = D("d", "dbx:d");
        var child = F("d/x", "dbx:x", "hx", mtime: T0);
        var at = T0.AddMinutes(1);
        var rows = new[]
        {
            Row(Base("kd", D("d", "loc:d"), folder), null, folder, Ev(A: true, atA: at)),
            Row(null, null, child, Ev(A: true, atA: at) with { DeletedHashA = child.Hash }),
        };
        var plan = Decide(true, rows);
        Require(plan.Actions.Any(action => action is
            { Kind: ActionKind.Delete, Target: Side.B, Path: "d", Rule: 6, DescendantsPlanned: true }),
            "a rule-6 folder delete with an unreviewed rule-19 child is not collapsed");
        Require(plan.Actions.Any(action => action is
            { Kind: ActionKind.Delete, Target: Side.B, Path: "d/x", Rule: 19, NeedsReview: true }),
            "the rule-19 child keeps its own delete and review flag");
        Require(plan.Actions.All(action => action.Rule != 23), "the mixed folder is never a rule-23 collapse");
        Console.WriteLine("PASS PostPrepare_MixedNeedsReviewChildKeepsFolderDeleteSeparate");
    }

    // Fix 3: copy and remove choices both run after the prepare-matched comparison, so an unchanged survivor of a
    // post-prepare deletion is ordinary propagation (rule 6, no choice flag), never resurrected or re-reviewed.
    private static void PostPrepare_UnchangedSurvivorBeatsChoice()
    {
        foreach (var remove in new[] { false, true })
        {
            var local = F("d/x", "loc:x", "hx");
            var match = new PrepareMatch(Content.SideStamp(local), "dbx-gone|v:1");
            var options = new PlanOptions(TimeSpan.FromSeconds(2), true, Label, V2Settings.Default.MaxLabelAttempts,
                null, [new OneSidedChoice("d", Side.A, remove)], null,
                new Dictionary<string, PrepareMatch>(StringComparer.OrdinalIgnoreCase) { ["d/x"] = match });
            var plan = Planner.Decide(PairId, Gen, new[] { Row(null, local, null, Ev(B: true)) }, options);
            var action = plan.Actions.Single();
            Require(action is { Kind: ActionKind.Delete, Target: Side.A, Path: "d/x", Rule: 6 } &&
                !action.FirstSyncChoice && !action.ChoiceRemove && action.RecoveryFirst,
                "an unchanged prepare-matched survivor is ordinary propagation under a " +
                (remove ? "remove" : "copy") + " choice");
        }
        Console.WriteLine("PASS PostPrepare_UnchangedSurvivorBeatsChoice");
    }
}
