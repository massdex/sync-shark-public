namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dispatcher operations for the dropbox module.</summary>
public sealed class Dispatcher
{
    /// <summary>Accepts one member; groups concurrently ready writes by route, not create intent. Keep exclusiveCreate per waiting member: an existing folder may satisfy ensure, never exclusive creation. Persist only the name for accepted creates. On a returned token saves the shared launch once through IStore, then releases each caller with its own reply. A Running reply remains pending.</summary>
    public Task<(AcceptedJob? Accepted, JobReply MemberReply)> SubmitAsync(string pairId, string route, JobMember member, string? expectedRevision, bool exclusiveCreate, DropboxStaged? upload, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Writes through the single HTTP owner.</summary>
    public Dispatcher(DropboxApi api, IStore store, Func<AcceptedJob, Task>? installed = null) => throw new NotImplementedException();
    /// <summary>Submit one pair-bound batch member with its selected identity and create intent.</summary>
    public Task<(AcceptedJob? Accepted, JobReply MemberReply)> SubmitAsync(PairConfig pair, string route, JobMember member, string? expectedRevision, bool exclusiveCreate, DropboxStaged? upload, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Project one ordered member and its absence evidence into a singleton reply.</summary>
    public static JobReply.Terminal MemberReply(JobReply.Terminal batch, int ordinal) => throw new NotImplementedException();
    /// <summary>Resolve an ensure-folder clash for the selected member without resubmitting it.</summary>
    public Task<JobReply.Terminal> ResolveCreateClashAsync(PairConfig pair, JobMember member, JobReply.Terminal reply, CancellationToken ct) => throw new NotImplementedException();
}
