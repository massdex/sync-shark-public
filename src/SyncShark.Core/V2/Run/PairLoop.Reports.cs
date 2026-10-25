namespace Massdex.SyncShark.V2;

internal sealed partial class PairLoop
{
    // Retry when something failed or could not be verified (outcome failed, or partial with failures); a run whose
    // only leftover is held rows waits for the user's decision, not for a retry.
    private void AfterReport(RunReport report)
    {
        _deferredRetry = false;
        var previous = _last;
        // The report carries its real completion instant, so the tray announces this run at its true time and can
        // order two completions without inventing a timestamp of its own.
        _last = report with { CompletedUtc = _in.Time.GetUtcNow() };
        if (report.Outcome == RunOutcomes.StateDamaged)
        {
            _held = new NoticeReason(RunOutcomes.StateDamaged,
                SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { Outcome = report.Outcome }).NotificationDetail);
            _retryAt = null;
            Publish(AutoStatus.Held, _held);
            return;
        }
        if (report.HaltedBy is { } halted) { Hold(halted, record: false); return; }
        if (report.Outcome is RunOutcomes.Stopped or RunOutcomes.Paused) return;
        if (report.ProviderReached || report.Outcome is RunOutcomes.Complete or RunOutcomes.Partial)
        {
            try
            {
                _in.Store.CloseNoticeFamily(_pairId, "autosync-failed");
            }
            catch (Exception error) { RunOutput.Observe(error); }
        }
        // SyncRun already recorded a returned report's failure. The host never counts or publishes it twice.
        if (report.CompletionFailed || report.Outcome == RunOutcomes.Failed || report.FailureCount > 0 || report.Failures.Count > 0 ||
            report.DeletionEvidencePending.Count > 0) ScheduleRetry();
        else if (report.JournalBacklog || DrainedPlanning(report, previous))
        {
            FailureRetryOwner.ResetDeferredRetry(_in.Store, _pairId);
            _retryAt = _in.Time.GetUtcNow(); // Useful bounded progress, not a failure/backoff.
        }
        else if (report.Deferred > 0) ScheduleDeferredRetry(report, previous);
        // Nothing outstanding: the successful run already closed its run notice.
    }

    // Immediate re-run only on actual drain: the queue fell by at least one admitted page of entities. A partial
    // page, a held page, an admission, or a first report with no previous count waits for the normal schedule.
    private static bool DrainedPlanning(RunReport report, RunReport? previous) =>
        report.PlanningBacklog > 0 && previous is { PlanningBacklog: > 0 } prior &&
        (long)prior.PlanningBacklog - report.PlanningBacklog >= SqliteStateStore.DeferredIntakeEntityCap;

    private void ScheduleRetry()
    {
        _deferredRetry = false;
        var now = _in.Time.GetUtcNow();
        var state = FailureRetryOwner.RunFailureState(_in.Store, _pairId);
        _retryAt = state?.RetryAfter ?? FailureRetryOwner.Failed(0, now).RetryAfter;
    }

    // A deferred-only report is not a failure: wake when the earliest retained item retry is due, bounded by the
    // safety floor and the shared ceiling. Only a report that made no progress (neither the deferred count nor the
    // planning backlog shrank) advances the shared exponential tier, so a constant backlog cannot spin at ten seconds.
    private void ScheduleDeferredRetry(RunReport report, RunReport? previous)
    {
        _deferredRetry = true;
        var now = _in.Time.GetUtcNow();
        var floor = now + _in.Settings.MinSafetyPoll;
        var ceiling = now + TimeSpan.FromSeconds(FailureRetryOwner.MaximumRetrySeconds);
        var deadline = _in.Store.EarliestSyncRetryAfter(_pairId) ?? ceiling;
        if (deadline < floor) deadline = floor;
        if (deadline > ceiling) deadline = ceiling;
        if (Progressed(report, previous))
        {
            FailureRetryOwner.ResetDeferredRetry(_in.Store, _pairId);
            _retryAt = deadline;
            return;
        }
        var state = FailureRetryOwner.Failed(FailureRetryOwner.DeferredRetryState(_in.Store, _pairId)?.Attempts ?? 0, now);
        FailureRetryOwner.RecordDeferredRetry(_in.Store, _pairId, report.Deferred, state);
        // No earlier than the shared tier and never before an item could actually be due.
        if (state.RetryAfter > deadline) deadline = state.RetryAfter;
        _retryAt = deadline;
    }

    private static bool Progressed(RunReport report, RunReport? previous) => previous is { } prior &&
        (report.Deferred < prior.Deferred || report.PlanningBacklog < prior.PlanningBacklog);
}
