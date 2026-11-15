namespace Massdex.SyncShark.V2;

/// <summary>Core v2's host satisfies the window's <see cref="Massdex.SyncShark.ISyncHost"/> surface.</summary>
/// <remarks>
/// Members mapped onto v2 ("M") read <see cref="IStateStore"/> or <see cref="ISyncRun"/>; members not in v2 yet
/// answer an empty query ("E") or throw the one not-in-v2 message ("X"); pass-through members ("P") forward to
/// the v1 host for accounts, account-scoped sharing and settings, never with a v2 pair id. The split by screen area is the ISync*Host
/// interfaces in this folder. Production composition is <see cref="EngineComposition.Open"/>.
/// </remarks>
public sealed partial class V2SyncHost : ISyncHost
{
    private readonly IStateStore _store;
    private readonly ISyncRun _run;
    private readonly ObservedExecutor _observed;
    private readonly SyncAuthorityRuntimeHost _v1;
    private readonly V2Settings _settings;
    private readonly TimeProvider _time;
    private readonly string _databasePath;
    private readonly object _activeGate = new();
    private readonly Dictionary<CancellationTokenSource, TaskCompletionSource> _batches = [];
    private readonly Dictionary<string, SyncActiveRunSnapshot> _activeRuns = new(StringComparer.Ordinal);
    private bool _manualPaused;
    private TaskCompletionSource? _batchDone;
    private SyncActiveRunSnapshot? _active;
    private string? _selectedPairId;
    private string? _progressError;
    // The production composition owns the store and the long-poll HTTP client; the host disposes it as a whole.
    // It is also the composition whose continuous sync the monitor mappings drive; a fixture leaves it null.
    internal EngineComposition? Composition { get; init; }
    // Continuous sync's pause source: the saved preferences the global/account pause rule reads. Production sets
    // it from the state authority; a fixture may supply its own, and a null source means never paused.
    internal Func<UiPreferences>? PauseSource { get; init; }

    /// <summary>Builds the host over the v2 pieces; the v1 host serves pass-through members only.</summary>
    public V2SyncHost(IStateStore store, ISyncRun run, ObservedExecutor observed, SyncAuthorityRuntimeHost v1,
        V2Settings settings, TimeProvider time, string databasePath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _observed = observed ?? throw new ArgumentNullException(nameof(observed));
        _v1 = v1;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
    }

