namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Completion is bookkeeping after effects. Keep the executor's tally even when a
    // bookkeeping operation fails, and let independent completion operations finish.
    private sealed class CompletionSteps(SyncRun run, string pairId, CancellationToken ct)
    {
        private Exception? _error;
        private bool _halt;
        internal bool Step(Action action)
        {
            if (_halt) return false;
            try { action(); return true; }
            catch (Exception error) { Failed(error); return false; }
        }

        internal async Task<bool> StepAsync(Func<Task> action)
        {
            if (_halt) return false;
            try { await action(); return true; }
            catch (Exception error) { Failed(error); return false; }
        }

        private void Failed(Exception error)
        {
            _error ??= error;
            // Lost authority, cancellation or damaged state halts dependent writes,
            // but cannot retroactively erase effects already counted by the executor.
            _halt |= !RunFailure(error, ct);
            RunLog.Trace("Run completion step failed: " + error.ToString());
        }

        internal RunReport Finish(RunReport report)
        {
            if (_error is null) return report;
            // One stable pair-scoped notice/retry, regardless of the number of failed steps.
            try { run.RecordUnavailable(pairId, _error); }
            catch (Exception noticeError) { RunLog.Trace("Completion notice unavailable: " + noticeError.ToString()); }
            return report with { Outcome = _error is PairPausedException ? RunOutcomes.Paused :
                ct.IsCancellationRequested ? RunOutcomes.Stopped : RunOutcomes.Partial, CompletionFailed = true };
        }
    }
}
