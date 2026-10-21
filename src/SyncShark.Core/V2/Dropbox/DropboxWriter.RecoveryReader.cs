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
}
