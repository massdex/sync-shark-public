namespace Massdex.SyncShark.Engine.Run;

/// <summary>One operation token and its exact pair grants; only whole-Repair grants are exclusive.
/// Only ScanOwnership constructs one; the operation that acquired it disposes it exactly once,
/// after every borrower has finished. It is resource ownership, not root approval or permission to skip a listing.</summary>
internal sealed class ScanReservation : IAsyncDisposable
{
    private readonly string[] _pairIds;
    private ScanOwnership? _owner;
    private readonly bool _exclusive;
    internal Guid ScanId { get; } = ResourceNames.NewScanToken();

    /// <summary>Binds the issuing ownership instance and the exact acquired pair IDs.</summary>
    internal ScanReservation(ScanOwnership owner, IReadOnlyList<string> pairIds, bool exclusive)
    {
        _owner = owner;
        _exclusive = exclusive;
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
        var owner = Interlocked.Exchange(ref _owner, null);
        if (_exclusive) owner?.Release(_pairIds);
        return ValueTask.CompletedTask;
    }
}
