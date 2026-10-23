namespace Massdex.SyncShark.V2;

// FIFO admission, reentrant on the owning thread. Cancelled waiters leave the queue
// immediately, including when the current holder cannot yet release its transaction.
internal sealed class FairStoreGate
{
    private static readonly AsyncLocal<CancellationToken> AdmissionCancellation = new();
    internal static CancellationToken CurrentCancellation => AdmissionCancellation.Value;
    internal static IDisposable WithCancellation(CancellationToken ct)
    {
        var previous = AdmissionCancellation.Value;
        AdmissionCancellation.Value = ct;
        return new CancellationScope(previous);
    }
    private sealed class CancellationScope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => AdmissionCancellation.Value = previous;
    }

    private readonly object _sync = new();
    private readonly LinkedList<object> _waiters = new();
    private int _owner, _depth;
    internal int Waiting { get { lock (_sync) return _waiters.Count; } }
    internal bool IsHeldByCurrentThread { get { lock (_sync) return _owner == Environment.CurrentManagedThreadId; } }

    internal Lease Enter(CancellationToken? cancellationToken = null,
        [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        var ct = cancellationToken ?? CurrentCancellation;
        var thread = Environment.CurrentManagedThreadId;
        // Dispose registration OUTSIDE _sync: a cancellation callback may itself be waiting for _sync.
        using var registration = ct.UnsafeRegister(static state =>
        {
            var gate = (FairStoreGate)state!;
            lock (gate._sync) Monitor.PulseAll(gate._sync);
        }, this);
        lock (_sync)
        {
            if (_owner == thread) { _depth++; return new(this, default); }
            ct.ThrowIfCancellationRequested();
            var node = _waiters.AddLast(new object());
            using var waiting = RunOperations.Begin(RunOperationKind.Store, "wait " + operationName);
            try
            {
                while (_owner != 0 || !ReferenceEquals(node, _waiters.First))
                {
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_sync);
                }
                ct.ThrowIfCancellationRequested();
                var holding = RunOperations.Begin(RunOperationKind.Store, "hold " + operationName);
                _owner = thread;
                _depth = 1;
                return new(this, holding);
            }
            finally
            {
                _waiters.Remove(node);
                Monitor.PulseAll(_sync);
            }
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            if (_owner != Environment.CurrentManagedThreadId) throw new SynchronizationLockException();
            if (--_depth != 0) return;
            _owner = 0;
            Monitor.PulseAll(_sync);
        }
    }

    internal readonly struct Lease(FairStoreGate gate, RunOperations.Token holding) : IDisposable
    {
        public void Dispose() { gate.Exit(); holding.Dispose(); }
    }
}
