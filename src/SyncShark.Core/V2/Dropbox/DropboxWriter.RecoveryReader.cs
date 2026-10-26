namespace Massdex.SyncShark.V2;

/// <summary>Reads the immutable Dropbox revision a Recovery row retains.</summary>
public sealed partial class DropboxWriter
{
    Task<ItemState> IRecoveryRecordReader.DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct) =>
        DropboxWriter.DescribeRecoveryDropboxPreviewAsync(_client, _pairId, _side, recovery, ct);

    async Task<Stream> IRecoveryRecordReader.OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct)
    {
        var retained = await ((IRecoveryRecordReader)this).DescribeRecoveryAsync(recovery, ct).ConfigureAwait(false);
        return await ((IRetainedNoticeReader)this).OpenRetainedAsync(retained, ct).ConfigureAwait(false);
    }

    Task<Stream> IRecoveryRecordReader.OpenRecoveryRecordAsync(RecoveryRecord recovery, ItemState described,
        CancellationToken ct)
    {
        // Reuse the already-described revision only for this one operation. Re-validate the durable row's ownership
        // and exact revision shape, and require the supplied path/size/hash/revision to match it; the pinned
        // historical download then proves the bytes and their identity again. Nothing is cached.
        RecoveryRecordReads.RequireComplete(recovery);
        RecoveryRecordReads.RequireOwnership(_pairId, recovery.PairId, _side, recovery.Side);
        RecoveryRecordReads.RequireDropboxRevisionShape(recovery);
        if (!RecoveryRecordReads.MatchesDescribed(recovery, described))
            throw new InvalidDataException(
                "The supplied Recovery metadata does not match the durable row; nothing was read or changed.");
        return ((IRetainedNoticeReader)this).OpenRetainedAsync(described, ct);
    }

    /// <summary>Describes one Dropbox revision from revision metadata only, without downloading its bytes.</summary>
    internal static async Task<ItemState> DescribeRecoveryDropboxPreviewAsync(DropboxPilotClient client, string? pairId,
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

}
