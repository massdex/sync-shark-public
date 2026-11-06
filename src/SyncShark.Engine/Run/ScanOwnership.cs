namespace Massdex.SyncShark.Engine.Run;

/// <summary>The one product-state-lifetime owner of the disposable whole-pair scan of an initial real
/// Repair (LIVE 3.2). The host composition creates one instance and injects it into every pair session as
/// PairSession.Scans; only that Repair enters it, and a configuration change drains its waiters outside any
/// resource lock. It is scan ownership, not a Monitor mutex, root approval or permission to skip a listing.</summary>
internal sealed class ScanOwnership
{
    private readonly object gate = new();
    private readonly HashSet<string> held = new(StringComparer.Ordinal);
    private readonly HashSet<TaskCompletionSource> waiters = new();

    /// <summary>Waits for the exclusive slot of every exact pair, acquiring sorted distinct IDs in that order
    /// and releasing any acquired prefix on cancellation; returns the typed reservation that the acquiring
    /// operation disposes exactly once after all borrowers finish.</summary>
    internal async ValueTask<ScanReservation> EnterAsync(IReadOnlyList<string> pairIds,
        Func<bool> acceptGrant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(acceptGrant);
        var owned = pairIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var acquired = new List<string>(owned.Length);
        try
        {
            foreach (var pairId in owned)
            {
                await AcquireAsync(pairId, acquired, ct).ConfigureAwait(false);
                if (!acceptGrant())
                {
                    ct.ThrowIfCancellationRequested();
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                }
            }
            return new ScanReservation(this, acquired);
        }
        catch
        {
            Release([.. acquired]);
            throw;
        }
    }

    /// <summary>Frees the acquired slots and wakes waiters to retry; owns no decision about whether a scan may run.</summary>
    internal void Release(string[] pairIds)
    {
        lock (gate)
        {
            held.ExceptWith(pairIds);
            foreach (var waiter in waiters) waiter.TrySetResult();
            waiters.Clear();
        }
    }

    private async Task AcquireAsync(string pairId, List<string> acquired, CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource waiter;
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!held.Contains(pairId))
                {
                    held.Add(pairId);
                    acquired.Add(pairId);
                    return;
                }
                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add(waiter);
            }
            try
            {
                await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                lock (gate) waiters.Remove(waiter);
                throw;
            }
        }
    }
}
