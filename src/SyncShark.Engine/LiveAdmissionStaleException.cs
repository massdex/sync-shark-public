namespace Massdex.SyncShark.Engine;

/// <summary>Signals that a live admission went stale before its effect (LIVE 4).</summary>
internal sealed class LiveAdmissionStaleException(LiveAdmissionStaleReason reason, string? detail = null) : Exception
{
    /// <summary>The reason the admission no longer authorizes its effect.</summary>
    internal LiveAdmissionStaleReason Reason { get; } = reason;

    /// <summary>The evidence that made it stale, such as the blocking ancestor key; null when the reason names it.</summary>
    internal string? Detail { get; } = detail;

    internal (WorkSelection Selection, Side Side)? AncestorBlocker { get; init; }

    /// <summary>The Local key whose in-use wait refused admission (ChangeTable.InUseBlocks); null for every other refusal.</summary>
    internal string? WaitKey { get; init; }

    /// <summary>B-205: which hold refused (ordinary-hold, episode-newer or subject-mismatch) with its work ids; null
    /// for every other refusal.</summary>
    internal string? HoldDiagnostic { get; init; }

    /// <summary>A contained content observation needing fresh admission within the same item attempt.</summary>
    internal Row? HandoffRow { get; init; }

    /// <summary>Set when the path that refused already logged this refusal; an outer catch must not report it twice.</summary>
    internal bool Reported { get; set; }
}
