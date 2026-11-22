namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderMerge operations for the executor module.</summary>
public sealed class FolderMerge
{
    /// <summary>Pass both endpoints and the awaited population handoff into ordinary child recovery and source cleanup.</summary>
    public Task ApplyAsync(PairConfig pair, Entry moved, Entry destination, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
