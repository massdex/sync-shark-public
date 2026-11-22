namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox reads and writes</summary>
public sealed class DropboxWriter
{
    /// <summary>Upload operation on the supplied facts.</summary>
    public Task<Entry> UploadAsync(DropboxApi api, IStaged stage, Entry? expected, CancellationToken ct) => throw new NotImplementedException();
}
