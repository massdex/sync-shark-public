namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static void RunTallyMemoryChecks()
    {
        Tally_DuplicateTerminalResultCountsOnce();
        var fx = Build();
        var tally = new SyncRun.Tally();
        var action = new SyncAction("planned", 1, ActionKind.CopyNew, Side.B, "x", null, null, null, false, null);
        Parallel.For(0, 64, _ => tally.Plan([action, action, action with { ActionId = "hold", Kind = ActionKind.Hold }]));
        tally.Add(fx.Pair, action, new ActionResult(action.ActionId, Outcome.Failed, null, null, null, "failed"));
        tally.Add(fx.Pair, action, new ActionResult(action.ActionId, Outcome.Done, null, null, null, null));
        tally.Add(fx.Pair, action, new ActionResult(action.ActionId, Outcome.Done, null, null, null, null));
        tally.Add(fx.Pair, null, new ActionResult("child", Outcome.Skipped, null, null, null, "skipped"));
        tally.Add(fx.Pair, null, new ActionResult("child", Outcome.Skipped, null, null, null, "skipped"));
        Require(tally.Total == 1 && tally.PlannedDone == 1 && tally.ExtraDone == 1 && tally.ProgressDone() == 1,
            "duplicate plans and terminal results advance unique progress once, excluding Holds");
        Require(tally.WasDone(action.ActionId) && !tally.WasDone("child") && !tally.WasDone("missing") &&
            tally.Done == 2 && tally.Failed == 1 && tally.Skipped == 2,
            "membership remains sticky Done while aggregate result counts retain existing duplicate semantics");
        // A result observed before a later plan retains its original extra classification.
        tally.Plan([action with { ActionId = "child" }]);
        tally.Add(fx.Pair, null, new ActionResult("child", Outcome.Done, null, null, null, null));
        Require(tally.Total == 2 && tally.PlannedDone == 1 && tally.ExtraDone == 1 && tally.WasDone("child"),
            "late naming does not reclassify already counted extra results");

        var failures = new SyncRun.Tally();
        for (var i = 0; i < 240; i++)
            failures.Add(fx.Pair, null, new ActionResult("failure-" + i,
                i < 205 ? Outcome.Failed : Outcome.Uncertain, null, null, null, "reason-" + i));
        Require(failures.Failures.Count == 200 && failures.Failed == 205 && failures.Uncertain == 35 &&
            failures.ExtraDone == 240 && failures.Failures.Select(result => result.ActionId)
                .SequenceEqual(Enumerable.Range(0, 200).Select(i => "failure-" + i)),
            "failure examples retain exactly the first 200 with accurate failed and uncertain totals");
    }
}
