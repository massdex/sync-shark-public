namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox reads and writes</summary>
public sealed class DropboxWriter
{
    /// <summary>Upload operation on the supplied facts.</summary>
    public Task<Entry> UploadAsync(DropboxApi api, IStaged stage, Entry? expected, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>UploadAsync for the selected DropboxWriter operation.</summary>
    public Task<RemovalResult> UploadAsync(PairConfig pair, Dispatcher dispatcher, DropboxStaged stage, Entry? expected, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Interpret the outcome for the selected job member.</summary>
    public static RemovalResult Classify(string route, JobMember member, Entry? expected, DropboxStaged? upload, AcceptedJob? accepted, JobReply reply) => throw new NotImplementedException();
}
