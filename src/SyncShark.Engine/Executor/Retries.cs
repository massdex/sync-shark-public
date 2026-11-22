namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retries operations for the executor module.</summary>
public sealed class Retries
{
    /// <summary>Retry operation on the supplied facts.</summary>
    public Task RetryAsync(PairConfig pair, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
