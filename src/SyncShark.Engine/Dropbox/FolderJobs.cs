namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>FolderJobs operations for the dropbox module.</summary>
public sealed class FolderJobs
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<JobReply> ReadAsync(DropboxApi api, AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>ReadAsync for the selected FolderJobs operation.</summary>
    public Task<JobReply> ReadAsync(DropboxApi api, PairConfig pair, AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Identify the check route for a launched job.</summary>
    public static string CheckRoute(string route) => throw new NotImplementedException();
}
