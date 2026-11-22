namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderMerge operations for the executor module.</summary>
public sealed class FolderMerge
{
    /// <summary>Pass both endpoints and the awaited population handoff into ordinary child recovery and source cleanup.</summary>
    public Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry moved, Entry destination, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>ApplyAsync for the selected FolderMerge operation.</summary>
    public Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry moved, Entry destination, Side written, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
