namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns finite live opportunities and session-only pause; host owns all operation and source lifetimes.</summary>
internal sealed class AutoSync
{
    private readonly object _gate = new();
    private readonly IStateStore _store;
    private readonly Func<string, RunLog?> _openLog;
    private readonly EngineHost _host;
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _active = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = NewSignal();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private volatile PauseSnapshot _pause = new(false, new HashSet<string>(StringComparer.Ordinal));
    private Exception? _failure;

    internal AutoSync(IStateStore store, Func<string, RunLog?> openLog, EngineHost host)
    {
        _store = store;
        _openLog = openLog;
        _host = host;
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _stop = new CancellationTokenSource();
            Kick(null);
            _loop = RunAsync(_stop.Token);
        }
    }

    internal void Kick(string? pairId, bool receiptsOnly = false)
    {
        var ids = pairId is null ? _store.Pairs().Select(pair => pair.PairId) : [pairId];
        lock (_gate)
        {
            if (_stop?.IsCancellationRequested == true) return;
            foreach (var id in ids)
                _pending[id] = receiptsOnly && (!_pending.TryGetValue(id, out var prior) || prior);
            Signal();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task changed;
                List<(string PairId, bool ReceiptsOnly, TaskCompletionSource Completion)> starts = [];
                lock (_gate)
                {
                    if (_failure is { } failure) throw failure;
                    foreach (var (id, receiptsOnly) in _pending.ToArray())
                    {
                        if (_active.ContainsKey(id)) continue;
                        _pending.Remove(id);
                        if (IsPaused(id)) continue;
                        var completion = NewSignal();
                        _active.Add(id, completion.Task);
                        starts.Add((id, receiptsOnly, completion));
                    }
                    changed = _changed.Task;
                }
                foreach (var start in starts) _ = ObserveAsync(start.PairId, start.ReceiptsOnly, start.Completion, ct);
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ObserveAsync(string pairId, bool receiptsOnly, TaskCompletionSource completion, CancellationToken ct)
    {
        try { await OpportunityAsync(pairId, receiptsOnly, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || IsPaused(pairId)) { }
        catch (LiveAdmissionStaleException) { }
        catch (EngineRequestException) { }
        catch (Exception error)
        {
            lock (_gate) _failure ??= error;
            _host.BackgroundFailed(error);
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(pairId);
                completion.TrySetResult();
                Signal();
            }
        }
    }

    private async Task OpportunityAsync(string pairId, bool receiptsOnly, CancellationToken ct)
    {
        var current = _store.ReadPairExecution(pairId);
        if (current is null || _host.Admission(pairId, current.Value.Pair) != EngineHost.PairAdmission.Admitted ||
            _store.ReadLiveRefusal(pairId) == LiveRefusal.Pair) return;
        var generation = current.Value.Generation;
        Massdex.SyncShark.V2.FailureRetryOwner.State? admitted = null;
        // A pre-deadline item wait holds ordinary dispatch; receipt reconciliation is never held.
        if (!receiptsOnly && _host.ItemRetryHolds(pairId, generation, out admitted)) return;
        await _host.DispatchSessionAsync(pairId, () => _openLog(pairId), async (session, token) =>
        {
            try
            {
                if (receiptsOnly)
                {
                    if (await session.Syncer.ReconcileReceiptsAsync(token, progressOnly: true).ConfigureAwait(false))
                        Kick(pairId);
                }
                else
                {
                    await session.Syncer.SyncWorkAsync(token).ConfigureAwait(false);
                    // A normal return means no selectable work remained only while the host still admits this
                    // dispatch: a drain refuses lanes before cancellation reaches the token, and its state stays set
                    // until retirement. Only the exact admitted wait is cleared.
                    if (admitted is { } drained && !token.IsCancellationRequested)
                        _store.CompleteItemRetry(pairId, generation, drained);
                }
                _host.ScheduleFolderPoll(pairId, _store.HasFolderJobProgress(pairId, token),
                    () => Kick(pairId, receiptsOnly: true));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
            }
            return session.Pair;
        }, ct).ConfigureAwait(false);
    }

    internal bool IsGloballyPaused => _pause.Global;

    internal bool IsPaused(string pairId)
    {
        var snapshot = _pause;
        return snapshot.Global || snapshot.Pairs.Contains(pairId);
    }
    internal void PublishPause(string? pairId, bool paused)
    {
        var prior = _pause;
        if (pairId is null) _pause = new PauseSnapshot(paused, prior.Pairs);
        else
        {
            var pairs = new HashSet<string>(prior.Pairs, StringComparer.Ordinal);
            if (paused) pairs.Add(pairId); else pairs.Remove(pairId);
            _pause = new PauseSnapshot(prior.Global, pairs);
        }
    }

    internal async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? stop;
        lock (_gate) { loop = _loop; stop = _stop; }
        if (loop is null) return;
        await stop!.CancelAsync().ConfigureAwait(false);
        try { await loop.ConfigureAwait(false); }
        finally
        {
            Task[] active;
            lock (_gate) active = _active.Values.ToArray();
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }
    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record PauseSnapshot(bool Global, IReadOnlySet<string> Pairs);
}