    /// <summary>Opens the production host over the same v2 composition the CLI uses.</summary>
    public static V2SyncHost OpenProduction(PilotStateAuthority stateAuthority, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(stateAuthority);
        ArgumentNullException.ThrowIfNull(httpClient);
        var composition = EngineComposition.Open(
            new EngineHostInputs(SyncDataLocation.ProductRoot, stateAuthority, httpClient), V2Settings.Default,
            TimeProvider.System);
        try
        {
            var preferences = new UiPreferencesStore(stateAuthority);
            var host = new V2SyncHost(composition.Store, composition.Run, composition.Observed,
                new SyncAuthorityRuntimeHost(stateAuthority, httpClient), composition.Settings, TimeProvider.System,
                composition.DatabasePath)
            {
                Composition = composition,
                PauseSource = preferences.Load
            };
            try { host.ReconnectCloudFiles(CancellationToken.None); }
            catch { host._cloudFiles?.Dispose(); host._v1.Dispose(); throw; }
            return host;
        }
        catch
        {
            composition.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The window stops the active run and waits for its checkpoint before disposing the host, so this only
        // signals a stop (without blocking the caller) in case a run is somehow still active.
        if (HasActiveRun) _ = _run.StopAsync();
        DetachWakeSource();
        _cloudFiles?.Dispose();
        if (Composition is not null) Composition.Dispose();
        else _store.Dispose();
        _v1?.Dispose();
    }

    /// <inheritdoc />
    public Task<SyncPairSet> LoadPairsAsync(CancellationToken cancellationToken = default,
        Action<string>? startupProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defs = _store.Pairs().OrderBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var pairs = defs.Select(pair => Mapped(pair)).ToArray();
        return Task.FromResult(new SyncPairSet(pairs, Selected(defs)));
    }

    /// <inheritdoc />
    public Task<SyncPairSet> SelectPairAsync(string pairId, CancellationToken cancellationToken = default)
    {
        if (_store.Pair(pairId) is null)
            throw new InvalidDataException("The selected sync pair is not retained or available for migration.");
        _selectedPairId = pairId;
        return LoadPairsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public bool IsPairPaused(string pairId) => _store is SqliteStateStore sql && sql.PairPaused(pairId);

    public Task<bool> SetPairPausedAsync(string pairId, bool paused, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_store.Pair(pairId) is null) throw new InvalidDataException("The requested sync pair is not retained.");
        if (_store is not SqliteStateStore sql) throw new NotSupportedException("Durable pause requires SQLite.");
        sql.SetPairPaused(pairId, paused);
        // Cancellation reaches in-flight workers and queued admission immediately; effect owners
        // still retain their intents and drain safely before releasing storage.
        KickAutoSync(pairId);
        return paused;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<SyncPairSet> SetPairEnabledAsync(string pairId, bool enabled,
        CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        var pair = _store.Pair(pairId) ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (enabled && PairAdmission.DecideSaved(pair, _store.Pairs()) is { } refusal)
            throw new InvalidOperationException(refusal.Detail);
        _store.SavePair(pair with { Enabled = enabled });
        // An enable or disable changes eligibility; re-evaluate the pair now instead of waiting for a signal.
        KickAutoSync(pairId);
        return LoadPairsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncPairSet> DisableFilesOnDemandAsync(string pairId, CancellationToken cancellationToken = default,
        string? expectedConnectionId = null, long? expectedPolicyRevision = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_store.Pair(pairId) is null) throw new InvalidDataException("The requested sync pair is not retained.");
        // This host cannot prove that every pointer is resident. Keep registration/startup intact.
        throw StorageHostUnsupported();
    }

    /// <inheritdoc />
    public Task VerifyHealthAsync(CancellationToken cancellationToken = default, Action<string>? startupProgress = null)
    {
        // Health is proven when the store opened (schema check at open); a failure would have thrown there.
        return Task.CompletedTask;
    }

    private SyncPair Mapped(PairDef pair) => PairMapping.ToSyncPair(pair, _store.FirstSyncDone(pair.PairId), _settings) with
    {
        // Removing a policy does not remove its registration/pointers. Project this pair's retained
        // evidence, never another pair's opt-in or the availability of the optional storage schema.
        FilesOnDemandEnabled = _store is SqliteStateStore sql && sql.PairUsesStorage(pair)
    };

    private static NotSupportedException StorageHostUnsupported() => new(
        "Files On-Demand storage management is unsupported in the full-window host: only the shared background " +
        "host drains storage work. No hydration or offline completion has been verified. " +
        "Options: use --cli storage online-only/offline/inherit/list with the background host running; " +
        "keep Sync Shark and its registration installed until offline completion is verified, or retain " +
        "your cloud copy and obtain an independent usable local copy before choosing removal. No storage change was made.");

    private string? Selected(IReadOnlyList<PairDef> defs) =>
        _selectedPairId is not null && defs.Any(def => string.Equals(def.PairId, _selectedPairId, StringComparison.Ordinal))
            ? _selectedPairId
            : defs.Count > 0 ? defs[0].PairId : null;

    // The account scope helper: a null connection means every pair; otherwise a pair is in scope when either of
    // its roots belongs to that connection.
    private static bool InScope(PairDef pair, string? connectionId) => connectionId is null ||
        string.Equals(pair.A.AccountId, connectionId, StringComparison.Ordinal) ||
        string.Equals(pair.B.AccountId, connectionId, StringComparison.Ordinal);
}
