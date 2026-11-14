using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>The CLI runtime for `engine …` commands (section 9.2) and the native `dropbox` group. Construction does
/// no state access; the old-command members throw SyncCliUnsupportedException. The Dropbox seam opens the product
/// authority on first Dropbox access only: a host-bound instance borrows the host's authority, a standalone instance
/// takes the same exclusive PilotInstanceLock the direct CLI takes, and both own one native client pair.</summary>
public sealed class EngineCliRuntime : ISyncCliRuntime, IEngineCliAccess, IEngineHostReads, IDropboxCliAccess
{
    private readonly EngineOperations? _operations;
    private readonly PilotStateAuthority? _hostAuthority;
    private readonly int _width = RunLog.HumanWidth;
    // The app edge's retained terminal-startup notice; only an absent-host status result shows it.
    private readonly string? _startupNotice;
    private readonly SyncTransferRateLimiter _dropboxLimiter = new(new SyncTransferSettings());
    private PilotInstanceLock? _ownLock;
    private DropboxAccountClients? _dropboxAccounts;
    private HttpClient? _dropboxHttp;
    private enum Lifetime { Active, Disposed }
    private int _disposed;

    public EngineCliRuntime() { }
    public EngineCliRuntime(int width, string? startupNotice = null) { _width = width; _startupNotice = startupNotice; }
    public EngineCliRuntime(EngineOperations operations) =>
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    /// <summary>The host-bound form: the Dropbox seam borrows the running host's already-held authority and never
    /// takes a second product lock or disposes that authority.</summary>
    internal EngineCliRuntime(EngineOperations operations, PilotStateAuthority authority)
        : this(operations) => _hostAuthority = authority ?? throw new ArgumentNullException(nameof(authority));

    /// <summary>The HTTP handler the Dropbox seam sends through; a test replaces it with a scripted wire.</summary>
    internal Func<HttpMessageHandler> DropboxHandlerFactory { get; set; } =
        () => new HttpClientHandler { AllowAutoRedirect = false };

    /// <summary>The protected credential store the Dropbox seam reads; a test replaces it with a stand-in.</summary>
    internal Func<IDropboxRefreshTokenStore> DropboxCredentialsFactory { get; set; } =
        () => new WindowsDropboxRefreshTokenStore();

    /// <summary>The account-bound Dropbox route clients, constructed on first access under the one authority this
    /// runtime may use. Ownership order: existing clients, then authority, then the native client, then credentials
    /// and clients, so a later construction failure can still release everything already owned.</summary>
    public DropboxAccountClients DropboxAccounts
    {
        get
        {
            if (_dropboxAccounts is not null) return _dropboxAccounts;
            var authority = _hostAuthority;
            if (authority is null)
            {
                _ownLock = CliProductLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
                authority = _ownLock.StateAuthority;
                // The standalone path admits its own acquisition once, after the lock and before any client or
                // account access; the hosted path already holds the host's admitted lifetime.
                _ = EngineStartupAdmission.Require(SyncSharkRuntimePaths.ProductRoot);
            }
            var http = DropboxCliClients.Create(DropboxHandlerFactory);
            _dropboxHttp = http;
            _dropboxAccounts = DropboxCliClients.Compose(
                authority, http, DropboxCredentialsFactory(), _dropboxLimiter);
            return _dropboxAccounts;
        }
    }

    /// <summary>The folder oversized Dropbox results are spilled to.</summary>
    public string ResultFolder => Path.Combine(SyncSharkRuntimePaths.ProductRoot, DropboxCliSettings.ResultFolderName);

    /// <summary>The Dropbox CLI tunables in effect.</summary>
    public DropboxCliSettings DropboxCliSettings => DropboxCliSettings.Default;

    public Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
        CancellationToken cancellationToken, SyncActivityQuery? query, string? search,
        string? filter, string? pairId, string? itemType, DateTimeOffset? sinceUtc) => throw OldCommand();
    public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken, int limit, int offset) =>
        throw OldCommand();
    public Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress, CancellationToken cancellationToken) =>
        throw OldCommand();

    /// <summary>Runs one engine invocation and maps EngineRequestException to a usage error. A preference command is
    /// host-only: this standalone runtime has no verified host-bound operations instance, so it reports the command
    /// unavailable instead of constructing a local operations instance.</summary>
    public async Task<object> RunEngineCommandAsync(SyncCliInvocation invocation, Action<string> progress,
        CancellationToken ct)
    {
        if (_operations is null && EngineCliCatalog.CommandKey(invocation.Command) is
            (EngineCommandKey.PreferencesShow or EngineCommandKey.PreferencesSet))
            throw new SyncCliUnsupportedException(EngineCliCatalog.Message(EngineMessageKey.NeedsEngine));
        try
        {
            var result = await new EngineCommands(_operations ?? new EngineOperations(SyncSharkRuntimePaths.ProductRoot,
                progress, _width)).RunAsync(invocation, ct);
            // Additive presentation only: the retained notice never replaces the state the host actually reported.
            return result is Results.HostResult status && _startupNotice is { Length: > 0 } notice
                ? status with { Message = status.Message + Environment.NewLine + notice }
                : result;
        }
        catch (EngineRequestException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
    }

    /// <summary>Answers one host-owned read from the host's own operations, with no product lock. A missing pair is
    /// the same usage refusal the direct path reports.</summary>
    public async Task<object> ReadHostAsync(EngineHostReadKind kind, SyncCliInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EngineHostReads.ReadAsync(
                _operations ?? new EngineOperations(SyncSharkRuntimePaths.ProductRoot, static _ => { }),
                kind, invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (EngineRequestException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
    }

    /// <summary>Releases the native Dropbox client and the owned product lock, including a partially constructed
    /// runtime; the borrowed host authority and its operations are never disposed here.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, (int)Lifetime.Disposed) == (int)Lifetime.Active)
        {
            _dropboxHttp?.Dispose();
            _ownLock?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private static SyncCliUnsupportedException OldCommand() =>
        new(EngineCliCatalog.Message(EngineMessageKey.OldCommand));
}
