namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task HotfixCompletionChecksAsync()
    {
        Require(PilotPath.NormalizeInventoryPath("") == "" &&
            !SyncIgnorePolicy.IsIgnored("", true, [new(SyncIgnoreKind.FolderName, "cache")]),
            "metadata root sentinel is valid and never ignored");
        var store = new RecordingStore(new InMemoryStateStore());
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        store.SavePair(fx.Pair with { IgnoreRules = ["FolderName:cache"] });
        store.SaveSyncRetry(fx.Pair.PairId, new("", "run", 1, "earlier run failure", DateTimeOffset.MinValue));
        fx.A.Seed("first.txt", "copy with root retry");
        var result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome != RunOutcomes.Failed && result.Done == 1 && result.Uploaded == 1 &&
            store.SyncRetryAt(fx.Pair.PairId, "") is null,
            "root retry with active ignore rules completes, retains transfer tally and retires run debt");

        store.FailPrune = true;
        fx.A.Seed("second.txt", "copy before completion fault");
        result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome == RunOutcomes.Partial && result.Done == 1 && result.Uploaded == 1 &&
            result.Failed == 0 && result.CompletionFailed &&
            store.SyncRetryAt(fx.Pair.PairId, "")?.Kind == "run",
            "post-effect failure preserves real counts and durable root retry");
        var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "run-unavailable");
        Require(NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.RetryNow) &&
            NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.Ignore),
            "run completion notice provides Retry and Ignore");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "run-unavailable") == 1,
            "repeated completion failure does not duplicate notice");
        store.FailPrune = false;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "run-unavailable") &&
            store.SyncRetryAt(fx.Pair.PairId, "") is null,
            "successful next run clears completion debt and notice");
        fx.A.Seed("third.txt", "independent completion");
        store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, "third.txt", "old issue", "completion-fixture")]);
        store.FailCloseNoticeId = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "completion-fixture").NoticeId;
        var completedBefore = store.CompletedGenerations;
        result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome == RunOutcomes.Partial && result.Uploaded == 1 && result.Done == 1 &&
            store.CompletedGenerations >= completedBefore + 2,
            "notice completion failure cannot prevent independent cursor completion on either side");
        store.FailCloseNoticeId = null;

        var applyStore = new RecordingStore(new InMemoryStateStore());
        var applyFx = Build(suppliedStore: applyStore);
        applyFx.A.Seed("apply.txt", "first sync effect");
        var applyRun = applyFx.Run();
        var plan = await applyRun.PreviewFirstSyncAsync(applyFx.Pair.PairId, new RecordingProgress(), default);
        applyStore.AddNotices([new(applyFx.Pair.PairId, 40, NoticeKind.Info, "apply.txt", "old issue", "completion-fixture")]);
        applyStore.FailCloseNoticeId = applyStore.OpenNotices(applyFx.Pair.PairId).Single(n => n.ReasonKey == "completion-fixture").NoticeId;
        result = await applyRun.ApplyAsync(applyFx.Pair.PairId, plan, new RecordingProgress(), default);
        Require(result.Outcome == RunOutcomes.Partial && result.Uploaded == 1 && result.Done == 1 &&
            applyStore.FirstSyncDone(applyFx.Pair.PairId),
            "first-sync notice failure preserves counts and allows independent safe promotion");
        var retryStore = new RecordingStore(new InMemoryStateStore());
        var retryFx = Build(suppliedStore: retryStore);
        var retryRun = retryFx.Run();
        await FirstSyncAsync(retryRun, retryFx);
        retryFx.A.Seed("good.txt", "must count");
        retryFx.A.Seed("retry.txt", "must remain pending");
        retryFx.WriterB.PutFailure = action => action.Path == "retry.txt" ? new IOException("injected item failure") : null;
        retryStore.FailSaveRetryPath = "retry.txt";
        completedBefore = retryStore.CompletedGenerations;
        result = await retryRun.IncrementalAsync(retryFx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome == RunOutcomes.Partial && result.Done == 1 && result.Failed == 1 &&
            retryStore.CompletedGenerations == completedBefore &&
            retryStore.UnresolvedIntents(retryFx.Pair.PairId).Any(a => a.Path == "retry.txt"),
            "retry persistence failure cannot advance cursors past unfinished work or erase tally/intents");
        store.PruneError = new StateDamagedException("injected damaged store after effects");
        fx.A.Seed("fourth.txt", "completed before damaged-store report");
        result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome == RunOutcomes.Partial && result.Done == 1 && result.Uploaded == 1 && result.CompletionFailed,
            "even a terminal completion safety failure cannot erase already completed effect counts");
        store.PruneError = null;
        Console.WriteLine("PASS root retry, post-effect tally, deduplicated completion notice and recovery");
    }
}
