namespace Massdex.SyncShark;

public sealed record SyncAccountPause(string ConnectionId, bool UntilResumed, DateTimeOffset? UntilUtc,
    string? TimeZoneId = null)
{
    public bool IsActive => IsActiveAt(DateTimeOffset.UtcNow);

    // The injected clock keeps the scope and expiry decision testable and independent of the
    // ambient clock, while IsActive retains its existing meaning for callers.
    public bool IsActiveAt(DateTimeOffset nowUtc) => PauseSchedule.IsPauseActive(UntilResumed, UntilUtc, nowUtc);
}
