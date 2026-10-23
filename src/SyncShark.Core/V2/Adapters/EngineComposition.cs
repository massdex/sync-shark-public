namespace Massdex.SyncShark.V2;

/// <summary>What the host hands the engine: where state lives, the v1 state authority and the shared HTTP client.</summary>
/// <remarks>
/// The local Recovery location and retention come from the product settings store through
/// <paramref name="StateAuthority"/> (decision 55). <paramref name="DropboxCredentials"/> defaults to the Windows
/// protected store; a check passes a stand-in. <paramref name="LongpollHandler"/> is the handler of the engine's own
/// long-poll HTTP client (a check passes one that fails on any request); null means a production handler.
/// </remarks>
public sealed record EngineHostInputs(string ProductRoot, PilotStateAuthority? StateAuthority, HttpClient Http,
    IDropboxRefreshTokenStore? DropboxCredentials = null, HttpMessageHandler? LongpollHandler = null)
{
    // Explicit fixture dependency; null preserves the native production activity/power policy.
    internal Func<bool>? ContentPausedForTest { get; init; }
}

/// <summary>The single composition home: CLI and UI both open the engine here, never wiring it twice.</summary>
/// <remarks>
/// Opening reads no endpoint: endpoints are opened per pair when a run starts, so status and pair lists never touch
/// a provider. A damaged database throws <see cref="StateDamagedException"/> (no auto-recreate); an old one throws
/// <see cref="StateNeedsFreshDatabaseException"/>. The caller owns the product lock.
/// </remarks>
public sealed class EngineComposition : IDisposable
{
    // Dropbox may answer a long-poll up to this much later than the requested timeout (its documented jitter).
    private static readonly TimeSpan LongpollJitter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LongpollHeadroom = TimeSpan.FromSeconds(30);
    // Headroom above the explicit batch operation timeout, so the transport never pre-empts a batch commit.
    private static readonly TimeSpan BatchTimeoutHeadroom = TimeSpan.FromMinutes(1);

    private readonly HttpClient _longpollHttp;

    private EngineComposition(SqliteStateStore store, SyncRun run, ObservedExecutor observed,
        ProductionEndpoints endpoints, IRootProbe roots, V2Settings settings, EnginePaths paths, TimeProvider time,
        HttpClient longpollHttp)
    {
        _longpollHttp = longpollHttp;
        Store = store;
        Run = run;
        Observed = observed;
        Endpoints = endpoints;
        Roots = roots;
        Settings = settings;
        Paths = paths;
        _time = time;
    }

    public IStateStore Store { get; }
    public SyncRun Run { get; }
    public ObservedExecutor Observed { get; }
    public ProductionEndpoints Endpoints { get; }
    public IRootProbe Roots { get; }
    public V2Settings Settings { get; }
    public EnginePaths Paths { get; }
    public string DatabasePath => Paths.DatabasePath;

    /// <summary>
    /// The timeout of the HTTP client the Dropbox change watches long-poll through: the long-poll timeout plus Dropbox's
    /// jitter plus headroom, so a quiet poll returns before the client gives up (the shared client keeps 100 s).
    /// </summary>
    public TimeSpan LongpollHttpTimeout => _longpollHttp.Timeout;

    /// <summary>The long-poll HTTP timeout the composition uses for <paramref name="settings"/>.</summary>
    public static TimeSpan LongpollTimeoutFor(V2Settings settings) =>
        settings.DropboxLongpollTimeout + LongpollJitter + LongpollHeadroom;

    /// <summary>
    /// The timeout for the Dropbox route client the pair sync commits batches through: the largest explicit batch
    /// operation timeout — a full upload or delete batch, base plus its per-entry allowance — plus headroom, so the
    /// transport never cuts a slow finish_batch_v2 or delete_batch short (never the implicit 100 s
    /// <see cref="HttpClient"/> default).
    /// </summary>
    public static TimeSpan DropboxHttpTimeoutFor(V2Settings settings)
    {
        var maximumBatch = Math.Max(1, Math.Max(settings.UploadBatchSize, settings.DeleteBatchSize));
        return settings.BatchOperationTimeoutFor(maximumBatch, maximumBatch) + BatchTimeoutHeadroom;
    }

