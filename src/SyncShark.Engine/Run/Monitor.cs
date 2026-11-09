using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Run;

// One scheduler: the loop, deadline and pair-wait state and the host wait share the single _gate lock and one timer.
/// <summary>Schedules prerequisite, listener and observational deadlines through one monotonic host wait.</summary>
internal sealed class Monitor : IAsyncDisposable
{
    private readonly object _gate = new();
    private IStateStore? _store;
    private EngineSettings _settings = null!;
    private readonly TimeProvider _time;
    private Func<string, bool> _isPaused = null!;
    private Func<string, RunLog?> _openLog = null!;
    private EngineHost _host = null!;
    private Func<bool>? _observation;
    private IDisposable? _observationDeadline;
    private Task? _stopped;
    // RQ-090's observation cadence is not a retry timeout.
    private const int QuietSeconds = 30;
    private readonly Dictionary<string, PairWait> _pairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Deadline> _listeners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Deadline> _folderJobs = new(StringComparer.Ordinal);
    private readonly HashSet<Deadline> _quiet = [];
    private readonly HashSet<string> _opportunities = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _active = [];
    private readonly HashSet<string> _runningPairs = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = NewSignal();
    // Orders PairWait observations so a refresh that read before a newer observation leaves it in place.
    private long _observations;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private ITimer? _timer;
    private Exception? _failure;
    private volatile bool _quietOnly;

    internal Monitor(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    internal Monitor(EngineSettings settings, TimeProvider time,
        Func<string, RunLog?> openLog, Func<string, bool> isPaused, EngineHost host) : this(time) =>
        Attach(settings, openLog, isPaused, host);

    internal void Attach(EngineSettings settings, Func<string, RunLog?> openLog,
        Func<string, bool> isPaused, EngineHost host)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(openLog);
        ArgumentNullException.ThrowIfNull(isPaused);
        ArgumentNullException.ThrowIfNull(host);
        if (_host is not null) throw new InvalidOperationException();
        _settings = settings;
        _openLog = openLog;
        _isPaused = isPaused;
        _host = host;
    }

