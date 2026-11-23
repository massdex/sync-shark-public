namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>UploadContent operations for the dropbox module.</summary>
public sealed class UploadContent
{
    /// <summary>A selected folder must use a completed upload session before its removal, even for a sole small file. Other sole small uploads may keep the source open.</summary>
    public Task<DropboxStaged> StageAsync(DropboxApi api, string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct) => throw new NotImplementedException();
}
