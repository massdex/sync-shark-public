namespace Massdex.SyncShark.V2;

internal static class EvidencePendingNotice
{
    internal static Notice Create(string pairId, string path, string reason) =>
        FailureRetryOwner.RetryNotice(pairId, path, reason) with
        { Rule = 4, ReasonKey = PlanNotices.DeletionEvidencePending.Key };
}
