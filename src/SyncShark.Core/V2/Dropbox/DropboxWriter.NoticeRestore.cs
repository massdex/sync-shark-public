namespace Massdex.SyncShark.V2;

public sealed partial class DropboxWriter : IRetainedNoticeReader
{
    async Task<Stream> IRetainedNoticeReader.OpenRetainedAsync(ItemState retained, CancellationToken ct)
    {
        if (retained.Kind != ItemKind.File || retained.VersionId is null || retained.Hash is null)
            throw new ArgumentException("A retained Dropbox version requires an exact file revision and hash.");
        var spool = NewSpool();
        try
        {
            await _client.DownloadHistoricalRevisionAsync(DropboxItems.ToEntry(retained), spool, ct)
                .ConfigureAwait(false);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
