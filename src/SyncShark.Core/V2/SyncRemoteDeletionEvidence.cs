namespace Massdex.SyncShark;

/// <summary>
/// Provider evidence for an exact remote path's current deletion. A null timestamp is not
/// deletion authority; Reason explains unsupported, unavailable or inconclusive evidence.
/// </summary>
public sealed record SyncRemoteDeletionEvidence(DateTimeOffset? DeletedUtc, string Reason,
    bool LookupFailed = false, string? ContentHash = null, bool PermanentFailure = false, bool Answered = false);
