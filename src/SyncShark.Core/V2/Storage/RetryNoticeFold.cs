namespace Massdex.SyncShark.V2;

/// <summary>One policy for planner observations and their canonical per-item retry notice.</summary>
internal static class RetryNoticeFold
{
    internal static bool SuppressIncoming(Notice notice, Func<bool> deletionHoldExists,
        Func<int, string, Notice?> actionableNotice, Action<Notice> supersede)
    {
        if (notice.ReasonKey is not ("content-unconfirmed" or "sync-retry")) return false;
        if (deletionHoldExists()) return true;
        var same = actionableNotice(notice.Rule, notice.ReasonKey);
        if (same is not null && Specificity(same) > Specificity(notice)) return true;
        var other = notice.ReasonKey == "sync-retry"
            ? actionableNotice(30, "content-unconfirmed")
            : actionableNotice(40, "sync-retry");
        if (other is null) return false;
        // Stable cause evidence, not formatted detail or attempt counters, determines specificity.
        // A generic content-proof retry must not replace the content diagnosis. A concrete
        // executor failure still owns the planner observation, including when it is ignored.
        if (Specificity(other) >= Specificity(notice)) return true;
        supersede(other);
        return false;
    }

    private static int Specificity(Notice notice) => notice.ReasonKey == "content-unconfirmed" ? 1 :
        notice.CauseKey?.StartsWith("Content proof is unavailable", StringComparison.Ordinal) == true ? 0 : 2;
}
