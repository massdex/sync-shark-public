namespace Massdex.SyncShark.V2;

/// <summary>Host lifetime, one independent indexing run per local root. Sync runs never join these tasks.</summary>
internal sealed class LocalIndexingRuns : IAsyncDisposable
{
    private readonly IStateStore _store;
    private readonly Func<RootDef, CancellationToken, Task<int>> _inventory;
    private readonly LocalGapWorker _gaps;
    private readonly ILocalGapIo _io;
    private readonly Func<RootDef, NtfsUsnCheckpoint?> _checkpoint;
    private readonly object _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Stop, Task<string> Task)> _roots = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task? _host;
    private Func<PairDef, bool> _paused = _ => false;
    private Action<string> _wake = _ => { };

    internal LocalIndexingRuns(IStateStore store, Func<RootDef, CancellationToken, Task<int>> inventory,
        LocalGapWorker gaps, ILocalGapIo io, Func<RootDef, NtfsUsnCheckpoint?>? checkpoint = null)
    {
        _store = store; _inventory = inventory; _gaps = gaps; _io = io;
        _checkpoint = checkpoint ?? (root => NtfsUsnJournal.TryReadCheckpoint(root.RootPath, out var value) ? value : null);
    }

    internal void Start(Func<PairDef, bool> paused, Action<string> wake)
    {
        if (!_store.SupportsIndexPlanning) return;
        lock (_gate)
        {
            _paused = paused; _wake = wake;
            if (_host is { IsCompleted: false }) return;
            _stop = new CancellationTokenSource();
            var ct = _stop.Token;
            _host = Task.Run(() => HostAsync(ct));
        }
    }

    private bool Active(PairDef pair) => pair.Enabled && !_paused(pair) && !_store.PairPaused(pair.PairId);
    private bool Active(RootDef root) => _store.Pairs().Any(pair =>
        (pair.A.RootId == root.RootId || pair.B.RootId == root.RootId) && Active(pair));
    private bool Debt(RootDef root) => (_store is ILocalGapStore gaps && gaps.HasGapProtection(root.RootId)) ||
        _store.RehashPendingCount(root.RootId) != 0;

    private async Task HostAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Capture before rechecking work: a concurrent committed mutation cannot be lost.
                var version = _store.IndexingWorkVersion;
                var active = _store.Pairs().Where(Active).SelectMany(pair => new[] { pair.A, pair.B })
                    .Where(root => root.Provider == SyncProviders.Local)
                    .DistinctBy(root => root.RootId).ToDictionary(root => root.RootId, StringComparer.Ordinal);
                lock (_gate)
                {
                    foreach (var (id, entry) in _roots)
                        if (!active.ContainsKey(id)) entry.Stop.Cancel();
                    foreach (var id in _roots.Where(row => row.Value.Task.IsCompleted).Select(row => row.Key).ToArray())
                    { _roots[id].Stop.Dispose(); _roots.Remove(id); }
                    foreach (var root in active.Values)
                    {
                        if (_roots.ContainsKey(root.RootId) || !Debt(root)) continue;
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _roots[root.RootId] = (stop, Task.Run(() => RunRootAsync(root, stop.Token, continuous: true)));
                    }
                }
                await _store.WaitForIndexingWorkAsync(version, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            Task[] tasks;
            lock (_gate)
            {
                foreach (var entry in _roots.Values) entry.Stop.Cancel();
                tasks = _roots.Values.Select(entry => entry.Task).ToArray();
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    /// <summary>One bounded CLI pass. An already-running host task is signalled, never joined.</summary>
    internal async Task<string> RunAsync(RootDef root, CancellationToken ct)
    {
        if (!_store.SupportsIndexPlanning) return "unsupported";
        if (ct.IsCancellationRequested) return "stopped";
        if (!Active(root)) return "paused";
        Task<string> task;
        lock (_gate)
        {
            if (_roots.TryGetValue(root.RootId, out var entry) && !entry.Task.IsCompleted) return "pending";
            if (_roots.Remove(root.RootId, out var finished)) finished.Stop.Dispose();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            task = Task.Run(() => RunRootAsync(root, stop.Token, continuous: false));
            _roots[root.RootId] = (stop, task);
        }
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = WatchPauseAsync();
        try
        {
            var outcome = await task.ConfigureAwait(false);
            return outcome == "stopped" && !ct.IsCancellationRequested && !Active(root) ? "paused" : outcome;
        }
        finally
        {
            monitorStop.Cancel();
            await monitor.ConfigureAwait(false);
        }

        async Task WatchPauseAsync()
        {
            try
            {
                while (!monitorStop.IsCancellationRequested)
                {
                    var version = _store.IndexingWorkVersion;
                    if (!Active(root))
                    {
                        lock (_gate)
                            if (_roots.TryGetValue(root.RootId, out var current) && current.Task == task)
                                current.Stop.Cancel();
                        return;
                    }
                    await _store.WaitForIndexingWorkAsync(version, monitorStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        }
    }

    private async Task<string> RunRootAsync(RootDef root, CancellationToken ct, bool continuous)
    {
        using var admission = StoreAdmission.WithCancellation(ct);
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                if (!Active(root)) return "paused";
                var version = _store.IndexingWorkVersion;
                if (!Debt(root)) return "complete";
                if (_store.IndexRetryDue(root.RootId))
                {
                    try { await PassAsync(root, continuous, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (!_gaps.IsStateDamage(error))
                    {
                        _gaps.RecordFailure(root, error);
                        RunOutput.Observe(error);
                    }
                }
                if (!Active(root)) return "paused";
                if (!Debt(root))
                {
                    _store.CompleteIndexFailure(root.RootId);
                    return "complete";
                }
                if (!continuous) return "pending";
                await WaitForWorkAsync(root, version, ct).ConfigureAwait(false);
            } while (true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "stopped"; }
        catch (Exception error) when (!_gaps.IsStateDamage(error))
        {
            _gaps.RecordFailure(root, error);
            RunOutput.Observe(error);
            return "pending";
        }
        finally { await _gaps.StopAsync(root.RootId).ConfigureAwait(false); }
    }

    private async Task WaitForWorkAsync(RootDef root, long version, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_store.NextIndexRetry(root.RootId) is { } due)
        {
            var delay = due - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero) return;
            wait.CancelAfter(delay);
        }
        try { await _store.WaitForIndexingWorkAsync(version, wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && wait.IsCancellationRequested) { }
    }

    private async Task PassAsync(RootDef root, bool continuous, CancellationToken ct)
    {
        var dirtyBefore = _store.IndexPlanningWatermark(root.RootId);
        if (_store is ILocalGapStore gaps)
        {
            if (continuous) _gaps.Start(root);
            else if (gaps.GapEpoch(root.RootId) is { } epoch)
                await Task.WhenAll(Task.Run(() => _gaps.Discover(root, epoch, ct), ct),
                    _gaps.HashPage(root, epoch, ct)).ConfigureAwait(false);
            LocalGapWorker.PromoteDiscovery(gaps, root, ct, _gaps.IsStateDamage);
            LocalGapWorker.Promote(gaps, _store, _io, root, gaps.GapResultWatermark(root.RootId),
                _checkpoint(root), _ => true, _ => { }, ct, _gaps.IsStateDamage);
        }
        await _inventory(root, ct).ConfigureAwait(false);
        // Selection is bounded by entities; the existing planning owner pages aliases later.
        var ready = _store.RehashCandidates(root.RootId, 512, _store.RehashWatermark(root.RootId), needsVerification: false);
        var published = _store.PublishIndexPlanning(root.RootId, ready);
        if (published > 0 || _store.IndexPlanningWatermark(root.RootId) != dirtyBefore)
            foreach (var pair in _store.Pairs().Where(pair => pair.A.RootId == root.RootId || pair.B.RootId == root.RootId))
                _wake(pair.PairId);
    }

    internal async Task StopAsync()
    {
        Task? host;
        Task[] tasks;
        lock (_gate)
        {
            _stop?.Cancel(); host = _host;
            foreach (var entry in _roots.Values) entry.Stop.Cancel();
            tasks = _roots.Values.Select(entry => entry.Task).ToArray();
        }
        if (host is not null) await host.ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var entry in _roots.Values) entry.Stop.Dispose();
            _roots.Clear(); _stop?.Dispose(); _stop = null; _host = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
