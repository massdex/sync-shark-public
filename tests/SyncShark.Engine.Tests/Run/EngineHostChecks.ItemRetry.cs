using System.Reflection;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;
using Monitor = Massdex.SyncShark.Engine.Run.Monitor;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>B-70 §14 host checks for the item retry wait: wake origin and admission (A), the drain (B4), Repair
/// registrations (C) and the gate (D), observed through a started host's file attempts and committed rows.</summary>
public static partial class EngineHostChecks
{
    private static readonly TimeSpan HookBound = TimeSpan.FromSeconds(30);

    private static async Task ItemRetryChecksAsync(string root)
    {
        // G0c first: the B-161 checks below take their unrelated work from this explicit local delivery.
        await LocalDeliveryUploadsAsync(root).ConfigureAwait(false);
        await ItemWaitHeldThroughEventsAsync(root).ConfigureAwait(false);
        await JoinedTurnHeldWithWaitAsync(root).ConfigureAwait(false);
        await ConsumedWaitReleasesSyncedFileAsync(root).ConfigureAwait(false);
        await StaleWaitHoldsNothingAsync(root).ConfigureAwait(false);
        await RowClearedDuringDispatchAsync(root).ConfigureAwait(false);
        await ConcurrentItemsAccountOnceAsync(root).ConfigureAwait(false);
        await StaleRefreshKeepsObservedWaitAsync(root).ConfigureAwait(false);
        await ItemWaitFiresWithoutEventAsync(root).ConfigureAwait(false);
        await ItemWaitAcrossRestartAsync(root).ConfigureAwait(false);
        await ItemWaitWallClockStepAsync(root, forward: false).ConfigureAwait(false);
        await ItemWaitWallClockStepAsync(root, forward: true).ConfigureAwait(false);
        await EventAfterDeadlineAttemptsOnceAsync(root).ConfigureAwait(false);
        await DueDispatchFailureAsync(root, fatal: true).ConfigureAwait(false);
        await DueDispatchFailureAsync(root, fatal: false).ConfigureAwait(false);
        await PausedDueDispatchKeepsRowAsync(root).ConfigureAwait(false);
        await RepairWithoutTurnReleasesRowAsync(root).ConfigureAwait(false);
        // N-161 before C2-C5: their settles after a Repair turn also cross its window, so N-161 owns that protection.
        await RepairTurnHoldsOnlyRetainedAsync(root, afterFinish: false).ConfigureAwait(false);
        await RepairTurnHoldsOnlyRetainedAsync(root, afterFinish: true).ConfigureAwait(false);
        await CancelledRepairHandsBackRetainedAsync(root).ConfigureAwait(false);
        await TransitionRefusalWakesRefusedPairAsync(root).ConfigureAwait(false);
        await PauseRetiresKeysAsync(root).ConfigureAwait(false);
        await RepairTurnStartsItemWaitAsync(root).ConfigureAwait(false);
        await FailedRepairTurnAdvancesOnceAsync(root).ConfigureAwait(false);
        await PausedRepairCompletionAccountsNothingAsync(root, providerWait: false).ConfigureAwait(false);
        await PausedRepairCompletionAccountsNothingAsync(root, providerWait: true).ConfigureAwait(false);
        await ResolveRunsBesideItemWaitAsync(root).ConfigureAwait(false);
        await RefreshInterleavingKeepsSignalAsync(root).ConfigureAwait(false);
        foreach (var memory in new[] { false, true })
        {
            foreach (var liveFirst in new[] { false, true })
            foreach (var outcome in new[] { LiveOutcome.Succeeds, LiveOutcome.Fails })
                await RepairCollisionFailsItemAsync(root, memory, liveFirst, outcome).ConfigureAwait(false);
            await RepairCollisionFailsItemAsync(root, memory, liveFirst: false, LiveOutcome.Stale).ConfigureAwait(false);
            await RepairCaptureRefusalFailsItemAsync(root, memory).ConfigureAwait(false);
            await CompletedRepairBesideNewDebtAsync(root, memory).ConfigureAwait(false);
            foreach (var shape in new[] { "own", "older", "concurrent", "settled" })
                await OrdinaryRepairItemWaitAsync(root, memory, shape).ConfigureAwait(false);
        }
        await RepairAdmissionRefusalPropagatesAsync(root).ConfigureAwait(false);
        await ExplicitRepairBesideRunningRepairAsync(root, afterFinish: false).ConfigureAwait(false);
        await ExplicitRepairBesideRunningRepairAsync(root, afterFinish: true).ConfigureAwait(false);
    }

