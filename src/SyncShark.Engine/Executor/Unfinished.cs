namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Unfinished operations for the executor module.</summary>
public sealed class Unfinished
{
    /// <summary>Check operation on the supplied facts.</summary>
    public Task CheckAsync(PairConfig pair, PathScope paths, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
