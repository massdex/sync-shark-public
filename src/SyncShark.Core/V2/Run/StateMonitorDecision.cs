namespace Massdex.SyncShark.V2;

/// <summary>Replay the retained history, independently of repairing any unknown scope.</summary>
internal sealed record StateMonitorDecision(string? ReplayCursor, ReconciliationScope? UnknownScope,
    ReconciliationTrigger Trigger, string? SavedCursor = null);
