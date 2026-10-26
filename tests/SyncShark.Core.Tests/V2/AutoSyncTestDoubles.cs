using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>A clock that moves only when a check advances it, firing due timers in order on the caller's thread.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _timerSequence;

    internal string DiagnosticSnapshot()
    {
        lock (_lock)
            return $"now={_now:O} timers=[" + string.Join("; ", _timers.Select(timer =>
                $"id={timer.Id},kind={timer.Kind},delayMs={timer.Delay.TotalMilliseconds},due={timer.Due:O},callback={timer.CallbackType}")) + "]";
    }
    private DateTimeOffset _now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    /// <summary>Timers armed and not yet fired or disposed; a loop parked on a delay holds exactly one.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_lock) return _timers.Count(timer => timer.Due is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing each timer that falls due (callbacks run outside the lock).</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            Id = Interlocked.Increment(ref clock._timerSequence);
        }

        internal long Id { get; }
        internal TimeSpan Delay { get; private set; }
        internal string CallbackType => _state?.GetType().FullName ?? _callback.Method.DeclaringType?.FullName ?? "unknown";
        internal string Kind => Delay == ChangeWait.DrainLimit ? "drain-deadline" :
            Delay == V2Settings.Default.ChangeQuietWindow ? "quiet-debounce" :
            Delay >= V2Settings.Default.MinSafetyPoll ? "safety-or-backoff" : "other";
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; private set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._lock)
            {
                if (!_clock._timers.Contains(this)) _clock._timers.Add(this);
                Delay = dueTime;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            return true;
        }

        public void Dispose()
        {
            lock (_clock._lock) _clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A watch a check signals by hand, following the IChangeWatch contract (pending at start, atomic consume).</summary>
internal sealed class FakeChangeWatch : IChangeWatch
{
    private readonly object _lock = new();
    private readonly bool _slowCancel;
    private bool _pending = true;
    private Exception? _fault;
    private TaskCompletionSource? _waiter;
    private long _waitGeneration;
    private long _cancelRequests;
    private long _cancelCompletions;

    internal string DiagnosticSnapshot()
    {
        lock (_lock) return $"generation={_waitGeneration},pending={_pending},waiting={_waiter is not null}," +
            $"cancelRequests={_cancelRequests},cancelCompletions={_cancelCompletions},overlaps={Overlaps},disposed={Disposed}";
    }

    /// <summary>A watch; <paramref name="fault"/> makes every wait fail, <paramref name="slowCancel"/> makes a cancelled
    /// wait finish only later (on another thread), so a caller that does not join it overlaps the next wait.</summary>
    public FakeChangeWatch(Exception? fault = null, bool slowCancel = false)
    {
        _fault = fault;
        _slowCancel = slowCancel;
    }

    public bool Disposed { get; private set; }

    /// <summary>Waits started while an earlier one was still outstanding (a contract breach by the caller).</summary>
    public int Overlaps { get; private set; }

    /// <summary>Ends the outstanding wait as cancelled though its caller did not cancel (a provider timeout).</summary>
    public void CancelUnasked()
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            waiter = _waiter;
            _waiter = null;
        }
        waiter?.TrySetCanceled();
    }

    /// <summary>True while a wait is outstanding and nothing is pending: the owner is parked on this watch.</summary>
    public bool Parked
    {
        get
        {
            lock (_lock) return _waiter is not null && !_pending;
        }
    }

    public void Signal()
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            waiter = _waiter;
            _waiter = null;
            if (waiter is null) _pending = true;
        }
        waiter?.TrySetResult();
    }

    public void Fail(Exception error)
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            waiter = _waiter;
            _waiter = null;
            if (waiter is null) _fault = error;
        }
        waiter?.TrySetException(error);
    }

    /// <summary>When set, the next ChangedAsync throws this directly instead of returning a task.</summary>
    public Exception? ThrowNext { get; set; }

    public Task ChangedAsync(CancellationToken ct)
    {
        if (ThrowNext is { } thrown)
        {
            ThrowNext = null;
            throw thrown;
        }
        lock (_lock)
        {
            _waitGeneration++;
            if (_waiter is not null)
            {
                Overlaps++;
                return Task.FromException(new InvalidOperationException("FakeChangeWatch: a wait is already outstanding."));
            }
            if (_fault is not null) return Task.FromException(_fault);
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (_pending)
            {
                _pending = false;
                return Task.CompletedTask;
            }
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiter = waiter;
            ct.Register(() => Cancel(waiter, ct));
            return waiter.Task;
        }
    }

    public void Dispose() => Disposed = true;

    // Cancelling never consumes: only a waiter still outstanding is cancelled; a signal it lost to stays completed.
    private void Cancel(TaskCompletionSource waiter, CancellationToken ct)
    {
        lock (_lock) _cancelRequests++;
        if (_slowCancel)
        {
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                await Task.Yield();
                Finish(waiter, ct);
            });
            return;
        }
        Finish(waiter, ct);
    }

    private void Finish(TaskCompletionSource waiter, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_waiter, waiter)) return;
            _waiter = null;
            _cancelCompletions++;
        }
        waiter.TrySetCanceled(ct);
    }
}

