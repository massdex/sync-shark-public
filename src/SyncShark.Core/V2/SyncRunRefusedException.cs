namespace Massdex.SyncShark.V2;

/// <summary>
/// A run was refused before any effect: the pair is unknown, off, its first sync is not applied, or its saved
/// layout may not coexist with another pair.
/// </summary>
public sealed class SyncRunRefusedException : Exception
{
    /// <summary>A refusal described by text only.</summary>
    public SyncRunRefusedException(string reason) : base(reason)
    {
    }

    /// <summary>A refusal with a stable reason (key and wording), which continuous sync shows as the hold reason.</summary>
    public SyncRunRefusedException(NoticeReason reason) : base(reason.Detail) => Reason = reason;

    /// <summary>The stable reason, or null for a text-only refusal.</summary>
    public NoticeReason? Reason { get; }
}