    internal void Start(bool quietOnly = false)
    {
        lock (_gate)
        {
            if (_stop?.IsCancellationRequested == true || _failure is not null || _loop?.IsCompleted == true)
                throw new InvalidOperationException();
            if (_loop is not null && (quietOnly || !_quietOnly)) throw new InvalidOperationException();
            // Quiet composition cannot read a store; promotion requires the host's published store.
            if (!quietOnly) _store ??= _host?.Store ?? throw new InvalidOperationException();
            _quietOnly = quietOnly;
            if (_loop is not null)
            {
                SignalLocked();
                return;
            }
            _stop = new CancellationTokenSource();
            _timer = _time.CreateTimer(_ => Signal(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // Schema composition may block a UI context; the owned loop must run independently of it.
            _loop = Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>Changes the observed lifetime without restarting its already-armed quiet deadline.</summary>
    internal void Observe(Func<bool> publish)
    {
        lock (_gate)
        {
            _observation = publish;
            ArmObservation();
        }
    }

    private void ArmObservation()
    {
        if (_observationDeadline is not null || _stop?.IsCancellationRequested == true) return;
        _observationDeadline = ScheduleQuietStatus(TimeSpan.FromSeconds(QuietSeconds), () =>
        {
            Func<bool>? publish;
            lock (_gate) { _observationDeadline = null; publish = _observation; }
            if (publish?.Invoke() == true)
                lock (_gate) ArmObservation();
        });
    }

    internal void RearmPrerequisiteWait(string? refusedPair = null)
    {
        lock (_gate)
        {
            if (refusedPair is not null && _pairs.TryGetValue(refusedPair, out var pair)) pair.Consumed = false;
            SignalLocked();
        }
    }

    internal void Wake(string pairId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pairId);
        lock (_gate)
        {
            _opportunities.Add(pairId);
            SignalLocked();
        }
    }

    internal void WakeSource(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceKey);
        // The feed owns provider floors. A notification never shortens its registered wait.
        lock (_gate)
        {
            if (_listeners.ContainsKey(sourceKey)) SignalLocked();
        }
    }

    internal IDisposable ScheduleListenerReconnect(string sourceKey, TimeSpan delay,
        Func<CancellationToken, Task> reconnect)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceKey);
        ArgumentNullException.ThrowIfNull(reconnect);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_gate)
        {
            if (_stop?.IsCancellationRequested == true) throw new OperationCanceledException(_stop.Token);
            if (_listeners.ContainsKey(sourceKey)) throw new InvalidOperationException();
            var deadline = new Deadline(this, sourceKey, _time.GetTimestamp(), delay, reconnect);
            _listeners.Add(sourceKey, deadline);
            SignalLocked();
            return deadline;
        }
    }

    internal void ScheduleFolderPoll(string pairId, TimeSpan delay, Action wake)
    {
        lock (_gate)
        {
            if (_stop?.IsCancellationRequested == true) return;
            if (_folderJobs.ContainsKey(pairId)) return;
            _folderJobs.Add(pairId, new Deadline(this, pairId, _time.GetTimestamp(), delay, _ =>
            {
                wake();
                return Task.CompletedTask;
            }, folderJob: true));
            SignalLocked();
        }
    }

    internal void WithdrawFolderPolls(string? pairId)
    {
        lock (_gate)
        {
            if (pairId is not null) _folderJobs.GetValueOrDefault(pairId)?.Dispose();
            else foreach (var deadline in _folderJobs.Values.ToArray()) deadline.Dispose();
        }
    }

    internal IDisposable ScheduleQuietStatus(TimeSpan delay, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_gate)
        {
            if (_stop?.IsCancellationRequested == true) throw new OperationCanceledException(_stop.Token);
            var deadline = new Deadline(this, null, _time.GetTimestamp(), delay, _ =>
            {
                publish();
                return Task.CompletedTask;
            });
            _quiet.Add(deadline);
            SignalLocked();
            return deadline;
        }
    }

    // One pump iteration: opportunities, retry waits and deadlines are resolved under the same _gate lock and armed in one snapshot.
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task changed;
                // Captured before the store is read: a signal after this point forces one more pass.
                if (!_quietOnly) RefreshPairs();
                lock (_gate) changed = _changed.Task;
                List<Func<CancellationToken, Task>> ready = [];
                lock (_gate)
                {
                    if (_failure is { } failure) throw failure;
                    var wait = Timeout.InfiniteTimeSpan;
                    foreach (var pairId in _quietOnly ? [] : _opportunities.ToArray())
                    {
                        _opportunities.Remove(pairId);
                        if (_runningPairs.Contains(pairId))
                        {
                            _opportunities.Add(pairId);
                            continue;
                        }
                        (long Generation, FailureRetryOwner.State State)? schedule = null;
                        // An event before the deadline must not use up the timed wait; at or after it, the wake is the due wake.
                        if (_pairs.TryGetValue(pairId, out var pair) &&
                            pair.Delay - _time.GetElapsedTime(pair.Started) <= TimeSpan.Zero)
                        {
                            pair.Consumed = true;
                            schedule = (pair.Generation, pair.State);
                        }
                        if (!_isPaused(pairId))
                        {
                            _runningPairs.Add(pairId);
                            ready.Add(token => RunPairAsync(pairId, token, schedule));
                        }
                    }
                    foreach (var (pairId, pair) in _pairs)
                    {
                        if (pair.Consumed || _isPaused(pairId) || _runningPairs.Contains(pairId)) continue;
                        var remaining = pair.Delay - _time.GetElapsedTime(pair.Started);
                        if (remaining <= TimeSpan.Zero)
                        {
                            pair.Consumed = true;
                            _runningPairs.Add(pairId);
                            ready.Add(token => RunPairAsync(pairId, token, (pair.Generation, pair.State)));
                        }
                        else wait = Earlier(wait, remaining);
                    }
                    foreach (var deadline in (_quietOnly ? _quiet : _listeners.Values.Concat(_folderJobs.Values).Concat(_quiet)).ToArray())
                    {
                        if (deadline.Claimed || deadline.Withdrawn) continue;
                        var remaining = deadline.Delay - _time.GetElapsedTime(deadline.Started);
                        if (remaining <= TimeSpan.Zero)
                        {
                            deadline.Claimed = true;
                            ready.Add(token => RunDeadlineAsync(deadline, token));
                        }
                        else wait = Earlier(wait, remaining);
                    }
                    _timer!.Change(wait, Timeout.InfiniteTimeSpan);
                }
                foreach (var execute in ready) Track(execute, ct);
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void RefreshPairs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long started;
        lock (_gate) started = _observations;
        long? ticks = null;
        long? row = null;
        while (true)
        {
            var count = 0;
            foreach (var current in _store!.ReadPrerequisiteRetries(ticks, row, _settings.StoragePageSize))
            {
                count += 1;
                ticks = current.State.RetryAfter.UtcTicks;
                row = current.PairRow;
                seen.Add(current.PairId);
                lock (_gate) Observe(current.PairId, current.Generation, current.State, out _, started);
            }
            if (count < _settings.StoragePageSize) break;
        }
        lock (_gate)
            foreach (var missing in _pairs.Where(pair => !seen.Contains(pair.Key) && pair.Value.Sequence <= started)
                         .Select(pair => pair.Key).ToArray())
                _pairs.Remove(missing);
    }

    /// <summary>Whether this pair's wait for state is still in the future, on the monotonic clock that fires it.</summary>
    internal bool ItemWaitPending(string pairId, long generation, FailureRetryOwner.State state)
    {
        lock (_gate)
        {
            var wait = Observe(pairId, generation, state, out var installed);
            if (installed) SignalLocked();
            return wait.Delay - _time.GetElapsedTime(wait.Started) > TimeSpan.Zero;
        }
    }

    // Callers hold _gate. The first sight of a state converts its stored instant to a monotonic delay, once. A
    // refresh never replaces a wait observed after its own read began: that observation is at least as new.
    private PairWait Observe(string pairId, long generation, FailureRetryOwner.State state, out bool installed,
        long? refreshStarted = null)
    {
        var known = _pairs.TryGetValue(pairId, out var prior);
        installed = !(known && (prior!.Generation == generation && prior.State == state ||
            prior.Sequence > refreshStarted));
        if (!installed) return prior!;
        var delay = state.RetryAfter - _time.GetUtcNow();
        return _pairs[pairId] = new PairWait(generation, state, _time.GetTimestamp(),
            delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ++_observations);
    }

    private async Task RunPairAsync(string pairId, CancellationToken ct,
        (long Generation, FailureRetryOwner.State State)? schedule = null)
    {
        try { await _host.RunPrerequisiteAsync(pairId, ct, schedule: schedule).ConfigureAwait(false); }
        // The attempt owner classified this stop and the attempt's own log holds its diagnosis, so the scheduler
        // reports no failure for it; any other outcome stays diagnostic.
        catch (PrerequisiteStoppedException) { }
        catch (Exception error)
        {
            // The host has already accounted for the attempt; this is diagnostic, never a second failure report.
            using var log = _openLog(pairId);
            log?.Failed(error, TextKey.RequiredRepairRetry);
        }
        finally
        {
            lock (_gate) { _runningPairs.Remove(pairId); SignalLocked(); }
        }
    }

    private async Task RunDeadlineAsync(Deadline deadline, CancellationToken ct)
    {
        lock (_gate)
        {
            if (deadline.Withdrawn) return;
            // Release this slot before the wake can register its successor.
            if (deadline.FolderJob) deadline.Dispose();
        }
        try { await deadline.Execute(ct).ConfigureAwait(false); }
        finally { deadline.Dispose(); }
    }

    private void Track(Func<CancellationToken, Task> execute, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _active.Add(completion.Task);
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try { await execute(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { lock (_gate) _failure ??= error; }
            finally
            {
                lock (_gate)
                {
                    _active.Remove(completion.Task);
                    completion.SetResult();
                    SignalLocked();
                }
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    internal Task StopAsync()
    {
        lock (_gate) return _stopped ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_gate) { loop = _loop; stop = _stop; }
        if (loop is null) return;
        stop!.Cancel();
        try { await loop.ConfigureAwait(false); }
        finally
        {
            Task[] active;
            lock (_gate) active = _active.ToArray();
            await Task.WhenAll(active).ConfigureAwait(false);
            if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _listeners.Clear();
                _folderJobs.Clear();
                _quiet.Clear();
                _opportunities.Clear();
                _pairs.Clear();
            }
        }
    }

    private void Signal() { lock (_gate) SignalLocked(); }
    private void SignalLocked()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TimeSpan Earlier(TimeSpan current, TimeSpan next) =>
        current == Timeout.InfiniteTimeSpan || next < current ? next : current;

    private sealed class PairWait(long generation, FailureRetryOwner.State state, long started, TimeSpan delay,
        long sequence)
    {
        internal long Sequence { get; } = sequence;
        internal long Generation { get; } = generation;
        internal FailureRetryOwner.State State { get; } = state;
        internal long Started { get; } = started;
        internal TimeSpan Delay { get; } = delay;
        internal bool Consumed;
    }

    private sealed class Deadline(Monitor owner, string? source, long started, TimeSpan delay,
        Func<CancellationToken, Task> execute, bool folderJob = false) : IDisposable
    {
        internal bool FolderJob { get; } = folderJob;
        internal long Started { get; } = started;
        internal TimeSpan Delay { get; } = delay;
        internal Func<CancellationToken, Task> Execute { get; } = execute;
        internal bool Claimed;
        internal bool Withdrawn;
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (Withdrawn) return;
                Withdrawn = true;
                if (FolderJob) owner._folderJobs.Remove(source!);
                else if (source is not null) owner._listeners.Remove(source);
                else owner._quiet.Remove(this);
                owner.SignalLocked();
            }
        }
    }
}
