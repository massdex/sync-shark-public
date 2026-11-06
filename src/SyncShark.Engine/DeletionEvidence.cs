namespace Massdex.SyncShark.Engine;

/// <summary>One exact target's deletion evidence: a non-destructive failure, a live location, or a substantiated
/// deletion. Variants are distinct outcomes, never boolean flags, so a listing flag or name match alone cannot
/// construct a deletion. Only a deletion variant can reach a removal decision, and only after the current-identity,
/// scope and freshness owners answered.</summary>
public abstract record DeletionEvidence(string Path)
{
    /// <summary>A specific identity, mapping, reply-shape or current-fact failure after no other variant applies.
    /// Cancellation propagates as an exception, never as this variant.</summary>
    public sealed record Unknown(string Path, TextKey Cause) : DeletionEvidence(Path);

    /// <summary>History stayed unreadable after the ordinary retry owner finished. Non-destructive; it never infers
    /// that the file never existed.</summary>
    public sealed record HistoryUnavailable(string Path, TextKey Cause) : DeletionEvidence(Path);

    /// <summary>The documented path-mode revision listing answered path/not_found: a successful history negative,
    /// not a generic miss and not a deletion.</summary>
    public sealed record NoRetainedHistory(string Path) : DeletionEvidence(Path);

    /// <summary>The shared identity owner proved a live incarnation at the exact target.</summary>
    public sealed record LiveAtTarget(string Path, IdentityLocation Location) : DeletionEvidence(Path);

    /// <summary>The shared identity owner proved a live incarnation elsewhere; its location is preserved intact, even
    /// outside the current pair.</summary>
    public sealed record LiveElsewhere(string Path, IdentityLocation Location) : DeletionEvidence(Path);

    /// <summary>A deleted file substantiated by coherent head history and a proved-null known-ID lookup. Exactly one of
    /// DeletedAt/TimeUnavailable and one of ContentHash/HashUnavailable is set, each unavailable reason naming that
    /// field's own failure so a coherent remainder stays usable.</summary>
    public sealed record DeletedFile(
        string Path, string Domain, string RemoteId, string? Revision,
        DateTimeOffset? DeletedAt, TextKey? TimeUnavailable,
        string? ContentHash, TextKey? HashUnavailable) : DeletionEvidence(Path)
    {
        public string Domain { get; init; } = RequireValue(Domain, nameof(Domain));

        public string RemoteId { get; init; } = RequireValue(RemoteId, nameof(RemoteId));

        public DateTimeOffset? DeletedAt { get; init; } = RequireDeletionTime(DeletedAt, TimeUnavailable);

        public string? ContentHash { get; init; } = RequireContentHash(ContentHash, HashUnavailable);

        private static string RequireValue(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(null, name) : value;

        private static DateTimeOffset? RequireDeletionTime(DateTimeOffset? deletedAt, TextKey? unavailable)
        {
            if ((deletedAt is null) == (unavailable is null))
                throw new ArgumentException(null, nameof(DeletedAt));
            if (unavailable is { } reason && reason is not (TextKey.DeletionTimeMissing
                    or TextKey.DeletionTimeMalformed or TextKey.DeletionTimeUnavailable))
                throw new ArgumentException(null, nameof(TimeUnavailable));
            return deletedAt;
        }

        private static string? RequireContentHash(string? contentHash, TextKey? unavailable)
        {
            if ((contentHash is null) == (unavailable is null))
                throw new ArgumentException(null, nameof(ContentHash));
            if (unavailable is { } reason && reason is not (TextKey.DeletionHashMissing
                    or TextKey.DeletionHashMalformed or TextKey.DeletionHashUnavailable))
                throw new ArgumentException(null, nameof(HashUnavailable));
            if (contentHash is not null &&
                !SyncContentIdentity.IdentityMatchesAlgorithm(SyncContentIdentity.DropboxBlockSha256, contentHash))
                throw new ArgumentException(null, nameof(ContentHash));
            return contentHash;
        }
    }

    /// <summary>A folder-only tombstone: the exact deleted path or a proper ancestor the path owner validated.
    /// It never authorizes recursive first-run removal.</summary>
    public sealed record DeletedPath(string Path, string TombstonePath) : DeletionEvidence(Path);

    /// <summary>A folder with no tombstone after every necessary exact and ancestor lookup returned a validated
    /// current answer.</summary>
    public sealed record NoFolderTombstone(string Path) : DeletionEvidence(Path);
}
