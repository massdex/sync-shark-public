namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dispatcher operations for the dropbox module.</summary>
public sealed class Dispatcher
{
    /// <summary>Accepts one member; groups concurrently ready writes by route, not create intent. Keep exclusiveCreate per waiting member: an existing folder may satisfy ensure, never exclusive creation. Persist only the name for accepted creates. On a returned token saves the shared launch once through IStore, then releases each caller with its own reply. A Running reply remains pending.</summary>
    public Task<(AcceptedJob? Accepted, JobReply MemberReply)> SubmitAsync(string pairId, string route, JobMember member, string? expectedRevision, bool exclusiveCreate, DropboxStaged? upload, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Writes through the single HTTP owner.</summary>
    public Dispatcher(DropboxApi api, IStore store) => throw new NotImplementedException();
    /// <summary>Submit one pair-bound batch member with its selected identity and create intent.</summary>
    public Task<(AcceptedJob? Accepted, JobReply MemberReply)> SubmitAsync(PairConfig pair, string route, JobMember member, string? expectedRevision, bool exclusiveCreate, DropboxStaged? upload, CancellationToken ct) => throw new NotImplementedException();
}
