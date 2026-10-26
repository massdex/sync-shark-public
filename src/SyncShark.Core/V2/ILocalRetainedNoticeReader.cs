namespace Massdex.SyncShark.V2;

internal interface ILocalRetainedNoticeReader
{
    Task<Stream> OpenRecoveryAsync(RecoveryRecord recovery, ItemState retained, CancellationToken ct);
}
