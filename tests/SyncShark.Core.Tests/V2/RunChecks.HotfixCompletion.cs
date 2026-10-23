namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task HotfixCompletionChecksAsync()
    {
        await AutoSyncChecks.ImmediateAccessNoticeAsync();
        await MaintenanceColdRunAsync();
        await CountedPauseColdRunAsync();
        await DurableGapCompletionAsync();
        await DurableGapCompletionAsync(interrupt: true);
        Require(!SyncIgnorePolicy.IsIgnored("", true, [new(SyncIgnoreKind.FolderName, "cache")]),
            "root retry sentinel is never ignored");
        var store = new RecordingStore(new InMemoryStateStore());
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        store.SavePair(fx.Pair with { IgnoreRules = ["FolderName:cache"] });
        store.SaveSyncRetry(fx.Pair.PairId, new("", "run", 1, "earlier run failure", DateTimeOffset.MinValue));
        store.RootIntakeReads = 0;
        fx.A.Seed("first.txt", "copy with root retry");
        var result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome != RunOutcomes.Failed && result.Done == 1 && result.Uploaded == 1 &&
            store.SyncRetryAt(fx.Pair.PairId, "") is null && store.RootIntakeReads == 0,
            "root retry with active ignore rules completes, retains transfer tally and retires run debt");

        store.FailPrune = true;
        fx.A.Seed("second.txt", "copy before completion fault");
        result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var measuredRunNotice = store.OpenNotices(fx.Pair.PairId).SingleOrDefault(n => n.ReasonKey == "run-unavailable");
        Console.WriteLine($"COMPLETION DEBT MEASUREMENT outcome={result.Outcome}; done={result.Done}; uploaded={result.Uploaded}; failed={result.Failed}; completionFailed={result.CompletionFailed}; rootKind={store.SyncRetryAt(fx.Pair.PairId, "")?.Kind}; noticeId={measuredRunNotice?.NoticeId}; attempts={measuredRunNotice?.FailureRetry?.Attempts}; due={measuredRunNotice?.FailureRetry?.RetryAfter:O}; detail={measuredRunNotice?.Detail}");
        // Recorded pair/run ownership: failure debt lives on its canonical notice, never a path retry row.
        Require(result.Outcome == RunOutcomes.Partial && result.Done == 1 && result.Uploaded == 1 &&
            result.Failed == 0 && result.CompletionFailed && store.SyncRetryAt(fx.Pair.PairId, "") is null &&
            measuredRunNotice?.NoticeId == FailureRetryOwner.RunNoticeId(fx.Pair.PairId) &&
            measuredRunNotice.FailureRetry is { Attempts: 1 } debt && debt.RetryAfter > DateTimeOffset.MinValue,
            "post-effect failure preserves real counts and canonical notice-owned retry debt without a root path row");
        var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "run-unavailable");
        Require(notice.Detail.Contains("injected completion prune failure", StringComparison.Ordinal) &&
            notice.Detail.Contains("Retry", StringComparison.Ordinal) && notice.Detail.Contains("Automatic retries", StringComparison.Ordinal) &&
            !notice.Detail.Contains("Check endpoint access", StringComparison.Ordinal),
            "completion notice names the exact bookkeeping failure and automatic repair path without inventing an access failure");
        Require(NoticeOptions.Options(notice).Any(o => o.Choice == NoticeChoices.RetryNow) &&
            NoticeOptions.Options(notice).Any(o => o.Choice == NoticeChoices.Ignore),
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
        // Recorded fatal-scope decision: damage is in-memory StateDamaged, never another store notice.
        store.PruneError = new StateDamagedException("injected damaged store after effects");
        fx.A.Seed("fourth.txt", "completed before damaged-store report");
        result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(result.Outcome == RunOutcomes.StateDamaged && result.Done == 1 && result.Uploaded == 1 && result.CompletionFailed && result.HaltedBy is StateDamagedException,
            "even a terminal completion safety failure cannot erase already completed effect counts");
        store.PruneError = new EndpointAccessException("injected lost account access");
        fx.A.Seed("fifth.txt", "completed before lost access");
        result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Console.WriteLine($"COMPLETION AUTHORITY MEASUREMENT outcome={result.Outcome}; done={result.Done}; uploaded={result.Uploaded}; failed={result.Failed}; halted={result.HaltedBy?.GetType().Name}; notices={string.Join(" | ", store.OpenNotices(fx.Pair.PairId).Select(n => $"{n.ReasonKey}; cause={n.CauseKey}; attempts={n.FailureRetry?.Attempts}; detail={n.Detail}"))}");
        var accessNotice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "run-unavailable");
        // Measured canonical owner: preserve actual access cause and completed counts, not a duplicate host label.
        Require(result.Outcome == RunOutcomes.Partial && result.Done == 1 && result.Uploaded == 1 && result.Failed == 0 &&
            result.HaltedBy is EndpointAccessException && accessNotice.NoticeId == FailureRetryOwner.RunNoticeId(fx.Pair.PairId) &&
            accessNotice.CauseKey == "injected lost account access" && accessNotice.FailureRetry is { Attempts: 2 } &&
            accessNotice.Detail.Contains("Reconnect", StringComparison.Ordinal) &&
            NoticeOptions.Options(accessNotice).Any(option => option.Choice == NoticeChoices.RetryNow) &&
            NoticeOptions.Options(accessNotice).Any(option => option.Choice == NoticeChoices.Ignore),
            "lost completion authority retains exact counts and cause with one durable actionable reconnect notice");
        store.PruneError = null;
        var executionStore = new RecordingStore(new InMemoryStateStore());
        var executionFx = Build(suppliedStore: executionStore);
        var executionRun = executionFx.Run();
        await FirstSyncAsync(executionRun, executionFx);
        executionFx.A.Seed("durable.txt", "committed before executor finally");
        executionStore.RefusalRefreshError = new EndpointAccessException("injected executor-finally authority loss");
        result = await executionRun.IncrementalAsync(executionFx.Pair.PairId, new RecordingProgress(), default);
        // Decision 3, run.md Terminal boundary decisions: an executor crash is Failed, even after Done.
        Require(result.Done == 1 && result.Uploaded == 1 && result.Outcome == RunOutcomes.Failed &&
            result.HaltedBy is EndpointAccessException && executionStore.UnresolvedIntents(executionFx.Pair.PairId).Count == 0,
            "executor finally failure keeps true committed tally before ExecuteWithEvidence returns");
        var cliStore = new RecordingStore(new InMemoryStateStore());
        var cliFx = Build(suppliedStore: cliStore);
        var cliRun = cliFx.Run();
        await FirstSyncAsync(cliRun, cliFx);
        cliFx.WriterB.PutFailure = _ =>
        {
            cliStore.RefusalRefreshError = new EndpointAccessException("injected CLI post-copy access loss");
            return null;
        };
        cliFx.A.Seed("cli.txt", "committed before CLI access loss");
        var runtime = new V2CliRuntime(null, cliStore, cliRun,
            new EndpointRootProbe(cliFx.Provider), Path.Combine(Path.GetTempPath(), "cli-counted-halt-" + Guid.NewGuid().ToString("N")));
        try
        {
            await runtime.RunPairAsync(executionFx.Pair.PairId, _ => { }, default);
            throw new InvalidOperationException("CLI must surface reconnect after counted access loss");
        }
        catch (SyncCliReviewException error)
        {
            Require(error.Message.Contains("Sign in to Dropbox again", StringComparison.Ordinal) &&
                error.Data["RunReport"] is RunReport { Done: 1, Uploaded: 1, HaltedBy: EndpointAccessException },
                "CLI reconnect classification retains its counted report: " + error.Message + "; report=" + error.Data["RunReport"]);
        }
        executionStore.RefusalRefreshError = null;
        var nextActions = new System.Collections.Concurrent.ConcurrentQueue<SyncAction>();
        executionStore.IntentRecorded = nextActions.Enqueue;
        var callsBefore = executionFx.WriterA.Calls + executionFx.WriterB.Calls;
        var priorBaselines = executionStore.Baselines(executionFx.Pair.PairId).Select(b => b.ObjectKey).Order().ToArray();
        result = await executionRun.IncrementalAsync(executionFx.Pair.PairId, new RecordingProgress(), default);
        Console.WriteLine($"NEXT-RUN done={result.Done}; outcome={result.Outcome}; deferred={result.Deferred}; deleted={result.Deleted}; uploaded={result.Uploaded}; downloaded={result.Downloaded}; actions={string.Join(",", nextActions.Select(a => a.Kind + ":" + a.Path))}; baselines-before={string.Join(",", priorBaselines)}; baselines-after={string.Join(",", executionStore.Baselines(executionFx.Pair.PairId).Select(b => b.ObjectKey).Order())}; durable-retries={executionStore.CountSyncRetries(executionFx.Pair.PairId)}");
        Require(result.Deleted == 0 && !nextActions.Any(a => a.Kind is ActionKind.CopyNew or ActionKind.Delete) &&
            result.Uploaded == 0 && result.Downloaded == 0 && executionFx.WriterA.Calls + executionFx.WriterB.Calls == callsBefore,
            "next run attempts no CopyNew/Delete and calls no endpoint effects, so moves zero bytes");
        Require(priorBaselines.SequenceEqual(executionStore.Baselines(executionFx.Pair.PairId).Select(b => b.ObjectKey).Order()),
            "next run retains the original committed baseline; no Adopt or replacement baseline is needed");
        Require(result.Done == 0 && result.Outcome == RunOutcomes.Complete && result.Deferred == 0,
            "next run does not duplicate the already committed copy");
        Console.WriteLine("PASS root retry, post-effect tally, deduplicated completion notice and recovery");
    }
    private static async Task CountedPauseColdRunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "counted-pause-" + Guid.NewGuid().ToString("N"));
        Fixture fx;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            fx = Build(suppliedStore: store);
            await FirstSyncAsync(fx.Run(), fx);
            fx.A.Seed("saved.txt", "committed before pause");
            var run = new SyncRun(store, new PauseAfterDone(NewExecutor(store)), fx.Provider, Settings, TimeProvider.System);
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(report.Outcome == RunOutcomes.Paused && report.Done >= 1 &&
                report.HaltedBy is null && !report.CompletionFailed,
                "counted pause retains paused report without a completion halt");
            Require(store.SyncRetryAt(fx.Pair.PairId, "") is null &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "run-unavailable" || n.ReasonKey.StartsWith("autosync-failed")),
                "counted pause creates no bookkeeping retry or failure notice");
            await AutoSyncChecks.CountedPauseDoesNotHoldAsync(report);
        }
        using (var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            Require(reopened.SyncRetryAt(fx.Pair.PairId, "") is null &&
                !reopened.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "run-unavailable" || n.ReasonKey.StartsWith("autosync-failed")),
                "cold reopen retains clean pause bookkeeping");
            var run = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, Settings, TimeProvider.System);
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(report.Outcome == RunOutcomes.Complete && report.Uploaded == 0 && report.HaltedBy is null,
                "cold next run resumes without repeating the completed copy");
        }
    }

    private sealed class PauseAfterDone(IExecutor inner) : IExecutor
    {
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var result in inner.ApplyAsync(plan, a, b, ct))
            {
                yield return result;
                if (result.Outcome == Outcome.Done) throw new PairPausedException("p1");
            }
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(intents, gen, a, b, ct, progress);
    }

    private static async Task MaintenanceColdRunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "maintenance-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture fx;
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                fx = Build(suppliedStore: store);
                var run = fx.Run();
                await FirstSyncAsync(run, fx);
                fx.A.Seed("saved.txt", "durable file");
                store.ResultMaintenanceForTest = () => throw new IOException("injected committed cleanup failure");
                var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(report.Uploaded == 1 && report.Failed == 0 && store.UnresolvedIntents(fx.Pair.PairId).Count == 0 &&
                    store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "store-maintenance") == 1,
                    "maintenance failure cannot convert committed copy into failed or replayable effect");
            }
            using (var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                var run = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, Settings, TimeProvider.System);
                var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(report.Uploaded == 0 && report.Downloaded == 0 &&
                    !reopened.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "store-maintenance"),
                    "cold next run retries maintenance even with no file effects and clears its notice");
            }
        }
        finally { LocalJournalChecks.RemoveFixture(folder); }
    }

}
