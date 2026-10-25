namespace Massdex.SyncShark.V2;

/// <summary>Durable due markers for explicit per-entity storage retries.</summary>
internal static class StorageDue
{
    // An explicit Retry of one entity. Negative so ordinary due<=$now admission includes it, and distinct
    // from normal scheduling so only this exact row may bypass a pair-level root backoff.
    internal const long UserRequested = -1;
    // A claimed row parks here while one worker performs the effect. This is the crash-retry delay, not an
    // exclusion guarantee: a concurrent Request may overwrite it.
    internal const long ClaimLeaseMs = 60_000;
}
