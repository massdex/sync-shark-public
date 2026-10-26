namespace Massdex.SyncShark.V2;

/// <summary>One stored deletion-evidence probe result: the server deletion time, or null for "no evidence".</summary>
public sealed record DeletionEvidenceRecord(string Path, DateTimeOffset? DeletedUtc, string? BasisCursor = null,
    string? ContentHash = null, DeletionEvidenceFailure? Failure = null)
{
    // Decision 78: legacy positive evidence without a revision hash needs a fresh history probe.
    // Negative evidence (never existed) requires no hash.
    public bool HasRequiredContentHash => DeletedUtc is null || ContentHash is not null;
}
