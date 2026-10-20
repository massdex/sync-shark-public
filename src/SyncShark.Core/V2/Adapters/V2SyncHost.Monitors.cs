namespace Massdex.SyncShark.V2;

// Continuous sync's edge mappings (continuous-sync.md section 3): the window's monitor members drive AutoSync and
// read its states, the tray and run balloon follow its Changed event, and the app's power/network hooks kick it.
public sealed partial class V2SyncHost
{
    private readonly object _monitorGate = new();
    private AutoSync? _autoSync;
    private bool _monitorsActive;
    private ISystemWakeSource? _wakeSource;

    /// <summary>Raised for every continuous-sync state change; the tray colour and run balloon follow it.</summary>
    public event Action<PairAutoState>? AutoSyncChanged;

    /// <summary>
    /// Starts (or restarts) continuous sync and reports one "Monitoring" result per enabled pair. A host with no
    /// production composition (a fixture) answers empty, exactly as it did before continuous sync was wired.
    /// </summary>
    public Task<IReadOnlyList<SyncAuthorityMonitorStartResult>> ConfigureMonitorsAsync(IEnumerable<SyncPair> pairs,
        Func<IReadOnlySet<string>, Task<bool>> onDebouncedRunRequested, Func<string, Task>? onHealthChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(onDebouncedRunRequested);
        cancellationToken.ThrowIfCancellationRequested();
        if (Composition is not { } composition)
            return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
        var auto = composition.StartAutoSync(Paused, SafetyPoll);
        lock (_monitorGate)
        {
            if (_autoSync is null)
            {
                _autoSync = auto;
                auto.Changed += OnAutoSyncChanged;
            }
            _monitorsActive = true;
        }
        var results = pairs.Where(pair => pair.Enabled)
            .Select(pair => new SyncAuthorityMonitorStartResult(pair.Id, EdgeMessages.Monitor("monitoring"), null, null))
            .ToArray();
        return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>(results);
    }

    /// <summary>Stops continuous sync (awaiting it); a later configure restarts it.</summary>
    public async Task StopMonitorsAsync(CancellationToken cancellationToken = default)
    {
        AutoSync? auto;
        lock (_monitorGate)
        {
            auto = _autoSync;
            _monitorsActive = false;
        }
        if (auto is not null) await auto.StopAsync().ConfigureAwait(false);
    }

    /// <summary>The current per-pair state, or empty while continuous sync is not configured.</summary>
    public Task<IReadOnlyList<SyncAuthorityMonitorProjection>> LoadMonitorProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutoSync? auto;
        bool active;
        lock (_monitorGate)
        {
            auto = _autoSync;
            active = _monitorsActive;
        }
        if (auto is null || !active)
            return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorProjection>>([]);
        var now = _time.GetUtcNow();
        return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorProjection>>(
            auto.States.Select(state => MonitorMapping.ToProjection(state, now)).ToArray());
    }

    /// <summary>
    /// Re-evaluates one pair (or every pair when null) now. The edge calls this after a pause, a resume, a pause
    /// expiry and a pair enable or disable, so a change of eligibility is honoured at once.
    /// </summary>
    public void KickAutoSync(string? pairId) => _autoSync?.Kick(pairId);

    /// <summary>
    /// Re-evaluates every pair on one account now. An account reconnect and a timed account pause that expired both
    /// make that account's pairs eligible (or clear a hold), and neither reaches a running loop through a
    /// reconfiguration, whose Start is a no-op once continuous sync is already running.
    /// </summary>
    public void KickAutoSyncForAccount(string connectionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        if (_autoSync is not { } auto) return;
        foreach (var pair in _store.Pairs())
            if (InScope(pair, connectionId)) auto.Kick(pair.PairId);
    }

    /// <summary>
    /// Kicks every pair when the computer resumes or the network returns. The app supplies the source's platform
    /// events; the subscription is detached on <see cref="Dispose"/> (and on a later call).
    /// </summary>
    public void WatchSystemWakes(ISystemWakeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_monitorGate)
        {
            if (_wakeSource is not null) _wakeSource.Woke -= OnSystemWake;
            _wakeSource = source;
            source.Woke += OnSystemWake;
        }
    }

    private void OnSystemWake() => KickAutoSync(null);

    private void DetachWakeSource()
    {
        lock (_monitorGate)
        {
            if (_wakeSource is not null) _wakeSource.Woke -= OnSystemWake;
            _wakeSource = null;
        }
    }

    private void OnAutoSyncChanged(PairAutoState state)
    {
        if (AutoSyncChanged is not { } changed) return;
        foreach (Action<PairAutoState> subscriber in changed.GetInvocationList())
        {
            try { subscriber(state); }
            catch (Exception) { /* Display subscribers cannot stop AutoSync or one another. */ }
        }
    }

    // The pause rule the edge supplies to AutoSync: the global pause or either side's account pause, read fresh
    // each time so a resume takes effect without a restart. A host with no pause source is never paused.
    private bool Paused(PairDef pair)
    {
        if (PauseSource is not { } preferences) return false;
        var saved = preferences();
        var now = _time.GetUtcNow();
        return PauseStateProjection.GlobalPaused(saved, now)
            || PauseStateProjection.AccountPaused(saved, pair.A.AccountId, now)
            || PauseStateProjection.AccountPaused(saved, pair.B.AccountId, now);
    }

    // The safety poll and retry ceiling come from the app's safety-check interval; a fixture has no settings store
    // and floors to the engine's minimum.
    private TimeSpan SafetyPoll() => TimeSpan.FromMinutes(_v1?.LoadAutomation().IntervalMinutes ?? 0);
}
