namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void Rule27RetirementIdentity()
    {
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var a = new ItemState("Shared.txt", ItemKind.File, "local", null, "old", 1, at, ItemFlags.None);
        var b = a with { ObjectId = "cloud" };
        var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
            new LabelRule("{name} [{side} {time}]{ext}", "yyyy-MM-dd", TimeSpan.Zero, "Local", "Peer"), 10);
        var ids = new List<string>();
        foreach (var key in new[] { "basis-one", "basis-two" })
        {
            var row = new PlanRow(new Baseline("pair", key, a, b, 1), a with { Hash = "edit-a" },
                b with { Hash = "edit-b" }, new Evidence(false, false, true, true));
            var plan = Planner.Decide("pair", 7, [row], options);
            var forget = plan.Actions.Single(action => action.Kind == ActionKind.Forget);
            Require(forget.Rule == 27 && forget.ObjectKey == key &&
                forget.ActionId == PlanIds.ActionId("pair", 7, 27, ActionKind.Forget, forget.Target, forget.Path, key),
                "actual keep-both rule-27 retirement uses the baseline identity");
            ids.Add(forget.ActionId);
        }
        Require(ids.Distinct().Count() == 2, "rule-27 retirements at one path have identity-scoped ids");
    }

    private static void EmbeddedCatalogsAreStrictUtf8()
    {
        var assembly = typeof(SyncRun).Assembly;
        var resources = assembly.GetManifestResourceNames().Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(resources.Contains("SyncShark.EdgeMessages.json"), "embedded message catalog is included in the encoding guard");
        foreach (var name in resources)
        {
            using var source = assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes.ToArray());
            using var document = System.Text.Json.JsonDocument.Parse(text.TrimStart('﻿'));
        }
        Require(MessageCatalog.Default.Message("firstSyncIncomplete").Length > 0,
            "catalog loader decodes every message, including non-ASCII text");
        EffectTraceCatalogCoverage();
    }

    private static async Task DuplicateFirstFailureAsync()
    {
        var fx = Build();
        fx.B.Seed("first.txt", "first content");
        fx.B.Seed("sibling.txt", "independent content");
        var deadline = DateTimeOffset.UtcNow.AddHours(1);
        var error = new ProviderThrottleExhaustedException(deadline, "retry-bound", "fixture-route");
        fx.WriterA.PutFailure = action => action.Path == "first.txt" ? error : null;
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var first = plan.Actions.Single(action => action.Path == "first.txt");
        plan = plan with { Actions = plan.Actions.Append(first with { ObjectKey = "rejected-key" }).ToArray() };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
        var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, first.Path)!;
        Require(report.Failed == 2 && report.Outcome == RunOutcomes.Partial && retry.Attempts == 1,
            "one real failure plus one rejected duplicate share one path attempt");
        var actualFailure = report.Failures.Single(result => result.RejectedDuplicatePath is null);
        Require(actualFailure.Reason!.Contains(error.Message) && retry.LastError == actualFailure.Reason &&
            retry.FailureKind == FailureRetryOwner.FailureKind.ProviderThrottle && retry.RetryAfter >= deadline,
            "the exact executor cause (including diagnostics), typed failure and provider Retry-After survive duplicate admission");
        Require(retry.ReviewedActions is { Count: 1 } && retry.ReviewedActions[0].ObjectKey == first.ObjectKey,
            "first-sync parks only the kept action's reviewed authority");
        Require(await fx.A.ObserveAsync("sibling.txt", default) is not null,
            "independent work executes despite both failures");
    }

    private static void DuplicateCompletionFailureContract()
    {
        foreach (var firstSync in new[] { false, true })
        {
            var fx = Build();
            var run = fx.Run();
            var source = fx.A.Seed("conflict.txt", "content");
            var first = new SyncAction("same-id", 27, ActionKind.KeepBoth, Side.B, source.Path,
                null, source, null, false, "kept-key") { ConflictTransitionKey = "basis" };
            var plan = new Plan(fx.Pair.PairId, 7, [first, first with { ObjectKey = "rejected-key" }], []);
            var tally = new SyncRun.Tally(Settings);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            object?[] indexArgs = [plan, fx.Pair, tally];
            typeof(SyncRun).GetMethod("IndexPlanActions", flags)!.Invoke(run, indexArgs);
            plan = (Plan)indexArgs[0]!;
            var deadline = DateTimeOffset.UtcNow.AddHours(1);
            var failure = new ActionResult(first.ActionId, Outcome.Failed, null, null, null, "real provider failure")
                { FailureKind = FailureRetryOwner.FailureKind.ProviderThrottle, RetryNotBefore = deadline };
            tally.Add(fx.Pair, first, failure);
            if (firstSync)
                typeof(SyncRun).GetMethod("ParkFirstSyncWorkWithEvidence", flags)!.Invoke(run,
                    [fx.Pair, plan, tally, new[] { first.Path }, Array.Empty<string>(), Array.Empty<string>(), false]);
            else
                typeof(SyncRun).GetMethod("UpdateRetryWork", flags)!.Invoke(run,
                    [fx.Pair, plan, tally, Array.Empty<SyncRetry>(), new[] { first.Path }, (Func<string, bool>)(_ => true), true]);
            var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, first.Path)!;
            Require(retry.Attempts == 1 && retry.LastError == failure.Reason && retry.FailureKind == failure.FailureKind &&
                retry.RetryAfter >= deadline, "both completion owners preserve the real failure without another attempt");
            var notices = fx.Store.OpenNotices(fx.Pair.PairId).Where(notice => notice.Path == first.Path).ToArray();
            Require(notices.Any(notice => notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked &&
                notice.Detail.Contains(failure.Reason!) && !notice.Detail.Contains("Ignore")) &&
                !notices.Any(notice => notice.ReasonKey == "sync-retry"),
                "tagged successors retain their real Retry-only failure contract");
        }
    }
}
