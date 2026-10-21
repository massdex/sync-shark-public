namespace Massdex.SyncShark.V2;

/// <summary>
/// Reads the exact immutable bytes one durable Recovery row names, without restoring, uploading or deleting anything.
/// </summary>
/// <remarks>
/// A reader is bound to one endpoint root. It refuses a row that belongs to another pair (when the writer knows its
/// pair), the other side or another provider before any provider request. Describing reads bounded metadata without
/// downloading or hashing retained bytes. A confirmed local writer may repair its authenticated interrupted archive
/// access envelope first; dry-run facades use the mutation-free preview helpers instead. Opening describes again
/// and then uses the endpoint's existing verified read path, which hashes the bytes,
/// so a stale or missing retained version fails with an explanatory exception while every byte of user data stays put.
/// <para>
/// Exception classification for callers: <see cref="FileNotFoundException"/> and <see cref="InvalidDataException"/>
/// mean the retained version is gone, the row does not match it, or this writer does not own it. <see cref="IOException"/>
/// means the provider or Recovery location could not be read right now and the operation should be retried.
/// </para>
/// </remarks>
internal interface IRecoveryRecordReader
{
    /// <summary>
    /// The exact retained file the Recovery row names, at <see cref="RecoveryRecord.Path"/>, carrying the recorded
    /// hash, size and provider revision. No byte read, download or content write. Confirmed local writers may repair
    /// an authenticated archive access envelope; preview facades never repair it. Throws when ownership or availability fails.
    /// </summary>
    Task<ItemState> DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct);

    /// <summary>
    /// Opens the exact retained bytes for an actual apply after describing the row and verifying them through the
    /// provider's existing immutable-read path. The caller owns and disposes the returned stream.
    /// </summary>
    Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct);
}

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
}

/// <summary>
/// Read-only Recovery previews that do not construct or open a writable endpoint: no control-namespace or Recovery
/// folder allocation, no archive access repair, no byte hashing and no historical download.
/// </summary>
/// <remarks>
/// The production dry run calls these with the pair id/side and the side's already-known Recovery root or Dropbox
/// client, so it never opens a <see cref="LocalWriter"/>. Opening actual bytes stays on
/// <see cref="IRecoveryRecordReader.OpenRecoveryRecordAsync"/> with a real writer.
/// </remarks>
internal static class RecoveryRecordPreviews
{
    /// <summary>Describes one local archive using metadata reads only.</summary>
    internal static ItemState DescribeLocal(string pairId, Side writerSide, string recoveryRoot,
        RecoveryRecord recovery, CancellationToken ct)
    {
        RecoveryRecordReads.RequireComplete(recovery);
        RecoveryRecordReads.RequireOwnership(pairId, recovery.PairId, writerSide, recovery.Side);
        if (!string.IsNullOrEmpty(recovery.VersionId))
            throw new InvalidDataException(
                "This Recovery record is not a local archive reference; nothing was read or changed.");
        ct.ThrowIfCancellationRequested();
        using var lease = PilotDirectoryLease.Open(recoveryRoot);
        var path = SyncLocalRecovery.BuildRecoveryPath(lease, pairId, LocalWriter.OperationId(recovery.ActionId));
        if (!IsExactLocalLocation(recovery.Location, path))
            throw new InvalidDataException(
                "The Recovery record is not bound to this pair's exact operation path; nothing was read or changed.");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The local Recovery copy is no longer available at its exact operation path; nothing was read or changed.",
                path);
        PilotDirectoryIdentity identity;
        long size;
        DateTime lastWriteUtc;
        try
        {
            // The archive's own NTFS identity is the retained source identity the record cannot store.
            identity = SyncLocalRecovery.ParseSourceIdentity(LocalWriter.DurableIdentity(path));
            var info = new FileInfo(path);
            size = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException(
                "The local Recovery copy's identity could not be verified; nothing was read or changed. " + error.Message,
                error);
        }
        if (size != recovery.Size)
            throw new InvalidDataException(
                "The local Recovery copy no longer matches the recorded size; nothing was read or changed.");
        return new ItemState(recovery.Path, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(identity.VolumeSerialNumber, identity.FileId), null, recovery.Hash,
            recovery.Size, new DateTimeOffset(lastWriteUtc), ItemFlags.None);
    }

    /// <summary>Describes one Dropbox revision from revision metadata only, without downloading its bytes.</summary>
    internal static async Task<ItemState> DescribeDropboxAsync(DropboxPilotClient client, string? pairId,
        Side writerSide, RecoveryRecord recovery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        RecoveryRecordReads.RequireComplete(recovery);
        RecoveryRecordReads.RequireOwnership(pairId, recovery.PairId, writerSide, recovery.Side);
        var revision = recovery.VersionId;
        if (string.IsNullOrEmpty(revision))
            throw new InvalidDataException(
                "This Recovery record is not a Dropbox revision reference; nothing was read or changed.");
        // Dropbox preserves a displaced revision in the victim's own path. A local archive record (an absolute
        // location) or a record for another spelling is not a Dropbox revision reference for this reader.
        if (Path.IsPathFullyQualified(recovery.Location) || !PairPath.Same(recovery.Location, recovery.Path))
            throw new InvalidDataException(
                "This Recovery record is not a Dropbox revision reference for this path; nothing was read or changed.");
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<DropboxRevisionEntry> revisions;
        try
        {
            revisions = await client.ListRevisionsAsync(recovery.Path, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (DropboxItems.MapAccessError(error) is var mapped && mapped != error)
        {
            throw mapped;
        }
        var retained = revisions.FirstOrDefault(entry =>
            string.Equals(entry.Revision, revision, StringComparison.Ordinal));
        if (retained is null)
            throw new FileNotFoundException(
                "The retained Dropbox revision is no longer in this file's version history; nothing was read or changed.");
        if (!PairPath.Same(retained.RelativePath, recovery.Path) || string.IsNullOrWhiteSpace(retained.FileId))
            throw new InvalidDataException(
                "Dropbox did not return the retained revision for this exact path and identity; nothing was read or changed.");
        if (retained.Size != recovery.Size)
            throw new InvalidDataException(
                "The retained Dropbox revision no longer matches the recorded size; nothing was read or changed.");
        var hash = retained.ContentHash;
        if (string.IsNullOrEmpty(hash))
            throw new InvalidDataException(
                "Dropbox did not return a content hash for the retained revision, so its bytes cannot be verified; " +
                "nothing was read or changed.");
        if (!string.Equals(hash, recovery.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The retained Dropbox revision no longer matches the recorded content hash; nothing was read or changed.");
        return new ItemState(recovery.Path, ItemKind.File, retained.FileId, retained.Revision, hash, retained.Size,
            retained.ModifiedUtc ?? recovery.Created, ItemFlags.None);
    }

    internal static bool IsExactLocalLocation(string location, string expectedPath)
    {
        if (!Path.IsPathFullyQualified(location)) return false;
        try { return string.Equals(Path.GetFullPath(location), expectedPath, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}
