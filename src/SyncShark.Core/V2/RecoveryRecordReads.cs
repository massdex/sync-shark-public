namespace Massdex.SyncShark.V2;

/// <summary>Shared completeness and ownership checks for one writer reading a durable Recovery row.</summary>
internal static class RecoveryRecordReads
{
    /// <summary>Rejects an incomplete row before it can reach a provider or a computed archive path.</summary>
    internal static void RequireComplete(RecoveryRecord recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (string.IsNullOrWhiteSpace(recovery.PairId) || string.IsNullOrWhiteSpace(recovery.ActionId) ||
            string.IsNullOrWhiteSpace(recovery.Path) || string.IsNullOrWhiteSpace(recovery.Location) ||
            string.IsNullOrWhiteSpace(recovery.Hash) || recovery.Size < 0)
            throw new InvalidDataException("The Recovery record is incomplete; nothing was read or changed.");
    }

    /// <summary>
    /// Rejects a row owned by another pair (when the writer knows its pair) or by the other side. The pair check is
    /// skipped only when the writer has no pair binding; a bound writer never reads another pair's copy.
    /// </summary>
    internal static void RequireOwnership(string? writerPairId, string recordPairId, Side writerSide,
        Side recordSide)
    {
        if (writerPairId is not null && !string.Equals(writerPairId, recordPairId, StringComparison.Ordinal))
            throw new InvalidDataException("The Recovery record belongs to another pair; nothing was read or changed.");
        if (writerSide != recordSide)
            throw new InvalidDataException(
                "The Recovery record belongs to the other side of the pair; nothing was read or changed.");
    }

    /// <summary>Rejects a row that is not a Dropbox revision reference for its exact path, before any request.</summary>
    internal static void RequireDropboxRevisionShape(RecoveryRecord recovery)
    {
        if (string.IsNullOrEmpty(recovery.VersionId))
            throw new InvalidDataException(
                "This Recovery record is not a Dropbox revision reference; nothing was read or changed.");
        if (Path.IsPathFullyQualified(recovery.Location) || !PairPath.Same(recovery.Location, recovery.Path))
            throw new InvalidDataException(
                "This Recovery record is not a Dropbox revision reference for this path; nothing was read or changed.");
    }

    /// <summary>
    /// Whether already-described metadata is the exact retained object a durable Recovery row names. Only the fields
    /// the row stores (path, size, content hash and provider revision) are compared. A mismatch is never trusted:
    /// the provider refuses instead of opening a supplied identity that the durable row does not name.
    /// </summary>
    internal static bool MatchesDescribed(RecoveryRecord recovery, ItemState described) =>
        described.Kind == ItemKind.File &&
        PairPath.Same(described.Path, recovery.Path) &&
        !string.IsNullOrEmpty(described.Hash) &&
        Content.SameContent(described.Size, described.Hash, recovery.Size, recovery.Hash) == true &&
        (string.IsNullOrEmpty(recovery.VersionId)
            ? described.VersionId is null
            : string.Equals(described.VersionId, recovery.VersionId, StringComparison.Ordinal));
}
