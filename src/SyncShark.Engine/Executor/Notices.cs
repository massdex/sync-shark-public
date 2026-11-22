namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Notices operations for the executor module.</summary>
public sealed class Notices
{
    /// <summary>Raise operation on the supplied facts.</summary>
    public Task RaiseAsync(IStore store, string pairId, Notice notice, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Close operation on the supplied facts.</summary>
    public Task CloseAsync(IStore store, string pairId, Notice expected, CancellationToken ct) => throw new NotImplementedException();
}
