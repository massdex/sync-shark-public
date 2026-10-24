namespace Massdex.SyncShark;

// A private execution-batch lifetime, not a persisted account assertion. Adapters
// supply their exact instance/binding and the same token they will authorize.
// Folder checks may share only an unfinished exact HTTP verification. Completed
// folder checks, item identity, revisions, hashes and effects are never cached.
// Only a successfully completed verification is retained for reuse: a failed or
// canceled flight is dropped, so the next caller starts exactly one new attempt
// instead of carrying a transient refusal for the rest of the execution batch.
internal sealed class SyncProviderAccountVerificationScope : IAsyncDisposable
{
    private static readonly AsyncLocal<SyncProviderAccountVerificationScope?> Ambient = new();
    private readonly SyncProviderAccountVerificationScope? _previous;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly Dictionary<object, Dictionary<(string Binding, string Token), Task>> _flights =
        new(ReferenceEqualityComparer.Instance);
    private int _disposed;

    private SyncProviderAccountVerificationScope(CancellationToken ownerToken)
    {
        _previous = Ambient.Value;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        Ambient.Value = this;
    }

    internal static SyncProviderAccountVerificationScope Begin(CancellationToken ownerToken) => new(ownerToken);
    internal static SyncProviderAccountVerificationScope? Current =>
        Ambient.Value is { } scope && Volatile.Read(ref scope._disposed) == 0 ? scope : null;

    internal Task VerifyAsync(object adapter, string bindingIdentity, string exactToken,
        Func<CancellationToken, Task> verify, CancellationToken waiterToken, bool reuseCompleted = true)
    {
        waiterToken.ThrowIfCancellationRequested();
        Task flight;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!_flights.TryGetValue(adapter, out var generations))
                _flights.Add(adapter, generations = new());
            var key = (bindingIdentity, exactToken);
            // A failed or canceled flight must never refuse the rest of the batch: drop it so the next caller
            // verifies again. reuseCompleted:false drops any completed flight, including a successful one.
            if (generations.TryGetValue(key, out var prior) &&
                (prior.IsFaulted || prior.IsCanceled || (!reuseCompleted && prior.IsCompleted)))
                generations.Remove(key);
            if (!generations.TryGetValue(key, out flight!))
            {
                // Only a successfully completed task authorizes reuse. A failed
                // flight remains a refusal for this batch, never a success receipt.
                // The next batch/token must validate again. No unowned None task.
                try { flight = VerifyUnleasedAsync(verify); }
                catch (Exception error) { flight = Task.FromException(error); }
                generations.Add(key, flight);
            }
        }
        // Cancelling a single waiter does not cancel other admitted file workers.
        // Cancelling the owning batch does cancel the actual HTTP request.
        return flight.WaitAsync(waiterToken);
    }

    private async Task VerifyUnleasedAsync(Func<CancellationToken, Task> verify)
    {
        // A shared prerequisite belongs to the batch, never to its initiating waiter's capacity.
        // Clear both ambient owners only in this async flow; waiter cancellation cannot close a
        // lease the flight would later try to re-admit, nor release another worker's gate.
        var lease = SyncTransferResourceController.Lease.Current.Value;
        var staging = SyncEffectStaging.Current.Value;
        SyncTransferResourceController.Lease.Current.Value = null;
        SyncEffectStaging.Current.Value = null;
        try { await verify(_lifetime.Token).ConfigureAwait(false); }
        finally
        {
            SyncTransferResourceController.Lease.Current.Value = lease;
            SyncEffectStaging.Current.Value = staging;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task[] flights;
        lock (_gate)
        {
            if (_disposed != 0) return ValueTask.CompletedTask;
            Volatile.Write(ref _disposed, 1);
            flights = _flights.Values.SelectMany(generation => generation.Values).ToArray();
        }
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
        return new ValueTask(DrainAsync(flights));
    }

    private async Task DrainAsync(Task[] flights)
    {
        try
        {
            try { _lifetime.Cancel(); }
            catch (AggregateException)
            {
                // A throwing cancellation callback must not abandon other HTTP
                // flights or replace the run's already retained failure/cancel.
                // Cancel(false) invokes all callbacks before reporting errors.
            }
            await Task.WhenAll(flights).ConfigureAwait(false);
        }
        catch
        {
            // Workers observed their own validation result. Cleanup must observe
            // and drain every flight without replacing the retained run's cause.
        }
        finally
        {
            lock (_gate) _flights.Clear();
            _lifetime.Dispose();
        }
    }
}
