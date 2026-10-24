namespace Massdex.SyncShark.V2;

internal sealed partial class PairLoop
{
    // Retry when something failed or could not be verified (outcome failed, or partial with failures); a run whose
    // only leftover is held rows waits for the user's decision, not for a retry.
    private void AfterReport(RunReport report)
    {
        // The report carries its real completion instant, so the tray announces this run at its true time and can
        // order two completions without inventing a timestamp of its own.
        if (_last is { Deferred: > 0 } previous && report.Deferred < previous.Deferred)
            _backoff = TimeSpan.Zero; // A bounded backlog is draining; don't exponentially slow useful progress.
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
        else if (report.JournalBacklog)
        {
            _backoff = TimeSpan.Zero;
            _retryAt = _in.Time.GetUtcNow(); // Useful bounded progress, not a failure/backoff.
        }
        else if (report.Deferred > 0) ScheduleRetry();
        else _backoff = TimeSpan.Zero;
    }

    private void ScheduleRetry()
    {
        var now = _in.Time.GetUtcNow();
        var state = FailureRetryOwner.RunFailureState(_in.Store, _pairId);
        _retryAt = state?.RetryAfter ?? FailureRetryOwner.Failed(0, now).RetryAfter;
        _backoff = _retryAt.Value > now ? _retryAt.Value - now : TimeSpan.Zero;
    }

}
