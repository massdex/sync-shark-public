namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderMerge operations for the executor module.</summary>
public sealed class FolderMerge
{
    /// <summary>Pass both endpoints and the awaited population handoff into ordinary child recovery and source cleanup.</summary>
    public Task ApplyAsync(PairConfig pair, PathScope paths, Entry moved, Entry destination, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
