namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>UploadContent operations for the dropbox module.</summary>
public sealed class UploadContent
{
    /// <summary>Stage operation on the supplied facts.</summary>
    public Task<DropboxStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) => throw new NotImplementedException();
}
