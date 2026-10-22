namespace Massdex.SyncShark.V2;

/// <summary>Reads the exact local Recovery archive a Recovery row retains.</summary>
public sealed partial class LocalWriter
{
    internal ItemState DescribeRecoveryReadOnly(RecoveryRecord recovery, CancellationToken ct) =>
        RecoveryRecordPreviews.DescribeLocal(_pairId, _side, _recoveryRoot, recovery, ct);

    Task<ItemState> IRecoveryRecordReader.DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct)
    {
        // This is the confirmed-operation writer, not the read-only preview facade. An interrupted archive
        // envelope denies even metadata access, so repair the exact owned envelope before describing it.
        // Preview calls RecoveryRecordPreviews.DescribeLocal directly and never enters this method.
        PrepareRecoveryRecordRead(recovery, ct);
        return Task.FromResult(RecoveryRecordPreviews.DescribeLocal(_pairId, _side, _recoveryRoot, recovery, ct));
    }

    async Task<Stream> IRecoveryRecordReader.OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct)
    {
        var retained = await ((IRecoveryRecordReader)this).DescribeRecoveryAsync(recovery, ct).ConfigureAwait(false);
        return await ((ILocalRetainedNoticeReader)this).OpenRecoveryAsync(recovery, retained, ct).ConfigureAwait(false);
    }

    async Task<Stream> IRecoveryRecordReader.OpenRecoveryRecordAsync(RecoveryRecord recovery, ItemState described,
        CancellationToken ct)
    {
        // Keep every confirmed-operation check, including ownership, the exact operation path and interrupted
        // archive ACL repair; only the metadata describe is skipped. The archive open re-verifies the retained
        // size/hash against the supplied state and its owned NTFS identity, so a mismatched row is never trusted.
        PrepareRecoveryRecordRead(recovery, ct);
        if (!RecoveryRecordReads.MatchesDescribed(recovery, described))
            throw new InvalidDataException(
                "The supplied Recovery metadata does not match the durable row; nothing was read or changed.");
        return await ((ILocalRetainedNoticeReader)this).OpenRecoveryAsync(recovery, described, ct)
            .ConfigureAwait(false);
    }

    private void PrepareRecoveryRecordRead(RecoveryRecord recovery, CancellationToken ct)
    {
        RecoveryRecordReads.RequireComplete(recovery);
        RecoveryRecordReads.RequireOwnership(_pairId, recovery.PairId, _side, recovery.Side);
        if (!string.IsNullOrEmpty(recovery.VersionId))
            throw new InvalidDataException(
                "This Recovery record is not a local archive reference; nothing was read or changed.");
        if (RecoveryUnavailable is { } unavailable)
            throw new IOException("Recovery location unavailable: " + unavailable);
        ct.ThrowIfCancellationRequested();
        using var lease = PilotDirectoryLease.Open(_recoveryRoot);
        var path = RecoveryPath(lease, recovery.ActionId);
        if (!RecoveryRecordPreviews.IsExactLocalLocation(recovery.Location, path))
            throw new InvalidDataException(
                "The Recovery record is not bound to this pair's exact operation path; nothing was read or changed.");
        SyncLocalRecovery.RestoreInterruptedArchiveAccess(path);
    }
}
