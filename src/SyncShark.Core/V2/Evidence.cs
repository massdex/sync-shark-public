namespace Massdex.SyncShark.V2;

/// <summary>Deletion and completeness evidence for one joined row; DeletedAt* is the deletion time when known, and
/// DeletedHash* is the last Dropbox revision's content hash when the history supplies one.</summary>
public sealed record Evidence(bool DeletedA, bool DeletedB, bool KnownA, bool KnownB,
    DateTimeOffset? DeletedAtA = null, DateTimeOffset? DeletedAtB = null,
    string? DeletedHashA = null, string? DeletedHashB = null)
{
    // Exact observations satisfy only no-baseline creation, never deletion or Forget coverage.
    public bool CreationObservedA { get; init; }
    public bool CreationObservedB { get; init; }
}
