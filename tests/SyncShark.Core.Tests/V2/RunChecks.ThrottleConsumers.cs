namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task ThrottleConsumersAsync()
    {
        foreach (var full in new[] { false, true })
        {
            var fx = Build();
            fx.A.Seed("retained.txt", "same");
            fx.B.Seed("retained.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            var clock = new CompletionClock();
            var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
            var deadline = clock.Now.AddMinutes(6);
            var failure = new IOException("wrapped listing", new ProviderThrottleExhaustedException(deadline, "backoff-bound", "list"));
            var inventory = new ScriptedInventory(fx.B) { ChangesError = failure, FullError = failure };
            fx.Provider.AddInventory(fx.Pair.B, inventory);
            if (full) await run.RequestRescanAsync([fx.Pair.B.RootId], default);
            var writes = fx.WriterA.Calls + fx.WriterB.Calls;
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "")!;
            Require(retry.Kind == "scan" && retry.FailureKind == FailureRetryOwner.FailureKind.ProviderThrottle &&
                retry.RetryAfter == deadline && retry.ReadRoots!.Contains(fx.Pair.B.RootId),
                "inventory exhaustion retains its wrapped throttle deadline as root scan debt");
            var extended = deadline.AddMinutes(1);
            inventory.ChangesError = inventory.FullError = new ProviderThrottleExhaustedException(extended, "wait-bound", "list");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var sleeping = fx.Store.SyncRetryAt(fx.Pair.PairId, "")!;
            Require(sleeping.RetryAfter == extended && sleeping.Attempts == retry.Attempts &&
                fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "sync-retry") == 1,
                "a later cooldown extends sleeping scan debt without another attempt or notice");
            Require(fx.WriterA.Calls + fx.WriterB.Calls == writes, "throttled inventory grants no absence effects");
            inventory.ChangesError = inventory.FullError = null;
            clock.Now = extended;
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "") is null, "successful inventory retires throttle scan debt");
        }
        await ThrottleDoneClosesNoticeAsync();
        await SleepingScanRetryNotRewrittenAsync();
    }

    // A sleeping scan row used to be rewritten on every run even when no new provider floor existed, so
    // thousands of escalated rows became thousands of autocommit upserts per run (1M rows at 1M scale).
    // The row may only be written when the floor moves its deadline later.
    private static async Task SleepingScanRetryNotRewrittenAsync()
    {
        var fx = Build(record: true);
        fx.A.Seed("retained.txt", "same");
        fx.B.Seed("retained.txt", "same");
        await FirstSyncAsync(fx.Run(), fx);
        var clock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        var deadline = clock.Now.AddMinutes(6);
        var failure = new IOException("wrapped listing",
            new ProviderThrottleExhaustedException(deadline, "backoff-bound", "list"));
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B) { ChangesError = failure, FullError = failure });
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var sleeping = fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty)!;
        Require(sleeping is { Kind: "scan", RetryAfter: var first } && first == deadline,
            "throttled root scan debt sleeps at the provider deadline");

        // The root feed is incomplete again but no longer throttled: floor is null, so the row must not be written.
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B) { Incomplete = true });
        var writes = fx.Recorder!.SaveRetryCalls;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Recorder.SaveRetryCalls == writes && fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty) == sleeping,
            "a sleeping scan retry with no new floor is not rewritten");

        // A later provider floor still moves the deadline, without counting another attempt.
        var extended = deadline.AddMinutes(1);
        var later = new IOException("wrapped listing",
            new ProviderThrottleExhaustedException(extended, "wait-bound", "list"));
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B) { ChangesError = later, FullError = later });
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var raised = fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty)!;
        Require(raised.FailureKind == FailureRetryOwner.FailureKind.ProviderThrottle && raised.RetryAfter == extended &&
            raised.Attempts == sleeping.Attempts && fx.Recorder.SaveRetryCalls == writes + 1,
            "a later floor still raises sleeping scan debt in exactly one write with no new attempt");
    }

    private static async Task ThrottleDoneClosesNoticeAsync()
    {
        var fx = Build();
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("queued.txt", "content");
        var clock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        var deadline = clock.Now.AddMinutes(6);
        var error = new ProviderThrottleExhaustedException(deadline, "retry-bound", "read");
        var failed = FailureRetryOwner.WithCause(new("action", Outcome.Failed, null, null, null, error.Message), error);
        run.SaveFailedRetry(fx.Pair, "queued.txt", failed.Reason, null, failure: failed);
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == "queued.txt" && n.ReasonKey == "sync-retry"),
            "throttle failure starts with retry debt and its notice");
        clock.Now = deadline;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Done > 0 && await fx.B.ObserveAsync("queued.txt", default) is not null &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "queued.txt") is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == "queued.txt" && n.ReasonKey == "sync-retry"),
            "verified Done through the run closes throttle retry debt and notice without direct store clearing");
    }
}
