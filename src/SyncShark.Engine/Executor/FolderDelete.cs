namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderDelete operations for the executor module.</summary>
public sealed class FolderDelete
{
    /// <summary>Dispatch a selected folder through ISide; propagate Pending and retain the binding until its member and evidence settle.</summary>
    public Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();
}