/// <summary>Hands out fake watches per root and can refuse to watch, as a missing or replaced root does.</summary>
internal sealed class FakeChangeWatchSource : IChangeWatchSource
{
    private readonly ConcurrentQueue<(string RootId, FakeChangeWatch Watch)> _created = new();

    /// <summary>When set, WatchAsync throws this.</summary>
    public Exception? WatchError { get; set; }

    /// <summary>When set, new watches arm fine but every wait fails with this.</summary>
    public Exception? WaitError { get; set; }

    /// <summary>New watches finish a cancelled wait only later.</summary>
    public bool SlowCancel { get; set; }

    public int Created => _created.Count;

    /// <summary>The newest watch for a root.</summary>
    public FakeChangeWatch Latest(string rootId) => _created.Last(entry => entry.RootId == rootId).Watch;

    public IEnumerable<FakeChangeWatch> All => _created.Select(entry => entry.Watch);

    public Task<IChangeWatch> WatchAsync(RootDef root, CancellationToken ct)
    {
        if (WatchError is { } error) return Task.FromException<IChangeWatch>(error);
        var watch = new FakeChangeWatch(WaitError, SlowCancel);
        _created.Enqueue((root.RootId, watch));
        return Task.FromResult<IChangeWatch>(watch);
    }
}

/// <summary>
/// A fake endpoint's inventory whose scans count and optionally wait for a release (to hold the real SyncRun gate);
/// it also signals its first scan and samples one probe there, so a check can wait for a scan and prove that an
/// earlier phase already ran.
/// </summary>
internal sealed class GatedInventory : IInventorySource
{
    private readonly FakeEndpoint _inner;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;

    public GatedInventory(FakeEndpoint inner) => _inner = inner;

    public TaskCompletionSource? Block { get; set; }

    /// <summary>Full and change scans started.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Completes when the first scan reaches this inventory, so a check can wait instead of polling.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Sampled once at the first scan; until then the value is not read.</summary>
    public Func<int>? Probe { get; set; }

    /// <summary>The probe's value at the first scan (e.g. how many intents were still unresolved); null until then.</summary>
    public int? ProbeAtFirstScan { get; private set; }

    public Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null) => _inner.ReadSubtreeAsync(path, accept, ct, progress);

    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        await EnterAsync(ct);
        return await _inner.FullAsync(gen, include, p, ct);
    }

    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        await EnterAsync(ct);
        return await _inner.ChangesSinceAsync(cursor, gen, include, ct);
    }

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        _inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => _inner.DeletionTimesAsync(paths, ct);

    private async Task EnterAsync(CancellationToken ct)
    {
        ProbeAtFirstScan ??= Probe?.Invoke();
        _entered.TrySetResult();
        Interlocked.Increment(ref _reads);
        if (Block is { } block) await block.Task.WaitAsync(ct);
    }
}