    /// <summary>Opens the store and builds the run; no provider is contacted.</summary>
    public static EngineComposition Open(EngineHostInputs host, V2Settings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        var transfer = host.StateAuthority is null
            ? new SyncTransferSettings()
            : new SyncTransferSettingsStore(host.StateAuthority).Load();
        // An explicit product concurrency cap governs both executor workers and resource admission.
        // Automatic (zero) preserves the engine's default rather than silently changing its worker policy.
        if (transfer.MaximumConcurrentFiles > 0)
            settings = settings with { MaxWorkers = transfer.MaximumConcurrentFiles };
        // The product transfer settings reach the engine's batch tunables the same way; automatic (zero) keeps the
        // engine default (V2Settings.Default), so the default lives only there.
        if (transfer.UploadBatchSize > 0)
            settings = settings with { UploadBatchSize = transfer.UploadBatchSize };
        if (transfer.UploadCommitConcurrency > 0)
            settings = settings with { UploadCommitConcurrency = transfer.UploadCommitConcurrency };
        if (transfer.DeleteBatchSize > 0)
            settings = settings with { DeleteBatchSize = transfer.DeleteBatchSize };
        // Decision 55: the user's Recovery location and retention live in the product settings store.
        var paths = new EnginePaths(host.ProductRoot, transfer.LocalRecoveryRoot);
        if (host.StateAuthority is not null)
            settings = settings with { RecoveryRetention = TimeSpan.FromDays(transfer.RecoveryRetentionDays) };
        var limiter = new SyncTransferRateLimiter(transfer);
        Func<string?> installationId = host.StateAuthority is null
            ? () => null
            : () => new SyncPairStore(host.StateAuthority).LoadInstallationId();
        var longpollHttp = new HttpClient(host.LongpollHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = LongpollTimeoutFor(settings)
        };
        var dropbox = new DropboxRootClients(DropboxAccounts(host, host.Http, limiter),
            DropboxAccounts(host, longpollHttp, limiter));
        SqliteStateStore store;
        try
        {
            store = SqliteStateStore.Open(host.ProductRoot, settings, time);
        }
        catch
        {
            longpollHttp.Dispose();
            throw;
        }
        try
        {
            var observed = new ObservedExecutor(new Executor(store, settings, limiter.Resources));
            var endpoints = new ProductionEndpoints(store, settings, time, paths, installationId,
                dropbox, host.ContentPausedForTest);
            var run = new SyncRun(store, observed, endpoints, settings, time, endpoints.ProductOwnedRelative);
            return new EngineComposition(store, run, observed, endpoints, new ProductionRootProbe(dropbox), settings,
                paths, time, longpollHttp);
        }
        catch
        {
            store.Dispose();
            longpollHttp.Dispose();
            throw;
        }
    }

    // The same retained connection and credential stores the v1 host and the Dropbox CLI use; opening sends nothing.
    // Token refresh always goes through the shared client; only the route calls use the given one.
    private static DropboxAccountClients? DropboxAccounts(EngineHostInputs host, HttpClient http,
        SyncTransferRateLimiter limiter)
    {
        if (host.StateAuthority is null) return null;
        var credentials = host.DropboxCredentials ?? new WindowsDropboxRefreshTokenStore();
        return new DropboxAccountClients(http, new DropboxConnectionStore(host.StateAuthority), credentials,
            binding => new RefreshingDropboxAccessTokenSource(host.Http, credentials, binding), limiter);
    }

    private readonly TimeProvider _time;
    private readonly object _autoGate = new();
    private AutoSync? _auto;

    /// <summary>Creates continuous sync on the first call and starts it on every call (restarts after a stop).</summary>
    /// <remarks>The CLI never calls this. A non-positive safety interval is floored to one minute.</remarks>
    public AutoSync StartAutoSync(Func<PairDef, bool> paused, Func<TimeSpan> safetyPoll)
    {
        ArgumentNullException.ThrowIfNull(paused);
        ArgumentNullException.ThrowIfNull(safetyPoll);
        AutoSync auto;
        lock (_autoGate)
        {
            if (_auto is null) Endpoints.SafetyPoll = () => Floor(safetyPoll());
            _auto ??= new AutoSync(Store, Run, Endpoints, Settings, paused, () => Floor(safetyPoll()), _time);
            auto = _auto;
        }
        Endpoints.Indexing?.Start(paused, pairId => auto.Kick(pairId));
        auto.Start();
        return auto;
    }

    /// <summary>Hosts one explicit indexing run without starting or awaiting a sync run.</summary>
    public Task<string> RunIndexAsync(string rootId, CancellationToken ct)
    {
        var root = Store.Pairs().SelectMany(pair => new[] { pair.A, pair.B })
            .FirstOrDefault(root => root.RootId == rootId && root.Provider == SyncProviders.Local)
            ?? throw new SyncCliTargetException("No local root has that exact identity.");
        return Endpoints.Indexing?.RunAsync(root, ct) ?? Task.FromResult("unsupported");
    }

    /// <summary>Reads durable indexing information without probing endpoints.</summary>
    public IReadOnlyList<LocalIndexStatus> IndexStatus() => Store.Pairs()
        .SelectMany(pair => new[] { pair.A, pair.B }).Where(root => root.Provider == SyncProviders.Local)
        .DistinctBy(root => root.RootId).Select(root => new LocalIndexStatus(root.RootId,
            Store is ILocalGapStore gaps && gaps.HasGapProtection(root.RootId),
            Store is ILocalGapStore progress ? progress.GapProgress(root.RootId) : string.Empty,
            Store.RehashPendingCount(root.RootId))
            { Remaining = Store.IndexingRemaining(root.RootId) }).ToArray();

    internal Task StopIndexingAsync() => Endpoints.Indexing?.StopAsync() ?? Task.CompletedTask;

    private static TimeSpan Floor(TimeSpan interval) => interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(1);

    /// <summary>Stops continuous sync (awaiting it) before closing the store.</summary>
    public void Dispose()
    {
        AutoSync? auto;
        lock (_autoGate) auto = _auto;
        try { auto?.StopAsync().GetAwaiter().GetResult(); }
        finally
        {
            try { Endpoints.StopGapWorkersAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { RunLog.Trace($"gap worker shutdown failed: {error}"); }
            finally
            {
                try { Store.Dispose(); }
                finally { _longpollHttp.Dispose(); }
            }
        }
    }
}
