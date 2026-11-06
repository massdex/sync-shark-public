namespace Massdex.SyncShark.Engine;

/// <summary>
/// Frozen member evidence and independently versioned effect, follow-up and recovery progress.
/// A stage allocation is only a locator; Stage carries ownership proof. EffectResult records
/// deletion evidence, while Result records recovered publication, neither implying root completion.
/// </summary>
public sealed record FolderRemovalItem(
    string OperationId,
    long Ordinal,
    long Generation,
    FolderRemovalItem.ProvenanceKind Provenance,
    string Path,
    string PolicyKey,
    Entry? Expected,
    SyncedEntry? Saved,
    FolderVerdict Verdict,
    bool FollowUp,
    FolderRemovalItem.FollowUpStatus FollowUpState,
    bool SelectedBranch,
    FolderEffectKind EffectKind,
    FolderRemovalItem.EffectStatus EffectState,
    long EffectGeneration,
    string? SubmissionId,
    int? SubmissionMember,
    FolderMemberResult? EffectResult,
    FolderRemovalItem.RecoveryPhase Phase,
    Entry? Selected,
    StageAllocation? StageAllocation,
    StageProof? Stage,
    Entry? Result,
    TextKey? Cause)
{
    /// <summary>Settled ends a member's recovery: Storage refuses any transition out of it.</summary>
    internal bool IsTerminal => Phase == RecoveryPhase.Settled;

    internal bool IsTerminalDiscard => Phase == RecoveryPhase.DiscardArmed && Cause == TextKey.RecoveryAbandonIncomingPending && Stage is not null;

    /// <summary>Persists explicit abandonment separately from retryable automatic stage cleanup.</summary>
    internal FolderRemovalItem BeginTerminalDiscard() => this with
    { Phase = RecoveryPhase.DiscardArmed, Cause = TextKey.RecoveryAbandonIncomingPending };

    /// <summary>Retires recovery custody without changing the evidence for an unverified effect.</summary>
    internal FolderRemovalItem SettledRecovery()
    {
        var verified = EffectState == EffectStatus.Verified;
        return this with
        {
            Phase = RecoveryPhase.Settled, Stage = null, StageAllocation = null,
            EffectState = verified ? EffectStatus.Settled : EffectState,
            EffectGeneration = verified ? checked(EffectGeneration + 1) : EffectGeneration
        };
    }

    /// <summary>Origin of a known path, not proof that its contents were deleted.</summary>
    public enum ProvenanceKind
    {
        Planned,
        Unexpected,
        BlockingParent
    }

    /// <summary>Progress of fresh survivor reconciliation, separate from provider effects.</summary>
    public enum FollowUpStatus
    {
        None,
        Pending,
        Done
    }

    /// <summary>Certainty and settlement of the member's selected effect.</summary>
    public enum EffectStatus
    {
        None,
        Ready,
        Armed,
        Accepted,
        Verified,
        NoEffect,
        Unknown,
        Settled
    }

    /// <summary>Durable recovery ordering, independent of the operation's aggregate phase.</summary>
    public enum RecoveryPhase
    {
        Planned,
        DeleteArmed,
        Deleted,
        RecoverySelected,
        AllocateArmed,
        StageArmed,
        Staged,
        PublishArmed,
        Published,
        Conflict,
        Unknown,
        DiscardArmed,
        Settled
    }
}
