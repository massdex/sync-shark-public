using System.Collections.Concurrent;

namespace Massdex.SyncShark;

/// <summary>
/// Process-wide byte admission for every recovery and Keep-both spool on a volume.
/// A reservation is held from before file creation until the caller has finished
/// consuming the verified spool, so separate Dropbox client instances cannot each
/// admit against the same stale free-space snapshot.
/// </summary>
internal static class SyncRecoverySpoolAdmission
{
    private static readonly ConcurrentDictionary<string, VolumeState> Volumes =
        new(StringComparer.OrdinalIgnoreCase);

    internal static long? CapacityBytesForTest { get; set; }
    internal static TimeSpan? WaitTimeoutForTest { get; set; }

    internal static async Task<Reservation> ReserveAsync(string volumeRoot, long bytes, string identity,
        long availableFreeSpace, long safetyReserve, CancellationToken cancellationToken)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A spool identity is required.", nameof(identity));
        var key = Path.GetFullPath(volumeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var usable = CapacityBytesForTest ?? Math.Max(0, availableFreeSpace - safetyReserve);
        var timeout = WaitTimeoutForTest ?? TimeSpan.FromMinutes(2);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WaitTimeoutForTest));
        var state = Volumes.GetOrAdd(key, static value => new VolumeState(value));
        return await state.ReserveAsync(bytes, identity, usable, timeout, cancellationToken);
    }

    internal static (long ReservedBytes, int WaitingCount, int ActiveCount) SnapshotForTest(string volumeRoot)
    {
        var key = Path.GetFullPath(volumeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Volumes.TryGetValue(key, out var state) ? state.Snapshot() : (0, 0, 0);
    }

    internal static void ResetForTest()
    {
        foreach (var state in Volumes.Values)
            if (state.Snapshot() is not (0, 0, 0))
                throw new InvalidOperationException("Cannot reset recovery-spool admission while reservations are active.");
        Volumes.Clear();
        CapacityBytesForTest = null;
        WaitTimeoutForTest = null;
    }

    internal sealed class Reservation : IAsyncDisposable, IDisposable
    {
        private readonly VolumeState _owner;
        private int _released;

        internal Reservation(VolumeState owner, long bytes, string identity)
        {
            _owner = owner;
            Bytes = bytes;
            Identity = identity;
        }

        internal long Bytes { get; }
        internal string Identity { get; }
        internal int OwnerProcessId { get; } = Environment.ProcessId;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class VolumeState(string volumeRoot)
    {
        private readonly object _gate = new();
        private readonly LinkedList<Waiter> _waiters = [];
        private long _capacity;
        private long _reserved;
        private int _active;

        internal async Task<Reservation> ReserveAsync(long bytes, string identity, long usableCapacity,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waiter waiter;
            lock (_gate)
            {
                if (_active == 0 && _waiters.Count == 0) _capacity = usableCapacity;
                if (bytes > _capacity)
                    throw new IOException(
                        $"The protected recovery spool on {volumeRoot} cannot admit {bytes} bytes within its {_capacity}-byte shared capacity.");
                waiter = new Waiter(bytes, identity);
                waiter.Node = _waiters.AddLast(waiter);
                TryGrantLocked();
            }

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                timeoutSource.Token);
            using var registration = linked.Token.Register(() => Cancel(waiter,
                cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException("Recovery-spool admission was cancelled.", cancellationToken)
                    : new TimeoutException(
                        $"Recovery-spool admission on {volumeRoot} did not become available within {timeout}.")));
            var reservation = await waiter.Completion.Task.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested) return reservation;
            reservation.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        private void Cancel(Waiter waiter, Exception exception)
        {
            var cancelled = false;
            lock (_gate)
            {
                if (waiter.State != 0) return;
                waiter.State = 2;
                if (waiter.Node is not null) _waiters.Remove(waiter.Node);
                waiter.Node = null;
                cancelled = true;
                TryGrantLocked();
            }
            if (cancelled) waiter.Completion.TrySetException(exception);
        }

        private void TryGrantLocked()
        {
            while (_waiters.First is { } node)
            {
                var waiter = node.Value;
                if (waiter.State != 0)
                {
                    _waiters.RemoveFirst();
                    continue;
                }
                if (_reserved + waiter.Bytes > _capacity) return;
                _waiters.RemoveFirst();
                waiter.Node = null;
                waiter.State = 1;
                _reserved = checked(_reserved + waiter.Bytes);
                _active++;
                waiter.Completion.TrySetResult(new Reservation(this, waiter.Bytes, waiter.Identity));
            }
        }

        internal void Release(Reservation reservation)
        {
            lock (_gate)
            {
                if (_active <= 0 || _reserved < reservation.Bytes)
                    throw new InvalidOperationException("Recovery-spool reservation accounting is inconsistent.");
                _active--;
                _reserved -= reservation.Bytes;
                TryGrantLocked();
                if (_active == 0 && _waiters.Count == 0) _capacity = 0;
            }
        }

        internal (long ReservedBytes, int WaitingCount, int ActiveCount) Snapshot()
        {
            lock (_gate) return (_reserved, _waiters.Count, _active);
        }

        private sealed class Waiter(long bytes, string identity)
        {
            internal long Bytes { get; } = bytes;
            internal string Identity { get; } = identity;
            internal TaskCompletionSource<Reservation> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal LinkedListNode<Waiter>? Node { get; set; }
            internal int State { get; set; }
        }
    }
}
