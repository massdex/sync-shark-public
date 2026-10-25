namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private RunReport FinishIncrementalCompletion(CompletionSteps completion, string pairId, Plan plan,
        Tally tally, ReconcileResult reconcile, IEnumerable<string> pendingEvidence, IEnumerable<string> unresolvedEvidence,
        ProgressThrottle throttle, bool journalBacklog, int planningBacklog, CancellationToken ct)
    {
        completion.Step(() => throttle.Report(RunPhases.Finishing, null, tally.Done, 0));
        completion.Step(() => throttle.Flush());
        var report = WithReconciliation(BuildReport(pairId, plan, tally, ct.IsCancellationRequested), reconcile)
            with { JournalBacklog = journalBacklog, PlanningBacklog = planningBacklog };
        completion.Step(() => report = report with { Deferred = _store.CountSyncRetries(pairId) });
        completion.Step(() => report = IncrementalOutcome(report,
            pendingEvidence.Concat(unresolvedEvidence).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        return completion.Finish(report);
    }

    // Completion is bookkeeping after effects. Keep the executor's tally even when a
    // bookkeeping operation fails, and let independent completion operations finish.
    private sealed class CompletionSteps(SyncRun run, string pairId, CancellationToken ct)
    {
        private Exception? _error;
        private bool _halt;
        private Exception? _haltingError;
        internal bool Step(Action action)
        {
            if (!_halt && (run._store as SqliteStateStore)?.TakeResultMaintenanceHalt() is { } maintenanceHalt) Failed(maintenanceHalt);
            if (_halt) return false;
            try { action(); return true; }
            catch (Exception error) { Failed(error); return false; }
        }

        internal async Task<bool> StepAsync(Func<Task> action)
        {
            if (!_halt && (run._store as SqliteStateStore)?.TakeResultMaintenanceHalt() is { } maintenanceHalt) Failed(maintenanceHalt);
            if (_halt) return false;
            try { await action(); return true; }
            catch (Exception error) { Failed(error); return false; }
        }

        internal void Failed(Exception error)
        {
            _error ??= error;
            // Lost authority, cancellation or damaged state halts dependent writes,
            // but cannot retroactively erase effects already counted by the executor.
            _halt |= !RunFailure(error, ct);
            if (!RunFailure(error, ct)) _haltingError ??= StorageFailureClassifier.IsDirectStateDamage(error)
                ? new StateDamagedException(error.Message, error) : error;
            RunLog.Trace("Run completion step failed: " + error.ToString());
        }

        internal RunReport Finish(RunReport report)
        {
            if (!_halt && (run._store as SqliteStateStore)?.TakeResultMaintenanceHalt() is { } maintenanceHalt) Failed(maintenanceHalt);
            if (_error is null) return report;
            // One stable pair-scoped notice/retry, regardless of the number of failed steps.
            try { run.RecordUnavailable(pairId, _haltingError ?? report.HaltedBy ?? _error, completion: true); }
            catch (Exception noticeError) { RunLog.Trace("Completion notice unavailable: " + noticeError.ToString()); }
            return report with { Paused = report.Paused || (_haltingError ?? report.HaltedBy ?? _error) is PairPausedException,
                Cancelled = report.Cancelled || ct.IsCancellationRequested, CompletionFailed = true, HaltedBy = _haltingError ?? report.HaltedBy };
        }
    }
}
