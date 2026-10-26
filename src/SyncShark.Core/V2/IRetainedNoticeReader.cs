namespace Massdex.SyncShark.V2;

/// <summary>Reads an immutable retained provider revision, not the current path's contents.</summary>
internal interface IRetainedNoticeReader
{
    Task<Stream> OpenRetainedAsync(ItemState retained, CancellationToken ct);
}
