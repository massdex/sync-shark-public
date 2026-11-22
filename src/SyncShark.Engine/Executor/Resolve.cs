namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Resolve operations for the executor module.</summary>
public sealed class Resolve
{
    /// <summary>Apply operation on the supplied facts.</summary>
    public Task ApplyAsync(PairConfig pair, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
