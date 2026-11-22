namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderDelete operations for the executor module.</summary>
public sealed class FolderDelete
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
