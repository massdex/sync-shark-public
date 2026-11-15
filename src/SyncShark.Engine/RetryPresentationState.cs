namespace Massdex.SyncShark.Engine;

/// <summary>How one issue's retry obligation presents (CV-RETRYVIEW, section 12.1): the exact eight states.
/// No RetryAfter and no parallel Run enum; the facts behind them come from the status query, not this type.</summary>
public enum RetryPresentationState { Running, Waiting, Offline, Paused, Disabled, RepairOnly, RecoveryRequired, Unavailable }
