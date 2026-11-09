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
        await ItemWaitHeldThroughEventsAsync(root).ConfigureAwait(false);
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
        await RepairTurnStartsItemWaitAsync(root).ConfigureAwait(false);
        await FailedRepairTurnAdvancesOnceAsync(root).ConfigureAwait(false);
        await PausedRepairCompletionAccountsNothingAsync(root, providerWait: false).ConfigureAwait(false);
        await PausedRepairCompletionAccountsNothingAsync(root, providerWait: true).ConfigureAwait(false);
        await RepairWindowHoldsDispatchAsync(root).ConfigureAwait(false);
        await ResolveRunsBesideItemWaitAsync(root).ConfigureAwait(false);
        await RefreshInterleavingKeepsSignalAsync(root).ConfigureAwait(false);
    }

    // A1: quiet pair, item-only row; events before the deadline attempt nothing, and the deadline attempts once.
    private static async Task ItemWaitHeldThroughEventsAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-events")).ConfigureAwait(false);
        var dispatched = rig.Dispatches;
        await rig.SettleAsync().ConfigureAwait(false);
        Check.Require(rig.Attempts == 0 && rig.Dispatches == dispatched,
            "A1 events before the item deadline dispatch nothing and attempt no file");
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
        await rig.AttemptedOnceAsync("A7, D6 the due timer alone attempts the retained file exactly once")
            .ConfigureAwait(false);
        await rig.DispatchedOnceAsync(dispatched, "A7 the due timer alone starts exactly one ordinary dispatch")
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
            var dispatched = rig.Dispatches;
            await rig.SettleAsync().ConfigureAwait(false);
            Check.Require(rig.Attempts == 0 && rig.Dispatches == dispatched,
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
        await rig.AttemptedOnceAsync("A5 the retained file is attempted once in total").ConfigureAwait(false);
        await rig.DispatchedOnceAsync(dispatched,
            "A5 an event after the deadline and the due timer start one ordinary dispatch in total").ConfigureAwait(false);
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

    // C1: a Repair with no temporary turn clears a pre-Repair item row, and its success Kick dispatches at once.
    private static async Task RepairWithoutTurnReleasesRowAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-no-turn"))
            .ConfigureAwait(false);
        var dispatched = rig.Dispatches;
        rig.Owe();
        rig.Monitor.Wake(rig.PairId);
        await UntilAsync(() => !rig.Store.Obligated(rig.PairId) && rig.Store.ReadPrerequisiteRetry(rig.PairId) is null &&
            rig.Dispatches > dispatched).ConfigureAwait(false);
        Check.Require(rig.Store.ReadItemRetry(rig.PairId) is null,
            "C1 a Repair with no temporary turn clears the row and its success Kick dispatches at once");
    }

    // C2, C3: a Repair's own turn (no provider wait) beside a root notice lets FinishRepair commit; completion writes
    // one item row with attempts 1, the A01 first wait and no notice, and the success Kick is held until it.
    private static async Task RepairTurnStartsItemWaitAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-turn"), turn: false)
            .ConfigureAwait(false);
        rig.Owe(notice: true);
        await using (var repair = rig.HoldRepair())
        {
            await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.TurnAsync(prerequisite: true).ConfigureAwait(false);
        }
        await UntilAsync(() => rig.Store.ReadItemRetry(rig.PairId) is not null).ConfigureAwait(false);
        var now = rig.Clock.GetUtcNow();
        Check.Require(!rig.Store.Obligated(rig.PairId) && RetryNotices(rig.Store, rig.PairId) == 0 &&
            rig.Store.ReadItemRetry(rig.PairId) == new FailureRetryOwner.State(1, now + rig.FirstWait),
            "C2 FinishRepair commits; completion writes one item-only row, attempts 1, the A01 first wait, no notice");
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
        var wait = TimeSpan.FromSeconds(20);
        var receipt = rig.Clock.GetUtcNow();
        var started = rig.Clock.GetTimestamp();
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Owe();
        await using (var repair = rig.HoldRepair(new IOException("synthetic Repair failure after its own turn")))
        {
            await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.TurnAsync(wait, started, prerequisite: true).ConfigureAwait(false);
        }
        await UntilAsync(() => rig.Store.ReadPrerequisiteRetry(rig.PairId) is not null).ConfigureAwait(false);
        Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) == new FailureRetryOwner.State(1, receipt + wait) &&
            RetryNotices(rig.Store, rig.PairId) == 1,
            "C4 the failed Repair's own turn advances once to receipt + R exactly and adds the notice");
    }

    // C5: a pause between FinishRepair and completion accounts nothing. On resume the retained file is attempted once
    // at resume when there was no provider wait; with one, the account floor sends no request before R.
    private static async Task PausedRepairCompletionAccountsNothingAsync(string root, bool providerWait)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, providerWait ? "item-paused-repair-wait" :
            "item-paused-repair"), turn: false).ConfigureAwait(false);
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
            await UntilAsync(() => !rig.Host.ItemRetryHolds(rig.PairId, generation, out _)).ConfigureAwait(false);
            var calls = rig.Calls;
            await rig.Host.ResumeAsync(rig.PairId, default).ConfigureAwait(false);
            if (providerWait)
            {
                await UntilAsync(() => rig.Clock.NextDueIn == wait).ConfigureAwait(false);
                Check.Require(rig.Calls == calls, "C5 on resume the account floor sends no request before R");
                rig.Clock.Advance(wait);
            }
            await UntilAsync(() => rig.Attempts == 1).ConfigureAwait(false);
            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.PairId) is null,
                "C5 on resume the retained file is attempted once, with no row");
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

    // C6: between FinishRepair and completion of a Repair with its own turn, an event Kick is held; after completion
    // the pair stays held until the item deadline.
    private static async Task RepairWindowHoldsDispatchAsync(string root)
    {
        await using var rig = await ItemRig.StartedAsync(Path.Combine(root, "item-repair-window"), turn: false)
            .ConfigureAwait(false);
        rig.Owe();
        await using (var repair = rig.HoldRepair(window: true))
        {
            await repair.Finishing.WaitAsync(HookBound).ConfigureAwait(false);
            await rig.TurnAsync(prerequisite: true).ConfigureAwait(false);
            repair.Proceed();
            await repair.Window.WaitAsync(HookBound).ConfigureAwait(false);
            var dispatched = rig.Dispatches;
            await rig.SettleAsync().ConfigureAwait(false);
            Check.Require(rig.Dispatches == dispatched && rig.Attempts == 0,
                "C6 an event Kick in the window after FinishRepair attempts nothing");
        }
        await UntilAsync(() => rig.Store.ReadItemRetry(rig.PairId) is not null).ConfigureAwait(false);
        await rig.SettleAsync().ConfigureAwait(false);
        Check.Require(rig.Attempts == 0, "C6 after completion the pair is held until the item deadline");
        await rig.FireAsync(rig.FirstWait).ConfigureAwait(false);
        await rig.AttemptedOnceAsync("C6 the retained file is attempted once at the item deadline").ConfigureAwait(false);
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
    // one fake clock, with no journal or feed running. AutoSync reads live refusal once at the start of each
    // opportunity and asks for folder-job progress once after each dispatch that returns; each dispatch it starts
    // opens one log. A file attempt is one upload request for the retained file.
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
        private void MarkAttempts()
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

        internal static async Task<ItemRig> OpenAsync(string folder, FakeTimeProvider clock, DropboxWire? wire = null)
        {
            var rig = new ItemRig(clock);
            rig.Rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: rig.Monitor,
                mode: EngineHost.Mode.Persistent, wire: wire, decorate: store => new HookStore(store)
                {
                    OnReadLiveRefusal = () => Interlocked.Increment(ref rig._opportunities),
                    OnFolderJobProgress = () => Interlocked.Increment(ref rig._dispatches),
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
                }).ConfigureAwait(false);
            return rig;
        }

        // A completed first Repair, then a local file written after it that only a retained turn's work reaches,
        // optionally that turn's item row, then host start with Monitor idle.
        internal static async Task<ItemRig> StartedAsync(string folder, TimeSpan? providerWait = null, bool turn = true)
        {
            var rig = await OpenAsync(folder, NewClock()).ConfigureAwait(false);
            try
            {
                await rig.BaselineAsync().ConfigureAwait(false);
                if (turn) await rig.TurnAsync(providerWait, providerWait is null ? null : rig.Clock.GetTimestamp())
                    .ConfigureAwait(false);
                await rig.StartAsync().ConfigureAwait(false);
                // The startup opportunity is held by the row, or with no row dispatches once.
                if (turn) await UntilAsync(() => rig.Opportunities > 0).ConfigureAwait(false);
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
        private void WriteRetained() => System.IO.File.WriteAllText(Path.Combine(Rig.Pair.LocalRoot, File),
            "retained " + Interlocked.Increment(ref _version));

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

        // Waits until Monitor's timer is armed for exactly the remaining wait, then fires it.
        internal async Task ArmedAsync(TimeSpan remaining) =>
            await UntilAsync(() => Clock.NextDueIn == remaining).ConfigureAwait(false);

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
            await UntilAsync(() => Attempts > 0 && Store.ReadPrerequisiteRetry(PairId) is null, what).ConfigureAwait(false);
            Check.Require(Attempts == 1, what);
        }

        public async ValueTask DisposeAsync()
        {
            BeforeSelect = BeforeBegin = AfterRefresh = BeforeFinish = AfterFinish = AfterTemporary = null;
            try { if (Rig is not null) await Rig.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                Check.Require(Volatile.Read(ref _barrierFailure) is null,
                    "every held race was released by the check, not by a barrier timeout: " + _barrierFailure);
            }
        }
    }

    private static bool IsUpload(string path) =>
        path.EndsWith("/" + DropboxProtocol.Upload, StringComparison.Ordinal) ||
        path.EndsWith("/" + DropboxProtocol.UploadSessionStart, StringComparison.Ordinal);

    // One proven-no-effect temporary turn through the item port a session's executor uses. A null id reads the
    // file's observed id from that session.
    private static Task<(bool NewlyAccepted, DateTimeOffset? RetryAt)> TemporaryTurnAsync(EngineHost host,
        string pairId, string path, string? id, TimeSpan? wait, long? started, bool prerequisite = false) =>
        host.DispatchSessionAsync(pairId, () => null, async (session, token) =>
        {
            id ??= (await session.Local.ReadOneAsync(path, token).ConfigureAwait(false))!.Id;
            var subject = new IssueSubject([new IssueItem(Side.Local, path, id)], Side.Local);
            var epoch = host.Store.ReadSource(pairId, Side.Local)!.Value.Epoch;
            var port = session.Context.ItemAttempts;
            (bool NewlyAccepted, DateTimeOffset? RetryAt) reported = default;
            await port.ExecuteAsync(session.Context.Opportunity, null, subject, (attemptId, _) =>
            {
                reported = port.ReportTemporary(attemptId, epoch, path, subject, TextKey.FailChanged, null,
                    ItemRetryDispatch.OrdinaryItem, wait, started);
                return Task.FromResult(true);
            }, token).ConfigureAwait(false);
            return reported;
        }, default, prerequisite: prerequisite);
}