/// <summary>Records automatic runs, serialises them like SyncRun's gate and answers scripted reports or errors.</summary>
internal sealed class FakeSyncRun : ISyncRun
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<object> _script = new();

    /// <summary>Pair ids in the order their runs passed the gate.</summary>
    public ConcurrentQueue<string> Started { get; } = new();

    /// <summary>Pair ids in the order their runs were requested (before the gate).</summary>
    public ConcurrentQueue<string> Entered { get; } = new();

    /// <summary>While set, an admitted run waits for it before answering.</summary>
    public TaskCompletionSource? Block { get; set; }

    public int Runs(string pairId) => Started.Count(id => id == pairId);

    internal ConcurrentQueue<string> MonitorOrder { get; } = new();

    /// <summary>Reports progress through the sink the pair loop passes to reconciliation monitoring.</summary>
    public Action<IProgress<RunProgress>>? MonitorProgressScript { get; set; }

    /// <summary>Reports progress through the sink the pair loop passes to the incremental run.</summary>
    public Action<IProgress<RunProgress>>? ProgressScript { get; set; }

    Task<bool> ISyncRun.MonitorAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MonitorOrder.Enqueue("monitor:" + pairId);
        MonitorProgressScript?.Invoke(progress);
        return Task.FromResult(false);
    }

    // The real run owns returned-report failure persistence; PairLoop only reads this debt.
    internal IStateStore? FailureStore { get; set; }
    internal TimeProvider FailureClock { get; set; } = TimeProvider.System;

    private RunReport PersistReport(RunReport report)
    {
        if (FailureStore is not { } store) return report;
        if (report.HaltedBy is { } halted)
        {
            FailureRetryOwner.RecordRunFailure(store, report.PairId, halted, FailureClock.GetUtcNow(), isStateDamage: StorageFailureClassifier.IsStateDamage);
            if (StorageFailureClassifier.ScopeOf(halted, CancellationToken.None) == FailureRetryOwner.FailureScope.State)
                return report with { Outcome = RunOutcomes.StateDamaged };
        }
        else if (report.Outcome == RunOutcomes.Failed || report.CompletionFailed)
            FailureRetryOwner.RecordRunFailure(store, report.PairId,
                new IOException(report.Failures.FirstOrDefault()?.Reason ?? "Synthetic run failure"), FailureClock.GetUtcNow(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        else if (report.Outcome is RunOutcomes.Complete or RunOutcomes.Partial)
            store.CloseNotice(FailureRetryOwner.RunNoticeId(report.PairId), "resolved");
        return report;
    }

    /// <summary>Queues the answer of a later run: a <see cref="RunReport"/> or an exception to throw.</summary>
    public void Then(object answer) => _script.Enqueue(answer);

    public async Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct)
    {
        MonitorOrder.Enqueue("incremental:" + pairId);
        Entered.Enqueue(pairId);
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            Started.Enqueue(pairId);
            ProgressScript?.Invoke(p);
            if (Block is { } block) await block.Task.WaitAsync(ct);
            if (!_script.TryDequeue(out var answer)) return PersistReport(Report(pairId, RunOutcomes.Complete));
            return answer is Exception error ? throw error : PersistReport(((RunReport)answer) with { PairId = pairId });
        }
        finally
        {
            _gate.Release();
        }
    }

    public static RunReport Report(string pairId, string outcome, int held = 0, params ActionResult[] failures) =>
        new(pairId, outcome, 0, failures.Count(result => result.Outcome == Outcome.Failed), held, 0, 0, 0, 0, 0,
            failures);

    public Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> p, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task StopAsync() => throw new NotSupportedException();
}
