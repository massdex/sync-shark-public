namespace Massdex.SyncShark.V2;

/// <summary>
/// A source endpoint that can write one exact planned revision directly into a caller's stream instead of
/// materialising it in a product spool first. The bytes are verified against the planned revision, content
/// hash and size while they are written.
/// </summary>
public interface ISyncDirectDownloadSource
{
    Task<SyncDownloadResult> DownloadToAsync(ItemState s, Stream destination, CancellationToken ct);
}
