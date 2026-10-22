using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Continuous sync: one loop per stored pair turns watch signals, Kicks, retries and the safety poll into automatic
/// incremental runs (continuous-sync.md). It makes no sync decisions and uses contracts only.
/// </summary>
public sealed class AutoSync
{
    private readonly AutoSyncInputs _inputs;
    private readonly object _lock = new();
    private readonly Dictionary<string, (PairLoop Loop, Task Task)> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PairAutoState> _states = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;

    /// <summary>Creates continuous sync over the store, run and watch source; the edge supplies pause and poll interval.</summary>
    public AutoSync(IStateStore store, ISyncRun run, IChangeWatchSource watches, V2Settings settings,
        Func<PairDef, bool> paused, Func<TimeSpan> safetyPoll, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(watches);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paused);
        ArgumentNullException.ThrowIfNull(safetyPoll);
        ArgumentNullException.ThrowIfNull(time);
        _inputs = new AutoSyncInputs(store, run, watches, settings, paused, safetyPoll, time, Publish);
    }

    /// <summary>Raised on every state change of a pair (tray colour, run balloon).</summary>
    public event Action<PairAutoState>? Changed;

    /// <summary>The latest state of every pair, ordered by pair id.</summary>
    public IReadOnlyList<PairAutoState> States =>
        _states.Values.OrderBy(state => state.PairId, StringComparer.Ordinal).ToArray();

    /// <summary>Starts a loop for every stored pair; a second call does nothing.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_stop is not null) return;
            _stop = new CancellationTokenSource();
            StartMissingLoops(_stop.Token);
        }
    }

    /// <summary>
    /// Re-evaluates one pair (or all, and picks up new pairs, when null): clears holds, retries and backoff, rebuilds
    /// the watches and gives one run; a pair now paused or off has its automatic run cancelled.
    /// </summary>
    public void Kick(string? pairId)
    {
        lock (_lock)
        {
            if (_stop is null) return;
            StartMissingLoops(_stop.Token);
            foreach (var (id, entry) in _loops)
                if (pairId is null || string.Equals(id, pairId, StringComparison.Ordinal))
                    entry.Loop.Kick();
        }
    }

    /// <summary>Stops every loop, cancelling automatic runs, and waits for them to finish.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stop;
        Task[] loops;
        lock (_lock)
        {
            stop = _stop;
            _stop = null;
            loops = _loops.Values.Select(entry => entry.Task).ToArray();
            _loops.Clear();
        }
        if (stop is null) return;
        stop.Cancel();
        await Task.WhenAll(loops).ConfigureAwait(false);
        stop.Dispose();
    }

    private void StartMissingLoops(CancellationToken stop)
    {
        foreach (var finished in _loops.Where(entry => entry.Value.Task.IsCompleted).Select(entry => entry.Key).ToArray())
        {
            // A loop ends when its pair is removed; its state goes with it.
            _loops.Remove(finished);
            _states.TryRemove(finished, out _);
        }
        foreach (var pair in _inputs.Store.Pairs())
        {
            if (_loops.ContainsKey(pair.PairId)) continue;
            var loop = new PairLoop(pair.PairId, _inputs);
            _loops[pair.PairId] = (loop, Task.Run(() => SuperviseAsync(pair.PairId, loop, stop), CancellationToken.None));
        }
    }

    private async Task SuperviseAsync(string pairId, PairLoop loop, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await loop.RunAsync(stop).ConfigureAwait(false);
                if (stop.IsCancellationRequested || _inputs.Store.Pair(pairId) is null) return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { RunOutput.Observe(error); }
            try { await Task.Delay(_inputs.Settings.MinSafetyPoll, _inputs.Time, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
    }

    private void Publish(PairAutoState state)
    {
        _states[state.PairId] = state;
        if (Changed is not { } changed) return;
        foreach (Action<PairAutoState> subscriber in changed.GetInvocationList())
        {
            try { subscriber(state); }
            catch (Exception) { /* A display subscriber must not terminate the pair loop or skip other subscribers. */ }
        }
    }
}
