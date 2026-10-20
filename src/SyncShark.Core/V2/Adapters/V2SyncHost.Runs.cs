namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    /// <inheritdoc />
    public bool HasActiveRun
    {
        get { lock (_activeGate) return _batchDone is not null; }
    }

    /// <inheritdoc />
    public SyncActiveRunSnapshot? ActiveRun
    {
        get { lock (_activeGate) return _active; }
    }

    /// <inheritdoc />
    public bool AuthorityStateExists => File.Exists(_databasePath);

    /// <inheritdoc />
    public string? ActivityLogError => Volatile.Read(ref _progressError);

    /// <inheritdoc />
    public bool ActivityLogIncomplete => ActivityLogError is not null;

    /// <inheritdoc />
    public async Task<SyncAuthorityBatchResult> RunPairsAsync(IEnumerable<SyncPair> pairs, string trigger,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default,
        Action<string>? admissionProgress = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A sync is already running.");
        // One token and one completion task cover the whole batch: a stop cancels the batch even between pairs,
        // and a checkpoint waiter registered on any pair completes once, at the end of the batch.
        var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delivery = onProgress is null ? null : new ProgressDelivery(onProgress, ProgressNotShown);
        BeginBatch(batch);
        try
        {
            var items = new List<SyncAuthorityBatchItemResult>();
            var stopped = false;
            foreach (var pair in pairs)
            {
                if (stopped || batch.IsCancellationRequested) { stopped = true; break; }
                var started = _time.GetUtcNow();
                SetActivePair(new SyncActiveRunSnapshot(Guid.NewGuid().ToString("N"), "sync", pair.Id, started,
                    false, false, null));
                RunReport? report = null;
                SyncAuthorityBatchItemResult? unfinished = null;
                // The app's runs open a run log too (RQ-130), under the same RunLog owner as the CLI.
                using var log = RunLog.Open(SyncDataLocation.ProductRoot, pair.Name, "sync", verbose: false);
                using var observation = _observed.Begin();
                try
                {
                    report = await _run.IncrementalAsync(pair.Id,
                        new LoggedProgress(new ProgressBridge(this, pair, started, delivery), log),
                        batch.Token).ConfigureAwait(false);
                }
                catch (SyncRunRefusedException error)
                {
                    unfinished = new SyncAuthorityBatchItemResult(pair, null, "refused", error.Message);
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested)
                {
                    // A stop or the caller's cancellation ends the run as stopped work, not as a failure.
                    stopped = true;
                }
                catch (OperationCanceledException error)
                {
                    // A cancellation the batch did not ask for (a provider timeout) is this pair's failure.
                    unfinished = new SyncAuthorityBatchItemResult(pair, null, "failed", error.Message);
                }
                if (unfinished is not null)
                {
                    items.Add(unfinished);
                    continue;
                }
                if (report is null) break;
                var pairDef = _store.Pair(pair.Id) ??
                    throw new InvalidDataException("The folder pair " + pair.Id + " is no longer available.");
                items.Add(RunResultMapping.ToItem(pair, pairDef, report, observation.Take()));
                var summary = Summary(pair, report);
                try { admissionProgress?.Invoke(summary); }
                catch (Exception) { /* Reporting a completed pair cannot fail it or skip later pairs. */ }
                if (string.Equals(report.Outcome, RunOutcomes.Stopped, StringComparison.Ordinal)) stopped = true;
            }
            return new SyncAuthorityBatchResult(items, stopped);
        }
        finally
        {
            try
            {
                // Every queued report reaches the window before the batch counts as finished, unless the window
                // does not take it within the bound; then the batch ends anyway and the gap is named.
                if (delivery is not null &&
                    !await delivery.CompleteAsync(_settings.ProgressDrainTimeout).ConfigureAwait(false))
                    Volatile.Write(ref _progressError, EdgeMessages.Text("progressAbandoned"));
            }
            finally
            {
                var done = EndBatch();
                batch.Dispose();
                _runGate.Release();
                done?.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public Task<SyncAuthorityBatchResult> RecoverPairsAsync(IEnumerable<SyncPair> pairs,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default,
        Action<string>? admissionProgress = null)
    {
        // Core v2 reconciles recorded intents at the start of every run, so there is nothing else to recover.
        return Task.FromResult(new SyncAuthorityBatchResult([], false));
    }

    /// <inheritdoc />
    public async Task<bool> StopActiveRunAsync(CancellationToken cancellationToken = default)
    {
        // Stop acts only on the batch active now: a later batch has its own token and completion task.
        CancellationTokenSource? batch;
        TaskCompletionSource? done;
        lock (_activeGate)
        {
            batch = _batchSource;
            done = _batchDone;
            if (batch is not null && _active is not null) _active = _active with { StopRequested = true };
        }
        if (batch is null || done is null) return false;
        try
        {
            await batch.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // That batch already ended; its completion task below is (about to be) complete.
        }
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<bool?> SetActiveRunPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasActiveRun) throw EdgeMessages.NotInV2("pausingRun");
        return Task.FromResult<bool?>(null);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForActiveRunCheckpointAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? done;
        lock (_activeGate) done = _batchDone;
        if (done is null) return true;
        var finished = await Task.WhenAny(done.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false)
            == done.Task;
        cancellationToken.ThrowIfCancellationRequested();
        return finished;
    }

    /// <inheritdoc />
    public Task<int> LoadPendingQueueCountAsync(CancellationToken cancellationToken = default,
        string? connectionId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = _store.Pairs().Where(pair => InScope(pair, connectionId))
            .Sum(pair => _store.UnresolvedIntents(pair.PairId).Count);
        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> LoadPairIdsNeedingAttentionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlySet<string> ids = _store.OpenNotices(null)
            .Where(notice => notice.Kind == NoticeKind.UserDecision)
            .Select(notice => notice.PairId)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(ids);
    }

    private void BeginBatch(CancellationTokenSource source)
    {
        lock (_activeGate)
        {
            _batchSource = source;
            _batchDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void SetActivePair(SyncActiveRunSnapshot snapshot)
    {
        lock (_activeGate) _active = snapshot;
    }

    private void SetActiveProgress(SyncAuthorityBatchProgress progress)
    {
        lock (_activeGate)
        {
            if (_active is not null) _active = _active with { Progress = progress };
        }
    }

    private TaskCompletionSource? EndBatch()
    {
        TaskCompletionSource? done;
        lock (_activeGate)
        {
            _active = null;
            done = _batchDone;
            _batchDone = null;
            _batchSource = null;
        }
        return done;
    }

    private static string Summary(SyncPair pair, RunReport report) =>
        $"{pair.Name}: {report.Uploaded} uploaded, {report.Downloaded} downloaded, {report.Deleted} deleted, " +
        $"{report.Moved} moved, {report.KeptBoth} kept both, {report.Held} held, {report.Failed} failed.";

    // A failed progress delivery is named on the host's log-error channel, which the window shows with the log.
    private void ProgressNotShown(Exception error) =>
        Volatile.Write(ref _progressError, EdgeMessages.Text("progressNotShown", error.Message));

    // The run reports progress through a synchronous IProgress on its worker; the report only updates the snapshot
    // and queues the window's copy, so the window's callback never runs on (or blocks) the run worker.
    private sealed class ProgressBridge(V2SyncHost host, SyncPair pair, DateTimeOffset started,
        ProgressDelivery? delivery) : IProgress<RunProgress>
    {
        public void Report(RunProgress value)
        {
            var progress = RunResultMapping.ToProgress(pair, value, started, host._time.GetUtcNow());
            host.SetActiveProgress(progress);
            delivery?.Post(progress);
        }
    }

    // Records each run line in the pair's run log while forwarding it unchanged to the window's progress bridge.
    private sealed class LoggedProgress(IProgress<RunProgress> inner, RunLog log) : IProgress<RunProgress>
    {
        public void Report(RunProgress value)
        {
            try
            {
                log.Summary(RunLog.Line(value));
            }
            catch (Exception error) { RunOutput.Observe(error); }
            inner.Report(value);
        }
    }
}
