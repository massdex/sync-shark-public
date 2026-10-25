namespace Massdex.SyncShark.V2;

/// <summary>One pair's continuous-sync loop (continuous-sync.md section 2): wait, settle, run, retry or hold.</summary>
internal sealed partial class PairLoop
{
    private static readonly IProgress<RunProgress> NoProgress = new Progress<RunProgress>();

    // Dead watches rebuilt at once before the pair is held; more would spin while a root keeps failing.
    private const int MaxDeadWatchRebuilds = 1;

    private readonly string _pairId;
    private readonly AutoSyncInputs _in;
    private readonly object _lock = new();
    private readonly List<IChangeWatch> _watches = [];
    private TaskCompletionSource _kick = NewKick();
    private CancellationTokenSource? _runSource;
    private NoticeReason? _held;
    private DateTimeOffset _lastIdleAnnounce = DateTimeOffset.MinValue;
    private DateTimeOffset? _retryAt;
    private int _deadWatchRebuilds;
    private RunReport? _last;
    private bool _watchDegraded;
    private DateTimeOffset _watchRetryAt;
    private DateTimeOffset _pollAt;
    private int _watchAttempts;
    private bool _watchStateLoaded;
    private string _wakeCause = "startup";

    internal PairLoop(string pairId, AutoSyncInputs inputs)
    {
        _pairId = pairId;
        _in = inputs;
    }

    private void LoadWatchState()
    {
        if (_watchStateLoaded) return;
        var notice = WatchNotice(string.Empty);
        if (_in.Store.NoticeById(_pairId, NoticeTable.Id(notice)) is { Closed: false, Notice.FailureRetry: { } retry })
        {
            _watchDegraded = true;
            _watchAttempts = retry.Attempts;
            _watchRetryAt = retry.RetryAfter;
        }
        _watchStateLoaded = true;
    }

    /// <summary>Wakes the loop for a re-evaluation; a pair now paused or off has its automatic run cancelled.</summary>
    internal void Kick()
    {
        lock (_lock)
        {
            _kick.TrySetResult();
            if (_last?.Outcome == RunOutcomes.StateDamaged) return;
            if (_runSource is not null && _in.Ineligible(_in.Store.Pair(_pairId)) is not null) _runSource.Cancel();
        }
    }

