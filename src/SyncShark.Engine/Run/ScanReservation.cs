namespace Massdex.SyncShark.Engine.Run;

/// <summary>The exclusive resource that owns the one disposable whole-pair scan of an initial real Repair
/// (LIVE 3.2). Only ScanOwnership constructs one; the operation that acquired it disposes it exactly once,
/// after every borrower has finished. It is resource ownership, not root approval or permission to skip a listing.</summary>
internal sealed class ScanReservation : IAsyncDisposable
{
    private readonly string[] _pairIds;
    private ScanOwnership? _owner;

    /// <summary>Binds the issuing ownership instance and the exact acquired pair IDs.</summary>
    internal ScanReservation(ScanOwnership owner, IReadOnlyList<string> pairIds)
    {
        _owner = owner;
        _pairIds = [.. pairIds];
    }

    /// <summary>Refuses a wrong owner, a pair outside the acquired set, or a disposed scope, without waiting.</summary>
    internal void Require(ScanOwnership owner, string pairId)
    {
        var current = Volatile.Read(ref _owner);
        if (current is null || !ReferenceEquals(current, owner) || Array.IndexOf(_pairIds, pairId) < 0)
            throw new InvalidOperationException();
    }

    /// <summary>Releases the acquired slots once; any later release is a no-op.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _owner, null)?.Release(_pairIds);
        return ValueTask.CompletedTask;
    }
}
