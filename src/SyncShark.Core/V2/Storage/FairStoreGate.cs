namespace Massdex.SyncShark.V2;

// FIFO admission prevents a fast background page loop from repeatedly barging ahead
// of a queued foreground call. Reentrant on the owning thread, like the prior Monitor.
internal sealed class FairStoreGate
{
    private readonly object _sync = new();
    private long _next, _serving;
    private int _owner, _depth;
    internal int Waiting { get { lock (_sync) return checked((int)(_next - _serving - (_depth > 0 ? 1 : 0))); } }

    internal Lease Enter()
    {
        var thread = Environment.CurrentManagedThreadId;
        lock (_sync)
        {
            if (_owner == thread) { _depth++; return new(this); }
            var ticket = _next++;
            while (ticket != _serving) Monitor.Wait(_sync);
            _owner = thread;
            _depth = 1;
            return new(this);
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            if (_owner != Environment.CurrentManagedThreadId) throw new SynchronizationLockException();
            if (--_depth != 0) return;
            _owner = 0;
            _serving++;
            Monitor.PulseAll(_sync);
        }
    }

    internal readonly struct Lease(FairStoreGate gate) : IDisposable
    {
        public void Dispose() => gate.Exit();
    }
}
