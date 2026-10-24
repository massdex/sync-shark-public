namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static Dictionary<string, ActionResult> FailureReasons(Plan plan, Tally tally)
    {
        var failed = tally.Failures.Where(result => result.RejectedDuplicatePath is null).GroupBy(result => result.ActionId).ToDictionary(group => group.Key, group => group.Last());
        var reasons = new Dictionary<string, ActionResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan.Actions)
            if (tally.WasQuotaBlocked(action.ActionId)) reasons[action.Path] = new(action.ActionId, Outcome.Failed, null, null, null, DropboxQuota.Detail);
            else if (failed.GetValueOrDefault(action.ActionId) is { } reason) reasons[action.Path] = reason;
        foreach (var (path, failure) in tally.DuplicateEffectFailures) reasons[path] = failure;
        return reasons;
    }

    private FailureRetryOwner.State ParkedFailureState(SyncRetry? prior, ActionResult? reason, bool suppressAttempt) =>
        FailureRetryOwner.NotBefore(FailureRetryOwner.ReportedFailure(prior, _time.GetUtcNow(), retainSleeping: true,
            countAttempt: !suppressAttempt), reason?.RetryNotBefore);
    internal sealed partial class Tally
    {
        private void RecordFailureResult(SyncAction? action, ActionResult result)
        {
            RecordDuplicateEffectFailure(action, result);
            if (result.Outcome is Outcome.Failed or Outcome.Uncertain && Failures.Count < _failureSampleLimit)
                Failures.Add(result);
            // A skipped effect is still unfinished, but it is not a failure: keep its bounded reason in the
            // separate skip histogram so deferred work is explained without inflating the failure counts.
            if (result.Outcome is Outcome.Failed or Outcome.Uncertain)
                RunInstrumentation.Current?.RecordFailure(result.Reason);
            else if (result.Outcome is Outcome.Skipped or Outcome.Refused)
                RunInstrumentation.Current?.RecordSkip(result.Reason);
        }
    }
}
