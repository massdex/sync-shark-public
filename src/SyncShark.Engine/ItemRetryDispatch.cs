namespace Massdex.SyncShark.Engine;

/// <summary>How a retained in-use item obligation is retried without a timer (section 12.1): ordinary item reobservation, scoped local branch, or exact pending-operation recovery.</summary>
public enum ItemRetryDispatch { OrdinaryItem, LocalBranch, LocalRecovery }
