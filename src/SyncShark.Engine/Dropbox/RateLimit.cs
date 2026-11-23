using System.Globalization;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>RateLimit operations for the dropbox module: one adaptive admission slot per file transfer, growth only on
/// finished transfers, and waits (a stated provider wait and the adopted retry schedule) emitted to the run log only.</summary>
public sealed class RateLimit
{
    /// <summary>Growth until the first slow-down of the run doubles a full round, then one per finished transfer
    /// (D33, product-0517).</summary>
    private const int GrowthFactor = 2;
    /// <summary>One busy period halves the admission level, never below one (D33, product-0518).</summary>
    private const int ShrinkDivisor = 2;
    /// <summary>A level of zero would admit nothing.</summary>
    private const int MinimumLevel = 1;

    // Task.Delay on .NET 8 accepts at most 0xfffffffe milliseconds per timer.
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly object _gate = new();
    private readonly LinkedList<TaskCompletionSource?> _waiters = new();
    private readonly EngineSettings.BackoffSettings _backoff;
    private readonly Action<RunEvent> _events;
    private int _level;
    private int _active;
    private int _roundSuccesses;
    private bool _slowedThisRun;

    /// <summary>Use the configured starting level and retry schedule for this transport; emit waits to its run log only.</summary>
    public RateLimit(int initialLevel, EngineSettings.BackoffSettings backoff, Action<RunEvent> events)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialLevel);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(events);
        _level = initialLevel;
        _backoff = backoff;
        _events = events;
    }

    /// <summary>The current admission level; one slot is one file transfer in progress.</summary>
    public int Level
    {
        get { lock (_gate) return _level; }
    }

    /// <summary>Admits one file transfer, FIFO, until released or cancelled.</summary>
    public async Task<IAsyncDisposable> AdmitAsync(CancellationToken ct)
    {
        var node = new LinkedListNode<TaskCompletionSource?>(null);
        lock (_gate) _waiters.AddLast(node);
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_active < _level && node == _waiters.First)
                    {
                        _waiters.Remove(node);
                        _active += 1;
                        Pulse();
                        return new Permit(this);
                    }
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    node.Value = completion;
                    changed = completion.Task;
                }
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    node.List.Remove(node);
                    Pulse();
                }
            }
            throw;
        }
    }

    /// <summary>Grows the level only after a finished successful file transfer (D33).</summary>
    public void TransferFinished(bool success)
    {
        if (!success) return;
        lock (_gate)
        {
            Grow();
            Pulse();
        }
    }

    /// <summary>Applies one slow-down for a new busy period; the caller decides the period (D33, product-0518).</summary>
    public void SlowDown()
    {
        lock (_gate)
        {
            _level = Math.Max(MinimumLevel, _level / ShrinkDivisor);
            _slowedThisRun = true;
        }
    }

    /// <summary>The adopted retry schedule after a failure that supplied no positive provider wait (product-0425/0526).</summary>
    public TimeSpan RetryDelay(int failures) => RetryDelay(failures, Random.Shared.NextDouble);

    /// <summary>The adopted schedule with an explicit sample in [0,1]; the first delay is exact, later nominal delays
    /// cap before jitter and then take a symmetric offset (96-144 s at the 120 s cap with 0.2 jitter).</summary>
    public TimeSpan RetryDelay(int failures, Func<double> sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failures);
        ArgumentNullException.ThrowIfNull(sample);
        if (failures <= 0) return TimeSpan.FromSeconds(_backoff.InitialBackoffSeconds);
        var nominal = Math.Min(_backoff.InitialBackoffSeconds * Math.Pow(_backoff.BackoffMultiplier, failures),
            _backoff.MaximumBackoffSeconds);
        var offset = (2 * sample() - 1) * _backoff.BackoffJitter * nominal;
        return TimeSpan.FromSeconds(nominal + offset);
    }

    /// <summary>Wait a positive provider-requested time on the supplied clock and record it in the run log.</summary>
    public async Task WaitAsync(TimeSpan delay, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clock);
        // A zero or missing time is no time given: the attempt owner waits its adopted retry schedule instead.
        if (delay <= TimeSpan.Zero) return;
        try { _events(WaitEvent(delay)); }
        catch (Exception) { }
        while (delay > MaximumTimerDelay)
        {
            await Task.Delay(MaximumTimerDelay, clock, ct).ConfigureAwait(false);
            delay -= MaximumTimerDelay;
        }
        await Task.Delay(delay, clock, ct).ConfigureAwait(false);
    }

    // Callers hold _gate.
    private void Grow()
    {
        if (_slowedThisRun)
        {
            if (_level < int.MaxValue) _level += 1;
            return;
        }
        _roundSuccesses = _roundSuccesses == int.MaxValue ? int.MaxValue : _roundSuccesses + 1;
        if (_roundSuccesses < _level) return;
        _roundSuccesses = 0;
        _level = _level >= int.MaxValue / GrowthFactor ? int.MaxValue : _level * GrowthFactor;
    }

    // Callers hold _gate; asynchronous continuations never run inside the state transition.
    private void Pulse()
    {
        if (_active < _level) _waiters.First?.Value?.TrySetResult();
    }

    private static RunEvent WaitEvent(TimeSpan delay)
    {
        var seconds = ((long)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return new RunEvent(RunEventKind.Wait, string.Empty, string.Empty,
            Text.Get(TextKey.WaitTooManyRequests, (TextArg.Seconds, seconds)), string.Empty)
            { Cause = TextKey.WaitTooManyRequests };
    }

    private sealed class Permit(RateLimit owner) : IAsyncDisposable
    {
        private bool _released;

        /// <summary>Returns the slot: drops the active count and wakes the next waiter.</summary>
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_released) return ValueTask.CompletedTask;
                _released = true;
                owner._active -= 1;
                owner.Pulse();
            }
            return ValueTask.CompletedTask;
        }
    }
}
