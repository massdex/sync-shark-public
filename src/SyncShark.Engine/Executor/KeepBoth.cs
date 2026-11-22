namespace Massdex.SyncShark.Engine.Executor;

/// <summary>KeepBoth operations for the executor module.</summary>
public sealed class KeepBoth
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, Row row, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
