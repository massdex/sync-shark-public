namespace Massdex.SyncShark.Engine.Executor;

/// <summary>KeepBoth operations for the executor module.</summary>
public sealed class KeepBoth
{
    /// <summary>Fill the fresh exclusive folder label through the awaited M4 population handoff; bind the completed leg, never re-list an abandoned label.</summary>
    public Task ApplyAsync(PairConfig pair, Row row, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
