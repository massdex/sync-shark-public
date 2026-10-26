namespace Massdex.SyncShark.V2;

// FIFO admission, reentrant on the owning thread. Cancelled waiters leave the queue
// immediately, including when the current holder cannot yet release its transaction.
internal sealed class FairStoreGate
{
    internal static CancellationToken CurrentCancellation => StoreAdmission.CurrentCancellation;
    internal static IDisposable WithCancellation(CancellationToken ct) => StoreAdmission.WithCancellation(ct);
    // Only cleanup calls use this independent admission deadline. It must never wrap effects
    // or completed-effect commits. A blocked cleanup remains pending and reaches the normal
    // failure owner; trying to write a notice through the same blocked gate would deadlock.
    internal static void Cleanup(Action cleanup, TimeSpan? admissionTimeout = null, Exception? primaryError = null) =>
        Admit(cleanup, admissionTimeout, primaryError, bestEffort: false);

    // Admission for a cleanup that owns no safety authority (a transient info notice only). Same deadline
    // and same diagnostics, but a missed admission is observed and left for the next pass instead of failing
    // the run: a busy store must never cost a completed scan. Never use this for an effectful cleanup.
    internal static void CleanupBestEffort(Action cleanup, TimeSpan? admissionTimeout = null, Exception? primaryError = null)
    {
        BeforeAdmissionForTest?.Invoke();
        Admit(cleanup, admissionTimeout, primaryError, bestEffort: true);
    }

    private static void Admit(Action cleanup, TimeSpan? admissionTimeout, Exception? primaryError, bool bestEffort)
    {
        using var deadline = new CancellationTokenSource(admissionTimeout ?? TimeSpan.FromSeconds(5));
        using var admission = WithCancellation(deadline.Token);
        try
        {
            cleanup();
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            var failure = new TimeoutException(bestEffort
                ? "Best-effort store cleanup admission timed out; the next pass repairs it."
                : "Store cleanup admission timed out; retained safety state must be retried before dependent work.", error);
            RunOutput.Observe(failure);
            if (RetainFatal(primaryError, failure)) return;
            if (!bestEffort) throw failure;
        }
        catch (Exception error)
        {
            RunOutput.Observe(error);
            if (RetainFatal(primaryError, error)) return;
            throw;
        }
    }

    // The wait-notice close is the only production best-effort cleanup. A check holds the gate past the
    // admission deadline from here, so it proves a completed scan survives a missed close without sleeping.
    internal static Action? BeforeAdmissionForTest { get; set; }

    private static bool RetainFatal(Exception? primaryError, Exception secondary)
    {
        if (primaryError is null || StorageFailureClassifier.ScopeOf(primaryError, CancellationToken.None) !=
            FailureRetryOwner.FailureScope.State) return false;
        // The original throw retains its type/stack and remains the failure owner's authority.
        // Cleanup diagnostics are secondary; they must never downgrade state damage to retryable IO.
        const string key = "StoreCleanupFailure";
        primaryError.Data[key] = primaryError.Data[key] is Exception prior
            ? new AggregateException(prior, secondary) : secondary;
        return true;
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
