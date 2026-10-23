namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private void WriteParkedWorkNotice(string pairId, string path, string reason, int attempts,
        SyncAction? conflictSuccessor, bool pendingEvidence, bool evidenceUnavailable)
    {
        if (conflictSuccessor is not null)
            WriteNotices([ConflictCopyBlockedNotice(pairId, conflictSuccessor, reason)]);
        else if (pendingEvidence && !evidenceUnavailable)
            WriteNotices([EvidencePendingNotice.Create(pairId, path, reason)]);
        else if (reason != DropboxQuota.Detail && !(evidenceUnavailable && pendingEvidence))
            WriteNotices([FailureRetryOwner.RetryNotice(pairId, path, reason, attempts)]);
    }
}
