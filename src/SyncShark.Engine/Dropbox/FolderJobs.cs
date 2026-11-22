namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>FolderJobs operations for the dropbox module.</summary>
public sealed class FolderJobs
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<JobReply> ReadAsync(DropboxApi api, AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();
}
