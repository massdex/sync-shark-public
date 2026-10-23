namespace Massdex.SyncShark.V2;

/// <summary>Reads the immutable Dropbox revision a Recovery row retains.</summary>
public sealed partial class DropboxWriter
{
    Task<ItemState> IRecoveryRecordReader.DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct) =>
        RecoveryRecordPreviews.DescribeDropboxAsync(_client, _pairId, _side, recovery, ct);

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
}
