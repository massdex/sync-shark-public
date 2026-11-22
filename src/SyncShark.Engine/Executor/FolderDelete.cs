namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderDelete operations for the executor module.</summary>
public sealed class FolderDelete
{
    /// <summary>Dispatch a selected folder through ISide; propagate Pending and retain the binding until its member and evidence settle.</summary>
    public Task ApplyAsync(PairConfig pair, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
