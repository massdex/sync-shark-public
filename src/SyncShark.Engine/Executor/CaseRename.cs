namespace Massdex.SyncShark.Engine.Executor;

/// <summary>CaseRename operations for the executor module.</summary>
public sealed class CaseRename
{
    /// <summary>For each physical hop with unproved coverage, await the old-binding comparison with the observed before/after endpoints before taking the next leg. A covered hop requires no new listing.</summary>
    public Task<RemovalResult> MoveAsync(ISide side, Entry expected, string destination, string suffix, SyncedEntry savedBinding, Scope currentEndpoints, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
