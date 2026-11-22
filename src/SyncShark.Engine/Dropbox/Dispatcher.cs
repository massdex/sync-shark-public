namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dispatcher operations for the dropbox module.</summary>
public sealed class Dispatcher
{
    /// <summary>Submit operation on the supplied facts.</summary>
    public Task<JobReply> SubmitAsync(string route, IReadOnlyList<JobMember> ready, CancellationToken ct) => throw new NotImplementedException();
}
