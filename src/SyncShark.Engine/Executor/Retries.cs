namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retries operations for the executor module.</summary>
public sealed class Retries
{
    /// <summary>Retry operation on the supplied facts.</summary>
    public Task RetryAsync(PairConfig pair, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, CancellationToken ct) => throw new NotImplementedException();
}
