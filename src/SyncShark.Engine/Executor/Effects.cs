namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Effects operations for the executor module.</summary>
public sealed class Effects
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, Planner.SyncAction action, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
