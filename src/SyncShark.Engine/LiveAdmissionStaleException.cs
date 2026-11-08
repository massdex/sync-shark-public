namespace Massdex.SyncShark.Engine;

/// <summary>Signals that a live admission went stale before its effect (LIVE 4).</summary>
internal sealed class LiveAdmissionStaleException(LiveAdmissionStaleReason reason, string? detail = null) : Exception
{
    /// <summary>The reason the admission no longer authorizes its effect.</summary>
    internal LiveAdmissionStaleReason Reason { get; } = reason;

    /// <summary>The evidence that made it stale, such as the blocking ancestor key; null when the reason names it.</summary>
    internal string? Detail { get; } = detail;

    internal (WorkSelection Selection, Side Side)? AncestorBlocker { get; init; }
}
