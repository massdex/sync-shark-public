namespace Massdex.SyncShark.Engine.Executor;

/// <summary>FolderMerge operations for the executor module.</summary>
public sealed class FolderMerge
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, Entry moved, Entry destination, ISide side, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