    // A1: quiet pair, item-only row; events before the deadline attempt nothing, and the deadline attempts once.
    // N-161o (B-161, IR-1): the wait the host recorded holds only its own file, so unrelated work syncs before it.
    private static async Task ItemWaitHeldThroughEventsAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-events")).ConfigureAwait(false);
        await rig.SettleAsync().ConfigureAwait(false);
        Check.Require(rig.Attempts == 0, "A1 events before the item deadline attempt no file");
        var commits = new UploadCommits(rig, ItemRig.File);
        // Sampled before the delivery: its own catch-up wakes a dispatch that could send the file during the await.
        var mark = commits[ItemRig.File];
        var other = await rig.DeliverLocalAsync("other.txt").ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161o").ConfigureAwait(false);
        Check.Require(Uploaded(rig, "other.txt", other), "B-161 an item wait lets unrelated work sync before its deadline");
        Check.Require(commits[ItemRig.File] == mark, "B-161 an item wait holds its own file until its deadline");
        rig.MarkAttempts();
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("A1 the deadline attempts the retained file exactly once").ConfigureAwait(false);
    }

    // A7, D6: with no event at all, the timed wake alone dispatches the retained work once, including a row committed
    // by a later temporary turn on a running quiet host (its commit signal is what arms Monitor's timer).
    private static async Task ItemWaitFiresWithoutEventAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-quiet"), ItemRig.Far)
            .ConfigureAwait(false);
        var advanced = await rig.TurnAsync().ConfigureAwait(false);
        var row = rig.Store.ReadItemRetry(rig.PairId);
        Check.Require(advanced.NewlyAccepted && row is { Attempts: 2 } && advanced.RetryAt == row.Value.RetryAfter,
            "D6 a temporary turn on a running quiet host commits the next item state");
        var dispatched = rig.Dispatches;
        var remaining = row!.Value.RetryAfter - rig.Clock.GetUtcNow();
        await rig.ArmedAsync(remaining).ConfigureAwait(false);
        Check.Require(await DueWakesAsync(rig, "A7", () => rig.Clock.Advance(remaining)).ConfigureAwait(false) == 1,
            "A7 the due timer alone makes exactly one due wake");
        // Dispatch count first: AttemptedOnceAsync drains AutoSync, which also runs any opportunity queued meanwhile.
        await rig.DispatchedOnceAsync(dispatched, "A7 the due timer alone starts exactly one ordinary dispatch")
            .ConfigureAwait(false);
        await rig.AttemptedOnceAsync("A7, D6 the due timer alone attempts the retained file exactly once")
            .ConfigureAwait(false);
    }

    // A2: a restart with a future deadline attempts nothing at startup and once at the deadline.
    private static async Task ItemWaitAcrossRestartAsync(string root)
    {
        var folder = Path.Combine(root, "item-restart");
        var clock = ItemRig.NewClock();
        DropboxWire wire;
        await using (var before = await ItemRig.OpenAsync(folder, clock).ConfigureAwait(false))
        {
            await before.BaselineAsync().ConfigureAwait(false);
            await before.TurnAsync().ConfigureAwait(false);
            wire = before.Rig.Wire;
        }
        await using var rig = await ItemRig.OpenAsync(folder, clock, wire).ConfigureAwait(false);
        await rig.StartAsync().ConfigureAwait(false);
        await UntilAsync(() => rig.Opportunities > 0).ConfigureAwait(false);
        await rig.SettleAsync().ConfigureAwait(false);
        Check.Require(rig.Attempts == 0 && rig.Dispatches == 0,
            "A2 the restarted host attempts nothing at startup while the stored deadline is in the future");
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("A2 the restarted host attempts the retained file once at the deadline")
            .ConfigureAwait(false);
    }

    // A3 (backward) and A8 (forward): a wall-clock step after Monitor first saw the row changes nothing; the attempt
    // is exactly once at the monotonic deadline, and an event before it is held.
    private static async Task ItemWaitWallClockStepAsync(string root, bool forward)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, forward ? "item-wall-forward" :
            "item-wall-backward")).ConfigureAwait(false);
        await rig.ArmedAsync(rig.FirstWait).ConfigureAwait(false);
        rig.Clock.StepWallClock(forward ? TimeSpan.FromHours(1) : -TimeSpan.FromHours(1));
        if (forward)
        {
            await rig.SettleAsync().ConfigureAwait(false);
            Check.Require(rig.Attempts == 0,
                "A8 a forward wall-clock jump past the stored instant does not admit an event before the monotonic deadline");
        }
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync(forward ? "A8 the retained file is attempted once at the monotonic deadline" :
            "A3 a backward wall-clock step does not delay the attempt past the monotonic deadline").ConfigureAwait(false);
    }

    // A5: an event after the monotonic deadline, handled before the timer, is the due wake: one ordinary dispatch
    // and one file attempt in total. The deadline passes and the event arrives while the pump is held, so the same
    // pass sees the event first.
    private static async Task EventAfterDeadlineAttemptsOnceAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-late-event")).ConfigureAwait(false);
        await rig.ArmedAsync(rig.FirstWait).ConfigureAwait(false);
        var dispatched = rig.Dispatches;
        var wakes = await DueWakesAsync(rig, "A5", () =>
        {
            // The due timer only signals the held pump; the event is queued before that pass handles opportunities.
            rig.Clock.Advance(rig.FirstWait);
            rig.Monitor.Wake(rig.PairId);
        }).ConfigureAwait(false);
        Check.Require(wakes == 1, "A5 an event after the deadline and the due timer make one due wake in total");
        // Dispatch count first: AttemptedOnceAsync drains AutoSync, which also runs any opportunity queued meanwhile.
        await rig.DispatchedOnceAsync(dispatched,
            "A5 an event after the deadline and the due timer start one ordinary dispatch in total").ConfigureAwait(false);
        await rig.AttemptedOnceAsync("A5 the retained file is attempted once in total").ConfigureAwait(false);
    }

    // A5, A7: counts the due wakes from a held Monitor refresh to the settled pump. Each Monitor prerequisite wake
    // reads the pair's retry row once (RunPrerequisiteAsync) before it kicks AutoSync; nothing else in this item-only
    // interval makes that singular read. The baseline is taken while the pass is held in its store read, `due` runs
    // there, and the one dispatch is held until the pump has settled, so a repeated due wake is counted even when its
    // kick would coalesce into the held dispatch.
    private static async Task<int> DueWakesAsync(ItemRig rig, string check, Action due)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.AfterRefresh = () =>
        {
            rig.AfterRefresh = null;
            read.TrySetResult();
            rig.Hold(release.Task, check + " the held Monitor pass is released");
        };
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeSelect = () =>
        {
            rig.BeforeSelect = null;
            selecting.TrySetResult();
            rig.Hold(selected.Task, check + " the held dispatch is released");
        };
        int wakes;
        try
        {
            rig.Monitor.RearmPrerequisiteWait();
            await read.Task.WaitAsync(HookBound).ConfigureAwait(false);
            wakes = rig.Rig.Store.RetryReads;
            due();
        }
        finally { release.TrySetResult(); }
        try
        {
            await selecting.Task.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.IterateAsync().ConfigureAwait(false);
            await rig.IterateAsync().ConfigureAwait(false);
            return rig.Rig.Store.RetryReads - wakes;
        }
        finally { selected.TrySetResult(); }
    }

    // A6 (fatal) and A9 (EngineRequestException): an unexpected failure of the due dispatch stops the host with the
    // row unchanged, and the restarted host attempts it once at startup; a refused request keeps the host running
    // and the next event attempts the work once.
    private static async Task DueDispatchFailureAsync(string root, bool fatal)
    {
        var folder = Path.Combine(root, fatal ? "item-due-fatal" : "item-due-refused");
        var rig = await ItemRig.StartedAsync(folder).ConfigureAwait(false);
        var row = rig.Store.ReadItemRetry(rig.PairId);
        var thrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception failure = fatal ? new InvalidOperationException("synthetic due-dispatch failure") :
            new EngineRequestException(TextKey.ResolveStale);
        rig.BeforeSelect = () =>
        {
            rig.BeforeSelect = null;
            thrown.TrySetResult();
            throw failure;
        };
        Exception? checkFailure = null;
        try
        {
            await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
            await thrown.Task.WaitAsync(HookBound).ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) == row && rig.Attempts == 0,
                "A6, A9 a due dispatch that ends before any turn leaves the row unchanged");
            if (!fatal)
            {
                rig.Host.WakeAfterOperation(rig.PairId);
                await rig.AttemptedOnceAsync("A9 the next event dispatches the retained work once").ConfigureAwait(false);
                Check.Require(rig.Host.Status().Status is not (AutoStatus.Held or AutoStatus.Off),
                    "A9 a refused request is not fatal: the host keeps running");
                return;
            }
            await UntilAsync(() => rig.Host.Status().Status == AutoStatus.Held).ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) == row,
                "A6 an unexpected failure stops the host and leaves the row unchanged");
        }
        catch (Exception error)
        {
            checkFailure = error;
            throw;
        }
        finally
        {
            // A failed check is the reported failure; a cleanup error it caused must not replace it.
            try { await rig.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) when (checkFailure is not null) { }
            catch (AggregateException error) when (fatal &&
                error.InnerExceptions.All(inner => ReferenceEquals(inner, failure))) { }
        }
        await using var restarted = await ItemRig.OpenAsync(folder, rig.Clock, rig.Rig.Wire).ConfigureAwait(false);
        await restarted.StartAsync().ConfigureAwait(false);
        await restarted.AttemptedOnceAsync("A6 after restart the past-due row is attempted once at startup")
            .ConfigureAwait(false);
    }

    // B4: a pause during the due dispatch leaves the admitted row unchanged. The lane is held before its admission
    // check while Pause withdraws admission, and Pause's cancellation is held back (as in BUG-132's scenario), so the
    // refused lane and the pass return normally with the token uncancelled: the path that cleared the row.
    private static async Task PausedDueDispatchKeepsRowAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-paused-drain")).ConfigureAwait(false);
        var row = rig.Store.ReadPrerequisiteRetry(rig.PairId);
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeBegin = () =>
        {
            rig.BeforeBegin = null;
            selecting.TrySetResult();
            rig.Hold(go.Task, "B4 the held lane is released");
        };
        var dispatched = rig.Dispatches;
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await selecting.Task.WaitAsync(HookBound).ConfigureAwait(false);
        var drain = new DeferredSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task pausing;
        SynchronizationContext.SetSynchronizationContext(drain);
        try { pausing = rig.Host.PauseAsync(rig.PairId, default); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            go.TrySetResult();
            // The dispatch reads folder progress right after its drain decision.
            await UntilAsync(() => rig.Dispatches > dispatched, "B4 the paused dispatch returns before cancellation")
                .ConfigureAwait(false);
            var after = rig.Store.ReadPrerequisiteRetry(rig.PairId);
            Check.Require(rig.Host.IsPaused(rig.PairId) && after == row,
                $"B4 a pause that cancels the due work mid-run leaves the item row unchanged (paused {rig.Host.IsPaused(rig.PairId)}, before {row}, after {after}, item {rig.Store.ReadItemRetry(rig.PairId)}, attempts {rig.Attempts})");
        }
        finally
        {
            go.TrySetResult();
            drain.Release();
        }
        await pausing.WaitAsync(HookBound).ConfigureAwait(false);
    }

    // C1: a Repair with no temporary turn clears a pre-Repair item row, and its success Kick dispatches at once. A first
    // Repair syncs the retained file and its events settle; the row is then written over the synced file, so the second
    // Repair makes no effect and has no local change to catch up, and only its success Kick can dispatch after it.
    private static async Task RepairWithoutTurnReleasesRowAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-no-turn"), turn: false)
            .ConfigureAwait(false);
        rig.Owe();
        rig.Monitor.Wake(rig.PairId);
        await UntilAsync(() => !rig.Store.Obligated(rig.PairId) && rig.Attempts > 0,
            "C1 a first Repair syncs the retained file").ConfigureAwait(false);
        await rig.SettleAsync().ConfigureAwait(false);
        await rig.TurnAsync().ConfigureAwait(false);
        await rig.IterateAsync().ConfigureAwait(false);
        // A later opportunity starts only after the settle's last one ends; once AutoSync is idle again its dispatch
        // (the armed row holds only the turn's file) has been counted, so nothing in flight can dispatch later.
        var seen = rig.Opportunities;
        rig.Host.WakeAfterOperation(rig.PairId);
        await UntilAsync(() => rig.Opportunities > seen && rig.AutoSyncIdle(),
            "C1 the held opportunity's decision has completed").ConfigureAwait(false);
        var dispatched = rig.Dispatches;
        rig.Owe();
        rig.Monitor.Wake(rig.PairId);
        await UntilAsync(() => !rig.Store.Obligated(rig.PairId) && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null &&
            rig.Dispatches > dispatched, "C1 the Repair clears the row and its success Kick dispatches")
            .ConfigureAwait(false);
        Check.Require(rig.Store.ReadItemRetry(rig.PairId) is null,
            "C1 a Repair with no temporary turn clears the row and its success Kick dispatches at once");
    }

    // C2, C3: a Repair's own turn (no provider wait) beside a root notice lets FinishRepair commit; completion writes
    // one item row with attempts 1, the A01 first wait and no notice, and the success Kick is held until it.
    private static async Task RepairTurnStartsItemWaitAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-turn"), turn: false)
            .ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        rig.Owe(notice: true);
        await using (var repair = rig.HoldRepair())
        {
            await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.TurnAsync(prerequisite: true).ConfigureAwait(false);
        }
        await UntilAsync(() => rig.Store.ReadItemRetry(rig.PairId) is not null,
            "C2 the successful Repair's completion writes its own turn's item row").ConfigureAwait(false);
        var now = rig.Clock.GetUtcNow();
        Check.Require(!rig.Store.Obligated(rig.PairId) && RetryNotices(rig.Store, rig.PairId) == 0 &&
            rig.Store.ReadItemRetry(rig.PairId) == new FailureRetryOwner.State(1, now + rig.FirstWait),
            "C2 FinishRepair commits; completion writes one item-only row, attempts 1, the A01 first wait, no notice");
        await UntilAsync(() => RunLogsContain(rig, 0, nameof(TextKey.WaitItemRetry)),
            "C2 the item wait is printed to the pair's log as WaitItemRetry").ConfigureAwait(false);
        await rig.SettleAsync().ConfigureAwait(false);
        Check.Require(rig.Attempts == 0, "C3 the post-Repair Kick is held: no immediate resend");
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("C3 the retained file is attempted once at the item deadline").ConfigureAwait(false);
    }

    // C4: a Repair that fails after its own turn with a provider wait R advances once, adds the notice, and its
    // deadline is the receipt plus R exactly.
    private static async Task FailedRepairTurnAdvancesOnceAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-failed"), turn: false)
            .ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        var wait = TimeSpan.FromSeconds(20);
        var receipt = rig.Clock.GetUtcNow();
        var started = rig.Clock.GetTimestamp();
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        // Ordinary dispatch runs beside Repair by design; held before it selects work, it cannot take the retained
        // file from the held Repair, so the only accounted failure is that Repair's own.
        var accounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeSelect = () => rig.Hold(accounted.Task, "C4 ordinary dispatch waits for the Repair's accounting");
        try
        {
            rig.Owe();
            await using (var repair = rig.HoldRepair(new IOException("synthetic Repair failure after its own turn")))
            {
                await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
                await rig.TurnAsync(wait, started, prerequisite: true).ConfigureAwait(false);
            }
            await UntilAsync(() => rig.Store.ReadPrerequisiteRetry(rig.PairId) is not null,
                "C4 the failed Repair is accounted").ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) == new FailureRetryOwner.State(1, receipt + wait) &&
                RetryNotices(rig.Store, rig.PairId) == 1,
                "C4 the failed Repair's own turn advances once to receipt + R exactly and adds the notice");
        }
        finally
        {
            rig.BeforeSelect = null;
            accounted.TrySetResult();
        }
    }

    // C5: a pause between FinishRepair and completion accounts nothing. On resume the retained file is attempted once
    // at resume when there was no provider wait; with one, the account floor sends no request before R.
    private static async Task PausedRepairCompletionAccountsNothingAsync(string root, bool providerWait)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, providerWait ? "item-paused-repair-wait" :
            "item-paused-repair"), turn: false).ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        var wait = TimeSpan.FromSeconds(20);
        var started = rig.Clock.GetTimestamp();
        var account = (rig.Rig.Pair.Roots!.Dropbox.AccountId, (string?)null, (string?)null);
        rig.Owe();
        try
        {
            await using (var repair = rig.HoldRepair(window: true))
            {
                await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
                await rig.TurnAsync(providerWait ? wait : null, providerWait ? started : null, prerequisite: true)
                    .ConfigureAwait(false);
                // The 429 that carried R installed the account floor at its receipt.
                if (providerWait) FailureRetryOwner.InstallProviderConstraint(account, started, wait, rig.Clock);
                repair.Proceed();
                await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
                var pausing = rig.Host.PauseAsync(rig.PairId, default);
                await UntilAsync(() => rig.Host.IsPaused(rig.PairId)).ConfigureAwait(false);
                repair.Proceed();
                await pausing.ConfigureAwait(false);
            }
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) is null && rig.Attempts == 0,
                "C5 a paused completion writes no item row");
            var generation = rig.Store.ReadPairExecution(rig.PairId)!.Value.Generation;
            await UntilAsync(() => !rig.Host.ItemRetryHolds(rig.PairId, generation, out _),
                "C5 a paused completion leaves no item wait holding the pair").ConfigureAwait(false);
            var calls = rig.Calls;
            await rig.Host.ResumeAsync(rig.PairId, default).ConfigureAwait(false);
            if (providerWait)
            {
                await UntilAsync(() => rig.Clock.NextDueIn == wait).ConfigureAwait(false);
                Check.Require(rig.Calls == calls, "C5 on resume the account floor sends no request before R");
                rig.Clock.Advance(wait);
            }
            await rig.AttemptedOnceAsync("C5 on resume the retained file is attempted once, with no row")
                .ConfigureAwait(false);
        }
        finally
        {
            // The floor is process-wide; let it expire on this clock so no later check inherits it.
            if (providerWait)
            {
                if (rig.Clock.GetElapsedTime(started) < wait) rig.Clock.Advance(wait);
                FailureRetryOwner.RemainingProviderConstraint((account.Item1, "", ""), rig.Clock);
            }
        }
    }

    // N-161 (B-161, IR-1), replacing C6: a Repair's own unaccounted temporary turn holds only its retained file. Unrelated
    // live work syncs beside it, before FinishRepair (the obligation still standing) and in the window after it; after
    // completion its item wait still holds only that file, which is attempted once at the item deadline.
    private static async Task RepairTurnHoldsOnlyRetainedAsync(string root, bool afterFinish)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root,
            "item-repair-window" + (afterFinish ? "-after-finish" : "")), turn: false).ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        var what = $"(after FinishRepair {afterFinish})";
        var commits = new UploadCommits(rig, ItemRig.File);
        rig.Owe();
        await using (var repair = rig.HoldRepair(window: true))
        {
            await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.TurnAsync(prerequisite: true).ConfigureAwait(false);
            if (afterFinish)
            {
                repair.Proceed();
                await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
            }
            // Sampled before both deliveries: each one's catch-up wakes a dispatch that could send the file meanwhile.
            var mark = commits[ItemRig.File];
            var other = await rig.DeliverLocalAsync("other.txt").ConfigureAwait(false);
            // New content, so a send of the retained file would be a real upload; delivered, so its wake is settled
            // below rather than whenever the journal reads it (B-175 scan).
            await rig.DeliverRetainedAsync().ConfigureAwait(false);
            await rig.SettledIdleAsync("N-161 " + what).ConfigureAwait(false);
            Check.Require(Uploaded(rig, "other.txt", other),
                "B-161 unrelated live work syncs beside the Repair's unaccounted temporary turn " + what);
            Check.Require(commits[ItemRig.File] == mark,
                "B-161 the Repair's retained file is not sent before its accounting " + what);
            if (!afterFinish)
            {
                repair.Proceed();
                await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
            }
            repair.Proceed();
        }
        await UntilAsync(() => rig.Store.ReadItemRetry(rig.PairId) is not null && !rig.RepairRegistered(),
            "N-161 the Repair's completion writes its own turn's item row " + what).ConfigureAwait(false);
        // Sampled before the delivery: its own catch-up wakes a dispatch that could send the file during the await.
        var mark2 = commits[ItemRig.File];
        var other2 = await rig.DeliverLocalAsync("other2.txt").ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161 after completion " + what).ConfigureAwait(false);
        Check.Require(Uploaded(rig, "other2.txt", other2),
            "B-161 after a successful Repair its item wait lets unrelated work sync " + what);
        Check.Require(commits[ItemRig.File] == mark2,
            "B-161 after a successful Repair the retained file waits for its deadline " + what);
        rig.MarkAttempts();
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("N-161 the retained file is attempted once at the item deadline " + what)
            .ConfigureAwait(false);
    }

    // N-162 (B-162, IR-1): a Repair with its own retained turn, stopped by its caller just after FinishRepair commits,
    // accounts nothing; its completion hands the retained file back to AutoSync, which sends it. The kill point follows
    // the awaited Repair, whose completion runs before it returns, and AutoSync going idle.
    private static async Task CancelledRepairHandsBackRetainedAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-cancelled"), turn: false)
            .ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        var commits = new UploadCommits(rig, ItemRig.File);
        var mark = commits[ItemRig.File];
        using var stop = new CancellationTokenSource();
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeFinish = () =>
        {
            rig.BeforeFinish = null;
            finishing.TrySetResult();
            rig.Hold(turned.Task, "N-162 the held Repair reaches FinishRepair");
        };
        // B-175: the rewrite is committed work, and the dispatch its wake queued has ended, while the Repair is still
        // registered, so that dispatch skips the retained key (IR-1) and no journal wake is left for later: only the
        // completion's hand-back can send the file.
        var delivered = false;
        var drained = false;
        rig.AfterFinish = () =>
        {
            rig.AfterFinish = null;
            try
            {
                // Changed after the Repair's scans, so only the retained turn's later work carries it.
                delivered = rig.DeliverRetainedAsync().Wait(HookBound);
                drained = delivered && SpinWait.SpinUntil(rig.AutoSyncIdle, HookBound);
            }
            finally { stop.Cancel(); }
        };
        rig.Owe();
        var repairing = Task.Run(() => rig.Host.Operations.RepairAsync(rig.PairId, stop.Token));
        try
        {
            await finishing.Task.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.TurnAsync(prerequisite: true).ConfigureAwait(false);
        }
        finally { turned.TrySetResult(); }
        try { await repairing.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or PrerequisiteStoppedException) { }
        Check.Require(delivered && drained,
            "setup: N-162 the rewrite is committed as work and its dispatch has ended before the cancel");
        Check.Require(!rig.RepairRegistered() && rig.Store.ReadItemRetry(rig.PairId) is null,
            "B-162 the cancelled completion accounts nothing");
        await UntilAsync(rig.AutoSyncIdle, "N-162 the handed-back opportunity has ended").ConfigureAwait(false);
        Check.Require(commits[ItemRig.File] > mark,
            "B-162 a Repair cancelled after FinishRepair hands its retained file back to AutoSync");
    }

    // N-160 (B-160, IR-1): an opportunity that another pair's configuration transition refused is woken when that
    // transition ends. The changing pair is never saved and overlaps nothing of the rig's pair, so it alone is affected;
    // the mark is read inside the transition, after the refused opportunity has ended.
    private static async Task TransitionRefusalWakesRefusedPairAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-transition-refusal"), turn: false)
            .ConfigureAwait(false);
        var otherRoot = Directory.CreateDirectory(Path.Combine(root, "item-transition-other")).FullName;
        var other = rig.Rig.Pair with
        {
            PairId = "b160-other", Name = "B160 Other", LocalRoot = otherRoot, DropboxRoot = "/B160Other", Roots = null
        };
        var mark = int.MaxValue;
        var chosen = false;
        try
        {
            await rig.Host.ChangeGenerationAsync(other, async (_, _, _, _) =>
            {
                chosen = true;
                var seen = rig.Opportunities;
                rig.Host.WakeAfterOperation(rig.PairId);
                await UntilAsync(() => rig.Opportunities > seen && rig.AutoSyncIdle(),
                    "N-160 the refused opportunity has ended").ConfigureAwait(false);
                mark = rig.Opportunities;
                return true;
            }, default).ConfigureAwait(false);
        }
        // Installing the never-saved changing pair may fail after the transition has already chosen its pairs.
        catch (Exception) when (chosen) { }
        await UntilAsync(rig.AutoSyncIdle, "N-160 the transition's wakes have been handled").ConfigureAwait(false);
        Check.Require(rig.Opportunities > mark,
            "B-160 the pair refused during another pair's transition is woken when it ends");
    }

    // N-161z (B-161, IR-1): a pause retires the pair's recorded keys after its drain. A second turn on the waiting file
    // starts the pause from inside its own report (the AfterTemporary hook, under the host lock, before the turn records
    // its key), so the pause drains that very dispatch while it still records. After resume the pending row holds the
    // whole pair until its deadline, as after a restart; at the deadline the paused pair's work runs.
    private static async Task PauseRetiresKeysAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-paused-keys"), turn: false)
            .ConfigureAwait(false);
        await rig.TurnAsync().ConfigureAwait(false);
        Task? pausing = null;
        rig.AfterTemporary = () =>
        {
            rig.AfterTemporary = null;
            pausing = rig.Host.PauseAsync(rig.PairId, default);
        };
        // The pause may refuse the drained turn's completion; its report is committed before the hook runs.
        try { await rig.TurnAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or LiveAdmissionStaleException) { }
        finally { rig.AfterTemporary = null; }
        await (pausing ?? throw new InvalidOperationException("N-161z the turn did not start the pause"))
            .WaitAsync(HookBound).ConfigureAwait(false);
        await rig.Host.ResumeAsync(rig.PairId, default).ConfigureAwait(false);
        var commits = new UploadCommits(rig, ItemRig.File);
        // Sampled before the delivery: its own catch-up wakes a dispatch that could send the file during the await.
        var mark = commits[ItemRig.File];
        var other = await rig.DeliverLocalAsync("other.txt").ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161z").ConfigureAwait(false);
        Check.Require(!Uploaded(rig, "other.txt", other) && commits[ItemRig.File] == mark,
            "B-161 after a pause a pending wait holds the pair until its deadline, as after a restart");
        // The pair's current wait: the second turn advanced the first one's.
        var row = rig.Store.ReadItemRetry(rig.PairId)!.Value;
        await rig.FireAsync(row.RetryAfter - rig.Clock.GetUtcNow()).ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161z at the deadline").ConfigureAwait(false);
        Check.Require(Uploaded(rig, "other.txt", other), "B-161 the paused pair's work runs at the wait's deadline");
    }

    // G0c (B-167): in this rig an explicit local catch-up is how a local write becomes work, including the later ones
    // that run with the journal it started; each upload carries the exact content the delivery wrote.
    private static async Task LocalDeliveryUploadsAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-delivery"), turn: false)
            .ConfigureAwait(false);
        foreach (var name in new[] { "other.txt", "other.txt", "other2.txt" })
        {
            var content = await rig.DeliverLocalAsync(name).ConfigureAwait(false);
            await rig.SettledIdleAsync("G0c " + name).ConfigureAwait(false);
            Check.Require(Uploaded(rig, name, content), "G0c a delivered local write uploads with its content: " + name);
        }
    }

    // N-161o, joined arm (B-161, IR-1): a second file's turn in the registration that armed the wait joins that wait, so
    // its file is held with it until the deadline.
    private static async Task JoinedTurnHeldWithWaitAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-joined"), turn: false)
            .ConfigureAwait(false);
        const string Joined = "b.txt";
        System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, Joined), "joined");
        await TemporaryTurnAsync(rig.Host, rig.PairId, ItemRig.File, null, null, null, then: [Joined]).ConfigureAwait(false);
        var commits = new UploadCommits(rig, Joined);
        // Sampled before the delivery: its own catch-up wakes a dispatch that could send the file during the await.
        var mark = commits[Joined];
        await rig.DeliverLocalAsync(Joined).ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161o joined").ConfigureAwait(false);
        Check.Require(commits[Joined] == mark, "B-161 a joined turn's file is held with the pair's current wait");
    }

    // N-161x (B-161, IR-1): a due wait hands its keys to the dispatch it admits. a.txt and item.txt share one wait
    // (two turns in one registration). The due dispatch is admitted, which consumes both keys; then, in its BeforeSelect
    // and so before it selects anything, a stand-in turn from another registration refuses item.txt again and advances
    // the row the dispatch was admitted with. Only after that refusal does the dispatch select and upload a.txt's
    // original content. A later a.txt edit is not held by the wait item.txt's refusal started. The keys are consumed at
    // admission, not by a.txt's success, so the refusal need not follow the success.
    private static async Task ConsumedWaitReleasesSyncedFileAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-consumed"), turn: false)
            .ConfigureAwait(false);
        const string Synced = "a.txt";
        System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, Synced), "a 0");
        await TemporaryTurnAsync(rig.Host, rig.PairId, ItemRig.File, null, null, null, then: [Synced]).ConfigureAwait(false);
        var admitted = rig.Store.ReadItemRetry(rig.PairId);
        var armed = 1;
        rig.BeforeSelect = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1) return;
            rig.Hold(TemporaryTurnAsync(rig.Host, rig.PairId, ItemRig.File, null, null, null),
                "N-161x the stand-in refusal of item.txt commits");
        };
        try
        {
            await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
            await UntilAsync(() => Volatile.Read(ref armed) == 0 && rig.AutoSyncIdle(),
                "N-161x the due dispatch has ended").ConfigureAwait(false);
        }
        finally { rig.BeforeSelect = null; }
        Check.Require(rig.Store.ReadItemRetry(rig.PairId) is { Attempts: 2 } advanced && advanced != admitted,
            "setup: item.txt's refusal in the due dispatch advanced the wait it was admitted with");
        Check.Require(Uploaded(rig, Synced, "a 0"),
            "B-161 a due wait hands back its keys: a.txt's original content reaches Dropbox in the due dispatch");
        var edited = await rig.DeliverLocalAsync(Synced).ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161x").ConfigureAwait(false);
        Check.Require(Uploaded(rig, Synced, edited),
            "B-161 a due wait hands back its keys: a file that synced at the deadline is not held by the next wait");
    }

    // N-161v (B-161, IR-1): an ordinary Repair syncs the waiting file and its FinishRepair clears the row outside the
    // host; the recorded wait then holds nothing. The clock is not advanced, so the old state would still be pending.
    private static async Task StaleWaitHoldsNothingAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-stale-wait"), turn: false)
            .ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        await rig.TurnAsync().ConfigureAwait(false);
        rig.WriteRetained();
        await rig.Host.Operations.RepairAsync(rig.PairId, default).ConfigureAwait(false);
        Check.Require(rig.Store.ReadItemRetry(rig.PairId) is null && !rig.Store.Obligated(rig.PairId),
            "setup: the ordinary Repair synced the waiting file and cleared its wait");
        var commits = new UploadCommits(rig, ItemRig.File);
        // Sampled before the delivery: its own catch-up wakes a dispatch that could send the file during the await.
        var mark = commits[ItemRig.File];
        await rig.DeliverLocalAsync(ItemRig.File).ConfigureAwait(false);
        await rig.SettledIdleAsync("N-161v").ConfigureAwait(false);
        Check.Require(commits[ItemRig.File] > mark && rig.Store.ReadItemRetry(rig.PairId) is null,
            "B-161 a wait the store no longer holds holds nothing");
    }

    // N-161g (B-161, IR-1): the dispatch is admitted while the wait is pending; its exact row is then cleared outside the
    // host (AutoSync's own CompleteItemRetry call) before the dispatch selects, so the waiting file is attempted in that
    // same dispatch. Exactly one opportunity runs after the clear: a later one, admitted with no row, would retire the
    // entry itself (ReleaseItemWait) and send the file, hiding a hold that ignored the clear.
    private static async Task RowClearedDuringDispatchAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-cleared-mid-dispatch"), turn: false)
            .ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        await rig.TurnAsync().ConfigureAwait(false);
        // Delivered, so its wake is settled below: no journal wake can add an opportunity after the clear (B-175 scan).
        await rig.DeliverRetainedAsync().ConfigureAwait(false);
        var generation = rig.Store.ReadPairExecution(rig.PairId)!.Value.Generation;
        var row = rig.Store.ReadItemRetry(rig.PairId)!.Value;
        var commits = new UploadCommits(rig, ItemRig.File);
        // No opportunity is left queued before the one that sees the clear; the pending wait holds the file through these.
        await rig.SettledIdleAsync("N-161g setup").ConfigureAwait(false);
        var mark = commits[ItemRig.File];
        Check.Require(mark == 0, "setup: the pending wait holds item.txt before the clear");
        // The settled dispatches confirmed the wait against its row; each released that confirmation as it retired.
        Check.Require(rig.CheckedDispatch() is null,
            "B-161 a retired dispatch's row confirmation is cleared, so the wait keeps no retired registration");
        var armed = 1;
        rig.BeforeSelect = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1) rig.Store.CompleteItemRetry(rig.PairId, generation, row);
        };
        try
        {
            rig.Host.WakeAfterOperation(rig.PairId);
            await UntilAsync(() => Volatile.Read(ref armed) == 0 && rig.AutoSyncIdle(),
                "N-161g the one dispatch that saw the row clear has ended").ConfigureAwait(false);
        }
        finally { rig.BeforeSelect = null; }
        Check.Require(commits[ItemRig.File] > mark && rig.Store.ReadItemRetry(rig.PairId) is null,
            "B-161 a wait cleared during a dispatch no longer holds its file in that dispatch");
    }

    // D3: a user Retry of another issue on a waiting pair runs at once; the item wait is untouched.
    private static async Task ResolveRunsBesideItemWaitAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-resolve")).ConfigureAwait(false);
        var row = rig.Store.ReadItemRetry(rig.PairId);
        const string Other = "other.txt";
        rig.Rig.Wire.SeedFile(Other, "user retry", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        rig.Store.Commit(rig.PairId, StateChange.Empty with
            { Raise = [(IssueKind.Failure, TextKey.FailChanged, Other, new IssueSubject([], null))] });
        var issue = rig.Store.Issues(rig.PairId).Single(value => value.Path == Other);
        await rig.Host.Operations.ResolveAsync(rig.PairId, issue.Id, ResolutionChoice.Retry, null, null, false, null,
            default).ConfigureAwait(false);
        Check.Require(rig.Store.IssueById(rig.PairId, issue.Id) is null &&
            File.Exists(Path.Combine(rig.Rig.Pair.LocalRoot, Other)) &&
            rig.Store.ReadItemRetry(rig.PairId) == row && rig.Attempts == 0,
            "D3 a user Retry on another issue of a waiting pair runs at once and leaves the item wait alone");
    }

    // D7: a temporary commit and its signal while Monitor is inside RefreshPairs, after its store read, still arm the
    // new deadline with no further event.
    private static async Task RefreshInterleavingKeepsSignalAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-refresh-race"), ItemRig.Far)
            .ConfigureAwait(false);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.AfterRefresh = () =>
        {
            rig.AfterRefresh = null;
            read.TrySetResult();
            rig.Hold(release.Task, "D7 the held Monitor pass is released");
        };
        rig.Monitor.RearmPrerequisiteWait();
        await read.Task.WaitAsync(HookBound).ConfigureAwait(false);
        try { await rig.TurnAsync().ConfigureAwait(false); }
        finally { release.TrySetResult(); }
        var row = rig.Store.ReadItemRetry(rig.PairId)!.Value;
        await rig.FireAsync(row.RetryAfter - rig.Clock.GetUtcNow()).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("D7 a signal during RefreshPairs arms the new deadline with no further event")
            .ConfigureAwait(false);
    }

    // HR2-01: two distinct items of one registration report temporary failures together; the item wait advances
    // once and the second joins it. Both attempts are reserved first. The first item's report is held just after its
    // store commit, before it publishes; the second item then reports on its own thread, and the hold is released
    // only once the second is seen either blocked on a lock (serialized: the only lock on that path the held first
    // report owns is the host gate) or committing its own advance while the first is unpublished (unserialized).
    private static async Task ConcurrentItemsAccountOnceAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-concurrent"), turn: false)
            .ConfigureAwait(false);
        const string Second = "second.txt";
        System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, Second), "second");
        await rig.SyncedAsync().ConfigureAwait(false);
        var now = rig.Clock.GetUtcNow();
        var watermark = rig.Store.ReadWorkWatermark();
        var (first, second) = await rig.Host.DispatchSessionAsync(rig.PairId, () => null, async (session, token) =>
        {
            var port = session.Context.ItemAttempts;
            var epoch = rig.Store.ReadSource(rig.PairId, Side.Local)!.Value.Epoch;
            async Task<IssueSubject> SubjectAsync(string path)
            {
                var id = (await session.Local.ReadOneAsync(path, token).ConfigureAwait(false))!.Id;
                return new IssueSubject([new IssueItem(Side.Local, path, id)], Side.Local);
            }
            (bool NewlyAccepted, DateTimeOffset? RetryAt) Report(Guid attemptId, string path, IssueSubject subject) =>
                port.ReportTemporary(attemptId, epoch, path, subject, TextKey.FailChanged, null,
                    ItemRetryDispatch.OrdinaryItem, null, null);
            var firstSubject = await SubjectAsync(ItemRig.File).ConfigureAwait(false);
            var secondSubject = await SubjectAsync(Second).ConfigureAwait(false);
            var reserved = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread? reporter = null;
            var commits = 0;
            rig.AfterTemporary = () =>
            {
                if (Interlocked.Increment(ref commits) != 1)
                {
                    secondCommitted.TrySetResult();
                    return;
                }
                committing.TrySetResult();
                rig.Hold(release.Task, "HR2-01 the held first report is released");
            };
            // The second attempt is reserved now and reports only when told to, on its own thread.
            var secondTask = port.ExecuteAsync(session.Context.Opportunity, null, secondSubject, async (attemptId, _) =>
            {
                reserved.TrySetResult(attemptId);
                await go.Task.ConfigureAwait(false);
                var done = new TaskCompletionSource<(bool NewlyAccepted, DateTimeOffset? RetryAt)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                reporter = new Thread(() =>
                {
                    try { done.TrySetResult(Report(attemptId, Second, secondSubject)); }
                    catch (Exception error) { done.TrySetException(error); }
                });
                reporter.Start();
                return await done.Task.ConfigureAwait(false);
            }, token);
            await reserved.Task.WaitAsync(HookBound, token).ConfigureAwait(false);
            var firstTask = port.ExecuteAsync(session.Context.Opportunity, null, firstSubject, (attemptId, _) =>
                Task.Run(() => Report(attemptId, ItemRig.File, firstSubject)), token);
            try
            {
                await committing.Task.WaitAsync(HookBound, token).ConfigureAwait(false);
                go.TrySetResult();
                await UntilAsync(() => secondCommitted.Task.IsCompleted || Volatile.Read(ref reporter) is { } thread &&
                    (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0).ConfigureAwait(false);
            }
            finally
            {
                go.TrySetResult();
                release.TrySetResult();
            }
            return (await firstTask.ConfigureAwait(false), await secondTask.ConfigureAwait(false));
        }, default).ConfigureAwait(false);
        var row = rig.Store.ReadPrerequisiteRetry(rig.PairId);
        Check.Require(row == new FailureRetryOwner.State(1, now + rig.FirstWait) &&
                first == (true, row!.Value.RetryAfter) && second == (true, row.Value.RetryAfter),
            "HR2-01 concurrent distinct items of one registration advance the item wait once; the second joins it");
        Check.Require(rig.Store.ReadWorkWatermark() == watermark + 2,
            "HR2-01 both concurrent items retain their work");
    }

    // HR2-02: a Monitor refresh that read before a temporary commit, released after admission observed the new
    // row and the wall clock jumped, keeps that observed monotonic deadline.
    private static async Task StaleRefreshKeepsObservedWaitAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-stale-refresh"), turn: false)
            .ConfigureAwait(false);
        await rig.SyncedAsync().ConfigureAwait(false);
        var generation = rig.Store.ReadPairExecution(rig.PairId)!.Value.Generation;
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.AfterRefresh = () =>
        {
            rig.AfterRefresh = null;
            read.TrySetResult();
            rig.Hold(release.Task, "HR2-02 the held Monitor pass is released");
        };
        rig.Monitor.RearmPrerequisiteWait();
        await read.Task.WaitAsync(HookBound).ConfigureAwait(false);
        try
        {
            await rig.TurnAsync().ConfigureAwait(false);
            // Pending through the window, and the row holds its ordinary upload until the deadline.
            rig.WriteRetained();
            Check.Require(rig.Host.ItemRetryHolds(rig.PairId, generation, out var admitted) &&
                    admitted == rig.Store.ReadItemRetry(rig.PairId),
                "HR2-02 admission observes the committed row before the stale refresh applies");
            rig.Clock.StepWallClock(TimeSpan.FromHours(1));
        }
        finally { release.TrySetResult(); }
        await rig.ArmedAsync(rig.FirstWait).ConfigureAwait(false);
        Check.Require(rig.Attempts == 0,
            "HR2-02 the stale refresh and the wall-clock jump leave the observed monotonic deadline in place");
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("HR2-02 the retained file is attempted once at the observed deadline")
            .ConfigureAwait(false);
    }

    // The B-70 §14 host rig: a Persistent host over the composed Repair rig, started so AutoSync and Monitor run on
    // one fake clock. The pair is composed before host start (RepairChecks.cs:4650), so start's recompose returns
    // early (EngineHost.RecomposeCurrentAsync) and no feed is started. The journal starts at the first local catch-up
    // (B-167, B-175): a host Repair's, a live upload's coverage capture or DeliverLocalAsync's. Until then a local write
    // alone is never acquired; after it the journal acquires a write when it reads it, so a check whose verdict needs the
    // write acquired before a barrier delivers it explicitly (DeliverLocalAsync, DeliverRetainedAsync). AutoSync reads
    // live refusal once at the start
    // of each opportunity and asks for folder-job progress once after each dispatch that returns; each dispatch it
    // starts opens one log. A file attempt is one upload request for the retained file.
    private sealed class ItemRig : IAsyncDisposable
    {
        internal const string File = "item.txt";
        // A provider wait that keeps a first row far from every deadline a check drives.
        internal static readonly TimeSpan Far = TimeSpan.FromHours(1);
        private int _opportunities;
        private int _dispatches;
        private int _attemptMark;
        private int _refreshes;
        private string? _barrierFailure;
        private int _version;

        private ItemRig(FakeTimeProvider clock)
        {
            Clock = clock;
            Monitor = new Monitor(clock);
        }

        internal FakeTimeProvider Clock { get; }
        internal Monitor Monitor { get; }
        internal RepairChecks.ComposedRig Rig { get; private set; } = null!;
        internal Action? BeforeSelect;
        internal Action? BeforeBegin;
        internal Action? AfterRefresh;
        internal Action? AfterTemporary;
        internal Action? BeforeFinish;
        internal Action? AfterFinish;
        internal Action<Exception>? FinishFailed;
        internal Action<StateChange>? BeforeCommit;
        internal Action<string>? BeforeCapture;
        internal Action? LaterEvidenceRefused;
        internal Action? AfterDispatch;
        // Every run log the host opened, in order.
        internal readonly List<string> Logs = [];

        internal bool AutoSyncIdle() => EngineHostChecks.AutoSyncIdle(Host, PairId);

        // The pair's prerequisite Repair is registered: it retires only after its completion has run.
        internal bool RepairRegistered()
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            lock (typeof(EngineHost).GetField("_gate", Private)!.GetValue(Host)!)
                return ((System.Collections.IDictionary)typeof(EngineHost).GetField("_prerequisites", Private)!
                    .GetValue(Host)!).Contains(PairId);
        }
        // The dispatch the pair's item wait last confirmed against its stored row, or null (IR-1).
        internal object? CheckedDispatch()
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            lock (typeof(EngineHost).GetField("_gate", Private)!.GetValue(Host)!)
            {
                var wait = ((System.Collections.IDictionary)typeof(EngineHost).GetField("_itemWaits", Private)!
                    .GetValue(Host)!)[PairId];
                return wait?.GetType().GetField("Checked", Private)!.GetValue(wait);
            }
        }
        internal string PairId => Rig.Pair.PairId;
        internal EngineHost Host => Rig.Host;
        internal IStateStore Store => Rig.Store;
        internal TimeSpan FirstWait => TimeSpan.FromSeconds(Host.Settings.Backoff.InitialBackoffSeconds);
        internal int Opportunities => Volatile.Read(ref _opportunities);
        internal int Dispatches => Volatile.Read(ref _dispatches);
        // Store reads of the retry rows; Monitor makes one per pass.
        internal int Refreshes => Volatile.Read(ref _refreshes);

        // A hook barrier that times out fails the check instead of silently releasing the held race.
        internal void Hold(Task released, string what)
        {
            if (released.Wait(HookBound)) return;
            Interlocked.CompareExchange(ref _barrierFailure, what, null);
            throw new TimeoutException(what);
        }
        internal int Calls
        {
            get { lock (Rig.Wire.Calls) return Rig.Wire.Calls.Count; }
        }
        internal int Attempts
        {
            get
            {
                lock (Rig.Wire.Calls) return Rig.Wire.Calls.Count(IsUpload) - Volatile.Read(ref _attemptMark);
            }
        }

        // Later attempts count from here; the Repair's own scan may already have synced the file.
        internal void MarkAttempts()
        {
            lock (Rig.Wire.Calls) Volatile.Write(ref _attemptMark, Rig.Wire.Calls.Count(IsUpload));
        }

        internal static FakeTimeProvider NewClock()
        {
            var clock = new FakeTimeProvider();
            // Real wall time, so the local file times this rig writes are never in the clock's future.
            clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
            return clock;
        }

        internal static async Task<ItemRig> OpenAsync(string folder, FakeTimeProvider clock, DropboxWire? wire = null,
            bool memory = false)
        {
            var rig = new ItemRig(clock);
            // A Repair needs its run log (Repair.RunAsync), as every production host opens one.
            rig.Rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: rig.Monitor,
                openLog: id =>
                {
                    var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, Settings.LogBufferCharacters);
                    lock (rig.Logs) rig.Logs.Add(log.Path);
                    return log;
                },
                mode: EngineHost.Mode.Persistent, wire: wire,
                decorate: store => new HookStore(memory ? InMemory(store) : store)
                {
                    OnReadLiveRefusal = () => Interlocked.Increment(ref rig._opportunities),
                    OnFolderJobProgress = () =>
                    {
                        Interlocked.Increment(ref rig._dispatches);
                        rig.AfterDispatch?.Invoke();
                    },
                    BeforeSelectWork = () => rig.BeforeSelect?.Invoke(),
                    BeforeBeginSelection = () => rig.BeforeBegin?.Invoke(),
                    AfterPrerequisiteRetries = () =>
                    {
                        Interlocked.Increment(ref rig._refreshes);
                        rig.AfterRefresh?.Invoke();
                    },
                    AfterTemporaryItem = () => rig.AfterTemporary?.Invoke(),
                    BeforeFinishRepair = () => rig.BeforeFinish?.Invoke(),
                    AfterFinishRepair = () => rig.AfterFinish?.Invoke(),
                    FinishFailed = error => rig.FinishFailed?.Invoke(error),
                    OnCommit = change => rig.BeforeCommit?.Invoke(change),
                    BeforeItemRetryForSubject = path => rig.BeforeCapture?.Invoke(path),
                    LaterEvidenceRefused = () => rig.LaterEvidenceRefused?.Invoke(),
                }).ConfigureAwait(false);
            return rig;
        }

        // The same rig over the in-memory store (RepairChecks.ConcurrentForgottenAsync's swap).
        private static IStateStore InMemory(IStateStore opened)
        {
            opened.Dispose();
            return new InMemoryStateStore();
        }

        // A completed first Repair, then a local file written after it that only a retained turn's work reaches,
        // optionally that turn's item row, then host start with Monitor idle.
        internal static async Task<ItemRig> StartedAsync(string folder, TimeSpan? providerWait = null, bool turn = true,
            bool memory = false)
        {
            var rig = await OpenAsync(folder, NewClock(), memory: memory).ConfigureAwait(false);
            try
            {
                await rig.BaselineAsync().ConfigureAwait(false);
                if (turn) await rig.TurnAsync(providerWait, providerWait is null ? null : rig.Clock.GetTimestamp())
                    .ConfigureAwait(false);
                await rig.StartAsync().ConfigureAwait(false);
                // The startup opportunity dispatches beside the row's recorded file (IR-1), or with no row once; either
                // way it has ended and been counted before a check marks anything.
                if (turn) await UntilAsync(() => rig.Opportunities > 0 && rig.AutoSyncIdle()).ConfigureAwait(false);
                else await UntilAsync(() => rig.Dispatches > 0).ConfigureAwait(false);
                await rig.IterateAsync().ConfigureAwait(false);
                await rig.IterateAsync().ConfigureAwait(false);
                return rig;
            }
            catch
            {
                await rig.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task BaselineAsync()
        {
            using var log = Rig.Log(Path.GetDirectoryName(Rig.Pair.LocalRoot)!, "baseline");
            Check.Require((await Rig.RunAsync(log, default).ConfigureAwait(false)).Completed &&
                !Store.Obligated(PairId), "the item-retry rig starts after a completed first Repair");
            WriteRetained();
        }

        // New content each time, so every write is a change the next sync must carry.
        internal void WriteRetained() => System.IO.File.WriteAllText(Path.Combine(Rig.Pair.LocalRoot, File),
            Retained(Interlocked.Increment(ref _version)));

        // B-167, B-175: this rig has no journal until its first local catch-up (EngineHost.WithVolumeStoppedAsync), and
        // every host Repair and every live upload's coverage capture runs one, so after that a write is acquired and
        // wakes the pair whenever the journal reads it. Sampling before the write and checking this call's own catch-up
        // makes the barrier hold whichever of the two acquires the edit, with its wake already queued; the returned
        // content is what an upload of it carries.
        internal Task<string> DeliverLocalAsync(string name) =>
            DeliverAsync(name, name + " " + Interlocked.Increment(ref _version));

        // The retained file's next content, delivered the same way.
        internal Task<string> DeliverRetainedAsync() => DeliverAsync(File, Retained(Interlocked.Increment(ref _version)));

        private async Task<string> DeliverAsync(string name, string content)
        {
            var before = Store.ReadWorkWatermark();
            System.IO.File.WriteAllText(Path.Combine(Rig.Pair.LocalRoot, name), content);
            var caught = await Host.CatchUpLocalAsync(PairId, Store.ReadPairExecution(PairId)!.Value.Generation, default)
                .ConfigureAwait(false);
            Check.Require(caught.ThroughWorkId > before, "setup: the edit to " + name + " is committed as work");
            return content;
        }
        private static string Retained(int version) => "retained " + version;

        // Before this the rig runs no journal or feed (the pair is composed before host start, RepairChecks.cs:4650, so
        // start's recompose returns early in EngineHost.RecomposeCurrentAsync), so the retained file's pre-start write is
        // never acquired; this upload's coverage capture is the rig's first local catch-up and starts the journal.
        // A temporary turn's retained work fired at its deadline, as A1 sends it, is the only way to its upload. Once
        // the upload is on Dropbox with no row and AutoSync is idle, later attempts count from here. A check that
        // needs a later upload writes the file again afterwards.
        internal async Task SyncedAsync()
        {
            await TurnAsync().ConfigureAwait(false);
            await FireAsync(FirstWait).ConfigureAwait(false);
            await UntilAsync(() => AutoSyncIdle() && Store.ReadPrerequisiteRetry(PairId) is null &&
                    OnDropbox(this, File, Retained(Volatile.Read(ref _version))),
                "the retained file's pre-start write is uploaded at its turn's deadline").ConfigureAwait(false);
            MarkAttempts();
        }

        internal Task StartAsync()
        {
            MarkAttempts();
            return Host.StartAsync(default);
        }

        internal Task<(bool NewlyAccepted, DateTimeOffset? RetryAt)> TurnAsync(TimeSpan? wait = null,
            long? started = null, bool prerequisite = false) =>
            TemporaryTurnAsync(Host, PairId, File, null, wait, started, prerequisite);

        // The pair owes a Repair (lost local interval), optionally beside an earlier failed attempt's root notice.
        internal void Owe(bool notice = false)
        {
            var generation = Store.ReadPairExecution(PairId)!.Value.Generation;
            Store.RequireRepairAfterLoss(PairId, generation);
            if (!notice) return;
            var local = Store.ReadSource(PairId, Side.Local)!.Value;
            var dropbox = Store.ReadSource(PairId, Side.Dropbox)!.Value;
            Store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, dropbox.Epoch, local.Revision,
                dropbox.Revision, null, new FailureRetryOwner.State(1, Clock.GetUtcNow() + Far), RetryNotice());
        }

        // Starts the owed prerequisite Repair through Monitor's event wake and holds it just before FinishRepair (after
        // its scans, where the retained file is written) and, with a window, again just after FinishRepair commits.
        internal HeldRepair HoldRepair(Exception? failure = null, bool window = false)
        {
            var held = new HeldRepair(this, failure, window);
            Monitor.Wake(PairId);
            return held;
        }

        internal sealed class HeldRepair : IAsyncDisposable
        {
            private readonly TaskCompletionSource _finishing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _window = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _turned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _proceeds;

            internal HeldRepair(ItemRig rig, Exception? failure, bool window)
            {
                rig.BeforeFinish = () =>
                {
                    rig.BeforeFinish = null;
                    rig.MarkAttempts();
                    _finishing.TrySetResult();
                    rig.Hold(_turned.Task, "the held Repair reaches FinishRepair");
                    if (failure is not null) throw failure;
                };
                // A FinishRepair failure ends the window wait with that error, not a timeout.
                rig.FinishFailed = window ? error => _window.TrySetException(error) : null;
                rig.AfterFinish = () =>
                {
                    rig.AfterFinish = null;
                    // Changed after the Repair's scans, so only the retained turn's later work carries it.
                    rig.WriteRetained();
                    if (!window) return;
                    _window.TrySetResult();
                    rig.Hold(_completed.Task, "the held Repair completes after FinishRepair");
                };
            }

            internal Task Finishing => _finishing.Task;
            internal Task Window => _window.Task;

            // The first call lets FinishRepair run; the second ends the window after it.
            internal void Proceed() => (Interlocked.Increment(ref _proceeds) == 1 ? _turned : _completed).TrySetResult();

            public ValueTask DisposeAsync()
            {
                _turned.TrySetResult();
                _completed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }

        // One Monitor pass after every earlier signal, with its tail done: a zero quiet deadline is published by the
        // pump itself, whose deadline release then signals one more pass; that pass's store read ends the tail.
        internal async Task IterateAsync()
        {
            var published = 0;
            var before = 0;
            using var deadline = Monitor.ScheduleQuietStatus(TimeSpan.Zero, () =>
            {
                before = Refreshes;
                Volatile.Write(ref published, 1);
            });
            await UntilAsync(() => Volatile.Read(ref published) == 1).ConfigureAwait(false);
            await UntilAsync(() => Refreshes > Volatile.Read(ref before)).ConfigureAwait(false);
        }

        // Every event before this call is fully handled by AutoSync: opportunities for one pair run one at a time, so
        // three rounds of event and started opportunity end with the one covering the first event finished.
        internal async Task SettleAsync()
        {
            for (var round = 0; round < 3; round++)
            {
                var seen = Opportunities;
                Host.WakeAfterOperation(PairId);
                await UntilAsync(() => Opportunities > seen).ConfigureAwait(false);
            }
        }

        // The kill-point barrier: settled, then no opportunity running or queued, so every dispatch the events caused
        // has ended and been counted.
        internal async Task SettledIdleAsync(string what)
        {
            await SettleAsync().ConfigureAwait(false);
            await UntilAsync(AutoSyncIdle, what + " the settled opportunities have ended").ConfigureAwait(false);
        }

        // Waits until Monitor's timer is armed for exactly the remaining wait, then fires it.
        internal async Task ArmedAsync(TimeSpan remaining) =>
            await UntilAsync(() => Clock.NextDueIn == remaining,
                $"Monitor's timer is armed for exactly the remaining item wait {remaining}").ConfigureAwait(false);

        internal async Task FireAsync(TimeSpan remaining)
        {
            await ArmedAsync(remaining).ConfigureAwait(false);
            Clock.Advance(remaining);
        }

        // The dispatch AutoSync started since the mark has returned, and a following settled Monitor pass started no
        // other; an AutoSync opportunity queued earlier but not yet run is not excluded by this helper.
        internal async Task DispatchedOnceAsync(int mark, string what)
        {
            await UntilAsync(() => Dispatches > mark).ConfigureAwait(false);
            await IterateAsync().ConfigureAwait(false);
            Check.Require(Dispatches == mark + 1, what);
        }

        internal async Task AttemptedOnceAsync(string what)
        {
            // Counted once the attempting dispatch has settled and nothing it queued is left (AutoSync idle), so a
            // second upload in the same work is seen.
            await UntilAsync(() => Attempts > 0 && Store.ReadPrerequisiteRetry(PairId) is null && AutoSyncIdle(), what)
                .ConfigureAwait(false);
            var attempts = Attempts;
            Check.Require(attempts == 1, $"{what} (attempts {attempts})");
        }

        public async ValueTask DisposeAsync()
        {
            BeforeSelect = BeforeBegin = AfterRefresh = BeforeFinish = AfterFinish = AfterTemporary = null;
            FinishFailed = null;
            BeforeCommit = null;
            BeforeCapture = null;
            try { if (Rig is not null) await Rig.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                Check.Require(Volatile.Read(ref _barrierFailure) is null,
                    "every held race was released by the check, not by a barrier timeout: " + _barrierFailure);
            }
        }
    }

    // How the held live upload ends once released: settled, refused by the provider, or refused as stale.
    private enum LiveOutcome { Succeeds, Fails, Stale }

    // B-131 (engine.md 369, 881): ordinary dispatch runs beside Repair. A Repair row that meets an item an ordinary
    // dispatch holds raises that item's FailChanged Failure and lets its siblings finish; it is not a failed
    // prerequisite. A successful repair-first collision repeats on the same item, so its Failure and work are shown
    // not to accumulate.
    private static async Task RepairCollisionFailsItemAsync(string root, bool memory, bool liveFirst, LiveOutcome outcome)
    {
        var folder = Path.Combine(root, "repair-collision-" + (memory ? "memory" : "store") +
            (liveFirst ? "-live-first-" : "-repair-first-") + outcome.ToString().ToLowerInvariant());
        await using var rig = await ItemRig.StartedAsync(folder, turn: false, memory: memory).ConfigureAwait(false);
        // The retained file's pre-start write is synced first, so the target's held upload stays the only live attempt.
        await rig.SyncedAsync().ConfigureAwait(false);
        var rounds = !liveFirst && outcome == LiveOutcome.Succeeds ? 2 : 1;
        for (var round = 1; round <= rounds; round++)
            await CollideAsync(rig, liveFirst, outcome, round,
                $"(memory {memory}, live first {liveFirst}, live {outcome}, round {round})").ConfigureAwait(false);
    }

    // One collision. The live upload of the target is held at its provider commit, after its Host reservation. The
    // sibling is a Dropbox-only file: live work refuses its Local write while the Repair is owed, so only the Repair
    // downloads it. With liveFirst the live upload settles before the Repair's item Failure commits. Fails: the
    // provider refuses the released upload and every resend of it, so its work stays visibly owned until the user's
    // Retry. Stale: the Local source epoch advances while the upload is held, so the provider takes the
    // upload but its settlement is refused as stale (ChangeTable.RequireCurrent): no binding is saved and the Repair's
    // Failure stays visible. Advancing the epoch over the item's retained work owes a Repair (LocalSkippedCoverage,
    // ChangeTable.AdvanceSource), which owns the work: it records the item Dropbox already has, with no second upload.
    private static async Task CollideAsync(ItemRig rig, bool liveFirst, LiveOutcome outcome, int round, string what)
    {
        const string target = "target.txt";
        var sibling = $"sibling{round}.txt";
        var content = "live " + what;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedRepair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        // B-174 (Stale): armed before the debt lands, completed once the stale check has read the state.
        TaskCompletionSource? staleChecked = null;
        rig.Rig.Transport.Respond = async (request, token) =>
        {
            if (!await CommitsUploadAsync(request, target, token).ConfigureAwait(false))
            {
                // Only an owed Repair a stray wake starts early sends a request here; its first one, the root read,
                // precedes every effect it has on what the stale check reads.
                if (Volatile.Read(ref staleChecked) is { } gate)
                    rig.Hold(gate.Task, "B-174 an owed Repair started before the stale check waits for it");
                return null;
            }
            if (Interlocked.Increment(ref commits) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(HookBound, token).ConfigureAwait(false);
            }
            return null;
        };
        try
        {
            // The live upload is the target's retained turn, fired at its deadline. The journal the rig's earlier
            // catch-ups started can acquire the write and start a dispatch that reserves the target before the turn
            // does (EngineHost.ReserveItem), so ordinary selection waits until the turn has recorded the target's wait;
            // that dispatch then skips the target (IR-1).
            rig.BeforeSelect = () => rig.Hold(turned.Task, "B-131 ordinary dispatch waits for the target's turn");
            System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, target), content);
            await TemporaryTurnAsync(rig.Host, rig.PairId, target, null, null, null).ConfigureAwait(false);
            rig.BeforeSelect = null;
            turned.TrySetResult();
            await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
            await entered.Task.WaitAsync(HookBound).ConfigureAwait(false);
            // The provider refuses the held upload when it is released (DropboxWire reads the refusal at handling).
            if (outcome == LiveOutcome.Fails) rig.Rig.Wire.NameWriteErrors[target] = "other";
            // Dispatches counted before the live dispatch returns; only that held dispatch can return while it holds.
            var live = rig.Dispatches;
            // Upload commits the live dispatch made before the Repair's check: its held one, plus with liveFirst any
            // resend of its refused upload inside that dispatch.
            var settled = 1;
            // One selection gate for the collision. A pair's ordinary dispatches run one at a time and each is counted
            // after its last selection, so a selection seen at count n belongs to the dispatch that next moves the count
            // past n. Until the Repair's check, a dispatch after the live one (the Repair's completion kick) is held.
            // Unarmed, every selection records its count; armed, only the dispatch still at the last recorded count
            // selects and every later one is held until the Retry is checked.
            var selections = new object();
            var armed = false;
            var lastUnarmed = -1;
            rig.BeforeSelect = () =>
            {
                while (true)
                {
                    Task held;
                    lock (selections)
                    {
                        var count = rig.Dispatches;
                        if (!checkedRepair.Task.IsCompleted && count > live) held = checkedRepair.Task;
                        else if (armed && !retried.Task.IsCompleted && count != lastUnarmed) held = retried.Task;
                        else
                        {
                            if (!armed) lastUnarmed = count;
                            return;
                        }
                    }
                    rig.Hold(held, "B-131 ordinary dispatch waits for the check");
                }
            };
            if (liveFirst)
            {
                rig.BeforeCommit = change =>
                {
                    if (!change.Raise.Any(raised => raised.Kind == IssueKind.Failure &&
                        raised.Cause == TextKey.FailChanged && raised.Path.EndsWith(target, StringComparison.Ordinal)))
                        return;
                    rig.BeforeCommit = null;
                    live = rig.Dispatches;
                    release.TrySetResult();
                    // The held dispatch settles its upload and returns before the Repair's item Failure commits.
                    if (!SpinWait.SpinUntil(() => rig.Dispatches > live, HookBound))
                        throw new TimeoutException("B-131 the live upload settles before the Repair's item Failure");
                    settled = Volatile.Read(ref commits);
                };
            }
            rig.Owe();
            rig.Rig.Wire.SeedFile(sibling, "sibling " + what);
            rig.Monitor.Wake(rig.PairId);
            await UntilAsync(() => !rig.Store.Obligated(rig.PairId) &&
                System.IO.File.Exists(Path.Combine(rig.Rig.Pair.LocalRoot, sibling)),
                "B-131 the Repair finishes its sibling beside the live item " + what).ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) is null &&
                RetryNotices(rig.Store, rig.PairId) == 0 && TargetFailures(rig, target) == 1 &&
                TargetFailures(rig, target, TextKey.FailChanged) == 1 && Volatile.Read(ref commits) == settled &&
                SiblingSynced(rig, sibling, "sibling " + what),
                $"B-131 a Repair that meets the live item raises only that item's FailChanged Failure, syncs its sibling and starts no upload of the item {what} (retry {rig.Store.ReadPrerequisiteRetry(rig.PairId)}, notices {RetryNotices(rig.Store, rig.PairId)}, item failures {TargetFailures(rig, target)}, upload commits {commits}, live commits {settled})");
            checkedRepair.TrySetResult();
            if (!liveFirst) live = rig.Dispatches;
            if (outcome == LiveOutcome.Stale)
            {
                // The collision Repair's registration is removed before its completion wakes Monitor for an event it
                // saw, and the journal its catch-ups restarted can wake the pair at any time, so once the debt below
                // lands an owed Repair may start on its own (RQ-179). The gate holds such a Repair at its first Dropbox
                // request until the stale check has read the state (B-174).
                await UntilAsync(() => !rig.RepairRegistered(), "B-131 the collision Repair's registration has retired " + what)
                    .ConfigureAwait(false);
                Volatile.Write(ref staleChecked, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var generation = rig.Store.ReadPairExecution(rig.PairId)!.Value.Generation;
                var source = rig.Store.ReadSource(rig.PairId, Side.Local)!.Value;
                rig.Store.AdvanceSource(rig.PairId, Side.Local, generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch + 1, source.Revision + 1, source.Provenance,
                    source.Cursor, source.Obligation);
            }
            release.TrySetResult();
            if (outcome == LiveOutcome.Stale)
            {
                // The live dispatch ends however its stale refusal is reported; the upload itself reached Dropbox.
                await UntilAsync(() => rig.AutoSyncIdle() && OnDropbox(rig, target, content),
                    "B-131 the stale live dispatch ends " + what).ConfigureAwait(false);
                var owed = rig.Store.ReadSource(rig.PairId, Side.Local)?.Obligation;
                Check.Require(rig.Store.SyncedByKey(rig.PairId, Side.Local, PathNames.Key(target)) is null &&
                    TargetFailures(rig, target) == 1 && TargetFailures(rig, target, TextKey.FailChanged) == 1 &&
                    RetryNotices(rig.Store, rig.PairId) == 0 && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null &&
                    owed == SourceObligation.LocalSkippedCoverage && rig.Store.Obligated(rig.PairId),
                    $"B-131 the held upload reached Dropbox but its stale settlement was refused: no binding, no notice, the item keeps its FailChanged Failure and the pair owes a Repair for its work {what} (item failures {TargetFailures(rig, target)}, obligation {owed})");
                staleChecked!.TrySetResult();
                // Any wake starts the owed Repair (RQ-179): this wake starts it, or joins the one a stray wake started and
                // the gate held. It records the item and the user's Retry closes any Failure left, neither sending the
                // upload again.
                rig.Monitor.Wake(rig.PairId);
                await UntilAsync(() => !rig.Store.Obligated(rig.PairId) && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null,
                    "B-131 the owed Repair after the stale refusal completes " + what).ConfigureAwait(false);
                Check.Require(Uploaded(rig, target, content) && Volatile.Read(ref commits) == 1,
                    $"B-131 the owed Repair records the item Dropbox already has, with no second upload {what} (upload commits {commits})");
                await rig.Host.Operations.ResolveAsync(rig.PairId, null, ResolutionChoice.Retry, null, null, true, null,
                    default).ConfigureAwait(false);
                Check.Require(TargetFailures(rig, target) == 0 && Uploaded(rig, target, content) &&
                    Volatile.Read(ref commits) == 1 && RetryNotices(rig.Store, rig.PairId) == 0,
                    $"B-131 the stale holder's item ends synced with no Failure and one upload {what} (item failures {TargetFailures(rig, target)}, upload commits {commits})");
                return;
            }
            else await UntilAsync(() => rig.Dispatches > live, "B-131 the live dispatch returns " + what).ConfigureAwait(false);
            if (outcome == LiveOutcome.Fails)
            {
                // The live item retries its refused upload as ordinary item work (each refused again, as the
                // provider's own DropboxRefused Failure); it stays one visible Failure, never a prerequisite retry.
                // Arming then draining proves every dispatch that selected unarmed has been counted, so every
                // automatic resend has finished and any later selection (the Repair's completion kick, whenever it
                // lands) is held until the Retry is checked. The wake lets a dispatch still at the last recorded count
                // finish if the one that recorded it ended uncounted.
                await rig.SettleAsync().ConfigureAwait(false);
                lock (selections) armed = true;
                rig.Host.WakeAfterOperation(rig.PairId);
                await UntilAsync(() => { lock (selections) return rig.Dispatches > lastUnarmed; },
                    "B-131 every dispatch that selected before the Retry gate has ended " + what).ConfigureAwait(false);
                var sent = Volatile.Read(ref commits);
                Check.Require(TargetFailures(rig, target) == 1 && sent >= settled && !Uploaded(rig, target, content) &&
                    rig.Store.ReadPrerequisiteRetry(rig.PairId) is null && RetryNotices(rig.Store, rig.PairId) == 0,
                    $"B-131 the unfinished live item keeps one visible Failure and no prerequisite retry {what} (item failures {TargetFailures(rig, target)}, upload commits {commits})");
                rig.Rig.Wire.NameWriteErrors.Remove(target);
                await rig.Host.Operations.ResolveAsync(rig.PairId, null, ResolutionChoice.Retry, null, null, true, null,
                    default).ConfigureAwait(false);
                Check.Require(TargetFailures(rig, target) == 0 && Uploaded(rig, target, content) &&
                    Volatile.Read(ref commits) == sent + 1 && RetryNotices(rig.Store, rig.PairId) == 0,
                    $"B-131 the user's Retry sends the refused upload once more and closes its Failure {what} (item failures {TargetFailures(rig, target)}, upload commits {commits})");
                return;
            }
            if (liveFirst)
            {
                // The live upload synced the item before the Repair's late FailChanged, which stays until Retry.
                Check.Require(Uploaded(rig, target, content) && TargetFailures(rig, target) == 1 &&
                    TargetFailures(rig, target, TextKey.FailChanged) == 1,
                    $"B-131 the live upload synced the item and only the Repair's late FailChanged remains {what} (item failures {TargetFailures(rig, target)})");
                await rig.Host.Operations.ResolveAsync(rig.PairId, null, ResolutionChoice.Retry, null, null, true, null,
                    default).ConfigureAwait(false);
                Check.Require(TargetFailures(rig, target) == 0 && Uploaded(rig, target, content) &&
                    Volatile.Read(ref commits) == 1,
                    $"B-131 the user's Retry closes the late Failure of a synced item without a second upload {what} (item failures {TargetFailures(rig, target)}, upload commits {commits})");
                return;
            }
            // The live upload settles after the Repair's Failure, and its successful settlement closes that Failure.
            await UntilAsync(() => TargetFailures(rig, target) == 0,
                "B-131 the live upload settles and closes the item Failure " + what).ConfigureAwait(false);
            Check.Require(Uploaded(rig, target, content) && Volatile.Read(ref commits) == 1 &&
                RetryNotices(rig.Store, rig.PairId) == 0,
                $"B-131 the live upload syncs the item once and leaves no Failure {what} (upload commits {commits})");
        }
        finally
        {
            rig.BeforeCommit = null;
            rig.BeforeSelect = null;
            Volatile.Read(ref staleChecked)?.TrySetResult();
            turned.TrySetResult();
            release.TrySetResult();
            checkedRepair.TrySetResult();
            retried.TrySetResult();
        }
    }

    // AutoSync has no opportunity running or queued for the pair, so every earlier hold decision has completed.
    private static bool AutoSyncIdle(EngineHost host, string pairId)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var auto = typeof(EngineHost).GetField("_auto", Private)!.GetValue(host)!;
        var type = auto.GetType();
        lock (type.GetField("_gate", Private)!.GetValue(auto)!)
            return !((System.Collections.IDictionary)type.GetField("_active", Private)!.GetValue(auto)!).Contains(pairId) &&
                !((System.Collections.IDictionary)type.GetField("_pending", Private)!.GetValue(auto)!).Contains(pairId);
    }

    // The item's Dropbox copy has the content and its synced binding is saved.
    private static bool Uploaded(ItemRig rig, string name, string content) => OnDropbox(rig, name, content) &&
        rig.Store.SyncedByKey(rig.PairId, Side.Local, PathNames.Key(name)) is { IsFolder: false };

    private static bool OnDropbox(ItemRig rig, string name, string content) =>
        rig.Rig.Wire.Snapshot().Any(item => item.Path == name && !item.Folder &&
            System.Text.Encoding.UTF8.GetString(item.Bytes) == content);

    private static bool SiblingSynced(ItemRig rig, string name, string content) =>
        System.IO.File.ReadAllText(Path.Combine(rig.Rig.Pair.LocalRoot, name)) == content &&
        rig.Store.SyncedByKey(rig.PairId, Side.Local, PathNames.Key(name)) is { IsFolder: false };

    // B-141 (engine.md 819, RQ-179): debt that lands after a Repair's FinishRepair and before its completion is a new
    // event. The completed Repair records no prerequisite retry and no notice, and the owed Repair still runs.
    private static async Task CompletedRepairBesideNewDebtAsync(string root, bool memory)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root,
            "repair-new-debt-" + (memory ? "memory" : "store")), turn: false, memory: memory).ConfigureAwait(false);
        var what = $"(memory {memory})";
        // The owed Repair is held at its FinishRepair, so the completed Repair's accounting is read before the owed
        // Repair could close a notice or retry it left. Reaching that hold also proves the first completion has run:
        // the pair registers one prerequisite at a time.
        var owedFinishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            rig.Owe();
            await using (var repair = rig.HoldRepair(window: true))
            {
                await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
                repair.Proceed();
                await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
                rig.BeforeFinish = () =>
                {
                    rig.BeforeFinish = null;
                    owedFinishing.TrySetResult();
                    rig.Hold(checkedFirst.Task, "B-141 the owed Repair waits for the completed Repair's check");
                };
                // New debt and its event land between FinishRepair and the completion.
                rig.Owe();
                rig.Monitor.Wake(rig.PairId);
                repair.Proceed();
            }
            await owedFinishing.Task.WaitAsync(HookBound).ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) is null && RetryNotices(rig.Store, rig.PairId) == 0,
                $"B-141 a completed Repair beside new debt records no prerequisite retry and no notice, and the owed Repair runs {what} (retry {rig.Store.ReadPrerequisiteRetry(rig.PairId)}, notices {RetryNotices(rig.Store, rig.PairId)})");
        }
        finally
        {
            rig.BeforeFinish = null;
            checkedFirst.TrySetResult();
        }
        await UntilAsync(() => !rig.Store.Obligated(rig.PairId) && !rig.RepairRegistered(),
            "B-141 the owed Repair for the new debt completes " + what).ConfigureAwait(false);
        Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) is null && RetryNotices(rig.Store, rig.PairId) == 0,
            $"B-141 the owed Repair clears the new debt with no retry or notice {what} (retry {rig.Store.ReadPrerequisiteRetry(rig.PairId)}, notices {RetryNotices(rig.Store, rig.PairId)})");
    }

    // B-131: a Repair row whose retry capture is refused as a changed selection (the store's read finds the item's
    // episode no longer matches; injected at that read) raises only that item's FailChanged Failure and lets the Repair
    // finish its sibling. Ordinary dispatch is held until then, so only the Repair captures the item; the user's Retry
    // then syncs it.
    private static async Task RepairCaptureRefusalFailsItemAsync(string root, bool memory)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root,
            "repair-capture-refused-" + (memory ? "memory" : "store")), turn: false, memory: memory).ConfigureAwait(false);
        const string target = "target.txt";
        const string sibling = "sibling.txt";
        var what = $"(memory {memory})";
        var refusals = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeSelect = () => rig.Hold(done.Task, "B-131 ordinary dispatch waits for the Repair");
        rig.BeforeCapture = path =>
        {
            if (!path.EndsWith(target, StringComparison.Ordinal)) return;
            Interlocked.Increment(ref refusals);
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        };
        try
        {
            System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, target), "captured " + what);
            rig.Owe();
            rig.Rig.Wire.SeedFile(sibling, "sibling " + what);
            rig.Monitor.Wake(rig.PairId);
            await UntilAsync(() => !rig.Store.Obligated(rig.PairId) &&
                System.IO.File.Exists(Path.Combine(rig.Rig.Pair.LocalRoot, sibling)),
                "B-131 the Repair finishes its sibling beside the refused capture " + what).ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) is null && RetryNotices(rig.Store, rig.PairId) == 0 &&
                TargetFailures(rig, target) == 1 && TargetFailures(rig, target, TextKey.FailChanged) == 1 &&
                Volatile.Read(ref refusals) > 0 && SiblingSynced(rig, sibling, "sibling " + what),
                $"B-131 a Repair row whose retry capture is refused raises only that item's FailChanged Failure and syncs its sibling {what} (retry {rig.Store.ReadPrerequisiteRetry(rig.PairId)}, notices {RetryNotices(rig.Store, rig.PairId)}, item failures {TargetFailures(rig, target)}, refusals {refusals})");
        }
        finally
        {
            rig.BeforeCapture = null;
            rig.BeforeSelect = null;
            done.TrySetResult();
        }
        // The ordinary dispatch released above has ended before the Retry starts.
        await UntilAsync(rig.AutoSyncIdle, "B-131 the released ordinary dispatch ends " + what).ConfigureAwait(false);
        await rig.Host.Operations.ResolveAsync(rig.PairId, null, ResolutionChoice.Retry, null, null, true, null, default)
            .ConfigureAwait(false);
        Check.Require(TargetFailures(rig, target) == 0 && Uploaded(rig, target, "captured " + what),
            $"B-131 the user's Retry syncs the item whose capture was refused and closes its Failure {what} (item failures {TargetFailures(rig, target)})");
    }

    // B-131: only the attempt collision is contained at the row. A Repair row whose reservation is refused because the
    // Repair itself lost admission (a pause withdraws it, EngineHost.CanAdmit) keeps that refusal: the Repair stops and
    // the row raises no item Failure. The Repair is held at the row's attempt capture, just before it reserves, and
    // Pause's cancellation is held back (as in B4) so the reservation, not the token, refuses first.
    private static async Task RepairAdmissionRefusalPropagatesAsync(string root)
    {
        var folder = Path.Combine(root, "repair-refusal-propagates");
        await using var rig = await ItemRig.StartedAsync(folder, turn: false).ConfigureAwait(false);
        const string target = "target.txt";
        // Ordinary dispatch stays out of the target, so only the Repair captures and reserves it.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeSelect = () => rig.Hold(done.Task, "B-131 ordinary dispatch waits for the refused Repair");
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeCapture = path =>
        {
            if (!path.EndsWith(target, StringComparison.Ordinal)) return;
            rig.BeforeCapture = null;
            capturing.TrySetResult();
            rig.Hold(go.Task, "B-131 the Repair row is released after the pause");
        };
        var drain = new DeferredSynchronizationContext();
        Task? pausing = null;
        try
        {
            System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, target), "refused");
            int opened;
            lock (rig.Logs) opened = rig.Logs.Count;
            rig.Owe();
            rig.Monitor.Wake(rig.PairId);
            await capturing.Task.WaitAsync(HookBound).ConfigureAwait(false);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(drain);
            try { pausing = rig.Host.PauseAsync(rig.PairId, default); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            go.TrySetResult();
            // The refused Repair stops and retires; its log (Failed lines flush at once) is then read once.
            await UntilAsync(() => !rig.RepairRegistered(), "B-131 the refused Repair stops").ConfigureAwait(false);
            var logged = RunLogsContain(rig, opened, "Failed\tLiveAdmissionStaleException");
            Check.Require(logged && TargetFailures(rig, target) == 0 && rig.Store.Obligated(rig.PairId),
                $"B-131 a Repair row refused for lost admission stops the Repair with that refusal and raises no item Failure (refusal logged {logged}, item failures {TargetFailures(rig, target)}, obligated {rig.Store.Obligated(rig.PairId)})");
        }
        finally
        {
            rig.BeforeCapture = null;
            rig.BeforeSelect = null;
            go.TrySetResult();
            done.TrySetResult();
            drain.Release();
        }
        if (pausing is not null) await pausing.WaitAsync(HookBound).ConfigureAwait(false);
    }

    // B-131: an explicit Repair while the pair's prerequisite Repair is registered, before FinishRepair or in the window
    // after it (the obligation already cleared), is refused as already running and starts no second Repair; the running
    // Repair takes it as its owed event and completes.
    private static async Task ExplicitRepairBesideRunningRepairAsync(string root, bool afterFinish)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root,
            "repair-already-running" + (afterFinish ? "-after-finish" : "")), turn: false).ConfigureAwait(false);
        var finishes = 0;
        Exception? refused = null;
        rig.Owe();
        await using (var repair = rig.HoldRepair(window: true))
        {
            await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
            if (afterFinish)
            {
                repair.Proceed();
                await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
            }
            try { await rig.Host.Operations.RepairAsync(rig.PairId, default).ConfigureAwait(false); }
            catch (Exception error) { refused = error; }
            if (!afterFinish)
            {
                repair.Proceed();
                await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
            }
            // The held Repair is past FinishRepair; any later FinishRepair would be a second Repair.
            rig.BeforeFinish = () => Interlocked.Increment(ref finishes);
            repair.Proceed();
        }
        await UntilAsync(() => !rig.Store.Obligated(rig.PairId) && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null,
            "B-131 the running Repair completes the obligation").ConfigureAwait(false);
        await rig.IterateAsync().ConfigureAwait(false);
        rig.BeforeFinish = null;
        Check.Require(refused is EngineRequestException { Key: TextKey.RepairAlreadyRunning } &&
            Volatile.Read(ref finishes) == 0,
            $"B-131 an explicit Repair beside the running prerequisite Repair is refused as already running and starts no second Repair (after FinishRepair {afterFinish}; {refused?.GetType().Name}: {refused?.Message}; later FinishRepair calls {finishes})");
    }

    // B-141 R2, through the public ordinary Repair (no prerequisite debt). FinishRepair keeps an item-only wait while
    // the pair still has pending work after the Repair and clears it once the Repair left none:
    //   own:        the Repair's own upload of the file is refused with proven no effect; its wait starts at once
    //               (attempts 1, the A01 first wait, its deadline in the Repair's log) and survives FinishRepair.
    //   older:      an earlier turn's wait, and the Repair's upload is refused again: the same row advances once
    //               (attempts 2, never reset) and survives; the file is attempted once at the new deadline.
    //   concurrent: an ordinary turn on another file commits while the Repair waits at FinishRepair; that newer row
    //               survives unchanged and its file is attempted once at its deadline.
    //   settled:    an earlier turn's wait whose file the Repair syncs: no work is left, so the wait is cleared and
    //               holds nothing.
    // No notice in any shape, and nothing is attempted before a kept deadline. The refusal is a write throttle: its
    // limiter pause (AdaptiveLimiter.Throttle) is the same first wait on this clock and holds the Repair's next request,
    // so own's kept deadline is due when the Repair returns and its file is sent then.
    private static async Task OrdinaryRepairItemWaitAsync(string root, bool memory, string shape)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root,
            "repair-ordinary-wait-" + shape + "-" + (memory ? "memory" : "store")), turn: false, memory: memory)
            .ConfigureAwait(false);
        var what = $"(shape {shape}, memory {memory})";
        var generation = rig.Store.ReadPairExecution(rig.PairId)!.Value.Generation;
        const string other = "other.txt";
        var refused = shape is "own" or "older";
        // No ordinary attempt holds the file at the earlier turn or the Repair; the Repair's input is written below.
        await rig.SyncedAsync().ConfigureAwait(false);
        FailureRetryOwner.State? earlier = null;
        if (shape is "older" or "settled")
        {
            await rig.TurnAsync().ConfigureAwait(false);
            earlier = rig.Store.ReadItemRetry(rig.PairId);
        }
        Check.Require(!rig.Store.Obligated(rig.PairId) && rig.Store.ReadItemRetry(rig.PairId) == earlier &&
            (earlier is null) == (shape is "own" or "concurrent"),
            "B-141 R2 the ordinary Repair starts with no prerequisite debt and the shape's earlier wait " + what);
        // A batch member refused for write contention is TemporaryNoEffect (DropboxErrors.MapUploadFailure); a direct
        // upload's refusal is not. A composite work scope the check holds on the pair's limiter, from its own
        // dispatch and independent of the Repair, keeps the file from staging direct (AdaptiveLimiter.EnterWorkAsync,
        // SelectDirect). The check releases it only after the Repair has returned, so the Repair never waits on it.
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = refused
            ? rig.Host.DispatchSessionAsync(rig.PairId, () => null, async (session, token) =>
            {
                await using var work = await session.Admission.EnterWorkAsync(false, token).ConfigureAwait(false);
                holding.TrySetResult();
                await release.Task.WaitAsync(HookBound, token).ConfigureAwait(false);
                return true;
            }, default)
            : Task.FromResult(true);
        // concurrent: the Repair waits at FinishRepair, after its scans, while an ordinary turn on another file commits.
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (shape == "concurrent")
            rig.BeforeFinish = () =>
            {
                rig.BeforeFinish = null;
                finishing.TrySetResult();
                rig.Hold(proceed.Task, "B-141 R2 the Repair waits at FinishRepair for the concurrent turn");
            };
        // Ordinary dispatch is held until the row is read, so only the Repair sends the file.
        var refusedTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RepairSummary summary;
        FailureRetryOwner.State? concurrent = null, row;
        DateTimeOffset now;
        int attempts;
        try
        {
            if (refused)
            {
                await holding.Task.WaitAsync(HookBound).ConfigureAwait(false);
                rig.BeforeSelect = () => rig.Hold(read.Task, "B-141 R2 ordinary dispatch waits for the Repair's row");
                rig.AfterTemporary = () => refusedTurn.TrySetResult();
                rig.Rig.Wire.NameWriteErrors[ItemRig.File] = DropboxProtocol.TooManyWriteOperations;
            }
            // The Repair's own input, written only now that ordinary selection is held (own, older) or the earlier wait
            // holds ordinary dispatch (settled), so no ordinary dispatch can upload it first. The journal SyncedAsync
            // started may acquire the write before the Repair's own catch-up does; that wake's dispatch is held the same way.
            if (shape != "concurrent") rig.WriteRetained();
            // concurrent: the journal can acquire other.txt before its turn records the wait (B-175 scan), so ordinary
            // selection is held until the row is read; only Repair's settle, not SelectWork, runs meanwhile.
            else rig.BeforeSelect = () => rig.Hold(read.Task, "B-141 R2 ordinary dispatch waits for the concurrent row");
            // Started apart from this thread: the concurrent shape's BeforeFinish hold blocks the Repair synchronously.
            var repair = Task.Run(() => rig.Host.Operations.RepairAsync(rig.PairId, default));
            if (refused)
            {
                // The refusal's write-throttle pause (the first backoff, as the item's first wait) ends only on this clock.
                await refusedTurn.Task.WaitAsync(HookBound).ConfigureAwait(false);
                // Monitor sees the committed row before the clock moves; its wall and monotonic reads are separate.
                rig.Monitor.ItemWaitPending(rig.PairId, generation, rig.Store.ReadItemRetry(rig.PairId)!.Value);
                rig.Clock.Advance(rig.FirstWait);
            }
            if (shape == "concurrent")
            {
                await finishing.Task.WaitAsync(HookBound).ConfigureAwait(false);
                System.IO.File.WriteAllText(Path.Combine(rig.Rig.Pair.LocalRoot, other), "concurrent " + what);
                await TemporaryTurnAsync(rig.Host, rig.PairId, other, null, null, null).ConfigureAwait(false);
                concurrent = rig.Store.ReadItemRetry(rig.PairId);
                proceed.TrySetResult();
            }
            summary = await repair.ConfigureAwait(false);
            (row, now, attempts) = (rig.Store.ReadItemRetry(rig.PairId), rig.Clock.GetUtcNow(), rig.Attempts);
        }
        finally
        {
            rig.BeforeFinish = null;
            rig.AfterTemporary = null;
            rig.BeforeSelect = null;
            proceed.TrySetResult();
            rig.Rig.Wire.NameWriteErrors.Remove(ItemRig.File);
            read.TrySetResult();
            release.TrySetResult();
            await holder.ConfigureAwait(false);
        }
        string log;
        using (var stream = new FileStream(summary.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            log = reader.ReadToEnd();
        var expected = shape switch
        {
            "own" => row == new FailureRetryOwner.State(1, now),
            "older" => row is { Attempts: 2 } advanced && advanced.RetryAfter >= earlier!.Value.RetryAfter,
            "concurrent" => concurrent is { Attempts: 1 } && row == concurrent,
            _ => row is null && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null &&
                !rig.Host.ItemRetryHolds(rig.PairId, generation, out _)
        };
        var logged = log.Split('\n').Any(line => line.Contains(ItemRig.File, StringComparison.Ordinal) &&
            line.Contains(nameof(RunEvent.RetryFacts.RetryAt), StringComparison.Ordinal));
        Check.Require(summary.Completed && !rig.Store.Obligated(rig.PairId) && RetryNotices(rig.Store, rig.PairId) == 0 &&
            expected && logged == refused,
            "B-141 R2 FinishRepair keeps a pending item-only wait and clears a settled one, with no notice and the " +
            $"refused turn's deadline logged {what}; earlier {earlier}; concurrent {concurrent}; row {row}; " +
            $"logged {logged}; completed {summary.Completed}");
        if (shape != "own")
        {
            await rig.SettleAsync().ConfigureAwait(false);
            Check.Require(rig.Attempts == attempts && rig.Store.ReadItemRetry(rig.PairId) == row,
                "B-141 R2 nothing is attempted before a kept deadline, and a cleared wait leaves nothing to send " + what);
            if (row is not { } kept) return;
            // The first wait is exact; an advanced wait carries jitter, so the clock moves to its stored deadline.
            if (kept.Attempts == 1) await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
            else
            {
                await rig.IterateAsync().ConfigureAwait(false);
                rig.Clock.Advance(kept.RetryAfter - now);
            }
        }
        await UntilAsync(() => rig.Attempts > attempts && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null,
            "B-141 R2 the retained file is attempted at its item deadline " + what).ConfigureAwait(false);
        Check.Require(rig.Attempts == attempts + 1,
            "B-141 R2 the retained file is attempted once at its item deadline " + what);
    }

    // Reads, once, only the logs opened from the given index on.
    private static bool RunLogsContain(ItemRig rig, int from, string text)
    {
        string[] paths;
        lock (rig.Logs) paths = rig.Logs.Skip(from).ToArray();
        return paths.Any(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(text, StringComparison.Ordinal);
        });
    }

    // The item's open Failures, of one cause or of any.
    private static int TargetFailures(ItemRig rig, string target, TextKey? cause = null) =>
        rig.Store.Issues(rig.PairId, [IssueKind.Failure]).Count(issue =>
            (cause is null || issue.Cause == cause) && issue.Path.EndsWith(target, StringComparison.Ordinal));

    // An upload request that commits the named file: a one-request upload or session finish names it in its argument
    // header, a batch finish in its JSON body (session starts and appends carry content only).
    private static async Task<bool> CommitsUploadAsync(HttpRequestMessage request, string name, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/" + DropboxProtocol.UploadSessionFinishBatch, StringComparison.Ordinal))
            return request.Content is not null && (await request.Content.ReadAsStringAsync(token).ConfigureAwait(false))
                .Contains(name, StringComparison.Ordinal);
        return (path.EndsWith("/" + DropboxProtocol.Upload, StringComparison.Ordinal) ||
                path.EndsWith("/" + DropboxProtocol.UploadSessionFinish, StringComparison.Ordinal)) &&
            request.Headers.TryGetValues("Dropbox-API-Arg", out var args) &&
            args.Any(arg => arg.Contains(name, StringComparison.Ordinal));
    }

    private static bool IsUpload(string path) =>
        path.EndsWith("/" + DropboxProtocol.Upload, StringComparison.Ordinal) ||
        path.EndsWith("/" + DropboxProtocol.UploadSessionStart, StringComparison.Ordinal);

    // Upload commits of the named files, counted at the rig's transport as CommitsUploadAsync names them; the wire still
    // answers every request. It replaces any earlier transport hook of the rig.
    private sealed class UploadCommits
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        internal UploadCommits(ItemRig rig, params string[] names)
        {
            rig.Rig.Transport.Respond = async (request, token) =>
            {
                foreach (var name in names)
                    if (await CommitsUploadAsync(request, name, token).ConfigureAwait(false))
                        lock (_counts) _counts[name] = _counts.GetValueOrDefault(name) + 1;
                return null;
            };
        }

        internal int this[string name]
        {
            get { lock (_counts) return _counts.GetValueOrDefault(name); }
        }
    }

    // One proven-no-effect temporary turn through the item port a session's executor uses; with `then`, each further
    // file's turn follows in the same registration and joins the first one's wait. A null id reads the file's observed
    // id from that session.
    private static Task<(bool NewlyAccepted, DateTimeOffset? RetryAt)> TemporaryTurnAsync(EngineHost host,
        string pairId, string path, string? id, TimeSpan? wait, long? started, bool prerequisite = false,
        IReadOnlyList<string>? then = null) =>
        host.DispatchSessionAsync(pairId, () => null, async (session, token) =>
        {
            var epoch = host.Store.ReadSource(pairId, Side.Local)!.Value.Epoch;
            var port = session.Context.ItemAttempts;
            async Task<(bool NewlyAccepted, DateTimeOffset? RetryAt)> ReportAsync(string file, string? known)
            {
                known ??= (await session.Local.ReadOneAsync(file, token).ConfigureAwait(false))!.Id;
                var subject = new IssueSubject([new IssueItem(Side.Local, file, known)], Side.Local);
                (bool NewlyAccepted, DateTimeOffset? RetryAt) reported = default;
                await port.ExecuteAsync(session.Context.Opportunity, null, subject, (attemptId, _) =>
                {
                    reported = port.ReportTemporary(attemptId, epoch, file, subject, TextKey.FailChanged, null,
                        ItemRetryDispatch.OrdinaryItem, wait, started);
                    return Task.FromResult(true);
                }, token).ConfigureAwait(false);
                return reported;
            }
            var first = await ReportAsync(path, id).ConfigureAwait(false);
            foreach (var next in then ?? []) await ReportAsync(next, null).ConfigureAwait(false);
            return first;
        }, default, prerequisite: prerequisite);
}
