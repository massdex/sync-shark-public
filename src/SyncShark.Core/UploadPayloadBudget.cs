namespace Massdex.SyncShark;

// Process resource, not pair/item state. Downloads never wait on this upload-only lane.
internal static class UploadPayloadBudget
{
    private static readonly object Gate = new();
    private static readonly LinkedList<Waiter> Waiters = new();
    private static long _capacity = V2.V2Settings.DefaultUploadPayloadBudgetBytes;
    private static long _used;
    internal static long Capacity
    {
        get { lock (Gate) return _capacity; }
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            lock (Gate) { _capacity = value; Drain(); }
        }
    }
    internal static long Used { get { lock (Gate) return _used; } }
    private sealed class Waiter(int requested)
    {
        internal readonly int Requested = requested;
        internal readonly TaskCompletionSource<int> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }

    // FIFO: small requests cannot repeatedly consume the space a waiting large request needs.
    private static void Drain()
    {
        while (Waiters.First is { } node)
        {
            var size = (int)Math.Min(node.Value.Requested, _capacity);
            if (size > _capacity - _used) break;
            Waiters.RemoveFirst(); node.Value.Node = null;
            _used += size;
            node.Value.Ready.TrySetResult(size);
        }
    }

    internal static async Task<Lease> RentAsync(int requested, CancellationToken cancellationToken)
    {
        if (requested < 1) throw new ArgumentOutOfRangeException(nameof(requested));
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter(requested);
        lock (Gate) { waiter.Node = Waiters.AddLast(waiter); Drain(); }
        using var registration = cancellationToken.Register(() =>
        {
            lock (Gate)
            {
                if (waiter.Node is not { } node) return;
                Waiters.Remove(node); waiter.Node = null;
                waiter.Ready.TrySetCanceled(cancellationToken); Drain();
            }
        });
        var size = await waiter.Ready.Task.ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new Lease(size); // Allocate OUTSIDE the lock, after reserving exact capacity.
        }
        catch { Release(size); throw; }
    }

    private static void Release(int size) { lock (Gate) { _used -= size; Drain(); } }
    internal sealed class Lease(int size) : IDisposable
    {
        private byte[]? _buffer = new byte[size];
        internal byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) Release(buffer.Length);
        }
    }
}
