namespace Massdex.SyncShark.V2;

/// <summary>One policy for planner observations and their canonical per-item retry notice.</summary>
internal static class RetryNoticeFold
{
    internal static bool SuppressIncoming(Notice notice, Func<bool> deletionHoldExists,
        Func<bool> canonicalRetryIsActionable, Action supersedePlannerObservation)
    {
        if (notice.ReasonKey is not ("content-unconfirmed" or "sync-retry")) return false;
        if (deletionHoldExists()) return true;
        var retryOpen = canonicalRetryIsActionable();
        if (notice.ReasonKey == "sync-retry" || retryOpen) supersedePlannerObservation();
        // Planner evidence cannot erase the executor's specific diagnosis, even when ignored.
        return notice.ReasonKey == "content-unconfirmed" && retryOpen;
    }
}
