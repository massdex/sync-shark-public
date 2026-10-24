namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static Dictionary<string, ActionResult> FailureReasons(Plan plan, Tally tally)
    {
        var failed = tally.Failures.GroupBy(result => result.ActionId).ToDictionary(group => group.Key, group => group.Last());
        var reasons = new Dictionary<string, ActionResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan.Actions)
            if (tally.WasQuotaBlocked(action.ActionId)) reasons[action.Path] = new(action.ActionId, Outcome.Failed, null, null, null, DropboxQuota.Detail);
            else if (failed.GetValueOrDefault(action.ActionId) is { } reason) reasons[action.Path] = reason;
        return reasons;
    }

    private FailureRetryOwner.State ParkedFailureState(SyncRetry? prior, ActionResult? reason, bool evidenceUnavailable) =>
        FailureRetryOwner.NotBefore(FailureRetryOwner.ReportedFailure(prior, _time.GetUtcNow(), retainSleeping: true,
            countAttempt: !evidenceUnavailable), reason?.RetryNotBefore);
}
