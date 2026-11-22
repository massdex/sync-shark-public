namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Unfinished operations for the executor module.</summary>
public sealed class Unfinished
{
    /// <summary>Check operation on the supplied facts.</summary>
    public Task CheckAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
