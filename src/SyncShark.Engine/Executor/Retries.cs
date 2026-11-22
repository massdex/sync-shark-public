namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retries operations for the executor module.</summary>
public sealed class Retries
{
    /// <summary>For FailPutBack retry the identified version create-only using the retained input and binding; never guess a version or overwrite an occupant.</summary>
    public Task RetryAsync(PairConfig pair, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
