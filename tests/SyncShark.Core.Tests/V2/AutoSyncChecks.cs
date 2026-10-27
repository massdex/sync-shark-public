using System.Diagnostics;

namespace Massdex.SyncShark.V2;

/// <summary>Continuous-sync checks (continuous-sync.md section 7) over a manual clock, fake watches and a fake run.</summary>
internal static partial class AutoSyncChecks
{
    private static readonly V2Settings Settings = V2Settings.Default;
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromMinutes(5);

    // Real-time bound for the loop to reach a parked state; the checks never sleep on the manual clock.
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(2);

    public static async Task RunAsync()
    {
        var clock = Stopwatch.StartNew();
        StateMonitorSignals();
        Require(Settings.ChangeMaxDelay == TimeSpan.FromSeconds(5), "continuous changes run within the five-second maximum delay");
        await RunTimingLogAsync();
        await RunLogNamesWakeAndResultAsync();
        await FailedRunKeepsSummaryAsync();
        await MonitorFailureKeepsSummaryAsync();
        await LatestProgressPerPairAsync();
        await StatusAnswersWithoutStoreGateAsync();
        await StatusFirstRequestSeededPairsAsync();
        StatusDeadlineBoundsTheRead();
        StatusKeepsMemoryPairsWhenStale();
        await JournalBacklog_ImmediateRunsAsync();
        await PlanningBacklog_DrainSignalAsync();
        await UrgentPagesRunWithoutActivityAsync();
        await NoticeHeldUrgentDoesNotSpinAsync();
        await Burst_OneRunAsync();
        await SteadyStream_RunsAtMaxDelayAsync();
        await SignalDuringRun_ExactlyOneMoreAsync();
        await PausedOrOff_NoRunsUntilKickAsync();
        await DurableResumeWithoutKickAsync();
        RunFailure_ExcludesDeferredAttempts();
        await Failed_RetryBacksOffAndResetsAsync();
        await Deferred_BacklogWaitsForEarliestAsync();
        await Deferred_NoProgressBacksOffAsync();
        await Deferred_FreshSignalsInterruptBackoffAsync();
        await Deferred_ReviewRegressionsAsync();
        await RefusedOrAccess_HeldUntilKickAsync();
        await ClashingLayout_HeldAtStartupAsync();
        await SafetyPoll_RunsAndRebuildsAsync();
        await Stop_CancelsCleanlyAsync();
        await TwoPairs_IndependentAsync();
        await PauseWhileQueued_NeverStartsAsync();
        await Partial_RetriesOnlyWithFailuresAsync();
        await HeldOnlyComplete_WaitsNormalIntervalAsync();
        await HeldWithRealWork_StillRetriesAsync();
        await RetryAt_NotBypassedBySignalsAsync();
        await WatchFault_RebuildsAndHoldsAsync();
        await FailureChecksAsync();
        await HostAdmissionChecksAsync();
        await WatchNotice_PersistsAndRecoversAsync();
        await ReviewStartupNoticeFaultAsync();
        await ReviewStartDuringStopDrainAsync();
        await RunCliChecks.HostCompareOutputIsNotLifecycleEvidenceAsync();
        AutoSyncSupervisorChecks.Run();
        await AutoSyncHostChecks.RunAsync();
        Console.WriteLine($"v2 autosync checks passed in {clock.ElapsedMilliseconds} ms.");
    }

