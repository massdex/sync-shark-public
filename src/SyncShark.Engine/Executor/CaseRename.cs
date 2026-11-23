namespace Massdex.SyncShark.Engine.Executor;

/// <summary>CaseRename operations for the executor module.</summary>
public sealed class CaseRename
{
    /// <summary>Await the Run handoff between physical legs with the saved logical binding and observed before/after endpoints. It checks coverage; an already covered hop is not listed again.</summary>
    public Task<RemovalResult> MoveAsync(ISide side, Entry expected, string destination, string suffix, SyncedEntry savedBinding, Scope currentEndpoints, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
