using System.Threading.Channels;

namespace Massdex.SyncShark.V2;

/// <summary>Delivers one batch's progress to the window in order, off the run worker, one report at a time.</summary>
/// <remarks>
/// The run posts without waiting; one reader awaits the window's callback. While the window is slow only the newest
/// pending report is kept, so the last report of the batch is always delivered. A failed delivery goes to
/// <c>failed</c> and the next report is still delivered. A window that never returns is abandoned at completion:
/// the batch is not held, and nothing more of this batch is delivered or reported.
/// </remarks>
internal sealed class ProgressDelivery
{
    private readonly Channel<SyncAuthorityBatchProgress> _pending = Channel.CreateBounded<SyncAuthorityBatchProgress>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _drained;
    // Admitting a delivery, publishing its error and abandoning are atomic with each other (never held across the
    // window's callback). A callback already admitted cannot be revoked; abandoning only stops new deliveries and
    // any error not yet published.
    private readonly object _gate = new();
    private bool _abandoned;

    internal ProgressDelivery(Func<SyncAuthorityBatchProgress, Task> deliver, Action<Exception> failed)
    {
        _drained = Task.Run(async () =>
        {
            await foreach (var progress in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_abandoned) return;
                }
                try
                {
                    await deliver(progress).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    lock (_gate)
                    {
                        if (!_abandoned) failed(error);
                    }
                }
            }
        });
    }

    /// <summary>Queues a report without blocking; an undelivered older report is replaced.</summary>
    internal void Post(SyncAuthorityBatchProgress progress) => _pending.Writer.TryWrite(progress);

    /// <summary>Accepts no more reports and waits up to <paramref name="timeout"/> for every queued report to be
    /// delivered; false when the window did not take them in time and the rest were dropped. After that, a later
    /// call only waits for the abandoned reader to exit.</summary>
    internal async Task<bool> CompleteAsync(TimeSpan timeout)
    {
        _pending.Writer.TryComplete();
        try
        {
            await _drained.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            lock (_gate) _abandoned = true;
            while (_pending.Reader.TryRead(out _)) { }
            return false;
        }
    }
}
