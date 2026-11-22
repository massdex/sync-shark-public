namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dispatcher operations for the dropbox module.</summary>
public sealed class Dispatcher
{
    /// <summary>Sends ready requests of one kind. Only the returned token and selected member footprints survive the call.</summary>
    public Task<(AcceptedJob? Accepted, JobReply Reply)> SubmitAsync(string pairId, string route, IReadOnlyList<(JobMember Member, string? ExpectedRevision, DropboxStaged? Upload)> ready, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Writes through the single HTTP owner.</summary>
    public Dispatcher(DropboxApi api) => throw new NotImplementedException();
}
