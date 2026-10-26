namespace Massdex.SyncShark.V2;

/// <summary>
/// What a Recovery-restore notice is allowed to hold. A pending notice protects a path only after the restore has a
/// frozen intent: bytes may already have been written and a later run must verify them. An action-less failure
/// (CONFLICT, PROTECTED, NO-VERSION, or FAILED before a write) is informational and holds nothing (finding 2).
/// </summary>
internal static class RecoveryRestoreHold
{
    internal const string PendingReason = "recovery-restore-pending";
    internal const string ReadyReason = "recovery-restore-ready";

    /// <summary>The restore's own create-only intent; its id is the durable rrs id and it cannot claim a baseline.</summary>
    internal static bool IsFrozenIntent(SyncAction action) =>
        action.ActionId.StartsWith("rrs", StringComparison.Ordinal) && action.SuppressReconciledBaseline;

    internal static bool HasFrozenIntent(ConflictNoticeOperation? operation) =>
        operation?.Actions.Any(IsFrozenIntent) == true;

    internal static bool Holds(Notice notice) =>
        notice.ReasonKey == PendingReason && HasFrozenIntent(notice.ConflictOperation);
}
