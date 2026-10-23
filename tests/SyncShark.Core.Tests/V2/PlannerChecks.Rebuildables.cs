namespace Massdex.SyncShark.V2;

/// <summary>Planner checks for the rebuildable-folder choice (rule 32, docs/design/rebuildables.md).</summary>
internal static partial class PlannerChecks
{
    private static void RunRebuildableChecks()
    {
        Rule32_RebuildableStopsSyncing();
        Rule32_RebuildableLocalOnlyUploadsNothing();
        Rule32_RebuildableSpellingKeptExactly();
        Rule32_RebuildableBoundary();
        Rule32_RebuildableWithoutChoiceUnchanged();
        Rule32_RebuildableCollapsesToOutermostRoot();
        Rule32_RebuildableUsesThePairCloudSide();
        Rule32_RebuildableCounterpartNotAFolderIsLeftAlone();
    }

    // The same folder and file on both sides: only one cloud folder Delete is planned, from a row that holds the
    // local copy, so it is never a one-sided removal; nothing is planned under the folder or on the local side.
    private static void Rule32_RebuildableStopsSyncing()
    {
        var rows = new[]
        {
            Row(null, D("node_modules", "idA"), null),
            Row(null, F("node_modules/p/x.js", "idA2", "h1"), null),
            Row(null, null, D("node_modules", "idB")),
            Row(null, null, F("node_modules/p/x.js", "idB2", "h2"))
        };
        var plan = DecideRebuildables(Side.B, ["node_modules"], rows);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "node_modules", null, false, 32, null, null));
        Require(plan.Actions[0].RebuildableRemove, "the delete is marked as the rebuildable cloud removal");
        Require(!plan.Actions[0].ChoiceRemove, "the rebuildable delete is not a one-sided choice remove");
        Require(!plan.Actions[0].OppositeAbsent, "the rebuildable delete is never a one-sided removal");
        AssertEmptyNotices(plan);
    }

    // A rebuildable folder only on the local side is never uploaded and nothing at all is planned.
    private static void Rule32_RebuildableLocalOnlyUploadsNothing()
    {
        var rows = new[]
        {
            Row(null, D("node_modules", "idA"), null),
            Row(null, F("node_modules/p/x.js", "idA2", "h1"), null)
        };
        AssertEmpty(DecideRebuildables(Side.B, ["node_modules"], rows));
    }

    // The inventory spelling is kept exactly: a folder whose name begins with a space still matches its own path.
    private static void Rule32_RebuildableSpellingKeptExactly()
    {
        var rows = new[]
        {
            Row(null, D(" app/node_modules", "idA"), null),
            Row(null, F(" app/node_modules/x.js", "idA2", "h1"), null),
            Row(null, null, D(" app/node_modules", "idB"))
        };
        AssertActions(DecideRebuildables(Side.B, [" app/node_modules"], rows),
            new Exp(ActionKind.Delete, Side.B, " app/node_modules", null, false, 32, null, null));
    }

    // A folder whose name merely starts with the rebuildable folder's is not suppressed (a/ vs ab/).
    private static void Rule32_RebuildableBoundary()
    {
        var rows = new[] { Row(null, F("node_modules2/x.txt", "idA", "h1"), null) };
        var plan = DecideRebuildables(Side.B, ["node_modules"], rows);
        Require(plan.Actions.Any(action => action is { Target: Side.B, Path: "node_modules2/x.txt" }),
            "a sibling path is not suppressed by the rebuildable folder");
    }

    // Without the choice the very same rows are planned normally, so the suppression is the choice's doing.
    private static void Rule32_RebuildableWithoutChoiceUnchanged()
    {
        var rows = new[]
        {
            Row(null, D("node_modules", "idA"), null),
            Row(null, F("node_modules/p/x.js", "idA2", "h1"), null),
            Row(null, null, D("node_modules", "idB"))
        };
        Require(Decide(true, rows).Actions.Count > 0, "without the choice the rows are planned normally");
    }

    // Nested selections collapse to their outermost root across categories: one delete at the outer folder only.
    private static void Rule32_RebuildableCollapsesToOutermostRoot()
    {
        var rows = new[]
        {
            Row(null, D("a/node_modules", "idA2"), null),
            Row(null, D("a/node_modules/.venv", "idA3"), null),
            Row(null, null, D("a/node_modules", "idB2")),
            Row(null, null, D("a/node_modules/.venv", "idB3"))
        };
        var plan = DecideRebuildables(Side.B, ["a/node_modules", "a/node_modules/.venv"], rows);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "a/node_modules", null, false, 32, null, null));
    }

    // The cloud side is the pair's own cloud endpoint, not an assumed side B.
    private static void Rule32_RebuildableUsesThePairCloudSide()
    {
        var rows = new[]
        {
            Row(null, null, D("node_modules", "idCloud")),
            Row(null, D("node_modules", "idLocal"), null)
        };
        var plan = DecideRebuildables(Side.A, ["node_modules"], rows);
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "node_modules", null, false, 32, null, null));
        Require(plan.Actions.All(action => action.Target != Side.B), "nothing is planned on the other side");
    }

    // A path whose cloud counterpart is not a folder is never deleted here: it falls to ordinary planning.
    private static void Rule32_RebuildableCounterpartNotAFolderIsLeftAlone()
    {
        var rows = new[]
        {
            Row(null, D("node_modules", "idA"), null),
            Row(null, null, F("node_modules", "idB", "h1"))
        };
        AssertEmpty(DecideRebuildables(Side.B, ["node_modules"], rows));
    }

    private static Plan DecideRebuildables(Side cloud, IReadOnlyList<string> folders, params PlanRow[] rows) =>
        Planner.Decide(PairId, Gen, rows, new PlanOptions(TimeSpan.FromSeconds(2), true, Label,
            V2Settings.Default.MaxLabelAttempts, null, null, new RebuildableSelection(cloud, folders)));
}
