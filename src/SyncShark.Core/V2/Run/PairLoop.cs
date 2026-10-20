namespace Massdex.SyncShark.V2;

/// <summary>One pair's continuous-sync loop (continuous-sync.md section 2): wait, settle, run, retry or hold.</summary>
internal sealed class PairLoop
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
    private bool _heldUntilKick;
    private DateTimeOffset _lastIdleAnnounce = DateTimeOffset.MinValue;
    private DateTimeOffset? _retryAt;
    private TimeSpan _backoff;
    private int _deadWatchRebuilds;
    private RunReport? _last;

    internal PairLoop(string pairId, AutoSyncInputs inputs)
    {
        _pairId = pairId;
        _in = inputs;
    }

    /// <summary>Wakes the loop for a re-evaluation; a pair now paused or off has its automatic run cancelled.</summary>
    internal void Kick()
    {
        lock (_lock)
        {
            _kick.TrySetResult();
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
                var pair = _in.Store.Pair(_pairId);
                if (pair is null) return;
                if (_in.Ineligible(pair) is { } blocked)
                {
                    DisposeWatches();
                    Publish(blocked.Status, blocked.Reason);
                    await WaitAsync([], null, stop).ConfigureAwait(false);
                    continue;
                }
                if (await ReadyToRunAsync(pair, stop).ConfigureAwait(false))
                    await RunOnceAsync(stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping is the normal way out of the loop.
        }
        catch (Exception error)
        {
            RecordFailure(error);
            Publish(AutoStatus.Held, AutoSyncReasons.WatchUnavailable);
            // This pair alone stops. A later explicit kick rebuilds its loop; the host and other pairs survive.
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
        if (_held is not null)
        {
            Publish(AutoStatus.Held, _held);
            // Watch problems are retried at the safety poll; access and refusals wait for the user (Kick).
            var wake = await WaitAsync([], _heldUntilKick ? null : _in.SafetyInterval(), stop).ConfigureAwait(false);
            if (wake.Kind == WakeKind.Timeout) _held = null;
            return false;
        }
        if (_retryAt is { } retryAt)
        {
            Publish(AutoStatus.Waiting, null);
            // Watches are not waited on here: their signals stay latched and never bring a retry forward.
            var wake = await WaitAsync([], retryAt - _in.Time.GetUtcNow(), stop).ConfigureAwait(false);
            if (wake.Kind == WakeKind.Kick) return false;
            _retryAt = null;
            return await ArmAsync(pair, stop).ConfigureAwait(false);
        }
        return await ArmAsync(pair, stop).ConfigureAwait(false) && await TriggeredAsync(stop).ConfigureAwait(false);
    }

    // Waits for a change, the safety poll or a Kick. A new watch starts with a signal pending, so startup, Kick,
    // the safety poll (which rebuilds the watches) and a dead-watch rebuild each lead to one settled run.
    private async Task<bool> TriggeredAsync(CancellationToken stop)
    {
        Publish(AutoStatus.Idle, null);
        AnnounceIdle();
        var wake = await WaitAsync(_watches, _in.SafetyInterval(), stop).ConfigureAwait(false);
        if (wake.Kind == WakeKind.Signal) return await SettleAsync(stop).ConfigureAwait(false);
        if (wake.Kind == WakeKind.Timeout) DisposeWatches();
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
            return true;
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            // Includes a provider's own cancellation (a timeout): only the loop's stop token means "stopping".
            DisposeWatches();
            Hold(error);
            return false;
        }
    }

    private async Task RunOnceAsync(CancellationToken stop)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_lock)
        {
            // Admission: a pause or turn-off that landed after the last check cancels before Run takes its gate;
            // one that lands later reaches this source through Kick, and Run re-checks the token under its gate.
            _runSource = source;
            if (_in.Ineligible(_in.Store.Pair(_pairId)) is not null) source.Cancel();
        }
        Publish(AutoStatus.Running, null);
        try
        {
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
            source.Dispose();
        }
    }

    // Retry when something failed or could not be verified (outcome failed, or partial with failures); a run whose
    // only leftover is held rows waits for the user's decision, not for a retry.
    private void AfterReport(RunReport report)
    {
        // The report carries its real completion instant, so the tray announces this run at its true time and can
        // order two completions without inventing a timestamp of its own.
        if (_last is { Deferred: > 0 } previous && report.Deferred < previous.Deferred)
            _backoff = TimeSpan.Zero; // A bounded backlog is draining; don't exponentially slow useful progress.
        _last = report with { CompletedUtc = _in.Time.GetUtcNow() };
        if (report.Outcome == RunOutcomes.Stopped) return;
        if (report.Outcome == RunOutcomes.Failed || report.FailureCount > 0 || report.Failures.Count > 0 || report.Deferred > 0 ||
            report.DeletionEvidencePending.Count > 0) ScheduleRetry();
        else _backoff = TimeSpan.Zero;
    }

    private void ScheduleRetry()
    {
        var ceiling = _in.SafetyInterval();
        _backoff = _backoff == TimeSpan.Zero ? _in.Settings.AutoRetryDelay : _backoff + _backoff;
        if (_backoff > ceiling) _backoff = ceiling;
        _retryAt = _in.Time.GetUtcNow() + _backoff;
    }

    private void RecordFailure(Exception error)
    {
        try
        {
            _in.Store.AddNotices([new Notice(_pairId, 0, NoticeKind.Info, string.Empty,
                "Automatic sync could not finish. Inspect status for the retry deadline or reconnect options; other pairs continue. " +
                error.GetType().Name, "autosync-failed")]);
        }
        catch (Exception) { /* A damaged/unavailable notice store cannot terminate another pair or the host. */ }
    }

    private void Hold(Exception error)
    {
        RecordFailure(error);
        _heldUntilKick = error is EndpointAccessException;
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
                Hold(wake.Error!);
        }
        if (wake.Kind != WakeKind.Kick) return wake;
        lock (_lock) _kick = NewKick();
        _held = null;
        _retryAt = null;
        _backoff = TimeSpan.Zero;
        _deadWatchRebuilds = 0;
        DisposeWatches();
        return wake;
    }

    private void DisposeWatches()
    {
        foreach (var watch in _watches) watch.Dispose();
        _watches.Clear();
    }

    private void Publish(AutoStatus status, NoticeReason? reason) =>
        _in.Publish(new PairAutoState(_pairId, status, reason, status == AutoStatus.Waiting ? _retryAt : null, _last)
        {
            NextRunUtc = status == AutoStatus.Idle ? _in.Time.GetUtcNow() + _in.SafetyInterval() :
                status == AutoStatus.Waiting ? _retryAt ?? _in.Time.GetUtcNow() + _in.Settings.ChangeQuietWindow : null
        });

    private static TaskCompletionSource NewKick() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
