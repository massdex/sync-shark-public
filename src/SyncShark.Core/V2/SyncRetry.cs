namespace Massdex.SyncShark.V2;

/// <summary>A path that must be revisited even when its inventory cursor has advanced.</summary>
public sealed record SyncRetry(string Path, string Kind, int Attempts, string LastError,
    DateTimeOffset RetryAfter, PrepareMatch? Match = null, HeldDeletion? Held = null,
    IReadOnlyList<SyncAction>? ReviewedActions = null, bool HeldReviewed = false,
    IReadOnlyList<SyncAction>? ProposedActions = null, ItemState? HeldExpected = null,
    IReadOnlyList<string>? ReadRoots = null, Notice? PendingNotice = null, bool ReprobeEvidence = false, bool EvidenceReleased = false)
{
    /// <summary>Display-only deletion hold; never supplies deletion authority or changes retry ownership.</summary>
    public string? DeleteBlockedDetail { get; init; }
    public bool DeleteBlockedDeferred { get; init; }
    public bool MissingDeleteIdentityReview { get; init; }
    public FailureRetryOwner.FailureKind? FailureKind { get; init; }
}
