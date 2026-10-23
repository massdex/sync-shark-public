namespace Massdex.SyncShark.V2;

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
        // Dropbox preserves a displaced revision in the victim's own path. A local archive record (an absolute
        // location) or a record for another spelling is not a Dropbox revision reference for this reader.
        RecoveryRecordReads.RequireDropboxRevisionShape(recovery);
        var revision = recovery.VersionId;
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
        if (Content.SameHash(hash, recovery.Hash) != true)
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
