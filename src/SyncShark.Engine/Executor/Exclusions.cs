namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Exclusions operations for the executor module.</summary>
public sealed class Exclusions
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
