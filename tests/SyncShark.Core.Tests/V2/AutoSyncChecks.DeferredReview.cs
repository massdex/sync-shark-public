namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    private static async Task Deferred_ReviewRegressionsAsync()
    {
        await Deferred_OuterFailureReplacesBackoffAsync();
        await Deferred_WatchFaultRebuildsImmediatelyAsync();
        await Deferred_SecondWatchFaultDegradesAsync();
        await Evidence_NoProgressBacksOffAsync();
        await Evidence_JournalRunsImmediatelyAsync();
    }

    private static async Task Deferred_WatchFaultRebuildsImmediatelyAsync()
    {
        await using var rig = new Rig(["p1"]);
        FailureRetryOwner.RecordDeferredRetry(rig._store, "p1", 1,
            FailureRetryOwner.Failed(5, rig.Clock.GetUtcNow()));
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial) with { Deferred = 1 });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        var deadline = rig.State("p1").RetryAt!.Value;
        rig.WatchA.Fail(new IOException("one-shot deferred watch fault"));
        await Until(() => rig.Watches.Created == 4 && rig.WatchA.Parked && rig.WatchB.Parked &&
            rig.Clock.ActiveTimers == 1, "deferred fault immediately rebuilds and debounces startup signals");
        Require(rig.State("p1").RetryAt is null && rig.Run.Runs("p1") == 1,
            "rebuild does not wait for the old deadline or skip debounce");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2 && rig.Clock.GetUtcNow() < deadline,
            "watch repair catches up once before the deferred deadline");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "rebuilt startup signals cannot cause duplicate catch-up");
        Pass("Deferred_WatchFaultRebuildsImmediately");
    }

    private static async Task Deferred_SecondWatchFaultDegradesAsync()
    {
        await using var rig = new Rig(["p1"]);
        FailureRetryOwner.RecordDeferredRetry(rig._store, "p1", 1,
            FailureRetryOwner.Failed(5, rig.Clock.GetUtcNow()));
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial) with { Deferred = 1 });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.WatchA.Fail(new IOException("first watch fault"));
        await Until(() => rig.Watches.Created == 4 && rig.WatchA.Parked && rig.WatchB.Parked &&
            rig.Clock.ActiveTimers == 1, "first fault reaches rebuilt startup debounce");
        rig.WatchA.Fail(new IOException("second watch fault"));
        await Until(() => rig.State("p1").Reason == AutoSyncReasons.WatchUnavailable &&
            rig.WatchA.Disposed && rig.WatchB.Disposed && rig.Clock.ActiveTimers == 1,
            "second death before catch-up degrades to timed polling");
        Require(rig.Watches.Created == 4, "repeated dead watches cannot rebuild in a tight loop");
        Pass("Deferred_RepeatFault");
    }

    private static async Task Evidence_JournalRunsImmediatelyAsync()
    {
        await using var rig = new Rig(["p1"]);
        var pending = FakeSyncRun.Report("p1", RunOutcomes.Partial) with
        { DeletionEvidencePending = new[] { "pending.txt" } };
        rig.Run.Then(pending with { JournalBacklog = true });
        rig.Run.Then(pending);
        await rig.StartAsync();
        var now = rig.Clock.GetUtcNow();
        await Until(() => rig.Run.Runs("p1") == 2 && rig.State("p1").RetryAt > now &&
            rig.Clock.ActiveTimers == 1, "journal backlog drains before evidence backoff without advancing time");
        Require(rig.Clock.GetUtcNow() == now &&
            FailureRetryOwner.DeferredRetryState(rig._store, "p1")?.Attempts == 1,
            "current journal work runs immediately; unchanged evidence then opens its shared tier");
        Pass("Evidence_JournalPriority");
    }

    private static async Task Evidence_NoProgressBacksOffAsync()
    {
        var tuned = Settings with { MinSafetyPoll = TimeSpan.FromSeconds(2) };
        await using var rig = new Rig(["p1"], settings: tuned);
        rig._store.SaveSyncRetry("p1", new SyncRetry("pending.txt", "deletion-evidence", 1,
            "Evidence is unavailable", rig.Clock.GetUtcNow()));
        var pending = FakeSyncRun.Report("p1", RunOutcomes.Partial) with
        { DeletionEvidencePending = new[] { "pending.txt", "other.txt" } };
        rig.Run.Then(pending);
        rig.Run.Then(pending);
        rig.Run.Then(pending with { DeletionEvidencePending = new[] { "pending.txt" } });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        var first = FailureRetryOwner.Failed(0, rig.Clock.GetUtcNow());
        Require(rig.State("p1").RetryAt == first.RetryAfter &&
            FailureRetryOwner.DeferredRetryState(rig._store, "p1")?.Attempts == 1,
            "evidence-only debt opens the shared tier without a real failure episode");
        rig.Clock.Advance(first.RetryAfter - rig.Clock.GetUtcNow());
        await Until(() => rig.Run.Runs("p1") == 2 && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() &&
            rig.Clock.ActiveTimers == 1, "unchanged evidence completes and re-arms");
        var second = FailureRetryOwner.Failed(1, rig.Clock.GetUtcNow());
        Require(rig.State("p1").RetryAt == second.RetryAfter &&
            FailureRetryOwner.DeferredRetryState(rig._store, "p1")?.Attempts == 2,
            "persistent evidence advances the retained shared counter");
        rig.Clock.Advance(second.RetryAfter - rig.Clock.GetUtcNow());
        await Until(() => rig.Run.Runs("p1") == 3 && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() &&
            rig.Clock.ActiveTimers == 1, "shrinking evidence completes and re-arms");
        Require(FailureRetryOwner.DeferredRetryState(rig._store, "p1") is null &&
            rig.State("p1").RetryAt == rig.Clock.GetUtcNow() + tuned.MinSafetyPoll,
            "evidence progress resets the shared tier to the retained item deadline");
        Pass("Evidence_NoProgressBacksOff");
    }
}