    /// <summary>Runs until <paramref name="stop"/> is cancelled or the pair is removed.</summary>
    internal async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (_last?.Outcome == RunOutcomes.StateDamaged)
                    {
                        Publish(AutoStatus.Held, _held);
                        await Task.Delay(Timeout.InfiniteTimeSpan, _in.Time, stop).ConfigureAwait(false);
                        continue; // Recovery recreates the engine; a safety poll cannot reopen damaged state.
                    }
                    try { LoadWatchState(); }
                    catch (Exception error)
                    {
                        // Startup I/O belongs to this worker, never the loop-creation caller.
                        RunOutput.Observe(error);
                        WatchFailure(error);
                        _watchStateLoaded = true;
                    }
                    var pair = _in.Store.Pair(_pairId);
                    if (pair is null) return;
                    if (_in.Ineligible(pair) is { } blocked)
                    {
                        DisposeWatches();
                        Publish(blocked.Status, blocked.Reason);
                        await WaitAsync([], _in.SafetyInterval(), stop).ConfigureAwait(false);
                        continue;
                    }
                    if (await ReadyToRunAsync(pair, stop).ConfigureAwait(false))
                        await RunOnceAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    RecordFailure(error);
                    // Never retire a worker because a watch, store read or callback failed.
                    DisposeWatches();
                    await Task.Delay(_in.Settings.MinSafetyPoll, _in.Time, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping is the normal way out of the loop.
        }
        finally
        {
            try { DisposeWatches(); }
            catch (Exception error) { RecordFailure(error); }
        }
    }

    // True when a run is due now; false sends the loop back to re-evaluate the pair.
    private async Task<bool> ReadyToRunAsync(PairDef pair, CancellationToken stop)
    {
        if (_watchDegraded && _held is null)
        {
            var now = _in.Time.GetUtcNow();
            if (now >= _watchRetryAt && await ArmAsync(pair, stop).ConfigureAwait(false))
                return await TriggeredAsync(stop).ConfigureAwait(false);
            // A broken notification channel supplies no sync authority. Ordinary run preparation
            // still validates root identity, coverage and account access before any effect.
            now = _in.Time.GetUtcNow();
            if (now >= _pollAt && (_retryAt is null || now >= _retryAt))
            {
                _pollAt = now + _in.SafetyInterval();
                _wakeCause = "safety-poll";
                _retryAt = null;
                return true;
            }
            var due = _pollAt;
            if (_retryAt is { } retry && retry > due) due = retry;
            if (_watchRetryAt < due) due = _watchRetryAt;
            Publish(AutoStatus.Waiting, AutoSyncReasons.WatchUnavailable);
            await WaitAsync([], due - now, stop).ConfigureAwait(false);
            return false;
        }
        if (_held is not null)
        {
            Publish(AutoStatus.Held, _held);
            // All holds retry at the safety poll; reconnect or Retry can wake them sooner.
            var wake = await WaitAsync([], _in.SafetyInterval(), stop).ConfigureAwait(false);
            if (wake.Kind == WakeKind.Timeout)
            {
                _held = null;
                // A refused run keeps its existing watches, whose startup signals are already consumed.
                // Make this poll's retry due now rather than waiting for another signal or safety interval.
                // A failed watch setup has no watches and still uses the normal arm-and-settle path.
                if (_watches.Count > 0) _retryAt = _in.Time.GetUtcNow();
            }
            return false;
        }
        if (_retryAt is { } retryAt) return await RetryReadyAsync(pair, retryAt, stop).ConfigureAwait(false);
        return await ArmAsync(pair, stop).ConfigureAwait(false) && await TriggeredAsync(stop).ConfigureAwait(false);
    }

    private async Task<bool> RetryReadyAsync(PairDef pair, DateTimeOffset retryAt, CancellationToken stop)
    {
        Publish(AutoStatus.Waiting, null);
        // Watch signals stay latched and never bring a retry forward.
        var wake = await WaitAsync([], retryAt - _in.Time.GetUtcNow(), stop).ConfigureAwait(false);
        if (wake.Kind == WakeKind.Kick) return false;
        _retryAt = null;
        _wakeCause = "retry";
        return await ArmAsync(pair, stop).ConfigureAwait(false);
    }

    // Waits for a change, the safety poll or a Kick. A new watch starts with a signal pending, so startup, Kick,
    // the safety poll (which rebuilds the watches) and a dead-watch rebuild each lead to one settled run.
    private async Task<bool> TriggeredAsync(CancellationToken stop)
    {
        Publish(AutoStatus.Idle, null);
        AnnounceIdle();
        var wake = await WaitAsync(_watches, _in.SafetyInterval(), stop).ConfigureAwait(false);
        if (wake.Kind == WakeKind.Signal) return await SettleAsync(stop).ConfigureAwait(false);
        if (wake.Kind == WakeKind.Timeout) { _wakeCause = "safety-poll"; DisposeWatches(); }
        return false;
    }

    // AutoSync idle is announced at most once a minute (RQ-090), so a pair waiting for changes is never mistaken for a
    // stuck one. It goes to the run log when one is open, otherwise to the console.
    private void AnnounceIdle()
    {
        try
        {
            var now = _in.Time.GetUtcNow();
            if (now - _lastIdleAnnounce < TimeSpan.FromMinutes(1)) return;
            _lastIdleAnnounce = now;
            var line = $"Idle: watching for changes since {now:HH:mm:ss}";
            RunLog.Trace(line);
        }
        catch (Exception error) { RunOutput.Observe(error); }
    }

    // Debounce: run once the watches have been quiet for ChangeQuietWindow, or ChangeMaxDelay after the first signal.
    private async Task<bool> SettleAsync(CancellationToken stop)
    {
        Publish(AutoStatus.Waiting, null);
        var deadline = _in.Time.GetUtcNow() + _in.Settings.ChangeMaxDelay;
        while (true)
        {
            var left = deadline - _in.Time.GetUtcNow();
            if (left <= TimeSpan.Zero) return true;
            var quiet = left < _in.Settings.ChangeQuietWindow ? left : _in.Settings.ChangeQuietWindow;
            var wake = await WaitAsync(_watches, quiet, stop).ConfigureAwait(false);
            if (wake.Kind == WakeKind.Timeout) return true;
            if (wake.Kind != WakeKind.Signal) return false;
        }
    }

    private async Task<bool> ArmAsync(PairDef pair, CancellationToken stop)
    {
        if (_watches.Count > 0) return true;
        try
        {
            _watches.Add(await _in.Watches.WatchAsync(pair.A, stop).ConfigureAwait(false));
            _watches.Add(await _in.Watches.WatchAsync(pair.B, stop).ConfigureAwait(false));
            _watchDegraded = false;
            return true;
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            // Includes a provider's own cancellation (a timeout): only the loop's stop token means "stopping".
            DisposeWatches();
            WatchFailure(error);
            return false;
        }
    }

    private void WatchFailure(Exception error)
    {
        var now = _in.Time.GetUtcNow();
        if (_watchAttempts == 0) _pollAt = now;
        _watchDegraded = true;
        var retry = FailureRetryOwner.Failed(_watchAttempts, now);
        _watchAttempts = retry.Attempts;
        _watchRetryAt = retry.RetryAfter;
        try { _in.Store.AddNotices([WatchNotice(error.Message) with { FailureRetry = retry, CauseKey = error.Message, CauseIdentity = FailureCauseIdentity.Of(error) }]); }
        catch (Exception noticeError) { RunOutput.Observe(noticeError); }
    }

    private Notice WatchNotice(string cause) => new(_pairId, 0, NoticeKind.Info, string.Empty,
        "Change watching unavailable: " + cause +
        ". Sync continues by periodic polling. Check the folder and connectivity, then Retry; Ignore silences this notice without changing sync protection.",
        "autosync-watch-unavailable");

    private async Task RunOnceAsync(CancellationToken stop)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            lock (_lock)
            {
                // Admission: a pause or turn-off that landed after the last check cancels before Run takes its gate;
                // one that lands later reaches this source through Kick, and Run re-checks the token under its gate.
                _runSource = source;
                if (_in.Ineligible(_in.Store.Pair(_pairId)) is not null) source.Cancel();
            }
            Publish(AutoStatus.Running, null);
            using var log = _in.BeginRunLog?.Invoke(_pairId, _wakeCause);
            _wakeCause = "watch-signal";
            AfterReport(await _in.Run.IncrementalAsync(_pairId, NoProgress, source.Token).ConfigureAwait(false));
            _deadWatchRebuilds = 0;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Paused, turned off or stopped: nothing to retry; the loop re-evaluates the pair.
        }
        catch (Exception error) when (error is EndpointAccessException or SyncRunRefusedException)
        {
            Hold(error);
        }
        catch (Exception error)
        {
            // Any other failure, including a cancellation our token did not ask for (a provider timeout), retries.
            RecordFailure(error);
            ScheduleRetry();
        }
        finally
        {
            lock (_lock) _runSource = null;
        }
    }

    private void RecordFailure(Exception error)
    {
        if (_last?.Outcome == RunOutcomes.StateDamaged) { RunOutput.Observe(error); return; }
        try
        {
            FailureRetryOwner.RecordRunFailure(_in.Store, _pairId, error, _in.Time.GetUtcNow(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        }
        catch (Exception) { /* A damaged/unavailable notice store cannot terminate another pair or the host. */ }
    }

    private void Hold(Exception error, bool record = true)
    {
        if (record) RecordFailure(error);
        _held = error switch
        {
            EndpointAccessException => AutoSyncReasons.AccountAccess,
            SyncRunRefusedException refused => refused.Reason ?? AutoSyncReasons.Refused with { Detail = error.Message },
            _ => AutoSyncReasons.WatchUnavailable
        };
    }

    // A Kick clears holds, retries and backoff and rebuilds the watches, whose pending signal gives one run.
    private async Task<Wake> WaitAsync(IReadOnlyList<IChangeWatch> watches, TimeSpan? timeout, CancellationToken stop)
    {
        Task kick;
        lock (_lock) kick = _kick.Task;
        var wake = await ChangeWait.AnyAsync(watches, kick, timeout, _in.Time, stop).ConfigureAwait(false);
        if (wake.Kind == WakeKind.Fault)
        {
            DisposeWatches();
            // One dead watch is rebuilt at once (its catch-up run resets the count); a second death before any run
            // completes is held as watch-unavailable and retried at the safety poll, never in a tight loop.
            if (wake.Error is EndpointAccessException || ++_deadWatchRebuilds > MaxDeadWatchRebuilds)
                WatchFailure(wake.Error!);
        }
        else if (watches.Count > 0 && wake.Kind is WakeKind.Signal or WakeKind.Timeout && _watchAttempts > 0)
        {
            _in.Store.CloseNoticeFamily(_pairId, "autosync-watch-unavailable");
            _watchAttempts = 0;
            _watchDegraded = false;
        }
        if (wake.Kind != WakeKind.Kick) return wake;
        _wakeCause = "kick";
        lock (_lock) _kick = NewKick();
        _held = null;
        _retryAt = null;
        _deadWatchRebuilds = 0;
        _watchRetryAt = _pollAt = _in.Time.GetUtcNow();
        DisposeWatches();
        return wake;
    }

    private void DisposeWatches()
    {
        var watches = _watches.ToArray();
        _watches.Clear();
        foreach (var watch in watches)
            try { watch.Dispose(); } catch (Exception error) { RunOutput.Observe(error); }
    }

    private void Publish(AutoStatus status, NoticeReason? reason) =>
        _in.Publish(new PairAutoState(_pairId, status, reason, status == AutoStatus.Waiting ? _retryAt : null, _last)
        {
            NextRunUtc = status == AutoStatus.Idle ? _in.Time.GetUtcNow() + _in.SafetyInterval() :
                status == AutoStatus.Waiting ? _retryAt ?? _in.Time.GetUtcNow() + _in.Settings.ChangeQuietWindow : null
        });

    private static TaskCompletionSource NewKick() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
