namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task DuplicatePlanActionsAsync()
    {
        await DuplicateBaselineRetirementsAsync();
        Rule27RetirementIdentity();
        EmbeddedCatalogsAreStrictUtf8();
        await DuplicateFirstFailureAsync();
        DuplicateCompletionFailureContract();
        foreach (var heldLane in new[] { false, true })
            foreach (var samePath in new[] { false, true })
                await DuplicateActionFailureAsync(heldLane, samePath);
        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            var old = "a" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', "pair", "7", "15", kind.ToString(), "B", "parent/child"))))[..16].ToLowerInvariant();
            var first = PlanIds.ActionId("pair", 7, 15, kind, Side.B, "Parent/Child", "baseline-one");
            var second = PlanIds.ActionId("pair", 7, 15, kind, Side.B, "Parent/Child", "baseline-two");
            Require(kind == ActionKind.Forget ? first != second && first != old : first == old && second == old,
                "only baseline-retiring Forget changes the established id formula: " + kind);
        }
        foreach (var rule in new[] { 15, 27 })
            Require(PlanIds.ActionId("pair", 7, rule, ActionKind.Forget, Side.B, "Parent/Child", "basis") ==
                PlanIds.ActionId("pair", 7, rule, ActionKind.Forget, Side.B, "parent/child", "basis"),
                "retirement ids remain deterministic and case-insensitive for rule " + rule);
        Console.WriteLine("PASS DuplicatePlanActions");
    }

    private static async Task DuplicateBaselineRetirementsAsync()
    {
        var fx = Build();
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var cloud = fx.B.Seed("Shared", "", kind: ItemKind.Folder);
        for (var i = 1; i <= 3; i++)
        {
            var local = cloud with { ObjectId = "local-" + i };
            var seed = new SyncAction("seed-" + i, 3, ActionKind.Adopt, Side.B, cloud.Path,
                null, local, cloud, false, "baseline-" + i);
            fx.Store.RecordIntent(fx.Pair.PairId, seed);
            fx.Store.CommitResult(fx.Pair.PairId, 1, seed,
                new(seed.ActionId, Outcome.Done, local, cloud, null, null));
        }
        var deleted = new Dictionary<string, DateTimeOffset?>();
        var rows = Planner.Join(fx.Store.Baselines(fx.Pair.PairId), [], [cloud], deleted, deleted,
            new Completeness(true, true, new HashSet<string>(), new HashSet<string>()), false);
        var plan = Planner.Decide(fx.Pair.PairId, 7, rows,
            new PlanOptions(TimeSpan.FromSeconds(2), false,
                new LabelRule("{name} [{side} {time}]{ext}", "yyyy-MM-dd", TimeSpan.Zero, "Local", "Peer"), 10));
        var retirements = plan.Actions.Where(action => action.Kind == ActionKind.Forget).ToArray();
        Require(retirements.Length == 2 && retirements.All(action => action.Rule == 15) &&
            retirements.Select(action => action.ActionId).Distinct().Count() == 2,
            "three identities sharing one local path and cloud object produce two distinct retirements");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Failed == 0 && report.Outcome != RunOutcomes.Failed,
            "the real duplicate-baseline shape completes without a whole-run failure");
        Require(!fx.Store.Baselines(fx.Pair.PairId).Any(basis =>
            retirements.Any(action => action.ObjectKey == basis.ObjectKey)),
            "both absent baseline identities are retired by their own Done results");
    }

    private static async Task DuplicateActionFailureAsync(bool heldLane, bool samePath)
    {
        var fx = Build();
        fx.B.Seed("first.txt", "first content");
        fx.B.Seed("sibling.txt", "independent content");
        if (heldLane)
        {
            fx.A.Seed("held.txt", "held content");
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
        }
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var first = plan.Actions.Single(action => action.Path == "first.txt");
        Require(first.Kind == ActionKind.CopyNew, "forced collision exercises a non-Forget effect");
        var duplicate = first with { Path = samePath ? first.Path : "rejected.txt", ObjectKey = "rejected-key" };
        plan = plan with { Actions = plan.Actions.Concat(new[] { duplicate, duplicate }).ToArray() };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default,
            heldLane ? ReviewedHeld(plan) : null);
        Require(report.Outcome == RunOutcomes.Partial && report.Failed == 2,
            "each extra duplicate is an item failure, not a failed run");
        Require(await fx.A.ObserveAsync("first.txt", default) is not null &&
            await fx.A.ObserveAsync("sibling.txt", default) is not null,
            "the first action and independent sibling both execute");
        Require(samePath || await fx.A.ObserveAsync("rejected.txt", default) is null,
            "the extra duplicate effect is never submitted to the executor");
        var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, duplicate.Path);
        Require(retry is { Attempts: 1 } && retry.ReviewedActions is null,
            "duplicate debt survives same-id success and is re-planned rather than replayed");
        var notices = fx.Store.OpenNotices(fx.Pair.PairId).Where(notice =>
            notice.Path == duplicate.Path && notice.ReasonKey == "sync-retry").ToArray();
        Require(notices.Length == 1 && notices[0].Detail.Contains("extra change was not applied") &&
            notices[0].Detail.Contains("Retry") && notices[0].Detail.Contains("Ignore"),
            "the shared failure owner supplies one actionable deduplicated notice");
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "run-unavailable"),
            "no whole-run failure notice is raised");
        Require(report.Failures.Count(result => result.RejectedDuplicatePath == duplicate.Path) == 2,
            "failure samples identify rejected paths separately from the kept effect");
        fx.Store.SaveSyncRetry(fx.Pair.PairId, retry! with { RetryAfter = DateTimeOffset.MinValue });
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, duplicate.Path) is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == duplicate.Path && notice.ReasonKey == "sync-retry"),
            "the next clean run retires duplicate debt and closes its notice");
    }
}