    internal static async Task JournalBacklog_ImmediateRunsAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { JournalBacklog = true });
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { JournalBacklog = true });
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete));
        await rig.StartAsync(); // Startup consumes the existing quiet-window debounce.
        var before = rig.Clock.GetUtcNow();
        await Until(() => rig.Run.Runs("p1") == 3, "journal suffix drains without timer advance or monitor signals");
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Clock.GetUtcNow() == before && rig.State("p1").RetryAt is null,
            "journal backlog drains immediately and clears its retry when caught up");
        Pass("JournalBacklog_ImmediateRuns");
    }

    // A planning backlog re-runs immediately only when the remaining queue actually shrank. Admission
    // (a full ordinary page) and held rows are not progress, so they must wait for the normal schedule.
    internal static async Task PlanningBacklog_DrainSignalAsync()
    {
        // A held page of at least 2,048 rows with nothing settling: equal or rising counts never re-run.
        await using (var held = new Rig(["p1"]))
        {
            held.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 2500 });
            held.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 2500 });
            held.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 2600 });
            await held.StartAsync();
            await held.ParkedAsync(AutoStatus.Idle);
            Require(held.Run.Runs("p1") == 1 && held.State("p1").RetryAt is null,
                "a held planning page with no previous count waits for the normal schedule");
            for (var expected = 2; expected <= 3; expected++)
            {
                held.Clock.Advance(SafetyPoll);
                await held.ParkedAsync(AutoStatus.Waiting);
                held.Clock.Advance(Settings.ChangeQuietWindow);
                await held.ParkedAsync(AutoStatus.Idle);
                Require(held.Run.Runs("p1") == expected && held.State("p1").RetryAt is null,
                    "an equal or rising remaining count schedules no immediate re-run");
            }
            Require(held.Run.Runs("p1") == 3, "a page of 2,048+ held rows reaches the normal schedule every run");
        }
        // A partial-page shrink is not enough to justify another full admission page immediately.
        await using (var partial = new Rig(["p1"]))
        {
            partial.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 2500 });
            partial.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 1000 });
            await partial.StartAsync();
            await partial.ParkedAsync(AutoStatus.Idle);
            partial.Clock.Advance(SafetyPoll);
            await partial.ParkedAsync(AutoStatus.Waiting);
            partial.Clock.Advance(Settings.ChangeQuietWindow);
            await partial.ParkedAsync(AutoStatus.Idle);
            Require(partial.Run.Runs("p1") == 2, "the sub-page shrink runs once on the normal schedule");
            partial.Clock.Advance(TimeSpan.FromSeconds(1));
            await partial.SettledAsync();
            Require(partial.Run.Runs("p1") == 2,
                "a shrink below one admitted page waits for the schedule instead of re-admitting immediately");
        }
        // Actual drain (at least one admitted page of entities, with work left) re-runs immediately, with no clock movement.
        await using (var drained = new Rig(["p1"]))
        {
            drained.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 2500 });
            drained.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { PlanningBacklog = 400 });
            drained.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete));
            await drained.StartAsync();
            await drained.ParkedAsync(AutoStatus.Idle);
            drained.Clock.Advance(SafetyPoll);
            await drained.ParkedAsync(AutoStatus.Waiting);
            drained.Clock.Advance(Settings.ChangeQuietWindow);
            var at = drained.Clock.GetUtcNow();
            // Run 2 reports 400 rows against the previous 2500 (a full page drained), so run 3 starts with no
            // timer advance; run 3 reports an empty queue, which stops the drain.
            await Until(() => drained.Run.Runs("p1") == 3, "a full-page planning drain re-runs immediately");
            await drained.ParkedAsync(AutoStatus.Idle);
            Require(drained.Clock.GetUtcNow() == at && drained.State("p1").RetryAt is null,
                "drain progress re-runs without a timer advance and stops when the queue empties");
        }
        Pass("PlanningBacklog_DrainSignal");
    }

    private static async Task Burst_OneRunAsync()
    {
        await using var rig = await Rig.StartedAsync();
        for (var i = 0; i < 50; i++) rig.WatchA.Signal();
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "a burst of 50 signals gives one run");
        Pass("Burst_OneRun");
    }

    private static async Task SteadyStream_RunsAtMaxDelayAsync()
    {
        await using var rig = await Rig.StartedAsync();
        var step = TimeSpan.FromMilliseconds(500);
        var elapsed = TimeSpan.Zero;
        rig.WatchA.Signal();
        while (rig.Run.Runs("p1") == 1)
        {
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.WatchA.Signal();
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Clock.Advance(step);
            elapsed += step;
            await rig.SettledAsync();
        }
        Require(elapsed == Settings.ChangeMaxDelay, "a steady stream runs at ChangeMaxDelay after the first signal");
        Pass("SteadyStream_RunsAtMaxDelay");
    }

    private static async Task SignalDuringRun_ExactlyOneMoreAsync()
    {
        await using var rig = await Rig.StartedAsync();
        rig.Run.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.WatchA.Signal();
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await Until(() => rig.Run.Runs("p1") == 2, "the second run starts");
        rig.WatchB.Signal();
        rig.Run.Block.SetResult();
        rig.Run.Block = null;
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 3, "a signal during a run (with no wait outstanding) gives exactly one more run");
        Pass("SignalDuringRun_ExactlyOneMore");
    }

    private static async Task PausedOrOff_NoRunsUntilKickAsync()
    {
        await using (var rig = new Rig(["p1"]) { Paused = true })
        {
            rig.Auto.Start();
            await Until(() => rig.Status("p1") == AutoStatus.Paused, "a paused pair shows Paused");
            rig.Clock.Advance(SafetyPoll * 3);
            Require(rig.Run.Entered.IsEmpty, "a paused pair never runs");
            rig.Paused = false;
            rig.Auto.Kick(null);
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Diagnostic("resume-before-quiet-advance");
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            rig.Diagnostic("resume-after-quiet-advance");
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(rig.Run.Runs("p1") == 1, "Kick after resume gives one run");
        }
        await using (var off = new Rig(["p1"], enabled: false))
        {
            off.Auto.Start();
            await Until(() => off.Status("p1") == AutoStatus.Off, "a pair turned off shows Off");
            off.Clock.Advance(SafetyPoll * 3);
            Require(off.Run.Entered.IsEmpty, "a pair turned off never runs");
        }
        Pass("PausedOrOff_NoRunsUntilKick");
    }

    private static async Task DurableResumeWithoutKickAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "autosync-resume-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(RigPair("p1", true));
        store.MarkFirstSyncDone("p1");
        store.SetPairPaused("p1", true);
        var clock = new ManualClock();
        var run = new FakeSyncRun();
        var auto = new AutoSync(store, run, new FakeChangeWatchSource(), Settings, _ => false, () => SafetyPoll, clock);
        auto.Start();
        await Until(() => auto.States.Any(s => s.Status == AutoStatus.Paused) && clock.ActiveTimers == 1,
            "durably paused pair parks with a safety timer");
        store.SetPairPaused("p1", false); // direct CLI/store change, deliberately no Kick
        clock.Advance(SafetyPoll);
        await Until(() => auto.States.Any(s => s.Status == AutoStatus.Waiting) && clock.ActiveTimers == 1,
            "safety poll observes durable resume without a Kick");
        clock.Advance(Settings.ChangeQuietWindow);
        await Until(() => run.Runs("p1") == 1, "resumed pair runs within one safety poll");
        await auto.StopAsync();
        Pass("DurableResumeWithoutKick");
    }

    private static void RunFailure_ExcludesDeferredAttempts()
    {
        foreach (var priorFailure in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            store.SavePair(RigPair("p1", true));
            var now = new ManualClock().GetUtcNow();
            if (priorFailure)
            {
                FailureRetryOwner.RecordRunFailure(store, "p1", new IOException("earlier failure"), now,
                    StorageFailureClassifier.IsStateDamage);
                store.CloseNotice(FailureRetryOwner.RunNoticeId("p1"), "resolved");
            }
            FailureRetryOwner.RecordDeferredRetry(store, "p1", 1, FailureRetryOwner.Failed(5, now));
            Require(FailureRetryOwner.RunFailureState(store, "p1") is null,
                "deferred state is not a real failure episode");
            var failure = FailureRetryOwner.RecordRunFailure(store, "p1", new IOException("new failure"), now,
                StorageFailureClassifier.IsStateDamage);
            Require(failure == FailureRetryOwner.Failed(0, now) &&
                FailureRetryOwner.RunFailureState(store, "p1") == failure &&
                FailureRetryOwner.DeferredRetryState(store, "p1") is null,
                "deferred-to-failure and failure-to-deferred-to-failure start at the first shared tier");
        }
        Pass("RunFailure_ExcludesDeferredAttempts");
    }

    private static async Task Failed_RetryBacksOffAndResetsAsync()
    {
        var failed = FakeSyncRun.Report("p1", RunOutcomes.Failed, 0, Failure(Outcome.Failed));
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(failed);
        rig.Run.Then(failed);
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        var first = rig.State("p1").RetryAt - rig.Clock.GetUtcNow();
        var firstTier = FailureRetryOwner.Failed(0, rig.Clock.GetUtcNow()).RetryAfter - rig.Clock.GetUtcNow();
        Require(first == firstTier, "a failed run retries after the shared first tier");
        rig.Clock.Advance(firstTier);
        await rig.ParkedAsync(AutoStatus.Waiting);
        var second = rig.State("p1").RetryAt - rig.Clock.GetUtcNow();
        var measuredDebt = FailureRetryOwner.RunFailureState(rig._store, "p1");
        Console.WriteLine($"RETRY MEASUREMENT first={first}; second={second}; ownerAttempts={measuredDebt?.Attempts}; ownerDue={measuredDebt?.RetryAfter}; runs={rig.Run.Runs("p1")}");
        Require(second == FailureRetryOwner.Failed(1, rig.Clock.GetUtcNow()).RetryAfter - rig.Clock.GetUtcNow(), "the retry delay doubles");
        rig.Clock.Advance(second!.Value);
        await rig.ParkedAsync(AutoStatus.Idle);
        rig.Run.Then(failed);
        rig.WatchA.Signal();
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Waiting);
        Require(rig.State("p1").RetryAt - rig.Clock.GetUtcNow() == firstTier,
            "a complete run resets the backoff");
        Pass("Failed_RetryBacksOffAndResets");
    }

    // A deferred-only run is not a failure: it waits for its earliest retained item deadline, never AutoRetryDelay.
    private static async Task Deferred_BacklogWaitsForEarliestAsync()
    {
        var tuned = Settings with { MinSafetyPoll = TimeSpan.FromSeconds(2) };
        await using var rig = new Rig(["p1"], settings: tuned);
        var due = rig.Clock.GetUtcNow() + TimeSpan.FromMinutes(4);
        rig._store.SaveSyncRetry("p1", new SyncRetry("stuck.txt", "sync", 1, "Content proof is unavailable", due));
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial) with { Deferred = 140 });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        Require(rig.State("p1").RetryAt == due,
            "a deferred-only backlog waits for the earliest item retry, not the ten-second failure tier");
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        await rig.SettledAsync();
        Require(rig.Run.Runs("p1") == 1, "no wake happens before the earliest item retry");
        Pass("Deferred_BacklogWaitsForEarliest");
    }

    // No progress advances the shared exponential tier; a real shrink resets it to the item deadline.
    private static async Task Deferred_NoProgressBacksOffAsync()
    {
        var tuned = Settings with { MinSafetyPoll = TimeSpan.FromSeconds(2) };
        await using var rig = new Rig(["p1"], settings: tuned);
        var start = rig.Clock.GetUtcNow();
        rig._store.SaveSyncRetry("p1", new SyncRetry("stuck.txt", "sync", 1, "Content proof is unavailable", start));
        var partial = FakeSyncRun.Report("p1", RunOutcomes.Partial);
        rig.Run.Then(partial with { Deferred = 140 });
        rig.Run.Then(partial with { Deferred = 140 });
        rig.Run.Then(partial with { Deferred = 139 });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        // The first report has no previous count, so it opens the shared tier: the due item retry is below the
        // safety floor, and the shared first tier is later than that floor.
        var first = FailureRetryOwner.Failed(0, rig.Clock.GetUtcNow()).RetryAfter - rig.Clock.GetUtcNow();
        Require(rig.State("p1").RetryAt - rig.Clock.GetUtcNow() == first &&
            FailureRetryOwner.DeferredRetryState(rig._store, "p1") is { Attempts: 1 },
            "the first non-shrinking deferred report opens the shared tier and its counter");
        rig.Clock.Advance(first);
        await Until(() => rig.Run.Runs("p1") == 2 && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() &&
            rig.Clock.ActiveTimers == 1, "the non-shrinking backlog retries at its tier");
        var second = rig.State("p1").RetryAt - rig.Clock.GetUtcNow();
        Require(second == TimeSpan.FromSeconds(20) && FailureRetryOwner.DeferredRetryState(rig._store, "p1") is { Attempts: 2 },
            "a report that made no progress advances the shared exponential tier");
        rig.Clock.Advance(second!.Value);
        await Until(() => rig.Run.Runs("p1") == 3 && rig.State("p1").RetryAt is not null &&
            rig.Clock.ActiveTimers == 1, "the shrinking backlog runs and re-arms");
        Require(FailureRetryOwner.DeferredRetryState(rig._store, "p1") is null &&
            rig.State("p1").RetryAt - rig.Clock.GetUtcNow() == tuned.MinSafetyPoll,
            "a shrinking backlog resets the backoff and returns to the item deadline");
        Pass("Deferred_NoProgressBacksOff");
    }

    private static async Task Deferred_FreshSignalsInterruptBackoffAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            await using var rig = new Rig(["p1"]);
            var due = rig.Clock.GetUtcNow() + TimeSpan.FromSeconds(FailureRetryOwner.MaximumRetrySeconds);
            rig._store.SaveSyncRetry("p1", new SyncRetry("sleeping.txt", "sync", 6, "Content proof is unavailable", due));
            FailureRetryOwner.RecordDeferredRetry(rig._store, "p1", 1,
                FailureRetryOwner.Failed(5, rig.Clock.GetUtcNow()));
            var partial = FakeSyncRun.Report("p1", RunOutcomes.Partial) with { Deferred = 1 };
            rig.Run.Then(partial);
            rig.Run.Then(partial);
            await rig.StartAsync();
            await Until(() => rig.Run.Runs("p1") == 1 && rig.State("p1").RetryAt is not null &&
                rig.WatchA.Parked && rig.WatchB.Parked && rig.Clock.ActiveTimers == 1, "high-tier deferred wait is armed");
            var deadline = rig.State("p1").RetryAt!.Value;
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await rig.ParkedAsync(AutoStatus.Waiting);
            Require(rig.Run.Runs("p1") == 1, "without a new signal the high-tier backoff holds");
            (side == Side.A ? rig.WatchA : rig.WatchB).Signal();
            await Until(() => rig.State("p1").RetryAt is null && rig.WatchA.Parked && rig.WatchB.Parked &&
                rig.Clock.ActiveTimers == 1, "fresh endpoint signal enters the existing debounce");
            var almostQuiet = Settings.ChangeQuietWindow - TimeSpan.FromMilliseconds(1);
            rig.Clock.Advance(almostQuiet);
            await rig.ParkedAsync(AutoStatus.Waiting);
            Require(rig.Run.Runs("p1") == 1, "fresh work waits for the quiet window");
            rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => rig.Run.Runs("p1") == 2 && rig.State("p1").RetryAt is not null &&
                rig.WatchA.Parked && rig.WatchB.Parked && rig.Clock.ActiveTimers == 1, "fresh work runs and re-arms deferred wait");
            Require(rig.Clock.GetUtcNow() < deadline && rig._store.SyncRetryAt("p1", "sleeping.txt")?.RetryAfter == due,
                "either endpoint wakes before the deferred deadline without advancing sleeping item retries");
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await rig.ParkedAsync(AutoStatus.Waiting);
            Require(rig.Run.Runs("p1") == 2, "the consumed signal cannot recreate a ten-second retry spin");
        }
        Pass("Deferred_FreshSignalsInterruptBackoff");
    }

    private static async Task Deferred_OuterFailureReplacesBackoffAsync()
    {
        var tuned = Settings with { MinSafetyPoll = TimeSpan.FromSeconds(2) };
        await using var rig = new Rig(["p1"], settings: tuned);
        FailureRetryOwner.RecordDeferredRetry(rig._store, "p1", 1,
            FailureRetryOwner.Failed(5, rig.Clock.GetUtcNow()));
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial) with { Deferred = 1 });
        await rig.StartAsync();
        await Until(() => rig.Run.Runs("p1") == 1 && rig.State("p1").RetryAt is not null &&
            rig.WatchA.Parked && rig.WatchB.Parked && rig.Clock.ActiveTimers == 1, "high-tier wait before outer failure");
        var deferredDeadline = rig.State("p1").RetryAt!.Value;
        var expected = FailureRetryOwner.Failed(0, rig.Clock.GetUtcNow());
        rig.ThrowEligibility = true;
        // A watch fault returns to the outer eligibility check; its failure must install a real cooldown.
        rig.WatchA.Fail(new IOException("one-shot watch fault"));
        await Until(() => FailureRetryOwner.RunFailureState(rig._store, "p1") == expected &&
            rig.WatchA.Disposed && rig.WatchB.Disposed && rig.Clock.ActiveTimers == 1,
            "outer eligibility failure records a first-tier real failure and enters containment delay");
        rig.Clock.Advance(tuned.MinSafetyPoll);
        await Until(() => rig.State("p1").RetryAt == expected.RetryAfter && rig.Clock.ActiveTimers == 1,
            "real failure replaces the deferred deadline");
        Require(expected.RetryAfter < deferredDeadline, "the old high deferred tier is not inherited");
        rig.WatchA.Signal();
        rig.WatchB.Signal();
        rig.Clock.Advance(expected.RetryAfter - rig.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        await rig.ParkedAsync(AutoStatus.Waiting);
        Require(rig.Run.Runs("p1") == 1 && rig.Watches.Created == 2,
            "signals from retired watches cannot bypass the real-failure cooldown or re-arm early");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => rig.Watches.Created == 4 && rig.WatchA.Parked && rig.WatchB.Parked &&
            rig.Clock.ActiveTimers == 1, "real-failure deadline re-arms watches through startup debounce");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "outer recovery produces exactly one retry run");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "consumed startup signals cannot produce a duplicate recovery run");
        Pass("Deferred_OuterFailureReplacesBackoff");
    }

    private static async Task RefusedOrAccess_HeldUntilKickAsync()
    {
        foreach (var (error, reason) in new (Exception, NoticeReason)[]
                 {
                     (new SyncRunRefusedException("refused for the check"), AutoSyncReasons.Refused),
                     (new SyncRunRefusedException(PairAdmissionReasons.SameRoot), PairAdmissionReasons.SameRoot),
                     (new EndpointAccessException("revoked"), AutoSyncReasons.AccountAccess)
                 })
        {
            await using var rig = new Rig(["p1"]);
            rig.Run.Then(error);
            await rig.StartAsync();
            await Until(() => rig.Status("p1") == AutoStatus.Held, "the pair is held");
            Require(rig.State("p1").Reason?.Key == reason.Key, "held with reason " + reason.Key);
            await rig.ParkedAsync(AutoStatus.Held);
            // One poll must retry the incomplete run without a new watch signal or another clock advance.
            rig.Clock.Advance(SafetyPoll);
            if (error is SyncRunRefusedException or EndpointAccessException)
            {
                await Until(() => rig.Run.Runs("p1") == 2, "ordinary refusal is re-evaluated on the safety poll");
                await rig.ParkedAsync(AutoStatus.Idle);
                continue;
            }
            rig.WatchA.Signal();
            Require(rig.Run.Runs("p1") == 1, "an account-access hold is not retried and ignores signals");
            rig.Auto.Kick("p1");
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(rig.Run.Runs("p1") == 2, "Kick releases the hold with one run");
        }
        Pass("RefusedOrAccess_HeldUntilKick");
    }

    // Two saved pairs on one local folder are both held from startup with the admission reason and never run.
    private static async Task ClashingLayout_HeldAtStartupAsync()
    {
        await using var rig = new Rig(["p1", "p2"]);
        rig.SavePair(RigPair("p2", true) with { A = RigPair("p1", true).A with { RootId = "a-p2" } });
        rig.Auto.Start();
        await Until(() => rig.Status("p1") == AutoStatus.Held && rig.Status("p2") == AutoStatus.Held,
            "both pairs on one folder are held at startup");
        Require(rig.State("p1").Reason == PairAdmissionReasons.SameRoot &&
            rig.State("p2").Reason == PairAdmissionReasons.SameRoot, "the hold reason is the admission refusal");
        rig.Clock.Advance(SafetyPoll * 3);
        Require(rig.Run.Entered.IsEmpty && !rig.Watches.All.Any(), "a held layout never watches or runs");
        Pass("ClashingLayout_HeldAtStartup");
    }

    private static async Task SafetyPoll_RunsAndRebuildsAsync()
    {
        await using var rig = await Rig.StartedAsync();
        var old = rig.WatchA;
        rig.Clock.Advance(SafetyPoll);
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "the safety poll runs with no signals");
        Require(old.Disposed && rig.Watches.Created == 4, "the safety poll rebuilds the watches");
        Require(rig.Run.MonitorOrder.SequenceEqual(new[]
            { "monitor:p1", "incremental:p1", "monitor:p1", "incremental:p1" }),
            "startup and signal-free safety poll sample reconciliation before incremental preparation");
        Pass("SafetyPoll_RunsAndRebuilds");
    }

    private static async Task Stop_CancelsCleanlyAsync()
    {
        var rig = await Rig.StartedAsync();
        rig.Run.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.WatchA.Signal();
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await Until(() => rig.Run.Runs("p1") == 2, "a blocked run is in progress");
        await rig.Auto.StopAsync().WaitAsync(SettleBound);
        Require(rig.Watches.All.All(watch => watch.Disposed), "Stop cancels the run and disposes every watch");
        await rig.DisposeAsync();
        Pass("Stop_CancelsCleanly");
    }

    private static async Task TwoPairs_IndependentAsync()
    {
        await using var rig = await Rig.StartedAsync("p1", "p2");
        rig.Watches.Latest("a-p2").Signal();
        await rig.ParkedAsync(AutoStatus.Waiting, "p2");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle, "p2");
        Require(rig.Run.Runs("p2") == 2 && rig.Run.Runs("p1") == 1, "a signal on one pair runs only that pair");
        Pass("TwoPairs_Independent");
    }

    private static async Task PauseWhileQueued_NeverStartsAsync()
    {
        await using var rig = await Rig.StartedAsync("p1", "p2");
        rig.Run.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Watches.Latest("a-p1").Signal();
        rig.Watches.Latest("a-p2").Signal();
        await Until(() => rig.Status("p1") == AutoStatus.Waiting && rig.Status("p2") == AutoStatus.Waiting &&
                          rig.Clock.ActiveTimers == 2, "both pairs settle");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await Until(() => rig.Run.Entered.Count(id => id == "p2") == 2 && rig.Run.Entered.Count(id => id == "p1") == 2,
            "one pair runs and the other is queued behind the gate");
        var queued = rig.Run.Runs("p1") == 2 ? "p2" : "p1";
        rig.PausedPair = queued;
        rig.Auto.Kick(queued);
        await Until(() => rig.Status(queued) == AutoStatus.Paused, "the queued pair shows Paused");
        rig.Run.Block.SetResult();
        var other = queued == "p1" ? "p2" : "p1";
        await Until(() => rig.Status(other) == AutoStatus.Idle && rig.Run.Runs(other) == 2, "the running pair finishes");
        Require(rig.Run.Runs(queued) == 1, "a run queued behind the gate never starts once its pair is paused");
        Pass("PauseWhileQueued_NeverStarts");
    }

    private static async Task Partial_RetriesOnlyWithFailuresAsync()
    {
        await using (var rig = new Rig(["p1"]))
        {
            rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial, 0, Failure(Outcome.Uncertain)));
            await rig.StartAsync();
            await rig.ParkedAsync(AutoStatus.Waiting);
            Require(rig.State("p1").RetryAt is not null, "a partial run with an uncertain effect is retried");
        }
        await using (var held = new Rig(["p1"]))
        {
            held.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial, held: 1));
            await held.StartAsync();
            await held.ParkedAsync(AutoStatus.Idle);
            Require(held.State("p1").RetryAt is null, "a partial run whose only leftover is held rows is not retried");
        }
        await using (var deferred = new Rig(["p1"]))
        {
            var observed = 0;
            deferred.Auto.Changed += _ => throw new InvalidOperationException("scripted display failure");
            deferred.Auto.Changed += _ => Interlocked.Increment(ref observed);
            deferred.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial) with { Deferred = 1 });
            await deferred.StartAsync();
            await deferred.ParkedAsync(AutoStatus.Waiting);
            Require(deferred.State("p1").RetryAt is not null && observed > 0,
                "deferred item schedules a later run despite a throwing status subscriber; other subscribers still run");
        }
        Pass("Partial_RetriesOnlyWithFailures");
    }

    // continuous-sync.md section 2 (Retry) and section 7 case 12: temporary held intents are a completed run with
    // held rows, not a failure, deferred work or pending deletion evidence. Such a report waits for signals or the
    // safety poll instead of scheduling a retry.
    private static async Task HeldOnlyComplete_WaitsNormalIntervalAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete, held: 3) with { PendingVerification = 3 });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Idle);
        var state = rig.State("p1");
        var report = state.LastReport!;
        Require(state.RetryAt is null, "a hold-only complete report schedules no retry");
        Require(state.NextRunUtc - rig.Clock.GetUtcNow() == SafetyPoll,
            "the held-only pair is parked on the normal safety interval");
        Require(report.Outcome == RunOutcomes.Complete && report.Held == 3 && report.PendingVerification == 3 &&
                report.FailureCount == 0 && report.Failures.Count == 0 && report.Deferred == 0 &&
                report.DeletionEvidencePending.Count == 0,
            "the held-only report is complete with held/pending counts and no failure, deferred or evidence counts");
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.SettledAsync();
        Require(rig.Run.Runs("p1") == 1, "a hold-only complete report does not run again inside the quiet window");
        rig.Clock.Advance(SafetyPoll - Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "the held-only pair next runs at the normal safety poll, not on a retry");
        Pass("HeldOnlyComplete_WaitsNormalInterval");
    }

    // A held/pending count must never mask outstanding work: a real failed result or pending deletion evidence still
    // schedules the ordinary retry, so separating temporary holds cannot swallow failures.
    private static async Task HeldWithRealWork_StillRetriesAsync()
    {
        await using (var failed = new Rig(["p1"]))
        {
            failed.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete, 3, Failure(Outcome.Failed))
                with { PendingVerification = 3 });
            await failed.StartAsync();
            await failed.ParkedAsync(AutoStatus.Waiting);
            Require(failed.State("p1").RetryAt - failed.Clock.GetUtcNow() == Settings.AutoRetryDelay,
                "a failed result still retries even when the report also carries held/pending rows");
            failed.Clock.Advance(Settings.AutoRetryDelay);
            await failed.ParkedAsync(AutoStatus.Idle);
            Require(failed.Run.Runs("p1") == 2, "the held report with a real failure retries at AutoRetryDelay");
        }
        await using (var evidence = new Rig(["p1"]))
        {
            evidence.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with
            {
                DeletionEvidencePending = ["pending.txt"]
            });
            await evidence.StartAsync();
            await evidence.ParkedAsync(AutoStatus.Waiting);
            Require(evidence.State("p1").RetryAt is not null, "pending deletion evidence still schedules its retry");
        }
        Pass("HeldWithRealWork_StillRetries");
    }

    private static async Task RetryAt_NotBypassedBySignalsAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Failed, 0, Failure(Outcome.Failed)));
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        for (var i = 0; i < 5; i++) rig.WatchA.Signal();
        rig.Clock.Advance(Settings.AutoRetryDelay / 2);
        await rig.SettledAsync();
        Require(rig.Run.Runs("p1") == 1, "signals do not bring a retry forward");
        rig.Clock.Advance(Settings.AutoRetryDelay / 2);
        await Until(() => rig.Run.Runs("p1") == 2, "the retry runs at RetryAt");
        Pass("RetryAt_NotBypassedBySignals");
    }

    private static async Task WatchFault_RebuildsAndHoldsAsync()
    {
        await using var rig = await Rig.StartedAsync();
        rig.WatchA.Fail(new IOException("watcher died"));
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2 && rig.Watches.Created == 4, "a dead watch is rebuilt with a catch-up run");
        rig.Watches.WatchError = new DirectoryNotFoundException("root gone");
        rig.Clock.Advance(SafetyPoll);
        // Superseded by degraded polling; actual SyncRun still validates the root before effects.
        await Until(() => rig.Run.Runs("p1") == 3 && rig.Clock.ActiveTimers == 1, "unwatchable root permits a poll");
        Require(rig._store.OpenNotices("p1").Count(n => n.ReasonKey == "autosync-watch-unavailable") == 1,
            "watch-only outage is visible");
        rig.Watches.WatchError = null;
        rig.Clock.Advance(Settings.AutoRetryDelay);
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 4, "a recovered watch catches up");
        Require(!rig._store.OpenNotices("p1").Any(n => n.ReasonKey == "autosync-watch-unavailable"), "watch recovery closes only its notice");
        Pass("WatchFault_RebuildsAndHolds");
    }

    private static PairDef RigPair(string id, bool enabled) => new(id, id,
        new RootDef("a-" + id, SyncProviders.Local, null, "C:\\" + id, "id:a-" + id),
        new RootDef("b-" + id, SyncProviders.Dropbox, "acc", "/" + id, "id:b-" + id), [], [], enabled);

    private static ActionResult Failure(Outcome outcome) => new("x", outcome, null, null, null, "scripted");

    private static async Task Until(Func<bool> condition, string label)
    {
        var bound = Stopwatch.StartNew();
        while (!condition())
        {
            if (bound.Elapsed > SettleBound) throw new InvalidOperationException("Timed out: " + label);
            await Task.Yield();
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private static void Pass(string name) => Console.WriteLine("PASS " + name);

    /// <summary>A store with enabled, first-synced pairs, and AutoSync over fakes and a manual clock.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        internal readonly IStateStore _store;
        private readonly string[] _pairIds;

        public Rig(string[] pairIds, bool enabled = true, Func<string, string, IDisposable>? beginRunLog = null,
            V2Settings? settings = null, IStateStore? store = null)
        {
            _store = store ?? new InMemoryStateStore();
            _pairIds = pairIds;
            Run.FailureStore = _store;
            Run.FailureClock = Clock;
            foreach (var id in pairIds)
            {
                _store.SavePair(RigPair(id, enabled));
                _store.MarkFirstSyncDone(id);
            }
            Auto = new AutoSync(_store, Run, Watches, settings ?? Settings, pair => { if (ThrowEligibility) { ThrowEligibility = false; throw new IOException("eligibility fault"); } return Paused || pair.PairId == PausedPair; },
                () => PollInterval, Clock, beginRunLog);
        }

        /// <summary>Saves or replaces one pair in the rig's store.</summary>
        public void SavePair(PairDef pair) => _store.SavePair(pair);

        public ManualClock Clock { get; } = new();
        public FakeSyncRun Run { get; } = new();
        public FakeChangeWatchSource Watches { get; } = new();
        public AutoSync Auto { get; }
        public volatile bool Paused;
        public volatile bool ThrowEligibility;
        public volatile string? PausedPair;

        /// <summary>The edge's safety-poll interval as the rig reports it.</summary>
        public TimeSpan PollInterval { get; set; } = SafetyPoll;

        public FakeChangeWatch WatchA => Watches.Latest("a-p1");
        public FakeChangeWatch WatchB => Watches.Latest("b-p1");

        public PairAutoState State(string pairId) => Auto.States.Single(state => state.PairId == pairId);

        public AutoStatus? Status(string pairId) =>
            Auto.States.FirstOrDefault(state => state.PairId == pairId)?.Status;

        /// <summary>Starts AutoSync; the startup run settles once the watches' initial signals go quiet.</summary>
        public async Task StartAsync()
        {
            Auto.Start();
            await Until(() => _pairIds.All(id => Status(id) == AutoStatus.Waiting) &&
                              Clock.ActiveTimers == _pairIds.Length, "startup settles");
            Clock.Advance(Settings.ChangeQuietWindow);
        }

        public static async Task<Rig> StartedAsync(params string[] pairIds)
        {
            var rig = new Rig(pairIds.Length == 0 ? ["p1"] : pairIds);
            await rig.StartAsync();
            foreach (var id in rig._pairIds) await rig.ParkedAsync(AutoStatus.Idle, id);
            Require(rig._pairIds.All(id => rig.Run.Runs(id) == 1), "startup gives one run per pair");
            return rig;
        }

        /// <summary>Waits until the pair shows the status and every loop is parked on one timer and its watches.</summary>
        public async Task ParkedAsync(AutoStatus status, string pairId = "p1")
        {
            try
            {
                await Until(() => Status(pairId) == status && Clock.ActiveTimers == _pairIds.Length &&
                    (status is not (AutoStatus.Idle or AutoStatus.Waiting) || WatchesParked(pairId) || State(pairId).RetryAt is not null),
                    pairId + " parks as " + status);
            }
            catch
            {
                Diagnostic("park-timeout expected=" + status, pairId);
                throw;
            }
        }

        public void Diagnostic(string phase, string pairId = "p1")
        {
            Console.Error.WriteLine($"AUTO-PARK phase={phase} pair={pairId} state={Status(pairId)} runs={Run.Runs(pairId)} " + Clock.DiagnosticSnapshot());
            try
            {
                Console.Error.WriteLine("AUTO-PARK A=[" + Watches.Latest("a-" + pairId).DiagnosticSnapshot() +
                    "] B=[" + Watches.Latest("b-" + pairId).DiagnosticSnapshot() + "]");
            }
            catch (Exception error) { Console.Error.WriteLine("AUTO-PARK watches unavailable: " + error.Message); }
        }

        /// <summary>Waits until every loop holds a timer again after the clock moved.</summary>
        public Task SettledAsync() => Until(() => Clock.ActiveTimers == _pairIds.Length, "loops settle");

        private bool WatchesParked(string pairId)
        {
            var a = Watches.Latest("a-" + pairId);
            var b = Watches.Latest("b-" + pairId);
            return a.Parked && b.Parked;
        }

        public async ValueTask DisposeAsync()
        {
            await Auto.StopAsync();
            _store.Dispose();
        }
    }
}
