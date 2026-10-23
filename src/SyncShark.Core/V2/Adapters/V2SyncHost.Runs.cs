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
        // One token and one completion task cover the whole batch: a stop cancels the batch even between pairs,
        // and a checkpoint waiter registered on any pair completes once, at the end of the batch.
        var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delivery = onProgress is null ? null : new ProgressDelivery(onProgress, ProgressNotShown);
        BeginBatch(batch);
        try
        {
            var results = new System.Collections.Concurrent.ConcurrentDictionary<int, SyncAuthorityBatchItemResult>();
            var stopped = 0;
            // Capture mapping inputs before any worker can report fatal persistence damage.
            var selected = pairs.Select(pair => (Pair: pair, Definition: _store.Pair(pair.Id) ??
                throw new InvalidDataException("The folder pair " + pair.Id + " is no longer available."))).ToArray();
            var work = selected.Select((entry, index) => RunPairAsync(entry.Pair, entry.Definition, index)).ToArray();
            await Task.WhenAll(work).ConfigureAwait(false);

            async Task CancelBatchAsync()
            {
                Interlocked.Exchange(ref stopped, 1);
                try { await batch.CancelAsync().ConfigureAwait(false); }
                catch (Exception cancellationError) { RunOutput.Observe(cancellationError); }
            }

            async Task RunPairAsync(SyncPair pair, PairDef pairDef, int index)
            {
                if (batch.IsCancellationRequested) { Interlocked.Exchange(ref stopped, 1); return; }
                // Each concurrent pair checks durable global/account/pair pause at its own
                // first safe point; a paused scope never acquires new effect authority.
                if (ActiveRun?.Paused == true) return;
                var started = _time.GetUtcNow();
                var snapshot = new SyncActiveRunSnapshot(Guid.NewGuid().ToString("N"), "sync", pair.Id, started, false, false, null);
                if (!SetActivePair(snapshot)) return;
                try
                {
                    RunReport? report = null;
                    SyncAuthorityBatchItemResult? unfinished = null;
                    // The app's runs open a run log too (RQ-130), under the same RunLog owner as the CLI.
                    using var log = RunLog.Open(SyncDataLocation.ProductRoot, pair.Name, "sync", verbose: false);
                    using var observation = _observed.Begin();
                    using var pause = PairPause.Begin(_store, pair.Id, PauseRequested(pair));
                    try
                    {
                        report = await _run.IncrementalAsync(pair.Id,
                            new LoggedProgress(new ProgressBridge(this, snapshot.Id, pair, started, delivery), log),
                            batch.Token).ConfigureAwait(false);
                    }
                    catch (SyncRunRefusedException error)
                    {
                        unfinished = new SyncAuthorityBatchItemResult(pair, null, "refused", error.Message);
                    }
                    catch (OperationCanceledException) when (batch.IsCancellationRequested)
                    {
                        // A stop or the caller's cancellation ends the run as stopped work, not as a failure.
                        Interlocked.Exchange(ref stopped, 1);
                    }
                    catch (OperationCanceledException error)
                    {
                        // A cancellation the batch did not ask for (a provider timeout) is this pair's failure.
                        unfinished = new SyncAuthorityBatchItemResult(pair, null, "failed", error.Message);
                    }
                    catch (Exception error) when (!IsFatalStore(error))
                    {
                        unfinished = new SyncAuthorityBatchItemResult(pair, null, "failed", error.Message);
                    }
                    if (unfinished is not null)
                    {
                        results[index] = unfinished;
                        return;
                    }
                    if (report is null) return;
                    // Fatal and stopped reports stop this batch: signal siblings before projecting retained
                    // counts. Never consult damaged storage or replace a typed report with an exception.
                    if (report.Outcome is RunOutcomes.StateDamaged or RunOutcomes.Stopped ||
                        report.HaltedBy is { } halted && IsFatalStore(halted))
                        await CancelBatchAsync().ConfigureAwait(false);
                    results[index] = RunResultMapping.ToItem(pair, pairDef, report, observation.Take());
                    var summary = Summary(pair, report);
                    try { admissionProgress?.Invoke(summary); }
                    catch (Exception) { /* Reporting a completed pair cannot fail it or skip later pairs. */ }
                }
                catch (Exception error) when (IsFatalStore(error))
                {
                    await CancelBatchAsync().ConfigureAwait(false);
                    throw;
                }
                finally { EndActivePair(snapshot.Id); }
            }
            return new SyncAuthorityBatchResult(results.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray(), stopped != 0);
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
                var done = EndBatch(batch);
                batch.Dispose();
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
        KeyValuePair<CancellationTokenSource, TaskCompletionSource>[] batches;
        lock (_activeGate)
        {
            batches = _batches.ToArray();
            foreach (var id in _activeRuns.Keys.ToArray())
                _activeRuns[id] = _activeRuns[id] with { StopRequested = true };
            _active = _activeRuns.Values.LastOrDefault();
        }
        if (batches.Length == 0) return false;
        var cancellations = new List<Task>();
        foreach (var batch in batches)
            try { cancellations.Add(batch.Key.CancelAsync()); }
            catch (ObjectDisposedException) { }
        await Task.WhenAll(cancellations.Concat(batches.Select(batch => batch.Value.Task)))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<bool?> SetActiveRunPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Global/account preferences own durable scope state. Never turn that scope into pair_pause.
        lock (_activeGate)
        {
            if (_active is null) return Task.FromResult<bool?>(null);
            _manualPaused = paused;
            foreach (var id in _activeRuns.Keys.ToArray())
                _activeRuns[id] = _activeRuns[id] with { Paused = paused };
            _active = _activeRuns.Values.LastOrDefault();
            return Task.FromResult<bool?>(paused);
        }
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
            .Sum(pair => _store.CountUnresolvedIntents(pair.PairId));
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
            _batches.Add(source, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _batchDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private bool SetActivePair(SyncActiveRunSnapshot snapshot)
    {
        lock (_activeGate)
        {
            // A pause written between the caller's check and this start is carried over, never overwritten:
            // SetActiveRunPausedAsync writes under this same lock, so the next pair cannot run past it.
            if (_manualPaused) return false;
            _activeRuns.Add(snapshot.Id, snapshot);
            _active = snapshot;
            return true;
        }
    }

    // The durable pause rule for one pair: the global pause or either side's account pause, read fresh before
    // each pair starts. A host with no preference source is never paused.
    private bool ScopePaused(SyncPair pair) => _store.Pair(pair.Id) is { } def && Paused(def);

    // Like CachedPairPaused, sample durable scope state every two seconds (retry failed reads at the next
    // safe point). The cache is per pair run; each starts fresh. Preference/store I/O never holds _activeGate.
    private Func<bool> PauseRequested(SyncPair pair)
    {
        var gate = new object();
        var loaded = false;
        var paused = false;
        long readAt = 0;
        return () =>
        {
            if (ActiveRun?.Paused == true) return true;
            lock (gate)
            {
                var now = _time.GetTimestamp();
                if (!loaded || _time.GetElapsedTime(readAt, now) >= TimeSpan.FromSeconds(2))
                {
                    try
                    {
                        paused = ScopePaused(pair);
                        readAt = now;
                        loaded = true;
                    }
                    catch (Exception error)
                    {
                        // A damaged or temporarily unreadable preference/store must not fail this pair.
                        // Keep the last value and timestamp so the next safe point retries the read.
                        RunOutput.Observe(error);
                    }
                }
                return paused;
            }
        };
    }

    private static bool IsFatalStore(Exception error) => error is StateDamagedException or StateNeedsFreshDatabaseException ||
        error is Microsoft.Data.Sqlite.SqliteException sql && StoreSchema.IsDamaged(sql);

    private void SetActiveProgress(string runId, SyncAuthorityBatchProgress progress)
    {
        lock (_activeGate)
        {
            if (!_activeRuns.TryGetValue(runId, out var snapshot)) return;
            _active = _activeRuns[runId] = snapshot with { Progress = progress };
        }
    }

    private void EndActivePair(string runId)
    {
        lock (_activeGate)
        {
            _activeRuns.Remove(runId);
            // Final progress can still be queued for the window. Keep that last snapshot while
            // its batch drains delivery; prefer a genuinely running pair whenever one remains.
            _active = _activeRuns.Values.LastOrDefault() ?? _active;
        }
    }

    private TaskCompletionSource EndBatch(CancellationTokenSource source)
    {
        lock (_activeGate)
        {
            var done = _batches[source];
            _batches.Remove(source);
            if (_batches.Count == 0)
            {
                _active = null;
                _manualPaused = false;
                _batchDone?.TrySetResult();
                _batchDone = null;
            }
            return done;
        }
    }

    private static string Summary(SyncPair pair, RunReport report) =>
        $"{pair.Name} ({report.Outcome}): {report.Uploaded} uploaded, {report.Downloaded} downloaded, {report.Deleted} deleted, " +
        $"{report.Moved} moved, {report.KeptBoth} kept both, {report.Held} held, {report.Failed} failed, {report.Pending} pending, {report.Deferred} deferred." +
        string.Concat(report.IndexingRemainingByRoot.Where(root => root.Value > 0)
            .Select(root => $" Indexing in progress ({root.Key}): {root.Value} remaining.")) +
        (report.PendingVerification > 0 ? $" {report.PendingVerification} changes pending background verification." : string.Empty);

    // A failed progress delivery is named on the host's log-error channel, which the window shows with the log.
    private void ProgressNotShown(Exception error) =>
        Volatile.Write(ref _progressError, EdgeMessages.Text("progressNotShown", error.Message));

    // The run reports progress through a synchronous IProgress on its worker; the report only updates the snapshot
    // and queues the window's copy, so the window's callback never runs on (or blocks) the run worker.
    private sealed class ProgressBridge(V2SyncHost host, string runId, SyncPair pair, DateTimeOffset started,
        ProgressDelivery? delivery) : IProgress<RunProgress>
    {
        public void Report(RunProgress value)
        {
            var progress = RunResultMapping.ToProgress(pair, value, started, host._time.GetUtcNow());
            host.SetActiveProgress(runId, progress);
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
