namespace Massdex.SyncShark.Engine;

/// <summary>The classified effect of one Delete or Publish (C-10). Callers read Outcome, never void return or path absence;
/// only a verified permitted removal may authorize publication or deletion settlement. On NoEffect, Retained, Unknown or
/// WrongRestored, Final names only a positively verified restored or current entry and authorizes no baseline or forget.
/// An accepted nonterminal Dropbox operation returns Pending; no binding or deletion is settled from that result.</summary>
public sealed record RemovalResult(RemovalOutcome Outcome, Entry? Final, TextKey? Cause)
{
    /// <summary>Shared acceptance saved once by Dispatcher before releasing a member.</summary>
    public AcceptedJob? Accepted { get; init; }
    /// <summary>Only this member's attributed observation, never a neighbour's receipt.</summary>
    public JobReply? Reply { get; init; }
    /// <summary>The journal position of the last record a synchronous Local publication wrote for Final, or null when
    /// none is proved; the settlement stores it so the dirty owner can recognize this write's own echo.</summary>
    public JournalPosition? LocalPublished { get; init; }

    /// <summary>The complete selected Local content from a definite no-effect refusal; never durable authority.</summary>
    public Entry? ObservedLocal { get; init; }

    /// <summary>The recycled folder's contents at their original paths, read lazily from the shell's
    /// receipt; null when no folder receipt exists.</summary>
    public IAsyncEnumerable<Listed>? Recycled { get; init; }

    /// <summary>Observed identity and binding of the actual bin item, including a substitute. Never persisted as a receipt or used as proof of the selected victim.</summary>
    public IdentityLocation? RecycledVictim { get; init; }
    /// <summary>The failure's safe provider fact for the log: a Windows HRESULT here, a Dropbox tag elsewhere.</summary>
    public string? Provider { get; init; }

    internal static Entry? CompleteObservation(Entry selected, string? hash) =>
        hash is null ? null : selected with { ContentHash = hash };

    internal static RemovalResult Refused(ChangedUnderneathException refusal) =>
        new(RemovalOutcome.NoEffect, null, TextKey.FailChanged) { ObservedLocal = refusal.ObservedLocal };
}
