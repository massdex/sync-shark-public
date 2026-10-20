using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost(
    PilotStateAuthority stateAuthority,
    HttpClient httpClient,
    SyncPairStore pairStore,
    SyncRuntimeStateStore legacyRuntimeStore,
    DropboxConnectionStore connectionStore,
    IDropboxRefreshTokenStore credentialStore,
    SyncAuthorityDatabase? authorityDatabase = null,
    Func<SyncPair, DropboxConnectionBinding, ISyncRemoteStore>? remoteFactory = null,
    IGoogleDriveRefreshTokenStore? googleDriveCredentialStore = null,
    IGoogleDriveTokenEndpoint? googleDriveTokenEndpoint = null) : IDisposable
{
    private readonly GoogleDriveConnectionStore _googleDriveConnections = new(stateAuthority);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GoogleDriveAccessTokenSource>
        _googleTokens = new(StringComparer.Ordinal);
    private readonly IGoogleDriveRefreshTokenStore? _googleDriveCredentials = googleDriveCredentialStore;
    private readonly IGoogleDriveTokenEndpoint? _googleDriveEndpoint = googleDriveTokenEndpoint;

    internal static Action<Exception>? BatchItemFailureForTest { get; set; }
    internal static bool UseIndependentPairPlanningForTest { get; set; }
    internal Func<string, Task>? RewindApplyPhaseForTest { get; set; }
    internal Func<string, Task>? RepresentationAdoptionPhaseForTest { get; set; }
    internal Func<string, Task>? CoordinatedPlanningPhaseForTest { get; set; }
    internal Func<int, Task>? CoordinatedWorkerStartedForTest { get; set; }
    private const string CapabilityJson =
        "{\"name\":\"full-client-v1\",\"recursive\":true,\"create\":true,\"replace\":true,\"delete\":true,\"samePairMove\":true,\"crossPairMove\":true,\"cloudFiles\":true,\"selectiveFolders\":true,\"recoveryBrowser\":true,\"rewindHandoff\":true,\"pluralConnections\":true}";
    private readonly SyncAuthorityDatabase _authority = authorityDatabase ??
                                                         new SyncAuthorityDatabase(
                                                             SyncAuthorityDatabase.DefaultDatabasePath);
    private readonly SyncAutomationStore _automationStore = new(stateAuthority);
    private readonly SyncTransferSettingsStore _transferSettingsStore = new(stateAuthority);
    private readonly SyncTransferRateLimiter _transferLimiter = new(new SyncTransferSettings());
    public SyncTransferResourceStatus TransferResourceStatus => _transferLimiter.Resources.Snapshot();
    // The public host and the Rewind accessor use the SAME injected authority instance and writer.
    private SyncRewindStore? _rewindStoreLazy;
    private SyncRewindStore _rewindStore => _rewindStoreLazy ??= new(_authority);
    private readonly SyncHistoryStore _legacyHistoryStore = new(stateAuthority);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    // Local Rewind publication: one existing aggregate handle, one gate and one completion flag. The
    // flag is never an authority verdict; every reader still requires the published reference and
    // reads the current authority rows.
    private readonly SyncProductStateStore _rewindProductState = new(stateAuthority);
    private readonly SemaphoreSlim _rewindPublicationGate = new(1, 1);
    private volatile bool _rewindStoragePublished;
    private readonly SemaphoreSlim _monitorConfigurationGate = new(1, 1);
    private readonly object _runControlGate = new();
    private readonly object _monitorDebounceGate = new();
    private readonly object _cloudProviderGate = new();
    private readonly List<SyncAuthorityLocalMonitor> _localMonitors = [];
    private readonly List<SyncAuthorityDropboxMonitor> _dropboxMonitors = [];
    private readonly Dictionary<string, WindowsCloudFilesProvider> _cloudProviders =
        new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RefreshingDropboxAccessTokenSource> _accountTokens = new(StringComparer.Ordinal);
    private RefreshingDropboxAccessTokenSource AccountTokenSource(DropboxConnectionBinding connection) =>
        _accountTokens.GetOrAdd(connection.ConnectionId + "\n" + connection.AccountId + "\n" + connection.RootNamespaceId + "\n" + connection.CredentialReference,
            _ => new RefreshingDropboxAccessTokenSource(httpClient, credentialStore, connection));
    private void EvictAccountTokens(string connectionId)
    {
        // Cache keys include immutable identity and credential routing, not just the account key.
        // Remove every retained variant of this connection without invalidating another account.
        var prefix = connectionId + "\n";
        foreach (var key in _accountTokens.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) _accountTokens.TryRemove(key, out _);
    }
    public IReadOnlyList<ProviderConnectionRecord> LoadProviderConnections() => connectionStore.LoadConnections()
        .Concat(_googleDriveConnections.LoadActiveBindings().Select(item => item.ToProviderRecord())).ToArray();
    public DropboxConnectionBinding? LoadDropboxConnection(string? connectionId = null) => connectionStore.Load(connectionId);
    private PilotRunControl? _activeRunControl;
    private TaskCompletionSource<bool>? _activeRunCheckpoint;
    private SyncActiveRunSnapshot? _activeRunSnapshot;
    public bool HasActiveRun { get { lock (_runControlGate) return _activeRunControl is not null; } }
    public SyncActiveRunSnapshot? ActiveRun
    {
        get
        {
            lock (_runControlGate)
                return _activeRunSnapshot is { } value && _activeRunControl is { } control
                    ? value with { Paused = control.IsPaused, StopRequested = control.IsStopRequested } : null;
        }
    }
    private CancellationTokenSource? _monitorDebounce;
    private Func<IReadOnlySet<string>, Task<bool>>? _onMonitorRunRequested;
    private readonly Dictionary<string, long> _pendingMonitorPairs = new(StringComparer.Ordinal);
    private long _monitorHintGeneration;
    private long _monitorScopeGeneration;
    private Task? _monitorDelivery;
    private string? _installationId;
    private string? _selectedPairId;
    private bool _commandsRecovered;
    private int _disposed;

    public SyncAuthorityDatabase Authority => _authority;
    // Approved by root sshark:391-r3: one internal read-only fixture accessor that returns the SAME
    // capability-profile identity the initializer computes. INPUT identity only - it creates no profile,
    // calls no initializer, bypasses no authority check and does not manufacture queue output.
    internal string CapabilityProfileIdForTest => Id("profile", BuildIdentity.SourceRevision, CapabilityJson);
    public bool AuthorityStateExists => File.Exists(_authority.DatabasePath);

    public SyncAuthorityRuntimeHost(PilotStateAuthority stateAuthority, HttpClient httpClient)
        : this(stateAuthority, httpClient, new SyncPairStore(stateAuthority),
            new SyncRuntimeStateStore(stateAuthority), new DropboxConnectionStore(stateAuthority),
            new WindowsDropboxRefreshTokenStore(), AuthorityDatabaseFor(stateAuthority))
    {
    }

    private static SyncAuthorityDatabase AuthorityDatabaseFor(PilotStateAuthority stateAuthority)
    {
        var stateParent = Directory.GetParent(Path.GetFullPath(stateAuthority.StateRoot))
                          ?? throw new InvalidDataException(
                              "The protected state directory has no product parent for authority storage.");
        return new SyncAuthorityDatabase(Path.Combine(stateParent.FullName, "vNext", "state", "syncshark.db"));
    }

    public Task<SyncPairSet> LoadPairsAsync(CancellationToken cancellationToken = default,
        Action<string>? startupProgress = null) =>
        Task.Run(() => LoadPairsCoreAsync(cancellationToken, startupProgress), cancellationToken);

    private async Task<SyncPairSet> LoadPairsCoreAsync(CancellationToken cancellationToken,
        Action<string>? startupProgress)
    {
        startupProgress?.Invoke("Startup: verifying local state ownership; no file transfer has started.");
        stateAuthority.VerifyAuthority();
        await EnsureAuthorityInitializedAsync(cancellationToken, startupProgress);
        startupProgress?.Invoke("Startup: loading retained folder-pair definitions.");
        var retained = await _authority.LoadPairSetAsync(cancellationToken);
        var retainedIds = retained.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var legacy = pairStore.Load();
        var candidates = legacy.Pairs.Where(item => !retainedIds.Contains(item.Id)).Select(item => item with
        {
            ControlNamespace = null,
            RecoveryRoot = null,
            PolicyRevision = 1
        });
        var pairs = retained.Pairs.Concat(candidates)
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = _selectedPairId;
        if (selected is null || !pairs.Any(item => string.Equals(item.Id, selected, StringComparison.Ordinal)))
            selected = retained.SelectedPairId ??
                       (legacy.SelectedPairId is not null && pairs.Any(item => item.Id == legacy.SelectedPairId)
                           ? legacy.SelectedPairId
                           : pairs.FirstOrDefault()?.Id);
        _selectedPairId = selected;
        startupProgress?.Invoke($"Startup: loaded {pairs.Length} folder-pair definitions.");
        return await _authority.NormalizeRetainedReadbackAsync(new SyncPairSet(pairs, selected), cancellationToken);
    }

    public SyncAutomationSettings LoadAutomation()
    {
        stateAuthority.VerifyAuthority();
        return _automationStore.Load();
    }

    public SyncTransferSettings LoadTransferSettings()
    {
        stateAuthority.VerifyAuthority();
        var settings = _transferSettingsStore.Load();
        _transferLimiter.Update(settings);
        return settings;
    }

    public async Task<SyncPairSet> SelectPairAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await EnsureAuthorityInitializedAsync(cancellationToken);
        if (await _authority.IsPairDefinitionRetainedAsync(pairId, cancellationToken))
        {
            await _authority.SelectPairAsync(pairId, cancellationToken);
            _selectedPairId = pairId;
            return await LoadPairsAsync(cancellationToken);
        }

        var legacy = pairStore.Load();
        if (!legacy.Pairs.Any(item => string.Equals(item.Id, pairId, StringComparison.Ordinal)))
            throw new InvalidDataException("The selected sync pair is not retained or available for migration.");
        _selectedPairId = pairId;
        return await LoadPairsAsync(cancellationToken);
    }

    public async Task SaveAutomationAsync(SyncAutomationSettings settings,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await _automationStore.SaveAsync(settings, cancellationToken);
    }

    public async Task SaveTransferSettingsAsync(SyncTransferSettings settings,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var normalized = SyncTransferSettings.Normalize(settings);
        await _transferSettingsStore.SaveAsync(normalized, cancellationToken);
        _transferLimiter.Update(normalized);
    }

    public async Task<bool> IsDropboxConnectedAsync(CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        foreach (var binding in connectionStore.LoadActiveBindings())
            if (!string.IsNullOrWhiteSpace(await credentialStore.ForConnection(binding).ReadAsync(cancellationToken))) return true;
        return false;
    }

    public async Task<DropboxConnectionStatus> GetDropboxConnectionStatusAsync(bool verifyLive = true,
        CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        var binding = connectionStore.Load(connectionId);
        if (binding is null)
            return new DropboxConnectionStatus(null, false, false, "Disconnected", null, 0,
                null);
        var hasCredential = !string.IsNullOrWhiteSpace(await credentialStore.ForConnection(binding).ReadAsync(cancellationToken));
        var pairCount = (await LoadPairsAsync(cancellationToken)).Pairs.Count(pair => pair.ConnectionId == binding.ConnectionId ||
            string.IsNullOrWhiteSpace(pair.ConnectionId) && binding.ConnectionId == connectionStore.Load()?.ConnectionId);
        if (!hasCredential)
            return new DropboxConnectionStatus(binding, false, false,
                "Connection metadata exists, but its protected credential is unavailable.", null, pairCount,
                await LoadLastSuccessfulSyncSafeAsync(cancellationToken, binding.ConnectionId));
        if (!verifyLive)
            return new DropboxConnectionStatus(binding, true, false, "Connected; live verification pending.", null,
                pairCount, await LoadLastSuccessfulSyncSafeAsync(cancellationToken, binding.ConnectionId));
        try
        {
            var tokenSource = AccountTokenSource(binding);
            var accessToken = await tokenSource.GetAccessTokenAsync(cancellationToken);
            var account = await DropboxOAuth.GetCurrentAccountAsync(httpClient, accessToken, cancellationToken);
            if (!string.Equals(account.AccountId, binding.AccountId, StringComparison.Ordinal) ||
                !string.Equals(account.RootNamespaceId, binding.RootNamespaceId, StringComparison.Ordinal) ||
                !string.Equals(account.HomeNamespaceId, binding.HomeNamespaceId, StringComparison.Ordinal))
                throw new InvalidDataException("The live Dropbox account no longer matches the saved connection identity.");
            var verified = binding with
            {
                DisplayName = account.DisplayName,
                Email = account.Email,
                AccountType = account.AccountType,
                LastVerifiedUtc = DateTimeOffset.UtcNow
            };
            // Last verified is a user-facing health fact, not merely an in-memory refresh result.
            // Persist every successful identity check so restart cannot display the original
            // connection timestamp as though it were the latest provider verification.
            await connectionStore.SaveAsync(verified, cancellationToken);
            DropboxSpaceUsage? usage = null;
            using (var quotaTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                quotaTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                try { usage = await DropboxOAuth.GetSpaceUsageAsync(httpClient, accessToken, quotaTimeout.Token); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* Quota availability is not account health. */ }
            }
            return new DropboxConnectionStatus(verified, true, true, "Connected and verified.", null, pairCount,
                await LoadLastSuccessfulSyncSafeAsync(cancellationToken, binding.ConnectionId), usage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                           !cancellationToken.IsCancellationRequested)
        {
            return new DropboxConnectionStatus(binding, true, false,
                "Connected, but live verification needs attention.", exception.GetType().Name, pairCount,
                await LoadLastSuccessfulSyncSafeAsync(cancellationToken, binding.ConnectionId));
        }
    }

    private async Task<DateTimeOffset?> LoadLastSuccessfulSyncSafeAsync(CancellationToken cancellationToken, string? connectionId = null)
    {
        if (!File.Exists(_authority.DatabasePath)) return null;
        try
        {
            var primary = connectionStore.Load()?.ConnectionId;
            var ids = connectionId is null ? null : (await LoadPairsAsync(cancellationToken)).Pairs
                .Where(pair => (string.IsNullOrWhiteSpace(pair.ConnectionId) ? primary : pair.ConnectionId) == connectionId)
                .Select(pair => pair.Id).ToArray();
            return await _authority.LoadLastSuccessfulSyncUtcAsync(cancellationToken, ids);
        }
        catch (InvalidDataException) { return null; }
    }

    public async Task<DropboxConnectionBinding> ReauthorizeDropboxAsync(Func<Uri, Task> openAuthorizationPage,
        CancellationToken cancellationToken = default, string? connectionId = null, bool includeFileRequests = false)
    {
        stateAuthority.VerifyAuthority();
        // AUTH1: this interactive wait is the host's one pending authorization attempt. The caller token
        // keeps its existing caller-cancellation meaning and the attempt identity is what a CLI or the
        // running application's Connections panel can address exactly.
        using var attempt = BeginAuthorizationAttempt(SyncProviders.Dropbox, SyncAuthorizationAction.Reauthorize,
            connectionId, cancellationToken);
        var coordinator = new DropboxConnectionCoordinator(httpClient, CreateAuthorizationReceiver(),
            credentialStore, connectionStore);
        var binding = await coordinator.ConnectAsync(openAuthorizationPage, reauthorize: true,
            cancellationToken: attempt.Token, connectionId: connectionId, includeFileRequests: includeFileRequests);
        attempt.MarkCommitted();
        EvictAccountTokens(binding.ConnectionId);
        return binding;
    }

    public async Task DisconnectDropboxAsync(CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        var binding = connectionStore.Load(connectionId);
        if (binding is null) return;
        // Never tear down a different account's monitoring or native provider connection.
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Wait for the current sync to finish before disconnecting this account.");
        try
        {
        await StopAccountRuntimeAsync(binding.ConnectionId, cancellationToken);
        string? accessToken = null;
            try
            {
                accessToken = await AccountTokenSource(binding)
                    .GetAccessTokenAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                               !cancellationToken.IsCancellationRequested)
            {
                // Offline/provider-revoked disconnect still removes the protected local connection.
            }
        await credentialStore.ForConnection(binding).DeleteAsync(cancellationToken);
        EvictAccountTokens(binding.ConnectionId);
        // Retain the connection identity and all inert future-provider records. With no protected
        // credential it is disconnected, but a later reconnect must still prove the same account
        // and namespace rather than silently inheriting these pairs on another Dropbox account.
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.dropboxapi.com/2/auth/token/revoke");
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(string.Empty);
                using var response = await httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
                // Revocation is best-effort: local disconnect and file preservation do not depend on the network.
                _ = response.IsSuccessStatusCode;
            }
            catch
            {
                // The protected local credential is already gone; a later Dropbox security review may revoke it.
            }
        }
        }
        finally { _runGate.Release(); }
    }

    public Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadActivityProjectionAsync(
        CancellationToken cancellationToken = default) =>
        _authority.LoadActivityProjectionAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadActivityProjectionPageAsync(int limit,
        int offset, string? search, string? filter, string? itemType = null, string? pairId = null,
        DateTimeOffset? sinceUtc = null,
        CancellationToken cancellationToken = default, string? connectionId = null, SyncActivityQuery? query = null) =>
        // EPS2: historical activity is filtered by the recorded attribution itself, never by the currently
        // retained account binding or current pair membership, so rebinding or removing an account can neither
        // hide nor reattribute old history. The exact requested connectionId is still passed through, and the
        // database projection keeps validating the recorded endpoint/legacy account attribution.
        await _authority.LoadActivityProjectionPageAsync(limit, offset, search, filter, itemType, pairId, sinceUtc,
            cancellationToken, null, query, connectionId);

    public Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => _authority.LoadQueueProjectionAsync(cancellationToken: cancellationToken), cancellationToken);

    public async Task<int> LoadPendingQueueCountAsync(CancellationToken cancellationToken = default,
        string? connectionId = null) =>
        await _authority.LoadPendingQueueCountAsync(cancellationToken,
            await AccountPairIdsAsync(connectionId, cancellationToken));

    public async Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionPageAsync(int limit,
        int offset, string? search, string? filter, CancellationToken cancellationToken = default,
        string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null, string? connectionId = null) =>
        await _authority.LoadQueueProjectionPageAsync(limit, offset, search, filter, cancellationToken, pairId, itemType, sinceUtc,
            await AccountPairIdsAsync(connectionId, cancellationToken));

    public Task<IReadOnlySet<string>> LoadPairIdsNeedingAttentionAsync(
        CancellationToken cancellationToken = default) =>
        _authority.LoadPairIdsNeedingAttentionAsync(cancellationToken);

    public async Task<SyncRecoveryUsageSummary> LoadRecoveryUsageAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        var pair = await _authority.LoadPairDefinitionAsync(pairId, cancellationToken)
                   ?? throw new InvalidDataException("The requested recovery pair is not retained.");
        // Retained-copy usage is a local authority projection, independent of the backing provider.
        // Provider Trash is listed separately and does not count toward Sync Shark retention.
        // SS-D11: this pair's own retained, verified and release-authorized copies are aggregated in ONE
        // exact statement, so neither another pair's artifacts nor any unrelated pair definition is
        // hydrated. The retention cutoff is taken from the ONE instant captured here, and retained
        // authority that is not canonical is still refused instead of being reported as a silent zero.
        var usage = await _authority.LoadRecoveryUsageAggregateAsync(pair.Id,
            pair.RecoveryRetentionDays is { } retentionDays
                ? DateTimeOffset.UtcNow.AddDays(-retentionDays)
                : (DateTimeOffset?)null, cancellationToken);
        return new SyncRecoveryUsageSummary(pair.Id, checked((int)usage.ItemCount), usage.Size,
            usage.OldestRetainedUtc, checked((int)usage.CleanupEligibleCount), pair.RecoveryRetentionDays);
    }

    public async Task<IReadOnlyList<SyncRecoveryPairChoice>> LoadRecoveryPairChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var activeIds = (await LoadPairsAsync(cancellationToken)).Pairs.Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        return (await _authority.LoadAllPairDefinitionsAsync(cancellationToken))
            .Select(pair => new SyncRecoveryPairChoice(pair.Id, pair.Name, activeIds.Contains(pair.Id)))
            .OrderByDescending(item => item.Active)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<SyncRecoveryCatalogItem>> LoadRecoveryCatalogAsync(string pairId,
        bool includeDropboxDeletedItems = false, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var pair = await _authority.LoadPairDefinitionAsync(pairId, cancellationToken)
                   ?? throw new InvalidDataException("The requested recovery pair is not retained.");
        var result = new List<SyncRecoveryCatalogItem>();
        // SS-D11: this pair's own non-released copies are read directly, so no other pair's artifact rows
        // and no unrelated pair definition are hydrated to build this catalog.
        foreach (var artifact in await _authority.LoadRecoveryArtifactsForPairAsync(pair.Id,
                     cancellationToken))
            result.Add(await RetainedRecoveryCatalogItemAsync(pair, artifact, cancellationToken));

        if (includeDropboxDeletedItems)
        {
            RequireRecoveryBrowserSupported(pair);
            var account = await RecoveryAccountDisplayAsync(pair, cancellationToken);
            if (IsGooglePair(pair))
            {
                var trash = await LoadGoogleTrashCatalogAsync(pair, account, null, 100, cancellationToken);
                if (trash.HasMoreDeleted || trash.Warnings?.Count > 0)
                    throw new InvalidOperationException("Google Trash has more pages or incomplete results. Use the paged Recovery browser to retain those details.");
                result.AddRange(trash.Items);
            }
            else
            {
            var remote = CreateDropboxClient(pair, await RequireConnectionAsync(cancellationToken, pair.ConnectionId));
            foreach (var deleted in await remote.ListDeletedEntriesAsync(cancellationToken))
                result.Add(new SyncRecoveryCatalogItem("deleted:" + deleted.RelativePath, pair.Id, pair.Name,
                    SyncRecoverySource.DropboxDeleted, deleted.RelativePath, account, pair.DropboxRoot,
                    null, null, "Subject to the connected Dropbox account's revision history",
                    "Load revisions to select an exact recoverable version", null, null, null, "{}", false));
            }
        }

        return result.OrderByDescending(item => item.RetainedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<SyncRecoveryCatalogPage> LoadRecoveryCatalogPageAsync(string pairId,
        int archiveOffset = 0, string? deletedCursor = null, bool includeDropboxDeletedItems = false,
        int pageSize = 200, string? search = null, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        // SS-D11: the paged catalog already reads this pair's own page; only the pair resolution changes.
        var pair = await _authority.LoadPairDefinitionAsync(pairId, cancellationToken)
                   ?? throw new InvalidDataException("The requested recovery pair is not retained.");
        var archivePage = await _authority.LoadRecoveryArtifactsPageAsync(pair.Id, pageSize + 1,
            archiveOffset, search, cancellationToken);
        var hasMoreArchives = archivePage.Count > pageSize;
        var items = new List<SyncRecoveryCatalogItem>();
        foreach (var artifact in archivePage.Take(pageSize))
            items.Add(await RetainedRecoveryCatalogItemAsync(pair, artifact, cancellationToken));
        string? nextDeletedCursor = deletedCursor;
        var hasMoreDeleted = false;
        IReadOnlyList<string>? warnings = null;
        if (includeDropboxDeletedItems)
        {
            RequireRecoveryBrowserSupported(pair);
            var account = await RecoveryAccountDisplayAsync(pair, cancellationToken);
            if (IsGooglePair(pair))
            {
                var trash = await LoadGoogleTrashCatalogAsync(pair, account, deletedCursor, pageSize, cancellationToken);
                items.AddRange(string.IsNullOrWhiteSpace(search) ? trash.Items :
                    trash.Items.Where(item => item.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)));
                nextDeletedCursor = trash.DeletedCursor;
                hasMoreDeleted = trash.HasMoreDeleted;
                warnings = trash.Warnings;
            }
            else
            {
            var deletedPage = await CreateDropboxClient(pair, await RequireConnectionAsync(cancellationToken, pair.ConnectionId))
                .ListDeletedEntriesPageAsync(deletedCursor, pageSize, cancellationToken);
            items.AddRange(deletedPage.Entries.Select(deleted => new SyncRecoveryCatalogItem(
                "deleted:" + deleted.RelativePath, pair.Id, pair.Name, SyncRecoverySource.DropboxDeleted,
                deleted.RelativePath, account, pair.DropboxRoot, null, null,
                "Subject to the connected Dropbox account's revision history",
                "Load revisions to select an exact recoverable version", null, null, null, "{}", false)));
            nextDeletedCursor = deletedPage.ContinueCursor;
            hasMoreDeleted = nextDeletedCursor is not null;
            }
        }

        return new SyncRecoveryCatalogPage(items, archiveOffset + Math.Min(pageSize, archivePage.Count),
            hasMoreArchives, nextDeletedCursor, hasMoreDeleted, warnings);
    }

    private static SyncRecoveryCatalogItem ToRecoveryCatalogItem(SyncPair pair, string account,
        SyncRecoveryArtifactProjection artifact)
    {
        var objectKind = JsonString(artifact.IdentityJson, "kind") ?? "file";
        var recipe = JsonString(artifact.IdentityJson, "recipe");
        var isDirectory = string.Equals(objectKind, "directory", StringComparison.OrdinalIgnoreCase);
        var isDirectoryRecipe = isDirectory &&
                                string.Equals(recipe, "recreate_empty_directory", StringComparison.Ordinal);
        var retainedPath = JsonString(artifact.IdentityJson, "path", "RecoveryPath");
        var location = isDirectoryRecipe
            ? "Durable empty-folder recreate recipe"
            : retainedPath ?? "Recorded location unavailable";
        var identity = JsonString(artifact.IdentityJson, "identity", "FileId", "deletedIdentity");
        var revision = JsonString(artifact.IdentityJson, "revision", "Revision");
        var health = isDirectoryRecipe
            ? "Available; the exact folder path is verified again before recreation"
            : artifact.Side == "local"
                ? isDirectory
                    ? LocalDirectoryRecoveryHealth(pair, location)
                    : LocalRecoveryHealth(pair, location, artifact.Size)
                : pair.RecoveryRoot is null
                    ? "Blocked: this pair no longer has a bound cloud Recovery folder"
                    : "Recorded; stable cloud identity is verified again before restore";
        var retention = pair.RecoveryRetentionDays is null
            ? "Kept until you remove it"
            : $"Protected until {artifact.RetainedUtc.AddDays(pair.RecoveryRetentionDays.Value):yyyy-MM-dd}; cleanup is manual";
        var cleanupEligible = pair.RecoveryRetentionDays is null ||
                              artifact.RetainedUtc.AddDays(pair.RecoveryRetentionDays.Value) <= DateTimeOffset.UtcNow;
        return new SyncRecoveryCatalogItem(artifact.RecoveryArtifactId, pair.Id, pair.Name,
            artifact.Side == "local" ? SyncRecoverySource.LocalArchive : SyncRecoverySource.DropboxArchive,
            artifact.OriginalRelativePath, account, location, artifact.Size, artifact.RetainedUtc,
            retention, health, artifact.ContentHash,
            identity, revision, artifact.IdentityJson, !health.StartsWith("Blocked", StringComparison.Ordinal),
            isDirectory ? "folder" : "file", cleanupEligible);
    }

    public Task<SyncRecoveryCleanupResult> RemoveRecoveryCopyAsync(SyncRecoveryCatalogItem item,
        CancellationToken cancellationToken = default) =>
        RemoveRecoveryCopyCoreAsync(item, false, cancellationToken);

    private async Task<SyncRecoveryCleanupResult> RemoveRecoveryCopyCoreAsync(SyncRecoveryCatalogItem item,
        bool localCapacityPressure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        stateAuthority.VerifyAuthority();
        // SS-D11: the pair and the exact artifact are resolved by their exact binding, so no other pair's
        // definition or artifact is hydrated. The resolved projection is identical to the previous global
        // read of the same row, so every later source, side, containment and ownership decision of this
        // consumer is unchanged; a zero result keeps the same missing-row refusal.
        var pair = await _authority.LoadPairDefinitionAsync(item.PairId, cancellationToken)
                   ?? throw new InvalidDataException("The recovery pair is no longer retained.");
        var artifact = await _authority.LoadRecoveryArtifactAsync(pair.Id, item.Key, cancellationToken: cancellationToken)
                       ?? throw new InvalidDataException("The selected recovery copy is no longer retained.");
        if (SyncAuthorityCloudRecoveryDescriptor.TryParse(artifact.IdentityJson, out var cloudDescriptor))
            return await RemoveTypedCloudRecoveryAsync(pair, artifact, cloudDescriptor!, cancellationToken);
        if (item.Source is not (SyncRecoverySource.LocalArchive or SyncRecoverySource.DropboxArchive))
            throw new InvalidOperationException("Only a Sync Shark Recovery copy can be removed here.");
        var retainedSource = artifact.Side == "local" ? SyncRecoverySource.LocalArchive : SyncRecoverySource.DropboxArchive;
        if (item.Source != retainedSource)
            throw new InvalidDataException("The requested Recovery source disagrees with the retained copy.");
        if (retainedSource == SyncRecoverySource.DropboxArchive)
            SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        if (artifact.Status == "released")
            return new SyncRecoveryCleanupResult(pair.Id, item.Key, "The recovery copy was already removed.");
        var resumingAuthorizedRelease = artifact.Status == "release_authorized";
        if (!resumingAuthorizedRelease)
        {
            if (!(localCapacityPressure && retainedSource == SyncRecoverySource.LocalArchive) &&
                !RecoveryCleanupEligible(pair, artifact))
                throw new InvalidOperationException("This Recovery copy is still protected from cleanup.");
            await VerifyRecoveryCopyForReleaseAsync(pair, artifact, item.Source, cancellationToken);
            await _authority.AuthorizeRecoveryArtifactReleaseAsync(artifact.RecoveryArtifactId,
                cancellationToken);
        }
        if (item.Source == SyncRecoverySource.LocalArchive)
            await RemoveLocalRecoveryCopyAsync(pair, artifact, allowAbsent: true, cancellationToken);
        else
            await RemoveDropboxRecoveryCopyAsync(pair, artifact, allowAbsent: true, cancellationToken);
        await _authority.MarkRecoveryArtifactReleasedAsync(artifact.RecoveryArtifactId, cancellationToken);
        return new SyncRecoveryCleanupResult(pair.Id, item.Key,
            "The selected Recovery copy was removed. Its activity record remains available.");
    }

    private async Task VerifyRecoveryCopyForReleaseAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, SyncRecoverySource source,
        CancellationToken cancellationToken)
    {
        if (source == SyncRecoverySource.LocalArchive)
            await VerifyLocalRecoveryCopyAsync(pair, artifact, cancellationToken);
        else
            await VerifyDropboxRecoveryCopyAsync(pair, artifact, cancellationToken);
    }

    private static async Task VerifyLocalRecoveryCopyAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, CancellationToken cancellationToken)
    {
        if (string.Equals(JsonString(artifact.IdentityJson, "recipe"), "recreate_empty_directory",
                StringComparison.Ordinal)) return;
        if (pair.ControlNamespace is null)
            throw new InvalidOperationException("The pair has no bound local Recovery area.");
        var path = RequireContainedLocalRecoveryPath(pair, artifact);
        var identity = JsonString(artifact.IdentityJson, "identity") ??
                       throw new InvalidDataException("The local Recovery identity is absent.");
        if (string.Equals(JsonString(artifact.IdentityJson, "kind"), "directory",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path) || File.Exists(path))
                throw new IOException("The exact local Recovery folder is unavailable.");
            using var directory = PilotDirectoryLease.OpenOwnedDirectory(path,
                SyncLocalRecovery.ParseSourceIdentity(identity));
            var manifest = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider, path,
                cancellationToken);
            if (manifest.Hash != artifact.ContentHash || manifest.Size != artifact.Size)
                throw new IOException("The selected local Recovery folder changed after it was retained.");
            return;
        }
        if (!File.Exists(path))
            throw new IOException("The exact local Recovery copy is unavailable.");
        await using var file = SyncLocalFileAuthority.OpenExistingExclusive(path);
        if (!string.Equals(file.FinalPath, path, StringComparison.OrdinalIgnoreCase) ||
            file.VerifySingleLink() != SyncLocalRecovery.ParseSourceIdentity(identity) ||
            file.Stream.Length != artifact.Size)
            throw new IOException("The selected local Recovery copy changed identity or size.");
        file.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(pair.Provider, file.Stream, cancellationToken);
        if (!string.Equals(hash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected local Recovery copy changed content.");
    }

    internal async Task VerifyDropboxRecoveryCopyAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, CancellationToken cancellationToken)
    {
        var root = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var expected = RecoveryArchiveFromArtifact(artifact);
        var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
        var current = await GetBoundRecoveryAsync(pair, remote, expected.RecoveryPath, cancellationToken);
        if (current is null || !SameRecoveryArchive(current, expected))
            throw new IOException("The selected Dropbox Recovery copy changed or moved.");
        await remote.VerifyRecoveryRootAsync(root, cancellationToken);
    }

    private static async Task RemoveLocalRecoveryCopyAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, bool allowAbsent, CancellationToken cancellationToken)
    {
        if (string.Equals(JsonString(artifact.IdentityJson, "recipe"), "recreate_empty_directory",
                StringComparison.Ordinal)) return;
        if (pair.ControlNamespace is null)
            throw new InvalidOperationException("The pair has no bound local Recovery area.");
        var path = RequireContainedLocalRecoveryPath(pair, artifact);
        var directory = string.Equals(JsonString(artifact.IdentityJson, "kind"), "directory",
            StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (allowAbsent) return;
            throw new IOException("The exact local Recovery copy is unavailable inside its bound control area.");
        }
        var identity = JsonString(artifact.IdentityJson, "identity") ??
                       throw new InvalidDataException("The local Recovery identity is absent.");
        if (directory)
        {
            if (!Directory.Exists(path) || File.Exists(path))
                throw new IOException("The selected local Recovery folder changed object type.");
            using (var root = PilotDirectoryLease.OpenOwnedDirectory(path,
                       SyncLocalRecovery.ParseSourceIdentity(identity)))
            {
                await DeleteRecoveryDirectoryContentsAsync(root, cancellationToken);
                root.DeleteOnClose();
            }
            if (Directory.Exists(path) || File.Exists(path))
                throw new IOException("The selected local Recovery folder remained after cleanup.");
            return;
        }
        if (!File.Exists(path) || Directory.Exists(path))
            throw new IOException("The selected local Recovery copy changed object type.");
        await using (var file = SyncLocalFileAuthority.OpenExistingExclusive(path))
        {
            if (!string.Equals(file.FinalPath, path, StringComparison.OrdinalIgnoreCase) ||
                file.VerifySingleLink() != SyncLocalRecovery.ParseSourceIdentity(identity) ||
                file.Stream.Length != artifact.Size)
                throw new IOException("The selected local Recovery copy changed identity or size.");
            file.Stream.Position = 0;
            var hash = await SyncContentIdentity.ComputeAsync(pair.Provider, file.Stream, cancellationToken);
            if (!string.Equals(hash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected local Recovery copy changed content.");
            file.DeleteOnClose();
        }
        if (File.Exists(path))
            throw new IOException("The selected local Recovery copy remained after removal.");
    }

    private static async Task DeleteRecoveryDirectoryContentsAsync(PilotDirectoryLease directory,
        CancellationToken cancellationToken)
    {
        directory.VerifyBinding();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path)
                     .Order(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if (PilotPath.IsUnsafeAttributes(attributes))
                throw new IOException("Recovery cleanup stopped because a folder member is not a regular local object.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var child = PilotDirectoryLease.OpenOwnedDirectory(path);
                await DeleteRecoveryDirectoryContentsAsync(child, cancellationToken);
                child.DeleteOnClose();
            }
            else
            {
                await using var file = SyncLocalFileAuthority.OpenExistingExclusive(path);
                file.DeleteOnClose();
            }
            directory.VerifyBinding();
        }
    }

    private static string RequireContainedLocalRecoveryPath(SyncPair pair,
        SyncRecoveryArtifactProjection artifact)
    {
        var path = Path.GetFullPath(JsonString(artifact.IdentityJson, "path", "RecoveryPath") ??
                                    throw new InvalidDataException("The local Recovery path is absent."));
        var control = Path.GetFullPath(pair.ControlNamespace!.Local.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(control, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The exact local Recovery copy is outside its bound control area.");
        return path;
    }

    internal async Task RemoveDropboxRecoveryCopyAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, bool allowAbsent, CancellationToken cancellationToken)
    {
        var root = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var expected = RecoveryArchiveFromArtifact(artifact);
        var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
        var current = await GetBoundRecoveryAsync(pair, remote, expected.RecoveryPath, cancellationToken);
        if (current is null)
        {
            if (allowAbsent) return;
            throw new IOException("The exact Dropbox Recovery copy is unavailable.");
        }
        if (!SameRecoveryArchive(current, expected))
            throw new IOException("The selected Dropbox Recovery copy changed or moved.");
        await remote.DeleteRecoveryAsync(root, expected, cancellationToken);
    }

    private static SyncArchiveResult RecoveryArchiveFromArtifact(SyncRecoveryArtifactProjection artifact) =>
        new(JsonString(artifact.IdentityJson, "FileId", "fileId", "identity") ??
                throw new InvalidDataException("The Dropbox Recovery identity is absent."),
            JsonString(artifact.IdentityJson, "Revision", "revision") ??
                throw new InvalidDataException("The Dropbox Recovery revision is absent."),
            JsonString(artifact.IdentityJson, "RecoveryPath", "path") ??
                throw new InvalidDataException("The Dropbox Recovery path is absent."),
            artifact.Size, artifact.ContentHash);

    private static bool SameRecoveryArchive(SyncArchiveResult left, SyncArchiveResult right) =>
        string.Equals(left.FileId, right.FileId, StringComparison.Ordinal) &&
        string.Equals(left.Revision, right.Revision, StringComparison.Ordinal) &&
        string.Equals(PilotPath.NormalizeDropboxRoot(left.RecoveryPath),
            PilotPath.NormalizeDropboxRoot(right.RecoveryPath), StringComparison.OrdinalIgnoreCase) &&
        left.Size == right.Size && string.Equals(left.ContentHash, right.ContentHash,
            StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SyncRecoveryCatalogItem>> LoadDropboxRevisionCatalogAsync(string pairId,
        string relativePath, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        // SS-D11: the revision catalog resolves its exact pair by exact identity.
        var pair = await _authority.LoadPairDefinitionAsync(pairId, cancellationToken)
                   ?? throw new InvalidDataException("The requested recovery pair is not retained.");
        RequireRecoveryBrowserSupported(pair);
        if (IsGooglePair(pair))
            return await LoadGoogleRevisionCatalogAsync(pair, relativePath, cancellationToken);
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        var path = PilotPath.NormalizeRelative(relativePath);
        var revisions = await CreateDropboxClient(pair, connection).ListRevisionsAsync(path, cancellationToken);
        return revisions.Select(revision => new SyncRecoveryCatalogItem(
                "revision:" + revision.Revision, pair.Id, pair.Name, SyncRecoverySource.DropboxRevision,
                revision.RelativePath, ConnectionDisplay(connection), "Dropbox revision " + revision.Revision,
                revision.Size, revision.ModifiedUtc, "Subject to the connected Dropbox account's revision history",
                "Available from Dropbox", revision.ContentHash, revision.FileId, revision.Revision,
                JsonSerializer.Serialize(revision), !string.IsNullOrWhiteSpace(revision.ContentHash)))
            .OrderByDescending(item => item.RetainedUtc).ToArray();
    }

    public async Task<SyncRecoveryEffectPreview> PreviewRecoveryAsync(SyncRecoveryCatalogItem item,
        bool restoreAsCopy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        // SS-D11: the preview resolves its exact pair by exact identity instead of hydrating every pair.
        var pair = await _authority.LoadPairDefinitionAsync(item.PairId, cancellationToken)
                    ?? throw new InvalidDataException("The recovery pair is no longer retained.");
        // CC4E-1: one CC3C typed cloud Recovery artifact is resolved from its own retained authority BEFORE
        // the legacy Source-enum route and before any caller-supplied CanRestore flag. The flag is
        // presentation, never authority here, and an explicit typed descriptor is never downgraded to the
        // historical physical route.
        var typedCloudRestore = await ResolveTypedCloudRestoreRouteAsync(pair, item.Key, cancellationToken);
        if (typedCloudRestore is not null)
            return await PreviewTypedCloudRecoveryAsync(pair, item, typedCloudRestore, restoreAsCopy,
                cancellationToken);
        if (!item.CanRestore) throw new InvalidOperationException("This recovery item is not available for restoration.");
        if (item.Source == SyncRecoverySource.CloudTrash)
        {
            RequireGoogleTrashEntry(pair, item);
            if (restoreAsCopy) throw new InvalidOperationException("Google Trash restores the original item only; it cannot restore a copy.");
            return new(item.PairId, item.Key, item.Source, item.RelativePath, item.RelativePath, false, false,
                "Restore this exact item from Google Drive Trash to its original cloud folder. The item, version, permissions and destination are checked again; an occupied destination is refused, never overwritten.");
        }
        if (item.Source == SyncRecoverySource.DropboxArchive)
            SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        else if (item.Source == SyncRecoverySource.DropboxRevision)
            SyncDropboxArtifactPaths.ValidateBoundRecoveryContainerIfPresent(pair);
        var target = restoreAsCopy
            ? await FindAvailableRecoveryCopyPathAsync(pair, item.RelativePath, item.Source, cancellationToken)
            : PilotPath.NormalizeRelative(item.RelativePath);
        var existing = item.Source == SyncRecoverySource.LocalArchive
            ? File.Exists(PilotPath.ResolveContainedPath(pair.LocalRoot, target)) ||
              Directory.Exists(PilotPath.ResolveContainedPath(pair.LocalRoot, target))
            : (await (await CreateRemoteStoreForPairAsync(pair, cancellationToken))
                    .ListEntriesAsync(cancellationToken))
                .Any(entry => string.Equals(entry.RelativePath, target, StringComparison.OrdinalIgnoreCase));
        var preservation = restoreAsCopy || !existing
            ? "No current destination is displaced."
            : "The current destination is retained in Recovery before replacement.";
        return new SyncRecoveryEffectPreview(item.PairId, item.Key, item.Source, item.RelativePath, target,
            restoreAsCopy, existing,
            $"Restore {item.ObjectKind} from {(item.Source == SyncRecoverySource.LocalArchive ? "this computer" : SyncProviders.DisplayName(pair.Provider))} to {(restoreAsCopy ? "a copy" : "its original path")}. {preservation}" +
            (IsGooglePair(pair) && item.Source == SyncRecoverySource.DropboxRevision
                ? " Google requires keeping the selected revision before download; it can continue using cloud storage even if restoration later fails." : ""));
    }

    public async Task<SyncRecoveryRestoreResult> RestoreRecoveryAsync(SyncRecoveryCatalogItem item,
        bool restoreAsCopy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        stateAuthority.VerifyAuthority();
        var preview = await PreviewRecoveryAsync(item, restoreAsCopy, cancellationToken);
        var commandId = await CreateAcceptedCommandAsync("reconcile", item.PairId, item.Key,
            JsonSerializer.Serialize(new
            {
                action = "restore_recovery",
                pairIds = new[] { item.PairId },
                source = item.Source.ToString(),
                sourcePath = item.RelativePath,
                preview.TargetRelativePath,
                restoreAsCopy
            }), cancellationToken);
        var restoreAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        PilotRunControl? runControl = null;
        SyncPair? restorePair = null;
        var restoreStage = "Recovery restore admission";
        try
        {
            runControl = await BeginRunAsync(cancellationToken, commandId, "reconcile", item.PairId);
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            // SS-D11: the exact pair read replaces the whole-set read, and the previous LINQ Single failure
            // contract for a pair that is not retained is preserved exactly: the same
            // InvalidOperationException the sequence operator raised, now raised by the exact read.
            var pair = await _authority.LoadPairDefinitionAsync(item.PairId, cancellationToken)
                       ?? throw new InvalidOperationException("Sequence contains no matching element");
            restorePair = pair;
            restoreStage = "Restoring retained " + item.ObjectKind + " to " + preview.TargetRelativePath;
            string detail;
            // CC4E-1/CC4E-2: the typed cloud route precedes the legacy Source-enum switch here too, so a
            // typed cloud artifact receives the SAME behaviour from every existing Host and CLI surface, and
            // every retained authority of it is re-resolved again immediately before the effect.
            var typedCloudRestore = await ResolveTypedCloudRestoreRouteAsync(pair, item.Key, cancellationToken);
            string? typedCloudTarget = null;
            if (typedCloudRestore is not null)
            {
                var typedOutcome = await RestoreTypedCloudRecoveryAsync(pair, typedCloudRestore, restoreAsCopy,
                    preview.TargetRelativePath, commandId, restoreAdmission, cancellationToken);
                detail = typedOutcome.Detail;
                // Report the independently verified destination of this exact admitted action.
                typedCloudTarget = typedOutcome.DestinationPath;
            }
            else switch (item.Source)
            {
                case SyncRecoverySource.LocalArchive:
                    detail = await RestoreLocalArchiveAsync(pair, item, preview.TargetRelativePath, commandId,
                        cancellationToken);
                    break;
                case SyncRecoverySource.DropboxArchive:
                    detail = await RestoreDropboxArchiveAsync(pair, item, preview.TargetRelativePath, commandId,
                        cancellationToken);
                    break;
                case SyncRecoverySource.DropboxRevision:
                    detail = await RestoreDropboxRevisionAsync(pair, item, preview.TargetRelativePath,
                        restoreAsCopy, commandId, cancellationToken);
                    break;
                case SyncRecoverySource.CloudTrash:
                    detail = await RestoreGoogleTrashAsync(pair, item, restoreAsCopy, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException("Load an exact Dropbox revision before restoring a deleted item.");
            }
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(new
                {
                    restored = true,
                    TargetRelativePath = typedCloudTarget ?? preview.TargetRelativePath,
                    detail
                }),
                cancellationToken: CancellationToken.None);
            return new SyncRecoveryRestoreResult(item.PairId, item.Key,
                typedCloudTarget ?? preview.TargetRelativePath, detail);
        }
        catch (Exception exception)
        {
            await RetainRunCommandFailureAsync(commandId, restorePair is null ? [] : [restorePair],
                "recovery_restore_failed", restoreStage, exception, preview.TargetRelativePath,
                "Open this recovery run's operation log and retained recovery copy. Check the exact cause and current destination before retrying; an uncertain effect is not safe to repeat.",
                "Recovery restore reported an error");
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    // Async by contract: the GUI and every other caller awaits this instead of blocking the dispatcher
    // on a synchronous authority read.
    public async Task<IReadOnlyList<SyncRewindSession>> LoadRewindSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await EnsureRewindStoragePublishedAsync(cancellationToken);
        return await _rewindStore.LoadAsync(cancellationToken);
    }

    public async Task<SyncRewindHandoff> PrepareRewindAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await EnsureRewindStoragePublishedAsync(cancellationToken);
        if (await _rewindStore.LoadActiveAsync(pairId, cancellationToken) is not null)
            throw new InvalidOperationException("This pair already has an active Rewind checkpoint.");
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
                       string.Equals(item.Id, pairId, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested Rewind pair is not retained.");
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
            throw new NotSupportedException(
                "Dropbox Rewind requires a local folder paired with Dropbox. " +
                "Cloud-to-cloud pairs were not paused or changed.");
        if (IsGooglePair(pair))
            throw DriveCapabilityRefusal("Dropbox Rewind", "Choose a Dropbox pair. Nothing was paused or changed.");
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        var commandId = await CreateAcceptedCommandAsync("backup", pair.Id, pair.DropboxFolderId,
            JsonSerializer.Serialize(new { action = "prepare_rewind", pair.DropboxRoot, connection.ConnectionId }),
            cancellationToken);
        PilotRunControl? runControl = null;
        try
        {
            runControl = await BeginRunAsync(cancellationToken, commandId, "backup", pair.Id);
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress: null);
            if (await _authority.LoadPairStateAsync(pair.Id, cancellationToken) != "active")
                throw new InvalidOperationException(
                    "Complete the pair's effect-free first-sync review before preparing Dropbox Rewind.");
            if ((await _authority.ListRunnableOperationsForPairAsync(pair.Id, 1, cancellationToken)).Count != 0)
                throw new InvalidOperationException(
                    "This pair has retained recovery work. Reconcile it before creating a Rewind checkpoint.");
            var observation = await CreateObserver(pair, prepared).ObserveAsync("manual", cancellationToken);
            if (prepared.Remote is not ISyncRemoteChangeFeed feed)
                throw new NotSupportedException("The active Dropbox connection cannot checkpoint a Rewind cursor.");
            var cursor = await feed.GetLatestCursorAsync(cancellationToken);
            var session = new SyncRewindSession(Guid.NewGuid().ToString("N"), connection.ConnectionId, pair.Id,
                pair.Name, pair.DropboxRoot, observation.LocalScan.ScanGenerationId,
                observation.DropboxScan.ScanGenerationId, cursor, DateTimeOffset.UtcNow,
                SyncRewindStatus.AwaitingDropbox, Detail: "Pair paused at a complete local and Dropbox checkpoint.");
            await _rewindStore.UpsertAsync(session, cancellationToken);
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(new
                {
                    session.SessionId,
                    session.LocalCheckpointScanId,
                    session.DropboxCheckpointScanId,
                    paused = true
                }), cancellationToken: CancellationToken.None);
            await StopMonitorsAsync(CancellationToken.None);
            return new SyncRewindHandoff(session, BuildDropboxFolderUrl(pair.DropboxRoot),
                ConnectionDisplay(connection),
                "Dropbox does not expose a supported direct Rewind URL. Open the exact folder, then choose Folder settings → Rewind this folder.");
        }
        catch
        {
            await TryBlockCommandAsync(commandId, "rewind_checkpoint_failed");
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    public async Task<SyncRewindSession> PreviewRewindImportAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await EnsureRewindStoragePublishedAsync(cancellationToken);
        var session = await _rewindStore.LoadActiveAsync(pairId, cancellationToken)
                      ?? throw new InvalidOperationException("Prepare a Rewind checkpoint for this pair first.");
        var retryablePreEffectBlock = session.Status == SyncRewindStatus.ApplyBlocked &&
                                      (session.OperationGroupId is null ||
                                       (await _authority.ListOperationsForGroupAsync(session.OperationGroupId,
                                           cancellationToken)).Count == 0);
        if (session.Status is not (SyncRewindStatus.AwaitingDropbox or SyncRewindStatus.PreviewReady or
                SyncRewindStatus.Blocked) && !retryablePreEffectBlock)
            throw new InvalidOperationException(
                "This Rewind batch has entered durable apply. Reconcile that exact transaction before refreshing or resuming.");
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item => item.Id == pairId);
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        if (!string.Equals(connection.ConnectionId, session.ConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The active Dropbox account differs from the Rewind checkpoint account.");
        PilotRunControl? runControl = null;
        try
        {
            runControl = await BeginRunAsync(cancellationToken, kind: "reconcile", pairId: pair.Id);
            if (retryablePreEffectBlock && session.OperationGroupId is not null)
                await _authority.CancelEmptyOperationGroupAsync(session.OperationGroupId, cancellationToken);
            var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress: null);
            var observed = await CreateObserver(pair, prepared).ObserveAsync("manual", cancellationToken);
            var localBefore = (await _authority.LoadCompleteObservationAsync(pair.Id, "local",
                    session.LocalCheckpointScanId, cancellationToken))
                .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            var remoteBefore = (await _authority.LoadCompleteObservationAsync(pair.Id, "dropbox",
                    session.DropboxCheckpointScanId, cancellationToken))
                .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            var localAfter = (await _authority.LoadCompleteObservationAsync(pair.Id, "local",
                    observed.LocalScan.ScanGenerationId, cancellationToken))
                .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            var remoteAfter = (await _authority.LoadCompleteObservationAsync(pair.Id, "dropbox",
                    observed.DropboxScan.ScanGenerationId, cancellationToken))
                .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            var preview = BuildRewindPreview(localBefore, remoteBefore, localAfter, remoteAfter);
            var updated = session with
            {
                Status = SyncRewindStatus.PreviewReady,
                LocalPreviewScanId = observed.LocalScan.ScanGenerationId,
                DropboxPreviewScanId = observed.DropboxScan.ScanGenerationId,
                Preview = preview,
                OperationGroupId = null,
                PathProgress = preview.Select(item => new SyncRewindPathProgress(item.RelativePath,
                    SyncRewindPathStatus.Pending, null, "Reviewed and awaiting apply.", DateTimeOffset.UtcNow))
                    .ToArray(),
                Detail = $"{preview.Count:N0} Dropbox rollback change(s) detected; no local effect has been applied."
            };
            await _rewindStore.PublishPreviewAsync(updated, session.SessionId, session.LocalPreviewScanId,
                session.DropboxPreviewScanId, cancellationToken);
            return updated;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                           !cancellationToken.IsCancellationRequested)
        {
            var blocked = session with { Status = SyncRewindStatus.Blocked, Detail = exception.Message };
            await _rewindStore.PersistStateAsync(blocked.SessionId, blocked.PairId, SyncRewindStatus.Blocked,
                exception.Message, blocked.OperationGroupId, clearOperationGroup: false, CancellationToken.None);
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    public async Task<SyncRewindApplyResult> ApplyRewindImportAsync(string pairId,
        SyncRewindApplyConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await EnsureRewindStoragePublishedAsync(cancellationToken);
        var session = RequireConfirmedRewindSession(await _rewindStore.LoadActiveAsync(pairId, cancellationToken),
            confirmation);
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item => item.Id == pairId);
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        if (!string.Equals(connection.ConnectionId, session.ConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The active Dropbox account differs from the Rewind checkpoint account.");
        using var pendingRun = await BeginPendingRunAsync(cancellationToken, "reconcile", pair.Id);
        cancellationToken = pendingRun.Token;
        PilotRunControl? runControl = null;
        string? commandId = null;
        var commandTerminal = "completed";
        var confirmedPreviewAdmitted = false;
        try
        {
            if (RewindApplyPhaseForTest is not null)
                await RewindApplyPhaseForTest("confirmed_preview_admission");
            // Rebind under exclusive admission before any effect. A stale refusal
            // must not overwrite or mark the replacement preview blocked.
            session = RequireConfirmedRewindSession(await _rewindStore.LoadActiveAsync(pairId, cancellationToken),
                confirmation);
            var reviewedPreview = session.Preview ??
                throw new InvalidOperationException("The confirmed Rewind preview is unavailable.");
            var commandAdmission = await CreateAcceptedCommandAdmissionAsync("reconcile", pair.Id,
                session.SessionId, JsonSerializer.Serialize(new
                {
                    purpose = "rewind_apply", session.SessionId, pairIds = new[] { pair.Id },
                    confirmation.LocalPreviewScanId, confirmation.DropboxPreviewScanId
                }), cancellationToken, reservedCommandId: pendingRun.CommandId,
                reservation: pendingRun.Reservation);
            commandId = commandAdmission.CommandRequestId;
            runControl = pendingRun.EnterExecution();
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            confirmedPreviewAdmitted = true;
            var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress: null,
                forceReviewedDeletionEffects: true);
            var operationGroupId = session.OperationGroupId;
            var previewHash = Id("rewind-preview", session.PairId, confirmation.LocalPreviewScanId,
                confirmation.DropboxPreviewScanId,
                string.Join('\n', reviewedPreview.Select(item => item.RelativePath)
                    .Order(StringComparer.OrdinalIgnoreCase)));
            var operationGroupDetail = JsonSerializer.Serialize(new
            {
                purpose = "rewind_apply",
                session.SessionId,
                session.PairId,
                session.LocalPreviewScanId,
                session.DropboxPreviewScanId,
                previewHash,
                pathCount = reviewedPreview.Count
            });

            var progressByPath = reviewedPreview.ToDictionary(item => item.RelativePath,
                item => (session.PathProgress ?? []).FirstOrDefault(progress =>
                            string.Equals(progress.RelativePath, item.RelativePath,
                                StringComparison.OrdinalIgnoreCase)) ??
                        new SyncRewindPathProgress(item.RelativePath, SyncRewindPathStatus.Pending, null,
                            "Reviewed and awaiting apply.", DateTimeOffset.UtcNow),
                StringComparer.OrdinalIgnoreCase);
            // Terminal counts are maintained incrementally from the one reviewed dictionary that is
            // already in memory. The whole reviewed set is never serialized, sorted or rescanned per
            // checkpoint, and the ordered array is materialized only at a final public boundary.
            var terminalCompleted = progressByPath.Values.Count(item =>
                item.Status == SyncRewindPathStatus.Completed);
            var terminalConflicts = progressByPath.Values.Count(item =>
                item.Status == SyncRewindPathStatus.Conflict);
            async Task PersistCheckpointAsync(SyncAuthorityReviewedPathCheckpoint checkpoint)
            {
                if (!progressByPath.TryGetValue(checkpoint.RelativePath, out var current))
                    throw new InvalidDataException("A Rewind checkpoint escaped its exact reviewed path set.");
                var updated = current with
                {
                    Status = checkpoint.Status,
                    OperationId = checkpoint.OperationId,
                    Detail = checkpoint.Decision.Reason,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                if (updated.Status == SyncRewindPathStatus.Completed &&
                    current.Status != SyncRewindPathStatus.Completed) terminalCompleted++;
                if (updated.Status == SyncRewindPathStatus.Conflict &&
                    current.Status != SyncRewindPathStatus.Conflict) terminalConflicts++;
                progressByPath[checkpoint.RelativePath] = updated;
                session = session with
                {
                    Status = SyncRewindStatus.Applying,
                    Detail = $"Applying reviewed rollback paths: {terminalCompleted + terminalConflicts:N0}/{progressByPath.Count:N0} terminal."
                };
                await _rewindStore.PersistPathCheckpointAsync(session.SessionId, session.PairId,
                    session.LocalPreviewScanId!, session.DropboxPreviewScanId!, operationGroupId, checkpoint,
                    SyncRewindStatus.Applying, session.Detail ?? string.Empty, CancellationToken.None);
            }

            var reviewedPaths = progressByPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var runner = CreatePairRunner(pair, prepared, runControl, commandAdmission, prepared.CloudFiles);
            var recovery = new SyncRunResult(pair.Id, []);
            if (operationGroupId is not null)
            {
                await _authority.RegisterOperationGroupAsync(operationGroupId, "compound", operationGroupDetail,
                    cancellationToken);
                await _authority.MarkOperationGroupActiveAsync(operationGroupId, cancellationToken);
                session = session with
                {
                    Status = SyncRewindStatus.Applying,
                    OperationGroupId = operationGroupId,
                    PathProgress = progressByPath.Values.OrderBy(item => item.RelativePath,
                        StringComparer.OrdinalIgnoreCase).ToArray(),
                    Detail = "Rewind apply recovery is active; every retained path transition remains durably checkpointed."
                };
                await _rewindStore.PersistStateAsync(session.SessionId, session.PairId, SyncRewindStatus.Applying,
                    session.Detail ?? string.Empty, operationGroupId, clearOperationGroup: false,
                    cancellationToken);
                recovery = await runner.RecoverReviewedOperationGroupAsync(operationGroupId, reviewedPaths,
                    PersistCheckpointAsync, cancellationToken);
                if (recovery.Decisions.Any(item => item.Kind is SyncDecisionKind.ResponseUnknown or
                        SyncDecisionKind.Blocked or SyncDecisionKind.CleanupPending or SyncDecisionKind.Stopped) ||
                    progressByPath.Values.Any(item => item.OperationId is not null && !IsRewindTerminal(item)))
                {
                    var recoveryState = ClassifyRewindApply(progressByPath.Values, recovery);
                    return await RetainRewindApplyResultAsync(session, progressByPath, recoveryState, recovery);
                }
            }
            foreach (var retryable in progressByPath.Values.Where(item => !IsRewindTerminal(item)).ToArray())
                progressByPath[retryable.RelativePath] = retryable with
                {
                    Status = SyncRewindPathStatus.Pending,
                    OperationId = null,
                    Detail = "Revalidated retry is awaiting an exact reviewed effect.",
                    UpdatedUtc = DateTimeOffset.UtcNow
                };

            var verification = await CreateObserver(pair, prepared).ObserveAsync("manual", cancellationToken);
            var expectedDropbox = await _authority.LoadCompleteObservationAsync(pair.Id, "dropbox",
                session.DropboxPreviewScanId!, cancellationToken);
            var currentDropbox = await _authority.LoadCompleteObservationAsync(pair.Id, "dropbox",
                verification.DropboxScan.ScanGenerationId, cancellationToken);
            if (!EquivalentProviderObservationSets(expectedDropbox, currentDropbox))
            {
                const string fullSetReason =
                    "Dropbox changed anywhere in the selected pair after the reviewed Rewind preview. Refresh the preview or restore the exact provider state before retrying; no new local effect was issued.";
                var expectedByPath = expectedDropbox.ToDictionary(item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase);
                var currentByPath = currentDropbox.ToDictionary(item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase);
                var blockedDecisions = new List<SyncDecision>
                {
                    new("<rewind>", SyncDecisionKind.Blocked, fullSetReason)
                };
                foreach (var progress in progressByPath.Values.Where(item => !IsRewindTerminal(item)).ToArray())
                {
                    expectedByPath.TryGetValue(progress.RelativePath, out var expected);
                    currentByPath.TryGetValue(progress.RelativePath, out var current);
                    if (EquivalentProviderObserved(expected, current)) continue;
                    var blocked = progress with
                    {
                        Status = SyncRewindPathStatus.Blocked,
                        OperationId = null,
                        Detail = fullSetReason,
                        UpdatedUtc = DateTimeOffset.UtcNow
                    };
                    progressByPath[progress.RelativePath] = blocked;
                    blockedDecisions.Add(new SyncDecision(progress.RelativePath, SyncDecisionKind.Blocked,
                        fullSetReason));
                }
                var blockedState = progressByPath.Values.Any(IsRewindTerminal)
                    ? SyncRewindStatus.PartiallyApplied
                    : SyncRewindStatus.ApplyBlocked;
                return await RetainRewindApplyResultAsync(session, progressByPath, blockedState,
                    new SyncRunResult(pair.Id, blockedDecisions));
            }

            var pending = progressByPath.Values.Where(item => !IsRewindTerminal(item))
                .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (pending.Count == 0)
            {
                if (operationGroupId is not null)
                    await FinalizeReviewedOperationGroupAsync(operationGroupId, cancellationToken);
                return await RetainRewindApplyResultAsync(session, progressByPath,
                    SyncRewindStatus.AppliedPaused, recovery);
            }

            if (operationGroupId is null)
            {
                operationGroupId = Id("rewind-group", session.SessionId, confirmation.LocalPreviewScanId,
                    confirmation.DropboxPreviewScanId);
                await _authority.RegisterOperationGroupAsync(operationGroupId, "compound", operationGroupDetail,
                    cancellationToken);
                await _authority.MarkOperationGroupActiveAsync(operationGroupId, cancellationToken);
                session = session with
                {
                    Status = SyncRewindStatus.Applying,
                    OperationGroupId = operationGroupId,
                    PathProgress = progressByPath.Values.OrderBy(item => item.RelativePath,
                        StringComparer.OrdinalIgnoreCase).ToArray(),
                    Detail = "Rewind apply is active; every path transition is durably checkpointed."
                };
                await _rewindStore.PersistStateAsync(session.SessionId, session.PairId, SyncRewindStatus.Applying,
                    session.Detail ?? string.Empty, operationGroupId, clearOperationGroup: false,
                    cancellationToken);
                if (RewindApplyPhaseForTest is not null)
                    await RewindApplyPhaseForTest("applying_checkpoint");
                runControl.StopToken.ThrowIfCancellationRequested();
            }

            var apply = await runner.RunReviewedAsync("Rewind apply", pending, operationGroupId,
                PersistCheckpointAsync, cancellationToken);
            var combined = new SyncRunResult(pair.Id, recovery.Decisions.Concat(apply.Decisions)
                .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last()).ToArray());
            var applyState = ClassifyRewindApply(progressByPath.Values, combined);
            if (applyState == SyncRewindStatus.AppliedPaused)
                await FinalizeReviewedOperationGroupAsync(operationGroupId, cancellationToken);
            return await RetainRewindApplyResultAsync(session, progressByPath, applyState, combined);
        }
        catch (OperationCanceledException) when (confirmedPreviewAdmitted && runControl?.IsStopRequested == true)
        {
            var stopped = session with
            {
                Status = SyncRewindStatus.ApplyStopped,
                Detail = "Rewind apply stopped at a durable boundary. Completed paths remain recorded; retry Apply to reconcile the retained transaction."
            };
            await _rewindStore.PersistStateAsync(stopped.SessionId, stopped.PairId, SyncRewindStatus.ApplyStopped,
                stopped.Detail ?? string.Empty, stopped.OperationGroupId, clearOperationGroup: false,
                CancellationToken.None);
            return new SyncRewindApplyResult(stopped, new SyncRunResult(pair.Id,
            [
                new SyncDecision("<rewind>", SyncDecisionKind.Stopped, stopped.Detail ?? string.Empty)
            ]));
        }
        catch (Exception exception)
        {
            commandTerminal = "blocked";
            if (!confirmedPreviewAdmitted) throw;
            var retained = await _rewindStore.LoadActiveAsync(pairId, CancellationToken.None) ?? session;
            var completed = (retained.PathProgress ?? []).Count(IsRewindTerminal);
            var status = (retained.PathProgress ?? []).Any(item => item.Status == SyncRewindPathStatus.EffectUnknown)
                ? SyncRewindStatus.EffectUnknown
                : completed > 0 ? SyncRewindStatus.PartiallyApplied : SyncRewindStatus.ApplyBlocked;
            await _rewindStore.PersistStateAsync(retained.SessionId, retained.PairId, status, exception.Message,
                retained.OperationGroupId, clearOperationGroup: false, CancellationToken.None);
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                try
                {
                    await FinishRunActivityAsync(runControl);
                    if (commandId is not null)
                        await _authority.TransitionCommandRequestAsync(commandId, commandTerminal,
                            JsonSerializer.Serialize(new { purpose = "rewind_apply", stopped = runControl.IsStopRequested }),
                            CancellationToken.None);
                }
                finally { EndRun(runControl); }
            }
        }
    }

    private static SyncRewindSession RequireConfirmedRewindSession(SyncRewindSession? session,
        SyncRewindApplyConfirmation confirmation)
    {
        if (session is null)
            throw new InvalidOperationException("No active Rewind checkpoint exists for this pair.");
        if (session.Status is not (SyncRewindStatus.PreviewReady or SyncRewindStatus.Applying or
                SyncRewindStatus.PartiallyApplied or SyncRewindStatus.ApplyStopped or
                SyncRewindStatus.EffectUnknown or SyncRewindStatus.ApplyBlocked) || session.Preview is null ||
            session.LocalPreviewScanId is null || session.DropboxPreviewScanId is null)
            throw new InvalidOperationException("Detect and preview the Dropbox Rewind batch before applying it.");
        if (confirmation is null || !confirmation.Matches(session))
            throw new InvalidOperationException("The Rewind session or preview changed after it was reviewed. " +
                "Detect and review the current preview, then confirm that exact batch; no new local effect was issued.");
        return session;
    }

    private static bool IsRewindTerminal(SyncRewindPathProgress progress) =>
        progress.Status is SyncRewindPathStatus.Completed or SyncRewindPathStatus.Conflict;

    private async Task FinalizeReviewedOperationGroupAsync(string operationGroupId,
        CancellationToken cancellationToken, Func<SyncComparisonProgress, Task>? onProgress = null)
    {
        var started = DateTimeOffset.UtcNow;
        Task Report(int loaded, int total, string operationId) => onProgress is null
            ? Task.CompletedTask
            : onProgress(new SyncComparisonProgress("Confirming Reviewed Transfer Records", operationGroupId,
                loaded, total, started, DateTimeOffset.UtcNow,
                operationId.Length > 0
                    ? "Reading retained operation " + operationId +
                      ": its current status, members, dependencies, allocations and attempts. No file transfer is running."
                    : loaded < total
                        ? "Reading this operation group's member/allocation records in two batched database queries. No file transfer is running."
                        : "The operation records are loaded; checking whether the retained group can finish. No file transfer is running.",
                "operation records loaded"));
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress("Confirming Reviewed Transfer Records", operationGroupId,
                0, 0, started, DateTimeOffset.UtcNow,
                "Reading the exact retained operation IDs for this completed transfer group. The record count is not yet known; no file transfer is running.",
                "operation records loaded"));
        var operations = await _authority.ListOperationsForGroupAsync(operationGroupId, cancellationToken, Report);
        if (operations.Count == 0)
            await _authority.CancelEmptyOperationGroupAsync(operationGroupId, cancellationToken);
        else if (operations.All(operation => operation.Status == SyncAuthorityOperationStatus.Completed))
            await _authority.CompleteOperationGroupAsync(operationGroupId, cancellationToken);
    }

    private static SyncRewindStatus ClassifyRewindApply(IEnumerable<SyncRewindPathProgress> progress,
        SyncRunResult result)
    {
        var retained = progress.ToArray();
        if (retained.Any(item => item.Status == SyncRewindPathStatus.EffectUnknown) ||
            result.Decisions.Any(item => item.Kind == SyncDecisionKind.ResponseUnknown))
            return SyncRewindStatus.EffectUnknown;
        if (retained.Any(item => item.Status == SyncRewindPathStatus.Stopped) || result.Stopped)
            return SyncRewindStatus.ApplyStopped;
        if (retained.Any(item => item.Status == SyncRewindPathStatus.Blocked) || result.Blocked > 0)
            return retained.Any(IsRewindTerminal)
                ? SyncRewindStatus.PartiallyApplied
                : SyncRewindStatus.ApplyBlocked;
        if (retained.All(IsRewindTerminal)) return SyncRewindStatus.AppliedPaused;
        return retained.Any(IsRewindTerminal)
            ? SyncRewindStatus.PartiallyApplied
            : SyncRewindStatus.ApplyBlocked;
    }

    private async Task<SyncRewindApplyResult> RetainRewindApplyResultAsync(SyncRewindSession session,
        IReadOnlyDictionary<string, SyncRewindPathProgress> progress,
        SyncRewindStatus status, SyncRunResult result)
    {
        var ordered = progress.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var completed = ordered.Count(item => item.Status == SyncRewindPathStatus.Completed);
        var conflicts = ordered.Count(item => item.Status == SyncRewindPathStatus.Conflict);
        var blocked = ordered.Count(item => item.Status == SyncRewindPathStatus.Blocked);
        var unknown = ordered.Count(item => item.Status == SyncRewindPathStatus.EffectUnknown);
        var remaining = ordered.Count(item => item.Status is SyncRewindPathStatus.Pending or
            SyncRewindPathStatus.Planned or SyncRewindPathStatus.Stopped);
        var globalBlock = result.Decisions.FirstOrDefault(item =>
            item.Kind == SyncDecisionKind.Blocked && item.RelativePath == "<rewind>");
        var detail = status == SyncRewindStatus.AppliedPaused
            ? $"Rewind batch is complete: {completed:N0} applied/already-converged path(s), {conflicts:N0} preserved conflict(s). Pair remains paused until explicit resume."
            : globalBlock is not null
                ? globalBlock.Reason
            : $"Rewind apply is {status}: {completed:N0} complete, {conflicts:N0} preserved conflict(s), {blocked:N0} blocked, {unknown:N0} effect-unknown, {remaining:N0} unfinished. Pair remains paused.";
        var updated = session with { Status = status, PathProgress = ordered, Detail = detail };
        await _rewindStore.UpsertAsync(updated, CancellationToken.None);
        return new SyncRewindApplyResult(updated, result);
    }

    public async Task<SyncRewindSession> ResumeAfterRewindAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await EnsureRewindStoragePublishedAsync(cancellationToken);
        var session = await _rewindStore.LoadActiveAsync(pairId, cancellationToken)
                      ?? throw new InvalidOperationException("No paused Rewind checkpoint exists for this pair.");
        if (session.Status == SyncRewindStatus.AppliedPaused)
        {
            if (session.Preview is not null &&
                (session.PathProgress is null || session.PathProgress.Count != session.Preview.Count ||
                 session.PathProgress.Any(item => !IsRewindTerminal(item))))
                throw new InvalidOperationException("Rewind cannot resume until every reviewed path is terminal.");
            if (session.OperationGroupId is not null &&
                (await _authority.ListOperationsForGroupAsync(session.OperationGroupId, cancellationToken))
                .Any(item => item.Status != SyncAuthorityOperationStatus.Completed))
                throw new InvalidOperationException("Rewind cannot resume while its durable operation group is incomplete.");
        }
        else if (session.Status is SyncRewindStatus.AwaitingDropbox or SyncRewindStatus.PreviewReady or
                 SyncRewindStatus.Blocked)
        {
            var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item => item.Id == pairId);
            var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
            if (!string.Equals(connection.ConnectionId, session.ConnectionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active Dropbox account differs from the Rewind checkpoint account.");
            PilotRunControl? runControl = null;
            try
            {
                runControl = await BeginRunAsync(cancellationToken, kind: "reconcile", pairId: pair.Id);
                var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress: null);
                var observed = await CreateObserver(pair, prepared).ObserveAsync("manual", cancellationToken);
                var checkpoint = await _authority.LoadCompleteObservationAsync(pair.Id, "dropbox",
                    session.DropboxCheckpointScanId, cancellationToken);
                var current = await _authority.LoadCompleteObservationAsync(pair.Id, "dropbox",
                    observed.DropboxScan.ScanGenerationId, cancellationToken);
                if (!EquivalentObservationSets(checkpoint, current))
                    throw new InvalidOperationException(
                        "Dropbox differs from the pre-Rewind checkpoint. Detect, review, and apply that rollback batch before resuming ordinary sync.");
            }
            finally
            {
                if (runControl is not null)
                {
                    await FinishRunActivityAsync(runControl);
                    EndRun(runControl);
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                "Rewind apply is incomplete, stopped, blocked, or effect-unknown. Retry Apply until every reviewed path reaches the safe paused boundary.");
        }
        var updated = session with
        {
            Status = SyncRewindStatus.Resumed,
            Detail = "The Rewind checkpoint is closed and ordinary synchronization may resume."
        };
        await _rewindStore.PersistStateAsync(updated.SessionId, updated.PairId, SyncRewindStatus.Resumed,
            updated.Detail ?? string.Empty, updated.OperationGroupId, clearOperationGroup: false, cancellationToken);
        return updated;
    }

    // The explicit local Rewind publication sequence. It runs after authority initialization and pair
    // import, and before monitors or command recovery can admit Rewind-sensitive work. The exact legacy
    // collection is validated through the one shared normalizer, imported under its existing session
    // ids with exact per-session equality, and then cleared in one compare-and-publish aggregate update
    // that leaves only the bounded storage reference. No provider, credential, signing or file effect is
    // involved, and nothing here can create a second active authority.
    private async Task EnsureRewindStoragePublishedAsync(CancellationToken cancellationToken,
        string? resolvedInstallationId = null)
    {
        if (_rewindStoragePublished) return;
        // Resolve the installation identity BEFORE taking the publication gate: the first resolution
        // runs the existing initialization sequence, which calls this method again.
        var installationId = resolvedInstallationId ?? _installationId ??
            await EnsureAuthorityInitializedAsync(cancellationToken);
        await _rewindPublicationGate.WaitAsync(cancellationToken);
        try
        {
            if (_rewindStoragePublished) return;
            var state = _rewindProductState.Load();
            var pairIds = state.PairSet.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var legacy = SyncRewindState.NormalizeAll(state.RewindSessions ?? [], pairIds);
            if (state.InstallationId is not null &&
                !string.Equals(state.InstallationId, installationId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The retained Rewind aggregate belongs to another installation identity; both copies were preserved.");
            if (legacy.Count == 0)
            {
                await PublishRewindStorageReferenceAsync(null, installationId, cancellationToken);
                _rewindStoragePublished = true;
                return;
            }
            foreach (var pairId in legacy.Select(item => item.PairId).Distinct(StringComparer.Ordinal))
                if (!await _authority.PairDefinitionIsRetainedAsync(pairId, cancellationToken))
                    throw new InvalidDataException(
                        "A retained legacy Rewind checkpoint refers to a sync pair that is not registered in the authority database; both copies were preserved.");
            var digest = SyncRewindState.CanonicalDigest(legacy);
            foreach (var session in legacy)
            {
                if (await _rewindStore.MatchesAsync(session, cancellationToken)) continue;
                if (await _authority.RewindSessionExistsAsync(session.SessionId, cancellationToken))
                    throw new InvalidDataException(
                        "A retained Rewind session conflicts with the exact identity being imported; both copies were preserved.");
                await _rewindStore.UpsertAsync(session, cancellationToken);
            }
            await PublishRewindStorageReferenceAsync(digest, installationId, cancellationToken);
            _rewindStoragePublished = true;
        }
        finally
        {
            _rewindPublicationGate.Release();
        }
    }

    // Compare-and-publish: the aggregate is re-read under its own store gate, so a collection that
    // changed after import refuses publication and leaves both copies intact.
    private async Task PublishRewindStorageReferenceAsync(string? expectedDigest, string installationId,
        CancellationToken cancellationToken)
    {
        await _rewindProductState.UpdateAsync(current =>
        {
            var pairIds = current.PairSet.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var retained = SyncRewindState.NormalizeAll(current.RewindSessions ?? [], pairIds);
            if (expectedDigest is not null &&
                !string.Equals(SyncRewindState.CanonicalDigest(retained), expectedDigest, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained legacy Rewind collection changed while it was being published; both copies were preserved.");
            if (expectedDigest is null && retained.Count != 0)
                throw new InvalidOperationException(
                    "The retained legacy Rewind collection appeared while Rewind storage was being published; both copies were preserved.");
            if (current.RewindStorage is not null &&
                !string.Equals(current.RewindStorage.InstallationId, installationId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The retained Rewind storage reference belongs to another installation identity.");
            return current with
            {
                RewindSessions = [],
                RewindStorage = SyncRewindStorageReference.Create(installationId)
            };
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SyncConflictComparison>> LoadOpenConflictComparisonsAsync(
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        if (!File.Exists(_authority.DatabasePath)) return [];
        var comparisons = new List<SyncConflictComparison>();
        for (var offset = 0;;)
        {
            var page = await _authority.LoadOpenConflictProjectionPageAsync(500, offset,
                cancellationToken: cancellationToken);
            comparisons.AddRange(page.Select(ConflictComparison));
            if (page.Count < 500) return comparisons;
            offset = checked(offset + page.Count);
        }
    }

    public async Task<IReadOnlyList<SyncConflictComparison>> LoadOpenConflictComparisonsPageAsync(int limit,
        int offset, string? search, string? group = null, CancellationToken cancellationToken = default,
        string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        if (!File.Exists(_authority.DatabasePath)) return [];
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ValidateConflictGroup(group);
        var comparisons = new List<SyncConflictComparison>();
        var matchingSkipped = 0;
        var rawOffset = 0;
        var allowedPairs = await AccountPairIdsAsync(connectionId, cancellationToken);
        while (comparisons.Count < limit)
        {
            var rows = await _authority.LoadOpenConflictProjectionPageAsync(500, rawOffset, search,
                cancellationToken: cancellationToken, allowedPairIds: allowedPairs);
            if (rows.Count == 0) break;
            rawOffset = checked(rawOffset + rows.Count);
            foreach (var row in rows)
            {
                var comparison = ConflictComparison(row);
                if (!ConflictGroupMatches(comparison.Group, group)) continue;
                if (matchingSkipped < offset)
                {
                    matchingSkipped++;
                    continue;
                }
                comparisons.Add(comparison);
                if (comparisons.Count == limit) break;
            }
            if (rows.Count < 500) break;
        }
        return comparisons;
    }

    public async Task<int> CountOpenConflictComparisonsAsync(string? search, string? group = null,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        if (!File.Exists(_authority.DatabasePath)) return 0;
        ValidateConflictGroup(group);
        var count = 0;
        for (var rawOffset = 0;;)
        {
            var rows = await _authority.LoadOpenConflictProjectionPageAsync(500, rawOffset, search,
                cancellationToken: cancellationToken);
            if (rows.Count == 0) return count;
            rawOffset = checked(rawOffset + rows.Count);
            foreach (var row in rows)
            {
                var comparison = ConflictComparison(row);
                if (ConflictGroupMatches(comparison.Group, group)) count = checked(count + 1);
            }
            if (rows.Count < 500) return count;
        }
    }

    public async Task<IReadOnlyList<SyncConflictSelection>> CaptureOpenConflictSelectionAsync(string? search,
        string? group = null, CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        if (!File.Exists(_authority.DatabasePath)) return [];
        ValidateConflictGroup(group);
        var selection = new List<SyncConflictSelection>();
        var allowedPairs = await AccountPairIdsAsync(connectionId, cancellationToken);
        for (var rawOffset = 0;;)
        {
            var rows = await _authority.LoadOpenConflictProjectionPageAsync(500, rawOffset, search,
                cancellationToken: cancellationToken, allowedPairIds: allowedPairs);
            if (rows.Count == 0) return selection;
            rawOffset = checked(rawOffset + rows.Count);
            foreach (var row in rows)
            {
                var comparison = ConflictComparison(row);
                if (ConflictGroupMatches(comparison.Group, group))
                    selection.Add(new SyncConflictSelection(comparison.ConflictId, comparison.PairId,
                        comparison.RelativePath, comparison.Generation));
            }
            if (rows.Count < 500) return selection;
        }
    }

    private static SyncConflictComparison ConflictComparison(SyncAuthorityConflictProjection row)
    {
        var localModified = SyncBootstrapActions.IdentityTime(row.LocalIdentityJson, "lastWriteUtc");
        var dropboxModified = row.DropboxEvidenceJson is null
            ? null
            : SyncBootstrapActions.EvidenceTime(row.DropboxEvidenceJson, "modifiedUtc");
        var (group, recommendation) = ConflictRecommendation(localModified, dropboxModified);
        return new SyncConflictComparison(row.ConflictId, row.PairId,
            row.RelativePath, row.ConflictGeneration, row.LocalSize, row.DropboxSize,
            row.LocalHash, row.DropboxHash, localModified, dropboxModified, group,
            recommendation, row.Status);
    }

    /// <summary>
    /// The truthful neutral filter category. The two retained compatibility group keys name a physical
    /// port ('Local newer' is the local port, which is the logical RIGHT for a reversed local layout), so
    /// they are never presented as a Left/Right claim. This one category matches BOTH of them and changes
    /// no stored value: the raw keys stay accepted and addressable exactly as before.
    /// </summary>
    public const string NewerVersionGroup = "Newer version";

    private static bool ConflictGroupMatches(string actual, string? requested) =>
        string.IsNullOrWhiteSpace(requested) || string.Equals(requested, "All groups", StringComparison.Ordinal) ||
        string.Equals(actual, requested, StringComparison.Ordinal) ||
        string.Equals(requested, NewerVersionGroup, StringComparison.Ordinal) &&
        actual is "Local newer" or "Dropbox newer";

    private static void ValidateConflictGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || group is "All groups" or "Local newer" or "Dropbox newer" or
            NewerVersionGroup or "Approximately equal time" or "Unreliable or missing time") return;
        throw new ArgumentOutOfRangeException(nameof(group));
    }

    public async Task<IReadOnlyList<SyncBootstrapReviewItem>> LoadBootstrapReviewAsync(string pairId,
        string? search = null, string? group = null, string? itemKind = null, string? fileType = null,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null)
    {
        var review = await LoadBootstrapReviewCoreAsync(pairId, 0, 0, search, group,
            itemKind, fileType, cancellationToken, reviewedLocalScanId, reviewedRemoteScanId, readAll: true);
        return review.Items;
    }

    public Task<SyncBootstrapReviewPage> LoadBootstrapReviewPageAsync(string pairId,
        int limit = 250, int offset = 0, string? search = null, string? group = null,
        string? itemKind = null, string? fileType = null,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null)
        => LoadBootstrapReviewCoreAsync(pairId, limit, offset, search, group, itemKind, fileType,
            cancellationToken, reviewedLocalScanId, reviewedRemoteScanId, readAll: false);

    // Command preparation needs rows, counts and their exact snapshot identities together.
    // Reuse the same projection as the paged UI without running full comparison joins
    // again just to obtain a one-row page's metadata. This is not a larger public UI page.
    internal Task<SyncBootstrapReviewPage> LoadCompleteBootstrapReviewAsync(string pairId,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null)
        => LoadBootstrapReviewCoreAsync(pairId, 0, 0, null, null, null, null,
            cancellationToken, reviewedLocalScanId, reviewedRemoteScanId, readAll: true);

    private async Task<SyncBootstrapReviewPage> LoadBootstrapReviewCoreAsync(string pairId,
        int limit, int offset, string? search, string? group, string? itemKind, string? fileType,
        CancellationToken cancellationToken, string? reviewedLocalScanId,
        string? reviewedRemoteScanId, bool readAll)
    {
        stateAuthority.VerifyAuthority();
        var reviewPair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
                              string.Equals(item.Id, pairId, StringComparison.Ordinal))
                          ?? throw new InvalidDataException("The requested sync pair is not retained.");
        // CC4B-3: an explicit cloud/cloud pair is reviewed from its OWN typed cloud observations. Both cloud
        // sides are persisted under the pair's retained compatibility tags, so the two complete inventories
        // below are this pair's logical-left and logical-right cloud inventories and are never a local NTFS
        // root observation. The CC1 cloud projection is re-admitted first, so a non-cloud pair can never
        // silently be reviewed as though it were one.
        if (SyncCloudPairConfiguration.IsCloudOnly(reviewPair)) RequireCloudOnlyProjection(reviewPair);
        if (!await _authority.HasCompleteObservationAsync(pairId, "local", cancellationToken) ||
            !await _authority.HasCompleteObservationAsync(pairId, "dropbox", cancellationToken))
            return new SyncBootstrapReviewPage([], offset, 0, 0, 0, 0, 0, false, false, null, null);
        var evidence = readAll
            ? await _authority.LoadBootstrapEvidenceAsync(pairId, search, group,
                itemKind, fileType, cancellationToken, reviewedLocalScanId, reviewedRemoteScanId)
            : await _authority.LoadBootstrapEvidencePageAsync(pairId, limit, offset, search, group,
                itemKind, fileType, cancellationToken, reviewedLocalScanId, reviewedRemoteScanId);
        if (evidence is null)
            return new SyncBootstrapReviewPage([], offset, 0, 0, 0, 0, 0, false, true, null, null);
        // Engine action/group identities stay compatible. Public destinations use the
        // shared ordered-endpoint context for every combination, including a reversed
        // local/cloud pair and two local folders. Never infer a provider from a port name.
        var endpointContext = SyncEndpointContext.Capture(reviewPair);
        string DestinationLabel(string port) => SyncEndpointPresentation.PortTitle(endpointContext, port);
        var reviewedItems = await AddBootstrapDeletionEvidenceAsync(reviewPair, evidence.Items,
            null, cancellationToken);
        var rows = reviewedItems.Select(item =>
        {
            var (recommendation, effect) = item.Group switch
            {
                "Unreliable or missing" => ("Resolve blocked item", "Nothing changes. " +
                    (item.BlockedReason ?? "This item could not be observed reliably. Check access, then scan again.")),
                "Dropbox only" => ("Download", "Copy to " + DestinationLabel("local")),
                "Local only" => ("Upload", "Copy to " + DestinationLabel("dropbox")),
                SyncBootstrapActions.RemoteDeletedGroup => ("DeleteLocal",
                    "Deleted on " + DestinationLabel("dropbox") +
                    " after this copy was last changed — remove this copy; archive to Recovery first (restorable)"),
                SyncBootstrapActions.RemoteUpdatedGroup => ("Download", "Update on " + DestinationLabel("local") +
                    "; this endpoint still matches the last successful sync"),
                SyncBootstrapActions.LocalUpdatedGroup => ("Upload", "Update on " + DestinationLabel("dropbox") +
                    "; this endpoint still matches the last successful sync"),
                "Identical" => ("No change", "Record as already in sync"),
                // Newest wins: the same 2-second rule the planner applies. The display groups
                // (including the 2-minute "approximately equal" label) are unchanged and decide nothing.
                "Local newer" or "Dropbox newer" or "Approximately equal time" or "Unreliable or missing time" =>
                    SyncBootstrapActions.NewerPort(item.LocalModifiedUtc, item.DropboxModifiedUtc) is { } newer
                        ? ("Keep newer", "Keep the newer version (" + DestinationLabel(newer) + ") on both sides; the older version is archived to Recovery first")
                        : (item.LocalModifiedUtc is null || item.DropboxModifiedUtc is null
                            ? "Review conflict" : "Tie — needs your decision",
                            SyncBootstrapActions.ManualReason(item.LocalModifiedUtc, item.DropboxModifiedUtc)),
                _ => ("Review conflict", "Nothing changes until you choose")
            };
            if (item.Group is "Local only" or "Dropbox only" && item.BlockedReason is { } deletionReason)
                effect += "; " + deletionReason;
            if (item.Move is { } move)
            {
                recommendation = move.Action;
                effect = "Move on " + DestinationLabel(move.Kind == SyncAuthorityOperationKind.MoveLocal
                    ? "local" : "dropbox") + ": " + move.FromPath + " → " + item.RelativePath;
            }
            return new SyncBootstrapReviewItem(pairId, item.RelativePath, item.Group, item.ObjectKind,
                BootstrapFileType(item.RelativePath, item.ObjectKind), recommendation,
                effect, item.LocalSize, item.DropboxSize, item.LocalModifiedUtc, item.DropboxModifiedUtc, item.Move);
        }).ToArray();
        return new SyncBootstrapReviewPage(rows, evidence.Offset, evidence.TotalMatchingItems,
            evidence.TotalItems, evidence.LocalOnlyItems, evidence.DropboxOnlyItems,
            evidence.ConflictItems, evidence.HasMore, true, evidence.LocalScanGenerationId,
            evidence.DropboxScanGenerationId, evidence.LocalUpdatedItems, evidence.DropboxUpdatedItems);
    }

    private async Task<IReadOnlyList<SyncAuthorityBootstrapEvidence>> AddBootstrapDeletionEvidenceAsync(
        SyncPair pair, IReadOnlyList<SyncAuthorityBootstrapEvidence> items, ISyncRemoteStore? remote,
        CancellationToken token)
    {
        // Provider history is consulted only for first-sync files with no saved baseline.
        // Never interpret the local compatibility port of a cloud/cloud pair as NTFS.
        if (pair.InitialReviewApproved != false || SyncCloudPairConfiguration.IsCloudOnly(pair)) return items;
        var applying = remote is not null;
        var candidates = items.Where(item => item.Group == "Local only" && item.ObjectKind == "file" &&
            !item.HasBaseline && item.Move is null).ToArray();
        IReadOnlyDictionary<string, SyncRemoteDeletionEvidence> deletions =
            new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Length > 0)
        {
            try
            {
                if (remote is null && (IsLocalPair(pair) || IsGooglePair(pair)))
                    deletions = candidates.ToDictionary(item => item.RelativePath,
                        _ => new SyncRemoteDeletionEvidence(null, IsLocalPair(pair)
                            ? "Local deletion detection is unavailable; Recycle Bin history is not inspected."
                            : "Google Drive deletion-time evidence is unavailable in this adapter; absence alone does not prove deletion."),
                        StringComparer.OrdinalIgnoreCase);
                else
                {
                    remote ??= await CreateRemoteStoreForPairAsync(pair, token);
                    deletions = await remote.LookupDeletionEvidenceAsync(
                        candidates.Select(item => item.RelativePath).ToArray(), token);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or HttpRequestException or
                DropboxApiException or GoogleDriveApiException or InvalidOperationException)
            {
                deletions = candidates.ToDictionary(item => item.RelativePath,
                    _ => new SyncRemoteDeletionEvidence(null,
                        "Deletion history could not be checked; retry the review before copying this file.", true),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        return items.Select(item =>
        {
            if (item.Group == "Dropbox only" && !item.HasBaseline && item.Move is null)
                return item with { BlockedReason = "Local deletion detection is unavailable; Recycle Bin history is not inspected." };
            if (item.Group != "Local only" || item.ObjectKind != "file" || item.HasBaseline || item.Move is not null)
                return item;
            if (!deletions.TryGetValue(item.RelativePath, out var evidence))
                evidence = new(null, "No conclusive provider deletion evidence was returned.");
            if (evidence.LookupFailed && applying)
                throw new InvalidOperationException("Deletion history is unavailable for " + item.RelativePath + ". " + evidence.Reason);
            if (evidence.LookupFailed)
                return item with { Group = "Unreliable or missing", BlockedReason = evidence.Reason };
            if (evidence.DeletedUtc is { } deleted && item.LocalModifiedUtc is { } modified &&
                deleted > modified && item.LocalObjectId is not null && item.LocalObjectVersionId is not null)
                return item with { Group = SyncBootstrapActions.RemoteDeletedGroup };
            return item with { BlockedReason = evidence.Reason +
                (item.LocalModifiedUtc is null ? " This copy has no reliable last-modified time; removal is not recommended." :
                    evidence.DeletedUtc is not null ? " The deletion is not later than this copy's last change; keep the copy recommendation." : "") };
        }).ToArray();
    }

    public async Task RefreshBootstrapInventoryAsync(string pairId,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        await RefreshBootstrapInventoryCommandAsync(pairId, false, cancellationToken, onProgress);
    }

    // A preparation proof refreshes and compares real inventories under one run lease. It never
    // drains retained operations, approves first sync, or calls an executor. Ordinary coordinated
    // preparation is NOT a dry run: it may dispatch interrupted work before observing the pair.
    public async Task<SyncBootstrapReviewPage> PrepareBootstrapInventoryAsync(string pairId,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) =>
        (await RefreshBootstrapInventoryCommandAsync(pairId, true, cancellationToken, onProgress))!;

    private async Task<SyncBootstrapReviewPage?> RefreshBootstrapInventoryCommandAsync(string pairId,
        bool compareReview, CancellationToken cancellationToken,
        Func<SyncAuthorityBatchProgress, Task>? onProgress)
    {
        stateAuthority.VerifyAuthority();
        if (string.IsNullOrWhiteSpace(pairId))
            throw new InvalidDataException("The requested sync pair is not retained.");
        // r28: the stoppable preparing control is reserved and published BEFORE the pair read, so a Stop
        // target always exists even while the health gate or the database read is held. An invalid pair
        // still creates no durable command: only the temporary preparing control is disposed.
        using var pendingRun = await BeginPendingRunAsync(cancellationToken, "scan", pairId);
        var admissionToken = pendingRun.Token;
        var pair = (await LoadPairsAsync(admissionToken)).Pairs.SingleOrDefault(item =>
                       string.Equals(item.Id, pairId, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (compareReview && pair.InitialReviewApproved != false)
            throw new InvalidOperationException("First sync has already been reviewed for this pair.");
        var firstSyncScan = pair.InitialReviewApproved == false;
        var commandId = await CreateAcceptedCommandAsync("run_pair", pair.Id, null,
            JsonSerializer.Serialize(new { trigger = firstSyncScan ? "First sync scan" : "Folder scan", pairIds = new[] { pair.Id },
                pairNames = new[] { pair.Name } }), admissionToken, authorizesFileEffects: false,
            reservedCommandId: pendingRun.CommandId, reservation: pendingRun.Reservation);
        PilotRunControl? runControl = null;
        var terminalCommitted = false;
        try
        {
            runControl = pendingRun.EnterExecution();
            await using var databaseConnections = _authority.BeginConnectionReuseScope();
            using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                runControl.StopToken);
            cancellationToken = scanCancellation.Token;
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            await RefreshBootstrapInventoryCoreAsync(pair, cancellationToken, onProgress);
            SyncBootstrapReviewPage? review = null;
            if (compareReview)
            {
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null,
                    null, "Comparing the complete retained inventories and calculating first-sync choices; no file actions are running."));
                review = await LoadCompleteBootstrapReviewAsync(pair.Id, cancellationToken);
                if (!review.InventoryComplete || review.HasMore || review.Items.Count != review.TotalItems)
                    throw new InvalidDataException("Preparation did not return a complete first-sync review. No file actions were started.");
            }
            var scanResult = new Dictionary<string, object?>
            {
                ["pairId"] = pair.Id, ["inventoryScanComplete"] = true,
                ["preparationComplete"] = compareReview, ["totalItems"] = review?.TotalItems,
                ["localScanGenerationId"] = review?.LocalScanGenerationId,
                ["remoteScanGenerationId"] = review?.DropboxScanGenerationId
            };
            if (firstSyncScan) scanResult["firstSyncScanComplete"] = true;
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(scanResult),
                cancellationToken: CancellationToken.None);
            terminalCommitted = true;
            await AppendFirstSyncProblemEventAsync(pair, commandId, "Success",
                firstSyncScan
                    ? "Both folder inventories were refreshed. No file actions were started; review your choices before syncing."
                    : "Both folder inventories were refreshed. No file actions were started; first-sync approval and pair settings are unchanged.",
                firstSyncScan ? "First sync scan completed" : "Folder scan completed");
            return review;
        }
        catch (Exception error)
        {
            if (terminalCommitted) throw;
            if (runControl is null)
            {
                // Admission failed before this scan owned the run lease. Keep the refused
                // command trace, but never replace another run's progress or create a scan
                // problem for an inventory operation that did not start.
                await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                    JsonSerializer.Serialize(new { pairId = pair.Id, scanStarted = false,
                        message = "The folder scan did not start because another command is active or the request was cancelled." }),
                    cancellationToken: CancellationToken.None);
                throw;
            }
            var stopped = error is OperationCanceledException;
            if (!firstSyncScan)
            {
                await RetainRunCommandFailureAsync(commandId, [pair],
                    stopped ? "pair_scan_stopped" : "pair_scan_failed", "Reading folder inventories (no file actions)", error);
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Blocked", null, null,
                    "The folder scan did not complete. No file actions were started. " + SyncActivityText.Failure(error)));
                throw;
            }
            var identityCollision = error.Data[nameof(SyncAuthorityIdentityCollision)] as SyncAuthorityIdentityCollision;
            var current = ActiveRun?.Progress?.Comparison;
            var reason = error switch
            {
                DropboxApiException api => api.SafeReason,
                GoogleDriveApiException api => api.SafeReason,
                DirectoryNotFoundException => "A selected folder is no longer available.",
                FileNotFoundException => "An item disappeared while its inventory was being read.",
                UnauthorizedAccessException => "Access to a selected location was denied.",
                HttpRequestException => "The provider request could not be completed.",
                IOException => "Reading or retaining the folder inventory failed.",
                _ => "The inventory operation was refused or interrupted."
            };
            var failure = FirstSyncProblemJson(pair, commandId,
                stopped ? "first_sync_scan_stopped" : identityCollision is not null
                    ? "first_sync_identity_recovery_required" : "first_sync_scan_failed", "scan",
                stopped ? DescribeFirstSyncScanInterruption(current, runControl.StopToken.IsCancellationRequested)
                    : "First-sync scan failed during " + (current?.Phase ?? "folder and account preparation") +
                      ": " + reason + " No file actions were started. Diagnostic detail: " + SyncActivityText.Failure(error),
                identityCollision?.RelativePath ?? current?.RelativePath,
                identityCollision is not null
                    ? "Use Repair copied file mappings on First sync. It verifies completed history and both current files, preserves both files, and refuses unresolved effects. Repeating Scan Again cannot repair this retained mapping."
                    : stopped
                        ? "Open this run's operation log to inspect its last completed progress, then use Scan Again to retry. Your reviewed file choices have not been applied."
                        : "Check the folders and account connection, then use Scan Again. Your reviewed file choices have not been applied.");
            await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                JsonSerializer.Serialize(new { pairId = pair.Id, firstSyncScanComplete = false,
                    firstSyncProblem = failure }), cancellationToken: CancellationToken.None);
            await AppendFirstSyncProblemEventAsync(pair, commandId, stopped ? "Warning" : "Error",
                failure.Message + " " + failure.NextAction, stopped ? "First sync scan stopped" : "First sync scan failed");
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Blocked", null, null,
                failure.Message));
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    internal static string DescribeFirstSyncScanInterruption(SyncComparisonProgress? current, bool explicitlyStopped) =>
        SyncActivityText.Safe("The first-sync folder scan " + (explicitlyStopped ? "was stopped by request" :
            "was cancelled or reached its configured time limit") + " during " +
            (current?.Phase ?? "folder and account preparation") + ". " +
            (current is null ? "" : "Last reported work: " + SyncProgressDescription.Describe(current) + ". ") +
            "No file actions were started.");

    // The caller owns the run lease. Reviewed first sync uses this same scan without releasing its command
    // identity or admitting a competing job between the effects, final observation and approval.
    private async Task RefreshBootstrapInventoryCoreAsync(SyncPair pair, CancellationToken cancellationToken,
        Func<SyncAuthorityBatchProgress, Task>? onProgress)
    {
        await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null, null, null));
        var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress);
        await CreateObserver(pair, prepared, progress => NotifyProgressAsync(onProgress,
                new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: progress)))
            .ObserveAsync("manual", cancellationToken);
        if (await _authority.LoadPairStateAsync(pair.Id, cancellationToken) == "discovery_required")
        {
            var adopted = await CreateInitialPlanner(prepared).AdoptAsync(pair.Id, cancellationToken,
                progress => NotifyProgressAsync(onProgress,
                    new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: progress)));
            if (!adopted.Any(item => item.Kind == SyncDecisionKind.Blocked))
                await _authority.SetPairStateAsync(pair.Id, "active", cancellationToken);
        }
        else if (pair.InitialReviewApproved == false)
        {
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null,
                null, "Comparing the retained inventory records to identify first-sync conflicts; no file transfers or deletions are running."));
            var review = await _authority.LoadBootstrapEvidenceAsync(pair.Id,
                cancellationToken: cancellationToken);
            var conflicts = (review?.Items ?? []).Where(item => item.Group is
                    "Local newer" or "Dropbox newer" or "Approximately equal time" or "Unreliable or missing time")
                .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (conflicts.Count > 0)
            {
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null,
                    null, $"Retaining {conflicts.Count:N0} first-sync conflicts for explicit resolution; neither file version is changed."));
                await CreateInitialPlanner(prepared).RetainReviewConflictsAsync(pair.Id, conflicts, cancellationToken,
                    progress => NotifyProgressAsync(onProgress,
                        new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: progress)));
            }
        }
    }

    public async Task<SyncPair> ApproveBootstrapMergeAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        await _authority.EnsureFirstSyncCanFinalizeAsync(pairId, cancellationToken);
        var pairSet = await LoadPairsAsync(cancellationToken);
        var pair = pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId,
                       StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (!await _authority.HasCompleteObservationAsync(pairId, "local", cancellationToken) ||
            !await _authority.HasCompleteObservationAsync(pairId, "dropbox", cancellationToken))
            throw new InvalidOperationException("Both complete first-run inventories must finish before approval.");
        // CC4B-3: an explicit cloud/cloud pair is approved from its own typed cloud observations under the
        // retained compatibility tags, and the approval writes NO local-root authority: its new revision is
        // retained by RegisterPairPolicyAsync, which takes the cloud branch for exactly this pair shape.
        if (SyncCloudPairConfiguration.IsCloudOnly(pair)) RequireCloudOnlyProjection(pair);
        if (pair.InitialReviewApproved != false) return pair;
        var updated = pair with
        {
            InitialReviewApproved = true,
            PolicyRevision = checked(pair.PolicyRevision + 1)
        };
        var approveSelected = string.Equals(pairSet.SelectedPairId, pairId, StringComparison.Ordinal);
        var approval = await RegisterPairPolicyAsync(updated, cancellationToken, select: approveSelected);
        if (!approval.DefinitionCommitted)
            await _authority.RetainPairDefinitionAsync(updated, approveSelected, cancellationToken);
        return updated;
    }

    public Task<SyncBootstrapActionResult> RunBootstrapActionPlanAsync(
        SyncBootstrapActionPlan plan,
        Func<SyncAuthorityLiveAction, Task>? onAction = null,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, string? retryOfRunId = null,
        long? expectedPolicyRevision = null, Action<string>? onPreparationProgress = null)
        => RunBootstrapActionPlanCoreAsync(plan, onAction, cancellationToken, onProgress,
            retryOfRunId, expectedPolicyRevision, onPreparationProgress);

    private async Task<SyncBootstrapActionResult> RunBootstrapActionPlanCoreAsync(
        SyncBootstrapActionPlan plan,
        Func<SyncAuthorityLiveAction, Task>? onAction = null,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, string? retryOfRunId = null,
        long? expectedPolicyRevision = null, Action<string>? onPreparationProgress = null,
        SyncRunRetryPlan? preparationPreview = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        stateAuthority.VerifyAuthority();
        if (string.IsNullOrWhiteSpace(plan.PairId))
            throw new InvalidDataException("The requested sync pair is not retained.");
        // r28: reserve and publish the run gate BEFORE the settings load, the pair read and the choice
        // normalization, so a Stop during a large selection is observed immediately rather than after it.
        // An invalid pair still creates no durable command: only the temporary preparing control is disposed.
        using var pendingRun = await BeginPendingRunAsync(cancellationToken, "run_pair", plan.PairId);
        var admissionToken = pendingRun.Token;
        // First sync is also a standalone CLI entry point: do not depend on the
        // settings page or an ordinary sync having initialized the limiter.
        LoadTransferSettings();
        var pair = (await LoadPairsAsync(admissionToken)).Pairs.SingleOrDefault(item =>
                       string.Equals(item.Id, plan.PairId, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (expectedPolicyRevision is { } expected && pair.PolicyRevision != expected)
            throw new InvalidOperationException("The pair settings changed after the retained retry approval. Review the current settings first.");
        if (pair.InitialReviewApproved != false)
            throw new InvalidOperationException("First sync has already been reviewed for this pair.");
        // Reject a known pause before normalizing a large review or performing the
        // effect-admission health scan. The same guard still runs under the acquired
        // execution lock below, so a pause arriving after this preflight also wins.
        if (NewSyncAdmissionReason(pair) is { } preflightAdmissionReason)
            throw new InvalidOperationException(preflightAdmissionReason);
        if (string.IsNullOrWhiteSpace(plan.LocalScanGenerationId) ||
            string.IsNullOrWhiteSpace(plan.DropboxScanGenerationId))
            throw new InvalidDataException("Scan both folders before starting first sync.");

        // r28: normalize through a cancellation-aware loop so a Stop during a large selection is observed
        // rather than only after the whole array has been normalized.
        var normalizedChoices = new SyncBootstrapActionChoice[plan.Choices.Count];
        for (var choiceIndex = 0; choiceIndex < plan.Choices.Count; choiceIndex++)
        {
            admissionToken.ThrowIfCancellationRequested();
            var choice = plan.Choices[choiceIndex];
            normalizedChoices[choiceIndex] = choice with
            {
                RelativePath = PilotPath.NormalizeRelative(choice.RelativePath),
                ExpectedGroup = choice.ExpectedGroup.Trim(),
                Action = choice.Action.Trim()
            };
        }
        if (normalizedChoices.Select(item => item.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedChoices.Length)
            throw new InvalidDataException("The first-sync plan contains a duplicate path choice.");
        // Retry omits previously completed files from effects by using the existing
        // Skip action. That omission is not a new user decision to defer those files.
        // Derive fulfilled consent only from the exact retained original approval
        // and canonical retry evidence, never from an arbitrary caller's skip list.
        var completedRetryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The exact original terminal approval stays retained once. A retry keeps
        // only a compact reference to it instead of repeating the whole plan.
        SyncAuthorityRunHistory? originalRetryRun = null;
        SyncBootstrapActionPlan? originalRetryPlan = null;
        if (retryOfRunId is not null)
        {
            var (retainedOrigin, original, revision) = await ReadRetryOriginAsync(retryOfRunId, admissionToken,
                null, false, normalizedChoices.Where(item => !string.Equals(item.Action, "Skip for now",
                    StringComparison.Ordinal)).Select(item => item.RelativePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            originalRetryRun = retainedOrigin;
            originalRetryPlan = original;
            if (original is null || original.PairId != pair.Id || revision != pair.PolicyRevision ||
                original.LocalScanGenerationId != plan.LocalScanGenerationId ||
                original.DropboxScanGenerationId != plan.DropboxScanGenerationId ||
                original.Choices.Count != normalizedChoices.Length)
                throw new InvalidDataException("The retry must retain the exact original pair, policy and two-sided review.");
            var originalChoices = original.Choices.ToDictionary(item => PilotPath.NormalizeRelative(item.RelativePath),
                StringComparer.OrdinalIgnoreCase);
            // RetryRunAsync has just read this exact preview to select its paths.
            // Reuse that tentative evidence only within this private invocation;
            // public callers cannot supply it. Original consent is reread above,
            // and HandoffSelectedRetryAsync still independently reloads current
            // eligibility and exact operation IDs AFTER acquiring execution.
            var retryEvidence = preparationPreview ?? await LoadRunRetryAsync(retryOfRunId,
                admissionToken, pair.Id, onPreparationProgress);
            if (retryEvidence.RunId != retryOfRunId || retryEvidence.PairId != pair.Id ||
                retryEvidence.UnavailableReason is not null || retryEvidence.PolicyRevision != pair.PolicyRevision)
                throw new InvalidOperationException(retryEvidence.UnavailableReason ?? "The retained retry policy changed.");
            var retryItems = retryEvidence.Items.ToDictionary(item => PilotPath.NormalizeRelative(item.Path),
                StringComparer.OrdinalIgnoreCase);
            foreach (var choice in normalizedChoices)
            {
                if (!originalChoices.TryGetValue(choice.RelativePath, out var approved) ||
                    choice.ExpectedGroup != approved.ExpectedGroup || choice.Move != approved.Move ||
                    (choice.Action != "Skip for now" && choice.Action != approved.Action))
                    throw new InvalidDataException("A retry cannot add or change an originally reviewed file action.");
                retryItems.TryGetValue(choice.RelativePath, out var evidence);
                if (choice.Action != "Skip for now")
                {
                    if (evidence?.CanRetry != true)
                        throw new InvalidDataException("Completed, conflicting or uncertain files cannot be replayed by retry.");
                }
                else if (approved.Action != "Skip for now" && evidence?.State == "Completed")
                    completedRetryPaths.Add(choice.RelativePath);
            }
        }

        var skippedPaths = normalizedChoices.Where(item => item.Action == "Skip for now")
            .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        skippedPaths.ExceptWith(completedRetryPaths);

        onPreparationProgress?.Invoke("Checking schema, installation identity and reviewed command preconditions before accepting the sync; no file transfer has started.");
        // One real production helper writes the accepted reviewed payload: the
        // original approval keeps its complete inline plan, and every retry retains
        // only the compact reference to that original consent plus its own filter.
        // A NEW reviewed run normalizes its exact original selection once, in bounded non-authorizing chunks
        // plus one atomic seal, and then retains only the consent identity, the exact UTF-8 fingerprint of the
        // approved original request and the small existing summary fields in its command payload, so no whole
        // Choices array is repeated per command or per retry. A retry keeps its compact reference to the same
        // exact terminal original approval; the inline retained payload remains the legacy representation.
        SyncReviewedConsentReference? reviewedConsent = null;
        string? rootConsentId = null;
        string commandPayload;
        if (retryOfRunId is null)
        {
            var originalRequestJson = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
                plan with { Choices = normalizedChoices }, "Reviewed first sync", pair.PolicyRevision, null, null);
            var originalRequestSha256 =
                SyncReviewedConsentReference.LowercaseRequestSha256(originalRequestJson);
            rootConsentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(pendingRun.CommandId);
            var preparedChoices = new SyncAuthorityConsentPreparedChoice[normalizedChoices.Length];
            for (var preparedIndex = 0; preparedIndex < normalizedChoices.Length; preparedIndex++)
            {
                admissionToken.ThrowIfCancellationRequested();
                preparedChoices[preparedIndex] =
                    SyncAuthorityConsentEncoding.Prepare(normalizedChoices[preparedIndex]);
            }
            await PrepareReviewedOriginalConsentAsync(rootConsentId, pendingRun.CommandId, pair,
                plan with { Choices = normalizedChoices }, originalRequestJson, originalRequestSha256,
                preparedChoices, admissionToken, onPreparationProgress);
            commandPayload = SyncReviewedConsentReference.BuildConsentBackedRunRequestJson(pair,
                plan with { Choices = normalizedChoices }, "Reviewed first sync", pair.PolicyRevision, null,
                rootConsentId, originalRequestSha256);
        }
        else
        {
            var originalConsent = await _authority.InspectConsentBindingForCommandAsync(retryOfRunId,
                admissionToken);
            reviewedConsent = SyncReviewedConsentReference.Create(originalRetryRun!, originalRetryPlan!,
                normalizedChoices, originalConsent?.ConsentId);
            commandPayload = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
                plan with { Choices = normalizedChoices }, "Reviewed first sync", pair.PolicyRevision, retryOfRunId,
                reviewedConsent);
        }
        var commandId = await CreateAcceptedCommandAsync("run_pair", pair.Id, null, commandPayload,
            admissionToken, admissionProgress: onPreparationProgress,
            reservedCommandId: pendingRun.CommandId, reservation: pendingRun.Reservation, consentId: rootConsentId);
        var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        PilotRunControl? runControl = null;
        SyncRunResult? result = null;
        // R473-1/R473-3: the reviewed action count and the final remaining review are produced inside the guarded
        // region and consumed by the terminal command result, so they are declared here.
        var reviewedActionCount = 0;
        IReadOnlyList<SyncBootstrapReviewItem> remaining = [];
        // R474-3: the actual product stage and the known completed-effect count decide the truthful failure text.
        var effectStage = "preparation";
        var completedEffects = 0;
        string? activePath = null;
        var terminalCommitted = false;
        try
        {
            runControl = pendingRun.EnterExecution();
            await using var databaseConnections = _authority.BeginConnectionReuseScope();
            var currentPair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item => item.Id == pair.Id);
            if (currentPair.PolicyRevision != pair.PolicyRevision || currentPair.InitialReviewApproved != false)
                throw new InvalidOperationException("The first-sync approval or pair settings changed before execution acquired its lock.");
            pair = currentPair;
            if (NewSyncAdmissionReason(pair) is { } admissionReason)
                throw new InvalidOperationException(admissionReason);
            using var reviewedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                runControl.StopToken);
            cancellationToken = reviewedCancellation.Token;
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            // Admission is part of this retained run, under its execution lock.
            // A refusal must be visible in Activity/Problems, not only a footer.
            await _authority.EnsureReviewedRunCanStartAsync(pair.Id, cancellationToken, onPreparationProgress);
            // Retry-owned handoff: under the acquired execution lock and before the fresh planner plans them,
            // supersede ONLY the exact original operations this retry selected (validated ids, never by path).
            if (retryOfRunId is not null)
                await HandoffSelectedRetryAsync(retryOfRunId, commandId, pair,
                    normalizedChoices.Where(item => item.Action != "Skip for now")
                        .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    cancellationToken, completedRetryPaths);
            // Reviewed consent already binds this policy and both saved inventories.
            // Remapping selective-sync rules here recursively scans both roots and can
            // change the policy AFTER its locked check. Preserve the reviewed rules;
            // the runner still verifies each selected source, destination and root.
            // Ordinary unreviewed runs retain their existing remapping path.
            onPreparationProgress?.Invoke("Keeping the reviewed selection rules unchanged; preparing the bound endpoints before validating the approved file actions. No whole-folder selection scan is running.");
            // ENG3 (R473-2): the host reports and retains its own stage before the preparation await, so a direct
            // caller with no presentation callback still sees and keeps an accurate preparing/checking stage.
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null, null, null));
            var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress,
                forceReviewedDeletionEffects: normalizedChoices.Any(choice => choice.Action == "DeleteLocal"));
            effectStage = "validation";
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Running", null, null, null));
            // Validate consent against the immutable inventories the user SAW,
            // not a new global scan ID. The runner then re-observes each selected
            // path and fences its operation kind. Unrelated edits cannot abort
            // the entire review; newly appeared paths are not silently approved.
            onPreparationProgress?.Invoke("Validating every retained choice against the exact reviewed local and cloud inventories in one database query; no file transfer has started.");
            var reviewedInventory = (await _authority.LoadBootstrapEvidenceAsync(pair.Id,
                cancellationToken: cancellationToken, reviewedLocalScanId: plan.LocalScanGenerationId,
                reviewedRemoteScanId: plan.DropboxScanGenerationId)
                ?? throw new InvalidDataException("The reviewed inventory is unavailable. Scan again.")).Items;
            reviewedInventory = await AddBootstrapDeletionEvidenceAsync(pair, reviewedInventory, prepared.Remote,
                cancellationToken);
            onPreparationProgress?.Invoke($"Loaded {reviewedInventory.Count:N0} reviewed inventory rows; validating each approved action and direction before dispatch.");
            var actionable = reviewedInventory.Where(item => SyncBootstrapActions.IsActionable(item.Group))
                .ToDictionary(item => PilotPath.NormalizeRelative(item.RelativePath),
                    StringComparer.OrdinalIgnoreCase);
            // The database-bound terminal layout accounts for retired original
            // positions. Surviving choices must still match the original snapshot;
            // a caller cannot remove arbitrary choices or add newly appeared files.
            var terminalLayout = originalRetryPlan?.ConsentLayout;
            var expiredOriginalChoices = terminalLayout?.Ranges.Any(range => range.State == "expired") == true;
            var reviewedChoiceCount = terminalLayout?.OriginalCount ?? normalizedChoices.Length;
            if (actionable.Count != reviewedChoiceCount ||
                terminalLayout is not null && normalizedChoices.Length + terminalLayout.RetiredCount != reviewedChoiceCount ||
                normalizedChoices.Any(choice => !actionable.ContainsKey(choice.RelativePath)))
                throw new InvalidDataException(
                    "The choices must cover every upload and download in the reviewed snapshot. Load all recommendations or finish reviewing the remaining items.");

            if (terminalLayout is { RetiredCount: > 0 } && !expiredOriginalChoices)
            {
                // Only terminal completion is known for these old paths. Use the
                // immutable reviewed inventory for current-presence verification,
                // never recreate a choice body or authorize another file effect.
                var survivingPaths = normalizedChoices.Select(choice => choice.RelativePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                completedRetryPaths.UnionWith(actionable.Keys.Where(path => !survivingPaths.Contains(path)));
            }

            var expectedKinds = new Dictionary<string, SyncAuthorityOperationKind>(StringComparer.OrdinalIgnoreCase);
            var expectedMoves = new Dictionary<string, SyncBootstrapMoveReview>(StringComparer.OrdinalIgnoreCase);
            var refusedMoveChoices = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in normalizedChoices)
            {
                var item = actionable[choice.RelativePath];
                if (!string.Equals(item.Group, choice.ExpectedGroup, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"{choice.RelativePath} changed sides after review. Scan again before starting first sync.");
                var expectedAction = SyncBootstrapActions.TransferAction(item.Group);
                if (SyncBootstrapActions.IsMove(choice.Action))
                {
                    if (item.Move is null || choice.Move != item.Move || choice.Action != item.Move.Action)
                        throw new InvalidDataException(
                            $"The move approval for {choice.RelativePath} does not match the reviewed identity and old path. Review it again.");
                    expectedKinds[choice.RelativePath] = item.Move.Kind;
                    expectedMoves[choice.RelativePath] = item.Move;
                    continue;
                }
                // Retained copy approvals keep their original meaning. If the current planner
                // finds a move instead, it reports that path as needing explicit move review.
                if (!string.Equals(choice.Action, expectedAction, StringComparison.Ordinal) &&
                    !string.Equals(choice.Action, "Skip for now", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"{choice.Action} is not a valid first-sync action for {choice.RelativePath}.");
                if (!string.Equals(choice.Action, "Skip for now", StringComparison.Ordinal))
                {
                    expectedKinds[choice.RelativePath] = SyncBootstrapActions.TransferKind(item.Group);
                    if (item.Move is not null)
                        refusedMoveChoices[choice.RelativePath] = new SyncDecision(choice.RelativePath,
                            SyncDecisionKind.Blocked,
                            $"The reviewed item proposes moving '{item.Move.FromPath}' to '{choice.RelativePath}'. " +
                            "A copy choice is not one of this review's offered actions. Choose the explicit move or skip this file; no effect was attempted.");
                }
            }
            // Newest wins: the review showed "Keep newer" for these two-file conflicts. Approval applies the
            // planner's own decision, so the reviewed-kind fence admits exactly that replacement. Ties and
            // missing times are not admitted and stay open conflicts, as the review said.
            foreach (var item in reviewedInventory)
                if (item.Group is "Local newer" or "Dropbox newer" or "Approximately equal time" or
                        "Unreliable or missing time" && item.ObjectKind == "file" &&
                    item.LocalSize is not null && item.DropboxSize is not null &&
                    SyncBootstrapActions.NewerPort(item.LocalModifiedUtc, item.DropboxModifiedUtc) is { } newer)
                    expectedKinds[PilotPath.NormalizeRelative(item.RelativePath)] = newer == "local"
                        ? SyncAuthorityOperationKind.UpdateRemote : SyncAuthorityOperationKind.UpdateLocal;

            if (expectedKinds.Count == refusedMoveChoices.Count)
            {
                result = new SyncRunResult(pair.Id, []);
            }
            else
            {
                var operationGroupId = "bootstrap-" + Guid.NewGuid().ToString("N");
                await _authority.RegisterOperationGroupAsync(operationGroupId, "compound",
                    JsonSerializer.Serialize(new
                    {
                        purpose = "reviewed_first_sync",
                        runId = commandId,
                        pairId = pair.Id,
                        consentId = rootConsentId ?? reviewedConsent?.ConsentId,
                        reviewedPathCount = expectedKinds.Count
                    }), cancellationToken);
                effectStage = "dispatch";
                var reviewedSink = LiveActionProgress(pair, onProgress, onAction);
                Func<SyncAuthorityLiveAction, Task> countingSink = async action =>
                {
                    effectStage = "dispatch";
                    activePath = action.RelativePath;
                    if (string.Equals(action.Stage, "Completed", StringComparison.Ordinal))
                    {
                        completedEffects++;
                    }
                    await reviewedSink(action);
                };
                result = await CreatePairRunner(pair, prepared, runControl, commandAdmission, prepared.CloudFiles,
                        expectedOperationKinds: expectedKinds,
                        // R473-2: this sink is always attached, so the authoritative host retains the real
                        // inventory stages even when no caller subscribes to progress.
                        onInventory: progress =>
                        {
                            effectStage = progress.Phase;
                            activePath = progress.RelativePath;
                            return NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                                "Checking", null, null, null, Comparison: progress));
                        }, onProgress: onProgress, expectedMoveReviews: expectedMoves)
                    .RunReviewedAsync("Reviewed first sync", expectedKinds.Keys.Where(path => !refusedMoveChoices.ContainsKey(path)).ToHashSet(
                            StringComparer.OrdinalIgnoreCase), operationGroupId,
                        _ => Task.CompletedTask, cancellationToken, countingSink,
                        continueAfterBlockedPaths: true,
                        reviewedLocalScanId: plan.LocalScanGenerationId,
                        reviewedRemoteScanId: plan.DropboxScanGenerationId,
                        allowIndependentDirectoryCreates: true);
                effectStage = "Confirming reviewed transfer records";
                await FinalizeReviewedOperationGroupAsync(operationGroupId, cancellationToken,
                    step => NotifyProgressAsync(onProgress,
                        new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: step)));
            }
            if (refusedMoveChoices.Count > 0)
                result = result with { Decisions = result.Decisions.Concat(refusedMoveChoices.Values)
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray() };
            reviewedActionCount = expectedKinds.Count;
            // Retained completion prevents replay; it is not current-file proof.
            // Only at the approval boundary, use the existing two-endpoint observer
            // to verify fulfilled retry paths. This does not dispatch operations,
            // re-approve unrelated arrivals or require unchanged byte versions:
            // ordinary sync retains responsibility for later edits.
            var unverifiedCompletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (completedRetryPaths.Count > 0 && skippedPaths.Count == 0 && result.Blocked == 0 &&
                result.Conflicts == 0 && result.NeedsVerification == 0 && !result.Stopped &&
                expectedKinds.All(item => ReviewedActionFinished(result, item.Key, item.Value)))
            {
                effectStage = "validation";
                onPreparationProgress?.Invoke($"Verifying both current endpoints before completing {completedRetryPaths.Count:N0} previously completed reviewed actions. No completed transfer will be replayed; later edits remain ordinary sync work.");
                var observed = await CreateObserver(pair, prepared, progress => NotifyProgressAsync(onProgress,
                    new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: progress)))
                    .ObserveAsync("manual", cancellationToken);
                var current = await _authority.LoadPlanningObservationSetAsync(pair.Id,
                    prepared.Options.ConfigurationRevision, prepared.LocalRootBindingId,
                    prepared.DropboxRootBindingId, observed.LocalScan.ScanGenerationId,
                    observed.DropboxScan.ScanGenerationId, cancellationToken);
                var left = current.Local.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
                var right = current.Dropbox.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
                var uncertainPaths = current.LocalUncertainPaths.Concat(current.DropboxUncertainPaths)
                    .Concat(current.ExcludedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var verificationFailures = new List<SyncDecision>();
                var verificationStarted = DateTimeOffset.UtcNow;
                var checkedPaths = 0;
                foreach (var path in completedRetryPaths.Order(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var uncertain = uncertainPaths.Any(parent => SyncDirectoryStoragePolicy.Contains(parent, path));
                    var removed = actionable[path].Group == SyncBootstrapActions.RemoteDeletedGroup;
                    var valid = !uncertain && (removed
                        ? !left.ContainsKey(path) && !right.ContainsKey(path)
                        : left.TryGetValue(path, out var local) &&
                          right.TryGetValue(path, out var remote) && local.ObjectId == remote.ObjectId &&
                          local.ObjectKind == actionable[path].ObjectKind && remote.ObjectKind == local.ObjectKind);
                    var detail = valid
                        ? removed ? "The previously completed removal remains absent on both endpoints; its Recovery archive is retained."
                            : "The previously completed reviewed item is currently present on both bound endpoints. Its transfer is not replayed; later version changes remain ordinary sync work."
                        : "First-sync approval remains unsaved: this previously completed item is now missing, excluded, unreadable or has a different object identity/type on an endpoint. Its completed effect and failure history are retained. Inspect this path, restore or review its current state, then retry approval; no transfer was replayed.";
                    if (!valid)
                    {
                        unverifiedCompletedPaths.Add(path);
                        verificationFailures.Add(new(path, SyncDecisionKind.Blocked, detail));
                    }
                    await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null,
                        null, null, Comparison: new SyncComparisonProgress("Verifying Completed Reviewed Item", path,
                            ++checkedPaths, completedRetryPaths.Count, verificationStarted, DateTimeOffset.UtcNow,
                            detail, "completed items checked")));
                }
                if (verificationFailures.Count > 0)
                    result = result with { Decisions = result.Decisions.Concat(verificationFailures).ToArray() };
            }
            // Finish the finite set that was actually approved, not all future changes in the live folder.
            // In particular, Skip for now never becomes permission for ordinary sync to copy that path.
            remaining = actionable.Values.Where(item => skippedPaths.Contains(item.RelativePath) ||
                    unverifiedCompletedPaths.Contains(item.RelativePath) ||
                    expectedKinds.TryGetValue(item.RelativePath, out var kind) &&
                    !ReviewedActionFinished(result, item.RelativePath, kind))
                .Select(item => new SyncBootstrapReviewItem(pair.Id, item.RelativePath, item.Group,
                    item.ObjectKind, BootstrapFileType(item.RelativePath, item.ObjectKind),
                    normalizedChoices.FirstOrDefault(choice => string.Equals(choice.RelativePath, item.RelativePath,
                        StringComparison.OrdinalIgnoreCase))?.Action ?? "Verify first",
                    "Nothing changes", item.LocalSize, item.DropboxSize, item.LocalModifiedUtc,
                    item.DropboxModifiedUtc, item.Move)).ToArray();
            var firstSyncComplete = !expiredOriginalChoices && skippedPaths.Count == 0 && result.Blocked == 0 && result.Conflicts == 0 &&
                                    result.NeedsVerification == 0 && !result.Stopped &&
                                    expectedKinds.All(item => ReviewedActionFinished(result, item.Key, item.Value));
            // ENG4/R474-3: the retained command result carries bounded typed presentation data for both terminal
            // outcomes, so Needs Attention, First sync, Activity, Runs and the CLI all name the same exact state, the
            // same exact run and the same affected path.
            var retainedProblem = firstSyncComplete
                ? null
                : expiredOriginalChoices
                    ? FirstSyncProblemJson(pair, commandId, "first_sync_needs_attention", "approval",
                        "Some original choices have expired. Retained selected work may finish, but expired choices cannot approve the whole pair.",
                        null, "Scan again and review a new selection before completing first sync.")
                    : IncompleteFirstSyncProblem(pair, commandId, result, remaining);
            effectStage = "finalization";
            var terminalJson = RetainedFirstSyncResultJson(pair, result, reviewedActionCount,
                skippedPaths.Count, firstSyncComplete, retainedProblem);
            if (firstSyncComplete)
            {
                effectStage = "approval";
                cancellationToken.ThrowIfCancellationRequested();
                var approvalStarted = DateTimeOffset.UtcNow;
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null,
                    null, null, Comparison: new SyncComparisonProgress("Saving First Sync Approval", pair.Name,
                        0, 2, approvalStarted, DateTimeOffset.UtcNow,
                        "Revalidating the bound local roots and provider account, and retaining this pair's current policy before approval. No file transfer is running.",
                        "approval steps completed")));
                // Revalidate the current native roots/account using the original revision. Approval changes
                // only its existing flag/revision; it neither rebinds endpoints nor adds local cloud-only authority.
                await RegisterPairPolicyAsync(pair, cancellationToken);
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null,
                    null, null, Comparison: new SyncComparisonProgress("Saving First Sync Approval", pair.Name,
                        1, 2, approvalStarted, DateTimeOffset.UtcNow,
                        "Committing the reviewed pair's approval and exact retained command outcome in one guarded database transaction. Checking the original inventories and unresolved work before commit; no file transfer is running.",
                        "approval steps completed")));
                await _authority.FinalizeReviewedFirstSyncAsync(pair, commandId,
                    plan.LocalScanGenerationId, plan.DropboxScanGenerationId, terminalJson);
                pair = pair with { InitialReviewApproved = true, PolicyRevision = checked(pair.PolicyRevision + 1) };
            }
            else
                await _authority.TransitionCommandRequestAsync(commandId, "completed", terminalJson,
                    cancellationToken: CancellationToken.None);
            terminalCommitted = true;
            if (retainedProblem is null)
                await AppendFirstSyncProblemEventAsync(pair, commandId, "Success",
                    "The reviewed first-sync actions completed and approval is saved. Normal sync checks later changes; " +
                    "unresolved conflicts remain separate. Earlier run history is unchanged.");
            else
                await AppendFirstSyncProblemEventAsync(pair, commandId, "Warning",
                    retainedProblem.Message + " " + retainedProblem.NextAction);
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                firstSyncComplete ? "Completed" : "Blocked", result, null,
                firstSyncComplete
                    ? null
                    : retainedProblem!.Message + " " + retainedProblem.NextAction));
            return new SyncBootstrapActionResult(pair, result, result.AppliedItems,
                skippedPaths.Count,
                firstSyncComplete);
        }
        catch (Exception error)
        {
            if (terminalCommitted) throw;
            var failure = FirstSyncFailureProblem(pair, commandId, effectStage, completedEffects, activePath,
                error);
            if (result is null)
                await TryBlockCommandAsync(commandId, "reviewed_first_sync_failed", failure);
            else
                await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                    RetainedFirstSyncResultJson(pair, result, reviewedActionCount,
                        skippedPaths.Count, false, failure,
                        "reviewed_first_sync_failed"), cancellationToken: CancellationToken.None);
            await AppendFirstSyncProblemEventAsync(pair, commandId, "Error", failure.Message + " " + failure.NextAction);
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    private static string RetainedFirstSyncResultJson(SyncPair pair, SyncRunResult result,
        int reviewed, int skipped, bool firstSyncComplete, SyncFirstSyncProblem? problem, string? reason = null) =>
        JsonSerializer.Serialize(new
        {
            reason, reviewed, skipped, pairId = pair.Id, firstSyncComplete, firstSyncProblem = problem,
            reviewedWorkComplete = firstSyncComplete,
            result.Uploaded, result.Downloaded, result.Blocked, result.Conflicts, result.Stopped,
            failed = result.Failed, notAttempted = result.NotAttempted,
            needsVerification = result.NeedsVerification, detailsSource = "retained_history_and_authority",
            pairs = RetainedRunSummaries([new SyncAuthorityBatchItemResult(pair, result, null, null)])
        });

    private static bool ReviewedActionFinished(SyncRunResult result, string path, SyncAuthorityOperationKind kind)
    {
        var decision = result.Decisions.SingleOrDefault(item => string.Equals(item.RelativePath, path,
            StringComparison.OrdinalIgnoreCase));
        if (decision is null || decision.NotAttempted) return false;
        // The real reviewed planner's Unchanged/Adopted dispositions carry the already-satisfied
        // complete-observation authority; missing/Skipped/Blocked dispositions never count as success.
        if (decision.Kind is SyncDecisionKind.Identical or SyncDecisionKind.Baseline) return true;
        return kind switch
        {
            SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote =>
                decision.Kind == SyncDecisionKind.Uploaded,
            SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal =>
                decision.Kind == SyncDecisionKind.Downloaded,
            SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote =>
                decision.Kind == SyncDecisionKind.Recovered,
            SyncAuthorityOperationKind.DeleteLocal => decision.Kind == SyncDecisionKind.Archived,
            _ => false
        };
    }

    // ENG3: one composed live-action sink. The retained progress stream is fed first so status and jobs see the
    // real stage even when no UI callback is attached, and the caller's own presentation is unchanged.
    private Func<SyncAuthorityLiveAction, Task> LiveActionProgress(SyncPair pair,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, Func<SyncAuthorityLiveAction, Task>? onAction) =>
        // Both optional presentation callbacks share the ordered delivery boundary;
        // neither can overtake a preceding event while its durable write finishes.
        action => NotifyProgressAsync(onProgress,
            new SyncAuthorityBatchProgress(pair, "Running", null, null, null, action),
            onAction is null ? null : () => onAction(action));

    private static string BootstrapFileType(string relativePath, string objectKind)
    {
        if (string.Equals(objectKind, "directory", StringComparison.OrdinalIgnoreCase)) return "Folder";
        var extension = Path.GetExtension(relativePath);
        return string.IsNullOrWhiteSpace(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant();
    }

    // The retained conflict id and generation and each side's exact retained version identity are the only
    // authority this read accepts. The two trailing members are additive: a caller that does not bind the
    // exact retained conflict keeps the existing current-conflict-for-path resolution unchanged.
    public async Task<SyncConflictContent> LoadConflictContentAsync(string pairId, string relativePath,
        long maximumContentBytes = 8L * 1024 * 1024, CancellationToken cancellationToken = default,
        string? conflictId = null, long? conflictGeneration = null)
    {
        stateAuthority.VerifyAuthority();
        if (maximumContentBytes is < 1 or > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumContentBytes));
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item =>
            string.Equals(item.Id, pairId, StringComparison.Ordinal));
        // One endpoint resolution and revalidation, shared by the read and by the context the caller
        // receives. A document-less legacy pair resolves as left-local and right-cloud; an explicit
        // symmetric pair resolves through its own typed records, and a malformed document is refused
        // here rather than quietly read as the legacy fields or through a currently selected account.
        var endpoints = SyncEndpointContext.Capture(pair);
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
        {
            var projection = SyncCloudPairConfiguration.ValidateProjection(pair);
            if (!projection.IsValid) throw new SyncEndpointRuntimeRefusedException(projection);
        }
        var normalizedPath = PilotPath.NormalizeRelative(relativePath);
        var conflict = conflictId is null
            ? await _authority.LoadCurrentConflictForPathAsync(pairId, normalizedPath, cancellationToken)
              ?? throw new InvalidOperationException("The selected conflict is no longer open.")
            : await LoadBoundConflictAsync(pairId, normalizedPath, conflictId, conflictGeneration,
                cancellationToken);
        var read = await ReadConflictContentAsync(pair, endpoints, conflict, maximumContentBytes,
            cancellationToken);
        return new SyncConflictContent(conflict.ConflictId, conflict.PairId, conflict.LocalPath,
            read.LocalSize, read.DropboxSize, read.LocalHash, read.DropboxHash, read.LocalModifiedUtc,
            read.DropboxModifiedUtc, read.LocalBytes, read.DropboxBytes, read.Note, endpoints);
    }

    /// <summary>
    /// The exact retained conflict a caller bound by id and generation. A conflict that belongs to another
    /// pair or path, is no longer open, or has advanced past the caller's generation is refused rather than
    /// answered with the current conflict's versions.
    /// </summary>
    private async Task<SyncAuthorityConflictSnapshot> LoadBoundConflictAsync(string pairId, string relativePath,
        string conflictId, long? generation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictId);
        var conflict = await _authority.LoadConflictAsync(conflictId, cancellationToken);
        if (!string.Equals(conflict.PairId, pairId, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected conflict belongs to a different folder pair.");
        if (conflict.Status is not ("open" or "resolution_requested" or "resolving"))
            throw new InvalidOperationException("The selected conflict is no longer open.");
        if (!string.Equals(PilotPath.NormalizeRelative(conflict.LocalPath), relativePath, StringComparison.Ordinal) ||
            !string.Equals(PilotPath.NormalizeRelative(conflict.DropboxPath), relativePath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected conflict is not the retained conflict for this path.");
        if (generation is { } expected && conflict.ConflictGeneration != expected)
            throw new InvalidOperationException(
                "The conflict advanced after it was selected; reload the conflict before previewing its content.");
        return conflict;
    }

    /// <summary>
    /// The one conflict-content read. The pair's own resolved endpoint layout selects where each retained
    /// side reads its bytes from, so a cloud-first layout, a reversed local layout, a local-to-local pair and
    /// a two-cloud pair each read the side they actually retain instead of assuming one legacy local root
    /// plus one Dropbox folder. Each side is read once, bounded by its reviewed length, and a side whose
    /// exact retained version can no longer be verified leaves both sides unread with an explicit note; a
    /// newer, opposite-side or differently-accounted version is never returned in its place.
    /// </summary>
    private async Task<SyncConflictContentRead> ReadConflictContentAsync(SyncPair pair,
        SyncEndpointContext endpoints, SyncAuthorityConflictSnapshot conflict, long maximumContentBytes,
        CancellationToken cancellationToken)
    {
        var localVersion = await TryLoadConflictVersionAsync(conflict.LocalObjectVersionId, cancellationToken);
        var dropboxVersion = await TryLoadConflictVersionAsync(conflict.DropboxObjectVersionId, cancellationToken);
        var localSide = await DescribeConflictSideAsync(endpoints, SyncAuthorityCloudSides.LogicalLeft,
            conflict.LocalPath, localVersion, cancellationToken);
        var dropboxSide = await DescribeConflictSideAsync(endpoints, SyncAuthorityCloudSides.LogicalRight,
            conflict.DropboxPath, dropboxVersion, cancellationToken);
        byte[]? localBytes = null;
        byte[]? dropboxBytes = null;
        string? note;
        if (localVersion is null || dropboxVersion is null)
            note = "Content preview is unavailable because the exact retained " +
                SideWord((localVersion is null ? localSide : dropboxSide).Endpoint) +
                " version is no longer retained; neither version was read and the retained metadata remains available.";
        else if (localVersion.Size is null || dropboxVersion.Size is null)
            note = "Content preview is unavailable because the retained " +
                SideWord((localVersion.Size is null ? localSide : dropboxSide).Endpoint) +
                " version records no reviewed length; neither version was read and the retained metadata remains available.";
        else if (localVersion.Size > maximumContentBytes || dropboxVersion.Size > maximumContentBytes)
            note = $"Content preview is bounded to {maximumContentBytes / (1024 * 1024)} MiB per version; metadata remains available.";
        else
        {
            // The retained authority of each side and the exact reviewed identity of each version are bound
            // BEFORE any byte is read, so a foreign, malformed, unretained or differently-rooted version is
            // refused here rather than read and then judged.
            var localAuthority = await ResolveConflictSideAuthorityAsync(pair, endpoints,
                SyncAuthorityCloudSides.LogicalLeft, cancellationToken);
            var dropboxAuthority = await ResolveConflictSideAuthorityAsync(pair, endpoints,
                SyncAuthorityCloudSides.LogicalRight, cancellationToken);
            RequireConflictMembershipAuthority(conflict, localAuthority, dropboxAuthority);
            RequireConflictVersionAuthority(conflict, localVersion, SyncAuthorityCloudSides.LogicalLeft);
            RequireConflictVersionAuthority(conflict, dropboxVersion, SyncAuthorityCloudSides.LogicalRight);
            var localIdentity = BindConflictVersionIdentity(localAuthority, localVersion);
            var dropboxIdentity = BindConflictVersionIdentity(dropboxAuthority, dropboxVersion);
            note = null;
            var localRead = await TryReadConflictSideAsync(pair, localSide, localVersion, localIdentity,
                localAuthority, cancellationToken);
            var dropboxRead = await TryReadConflictSideAsync(pair, dropboxSide, dropboxVersion, dropboxIdentity,
                dropboxAuthority, cancellationToken);
            if (localRead.Note is not null || dropboxRead.Note is not null)
                note = localRead.Note ?? dropboxRead.Note;
            else if (localRead.Bytes is not null && dropboxRead.Bytes is not null)
            {
                localBytes = localRead.Bytes;
                dropboxBytes = dropboxRead.Bytes;
            }
            // Every awaited read is revalidated before any byte is returned: a pair or account rebind during
            // the read is refused here, including when both accounts are individually valid and connected.
            if (localBytes is not null && dropboxBytes is not null)
                await RevalidateConflictReadAsync(pair, endpoints, conflict, localAuthority, dropboxAuthority,
                    localVersion, dropboxVersion, cancellationToken);
        }
        return new SyncConflictContentRead(localSide.Version?.Size, dropboxSide.Version?.Size,
            localSide.Version?.ContentHash, dropboxSide.Version?.ContentHash, localSide.ModifiedUtc,
            dropboxSide.ModifiedUtc, localBytes, dropboxBytes, note);
    }

    /// <summary>
    /// One side's read with its bounded failure reported as that endpoint's own unavailable-content note: a
    /// refused, changed or disappeared version names the endpoint that could not supply its exact retained
    /// version, and neither version is read or substituted.
    /// </summary>
    private async Task<ConflictSideBytes> TryReadConflictSideAsync(SyncPair pair, ConflictSide side,
        SyncAuthorityObjectVersion version, ConflictVersionIdentity identity, ConflictSideAuthority authority,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadConflictSideAsync(pair, side, version, identity, authority, cancellationToken);
        }
        catch (Exception exception) when (IsConflictContentUnavailable(exception))
        {
            return new ConflictSideBytes(null, "Content preview is unavailable because this pair's " +
                SideWord(side.Endpoint) + " version could not be read without changing either version (" +
                exception.GetType().Name + "); retained metadata remains available.");
        }
    }

    /// <summary>
    /// One side's bounded read, selected by the endpoint the pair's own layout assigns to that side's
    /// retained compatibility port. A local endpoint reads its exact retained file inside its own retained
    /// local root; a Dropbox endpoint reads its exact retained historical revision; a Google Drive endpoint
    /// reads its exact still-current reviewed binary. Every read goes through that endpoint's own retained
    /// account and root, and none of them mutates, stages or substitutes content.
    /// </summary>
    private async Task<ConflictSideBytes> ReadConflictSideAsync(SyncPair pair, ConflictSide side,
        SyncAuthorityObjectVersion version, ConflictVersionIdentity identity, ConflictSideAuthority authority,
        CancellationToken cancellationToken)
    {
        if (authority.IsLocal)
            return new ConflictSideBytes(await ReadLocalConflictVersionAsync(pair, side, version,
                cancellationToken), null);
        return SyncEndpointProviders.Get(side.Endpoint.Kind).Kind switch
        {
            // Dropbox keeps its existing read-only historical-revision capability: the reviewed predecessor
            // revision is the only accepted version, and the adapter itself verifies the selected root, the
            // returned identity, the length and the native content hash.
            SyncEndpointKind.Dropbox => new ConflictSideBytes(await ReadDropboxConflictVersionAsync(side, version,
                identity, authority, cancellationToken), null),
            // Google Drive uses the adapter's existing reviewed current-binary read: it binds the native id and
            // revision, validates root and location before the media read, bounds and verifies size and hash,
            // and rereads metadata, version, root and location afterwards. An unavailable older revision stays
            // unavailable and is never silently substituted with the latest bytes.
            SyncEndpointKind.GoogleDrive => new ConflictSideBytes(await ReadGoogleDriveConflictVersionAsync(side,
                version, identity, authority, cancellationToken), null),
            _ => new ConflictSideBytes(null,
                "Content preview is unavailable because this build has no reviewed read for the " +
                SyncEndpointProviders.Get(side.Endpoint.Kind).DisplayName + " endpoint of this pair; no " +
                "provider call and no change to either version was made, and the retained metadata remains available.")
        };
    }

    /// <summary>
    /// The local side of one conflict version. The retained local identity is the reviewed identity: the
    /// exact local root the version was observed under, that root's own folder identity, the exact file
    /// identity (volume serial and file id), the reviewed length and last-write time and the exact content
    /// hash under this pair's registered content algorithm. Every one of them must still hold and the opened
    /// file must be a verified descendant of that root before a byte is returned.
    /// </summary>
    private async Task<byte[]> ReadLocalConflictVersionAsync(SyncPair pair, ConflictSide side,
        SyncAuthorityObjectVersion version, CancellationToken cancellationToken)
    {
        var endpoint = side.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint.LocalPath))
            throw new InvalidDataException("The resolved local endpoint carries no local folder path.");
        if (endpoint.LocalVolumeSerialNumber is not { } volumeSerial ||
            endpoint.LocalRootFileId is not { } rootFileId || volumeSerial == 0 || rootFileId == 0)
            throw new InvalidDataException("The resolved local endpoint carries no complete local folder identity.");
        // The retained identity must have been produced with this pair's registered content algorithm; a
        // foreign or stale identity is refused by the existing shared rule rather than reinterpreted.
        SyncAuthorityLocalVersionIdentity.RequireIdentityAlgorithm(version.IdentityJson, pair.Provider);
        var identity = RetainedLocalFileIdentity(version.IdentityJson);
        var lastWriteUtc = RetainedLocalLastWriteUtc(version.IdentityJson);
        using var root = PilotDirectoryLease.Open(PilotPath.NormalizeLocalRoot(endpoint.LocalPath),
            new PilotDirectoryIdentity(volumeSerial, rootFileId));
        root.VerifyBinding();
        var relative = PilotPath.NormalizeRelative(side.RelativePath);
        var fullPath = Path.GetFullPath(Path.Combine(root.Path, relative.Replace('/',
            Path.DirectorySeparatorChar)));
        if (!PilotPath.IsPathInside(root.Path, fullPath))
            throw new IOException("The retained local conflict path is not inside this endpoint's own local root.");
        var entry = new PilotLocalEntry(relative, PilotEntryKind.File, fullPath, version.Size ?? 0, lastWriteUtc,
            version.ContentHash, identity.VolumeSerialNumber, identity.FileId);
        // The existing reviewed-local-file read verifies the held file's exact path inside the bound root,
        // its reviewed length and last-write time and its content hash under the pair's algorithm.
        await using var stream = await PilotLocalScanner.OpenValidatedForUploadAsync(entry, root,
            cancellationToken, pair.Provider, pair.Id);
        if (!PilotDirectoryLease.GetFileInformationByHandle(stream.SafeFileHandle, out var observed))
            throw new IOException("The local conflict file identity could not be read.");
        var observedFileId = ((ulong)observed.FileIndexHigh << 32) | observed.FileIndexLow;
        if (observed.VolumeSerialNumber != identity.VolumeSerialNumber || observedFileId != identity.FileId)
            throw new IOException("The local conflict file is not the exact file identity that was reviewed.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    /// <summary>
    /// The cloud side of one conflict version, read through the store authority of that exact endpoint: the
    /// endpoint's own retained account and cloud folder, never the pair's legacy fields and never a
    /// currently selected account. The retained version's exact file id and revision are the only accepted
    /// version, and the provider store itself verifies the selected root, the returned identity, the length
    /// and the native content hash before any byte is accepted.
    /// </summary>
    private async Task<byte[]> ReadDropboxConflictVersionAsync(ConflictSide side,
        SyncAuthorityObjectVersion version, ConflictVersionIdentity identity, ConflictSideAuthority authority,
        CancellationToken cancellationToken)
    {
        if (authority.Store is not DropboxPilotClient remote)
            throw new NotSupportedException("The retained Dropbox adapter does not expose the reviewed " +
                "historical-revision read, so the " + SideWord(side.Endpoint) +
                " version stays unavailable rather than being substituted.");
        if (string.IsNullOrWhiteSpace(identity.NativeId) || string.IsNullOrWhiteSpace(identity.Revision))
            throw new InvalidDataException("The retained cloud version identity has no exact file id and revision.");
        var expected = new PilotRemoteEntry(PilotPath.NormalizeRelative(side.RelativePath), PilotEntryKind.File,
            identity.NativeId, identity.Revision, version.Size ?? 0, version.ContentHash, side.ModifiedUtc);
        await using var bytes = new MemoryStream(checked((int)(version.Size ?? 0)));
        var downloaded = await remote.DownloadHistoricalRevisionAsync(expected, bytes, cancellationToken);
        if (downloaded.Size != version.Size ||
            !string.Equals(downloaded.ContentHash, version.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(downloaded.FileId, identity.NativeId, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, identity.Revision, StringComparison.Ordinal))
            throw new IOException("The cloud bytes are not the exact retained version of this conflict.");
        return bytes.ToArray();
    }

    /// <summary>
    /// The Google Drive side of one conflict version, read through that adapter's existing reviewed
    /// current-binary read. The retained typed identity supplies the exact native file id and revision, and
    /// the adapter itself binds them, validates the selected root and the item's location before the media
    /// read, verifies the reviewed length and the provider's whole-file hash, and rereads the metadata, the
    /// version, the root and the location afterwards. No keepForever change, revision mutation, export, new
    /// scope or disk staging is used, and nothing is buffered beyond this side's reviewed length.
    /// </summary>
    private async Task<byte[]> ReadGoogleDriveConflictVersionAsync(ConflictSide side,
        SyncAuthorityObjectVersion version, ConflictVersionIdentity identity, ConflictSideAuthority authority,
        CancellationToken cancellationToken)
    {
        if (authority.Store is null)
            throw new InvalidOperationException("The " + SideWord(side.Endpoint) +
                " endpoint has no resolved adapter; the preview is refused rather than redirected.");
        if (string.IsNullOrWhiteSpace(identity.NativeId) || string.IsNullOrWhiteSpace(identity.Revision))
            throw new InvalidDataException("The retained Google Drive version has no exact native file id and " +
                "revision, so the exact reviewed binary cannot be requested.");
        var expected = new PilotRemoteEntry(PilotPath.NormalizeRelative(side.RelativePath), PilotEntryKind.File,
            identity.NativeId, identity.Revision, version.Size ?? 0, version.ContentHash, side.ModifiedUtc);
        await using var bytes = new MemoryStream(checked((int)(version.Size ?? 0)));
        var downloaded = await authority.Store.DownloadAsync(expected, bytes, cancellationToken);
        if (downloaded.Size != version.Size ||
            !string.Equals(downloaded.ContentHash, version.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(downloaded.FileId, identity.NativeId, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, identity.Revision, StringComparison.Ordinal))
            throw new IOException("The cloud bytes are not the exact retained reviewed version of this conflict.");
        return bytes.ToArray();
    }

    /// <summary>
    /// One conflict side's retained authority: its own endpoint on the retained compatibility port, the active
    /// root binding id the conflict's membership must name, whether its root carries an explicit cloud
    /// identity, the retained provider account facts and the adapter that reaches exactly that account and
    /// root. A local side carries no cloud authority. This is retained evidence for the read; it allocates no
    /// operation, no private stage and no effect.
    /// </summary>
    private sealed record ConflictSideAuthority(SyncEndpointContextSide Endpoint, string Port,
        string RootBindingId, bool IsExplicitCloudRoot, CloudAccountFacts? Facts, ISyncRemoteStore? Store,
        string? ProviderAccountId, string? NamespaceId, string? FolderId)
    {
        internal bool IsLocal => Endpoint.IsLocal;
        internal string Provider => Facts?.Provider ?? string.Empty;
        internal string Algorithm => SyncEndpointProviders.Get(Endpoint.Kind).ContentHashAlgorithm;
    }

    /// <summary>The exact native identity one cloud side's reviewed read must reproduce.</summary>
    private sealed record ConflictVersionIdentity(string? NativeId, string? Revision, string Algorithm);

    /// <summary>
    /// The retained authority of one conflict side, resolved from the pair's own endpoint layout and the
    /// retained rows: the active root binding of that exact pair side, the retained provider account and its
    /// native namespace, and the registered adapter for exactly that endpoint. A typed cloud root must agree
    /// with its retained account and namespace; a legacy cloud root is bound through its retained account row.
    /// Nothing here writes, allocates a stage, or falls back to a pair-primary or opposite-side account.
    /// </summary>
    private async Task<ConflictSideAuthority> ResolveConflictSideAuthorityAsync(SyncPair pair,
        SyncEndpointContext endpoints, string port, CancellationToken cancellationToken)
    {
        var endpoint = RequireConflictEndpointForPort(endpoints, port);
        var activeRoot = await _authority.LoadActiveRootBindingIdAsync(pair.Id, port, cancellationToken);
        if (endpoint.IsLocal)
            return new ConflictSideAuthority(endpoint, port, activeRoot, false, null, null, null, null, null);
        var retained = await _authority.LoadRetainedRootAuthorityAsync(pair.Id, port, cancellationToken)
            ?? throw new InvalidOperationException("The pair has no retained authority root for its " +
                SideWord(endpoint) + " side; the preview is refused rather than read through another root.");
        if (!string.Equals(retained.RootBindingId, activeRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("The retained authority root of the " + SideWord(endpoint) +
                " side is not its current active root; the preview is refused rather than read through a different root.");
        var facts = ResolveCloudAccount(endpoint.Kind, endpoint.ConnectionId);
        if (!retained.HasRetainedProviderAccount ||
            !string.Equals(retained.AccountProvider, facts.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(retained.AccountId, facts.AccountId, StringComparison.Ordinal) ||
            !string.Equals(retained.AccountRootNamespaceId, facts.RootNamespaceId, StringComparison.Ordinal) ||
            !string.Equals(retained.AccountHomeNamespaceId, facts.HomeNamespaceId, StringComparison.Ordinal) ||
            !SyncAuthorityProviderAccountIdentity.RetainedRowMatchesDerivation(facts.Provider,
                retained.IdentityProviderAccountId, facts.AccountId, facts.RootNamespaceId, facts.HomeNamespaceId))
            throw new InvalidOperationException("The retained authority root of the " + SideWord(endpoint) +
                " side does not belong to the exact account this endpoint names; the preview is refused.");
        var retainedNamespace = SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(facts.Provider,
            facts.AccountId, facts.RootNamespaceId, facts.HomeNamespaceId);
        if (retained.IsExplicitCloudIdentity &&
            (!string.Equals(retained.IdentityProvider, facts.Provider, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(retained.IdentityNamespaceId, retainedNamespace, StringComparison.Ordinal)))
            throw new InvalidOperationException("The retained cloud root identity of the " + SideWord(endpoint) +
                " side disagrees with its retained provider account; the preview is refused.");
        if (!string.IsNullOrWhiteSpace(endpoint.NamespaceId) &&
            !string.Equals(endpoint.NamespaceId, retainedNamespace, StringComparison.Ordinal))
            throw new InvalidOperationException("The " + SideWord(endpoint) + " endpoint namespace is not its " +
                "retained authority namespace; the preview is refused rather than read through another namespace.");
        if (!string.IsNullOrWhiteSpace(endpoint.FolderId) && !string.IsNullOrWhiteSpace(retained.IdentityFolderId) &&
            !string.Equals(endpoint.FolderId, retained.IdentityFolderId, StringComparison.Ordinal))
            throw new InvalidOperationException("The " + SideWord(endpoint) + " endpoint folder identity is not " +
                "its retained authority folder; the preview is refused rather than read through another root.");
        var folderId = !string.IsNullOrWhiteSpace(retained.IdentityFolderId)
            ? retained.IdentityFolderId
            : endpoint.FolderId;
        if (string.IsNullOrWhiteSpace(folderId))
            throw new InvalidDataException("The " + SideWord(endpoint) +
                " side retains no stable provider folder identity; the preview is refused.");
        var record = new SyncEndpointRecord(endpoint.Kind, Provider: facts.Provider,
            ConnectionId: facts.ConnectionId, NamespaceId: retainedNamespace, FolderId: folderId,
            FolderPath: endpoint.FolderPath);
        return new ConflictSideAuthority(endpoint, port, activeRoot, retained.IsExplicitCloudIdentity, facts,
            CreateCloudEndpointAdapter(record, facts), facts.ProviderAccountId, retainedNamespace, folderId);
    }

    /// <summary>
    /// The conflict's own membership authority must still be the pair's current active side roots. The
    /// snapshot already carries both membership ids, root binding ids and generations from its own
    /// conflict-to-membership joins, so a conflict whose sides no longer sit under those roots is refused.
    /// </summary>
    private static void RequireConflictMembershipAuthority(SyncAuthorityConflictSnapshot conflict,
        ConflictSideAuthority local, ConflictSideAuthority remote)
    {
        if (!string.Equals(conflict.LocalRootBindingId, local.RootBindingId, StringComparison.Ordinal) ||
            !string.Equals(conflict.DropboxRootBindingId, remote.RootBindingId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(conflict.LocalMembershipId) ||
            string.IsNullOrWhiteSpace(conflict.DropboxMembershipId) ||
            conflict.LocalMembershipGeneration < 1 || conflict.DropboxMembershipGeneration < 1)
            throw new InvalidOperationException("The retained conflict's membership authority is not the pair's " +
                "current active side roots; the preview is refused rather than read across a rebind.");
    }

    /// <summary>The retained version row must be the conflict's own logical object on the exact side it names.</summary>
    private static void RequireConflictVersionAuthority(SyncAuthorityConflictSnapshot conflict,
        SyncAuthorityObjectVersion version, string port)
    {
        if (!string.Equals(version.ObjectId, conflict.ObjectId, StringComparison.Ordinal) ||
            !string.Equals(version.Side, port, StringComparison.Ordinal))
            throw new InvalidDataException("A retained version of this conflict is not the version of its own " +
                "logical object on its own side; the preview is refused rather than read across objects.");
    }

    /// <summary>
    /// The exact reviewed native identity of one retained version. A typed cloud identity must agree with the
    /// side's retained provider, provider account, native namespace and registered algorithm, and an explicit
    /// type that is malformed, mixed or foreign is refused and never falls back to the legacy shape. A genuine
    /// legacy root keeps its legacy flat identity, bound to the same retained side. A local side keeps its own
    /// reviewed identity, which its own read verifies.
    /// </summary>
    private static ConflictVersionIdentity BindConflictVersionIdentity(ConflictSideAuthority side,
        SyncAuthorityObjectVersion version)
    {
        if (side.IsLocal) return new ConflictVersionIdentity(null, null, string.Empty);
        var hash = version.ContentHash ?? throw new InvalidDataException("The retained " + SideWord(side.Endpoint) +
            " version records no content hash; the preview is refused rather than read unverified.");
        if (SyncAuthorityCloudObservationIdentity.TryParse(version.IdentityJson, out var typed) && typed is not null)
        {
            if (!string.Equals(typed.Provider, side.Provider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(typed.ProviderAccountId, side.ProviderAccountId, StringComparison.Ordinal) ||
                !string.Equals(typed.NamespaceId, side.NamespaceId, StringComparison.Ordinal) ||
                !string.Equals(typed.ContentAlgorithm, side.Algorithm, StringComparison.Ordinal))
                throw new InvalidDataException("The retained cloud version identity does not agree with the " +
                    SideWord(side.Endpoint) + " side's retained provider, account, namespace or algorithm; it is " +
                    "refused rather than reinterpreted.");
            if (!SyncContentIdentity.IdentityMatchesAlgorithm(typed.ContentAlgorithm, hash))
                throw new InvalidDataException("The retained " + SideWord(side.Endpoint) +
                    " version hash is not a digest of its own registered algorithm; the preview is refused.");
            if (string.IsNullOrWhiteSpace(typed.Revision))
                throw new InvalidDataException("The retained " + SideWord(side.Endpoint) +
                    " version names no native revision, so no exact reviewed version can be requested.");
            return new ConflictVersionIdentity(typed.FileId, typed.Revision, typed.ContentAlgorithm);
        }
        if (side.IsExplicitCloudRoot)
            throw new InvalidDataException("A retained cloud root requires an explicit cloud observation " +
                "identity; the legacy identity is refused rather than reinterpreted.");
        if (!SyncContentIdentity.IdentityMatchesAlgorithm(side.Algorithm, hash))
            throw new InvalidDataException("The retained " + SideWord(side.Endpoint) +
                " version hash is not a digest of its own registered algorithm; the preview is refused.");
        using var legacyDocument = JsonDocument.Parse(version.IdentityJson);
        var legacy = legacyDocument.RootElement;
        if (legacy.ValueKind != JsonValueKind.Object || legacy.TryGetProperty("identityKind", out _))
            throw new InvalidDataException("The retained cloud version declares an invalid explicit identity " +
                "kind; it cannot be reinterpreted as a legacy identity.");
        var nativeId = legacy.TryGetProperty("fileId", out var id) ? id.GetString() : null;
        var revision = legacy.TryGetProperty("revision", out var pinned) ? pinned.GetString() : null;
        if (string.IsNullOrWhiteSpace(nativeId) || string.IsNullOrWhiteSpace(revision))
            throw new InvalidDataException("The retained " + SideWord(side.Endpoint) +
                " version identity has no exact native file id and revision; the preview is refused.");
        return new ConflictVersionIdentity(nativeId, revision, side.Algorithm);
    }

    /// <summary>
    /// After every awaited read: the pair's current ordered endpoint context, the protected account binding
    /// of each cloud side, the retained conflict with its versions and memberships, and the active root ids
    /// must all still be exactly what the read was performed under. A pair or account rebind is refused here,
    /// including when both accounts are individually valid and connected.
    /// </summary>
    private async Task RevalidateConflictReadAsync(SyncPair pair, SyncEndpointContext endpoints,
        SyncAuthorityConflictSnapshot conflict, ConflictSideAuthority localAuthority,
        ConflictSideAuthority dropboxAuthority, SyncAuthorityObjectVersion localVersion,
        SyncAuthorityObjectVersion dropboxVersion, CancellationToken cancellationToken)
    {
        var currentPair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, pair.Id, StringComparison.Ordinal));
        if (currentPair is null || SyncEndpointContext.Capture(currentPair) != endpoints)
            throw new InvalidOperationException("The pair's endpoint context changed while its conflict " +
                "preview was read; no content is returned.");
        var current = await _authority.LoadConflictAsync(conflict.ConflictId, cancellationToken);
        if (!string.Equals(current.Status, conflict.Status, StringComparison.Ordinal) ||
            current.ConflictGeneration != conflict.ConflictGeneration ||
            !string.Equals(current.ObjectId, conflict.ObjectId, StringComparison.Ordinal) ||
            !string.Equals(current.LocalObjectVersionId, conflict.LocalObjectVersionId, StringComparison.Ordinal) ||
            !string.Equals(current.DropboxObjectVersionId, conflict.DropboxObjectVersionId, StringComparison.Ordinal) ||
            !string.Equals(current.LocalMembershipId, conflict.LocalMembershipId, StringComparison.Ordinal) ||
            !string.Equals(current.DropboxMembershipId, conflict.DropboxMembershipId, StringComparison.Ordinal) ||
            current.LocalMembershipGeneration != conflict.LocalMembershipGeneration ||
            current.DropboxMembershipGeneration != conflict.DropboxMembershipGeneration ||
            !string.Equals(current.LocalPath, conflict.LocalPath, StringComparison.Ordinal) ||
            !string.Equals(current.DropboxPath, conflict.DropboxPath, StringComparison.Ordinal) ||
            !string.Equals(current.LocalRootBindingId, conflict.LocalRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(current.DropboxRootBindingId, conflict.DropboxRootBindingId, StringComparison.Ordinal))
            throw new InvalidOperationException("The retained conflict changed while its preview was read; no " +
                "content is returned.");
        foreach (var authority in new[] { localAuthority, dropboxAuthority })
        {
            var activeRoot = await _authority.LoadActiveRootBindingIdAsync(pair.Id, authority.Port,
                cancellationToken);
            if (!string.Equals(activeRoot, authority.RootBindingId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active " + SideWord(authority.Endpoint) +
                    " authority root changed while the conflict preview was read; no content is returned.");
            if (authority.IsLocal) continue;
            var rebound = await ResolveConflictSideAuthorityAsync(pair, endpoints, authority.Port,
                cancellationToken);
            if (!string.Equals(rebound.ProviderAccountId, authority.ProviderAccountId, StringComparison.Ordinal) ||
                !string.Equals(rebound.NamespaceId, authority.NamespaceId, StringComparison.Ordinal) ||
                !string.Equals(rebound.FolderId, authority.FolderId, StringComparison.Ordinal) ||
                !string.Equals(rebound.Facts!.ConnectionId, authority.Facts!.ConnectionId, StringComparison.Ordinal) ||
                !string.Equals(rebound.Facts.AccountId, authority.Facts.AccountId, StringComparison.Ordinal) ||
                !string.Equals(rebound.Facts.RootNamespaceId, authority.Facts.RootNamespaceId, StringComparison.Ordinal) ||
                !string.Equals(rebound.Facts.HomeNamespaceId, authority.Facts.HomeNamespaceId, StringComparison.Ordinal))
                throw new InvalidOperationException("The protected account binding of the " +
                    SideWord(authority.Endpoint) + " side changed while the conflict preview was read; no content " +
                    "is returned.");
        }
        var retainedLocal = await _authority.LoadObjectVersionAsync(localVersion.ObjectVersionId, cancellationToken);
        var retainedDropbox = await _authority.LoadObjectVersionAsync(dropboxVersion.ObjectVersionId,
            cancellationToken);
        if (!string.Equals(retainedLocal.ContentHash, localVersion.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(retainedDropbox.ContentHash, dropboxVersion.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(retainedLocal.IdentityJson, localVersion.IdentityJson, StringComparison.Ordinal) ||
            !string.Equals(retainedDropbox.IdentityJson, dropboxVersion.IdentityJson, StringComparison.Ordinal) ||
            retainedLocal.Size != localVersion.Size || retainedDropbox.Size != dropboxVersion.Size)
            throw new InvalidOperationException("The retained versions changed while the conflict preview was " +
                "read; no content is returned.");
    }

    /// <summary>The endpoint-derived word a preview note uses for one side: never a hardcoded provider label.</summary>
    private static string SideWord(SyncEndpointContextSide endpoint) => endpoint.IsLocal
        ? "local"
        : SyncEndpointProviders.Get(endpoint.Kind).DisplayName;

    /// <summary>
    /// One resolved conflict side: the pair's own endpoint for the retained compatibility port, the exact
    /// retained version row for that side, the relative path its retained membership names and the reviewed
    /// modification time that version carries.
    /// </summary>
    private async Task<ConflictSide> DescribeConflictSideAsync(SyncEndpointContext endpoints, string port,
        string relativePath, SyncAuthorityObjectVersion? version, CancellationToken cancellationToken)
    {
        var endpoint = RequireConflictEndpointForPort(endpoints, port);
        var evidence = endpoint.IsLocal || version is null
            ? null
            : await _authority.LoadLatestObjectVersionEvidenceAsync(version.ObjectVersionId, cancellationToken);
        return new ConflictSide(endpoint, PilotPath.NormalizeRelative(relativePath), version,
            version is null ? null : ConflictVersionModifiedUtc(version, evidence));
    }

    /// <summary>The endpoint the pair's own layout assigns to one retained compatibility port.</summary>
    private static SyncEndpointContextSide RequireConflictEndpointForPort(SyncEndpointContext endpoints,
        string port)
    {
        if (string.Equals(endpoints.Left.CompatibilityPort, port, StringComparison.Ordinal)) return endpoints.Left;
        if (string.Equals(endpoints.Right.CompatibilityPort, port, StringComparison.Ordinal)) return endpoints.Right;
        throw new InvalidDataException(
            "The pair's endpoint layout does not assign the retained " + port + " port to either side.");
    }

    /// <summary>The reviewed modification time of one retained version: its own local write time or its cloud evidence time.</summary>
    private static DateTimeOffset? ConflictVersionModifiedUtc(SyncAuthorityObjectVersion version,
        string? evidenceJson) =>
        SyncBootstrapActions.IdentityTime(version.IdentityJson, "lastWriteUtc") ??
        (evidenceJson is null ? null : SyncBootstrapActions.EvidenceTime(evidenceJson, "modifiedUtc"));

    /// <summary>
    /// One retained version row, or null when it is no longer retained. A missing row is reported as the
    /// exact unavailable side rather than resolved from the current version or another object.
    /// </summary>
    private async Task<SyncAuthorityObjectVersion?> TryLoadConflictVersionAsync(string objectVersionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _authority.LoadObjectVersionAsync(objectVersionId, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The reviewed local file identity (volume serial and file id) a retained local version carries.</summary>
    private static PilotDirectoryIdentity RetainedLocalFileIdentity(string identityJson)
    {
        using var identity = JsonDocument.Parse(identityJson);
        var root = identity.RootElement;
        if (root.TryGetProperty("volumeSerial", out var volume) && volume.ValueKind == JsonValueKind.String &&
            root.TryGetProperty("fileId", out var fileId) && fileId.ValueKind == JsonValueKind.String &&
            uint.TryParse(volume.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var volumeSerial) &&
            ulong.TryParse(fileId.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var rootFileId) && volumeSerial != 0 && rootFileId != 0)
            return new PilotDirectoryIdentity(volumeSerial, rootFileId);
        throw new InvalidDataException("The retained local version identity carries no exact local file identity.");
    }

    /// <summary>The reviewed last-write time a retained local version carries.</summary>
    private static DateTime RetainedLocalLastWriteUtc(string identityJson)
    {
        using var identity = JsonDocument.Parse(identityJson);
        if (identity.RootElement.TryGetProperty("lastWriteUtc", out var node) &&
            node.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.UtcDateTime;
        throw new InvalidDataException("The retained local version identity carries no reviewed last-write time.");
    }

    /// <summary>
    /// The exact failures a conflict-content preview presents as an unavailable-content note instead of as
    /// content: a refused, changed or disappeared identity, an unavailable account or credential, a provider
    /// refusal, an algorithm this build cannot compute or a malformed retained identity. The pair's endpoint
    /// resolution is refused before any read and is deliberately not covered here.
    /// </summary>
    private static bool IsConflictContentUnavailable(Exception exception) =>
        exception is IOException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or NotSupportedException or DropboxApiException or
            // The registered Drive adapter reports a provider refusal (a deleted, superseded or otherwise
            // unavailable reviewed object) as its own provider exception, exactly as Dropbox does above; a
            // provider refusal means this side's exact retained version is unavailable, never that another
            // revision may be substituted.
            GoogleDriveApiException;

    /// <summary>One conflict side resolved from the pair's own endpoint layout and its retained version.</summary>
    private sealed record ConflictSide(SyncEndpointContextSide Endpoint, string RelativePath,
        SyncAuthorityObjectVersion? Version, DateTimeOffset? ModifiedUtc);

    /// <summary>One conflict side's bounded read: the exact retained bytes, or why they were not read.</summary>
    private sealed record ConflictSideBytes(byte[]? Bytes, string? Note);

    /// <summary>The complete retained content preview of one conflict: metadata, exact bytes and any note.</summary>
    private sealed record SyncConflictContentRead(long? LocalSize, long? DropboxSize, string? LocalHash,
        string? DropboxHash, DateTimeOffset? LocalModifiedUtc, DateTimeOffset? DropboxModifiedUtc,
        byte[]? LocalBytes, byte[]? DropboxBytes, string? Note);

    private static (string Group, string Recommendation) ConflictRecommendation(DateTimeOffset? local,
        DateTimeOffset? dropbox)
    {
        if (local is null || dropbox is null) return ("Unreliable or missing time", "Review both versions");
        var delta = local.Value - dropbox.Value;
        if (delta.Duration() <= TimeSpan.FromMinutes(2))
            return ("Approximately equal time", "Review both versions");
        // W2-A: the recommendation is provider-neutral. The two compatibility group keys 'Local newer' and
        // 'Dropbox newer' are RETAINED because they are the existing physical/filter ports and are not always
        // logical left/right for a reversed layout; the group names never appear as a provider claim on a
        // human surface, and the recommendation itself names neither side and never selects a winner.
        return delta > TimeSpan.Zero
            ? ("Local newer", "Review the newer version")
            : ("Dropbox newer", "Review the newer version");
    }

    public Task<IReadOnlyList<SyncAuthorityMonitorProjection>> LoadMonitorProjectionAsync(
        CancellationToken cancellationToken = default) =>
        _authority.LoadMonitorProjectionAsync(cancellationToken: cancellationToken);

    public Task<SyncAuthorityBatchResult> RunPairsAsync(IEnumerable<SyncPair> pairs, string trigger,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null,
        CancellationToken cancellationToken = default, Action<string>? admissionProgress = null) =>
        RunBatchCommandAsync(pairs, trigger, recoveryOnly: false, onProgress, cancellationToken,
            admissionProgress);

    public Task<SyncAuthorityBatchResult> RecoverPairsAsync(IEnumerable<SyncPair> pairs,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null,
        CancellationToken cancellationToken = default, Action<string>? admissionProgress = null) =>
        RunBatchCommandAsync(pairs, "Startup recovery", recoveryOnly: true, onProgress, cancellationToken,
            admissionProgress);

    public async Task<bool?> TogglePauseActiveRunAsync(CancellationToken cancellationToken = default)
    {
        // The exact control and its new pause state are decided under the control lock BEFORE any
        // audit/database await, so a blocked or slow audit writer delays only this record and never
        // the pause/resume the user actually asked for.
        bool? paused;
        lock (_runControlGate)
        {
            if (_activeRunControl is null) paused = null;
            else
            {
                if (_activeRunControl.IsPaused) _activeRunControl.Resume();
                else _activeRunControl.Pause();
                paused = _activeRunControl.IsPaused;
            }
        }
        var commandKind = paused == false ? "resume" : "pause";
        var commandId = await CreateAcceptedCommandAsync(commandKind, null, null, "{}", cancellationToken);
        try
        {
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            if (paused is null)
                await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                    JsonSerializer.Serialize(new { reason = "no_active_run" }),
                    cancellationToken: CancellationToken.None);
            else
                await _authority.TransitionCommandRequestAsync(commandId, "completed",
                    JsonSerializer.Serialize(new { paused }), cancellationToken: CancellationToken.None);
            return paused;
        }
        catch
        {
            await TryBlockCommandAsync(commandId, "run_control_failed");
            throw;
        }
    }

    public async Task<bool> StopActiveRunAsync(CancellationToken cancellationToken = default)
    {
        // Capture and signal the exact active control before the audit command is accepted. The run's
        // own cancellation request must never wait on the durable audit writer.
        bool stopped;
        lock (_runControlGate)
        {
            stopped = _activeRunControl is not null;
            _activeRunControl?.Stop();
        }
        var commandId = await CreateAcceptedCommandAsync("stop", null, null, "{}", cancellationToken);
        try
        {
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            var resultJson = stopped
                ? JsonSerializer.Serialize(new { stopRequested = true })
                : JsonSerializer.Serialize(new { reason = "no_active_run" });
            await _authority.TransitionCommandRequestAsync(commandId, stopped ? "completed" : "blocked",
                resultJson, cancellationToken: CancellationToken.None);
            return stopped;
        }
        catch
        {
            await TryBlockCommandAsync(commandId, "run_control_failed");
            throw;
        }
    }

    // The exact target and its checkpoint are captured and signalled atomically, BEFORE the audit
    // command is accepted. A completed history row must never authorize stopping a different job that
    // started in the meantime, and a blocked or delayed audit writer must never delay this run's own
    // cancellation. The audit keeps using the caller's token, never the newly cancelled run token.
    public async Task<bool> StopActiveRunAsync(string expectedRunId, TimeSpan checkpointTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRunId);
        if (checkpointTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkpointTimeout));
        Task? checkpoint = null;
        lock (_runControlGate)
        {
            if (_activeRunSnapshot?.Id == expectedRunId && _activeRunControl is { } control)
            {
                checkpoint = _activeRunCheckpoint!.Task;
                control.Stop();
            }
        }
        var commandId = await CreateAcceptedCommandAsync("stop", null, expectedRunId,
            JsonSerializer.Serialize(new { expectedRunId }), cancellationToken);
        try
        {
            await _authority.TransitionCommandRequestAsync(commandId, "executing", cancellationToken: cancellationToken);
            if (checkpoint is null)
            {
                await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                    JsonSerializer.Serialize(new { reason = "exact_run_not_active", expectedRunId }),
                    cancellationToken: CancellationToken.None);
                return false;
            }
            // Wait for this captured job, not whichever job is active when a later query happens.
            await checkpoint.WaitAsync(checkpointTimeout, cancellationToken);
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(new { expectedRunId, checkpointReached = true }),
                cancellationToken: CancellationToken.None);
            return true;
        }
        catch
        {
            await TryBlockCommandAsync(commandId, "exact_run_checkpoint_unconfirmed");
            throw;
        }
    }

    public async Task<bool?> SetActiveRunPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        // Apply the exact pause/resume to the captured control under the control lock before the audit
        // command is accepted; only the record, never the effect, waits on the writer.
        string? expectedId = null;
        bool? result = null;
        lock (_runControlGate)
            if (_activeRunSnapshot is { } active && _activeRunControl is { } control)
            {
                expectedId = active.Id;
                if (paused) control.Pause(); else control.Resume();
                result = control.IsPaused;
            }
        if (expectedId is null) return null;
        var commandId = await CreateAcceptedCommandAsync(paused ? "pause" : "resume", null, expectedId,
            JsonSerializer.Serialize(new { expectedId, paused }), cancellationToken);
        try
        {
            await _authority.TransitionCommandRequestAsync(commandId, "executing", cancellationToken: cancellationToken);
            await _authority.TransitionCommandRequestAsync(commandId, result is null ? "blocked" : "completed",
                JsonSerializer.Serialize(new { expectedId, paused = result }), cancellationToken: CancellationToken.None);
            return result;
        }
        catch { await TryBlockCommandAsync(commandId, "run_control_failed"); throw; }
    }

    public async Task<bool> WaitForActiveRunCheckpointAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Task checkpoint;
        lock (_runControlGate)
            checkpoint = _activeRunCheckpoint?.Task ?? Task.CompletedTask;
        if (checkpoint.IsCompleted) return true;

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await checkpoint.WaitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<SyncRunResult> ResolveConflictCommandAsync(SyncPair pair, string relativePath,
        SyncAuthorityConflictResolutionKind resolutionKind, string? expectedConflictId = null,
        long? expectedConflictGeneration = null, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, Action<string>? admissionProgress = null)
    {
        if ((expectedConflictId is null) != (expectedConflictGeneration is null))
            throw new ArgumentException("Expected conflict identity and generation must be supplied together.");
        var payload = JsonSerializer.Serialize(new
        {
            relativePath,
            resolution = resolutionKind.ToString(),
            expectedConflictId,
            expectedConflictGeneration
        });
        var commandId = await CreateAcceptedCommandAsync("resolve_conflict", pair.Id, relativePath, payload,
            cancellationToken, admissionProgress: admissionProgress);
        var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        PilotRunControl? runControl = null;
        try
        {
            runControl = await BeginRunAsync(cancellationToken, commandId, "resolve_conflict", pair.Id);
            await using var databaseConnections = _authority.BeginConnectionReuseScope();
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            var result = await ResolveConflictCoreAsync(pair, relativePath, commandAdmission, resolutionKind, runControl,
                expectedConflictId, expectedConflictGeneration, cancellationToken, onProgress);
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(new
                {
                    result.Uploaded,
                    result.Downloaded,
                    result.Conflicts,
                    result.Blocked,
                    result.Stopped,
                    failed = result.Failed, notAttempted = result.NotAttempted,
                    needsVerification = result.NeedsVerification, detailsSource = "retained_history_and_authority",
                    pairs = RetainedRunSummaries([new SyncAuthorityBatchItemResult(pair, result, null, null)])
                }), cancellationToken: CancellationToken.None);
            return result;
        }
        catch
        {
            await TryBlockCommandAsync(commandId, "conflict_command_failed");
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    public async Task<DropboxConnectionBinding> ConnectDropboxAsync(Func<Uri, Task> openAuthorizationPage,
        CancellationToken cancellationToken = default, bool addAccount = false)
    {
        stateAuthority.VerifyAuthority();
        // AUTH1: the one pending attempt for this host. A second authorization is refused by the
        // controller before any browser page or provider call, and the first attempt is untouched.
        using var attempt = BeginAuthorizationAttempt(SyncProviders.Dropbox, SyncAuthorizationAction.Connect, null,
            cancellationToken);
        var coordinator = new DropboxConnectionCoordinator(httpClient, CreateAuthorizationReceiver(),
            credentialStore, connectionStore);
        var binding = await coordinator.ConnectAsync(openAuthorizationPage, cancellationToken: attempt.Token,
            addAccount: addAccount, includeFileRequests: true);
        attempt.MarkCommitted();
        EvictAccountTokens(binding.ConnectionId);
        return binding;
    }

    public async Task<IDropboxFolderBrowserService> CreateFolderBrowserAsync(
        CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        // Provider routing: a Google connection browses Google Drive folders through the same browser
        // contract instead of failing with a Dropbox account message.
        var google = _googleDriveConnections.Load(connectionId);
        if (google is not null && connectionStore.Load(connectionId) is null)
            return new GoogleDriveFolderBrowserClient(httpClient, GoogleTokenSource(google), google,
                google.SharedDrivesEnabled);
        var connection = await RequireConnectionAsync(cancellationToken, connectionId);
        var tokenSource = AccountTokenSource(connection);
        return new DropboxBrowserClient(httpClient, tokenSource, connection);
    }

    public async Task<DropboxFolderEntry> EnsureRecommendedRecoveryFolderAsync(
        CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        var browser = await CreateFolderBrowserAsync(cancellationToken, connectionId);
        var pairs = await LoadPairsAsync(cancellationToken);
        var bound = LoadAccountBinding(connectionId) ?? throw new InvalidOperationException("Connect the selected cloud account first.");
        return await EnsureRecommendedRecoveryFolderCoreAsync(browser, pairs.Pairs.Where(pair =>
            pair.ConnectionId == bound.ConnectionId || string.IsNullOrWhiteSpace(pair.ConnectionId)).ToArray(), cancellationToken);
    }

    internal static async Task<DropboxFolderEntry> EnsureRecommendedRecoveryFolderCoreAsync(
        IDropboxFolderBrowserService browser, IReadOnlyList<SyncPair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Any(pair => DropboxPathsOverlap(pair.DropboxRoot,
                SyncDropboxArtifactPaths.RecoveryContainerPath)))
            throw new InvalidOperationException(
                "The recommended Recovery folder overlaps an existing sync pair. Choose another dedicated Dropbox folder.");
        DropboxFolderEntry? parent = null;
        var expectedPath = string.Empty;
        foreach (var name in SyncDropboxArtifactPaths.RecoveryContainerPath.Split('/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            expectedPath += "/" + name;
            var matches = await FindChildFoldersAsync(browser, parent?.FolderId, name, cancellationToken);
            if (matches.Length > 1)
                throw new InvalidDataException($"Dropbox returned more than one {name} folder identity.");
            if (matches.Length == 1)
            {
                parent = matches[0];
            }
            else try
            {
                parent = await browser.CreateFolderAsync(parent?.FolderId, name, cancellationToken);
            }
            catch (DropboxApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                matches = await FindChildFoldersAsync(browser, parent?.FolderId, name, cancellationToken);
                var recovered = matches.SingleOrDefault();
                if (recovered is null) throw;
                parent = recovered;
            }
            if (!string.Equals(parent.Path, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Dropbox returned a recommended Recovery folder segment outside the expected product path.");
        }
        if (parent is null || !string.Equals(parent.Path, SyncDropboxArtifactPaths.RecoveryContainerPath,
                 StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dropbox did not return the expected recommended Recovery path.");
        return parent;
    }

    private static async Task<DropboxFolderEntry[]> FindChildFoldersAsync(
        IDropboxFolderBrowserService browser, string? parentFolderId, string name,
        CancellationToken cancellationToken)
    {
        const int maximumCursors = 4096;
        var matches = new Dictionary<string, DropboxFolderEntry>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var page = await browser.ListFoldersAsync(parentFolderId, cancellationToken);
        while (true)
        {
            foreach (var item in page.Folders.Where(item =>
                         string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                matches[item.FolderId] = item;
            if (matches.Count > 1 || page.ContinueCursor is null) break;
            if (cursors.Count >= maximumCursors)
                throw new InvalidDataException(
                    "Automatic folder lookup exceeded its bounded cursor window; use Dropbox browser search.");
            var cursorFingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(page.ContinueCursor)));
            if (!cursors.Add(cursorFingerprint))
                throw new InvalidDataException("Dropbox cycled a folder cursor during automatic path lookup.");
            page = await browser.ContinueFoldersAsync(page.ContinueCursor, cancellationToken);
        }
        return matches.Values.OrderBy(item => item.FolderId, StringComparer.Ordinal).ToArray();
    }

    [SupportedOSPlatform("windows")]
    public async Task<SyncPairBindingResult> BindPairAsync(SyncPairBindingRequest request,
        CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await BindPairCoreAsync(request, cancellationToken, progress);
    }

    [SupportedOSPlatform("windows")]
    private async Task<SyncPairBindingResult> BindPairCoreAsync(SyncPairBindingRequest request,
        CancellationToken cancellationToken, Action<string>? progress,
        long? recoveredExpectedUnpublishedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        stateAuthority.VerifyAuthority();
        if (request.Priority is < -100 or > 100)
            throw new InvalidDataException("Pair priority must be a whole number from -100 to 100.");
        if (!Enum.IsDefined(request.Direction)) throw new InvalidDataException("Unknown sync mode.");
        progress?.Invoke("Pair save: verifying retained database and installation state; no file transfer has started.");
        var installationId = await EnsureAuthorityInitializedAsync(cancellationToken, progress);
        progress?.Invoke("Pair save: checking current file effects and leases before changing the pair configuration.");
        await EnsurePairConfigurationMutableAsync(request.ReplacingPairId, cancellationToken);
        if (IsGoogleConnection(request.ConnectionId))
            return await BindGoogleDrivePairAsync(request, installationId, cancellationToken, progress,
                recoveredExpectedUnpublishedRevision);

        var retained = await LoadPairsAsync(cancellationToken);
        var prior = request.ReplacingPairId is null
            ? null
            : retained.Pairs.SingleOrDefault(item =>
                string.Equals(item.Id, request.ReplacingPairId, StringComparison.Ordinal));
        if (request.ReplacingPairId is not null && prior is null)
            throw new InvalidDataException("The sync pair selected for replacement is not retained.");
        if (request.ExpectedPolicyRevision is { } expectedRevision &&
            (expectedRevision < 1 || prior is null || prior.PolicyRevision != expectedRevision))
            throw new InvalidOperationException("The pair policy changed after it was selected. Read the current pair and confirm the exact revision again.");
        var connection = await RequireConnectionAsync(cancellationToken, request.ConnectionId);
        var requestedName = request.Name?.Trim() ?? string.Empty;
        if (requestedName.Length == 0 || requestedName.Length > 80)
            throw new InvalidDataException("Enter a pair name of 1 to 80 characters.");
        if (retained.Pairs.Any(existing =>
                !string.Equals(existing.Id, request.ReplacingPairId, StringComparison.Ordinal) &&
                string.Equals(existing.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another folder pair already uses that name.");
        ArgumentNullException.ThrowIfNull(request.RecoveryFolder);
        SyncDropboxArtifactPaths.RequireRecoveryContainer(request.RecoveryFolder.Path);
        if (!RecoveryPlacementAllowed(request.DropboxFolder.Path, request.RecoveryFolder.Path) ||
            retained.Pairs.Any(existing => !string.Equals(existing.Id, request.ReplacingPairId, StringComparison.Ordinal) &&
                                           string.Equals(existing.ConnectionId, connection.ConnectionId, StringComparison.Ordinal) &&
                                           DropboxPathsOverlap(existing.DropboxRoot, request.RecoveryFolder.Path)))
            throw new InvalidOperationException(
                "Recovery cannot be the same folder as a pair or contain a pair. When Recovery is inside a broader pair, Sync Shark excludes it automatically.");

        using var local = PilotDirectoryLease.Open(request.LocalRoot);
        if (request.FilesOnDemandEnabled &&
            WindowsCloudFilesProvider.IsCloudFilesPlaceholder(local.Path) &&
            !WindowsCloudFilesProvider.IsOwnedSyncRoot(local.Path))
            throw new InvalidOperationException(
                "This local folder already uses another File Explorer cloud provider. Keep this pair Available offline and use that provider's File Explorer controls for online-only storage.");
        var tokenSource = AccountTokenSource(connection);
        var verifier = new DropboxPilotClient(httpClient, tokenSource, request.DropboxFolder.Path, connection,
            request.DropboxFolder.FolderId, _transferLimiter);
        progress?.Invoke("Pair save: asking Dropbox to verify the selected data folder's exact account, namespace and folder identity.");
        await verifier.VerifyRootIdentityAsync(cancellationToken);
        var recoveryVerifier = new DropboxPilotClient(httpClient, tokenSource, request.RecoveryFolder.Path,
            connection, request.RecoveryFolder.FolderId, _transferLimiter);
        progress?.Invoke("Pair save: asking Dropbox to verify the retained Recovery folder's exact identity.");
        await recoveryVerifier.VerifyRootIdentityAsync(cancellationToken);

        var remoteBinding = new PilotRemoteRootBinding(request.DropboxFolder.Path,
            request.DropboxFolder.FolderId);
        var settings = new PilotSettings(local.Path, local.Identity.VolumeSerialNumber, local.Identity.FileId,
            remoteBinding.Path, remoteBinding.FolderId);
        var pair = SyncPair.FromPilot(settings, request.Name, connection.RootNamespaceId) with
        {
            Enabled = request.Enabled,
            Priority = request.Priority,
            PropagateDeletions = request.PropagateDeletions,
            SelectionRules = ProtectedSelectionRules(request.DropboxFolder.Path, request.RecoveryFolder,
                request.SelectionRules),
            SelectionDefaultIncluded = request.SelectionDefaultIncluded ?? true,
            ConnectionId = connection.ConnectionId,
            Provider = "dropbox",
            InitialReviewApproved = false,
            FilesOnDemandEnabled = request.FilesOnDemandEnabled,
            RecoveryRetentionDays = request.RecoveryRetentionDays,
            Direction = request.Direction,
            DefaultStorage = request.DefaultStorage,
            IgnoreRules = SyncIgnorePolicy.Normalize(request.IgnoreRules),
            DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(request.DirectoryStorageRules)
        };
        pair = RetainVerifiedEndpoints(pair,
            RetainedPolicyEditEndpointDocument(pair, prior, request.Endpoints), connection.RootNamespaceId);
        if (prior is null && retained.Pairs.Any(existing => SameRootIdentity(existing, pair)))
            throw new InvalidOperationException(
                "Those local and Dropbox folders already belong to another folder pair.");
        if (prior is not null && SameRootIdentity(prior, pair) &&
            (prior.RecoveryRoot is null || string.Equals(prior.RecoveryRoot.RootNamespaceId,
                connection.RootNamespaceId, StringComparison.Ordinal)))
            pair = pair with
            {
                Id = prior.Id,
                SelectionRules = request.SelectionRules ?? prior.SelectionRules,
                IgnoreRules = SyncIgnorePolicy.Normalize(request.IgnoreRules ?? prior.IgnoreRules),
                SelectionDefaultIncluded = request.SelectionDefaultIncluded ?? prior.SelectionDefaultIncluded,
                InitialReviewApproved = prior.InitialReviewApproved,
                DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(request.DirectoryStorageRules ?? prior.DirectoryStorageRules),
                DirectoryStorageApplyPaths = prior.DirectoryStorageApplyPaths,
                FileStorageRules = prior.FileStorageRules,
                DefaultStorage = request.DefaultStorage ?? prior.DefaultStorage,
                FilesOnDemandEnabled = request.FilesOnDemandEnabled || prior.FilesOnDemandEnabled,
            };
        var identityChanged = request.ReplacingPairId is not null &&
                              !string.Equals(request.ReplacingPairId, pair.Id, StringComparison.Ordinal);
        if (identityChanged && prior?.FilesOnDemandEnabled == true)
        {
            await DisableFilesOnDemandCoreAsync(prior.Id, cancellationToken);
            retained = await LoadPairsAsync(cancellationToken);
            prior = retained.Pairs.Single(item => string.Equals(item.Id, request.ReplacingPairId,
                StringComparison.Ordinal));
        }
        progress?.Invoke("Pair save: verifying directory storage rules against the selected folder identities.");
        pair = await BindDirectoryStorageRulesAsync(pair, prior, request.DirectoryStorageRules,
            connection, cancellationToken);
        var authoritative = await _authority.LoadPairSetAsync(cancellationToken);
        var authoritativePrior = authoritative.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, pair.Id, StringComparison.Ordinal));
        pair = pair with
        {
            PolicyRevision = authoritativePrior is null
                ? 1
                : SamePolicy(authoritativePrior, pair)
                    ? authoritativePrior.PolicyRevision
                    : checked(authoritativePrior.PolicyRevision + 1)
        };
        pair = pair with
        {
            RecoveryRoot = new SyncRemoteRecoveryRootBinding(request.RecoveryFolder.Path,
                request.RecoveryFolder.FolderId, connection.RootNamespaceId)
        };
        // Editing policy on the same bound roots is not a new control-directory allocation.
        // Reuse only the exact retained ownership identity after verifying its held directory
        // and marker. Replaying allocation at a new policy revision correctly collides with
        // its original deterministic operation; never weaken that database collision guard.
        var retainedControl = prior is not null && prior.Id == pair.Id && SameRootIdentity(prior, pair)
            ? prior.ControlNamespace : null;
        if (retainedControl is not null && (retainedControl.InstallationId != installationId ||
            retainedControl.NamespaceId != pair.Id))
            throw new IOException("The retained private staging owner does not match this installation and pair.");
        using var verifiedRetainedControl = retainedControl is null ? null : SyncControlNamespace.OpenLocal(retainedControl, local);
        if (prior is null && authoritativePrior is null && recoveredExpectedUnpublishedRevision is null)
        {
            // A no-effect failed save retains immutable blocked authority under the deterministic
            // pair ID. Use a fresh candidate only after proving its exact same-installation roots
            // and precondition-only collision; never unblock or replay the failed operation.
            await _authority.ReleaseFailedUnpublishedPairRootsAsync(cancellationToken);
            pair = await _authority.RetryFailedUnpublishedAccountPairAsync(pair, installationId,
                SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, connection.AccountId,
                    connection.RootNamespaceId, connection.HomeNamespaceId), connection.RootNamespaceId,
                cancellationToken);
        }
        progress?.Invoke("Pair save: registering the verified root bindings and policy revision in the retained database.");
        // root708-r8(1): carry the verified retained private-control ownership INTO the candidate before the
        // atomic publication serializes it. Otherwise the skipped post-commit retention would leave the
        // published definition without its ControlNamespace while only the in-memory pair gained it.
        if (retainedControl is not null)
            pair = pair with { ControlNamespace = retainedControl };
        var registration = await RegisterPairAuthorityAsync(pair, connection, installationId, local,
            cancellationToken, replacingPair: identityChanged ? prior : null,
            recoveredExpectedUnpublishedRevision: recoveredExpectedUnpublishedRevision,
            publishedPrior: !identityChanged && recoveredExpectedUnpublishedRevision is null &&
                authoritativePrior is not null && SameRootIdentity(authoritativePrior, pair) &&
                pair.PolicyRevision > authoritativePrior.PolicyRevision
                    ? authoritativePrior : null);
        SyncLocalControlNamespaceBinding localControl;
        try
        {
            localControl = retainedControl?.Local ?? registration.CreatedControl ??
                await new SyncAuthorityLocalControlNamespaceAllocator(_authority).BindAsync(local,
                    installationId, pair.Id, registration.LocalRootBindingId, pair.PolicyRevision,
                    registration.ProfileId, cancellationToken, partitionCandidate: pair);
        }
        catch (Exception allocationError) when (allocationError is IOException or InvalidOperationException)
        {
            // A rejected new pair must not leave invisible reservations blocking a later
            // save in this SAME initialized host. The shared transaction refuses published,
            // dispatched, uncertain or bound work; it never alters the failed control object.
            // Keep the original allocation failure if recovery itself cannot complete.
            try
            {
                var released = await _authority.ReleaseFailedUnpublishedPairRootsAsync(cancellationToken);
                progress?.Invoke($"Pair save refused: released {released} failed unpublished data-root reservations; private-control ownership remains unchanged.");
            }
            catch (Exception recoveryError) when (recoveryError is not OperationCanceledException)
            {
                progress?.Invoke("Pair save refused; reservation recovery is incomplete and will be checked at startup. " +
                    SyncActivityText.Failure(recoveryError));
            }
            throw;
        }
        pair = pair with
        {
            ControlNamespace = retainedControl ?? new SyncControlNamespaceBinding(installationId, pair.Id, localControl)
        };
        WindowsCloudFilesProvider? activatedCloudProvider = null;
        var providerStartedForBinding = false;
        if (pair.FilesOnDemandEnabled)
            activatedCloudProvider = GetOrStartCloudProvider(pair, verifier,
                out providerStartedForBinding);
        try
        {
            progress?.Invoke("Pair save: committing the verified pair definition; existing approval and owned staging are preserved for unchanged roots.");
            if (identityChanged)
            {
                local.VerifyBinding();
                await _authority.PublishAccountPairReplacementAsync(pair,
                    registration.ReplacementRoots ?? throw new InvalidOperationException("The replacement roots were not prepared."),
                    cancellationToken);
            }
            else
            {
                if (!registration.DefinitionCommitted)
                    await _authority.RetainPairDefinitionAsync(pair, select: true, cancellationToken);
            }
        }
        catch
        {
            if (providerStartedForBinding && activatedCloudProvider is not null)
                RollBackCloudProviderStart(pair.Id, activatedCloudProvider);
            throw;
        }
        var legacySource = pairStore.Load().Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, request.ReplacingPairId ?? pair.Id, StringComparison.Ordinal));
        if (legacySource is not null)
        {
            if (authoritativePrior is null && !identityChanged)
                await _authority.RecordAcceptedLegacyPairImportAsync(legacySource, pair,
                    cancellationToken: cancellationToken);
            await pairStore.UpsertAsync(pair, select: true,
                replacingPairId: identityChanged ? request.ReplacingPairId : null,
                cancellationToken: cancellationToken);
        }
        _selectedPairId = pair.Id;
        progress?.Invoke("Pair save: reading back the committed pair definition and current selection.");
        var pairSet = await LoadPairsAsync(cancellationToken, progress);
        progress?.Invoke("Pair save: committed pair definition read back successfully; no sync was started by this save.");
        return new SyncPairBindingResult(pairSet, pair, settings, identityChanged);
    }

    public async Task<SyncPairSet> SetPairEnabledAsync(string pairId, bool enabled,
        CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        progress?.Invoke($"Pair {pairId}: verifying local state before {(enabled ? "enabling" : "pausing")}; no file transfer was requested by this command.");
        stateAuthority.VerifyAuthority();
        await EnsureAuthorityInitializedAsync(cancellationToken, progress);
        progress?.Invoke($"Pair {pairId}: checking retained file effects and live leases before changing its enabled state.");
        await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
        var retained = await LoadPairsAsync(cancellationToken, progress);
        var pair = retained.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId,
                       StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (pair.Enabled == enabled)
        {
            progress?.Invoke($"Pair {pairId}: retained enabled state is already {enabled}; no configuration change was made.");
            return retained;
        }
        pair = pair with { Enabled = enabled, PolicyRevision = checked(pair.PolicyRevision + 1) };
        progress?.Invoke($"Pair {pairId}: saving enabled={enabled} at policy revision {pair.PolicyRevision}; existing history and completed file effects are retained.");
        await _authority.RegisterPairAsync(pair.Id, pair.PolicyRevision,
            new SyncAuthorityPairConfiguration(pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1,
                "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue),
            cancellationToken);
        await _authority.RetainPairDefinitionAsync(pair,
            string.Equals(retained.SelectedPairId, pairId, StringComparison.Ordinal), cancellationToken);
        var legacy = pairStore.Load();
        if (legacy.Pairs.Any(item => string.Equals(item.Id, pairId, StringComparison.Ordinal)))
            await pairStore.UpsertAsync(pair, select: string.Equals(legacy.SelectedPairId, pairId,
                StringComparison.Ordinal), cancellationToken: cancellationToken);
        var result = await LoadPairsAsync(cancellationToken, progress);
        progress?.Invoke($"Pair {pairId}: saved enabled={enabled} at policy revision {pair.PolicyRevision} and reloaded its retained definition.");
        return result;
    }

    public async Task VerifyPairRootConnectionsAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
                       string.Equals(item.Id, pairId, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Cloud-pair staging requires the Windows state authority.");
            await PrepareCloudPairAsync(pair, cancellationToken);
            return;
        }
        using var local = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        local.VerifyBinding();
        var connection = IsGooglePair(pair) ? null : await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        if (connection is not null && !string.Equals(pair.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException("This pair belongs to a different Dropbox connection.");
        var remote = CreateRemoteStoreForPair(pair, connection);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        if (pair.RecoveryRoot is not null)
        {
            SyncDropboxArtifactPaths.RequireRecoveryContainerIfDropbox(pair, pair.RecoveryRoot.RootPath);
            await remote.VerifyRecoveryRootAsync(pair.RecoveryRoot, cancellationToken);
        }
    }

    public async Task<SyncPairSet> RetirePairAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        stateAuthority.VerifyAuthority();
        await EnsureAuthorityInitializedAsync(cancellationToken);
        await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
        var retained = await LoadPairsAsync(cancellationToken);
        var pair = retained.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        if (pair is null)
            throw new InvalidDataException("The requested sync pair is not retained.");
        if (pair.FilesOnDemandEnabled)
        {
            await DisableFilesOnDemandCoreAsync(pair.Id, cancellationToken);
            retained = await LoadPairsAsync(cancellationToken);
        }
        // Authority refuses retirement before we remove the legacy projection or
        // dispose an owned provider. The run gate prevents a new run from entering
        // between the configuration check and this final publication.
        if (await _authority.IsPairDefinitionRetainedAsync(pairId, cancellationToken))
            await _authority.RetirePairDefinitionAsync(pairId, cancellationToken);
        lock (_cloudProviderGate)
        {
            if (_cloudProviders.Remove(pairId, out var provider)) provider.Dispose();
        }
        var legacy = pairStore.Load();
        if (legacy.Pairs.Any(item => string.Equals(item.Id, pairId, StringComparison.Ordinal)))
            await pairStore.RemoveAsync(pairId, cancellationToken);
        if (string.Equals(_selectedPairId, pairId, StringComparison.Ordinal)) _selectedPairId = null;
        return await LoadPairsAsync(cancellationToken);
    }

    public async Task EnsurePairConfigurationMutableAsync(string? pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        if (string.IsNullOrWhiteSpace(pairId) || !File.Exists(_authority.DatabasePath)) return;
        await _authority.EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
    }

    // The persisted guard alone cannot see a run still preparing its first plan.
    // Hold the existing run admission gate for the entire public configuration
    // mutation, including native/provider preparation and publication. Composed
    // authoring paths call private cores, never reacquire this non-reentrant gate.
    private async Task<IDisposable> EnterPairConfigurationMutationAsync(CancellationToken cancellationToken)
    {
        stateAuthority.VerifyAuthority();
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(
                "Wait for the active sync to reach its checkpoint before changing a folder pair.");
        return new PairConfigurationMutationLease(_runGate);
    }

    private sealed class PairConfigurationMutationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        internal void TransferOwnershipToRun() => _ = Interlocked.Exchange(ref _gate, null)
            ?? throw new ObjectDisposedException(nameof(PairConfigurationMutationLease));
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public Task<SyncFolderSelectionTree> LoadFolderSelectionTreeAsync(string pairId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadFolderSelectionTreeCoreAsync(pairId, cancellationToken), cancellationToken);

        // W2-C: the read-only endpoint hierarchy is produced by the one generic reader in
    // SyncAuthorityRuntimeHost.EndpointHierarchy.cs. The previous single-mechanism implementation that
    // opened pair.LocalRoot and routed the cloud side through IsGooglePair/CreateGoogleDriveClient(pair)
    // is removed there; this delegate keeps the public entry point unchanged.
    private Task<SyncFolderSelectionTree> LoadFolderSelectionTreeCoreAsync(string pairId,
        CancellationToken cancellationToken = default) =>
        LoadEndpointHierarchyTreeCoreAsync(pairId, cancellationToken);

public async Task<SyncPairDraftPreview> LoadPairDraftPreviewAsync(string localBrowseRoot,
        DropboxFolderEntry dropboxBrowseRoot, CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(localBrowseRoot);
        ArgumentNullException.ThrowIfNull(dropboxBrowseRoot);
        if (IsGoogleConnection(connectionId))
        {
            var google = _googleDriveConnections.Load(connectionId)
                         ?? throw new GoogleDriveBlockedException(
                             "This Google Drive account is not connected. Reconnect it before creating a pair.");
            var browseRoot = new SyncPair("pair-draft-preview-000000000001", "Pair draft preview",
                Path.GetFullPath(localBrowseRoot), 0, 0, dropboxBrowseRoot.Path,
                dropboxBrowseRoot.FolderId, ConnectionId: google.ConnectionId,
                Provider: SyncProviders.GoogleDrive);
            return await LoadGoogleDriveDraftPreviewAsync(browseRoot, localBrowseRoot, dropboxBrowseRoot,
                google, cancellationToken);
        }
        var connection = await RequireConnectionAsync(cancellationToken, connectionId);
        var normalizedLocalRoot = Path.GetFullPath(localBrowseRoot);
        using var root = PilotDirectoryLease.Open(normalizedLocalRoot);
        // A draft needs directory presence, not content hashes. The scanner retains verified
        // directory identities while includePath=false prevents every file from being opened.
        var local = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
            includePath: _ => false, traverseDirectory: _ => true);
        var remote = new DropboxPilotClient(httpClient,
            AccountTokenSource(connection),
            dropboxBrowseRoot.Path, connection, dropboxBrowseRoot.FolderId, _transferLimiter);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        var cloud = await remote.ListEntriesAsync(cancellationToken);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        var localFolders = local.Entries.Values.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteFolders = cloud.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = localFolders.Keys.Concat(remoteFolders.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nodes = paths.Select(path =>
        {
            localFolders.TryGetValue(path, out var localFolder);
            remoteFolders.TryGetValue(path, out var remoteFolder);
            var presence = localFolder is not null && remoteFolder is not null
                ? SyncFolderPresence.Mixed
                : remoteFolder is not null ? SyncFolderPresence.CloudOnly : SyncFolderPresence.LocalOnly;
            var localIdentity = localFolder?.VolumeSerialNumber is not null && localFolder.FileId is not null
                ? LocalSelectionIdentity(localFolder)
                : null;
            var dropboxIdentity = remoteFolder?.Id;
            return new SyncFolderSelectionNode(path,
                Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                path.Count(character => character == '/') + 1, false, presence,
                paths.Count(candidate => candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)),
                dropboxIdentity is null ? localIdentity : "dropbox:" + dropboxIdentity,
                new SyncFolderRollup(0, 0, 0, 0, 0, 0), localIdentity, dropboxIdentity);
        }).ToArray();
        int localRootFiles;
        try
        {
            localRootFiles = Directory.EnumerateFiles(root.Path, "*", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            localRootFiles = -1;
        }
        var dropboxRootFiles = cloud.Count(item => item.Kind == PilotEntryKind.File &&
                                                   !item.RelativePath.Contains('/'));
        return new SyncPairDraftPreview(normalizedLocalRoot, dropboxBrowseRoot, nodes, localRootFiles,
            dropboxRootFiles, local.Decisions.Count == 0 && localRootFiles >= 0);
    }

    public async Task<SyncFolderSelectionPage> LoadFolderSelectionPageAsync(string pairId,
        int limit = 250, int offset = 0, string? search = null, string? filter = null,
        bool refreshIndex = false, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
                       string.Equals(item.Id, pairId, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        var page = refreshIndex ? null : await _authority.LoadFolderSelectionPageAsync(pairId, limit,
            offset, search, filter, cancellationToken);
        if (page is not null) return page;
        var prepared = await PreparePairAsync(pair, cancellationToken);
        await CreateObserver(pair, prepared).ObserveAsync("manual", cancellationToken);
        return await _authority.LoadFolderSelectionPageAsync(pairId, limit, offset, search, filter,
                   cancellationToken) ??
               new SyncFolderSelectionPage(pairId, [], offset, 0, 0, false, 0, 0, 0, 0,
                   new SyncFolderRollup(0, 0, 0, 0, 0, 0));
    }

    private static IEnumerable<string> ExistingFolderAncestors(string relativePath,
        IReadOnlySet<string> folderPaths)
    {
        var candidate = PilotPath.NormalizeRelative(relativePath);
        while (candidate.Length != 0)
        {
            if (folderPaths.Contains(candidate)) yield return candidate;
            var separator = candidate.LastIndexOf('/');
            if (separator < 0) yield break;
            candidate = candidate[..separator];
        }
    }

    public Task<SyncCloudAvailabilityResult> SetFolderAvailabilityAsync(string pairId,
        string folderPath, bool keepOnDevice, CancellationToken cancellationToken = default) =>
        SetPathAvailabilityAsync(pairId, folderPath, keepOnDevice, cancellationToken);

    public async Task<SyncCloudAvailabilityResult> SetPathAvailabilityAsync(string pairId,
        string relativePath, bool keepOnDevice, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        stateAuthority.VerifyAuthority();
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(
                "Wait for the active synchronization run to finish before changing file availability.");
        try
        {
            var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
                           string.Equals(item.Id, pairId, StringComparison.Ordinal))
                       ?? throw new InvalidDataException("The requested sync pair is not retained.");
            return await SetPathAvailabilityCoreAsync(pair, relativePath, keepOnDevice, cancellationToken,
                onProgress: onProgress);
        }
        finally { _runGate.Release(); }
    }

    private async Task<SyncCloudAvailabilityResult> SetPathAvailabilityCoreAsync(SyncPair pair,
        string relativePath, bool keepOnDevice, CancellationToken cancellationToken,
        Func<string, bool>? includePath = null, bool recursive = true,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
            if (!pair.FilesOnDemandEnabled)
                throw new InvalidOperationException("Enable Windows Files On-Demand for this pair first.");
            await ReportStorageProgressAsync(pair, relativePath,
                "Opening the saved provider and verifying this pair's managed Cloud Files root.", onProgress);
            var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
            var provider = GetOrStartCloudProvider(pair, remote, out _);
            if (!keepOnDevice)
            {
                var folder = PilotPath.NormalizeRelative(relativePath);
                using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                    new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
                // This scoped availability scan decides whether local bytes may be freed, so it always
                // reads the held bytes instead of a retained metadata tuple that cannot prove freshness.
                await ReportStorageProgressAsync(pair, folder,
                    "Reading current local bytes in the selected subtree before freeing space; retained metadata is not used as freshness proof.", onProgress);
                var local = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                    includePath: item => InSubtree(item, folder),
                    onInventory: (path, observed) => ReportStorageProgressAsync(pair, path,
                        "Inspecting the local entry and reading its bytes where required; no file has been freed.",
                        onProgress, observed, unit: "local entries observed"),
                    refreshPaths: [folder], // Selected subtree only; this scan never authorizes deletion from absence.
                    contentProvider: pair.Provider, expectedPairId: pair.Id);
                await ReportStorageProgressAsync(pair, folder,
                    "Awaiting the provider's current file revisions for storage verification; no file has been freed.", onProgress);
                var cloud = (await remote.ListEntriesAsync(cancellationToken))
                    .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
                await ReportStorageProgressAsync(pair, folder,
                    "Loading exact retained two-sided synchronization points before availability changes.", onProgress);
                var synchronizationPoints = (await _authority.LoadLatestSynchronizationPointsAsync(pair.Id,
                        cancellationToken))
                    .Where(item => item.LocalRelativePath is not null)
                    .ToDictionary(item => item.LocalRelativePath!, StringComparer.OrdinalIgnoreCase);
                var conversionFailures = local.Decisions
                    .Where(item => InSubtree(item.RelativePath, folder) && (includePath?.Invoke(item.RelativePath) ?? true))
                    .Select(item => item.RelativePath + ": " + item.Reason)
                    .ToList();
                var candidates = new List<(PilotLocalEntry Local, PilotRemoteEntry Remote,
                    string CurrentIdentity, string ReplacementIdentity, bool NeedsAdoption)>();
                foreach (var item in local.Entries.Values.Where(item => item.Kind == PilotEntryKind.File &&
                                                                        item.CloudAvailability is null &&
                                                                        InSubtree(item.RelativePath, folder) &&
                                                                        (includePath?.Invoke(item.RelativePath) ?? true)))
                {
                    await ReportStorageProgressAsync(pair, item.RelativePath,
                        "Checking current local bytes, provider revision and the exact synchronized baseline; no file has been freed.", onProgress);
                    if (!cloud.TryGetValue(item.RelativePath, out var remoteEntry) ||
                        remoteEntry.Kind != PilotEntryKind.File || remoteEntry.Id is null ||
                        remoteEntry.Revision is null || remoteEntry.ContentHash is null ||
                        item.ContentHash is null || item.VolumeSerialNumber is null || item.FileId is null ||
                        item.Size != remoteEntry.Size ||
                        !string.Equals(item.ContentHash, remoteEntry.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        conversionFailures.Add(item.RelativePath +
                            ": local bytes do not exactly match a verified cloud revision; nothing was freed.");
                        continue;
                    }
                    var expectedIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                        item.VolumeSerialNumber.Value, item.FileId.Value, item.LastWriteUtc,
                        contentHash: item.ContentHash, size: item.Size);
                    var replacementIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                        item.VolumeSerialNumber.Value, item.FileId.Value, item.LastWriteUtc,
                        remoteEntry.Id, remoteEntry.Revision, contentHash: item.ContentHash, size: item.Size);
                    if (!synchronizationPoints.TryGetValue(item.RelativePath, out var point) ||
                        point.LocalObjectVersionId is null || point.DropboxObjectVersionId is null)
                    {
                        conversionFailures.Add(item.RelativePath +
                            ": no exact two-sided synchronization point exists; nothing was freed.");
                        continue;
                    }
                    var localVersion = await _authority.LoadObjectVersionAsync(point.LocalObjectVersionId,
                        cancellationToken);
                    var remoteVersion = await _authority.LoadObjectVersionAsync(point.DropboxObjectVersionId,
                        cancellationToken);
                    var remoteIdentityMatches = false;
                    using (var remoteIdentity = JsonDocument.Parse(remoteVersion.IdentityJson))
                    {
                        var rootElement = remoteIdentity.RootElement;
                        remoteIdentityMatches = rootElement.TryGetProperty("fileId", out var fileId) &&
                                                fileId.ValueKind == JsonValueKind.String &&
                                                string.Equals(fileId.GetString(), remoteEntry.Id,
                                                    StringComparison.Ordinal) &&
                                                rootElement.TryGetProperty("revision", out var revision) &&
                                                revision.ValueKind == JsonValueKind.String &&
                                                string.Equals(revision.GetString(), remoteEntry.Revision,
                                                    StringComparison.Ordinal);
                    }
                    var needsAdoption = SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(
                        localVersion.IdentityJson, expectedIdentity, item.ContentHash, item.Size);
                    var alreadyAdopted = SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(
                        localVersion.IdentityJson, replacementIdentity, item.ContentHash, item.Size);
                    if ((!needsAdoption && !alreadyAdopted) ||
                        !string.Equals(localVersion.ContentHash, item.ContentHash,
                            StringComparison.OrdinalIgnoreCase) || localVersion.Size != item.Size ||
                        !string.Equals(remoteVersion.ContentHash, remoteEntry.ContentHash,
                            StringComparison.OrdinalIgnoreCase) || remoteVersion.Size != remoteEntry.Size ||
                        !remoteIdentityMatches)
                    {
                        conversionFailures.Add(item.RelativePath +
                            ": current local and cloud identities are not the exact synchronized baseline; nothing was freed.");
                        continue;
                    }
                    candidates.Add((item, remoteEntry, localVersion.IdentityJson, replacementIdentity,
                        needsAdoption));
                }
                if (conversionFailures.Count != 0)
                {
                    await ReportStorageProgressAsync(pair, folder,
                        $"Storage verification refused {conversionFailures.Count} items. No file was freed; exact reasons are retained in the result.", onProgress);
                    return new SyncCloudAvailabilityResult(folder, false, 0, 0, conversionFailures);
                }
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (candidate.NeedsAdoption)
                    {
                        await ReportStorageProgressAsync(pair, candidate.Local.RelativePath,
                            "Retaining the verified local-to-cloud representation identity before native conversion.", onProgress);
                        await _authority.AdoptLocalCloudRepresentationAsync(pair.Id,
                            candidate.Local.RelativePath, candidate.CurrentIdentity,
                            candidate.ReplacementIdentity, candidate.Remote.Id!, candidate.Remote.Revision!,
                            candidate.Local.ContentHash!, candidate.Local.Size, cancellationToken);
                        if (RepresentationAdoptionPhaseForTest is not null)
                            await RepresentationAdoptionPhaseForTest(candidate.Local.RelativePath);
                    }
                    await ReportStorageProgressAsync(pair, candidate.Local.RelativePath,
                        "Converting the verified synchronized file to a managed online-only placeholder.", onProgress);
                    var converted = await provider.ConvertVerifiedSyncedFileAsync(candidate.Remote,
                        cancellationToken, dehydrateOverride: true);
                    var target = PilotPath.ResolveContainedPath(pair.LocalRoot, candidate.Local.RelativePath);
                    var convertedLastWrite = File.GetLastWriteTimeUtc(target);
                    if (converted.FileId != candidate.Local.FileId!.Value ||
                        convertedLastWrite != candidate.Local.LastWriteUtc)
                        throw new IOException(
                            "The converted placeholder changed local physical or timestamp identity.");
                    if (!string.Equals(converted.Identity.DropboxId, candidate.Remote.Id,
                            StringComparison.Ordinal) ||
                        !string.Equals(converted.Identity.DropboxRevision, candidate.Remote.Revision,
                            StringComparison.Ordinal) ||
                        !string.Equals(SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                                candidate.Local.VolumeSerialNumber!.Value, converted.FileId, convertedLastWrite,
                                converted.Identity.DropboxId, converted.Identity.DropboxRevision,
                                contentHash: candidate.Local.ContentHash, size: candidate.Local.Size),
                            candidate.ReplacementIdentity, StringComparison.Ordinal))
                        throw new IOException("The converted placeholder changed its adopted cloud identity.");
                }
            }
            return await provider.SetAvailabilityAsync(relativePath, keepOnDevice, recursive,
                cancellationToken, includePath, comparison => NotifyProgressAsync(onProgress,
                    new SyncAuthorityBatchProgress(pair, "Applying storage choices", null, null, null,
                        Comparison: comparison)));
    }

    public async Task<SyncPairSet> DisableFilesOnDemandAsync(string pairId,
        CancellationToken cancellationToken = default, string? expectedConnectionId = null,
        long? expectedPolicyRevision = null)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await DisableFilesOnDemandCoreAsync(pairId, cancellationToken,
            expectedConnectionId, expectedPolicyRevision);
    }

    private async Task<SyncPairSet> DisableFilesOnDemandCoreAsync(string pairId,
        CancellationToken cancellationToken, string? expectedConnectionId = null,
        long? expectedPolicyRevision = null)
    {
        stateAuthority.VerifyAuthority();
        await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
        var pairSet = await LoadPairsAsync(cancellationToken);
        var pair = pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId,
                       StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (expectedConnectionId is not null && pair.ConnectionId != expectedConnectionId ||
            expectedPolicyRevision is not null && pair.PolicyRevision != expectedPolicyRevision)
            throw new InvalidOperationException("The pair account or policy changed. Read its current state before changing storage.");
        if (!pair.FilesOnDemandEnabled) return pairSet;
        var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
        var provider = GetOrStartCloudProvider(pair, remote, out _);
        await provider.UnregisterAsync(cancellationToken);
        try
        {
            var updated = pair with
            {
                FilesOnDemandEnabled = false,
                DefaultStorage = SyncDirectoryStorage.AlwaysAvailable,
                DirectoryStorageRules = [],
                DirectoryStorageApplyPaths = [],
                FileStorageRules = [],
                PolicyRevision = checked(pair.PolicyRevision + 1)
            };
            var selected = string.Equals(pairSet.SelectedPairId, pair.Id, StringComparison.Ordinal);
            var policy = await RegisterPairPolicyAsync(updated, cancellationToken, select: selected);
            var retained = policy.DefinitionCommitted
                ? policy.CommittedPairSet!
                : await _authority.RetainPairDefinitionAsync(updated, selected, cancellationToken);
            lock (_cloudProviderGate)
                if (_cloudProviders.TryGetValue(pair.Id, out var current) && ReferenceEquals(current, provider))
                    _cloudProviders.Remove(pair.Id);
            provider.Dispose();
            _selectedPairId = retained.SelectedPairId;
            return await LoadPairsAsync(cancellationToken);
        }
        catch
        {
            // Persisted pair policy still says this root is managed. Re-register the exact owner
            // so a metadata failure cannot leave a silently orphaned Cloud Files root.
            if (!WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id, pair.Provider))
                provider.RegisterAndConnect();
            throw;
        }
    }

    public async Task<SyncPairSet> SetFolderSelectionAsync(string pairId, string folderPath,
        bool included, string? stableFolderIdentity = null, CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        stateAuthority.VerifyAuthority();
        await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
        var pairSet = await LoadPairsAsync(cancellationToken);
        var pair = pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId,
                       StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (!included)
            await EnsureSubtreeSafeToExcludeAsync(pair, folderPath, cancellationToken);
        var rules = SyncSelectionPolicy.SetFolder(pair.SelectionRules, folderPath, included,
            stableFolderIdentity);
        if (pair.RecoveryRoot is not null)
            rules = ProtectedSelectionRules(pair.DropboxRoot,
                new DropboxFolderEntry("Recovery", pair.RecoveryRoot.RootPath, pair.RecoveryRoot.RootFolderId),
                rules);
        if (rules.SequenceEqual(SyncSelectionPolicy.Normalize(pair.SelectionRules))) return pairSet;
        var updated = pair with
        {
            SelectionRules = rules,
            PolicyRevision = checked(pair.PolicyRevision + 1)
        };
        var selected = string.Equals(pairSet.SelectedPairId, pairId, StringComparison.Ordinal);
        var policy = await RegisterPairPolicyAsync(updated, cancellationToken, select: selected);
        var retained = policy.DefinitionCommitted
            ? policy.CommittedPairSet!
            : await _authority.RetainPairDefinitionAsync(updated, selected, cancellationToken);
        await _authority.UpdatePathIndexSelectionAsync(pairId,
            SyncSelectionPolicy.Compile(updated), cancellationToken);
        _selectedPairId = retained.SelectedPairId;
        return await LoadPairsAsync(cancellationToken);
    }

    public async Task<SyncPairSet> SetRootSelectionAsync(string pairId, bool included,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        stateAuthority.VerifyAuthority();
        await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
        var pairSet = await LoadPairsAsync(cancellationToken);
        var pair = pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId,
                       StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        if (!included) await EnsureSelectionSafeToExcludeAsync(pair, folderPath: null, cancellationToken);
        if (pair.SelectionDefaultIncluded == included && SyncSelectionPolicy.Normalize(pair.SelectionRules).Count == 0)
            return pairSet;
        IReadOnlyList<SyncSelectionRule> rules = [];
        if (included && pair.RecoveryRoot is not null)
            rules = ProtectedSelectionRules(pair.DropboxRoot,
                new DropboxFolderEntry("Recovery", pair.RecoveryRoot.RootPath, pair.RecoveryRoot.RootFolderId),
                rules);
        var updated = pair with
        {
            SelectionRules = rules,
            SelectionDefaultIncluded = included,
            PolicyRevision = checked(pair.PolicyRevision + 1)
        };
        var selected = string.Equals(pairSet.SelectedPairId, pairId, StringComparison.Ordinal);
        var policy = await RegisterPairPolicyAsync(updated, cancellationToken, select: selected);
        var retained = policy.DefinitionCommitted
            ? policy.CommittedPairSet!
            : await _authority.RetainPairDefinitionAsync(updated, selected, cancellationToken);
        await _authority.UpdatePathIndexSelectionAsync(pairId,
            SyncSelectionPolicy.Compile(updated), cancellationToken);
        _selectedPairId = retained.SelectedPairId;
        return await LoadPairsAsync(cancellationToken);
    }

    private Task EnsureSubtreeSafeToExcludeAsync(SyncPair pair, string folderPath,
        CancellationToken cancellationToken) =>
        EnsureSelectionSafeToExcludeAsync(pair, PilotPath.NormalizeRelative(folderPath), cancellationToken);

    private async Task EnsureSelectionSafeToExcludeAsync(SyncPair pair, string? folderPath,
        CancellationToken cancellationToken)
    {
        var folder = folderPath is null ? null : PilotPath.NormalizeRelative(folderPath);
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        // Exclusion safety compares held bytes with retained sync points, so a tuple-only match can
        // never stand in for the current content identity.
        var local = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
            contentProvider: pair.Provider, expectedPairId: pair.Id);
        if (local.Decisions.Any(item => folder is null || InSubtree(item.RelativePath, folder)))
            throw new InvalidOperationException(
                "This subtree contains an inaccessible or online-only local path. Resolve it before exclusion so local work cannot be hidden.");
        var points = await _authority.LoadLatestSynchronizationPointsAsync(pair.Id, cancellationToken);
        var synchronized = points.Where(point => point.LocalObjectVersionId is not null &&
                                                 point.DropboxObjectVersionId is not null &&
                                                 point.LocalRelativePath is not null &&
                                                  (folder is null || InSubtree(point.LocalRelativePath, folder)))
            .ToDictionary(point => point.LocalRelativePath!, StringComparer.OrdinalIgnoreCase);
        var current = local.Entries.Values.Where(item => folder is null || InSubtree(item.RelativePath, folder))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (var item in current.Values)
        {
            if (!synchronized.TryGetValue(item.RelativePath, out var point))
            {
                changed.Add(item.RelativePath);
                continue;
            }
            var version = await _authority.LoadObjectVersionAsync(point.LocalObjectVersionId!, cancellationToken);
            var identity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, item.Kind,
                item.VolumeSerialNumber!.Value, item.FileId!.Value, item.LastWriteUtc,
                item.CloudFileId, item.CloudRevision, contentHash: item.ContentHash, size: item.Size);
            if (!SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(version.IdentityJson,
                    identity, item.ContentHash, item.Size) ||
                !string.Equals(version.ContentHash, item.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                version.Size != item.Size)
                changed.Add(item.RelativePath);
        }
        changed.AddRange(synchronized.Keys.Where(path => !current.ContainsKey(path)));
        if (changed.Count > 0)
            throw new InvalidOperationException(
                "This subtree has local changes that are not at a verified common sync point. Sync or preserve them before exclusion (" +
                string.Join(", ", changed.Distinct(StringComparer.OrdinalIgnoreCase).Take(4)) + ").");
    }

    private static bool InSubtree(string path, string folder) =>
        folder == "." || string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private async Task<SyncPair> RefreshSelectionRulePathsAsync(SyncPair pair,
        CancellationToken cancellationToken, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        var rules = SyncSelectionPolicy.Normalize(pair.SelectionRules);
        if (rules.Count == 0) return pair;
        var started = DateTimeOffset.UtcNow;
        Task Report(string phase, string path, int count, string detail) => NotifyProgressAsync(onProgress,
            new SyncAuthorityBatchProgress(pair, "Checking", null, null, null,
                Comparison: new SyncComparisonProgress(phase, path, count, 0, started,
                    DateTimeOffset.UtcNow, detail, "paths inspected")));
        await Report("Locating Selected Folders", pair.LocalRoot, 0,
            "Reading local directory identities for selective-sync rules; no file content is read or transferred.");
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var local = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
            onInventory: (path, count) => Report("Locating Selected Folders", path, count,
                "Inspecting directory metadata for selective-sync folder identities; file contents are not read."),
            contentProvider: pair.Provider, expectedPairId: pair.Id, directoriesOnly: true);
        await Report("Locating Cloud Selection Folders", pair.DropboxRoot, 0,
            "Requesting cloud folder metadata to locate selective-sync folder identities; waiting for the provider listing, with no file transfer running.");
        var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
        var cloud = await remote.ListEntriesAsync(cancellationToken);
        await Report("Matching Selection Folder Identities", pair.Name, cloud.Count,
            "Matching returned cloud and local directory identities to the saved selective-sync rules; no file transfer is running.");
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in local.Entries.Values.Where(item => item.Kind == PilotEntryKind.Directory &&
                                                                  item.VolumeSerialNumber is not null &&
                                                                  item.FileId is not null))
            identities[LocalSelectionIdentity(entry)] = entry.RelativePath;
        foreach (var entry in cloud.Where(item => item.Kind == PilotEntryKind.Directory && item.Id is not null))
            identities["dropbox:" + entry.Id] = entry.RelativePath;
        var localByPath = local.Entries.Values.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteByPath = cloud.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remapped = rules.Select(rule =>
        {
            var stable = rule.StableFolderIdentity;
            if (stable is null)
            {
                if (remoteByPath.TryGetValue(rule.FolderPath, out var remoteFolder) && remoteFolder.Id is not null)
                    stable = "dropbox:" + remoteFolder.Id;
                else if (localByPath.TryGetValue(rule.FolderPath, out var localFolder) &&
                         localFolder.VolumeSerialNumber is not null && localFolder.FileId is not null)
                    stable = LocalSelectionIdentity(localFolder);
            }
            var path = stable is not null && identities.TryGetValue(stable, out var currentPath)
                ? currentPath
                : rule.FolderPath;
            return new SyncSelectionRule(path, rule.Included, stable);
        }).ToArray();
        var normalized = SyncSelectionPolicy.Normalize(remapped);
        if (normalized.SequenceEqual(rules)) return pair;
        try { await EnsurePairConfigurationMutableAsync(pair.Id, cancellationToken); }
        catch (InvalidOperationException) { return pair; }
        var updated = pair with
        {
            SelectionRules = normalized,
            PolicyRevision = checked(pair.PolicyRevision + 1)
        };
        var selected = string.Equals((await _authority.LoadPairSetAsync(cancellationToken)).SelectedPairId,
            pair.Id, StringComparison.Ordinal);
        var remapPolicy = await RegisterPairPolicyAsync(updated, cancellationToken, select: selected);
        if (!remapPolicy.DefinitionCommitted)
            await _authority.RetainPairDefinitionAsync(updated, selected, cancellationToken);
        await _authority.UpdatePathIndexSelectionAsync(pair.Id,
            SyncSelectionPolicy.Compile(updated), cancellationToken);
        return updated;
    }

    private static string LocalSelectionIdentity(PilotLocalEntry entry) =>
        $"local:{entry.VolumeSerialNumber!.Value:x8}:{entry.FileId!.Value:x16}";

    private DropboxPilotClient CreateDropboxClient(SyncPair pair, DropboxConnectionBinding connection) =>
        new(httpClient, AccountTokenSource(connection),
            pair.DropboxRoot, connection, pair.DropboxFolderId, _transferLimiter);

    private static string ConnectionDisplay(DropboxConnectionBinding connection) =>
        "Dropbox · " + (string.IsNullOrWhiteSpace(connection.Email)
            ? string.IsNullOrWhiteSpace(connection.DisplayName) ? connection.ConnectionId : connection.DisplayName
            : connection.Email);

    private static Uri BuildDropboxFolderUrl(string dropboxRoot)
    {
        var root = PilotPath.NormalizeDropboxRoot(dropboxRoot);
        var encoded = string.Join('/', root.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return new Uri("https://www.dropbox.com/home" + (encoded.Length == 0 ? string.Empty : "/" + encoded));
    }

    private static IReadOnlyList<SyncRewindPreviewItem> BuildRewindPreview(
        IReadOnlyDictionary<string, SyncAuthorityObservedEntry> localBefore,
        IReadOnlyDictionary<string, SyncAuthorityObservedEntry> remoteBefore,
        IReadOnlyDictionary<string, SyncAuthorityObservedEntry> localAfter,
        IReadOnlyDictionary<string, SyncAuthorityObservedEntry> remoteAfter)
    {
        var rows = new List<SyncRewindPreviewItem>();
        foreach (var path in remoteBefore.Keys.Concat(remoteAfter.Keys).Concat(localBefore.Keys).Concat(localAfter.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            remoteBefore.TryGetValue(path, out var priorDropbox);
            remoteAfter.TryGetValue(path, out var currentDropbox);
            if (EquivalentObserved(priorDropbox, currentDropbox)) continue;
            localBefore.TryGetValue(path, out var priorLocal);
            localAfter.TryGetValue(path, out var currentLocal);
            var localChanged = !EquivalentObserved(priorLocal, currentLocal);
            string group;
            string effect;
            string reason;
            if (EquivalentObserved(currentLocal, currentDropbox))
                (group, effect, reason) = ("Already converged", "Accept as the new sync baseline",
                    "Both sides now contain the same object content; no replacement is needed.");
            else if (localChanged)
                (group, effect, reason) = ("Local work also changed", "Create conflict; no automatic replacement",
                    "Local work changed after the checkpoint and is preserved for explicit review.");
            else if (currentDropbox is null)
                (group, effect, reason) = ("Rewind removed from Dropbox", "Retain local then remove from pair",
                    "Dropbox removed this path during the rollback and local content is unchanged since checkpoint.");
            else if (currentLocal is null)
                (group, effect, reason) = ("Rewind restored in Dropbox", "Create on this PC",
                    "Dropbox restored this path and no post-checkpoint local object occupies it.");
            else
                (group, effect, reason) = ("Rewind changed Dropbox", "Retain local then replace from Dropbox",
                    "Dropbox changed this path during rollback and local content is unchanged since checkpoint.");
            rows.Add(new SyncRewindPreviewItem(path, group, effect, true, localChanged, reason));
        }
        return rows;
    }

    private static bool EquivalentObservationSets(IReadOnlyList<SyncAuthorityObservedEntry> left,
        IReadOnlyList<SyncAuthorityObservedEntry> right)
    {
        if (left.Count != right.Count) return false;
        var rightByPath = right.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        return left.All(item => rightByPath.TryGetValue(item.RelativePath, out var candidate) &&
                                 EquivalentObserved(item, candidate));
    }

    private static bool EquivalentProviderObservationSets(IReadOnlyList<SyncAuthorityObservedEntry> left,
        IReadOnlyList<SyncAuthorityObservedEntry> right)
    {
        if (left.Count != right.Count) return false;
        var rightByPath = right.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        return left.All(item => rightByPath.TryGetValue(item.RelativePath, out var candidate) &&
                                EquivalentProviderObserved(item, candidate));
    }

    private static bool EquivalentProviderObserved(SyncAuthorityObservedEntry? left,
        SyncAuthorityObservedEntry? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return string.Equals(left.ObjectKind, right.ObjectKind, StringComparison.Ordinal) &&
               string.Equals(left.ObjectId, right.ObjectId, StringComparison.Ordinal) &&
               string.Equals(left.ObjectVersionId, right.ObjectVersionId, StringComparison.Ordinal) &&
               string.Equals(left.RootBindingId, right.RootBindingId, StringComparison.Ordinal) &&
               string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase) &&
               left.Size == right.Size;
    }

    private static bool EquivalentObserved(SyncAuthorityObservedEntry? left, SyncAuthorityObservedEntry? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return string.Equals(left.ObjectKind, right.ObjectKind, StringComparison.Ordinal) &&
               string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase) &&
               left.Size == right.Size;
    }

    private static string? JsonString(string json, params string[] names)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static string LocalRecoveryHealth(SyncPair pair, string location, long expectedSize)
    {
        try
        {
            if (pair.ControlNamespace is null) return "Blocked: local control namespace is unavailable";
            var path = Path.GetFullPath(location);
            var control = Path.GetFullPath(pair.ControlNamespace.Local.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(control, StringComparison.OrdinalIgnoreCase))
                return "Blocked: recorded archive is outside the bound local control area";
            if (!File.Exists(path)) return "Blocked: local archive is unavailable";
            return new FileInfo(path).Length == expectedSize
                ? "Available; content hash is verified before restore"
                : "Blocked: local archive size changed";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Blocked: " + exception.Message;
        }
    }

    private static string LocalDirectoryRecoveryHealth(SyncPair pair, string location)
    {
        try
        {
            if (pair.ControlNamespace is null) return "Blocked: local control namespace is unavailable";
            var path = Path.GetFullPath(location);
            var control = Path.GetFullPath(pair.ControlNamespace.Local.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(control, StringComparison.OrdinalIgnoreCase))
                return "Blocked: recorded folder archive is outside the bound local control area";
            if (!Directory.Exists(path) || File.Exists(path))
                return "Blocked: protected folder archive is unavailable";
            using var archive = PilotDirectoryLease.Open(path);
            return "Available; the protected folder identity and manifest are verified before restore";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Blocked: " + exception.Message;
        }
    }

    private async Task<string> FindAvailableRecoveryCopyPathAsync(SyncPair pair, string originalPath,
        SyncRecoverySource source, CancellationToken cancellationToken)
    {
        var path = PilotPath.NormalizeRelative(originalPath);
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : path[..slash];
        var name = slash < 0 ? path : path[(slash + 1)..];
        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        var prefix = $"{stem} (recovered {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss})";
        HashSet<string>? occupied = null;
        if (source != SyncRecoverySource.LocalArchive)
            occupied = (await (await CreateRemoteStoreForPairAsync(pair, cancellationToken))
                    .ListEntriesAsync(cancellationToken))
                .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var sequence = 0; sequence < 10_000; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = sequence == 0 ? string.Empty : $" {sequence}";
            var candidateName = prefix + suffix + extension;
            var candidate = parent.Length == 0 ? candidateName : parent + "/" + candidateName;
            var available = occupied is not null
                ? !occupied.Contains(candidate)
                : !File.Exists(PilotPath.ResolveContainedPath(pair.LocalRoot, candidate)) &&
                  !Directory.Exists(PilotPath.ResolveContainedPath(pair.LocalRoot, candidate));
            if (available) return candidate;
        }
        throw new IOException("No collision-free recovered-copy name is available.");
    }

    internal Func<string, string, Task>? ManualLocalRestorePhaseForTest { get; set; }

    private sealed record ManualLocalRestoreCheckpoint(int Version, string CommandId, string PairId,
        long PolicyRevision, string Provider, string RecoveryArtifactId, string SourceArchivePath,
        PilotDirectoryIdentity SourceIdentity, long ExpectedSize, string ExpectedHash,
        string RootPath, PilotDirectoryIdentity RootIdentity, string ControlPath,
        PilotDirectoryIdentity ControlIdentity, string TargetRelativePath, string TargetPath,
        string ParentPath, PilotDirectoryIdentity ParentIdentity, string StagePath,
        PilotDirectoryIdentity? StageIdentity, string Phase, string? PreimageRecoveryArtifactId = null);

    private static string ManualLocalRestoreSuffix(string commandId) =>
        commandId.StartsWith("command-", StringComparison.Ordinal) && commandId.Length == 40 &&
        Guid.TryParseExact(commandId[8..], "N", out _) ? commandId[8..]
            : throw new InvalidDataException("The retained manual restore command has no canonical stage identity.");

    private Task CheckpointManualLocalRestoreAsync(ManualLocalRestoreCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        _authority.CheckpointExecutingCommandResultAsync(checkpoint.CommandId,
            JsonSerializer.Serialize(new { manualLocalRestore = checkpoint }), cancellationToken);

    private async Task<string> RestoreLocalArchiveAsync(SyncPair pair, SyncRecoveryCatalogItem requested,
        string targetRelativePath, string commandId, CancellationToken cancellationToken)
    {
        var artifact = await _authority.LoadManualLocalRestoreSourceAsync(commandId, cancellationToken)
                       ?? throw new InvalidOperationException("The selected local recovery artifact is no longer retained.");
        if (artifact.RecoveryArtifactId != requested.Key || artifact.PairId != pair.Id ||
            artifact.Status is not ("retained" or "verified"))
            throw new InvalidOperationException("The selected local recovery artifact is not this exact command's retained source.");
        if (string.Equals(JsonString(artifact.IdentityJson, "kind"), "directory",
                StringComparison.OrdinalIgnoreCase))
            return await RestoreLocalDirectoryArtifactAsync(pair, artifact, targetRelativePath, commandId,
                cancellationToken);
        var archivePath = JsonString(artifact.IdentityJson, "path", "RecoveryPath")
                          ?? throw new InvalidDataException("The local recovery path is absent.");
        if (pair.ControlNamespace is null)
            throw new InvalidOperationException("The pair has no bound local recovery control area.");
        using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        using var controlRoot = SyncControlNamespace.OpenLocal(pair.ControlNamespace, selectedRoot);
        var exactArchive = Path.GetFullPath(archivePath);
        var controlPrefix = controlRoot.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!exactArchive.StartsWith(controlPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(exactArchive))
            throw new IOException("The exact protected local recovery object is unavailable.");
        await using var archive = SyncLocalFileAuthority.OpenExistingExclusive(exactArchive);
        var sourceIdentity = SyncLocalRecovery.ParseSourceIdentity(JsonString(artifact.IdentityJson, "identity")
            ?? throw new InvalidDataException("The retained local source has no original native identity."));
        archive.VerifySameSingleLink(sourceIdentity);
        if (archive.Stream.Length != artifact.Size)
            throw new IOException("The local recovery object length changed.");
        archive.Stream.Position = 0;
        var archiveHash = await SyncContentIdentity.ComputeAsync(pair.Provider, archive.Stream, cancellationToken);
        if (!string.Equals(archiveHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local recovery object bytes changed.");

        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, targetRelativePath);
        if (Directory.Exists(targetPath))
            throw new IOException("A directory occupies the local restore destination.");
        var normalizedTarget = PilotPath.NormalizeRelative(targetRelativePath);
        var separator = normalizedTarget.LastIndexOf('/');
        using var parent = separator < 0 ? null :
            PilotDirectoryLease.OpenOrCreateDescendant(selectedRoot, normalizedTarget[..separator]);
        // Validate and hold the real parent before creating a restore stage.
        // A foreign junction must refuse without leaving staging/sidecar effects.
        (parent ?? selectedRoot).VerifyBinding();
        var operationId = ManualLocalRestoreSuffix(commandId);
        var stagePath = Path.Combine(controlRoot.Path, "restore-" + operationId + ".partial");
        if (File.Exists(stagePath) || Directory.Exists(stagePath))
            throw new IOException("The local restore staging path is occupied.");
        var checkpoint = new ManualLocalRestoreCheckpoint(1, commandId, pair.Id, pair.PolicyRevision,
            pair.Provider, artifact.RecoveryArtifactId, exactArchive, archive.VerifySingleLink(),
            artifact.Size, artifact.ContentHash, selectedRoot.Path, selectedRoot.Identity,
            controlRoot.Path, controlRoot.Identity, normalizedTarget, targetPath,
            (parent ?? selectedRoot).Path, (parent ?? selectedRoot).Identity, stagePath, null, "preparing");
        await CheckpointManualLocalRestoreAsync(checkpoint, cancellationToken);
        await using var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
        checkpoint = checkpoint with { StageIdentity = stage.VerifySingleLink(), Phase = "stage_bound" };
        await CheckpointManualLocalRestoreAsync(checkpoint, cancellationToken);
        archive.Stream.Position = 0;
        await archive.Stream.CopyToAsync(stage.CreateGuardedWriteStream(), cancellationToken);
        await stage.Stream.FlushAsync(cancellationToken);
        stage.Stream.Flush(true);
        stage.Stream.Position = 0;
        var stageHash = await SyncContentIdentity.ComputeAsync(pair.Provider, stage.Stream, cancellationToken);
        if (stage.Stream.Length != artifact.Size ||
            !string.Equals(stageHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The staged local restore differs from the retained recovery object.");

        checkpoint = checkpoint with { Phase = "stage_verified" };
        await CheckpointManualLocalRestoreAsync(checkpoint, cancellationToken);

        string? retainedCurrent = null;
        if (File.Exists(targetPath))
        {
            var retained = await RetainCurrentLocalFileForRestoreAsync(pair, selectedRoot, controlRoot, artifact,
                operationId, commandId, targetRelativePath, "manual_restore_preimage", cancellationToken);
            retainedCurrent = retained.RecoveryPath;
            checkpoint = checkpoint with
            { PreimageRecoveryArtifactId = Id("manual-recovery-artifact", commandId, artifact.RecoveryArtifactId) };
        }
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            throw new IOException("The local restore destination remained occupied after recovery retention.");
        checkpoint = checkpoint with { Phase = "promotion_pending" };
        await CheckpointManualLocalRestoreAsync(checkpoint, cancellationToken);
        if (ManualLocalRestorePhaseForTest is { } beforePromotion)
            await beforePromotion("before-promotion", commandId);
        stage.RenameTo(selectedRoot, targetRelativePath);
        if (ManualLocalRestorePhaseForTest is { } afterPromotion)
            await afterPromotion("after-promotion", commandId);
        stage.Stream.Position = 0;
        var committedHash = await SyncContentIdentity.ComputeAsync(pair.Provider, stage.Stream, cancellationToken);
        if (!string.Equals(committedHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stage.FinalPath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The committed local restore failed exact readback.");
        checkpoint = checkpoint with { Phase = "promoted" };
        await CheckpointManualLocalRestoreAsync(checkpoint, cancellationToken);
        return retainedCurrent is null
            ? "The protected local archive was restored without displacing another file."
            : "The prior destination was retained at " + retainedCurrent + " before the protected local archive was restored.";
    }

    private async Task<SyncAuthorityBatchResult> VerifyManualLocalRestoreAccessAsync(string commandId,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, CancellationToken cancellationToken,
        Action<string>? onReadProgress)
    {
        // Share existing run/mutation exclusion, but do not create a new run/restore command or transfer.
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            stateAuthority.VerifyAuthority();
            var command = await _authority.LoadManualLocalRestoreCommandAsync(commandId, cancellationToken)
                ?? throw new InvalidDataException("The exact manual restore command is no longer retained.");
            // SS-D11: the exact ordinal pair read replaces the whole-set read; the same missing-pair
            // refusal is preserved for a pair that is not retained.
            var pair = await _authority.LoadPairDefinitionAsync(command.PairId, cancellationToken)
                ?? throw new InvalidDataException("The original manual restore pair is no longer retained.");
            using var request = JsonDocument.Parse(command.RequestJson);
            var target = JsonString(command.RequestJson, "TargetRelativePath")
                ?? throw new InvalidDataException("The manual restore has no original destination.");
            target = PilotPath.NormalizeRelative(target);
            string detail;
            try
            {
                if (command.Status != "blocked")
                    throw new InvalidOperationException("Only an unresolved blocked manual restore can have interrupted access verified. Completed effects are never replayed.");
                if (command.ResultJson is null)
                    throw new InvalidDataException("Legacy manual restore has no corroborated original root/stage checkpoint. Access and restore completion remain unresolved.");
                using var result = JsonDocument.Parse(command.ResultJson);
                if (!result.RootElement.TryGetProperty("manualLocalRestore", out var retained))
                    throw new InvalidDataException("Legacy manual restore has no corroborated original root/stage checkpoint. Current pair settings are not historical authority.");
                var checkpoint = retained.Deserialize<ManualLocalRestoreCheckpoint>()
                    ?? throw new InvalidDataException("The manual restore checkpoint is unreadable.");
                var expectedStage = checkpoint.StageIdentity
                    ?? throw new InvalidDataException("The stage native identity was not durably bound. No access or restore effect was issued by verification.");
                var requestedPairs = request.RootElement.GetProperty("pairIds").Deserialize<string[]>() ?? [];
                if (checkpoint.Version != 1 || checkpoint.CommandId != commandId || checkpoint.PairId != command.PairId ||
                    requestedPairs.Length != 1 || requestedPairs[0] != command.PairId ||
                    checkpoint.RecoveryArtifactId != command.RecoveryArtifactId ||
                    checkpoint.PolicyRevision != pair.PolicyRevision || checkpoint.Provider != pair.Provider ||
                    checkpoint.TargetRelativePath != target || checkpoint.ExpectedSize < 0 ||
                    checkpoint.SourceIdentity is null || checkpoint.RootIdentity is null ||
                    checkpoint.ControlIdentity is null || checkpoint.ParentIdentity is null ||
                    string.IsNullOrWhiteSpace(checkpoint.SourceArchivePath) ||
                    string.IsNullOrWhiteSpace(checkpoint.RootPath) || string.IsNullOrWhiteSpace(checkpoint.ControlPath) ||
                    string.IsNullOrWhiteSpace(checkpoint.ParentPath) || string.IsNullOrWhiteSpace(checkpoint.StagePath) ||
                    string.IsNullOrWhiteSpace(checkpoint.TargetPath) || string.IsNullOrWhiteSpace(checkpoint.ExpectedHash) ||
                    checkpoint.Phase is not ("stage_bound" or "stage_verified" or "promotion_pending" or "promoted") ||
                    checkpoint.RootIdentity != new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId) ||
                    !string.Equals(checkpoint.RootPath, Path.GetFullPath(pair.LocalRoot), StringComparison.OrdinalIgnoreCase) ||
                    pair.ControlNamespace is null ||
                    !string.Equals(checkpoint.ControlPath, pair.ControlNamespace.Local.RootPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The original manual command, source, target or pair/root binding is not corroborated. Nothing was restored or replayed.");
                using var root = PilotDirectoryLease.Open(checkpoint.RootPath, checkpoint.RootIdentity);
                using var control = SyncControlNamespace.OpenLocal(pair.ControlNamespace, root);
                var stagePath = Path.Combine(control.Path, "restore-" + ManualLocalRestoreSuffix(commandId) + ".partial");
                var targetPath = PilotPath.ResolveContainedPath(root.Path, target);
                var parentPath = Path.GetDirectoryName(targetPath)!;
                if (control.Identity != checkpoint.ControlIdentity ||
                    !string.Equals(stagePath, checkpoint.StagePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(targetPath, checkpoint.TargetPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(parentPath, checkpoint.ParentPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The original control/stage/destination authority changed. Access remains unresolved.");
                using var parent = PilotDirectoryLease.Open(parentPath, checkpoint.ParentIdentity);
                var artifact = await _authority.LoadManualLocalRestoreSourceAsync(commandId, cancellationToken)
                    ?? throw new IOException("The exact original retained local source is unavailable; no access effect was issued.");
                if (artifact.Status is not ("retained" or "verified"))
                    throw new IOException("The exact original local source is no longer retained; no access effect was issued.");
                var archivePath = JsonString(artifact.IdentityJson, "path", "RecoveryPath");
                var sourceIdentityText = JsonString(artifact.IdentityJson, "identity")
                    ?? throw new IOException("The retained source's original native identity is absent.");
                var prefix = control.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (archivePath is null || artifact.Size != checkpoint.ExpectedSize ||
                    SyncLocalRecovery.ParseSourceIdentity(sourceIdentityText) != checkpoint.SourceIdentity ||
                    !string.Equals(artifact.ContentHash, checkpoint.ExpectedHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFullPath(archivePath), checkpoint.SourceArchivePath, StringComparison.OrdinalIgnoreCase) ||
                    !checkpoint.SourceArchivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(JsonString(artifact.IdentityJson, "kind"), "directory", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The retained source does not match the original regular-file restore checkpoint.");
                await using var archive = SyncLocalFileAuthority.OpenExistingExclusive(checkpoint.SourceArchivePath);
                archive.VerifySameSingleLink(checkpoint.SourceIdentity);
                archive.Stream.Position = 0;
                if (archive.Stream.Length != checkpoint.ExpectedSize ||
                    !string.Equals(await SyncContentIdentity.ComputeAsync(pair.Provider, archive.Stream, cancellationToken),
                        checkpoint.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The original retained source bytes changed; no access effect was issued.");
                root.VerifyBinding(); control.VerifyBinding(); parent.VerifyBinding();
                if (!File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(stagePath)))
                {
                    detail = "The original access envelope is absent. Access may already have been repaired, but absence does not prove restore completion or no effect. The command and source/preimage stay unresolved and protected; nothing was replayed.";
                }
                else
                {
                    onReadProgress?.Invoke("Verifying the exact manual command's interrupted access envelope; no copy, promotion or restore command is being issued.");
                    // The expected-identity overload must reject a changed envelope BEFORE changing any ACL.
                    var repaired = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath,
                        expectedStage, stagePath, targetPath);
                    if (repaired != expectedStage)
                        throw new IOException("The exact command-bound stage identity was not restored.");
                    var restoredPath = File.Exists(stagePath) ? stagePath : targetPath;
                    await using var restored = SyncLocalFileAuthority.OpenExistingExclusive(restoredPath);
                    restored.VerifySameSingleLink(expectedStage);
                    if (!string.Equals(restored.FinalPath, restoredPath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The access-repaired file is no longer at an original prebound candidate.");
                    restored.Stream.Position = 0;
                    var hash = await SyncContentIdentity.ComputeAsync(pair.Provider, restored.Stream, cancellationToken);
                    var exactBytes = restored.Stream.Length == checkpoint.ExpectedSize &&
                                     string.Equals(hash, checkpoint.ExpectedHash, StringComparison.OrdinalIgnoreCase);
                    root.VerifyBinding(); control.VerifyBinding(); parent.VerifyBinding();
                    await _authority.RecordManualLocalRestoreAccessAsync(commandId, JsonSerializer.Serialize(new
                    {
                        accessRestored = true, restoreCompleted = false,
                        location = restoredPath == stagePath ? "stage" : "target",
                        identity = expectedStage, exactBytes, occurredUtc = DateTimeOffset.UtcNow
                    }), CancellationToken.None);
                    detail = exactBytes
                        ? "The exact interrupted manual file's access was restored and its retained bytes verified. Restore completion remains unresolved; source and displaced preimage stay protected. No copy, promotion or new restore command was issued."
                        : "The exact interrupted stage's access was restored, but its bytes are incomplete or changed. They were retained, never promoted or blessed as a complete restore. The command/source/preimage remain unresolved.";
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                           InvalidOperationException or JsonException or KeyNotFoundException)
            {
                detail = "Manual restore access remains unresolved: " + SyncActivityText.Failure(error) +
                         " Source, preimage and command remain protected; no restore was completed or replayed.";
            }
            // Existing CLI verification returns a bounded summary and points to the original run log.
            // Retain the precise qualified reason there; access recovery does not create a new run.
            await _authority.AppendDiagnosticActivityAsync(new SyncActivityEvent("",
                DateTimeOffset.UtcNow, [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics], "Warning",
                "Run", "Manual restore access verification", detail, pair.ConnectionId, pair.Id,
                RelativePath: target, RelatedRunId: commandId), CancellationToken.None);
            var outcome = new SyncRunResult(pair.Id,
                [new SyncDecision(target, SyncDecisionKind.Blocked, detail) { NotAttempted = true }]);
            if (onProgress is not null)
                await onProgress(new SyncAuthorityBatchProgress(pair, "Needs attention", outcome, null, null));
            return new SyncAuthorityBatchResult([new SyncAuthorityBatchItemResult(pair, outcome, null, null)], false);
        }
        finally { _runGate.Release(); }
    }

    private async Task<string> RestoreLocalDirectoryArtifactAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, string targetRelativePath, string commandId,
        CancellationToken cancellationToken)
    {
        if (pair.ControlNamespace is null)
            throw new InvalidOperationException("The pair has no bound local recovery control area.");
        var recipe = JsonString(artifact.IdentityJson, "recipe");
        var recipeOnly = string.Equals(recipe, "recreate_empty_directory", StringComparison.Ordinal);
        using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        using var controlRoot = SyncControlNamespace.OpenLocal(pair.ControlNamespace, selectedRoot);
        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, targetRelativePath);
        var operationId = commandId.StartsWith("command-", StringComparison.Ordinal)
            ? commandId["command-".Length..]
            : throw new InvalidDataException("The recovery command identity is not accepted.");

        if (Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            using var current = PilotDirectoryLease.Open(targetPath);
            selectedRoot.VerifyBinding();
            if (recipeOnly)
                return "The empty-folder recovery recipe is already satisfied by a folder at the destination.";
            var currentManifest = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider,
                targetPath, cancellationToken);
            if (currentManifest.Hash == artifact.ContentHash && currentManifest.Size == artifact.Size)
                return "The original destination already contains the retained folder manifest.";
        }

        string? retainedCurrent = null;
        if (File.Exists(targetPath))
        {
            var retained = await RetainCurrentLocalFileForRestoreAsync(pair, selectedRoot, controlRoot, artifact,
                operationId, commandId, targetRelativePath, "manual_folder_restore_preimage", cancellationToken);
            retainedCurrent = retained.RecoveryPath;
        }
        else if (Directory.Exists(targetPath))
        {
            retainedCurrent = await RetainCurrentLocalDirectoryAsync(pair, selectedRoot, controlRoot, artifact,
                targetRelativePath, targetPath, operationId, commandId, cancellationToken);
        }
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            throw new IOException("The local folder restore destination remained occupied after recovery retention.");

        if (recipeOnly)
        {
            using var created = PilotDirectoryLease.CreateDescendant(selectedRoot, targetRelativePath);
            selectedRoot.VerifyBinding();
            if (!string.Equals(created.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recovered empty folder did not reach its exact destination.");
            return retainedCurrent is null
                ? "The retained empty-folder recipe recreated the folder at its original path."
                : "The prior destination was retained at " + retainedCurrent + " before the folder was recreated.";
        }

        var archivePath = JsonString(artifact.IdentityJson, "path", "RecoveryPath")
                          ?? throw new InvalidDataException("The protected folder archive path is absent.");
        var archiveIdentity = JsonString(artifact.IdentityJson, "identity")
                              ?? throw new InvalidDataException("The protected folder archive identity is absent.");
        var exactArchive = Path.GetFullPath(archivePath);
        var controlPrefix = controlRoot.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!exactArchive.StartsWith(controlPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(exactArchive) || File.Exists(exactArchive))
            throw new IOException("The exact protected folder archive is unavailable.");
        using var archive = PilotDirectoryLease.OpenOwnedDirectory(exactArchive,
            SyncLocalRecovery.ParseSourceIdentity(archiveIdentity));
        var archiveManifest = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider,
            exactArchive, cancellationToken);
        if (archiveManifest.Hash != artifact.ContentHash || archiveManifest.Size != artifact.Size)
            throw new IOException("The protected folder archive manifest changed.");

        var stageName = "restore-" + operationId + ".partial";
        var stagePath = Path.Combine(controlRoot.Path, stageName);
        if (File.Exists(stagePath) || Directory.Exists(stagePath))
            throw new IOException("The local folder restore staging path is occupied.");
        using var stage = PilotDirectoryLease.CreateChild(controlRoot, stageName);
        await SyncAuthorityCrossPairMoveCoordinator.CopyDirectoryTreeAsync(exactArchive, stage.Path,
            cancellationToken);
        var stageManifest = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider, stage.Path,
            cancellationToken);
        var archiveAgain = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider, exactArchive,
            cancellationToken);
        if (stageManifest != archiveManifest || archiveAgain != archiveManifest)
            throw new IOException("The staged folder restore failed exact manifest verification.");
        stage.RenameTo(selectedRoot, targetRelativePath);
        selectedRoot.VerifyBinding();
        if (!string.Equals(stage.Path, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The recovered folder did not reach its exact destination.");
        return retainedCurrent is null
            ? "The protected folder archive was restored without displacing another object."
            : "The prior destination was retained at " + retainedCurrent + " before the folder archive was restored.";
    }

    private Task<SyncLocalArchiveResult> RetainCurrentLocalFileForRestoreAsync(SyncPair pair,
        PilotDirectoryLease selectedRoot, PilotDirectoryLease controlRoot,
        SyncRecoveryArtifactProjection sourceArtifact, string operationId, string commandId,
        string targetRelativePath, string provenance, CancellationToken cancellationToken) =>
        SyncLocalRecovery.ArchiveForManualRestoreAsync(selectedRoot, controlRoot, pair.Id, operationId,
            targetRelativePath, pair.Provider, async (retained, token) =>
            {
                var retainedJson = JsonSerializer.Serialize(new
                {
                    path = retained.RecoveryPath,
                    identity = $"{retained.VolumeSerialNumber:x8}:{retained.FileId:x16}",
                    size = retained.Size,
                    contentHash = retained.ContentHash,
                    originalPath = targetRelativePath,
                    provenance,
                    sourceRecoveryArtifactId = sourceArtifact.RecoveryArtifactId
                });
                await _authority.RegisterRecoveryArtifactAsync(
                    Id("manual-recovery-artifact", commandId, sourceArtifact.RecoveryArtifactId),
                    sourceArtifact.OperationId, sourceArtifact.ObjectId, null, "local", retainedJson,
                    retained.ContentHash, retained.Size, token);
            }, cancellationToken);

    private async Task<string> RetainCurrentLocalDirectoryAsync(SyncPair pair, PilotDirectoryLease selectedRoot,
        PilotDirectoryLease controlRoot, SyncRecoveryArtifactProjection sourceArtifact, string targetRelativePath,
        string targetPath, string operationId, string commandId, CancellationToken cancellationToken)
    {
        var recoveryPath = SyncLocalRecovery.BuildRecoveryPath(controlRoot, pair.Id, operationId);
        using var recoveryRoot = PilotDirectoryLease.OpenOrCreateDescendant(controlRoot,
            SyncLocalRecovery.RecoveryFolderPrefix + pair.Id);
        if (File.Exists(recoveryPath) || Directory.Exists(recoveryPath))
            throw new IOException("The manual folder-recovery destination is occupied.");
        var before = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider, targetPath,
            cancellationToken);
        using var current = PilotDirectoryLease.Open(targetPath);
        var identity = current.Identity;
        current.RenameTo(recoveryRoot, operationId + ".recovery");
        selectedRoot.VerifyBinding();
        controlRoot.VerifyBinding();
        if (!string.Equals(current.Path, recoveryPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The displaced folder did not reach its protected recovery destination.");
        var after = await SyncAuthorityCrossPairMoveCoordinator.BuildDirectoryManifestAsync(pair.Provider, recoveryPath,
            cancellationToken);
        if (after != before)
            throw new IOException("The displaced folder changed while being retained for recovery.");
        var retainedJson = JsonSerializer.Serialize(new
        {
            path = recoveryPath,
            identity = $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}",
            kind = "directory",
            size = after.Size,
            contentHash = after.Hash,
            originalPath = targetRelativePath,
            provenance = "manual_folder_restore_preimage",
            sourceRecoveryArtifactId = sourceArtifact.RecoveryArtifactId
        });
        await _authority.RegisterRecoveryArtifactAsync(
            Id("manual-recovery-artifact", commandId, sourceArtifact.RecoveryArtifactId),
            sourceArtifact.OperationId, sourceArtifact.ObjectId, null, "local", retainedJson, after.Hash,
            after.Size, cancellationToken);
        return recoveryPath;
    }

    internal async Task<string> RestoreDropboxArchiveAsync(SyncPair pair, SyncRecoveryCatalogItem requested,
        string targetRelativePath, string commandId, CancellationToken cancellationToken)
    {
        SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        // SS-D11: the pair, the artifact and the requested side are bound in one exact read, so no other
        // pair's artifact rows are hydrated. The resolved projection is identical to the previous global
        // read of the same row, and a zero result keeps the same missing-artifact refusal.
        var artifact = await _authority.LoadRecoveryArtifactAsync(pair.Id, requested.Key, "dropbox",
                           cancellationToken)
                       ?? throw new InvalidOperationException("The selected Dropbox recovery artifact is no longer retained.");
        if (string.Equals(JsonString(artifact.IdentityJson, "kind"), "directory",
                StringComparison.OrdinalIgnoreCase))
            return await RestoreDropboxDirectoryRecipeAsync(pair, artifact, targetRelativePath, commandId,
                cancellationToken);
        var expectedArchive = RecoveryArchiveFromArtifact(artifact);
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
        await remote.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
        var retainedCopy = await GetBoundRecoveryAsync(pair, remote, expectedArchive.RecoveryPath, cancellationToken);
        if (retainedCopy is null || !SameRecoveryArchive(retainedCopy, expectedArchive))
            throw new IOException("The selected recovery copy changed or moved. No destination was changed.");
        var current = (await remote.ListEntriesAsync(cancellationToken)).SingleOrDefault(item =>
            string.Equals(item.RelativePath, targetRelativePath, StringComparison.OrdinalIgnoreCase));
        string? retainedCurrent = null;
        if (current is not null)
        {
            if (current.Kind != PilotEntryKind.File)
                throw new IOException("A Dropbox folder occupies the restore destination.");
            if (string.Equals(current.ContentHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase) &&
                current.Size == artifact.Size)
                return "The original Dropbox destination already contains the retained recovery bytes.";
            var operationId = commandId["command-".Length..];
            var retained = await remote.CopyToRecoveryAsync(current, recoveryRoot,
                "restore-current-" + operationId + ".recovery", cancellationToken);
            retainedCurrent = retained.RecoveryPath;
            var retainedJson = JsonSerializer.Serialize(new
            {
                retained.FileId,
                retained.Revision,
                retained.RecoveryPath,
                retained.Size,
                retained.ContentHash,
                originalPath = targetRelativePath,
                provenance = "manual_restore_preimage",
                sourceRecoveryArtifactId = artifact.RecoveryArtifactId
            });
            await _authority.RegisterRecoveryArtifactAsync(
                Id("manual-recovery-artifact", commandId, artifact.RecoveryArtifactId), artifact.OperationId,
                artifact.ObjectId, null, "dropbox", retainedJson, retained.ContentHash, retained.Size,
                cancellationToken);
            await remote.DeleteAsync(current, cancellationToken);
        }
        var restored = await remote.CopyRecoveryToPathAsync(recoveryRoot, expectedArchive, targetRelativePath,
            cancellationToken);
        if (restored.Size != artifact.Size ||
            !string.Equals(restored.ContentHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The restored Dropbox recovery object failed exact readback.");
        return retainedCurrent is null
            ? "The Dropbox Recovery copy was restored without displacing another object."
            : "The prior Dropbox destination was retained at " + retainedCurrent + " before restore.";
    }

    internal async Task<string> RestoreDropboxDirectoryRecipeAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, string targetRelativePath, string commandId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(JsonString(artifact.IdentityJson, "recipe"), "recreate_empty_directory",
                StringComparison.Ordinal))
            throw new InvalidDataException("This Dropbox folder recovery artifact has no accepted restore recipe.");
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var remote = await CreateRemoteStoreForPairAsync(pair, cancellationToken);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        await remote.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
        var entries = (await remote.ListEntriesAsync(cancellationToken)).ToList();
        var target = entries.SingleOrDefault(item =>
            string.Equals(item.RelativePath, targetRelativePath, StringComparison.OrdinalIgnoreCase));
        if (target?.Kind == PilotEntryKind.Directory)
            return "The empty-folder recovery recipe is already satisfied by a Dropbox folder at the destination.";
        string? retainedCurrent = null;
        if (target is not null)
        {
            if (target.Kind != PilotEntryKind.File)
                throw new IOException("An unsupported Dropbox object occupies the folder restore destination.");
            var operationId = commandId.StartsWith("command-", StringComparison.Ordinal)
                ? commandId["command-".Length..]
                : throw new InvalidDataException("The recovery command identity is not accepted.");
            var retained = await remote.CopyToRecoveryAsync(target, recoveryRoot,
                "restore-current-" + operationId + ".recovery", cancellationToken);
            var retainedJson = JsonSerializer.Serialize(new
            {
                retained.FileId,
                retained.Revision,
                retained.RecoveryPath,
                retained.Size,
                retained.ContentHash,
                originalPath = targetRelativePath,
                provenance = "manual_folder_restore_preimage",
                sourceRecoveryArtifactId = artifact.RecoveryArtifactId
            });
            await _authority.RegisterRecoveryArtifactAsync(
                Id("manual-recovery-artifact", commandId, artifact.RecoveryArtifactId), artifact.OperationId,
                artifact.ObjectId, null, "dropbox", retainedJson, retained.ContentHash, retained.Size,
                cancellationToken);
            await remote.DeleteAsync(target, cancellationToken);
            entries.Remove(target);
            retainedCurrent = retained.RecoveryPath;
        }

        var segments = PilotPath.NormalizeRelative(targetRelativePath).Split('/');
        var path = string.Empty;
        PilotRemoteEntry? createdTarget = null;
        foreach (var segment in segments)
        {
            path = path.Length == 0 ? segment : path + "/" + segment;
            var existing = entries.SingleOrDefault(item =>
                string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.Kind != PilotEntryKind.Directory)
                    throw new IOException("A Dropbox file occupies a required parent of the folder restore destination.");
                createdTarget = existing;
                continue;
            }
            var created = await remote.CreateDirectoryAsync(path, cancellationToken);
            if (created.Kind != PilotEntryKind.Directory || string.IsNullOrWhiteSpace(created.Id) ||
                !string.Equals(created.RelativePath, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Dropbox did not verify the created recovery folder at its exact path.");
            entries.Add(created);
            createdTarget = created;
        }
        if (createdTarget is null ||
            !string.Equals(createdTarget.RelativePath, targetRelativePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Dropbox recovery folder was not created at its exact destination.");
        await remote.VerifyRootIdentityAsync(cancellationToken);
        return retainedCurrent is null
            ? "The retained empty-folder recipe recreated the Dropbox folder at its original path."
            : "The prior Dropbox file was retained at " + retainedCurrent + " before the folder was recreated.";
    }

    internal async Task<string> RestoreDropboxRevisionAsync(SyncPair pair, SyncRecoveryCatalogItem requested,
        string targetRelativePath, bool restoreAsCopy, string commandId, CancellationToken cancellationToken)
    {
        if (IsGooglePair(pair))
            return await RestoreGoogleRevisionAsync(pair, requested, targetRelativePath, restoreAsCopy, commandId, cancellationToken);
        SyncDropboxArtifactPaths.ValidateBoundRecoveryContainerIfPresent(pair);
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        var remote = CreateDropboxClient(pair, connection);
        var exact = (await remote.ListRevisionsAsync(requested.RelativePath, cancellationToken)).SingleOrDefault(item =>
                        string.Equals(item.Revision, requested.Revision, StringComparison.Ordinal) &&
                        string.Equals(item.FileId, requested.Identity, StringComparison.Ordinal) &&
                        item.Size == requested.Size &&
                        string.Equals(item.ContentHash, requested.ContentHash, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The selected Dropbox revision is no longer available with the recorded identity.");
        var revisionEntry = new PilotRemoteEntry(exact.RelativePath, PilotEntryKind.File, exact.FileId,
            exact.Revision, exact.Size, exact.ContentHash, exact.ModifiedUtc);
        var tempPath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot,
            "revision-restore-" + commandId["command-".Length..] + ".partial");
        return await RestoreJournalledRevisionAsync(pair, requested, targetRelativePath, restoreAsCopy,
            commandId, remote, async (consume, retain, token) =>
            {
                Directory.CreateDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
                await using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                await remote.DownloadHistoricalRevisionAsync(revisionEntry, temp, token);
                temp.Position = 0;
                return await consume(temp, exact.ContentHash!);
            }, () => !File.Exists(tempPath), cancellationToken);
    }

    public Task<IReadOnlyList<SyncAuthorityMonitorStartResult>> ConfigureMonitorsAsync(
        IEnumerable<SyncPair> pairs, Func<Task<bool>> onDebouncedRunRequested,
        Func<string, Task>? onHealthChanged = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDebouncedRunRequested);
        return ConfigureMonitorsAsync(pairs, _ => onDebouncedRunRequested(), onHealthChanged, cancellationToken);
    }

    public async Task<IReadOnlyList<SyncAuthorityMonitorStartResult>> ConfigureMonitorsAsync(
        IEnumerable<SyncPair> pairs, Func<IReadOnlySet<string>, Task<bool>> onDebouncedRunRequested,
        Func<string, Task>? onHealthChanged = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(onDebouncedRunRequested);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _monitorConfigurationGate.WaitAsync(cancellationToken);
        try
        {
            await StopMonitorsCoreAsync();
            lock (_monitorDebounceGate) _onMonitorRunRequested = onDebouncedRunRequested;
            var results = new List<SyncAuthorityMonitorStartResult>();
            var rewindPausedPairs = await _rewindStore.LoadActivePairIdsAsync(cancellationToken);
            // Keep change notification continuity while dispatch is paused. Timed pauses can then
            // resume without losing the account's watchers or rebuilding unrelated accounts.
            foreach (var pair in pairs.Where(item => item.Enabled))
            {
                var local = new List<SyncAuthorityLocalMonitor>(2);
                var cloud = new List<SyncAuthorityDropboxMonitor>(2);
                try
                {
                    if (rewindPausedPairs.Contains(pair.Id))
                    {
                        results.Add(new SyncAuthorityMonitorStartResult(pair.Id, "Paused for Rewind", null,
                            "The pair is held at a persistent Rewind checkpoint."));
                        continue;
                    }
                    var prepared = await PreparePairAsync(pair, cancellationToken);
                    var context = SyncEndpointContext.Capture(pair);
                    foreach (var side in new[] { context.Left, context.Right })
                    {
                        var localPort = side.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft;
                        var bindingId = localPort ? prepared.LocalRootBindingId : prepared.DropboxRootBindingId;
                        if (side.IsLocal)
                        {
                            if (side.LocalVolumeSerialNumber is not { } volume || side.LocalRootFileId is not { } id)
                                throw new InvalidDataException(SyncEndpointPresentation.HierarchyTitle(side) +
                                    " has no verified local folder identity for monitoring.");
                            var watcher = new SyncAuthorityLocalMonitor(pair, _authority, bindingId,
                                OnDurableMonitorHintAsync, onHealthChanged,
                                new SyncLocalEndpointBinding(side.LocalPath!, volume, id),
                                // Only the physical local inventory has the existing USN index. The second
                                // local endpoint uses the same bounded directory watcher/overflow recovery,
                                // not a volume-wide USN fallback triggered by unrelated disk activity.
                                enableUsnJournal: localPort);
                            local.Add(watcher);
                            await watcher.StartAsync(cancellationToken);
                        }
                        else
                        {
                            var store = prepared.Cloud is { } cloudPair
                                ? cloudPair.Endpoints.Ports.Single(port => port.CompatibilitySide == side.CompatibilityPort).Store
                                : prepared.Remote;
                            if (store is not ISyncRemoteChangeFeed feed)
                                throw new NotSupportedException(SyncEndpointPresentation.HierarchyTitle(side) +
                                    " does not provide automatic change notifications. Scheduled and manual checks remain available.");
                            var watcher = new SyncAuthorityDropboxMonitor(pair, store, feed, _authority,
                                bindingId, OnDurableMonitorHintAsync, onHealthChanged: onHealthChanged,
                                folderId: side.FolderId, endpointLabel: SyncEndpointPresentation.HierarchyTitle(side));
                            cloud.Add(watcher);
                            await watcher.StartAsync(cancellationToken);
                        }
                    }
                    _localMonitors.AddRange(local);
                    _dropboxMonitors.AddRange(cloud);
                    local.Clear();
                    cloud.Clear();
                    results.Add(new SyncAuthorityMonitorStartResult(pair.Id, "Monitoring", null, null));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    foreach (var watcher in cloud)
                        await StopMonitorSafelyAsync(watcher, watcher.Pair, () => watcher.LastFailureReason);
                    foreach (var watcher in local)
                        await StopMonitorSafelyAsync(watcher, watcher.Pair, () => watcher.LastFailureReason);
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var watcher in cloud)
                        await StopMonitorSafelyAsync(watcher, watcher.Pair, () => watcher.LastFailureReason);
                    foreach (var watcher in local)
                        await StopMonitorSafelyAsync(watcher, watcher.Pair, () => watcher.LastFailureReason);
                    // Configuration runs outside a transfer run. Retain its exact failure in the
                    // same operation log consumed by CLI and GUI, rather than only an ephemeral
                    // dashboard row. This presentation append cannot authorize or replay effects.
                    await RetainProgressAsync(new SyncAuthorityBatchProgress(pair,
                        "Automatic monitoring failed", null, exception.GetType().Name,
                        SyncActivityText.Failure(exception)), runId: null);
                    results.Add(new SyncAuthorityMonitorStartResult(pair.Id, "Monitor unavailable",
                        exception.GetType().Name, exception.Message));
                }
            }
            return results;
        }
        finally
        {
            _monitorConfigurationGate.Release();
        }
    }

    public async Task StopMonitorsAsync(CancellationToken cancellationToken = default)
    {
        await _monitorConfigurationGate.WaitAsync(cancellationToken);
        try
        {
            await StopMonitorsCoreAsync();
        }
        finally
        {
            _monitorConfigurationGate.Release();
        }
    }

    private Task OnDurableMonitorHintAsync(string pairId)
    {
        lock (_monitorDebounceGate)
        {
            if (_onMonitorRunRequested is null || Volatile.Read(ref _disposed) != 0)
                return Task.CompletedTask;
            _pendingMonitorPairs[pairId] = ++_monitorHintGeneration;
            if (_monitorDelivery is null || _monitorDelivery.IsCompleted)
            {
                _monitorDebounce?.Dispose();
                _monitorDebounce = new CancellationTokenSource();
                _monitorDelivery = DeliverDebouncedMonitorRunAsync(_monitorScopeGeneration, _monitorDebounce.Token);
            }
        }
        return Task.CompletedTask;
    }

    private async Task DeliverDebouncedMonitorRunAsync(long scopeGeneration, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                long observed;
                lock (_monitorDebounceGate) observed = _monitorHintGeneration;
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
                Func<IReadOnlySet<string>, Task<bool>>? callback;
                Dictionary<string, long> captured;
                lock (_monitorDebounceGate)
                {
                    if (scopeGeneration != _monitorScopeGeneration || cancellationToken.IsCancellationRequested) return;
                    if (observed != _monitorHintGeneration) continue;
                    callback = _onMonitorRunRequested;
                    captured = new Dictionary<string, long>(_pendingMonitorPairs, StringComparer.Ordinal);
                    if (callback is null || captured.Count == 0) { _monitorDelivery = null; return; }
                }
                if (!await callback(captured.Keys.ToHashSet(StringComparer.Ordinal))) continue;
                lock (_monitorDebounceGate)
                {
                    if (scopeGeneration != _monitorScopeGeneration || cancellationToken.IsCancellationRequested) return;
                    foreach (var pair in captured)
                        if (_pendingMonitorPairs.TryGetValue(pair.Key, out var current) && current == pair.Value)
                            _pendingMonitorPairs.Remove(pair.Key);
                    if (_pendingMonitorPairs.Count == 0) { _monitorDelivery = null; return; }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Durable dirty state remains in SQLite; periodic reconciliation can retry presentation failures.
        }
    }

    private async Task StopMonitorsCoreAsync()
    {
        // Invalidate queued scope, not a newer configuration's hints. Do not
        // await the UI consumer under the configuration gate: it can itself
        // reconfigure monitors. Feed pumps below are still cancelled/drained;
        // an already-running consumer cannot consume a newer scope watermark.
        lock (_monitorDebounceGate)
        {
            _onMonitorRunRequested = null;
            ++_monitorScopeGeneration;
            _pendingMonitorPairs.Clear();
            _monitorDelivery = null;
            _monitorDebounce?.Cancel();
            _monitorDebounce?.Dispose();
            _monitorDebounce = null;
        }
        foreach (var monitor in _dropboxMonitors)
            await StopMonitorSafelyAsync(monitor, monitor.Pair, () => monitor.LastFailureReason);
        _dropboxMonitors.Clear();
        foreach (var monitor in _localMonitors)
            await StopMonitorSafelyAsync(monitor, monitor.Pair, () => monitor.LastFailureReason);
        _localMonitors.Clear();
    }

    private async Task StopMonitorSafelyAsync(IAsyncDisposable monitor, SyncPair pair,
        Func<string?> lastFailure)
    {
        string? failure = null;
        try { await monitor.DisposeAsync(); }
        catch (Exception error) { failure = SyncActivityText.Failure(error); }
        failure ??= lastFailure();
        if (failure is null) return;
        // Containing one failed watcher must not abandon the remaining watchers.
        // Retain the reason in the same CLI/GUI log, including faults the pump contained.
        try
        {
            await RetainProgressAsync(new SyncAuthorityBatchProgress(pair,
                "Automatic monitoring failed", null, "monitor_failure", SyncActivityText.Safe(failure)), null);
        }
        catch (Exception error)
        {
            lock (_activityLogGate) RecordActivityLogFailure(error);
        }
    }

    private async Task<SyncRunResult> RunPairCoreAsync(SyncPair pair, string trigger, PilotRunControl runControl, SyncAuthorityCommandAdmission admission,
        IReadOnlyDictionary<string, string>? membershipObjectFences = null,
        IReadOnlyDictionary<string, string>? membershipPathFences = null,
        CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        if (NewSyncAdmissionReason(pair) is { } admissionReason)
            return new SyncRunResult(pair.Id,
                [new SyncDecision("<run>", SyncDecisionKind.Blocked, admissionReason)]);
        if (await _rewindStore.LoadActiveAsync(pair.Id, cancellationToken) is not null &&
            !string.Equals(trigger, "Rewind apply", StringComparison.Ordinal))
            return new SyncRunResult(pair.Id,
            [
                new SyncDecision("<rewind>", SyncDecisionKind.Blocked,
                    "This pair is paused at a Rewind checkpoint. Detect, preview and apply the provider rollback batch or explicitly resume before ordinary synchronization.")
            ]);
        pair = await RefreshSelectionRulePathsAsync(pair, cancellationToken, onProgress);
        var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress,
            forceReviewedDeletionEffects: string.Equals(trigger, "Rewind apply", StringComparison.Ordinal));
        try
        {
            var result = await CreatePairRunner(pair, prepared, runControl, admission, prepared.CloudFiles,
                    membershipObjectFences, membershipPathFences, onProgress: onProgress)
                .RunAsync(trigger, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (runControl.IsStopRequested)
        {
            return new SyncRunResult(pair.Id,
            [
                new SyncDecision("<run>", SyncDecisionKind.Stopped,
                    "Stop reached a cancellation boundary; every dispatched or uncertain effect remains durable for startup recovery.")
            ]);
        }
    }

    private async Task<SyncRunResult> RecoverPairCoreAsync(SyncPair pair, PilotRunControl runControl, SyncAuthorityCommandAdmission admission,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress);
        try
        {
            var recoveryRunner = CreatePairRunner(pair, prepared, runControl, admission, prepared.CloudFiles,
                    onInventory: progress => NotifyProgressAsync(onProgress,
                        new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: progress)),
                    onExecution: action => NotifyProgressAsync(onProgress,
                        new SyncAuthorityBatchProgress(pair, "Running", null, null, null, Action: action)),
                    onProgress: onProgress);
            return await recoveryRunner.RecoverAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (runControl.IsStopRequested)
        {
            return new SyncRunResult(pair.Id,
            [
                new SyncDecision("<recovery>", SyncDecisionKind.Stopped,
                    "Startup recovery stopped at a durable boundary and will resume on the next launch or run.")
            ]);
        }
    }

    private async Task<SyncRunResult> ResolveConflictCoreAsync(SyncPair pair, string relativePath, SyncAuthorityCommandAdmission admission,
        SyncAuthorityConflictResolutionKind resolutionKind, PilotRunControl runControl,
        string? expectedConflictId = null, long? expectedConflictGeneration = null,
        CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress);
        try
        {
            return await CreatePairRunner(pair, prepared, runControl, admission, prepared.CloudFiles, onProgress: onProgress)
                .ResolveConflictAsync(relativePath, resolutionKind, expectedConflictId,
                    expectedConflictGeneration, cancellationToken);
        }
        catch (OperationCanceledException) when (runControl.IsStopRequested)
        {
            return new SyncRunResult(pair.Id,
            [
                new SyncDecision("<run>", SyncDecisionKind.Stopped,
                    "Conflict resolution stopped at a durable recovery boundary.")
            ]);
        }
    }

    private async Task<SyncAuthorityBatchResult> RunBatchCommandAsync(IEnumerable<SyncPair> pairs, string trigger,
        bool recoveryOnly, Func<SyncAuthorityBatchProgress, Task>? onProgress,
        CancellationToken cancellationToken, Action<string>? admissionProgress = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        var requested = pairs.ToArray();
        // Reserve admission AND publish the stoppable preparing control before any command/database
        // preparation. Maintenance holds this same gate while writing: accepting a command first would
        // wait on that transaction and can deadlock a nested public request. A refused invocation still
        // publishes no control, no durable command and no effect.
        var requestedPairIds = requested.Select(pair => pair.Id).Distinct(StringComparer.Ordinal).ToArray();
        var requestedKind = recoveryOnly ? "reconcile" : requestedPairIds.Length == 1 ? "run_pair" : "run_all";
        using var pendingRun = await BeginPendingRunAsync(cancellationToken, requestedKind,
            requestedPairIds.Length == 1 ? requestedPairIds[0] : null);
        // The reserved control's own token reaches every preparation, health and database await that
        // already accepts cancellation, so Stop cancels preparation promptly instead of only the file
        // effects dispatched later.
        var admissionToken = pendingRun.Token;
        var admissionStartedUtc = DateTimeOffset.UtcNow;
        var admissionStages = new List<SyncComparisonProgress>();
        void ReportAdmission(string message)
        {
            // Health checks precede durable command acceptance. Keep their original
            // times without inventing an accepted run; attach them to that same run
            // only after normal admission succeeds, through the shared log pipeline.
            admissionStages.Add(new SyncComparisonProgress("Preparing Command", "", 0, 0,
                admissionStartedUtc, DateTimeOffset.UtcNow, message, "preparation steps"));
            admissionProgress?.Invoke(message);
        }
        ReportAdmission("Admission: checking Explorer storage choices for the requested pairs; no file transfer has started.");
        await RefreshExplorerStorageChoicesCoreAsync(admissionToken,
            requested.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal), ReportAdmission);
        ReportAdmission("Admission: loading current pair policy and preparing recovery order.");
        var current = (await LoadPairsAsync(admissionToken, ReportAdmission)).Pairs.ToDictionary(pair => pair.Id);
        var ordered = requested.Select(pair => current.GetValueOrDefault(pair.Id, pair)).DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("No folder pairs are available to sync. Resume the account first if it is paused.");
        ReportAdmission("Admission: reading retained Rewind ownership before preparing the command.");
        var rewindPausedPairIds = await _rewindStore.LoadActivePairIdsAsync(admissionToken);
        using var discoveryBatch = SyncDiscoveryBatch.Begin(ordered.Where(pair => !rewindPausedPairIds.Contains(pair.Id)));
        var commandPayload = JsonSerializer.Serialize(new
        {
            trigger,
            recoveryOnly,
            automatic = !recoveryOnly && trigger is "Monitor" or "Safety poll",
            pairIds = ordered.Select(item => item.Id).ToArray()
        });
        // The reserved identity is accepted as this exact command; no second id is generated here.
        var commandId = await CreateAcceptedCommandAsync(requestedKind,
            ordered.Length == 1 ? ordered[0].Id : null, null, commandPayload, admissionToken,
            admissionProgress: ReportAdmission, reservedCommandId: pendingRun.CommandId,
            reservation: pendingRun.Reservation);
        PilotRunControl? runControl = null;
        try
        {
            var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
            ReportAdmission("Admission: command " + commandId + " accepted; acquiring its run ownership before any file effect.");
            // The pending run already owns this invocation's gate and published identity.
            runControl = pendingRun.EnterExecution();
            foreach (var stage in admissionStages)
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(ordered[0],
                    "Checking", null, null, null, Comparison: stage));
            // Stop also cancels preparation and coordinated observation, not only
            // the individual effects that pair runners have already dispatched.
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, runControl.StopToken);
            cancellationToken = runCancellation.Token;
            await using var databaseConnections = _authority.BeginConnectionReuseScope();
            await _authority.TransitionCommandRequestAsync(commandId, "executing",
                cancellationToken: cancellationToken);
            foreach (var pair in ordered)
                await NotifyProgressAsync(onProgress,
                    new SyncAuthorityBatchProgress(pair, "Pending", null, null, null));

            if (!recoveryOnly && !UseIndependentPairPlanningForTest)
            {
                var coordinatedBatch = await RunCoordinatedPairBatchAsync(ordered, trigger, runControl, commandAdmission,
                    onProgress, cancellationToken);
                await _authority.TransitionCommandRequestAsync(commandId, "completed",
                    JsonSerializer.Serialize(new
                    {
                        pairCount = ordered.Length,
                        completedPairs = coordinatedBatch.Items.Count(item => item.Result is not null),
                        failedPairs = coordinatedBatch.Items.Count(item => item.FailureMessage is not null || item.Result is { Blocked: > 0 } or { Conflicts: > 0 } or { NeedsVerification: > 0 }),
                        pairs = RetainedRunSummaries(coordinatedBatch.Items),
                        coordinatedBatch.Stopped
                    }), cancellationToken: CancellationToken.None);
                return coordinatedBatch;
            }

            // Cross-pair correlation must run before an individual pair planner can turn one
            // physical move into an unrelated create/delete. It considers every enabled pair,
            // even when the user invoked Sync now for only one of them.
            var crossPair = await CoordinateCrossPairMovesAsync(recoveryOnly, runControl, commandAdmission, cancellationToken,
                onProgress: onProgress);

            var results = new SyncAuthorityBatchItemResult?[ordered.Length];
            var nextIndex = -1;
            var concurrency = Math.Min(ordered.Length, LoadTransferSettings().MaximumConcurrentPairs);
            async Task RunWorkerAsync()
            {
                while (!runControl.IsStopRequested)
                {
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= ordered.Length) return;
                    var pair = ordered[index];
                    await NotifyProgressAsync(onProgress,
                        new SyncAuthorityBatchProgress(pair, "Running", null, null, null));
                    if (crossPair.PreparationFailures.TryGetValue(pair.Id, out var preparationFailure))
                    {
                        var failed = new SyncAuthorityBatchItemResult(pair, null,
                            preparationFailure.GetType().Name, SyncActivityText.Failure(preparationFailure));
                        results[index] = failed;
                        await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                            "Needs attention", null, failed.FailureType, failed.FailureMessage));
                        continue;
                    }
                    try
                    {
                        var result = recoveryOnly
                            ? await RecoverPairCoreAsync(pair, runControl, commandAdmission, cancellationToken, onProgress)
                            : await RunPairCoreAsync(pair, trigger, runControl, commandAdmission,
                                crossPair.MembershipObjectFences.TryGetValue(pair.Id, out var objectFences)
                                    ? objectFences
                                    : null,
                                crossPair.MembershipPathFences.TryGetValue(pair.Id, out var pathFences)
                                    ? pathFences
                                    : null,
                                cancellationToken, onProgress);
                        if (!recoveryOnly) result = await ApplyStorageAfterRunAsync(pair, result, cancellationToken, onProgress);
                        var state = result.Blocked > 0 || result.Conflicts > 0 || result.NeedsVerification > 0
                            ? "Needs attention"
                            : result.Stopped ? "Stopped" : "Completed";
                        var item = new SyncAuthorityBatchItemResult(pair, result, null, null);
                        results[index] = item;
                        await NotifyProgressAsync(onProgress,
                            new SyncAuthorityBatchProgress(pair, state, result, null, null));
                        if (result.Stopped) return;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException ||
                                                       !cancellationToken.IsCancellationRequested)
                    {
                        BatchItemFailureForTest?.Invoke(exception);
                        var item = new SyncAuthorityBatchItemResult(pair, null, exception.GetType().Name,
                            SyncActivityText.Failure(exception));
                        results[index] = item;
                        await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                            "Needs attention", null, item.FailureType, item.FailureMessage));
                    }
                }
            }
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunWorkerAsync()));
            var completedResults = results.Where(item => item is not null).Select(item => item!).ToArray();
            var batch = new SyncAuthorityBatchResult(completedResults, runControl.IsStopRequested ||
                completedResults.Any(item => item.Result?.Stopped == true));
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(new
                {
                    pairCount = ordered.Length,
                    completedPairs = completedResults.Count(item => item.Result is not null),
                    failedPairs = completedResults.Count(item => item.FailureMessage is not null || item.Result is { Blocked: > 0 } or { Conflicts: > 0 } or { NeedsVerification: > 0 }),
                    pairs = RetainedRunSummaries(completedResults),
                    batch.Stopped
                }), cancellationToken: CancellationToken.None);
            return batch;
        }
        catch (Exception exception)
        {
            // This is a command-level failure, not evidence that every file failed.
            // Preserve the accepted command identity and exact safe cause while
            // retaining all successful per-file receipts and independent outcomes.
            await RetainRunCommandFailureAsync(commandId, ordered, "batch_command_failed",
                runControl is null ? "Acquiring run ownership" : "Sync command execution or completion", exception);
            throw;
        }
        finally
        {
            if (runControl is not null)
            {
                await FinishRunActivityAsync(runControl);
                EndRun(runControl);
            }
        }
    }

    private static object[] RetainedRunSummaries(IEnumerable<SyncAuthorityBatchItemResult> items) => items.Select(item => (object)new
    {
        pairId = item.Pair.Id, pairName = item.Pair.Name,
        uploaded = item.Result?.Uploaded, downloaded = item.Result?.Downloaded,
        unchanged = item.Result?.Identical, conflicts = item.Result?.Conflicts,
        blocked = item.Result?.Blocked, stopped = item.Result?.Stopped,
        failed = item.Result?.Failed, notAttempted = item.Result?.NotAttempted,
        needsVerification = item.Result?.NeedsVerification, skipped = item.Result?.Skipped,
        failureType = item.FailureType, failureMessage = SyncActivityText.Safe(item.FailureMessage ?? ""),
        policyRevision = item.Pair.PolicyRevision, detailsSource = "retained_history_and_authority"
    }).ToArray();

    private async Task<SyncAuthorityBatchResult> RunCoordinatedPairBatchAsync(
        IReadOnlyList<SyncPair> ordered, string trigger, PilotRunControl runControl, SyncAuthorityCommandAdmission admission,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, CancellationToken cancellationToken)
    {
        const int maximumRounds = 8;
        var pairById = ordered.ToDictionary(item => item.Id, StringComparer.Ordinal);
        // Corresponding INCLUDED overlap components. They are derived from the CURRENT pair definitions AND the
        // FROZEN verified hierarchy of each prepared cloud pair, once preparation below has resolved both; a
        // path-opaque relation is admitted only from that independently bound proof, exactly as the typed cloud
        // guard admits it. A related component serializes its pairs' complete fresh turns; unrelated pairs keep
        // their existing concurrency. The grouping is scheduling input only and never decides a content winner.
        IReadOnlyList<IReadOnlyList<string>> relatedGroups = Array.Empty<IReadOnlyList<string>>();
        var relatedGroupOfPair = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastRelatedPlanner = new Dictionary<string, string>(StringComparer.Ordinal);
        // The related-sweep epoch of each component advances only when a related pair commits an actual effect.
        // A peer that has not yet been freshly planned at the current epoch still owes its coordinated turn; a
        // peer that has is swept for this epoch even though the per-round rotation deferred it this round.
        var relatedEpochByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var plannedEpochByPair = new Dictionary<string, int>(StringComparer.Ordinal);
        int RelatedEpoch(string group) => relatedEpochByGroup.TryGetValue(group, out var epoch) ? epoch : 0;
        bool OwesRelatedFreshTurn(string pairId) =>
            relatedGroupOfPair.TryGetValue(pairId, out var group) &&
            (!plannedEpochByPair.TryGetValue(pairId, out var planned) || planned < RelatedEpoch(group));
        bool RelatedPlannedLastRound(string pairId) =>
            relatedGroupOfPair.TryGetValue(pairId, out var group) &&
            lastRelatedPlanner.TryGetValue(group, out var previous) &&
            string.Equals(previous, pairId, StringComparison.Ordinal);
        var decisionsByPair = ordered.ToDictionary(item => item.Id,
            _ => new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var monitorWatermarks = new Dictionary<string, SyncAuthorityMonitorWatermark>(StringComparer.Ordinal);
        var readyPairIds = new HashSet<string>(StringComparer.Ordinal);
        var failures = new Dictionary<string, Exception>(StringComparer.Ordinal);
        var refusedPathsByPair = ordered.ToDictionary(item => item.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        // Completed transfers are accumulated per pair before the final per-path reduction,
        // so a path that was uploaded and then downloaded in this run still counts once in
        // each direction. Path keys follow the same case-insensitive dictionary semantics.
        var uploadedPathsByPair = ordered.ToDictionary(item => item.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var downloadedPathsByPair = ordered.ToDictionary(item => item.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var quiescent = false;
        void RecordCompletedTransfers(string pairId, IEnumerable<SyncDecision> decisions)
        {
            foreach (var decision in decisions)
            {
                if (decision.Kind == SyncDecisionKind.Uploaded)
                    uploadedPathsByPair[pairId].Add(decision.RelativePath);
                else if (decision.Kind == SyncDecisionKind.Downloaded)
                    downloadedPathsByPair[pairId].Add(decision.RelativePath);
            }
        }

        try
        {
        // Retained two-pair effects own both roots and must reconcile before a pair-local
        // recovery pass. Publish the actual checking phase before entering observation work.
        foreach (var pair in ordered)
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null, null, null));
        // Retained two-pair effects own both roots and must reconcile before a pair-local
        // recovery pass can safely classify any membership as independent.
        var retainedCrossPairRecovery = await CoordinateCrossPairMovesAsync(recoveryOnly: true, runControl,
            admission, cancellationToken);
        foreach (var original in ordered)
            if (retainedCrossPairRecovery.PreparationFailures.TryGetValue(original.Id, out var failure))
                failures[original.Id] = failure;

        var preparationAttempts = new CoordinatedPreparationAttempt?[ordered.Count];
        var nextPreparation = -1;
        var preparationConcurrency = Math.Max(1, Math.Min(ordered.Count,
            LoadTransferSettings().MaximumConcurrentPairs));
        // Materialized once before the concurrent preparation workers start.
        var rewindPausedPairIds = await _rewindStore.LoadActivePairIdsAsync(cancellationToken);
        async Task PrepareWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextPreparation);
                if (index >= ordered.Count) return;
                var original = ordered[index];
                if (NewSyncAdmissionReason(original) is { } admissionReason)
                {
                    preparationAttempts[index] = new CoordinatedPreparationAttempt(original, null, null,
                        new SyncDecision("<run>", SyncDecisionKind.Blocked, admissionReason));
                    continue;
                }
                if (rewindPausedPairIds.Contains(original.Id))
                {
                    preparationAttempts[index] = new CoordinatedPreparationAttempt(original, null, null,
                        new SyncDecision("<rewind>", SyncDecisionKind.Blocked,
                            "This pair is paused at a Rewind checkpoint. Detect, preview and apply the provider rollback batch or explicitly resume before ordinary synchronization."));
                    continue;
                }
                try
                {
                    var pair = await RefreshSelectionRulePathsAsync(original, cancellationToken, onProgress);
                    var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress);
                    var runner = CreatePairRunner(pair, prepared, runControl, admission, prepared.CloudFiles,
                        onInventory: progress =>
                            NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                                "Checking", null, null, null, Comparison: progress)), onProgress: onProgress);
                    preparationAttempts[index] = new CoordinatedPreparationAttempt(pair,
                        await runner.PrepareCoordinatedAsync(trigger, cancellationToken), null, null,
                        prepared.Cloud);
                }
                catch (Exception exception) when (exception is not OperationCanceledException ||
                                                   !cancellationToken.IsCancellationRequested)
                {
                    BatchItemFailureForTest?.Invoke(exception);
                    preparationAttempts[index] = new CoordinatedPreparationAttempt(original, null,
                        exception, null);
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, preparationConcurrency).Select(_ => PrepareWorkerAsync()));
        var preparedCloudByPair = new Dictionary<string, SyncAuthorityPreparedCloudPair>(StringComparer.Ordinal);
        foreach (var attempt in preparationAttempts.Where(item => item is not null).Select(item => item!))
        {
            pairById[attempt.Pair.Id] = attempt.Pair;
            if (attempt.Cloud is not null) preparedCloudByPair[attempt.Pair.Id] = attempt.Cloud;
            if (attempt.Failure is not null)
            {
                failures[attempt.Pair.Id] = attempt.Failure;
                continue;
            }
            if (attempt.BlockingDecision is not null)
            {
                SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[attempt.Pair.Id],
                    attempt.BlockingDecision);
                continue;
            }
            var preparation = attempt.Preparation ??
                              throw new InvalidDataException("A coordinated preparation result is absent.");
            monitorWatermarks[attempt.Pair.Id] = preparation.MonitorWatermark;
            foreach (var decision in preparation.Decisions)
                SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[attempt.Pair.Id], decision);
            RecordCompletedTransfers(attempt.Pair.Id, preparation.Decisions);
            if (preparation.CanPlan) readyPairIds.Add(attempt.Pair.Id);
        }

        // Resolve the related components from the current definitions and each prepared cloud pair's frozen
        // verified hierarchy, so an admitted path-opaque corresponding overlap is grouped exactly as it was
        // admitted and its shared fence is derived by the reservation projection from the same bound proof.
        relatedGroups = SyncPairPartitionPolicy.RelatedOverlapGroups(ordered,
            pair => BoundOpaqueProofsForGrouping(pair, preparedCloudByPair));
        foreach (var group in relatedGroups)
            foreach (var pairId in group) relatedGroupOfPair[pairId] = group[0];

        string? previousNoEffectFingerprint = null;
        IReadOnlySet<string> previousPotentialMoveKeys = new HashSet<string>(StringComparer.Ordinal);
        quiescent = readyPairIds.Count == 0;
        var runningNotified = false;
        var capacityContinuations = 0;
        for (var round = 0; round < maximumRounds + capacityContinuations && readyPairIds.Count > 0 &&
                            !runControl.IsStopRequested; round++)
        {
            var coordination = await CoordinateCrossPairMovesAsync(recoveryOnly: false, runControl,
                admission, cancellationToken, reserveOnly: true, onProgress: onProgress);
            foreach (var pairId in readyPairIds.ToArray())
            {
                if (!coordination.PreparationFailures.TryGetValue(pairId, out var failure)) continue;
                failures[pairId] = failure;
                readyPairIds.Remove(pairId);
            }
            if (readyPairIds.Count == 0) break;

            var roundReservations = coordination.CoordinatedObjectReservations.ToDictionary(
                item => item.Key, item => item.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            var temporalMemberships = ReserveUnconfirmedOneSidedMemberships(
                coordination.ObservationSets, roundReservations, previousPotentialMoveKeys);
            previousPotentialMoveKeys = temporalMemberships.CurrentKeys;
            var invalidatedPairIds = coordination.ObservationSets.Values.Where(item =>
                    item.ConnectionId is not null &&
                    coordination.InvalidatedConnectionIds.Contains(item.ConnectionId))
                .Select(item => item.PairId).ToHashSet(StringComparer.Ordinal);

            if (CoordinatedPlanningPhaseForTest is not null)
                await CoordinatedPlanningPhaseForTest("after-cross-pair-reservations-before-ordinary-planning");
            if (!runningNotified)
            {
                foreach (var pairId in readyPairIds)
                    await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pairById[pairId],
                        "Running", null, null, null));
                runningNotified = true;
            }

            var pairPlans = new List<(SyncPair Pair, PreparedPair Prepared,
                SyncAuthorityPairPlanningResult Plan)>();
            // At most ONE pair of each related component plans per round; its related peer is deferred to the
            // next round's FRESH coordinated observation, so no two retained ordinary plans are ever built
            // from one pre-effect observation of the shared scope. Unrelated pairs plan as before.
            var plannedRelatedGroups = new HashSet<string>(StringComparer.Ordinal);
            var skippedRelatedPairIds = new HashSet<string>(StringComparer.Ordinal);
            // A related peer that still owes a fresh turn at the current epoch goes first; the pair that planned
            // the previous round goes last inside its own component. The sort is stable, so unrelated pairs keep
            // their retained order and are never serialized by a related component they do not belong to.
            var planningOrder = ordered.Where(item => readyPairIds.Contains(item.Id))
                .Select(item => item.Id)
                .OrderByDescending(OwesRelatedFreshTurn)
                .ThenByDescending(RelatedPlannedLastRound)
                .ToArray();
            foreach (var pairId in planningOrder)
            {
                // The admitted cross-pair prefix does not reserve its deferred
                // successors. Drain it before any ordinary plan, even if a slot
                // becomes available after coordination, then observe again.
                if (coordination.WorkWindowFull) break;
                if (invalidatedPairIds.Contains(pairId) ||
                    temporalMemberships.DeferredPairIds.Contains(pairId)) continue;
                if (relatedGroupOfPair.TryGetValue(pairId, out var relatedGroup))
                {
                    if (plannedRelatedGroups.Contains(relatedGroup))
                    { skippedRelatedPairIds.Add(pairId); continue; }
                    plannedRelatedGroups.Add(relatedGroup);
                }
                if (!coordination.PreparedPairs.TryGetValue(pairId, out var prepared) ||
                    !coordination.ObservationSets.TryGetValue(pairId, out var observationSet))
                {
                    failures[pairId] = new InvalidOperationException(
                        "The pair did not receive a complete member of the coordinated observation set.");
                    readyPairIds.Remove(pairId);
                    continue;
                }
                try
                {
                    var pair = pairById[pairId];
                    var runner = CreatePairRunner(pair, prepared, runControl, admission, prepared.CloudFiles,
                        coordination.MembershipObjectFences.TryGetValue(pairId, out var fences) ? fences : null,
                        coordination.MembershipPathFences.TryGetValue(pairId, out var pathFences)
                            ? pathFences
                            : null, onProgress: onProgress);
                    var reservations = roundReservations.TryGetValue(pairId,
                        out var claimed) ? claimed : null;
                    var participantSets = coordination.ObservationSets.Values.Where(item =>
                            item.ConnectionId is null || observationSet.ConnectionId is null
                                ? string.Equals(item.PairId, observationSet.PairId, StringComparison.Ordinal)
                                : string.Equals(item.ConnectionId, observationSet.ConnectionId,
                                    StringComparison.Ordinal))
                        .OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray();
                    await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                        "Comparing", null, null, null));
                    var plan = await runner.PlanBoundAsync(observationSet, coordination.ObservationSetId,
                        reservations, participantSets, cancellationToken,
                        comparison => NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                            "Comparing", null, null, null, Comparison: comparison)), refusedPathsByPair[pairId],
                        streamIndependentFiles: true,
                        onAction: action => NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(
                            pair, "Action", null, null, null, action)),
                        beforeIndependentWindow: CoordinatedPlanningPhaseForTest is null ? null :
                            () => CoordinatedPlanningPhaseForTest("after-ordinary-planning-before-dispatch"),
                        onIndependentResult: result =>
                        {
                            foreach (var decision in result.Decisions)
                                SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId],decision);
                            RecordCompletedTransfers(pairId,result.Decisions);
                            return Task.CompletedTask;
                        });
                    foreach (var decision in plan.Decisions)
                        SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId], decision);
                    pairPlans.Add((pair, prepared, plan));
                    if (relatedGroupOfPair.TryGetValue(pairId, out var plannedGroup))
                    {
                        lastRelatedPlanner[plannedGroup] = pairId;
                        plannedEpochByPair[pairId] = RelatedEpoch(plannedGroup);
                    }
                    if (plan.WorkWindowFull) break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException ||
                                                   !cancellationToken.IsCancellationRequested)
                {
                    BatchItemFailureForTest?.Invoke(exception);
                    failures[pairId] = exception;
                    readyPairIds.Remove(pairId);
                }
            }

            if (CoordinatedPlanningPhaseForTest is not null)
                await CoordinatedPlanningPhaseForTest("after-ordinary-planning-before-dispatch");

            var crossResults = new SyncAuthorityExecutionResult?[coordination.CrossPairPlans.Count];
            var crossFailures = new Exception?[coordination.CrossPairPlans.Count];
            var pairResults = new SyncAuthorityPairExecutionResult?[pairPlans.Count];
            var pairFailures = new Exception?[pairPlans.Count];
            var work = new List<Func<Task>>();
            for (var index = 0; index < coordination.CrossPairPlans.Count; index++)
            {
                var captured = index;
                work.Add(async () =>
                {
                    try
                    {
                        var value = coordination.CrossPairPlans[captured];
                        var operation = await _authority.LoadOperationAsync(value.Plan.OperationId,
                            cancellationToken);
                        if (await _authority.SupersedeReadyOperationIfPlanningAuthorityChangedAsync(
                                value.Plan.OperationId, operation.PlanningAuthorities, cancellationToken))
                        {
                            crossResults[captured] = new SyncAuthorityExecutionResult(value.Plan.OperationId,
                                SyncAuthorityOperationStatus.Superseded, false,
                                "A fresher coordinated observation superseded this cross-pair move.");
                            return;
                        }
                        crossResults[captured] = await RunCrossPairPlanAsync(value.Coordinator, value.Plan,
                            runControl, cancellationToken,
                            action => NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(
                                pairById[value.Plan.SourcePairId], "Action", null, null, null, action)));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException ||
                                                       !cancellationToken.IsCancellationRequested)
                    {
                        crossFailures[captured] = exception;
                    }
                });
            }
            for (var index = 0; index < pairPlans.Count; index++)
            {
                var captured = index;
                work.Add(async () =>
                {
                    try
                    {
                        var value = pairPlans[captured];
                        var runner = CreatePairRunner(value.Pair, value.Prepared, runControl, admission,
                            value.Prepared.CloudFiles,
                            coordination.MembershipObjectFences.TryGetValue(value.Pair.Id, out var fences)
                                ? fences
                                : null,
                            coordination.MembershipPathFences.TryGetValue(value.Pair.Id, out var pathFences)
                                ? pathFences
                                : null, onProgress: onProgress);
                        pairResults[captured] = await runner.ExecuteBoundAsync(value.Plan, cancellationToken,
                            action => NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(
                                value.Pair, "Action", null, null, null, action)));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException ||
                                                       !cancellationToken.IsCancellationRequested)
                    {
                        pairFailures[captured] = exception;
                    }
                });
            }

            var nextWork = -1;
            var activeWorkers = 0;
            var concurrency = Math.Max(1, Math.Min(work.Count,
                LoadTransferSettings().MaximumConcurrentPairs));
            async Task RunWorkAsync()
            {
                while (!runControl.IsStopRequested)
                {
                    var index = Interlocked.Increment(ref nextWork);
                    if (index >= work.Count) return;
                    var active = Interlocked.Increment(ref activeWorkers);
                    try
                    {
                        if (CoordinatedWorkerStartedForTest is not null)
                            await CoordinatedWorkerStartedForTest(active);
                        await work[index]();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeWorkers);
                    }
                }
            }
            try
            {
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunWorkAsync()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // All workers are drained. Reduce already completed pair results
                // before leaving this round, rather than discarding their successes.
            }

            var completedEffects = crossResults.Count(item => item?.Status ==
                                                              SyncAuthorityOperationStatus.Completed);
            var planningInvalidated = coordination.InvalidatedConnectionIds.Count > 0 ||
                                      crossResults.Any(item => item?.Status ==
                                          SyncAuthorityOperationStatus.Superseded);
            for (var index = 0; index < crossFailures.Length; index++)
            {
                if (crossFailures[index] is not { } failure) continue;
                BatchItemFailureForTest?.Invoke(failure);
                var plan = coordination.CrossPairPlans[index].Plan;
                foreach (var member in new[]
                         {
                             (plan.SourcePairId, plan.SourcePath),
                             (plan.DestinationPairId, plan.DestinationPath)
                         })
                    if (decisionsByPair.TryGetValue(member.Item1, out var decisions))
                        SyncAuthorityPairRunner.RetainMostMaterial(decisions,
                            new SyncDecision(member.Item2, SyncDecisionKind.Blocked,
                                $"The retained cross-pair move requires recovery after {failure.GetType().Name}."));
            }
            for (var index = 0; index < pairPlans.Count; index++)
            {
                var pairId = pairPlans[index].Pair.Id;
                if (pairFailures[index] is { } failure)
                {
                    BatchItemFailureForTest?.Invoke(failure);
                    failures[pairId] = failure;
                    readyPairIds.Remove(pairId);
                    continue;
                }
                if (pairResults[index] is not { } result)
                {
                    foreach (var pending in pairPlans[index].Plan.Operations)
                        SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId],
                            new SyncDecision(pending.RelativePath, SyncDecisionKind.Blocked,
                                "Not attempted: cancellation stopped this pair before its queued file was admitted.")
                            { NotAttempted = true });
                    continue;
                }
                completedEffects += result.CompletedEffects;
                planningInvalidated |= result.PlanningInvalidated;
                foreach (var decision in result.Decisions)
                {
                    SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId], decision);
                    // A stale unissued plan is a request to replan, not a file
                    // refusal. Keeping its path in the refusal set silently skipped
                    // the fresh successor and left ordinary edited files unsynced.
                    if (decision.Kind == SyncDecisionKind.Blocked &&
                        !(result.PlanningInvalidated && decision.NotAttempted))
                        refusedPathsByPair[pairId].Add(decision.RelativePath);
                    if (decision.Kind is SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending)
                        readyPairIds.Remove(pairId);
                }
                RecordCompletedTransfers(pairId, result.Decisions);
            }

            // A related-pair commit changes bytes the other pair of the same component shares: drop the
            // batch's cached volume metadata so the next ordinary read re-observes the shared scope.
            if (completedEffects > 0 &&
                (pairPlans.Any(item => relatedGroupOfPair.ContainsKey(item.Pair.Id)) ||
                 coordination.CrossPairPlans.Any(item =>
                     relatedGroupOfPair.ContainsKey(item.Plan.SourcePairId) ||
                     relatedGroupOfPair.ContainsKey(item.Plan.DestinationPairId))))
            {
                SyncDiscoveryBatch.InvalidateCurrentAfterRelatedCommit();
                // Advance the component's sweep epoch so every ready peer re-observes its shared scope before
                // the closing pass may terminate. The peers' saved choices are untouched; only their
                // observation is invalidated.
                foreach (var group in pairPlans.Select(item => item.Pair.Id)
                             .Concat(coordination.CrossPairPlans.SelectMany(item => new[]
                                 { item.Plan.SourcePairId, item.Plan.DestinationPairId }))
                             .Where(relatedGroupOfPair.ContainsKey)
                             .Select(pairId => relatedGroupOfPair[pairId])
                             .Distinct(StringComparer.Ordinal))
                    relatedEpochByGroup[group] = RelatedEpoch(group) + 1;
            }

            // A related peer is deferred only while it still owes a FRESH turn at the current effect epoch.
            // Once every ready peer of a component has been freshly planned at the latest epoch the component is
            // swept, so the closing no-effect pass terminates well before maximumRounds instead of deferring one
            // healthy peer of a two-pair component on every round.
            var deferredRelatedPairIds = skippedRelatedPairIds
                .Where(pairId => readyPairIds.Contains(pairId) && OwesRelatedFreshTurn(pairId))
                .ToHashSet(StringComparer.Ordinal);

            if (CoordinatedPlanningPhaseForTest is not null)
                await CoordinatedPlanningPhaseForTest("after-dispatch-before-next-observation-set");

            // A run owns the work derived from its complete observation, not a promise
            // that a live producer will stop changing forever. Every dispatched action
            // has already drained and verified its effect. Re-observe only while a
            // stale plan or coordinated/one-sided membership dependency needs it;
            // later independent arrivals belong to the next normal sync. In particular,
            // never turn successful files into a deadline failure by polling for two
            // globally unchanged whole-folder snapshots.
            // A multi-root correlation needs one bounded closing observation: an
            // existing member can move between roots after ordinary planning without
            // invalidating any queued file action (there may be no such action).
            // This is not a demand for two quiet snapshots. Further passes are only
            // for an actual invalidated plan, cross-pair effect or unresolved member.
            var workWindowFull = coordination.WorkWindowFull || pairPlans.Any(item => item.Plan.WorkWindowFull);
            if (workWindowFull)
            {
                if (completedEffects > 0)
                {
                    // The current window drained real effects. Its continuation is
                    // not another failed planning attempt; retain the existing
                    // invalidation budget only for actual invalidated authority.
                    if (!planningInvalidated) capacityContinuations++;
                    previousNoEffectFingerprint = null;
                    continue;
                }
                foreach (var pairId in readyPairIds)
                    SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId],
                        new SyncDecision("<work-window>", SyncDecisionKind.Blocked,
                            "Waiting for retained work to drain before planning more files.")
                        { NotAttempted = true });
                quiescent = true;
                break;
            }
            var unresolvedMemberships = temporalMemberships.DeferredPairIds.Any(readyPairIds.Contains) ||
                roundReservations.Any(item => readyPairIds.Contains(item.Key) && item.Value.Count > 0);
            if (!planningInvalidated && coordination.CrossPairPlans.Count == 0 && !unresolvedMemberships &&
                deferredRelatedPairIds.Count == 0 &&
                (round > 0 || coordination.ObservationSets.Count <= 1))
            {
                quiescent = true;
                break;
            }

            if (completedEffects == 0 && !planningInvalidated &&
                deferredRelatedPairIds.Count == 0 &&
                string.Equals(previousNoEffectFingerprint, coordination.ContentFingerprint,
                    StringComparison.Ordinal))
            {
                if (coordination.CrossPairPlans.Count == 0)
                    foreach (var reservation in roundReservations)
                    foreach (var objectId in reservation.Value)
                        if (decisionsByPair.TryGetValue(reservation.Key, out var retainedDecisions))
                            SyncAuthorityPairRunner.RetainMostMaterial(retainedDecisions,
                                new SyncDecision("<membership>", SyncDecisionKind.Blocked,
                                    $"Logical object {objectId} retained one physical identity in multiple pair roots across two unchanged observation sets; no membership effect was issued."));
                quiescent = true;
                break;
            }
            previousNoEffectFingerprint = completedEffects == 0 && !planningInvalidated
                ? coordination.ContentFingerprint
                : null;
        }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An already drained round owns real per-file decisions. Cancelling the next observation
            // must not discard those successes or erase the exact conflicts/retry information.
            foreach (var original in ordered)
                SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[original.Id],
                    new SyncDecision("<run>", SyncDecisionKind.Stopped,
                        "The run deadline or cancellation stopped remaining work. Completed files and exact unresolved conflicts are retained; no automatic retry was started."));
        }

        if (!quiescent && !runControl.IsStopRequested && !cancellationToken.IsCancellationRequested)
            foreach (var pairId in readyPairIds)
                SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId],
                    new SyncDecision("<planning>", SyncDecisionKind.Blocked,
                        "The coordinated observation set did not become effect-free within its bounded planning rounds."));

        foreach (var pairId in readyPairIds)
        {
            if (runControl.IsStopRequested || cancellationToken.IsCancellationRequested)
                SyncAuthorityPairRunner.RetainMostMaterial(decisionsByPair[pairId],
                    new SyncDecision("<run>", SyncDecisionKind.Stopped,
                        "Stop was requested; durable operation and recovery authority was retained."));
            else if (monitorWatermarks.TryGetValue(pairId, out var watermark) &&
                     !failures.ContainsKey(pairId) &&
                     !decisionsByPair[pairId].Values.Any(item => item.NotAttempted || item.Kind is
                         SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or
                         SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending))
            {
                await _authority.CloseMonitorHintsThroughAsync(pairId, watermark, cancellationToken);
            }
        }

        var items = new List<SyncAuthorityBatchItemResult>(ordered.Count);
        foreach (var original in ordered)
        {
            if (failures.TryGetValue(original.Id, out var failure))
            {
                var failed = new SyncAuthorityBatchItemResult(original, null, failure.GetType().Name,
                    SyncActivityText.Failure(failure));
                items.Add(failed);
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(original,
                    "Needs attention", null, failed.FailureType, failed.FailureMessage));
                continue;
            }
            var pair = pairById[original.Id];
            var result = new SyncRunResult(pair.Id, decisionsByPair[pair.Id].Values
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                TransferTotals = new SyncRunTransferTotals(
                    uploadedPathsByPair[pair.Id], downloadedPathsByPair[pair.Id])
            };
            if (!cancellationToken.IsCancellationRequested)
                result = await ApplyStorageAfterRunAsync(pair, result, cancellationToken, onProgress);
            items.Add(new SyncAuthorityBatchItemResult(pair, result, null, null));
            var state = result.Blocked > 0 || result.Conflicts > 0 || result.NeedsVerification > 0
                ? "Needs attention"
                : result.Stopped ? "Stopped" : "Completed";
            await NotifyProgressAsync(onProgress,
                new SyncAuthorityBatchProgress(pair, state, result, null, null));
        }
        return new SyncAuthorityBatchResult(items, runControl.IsStopRequested ||
            items.Any(item => item.Result?.Stopped == true));
    }

    private async Task<CrossPairCoordinationResult> CoordinateCrossPairMovesAsync(bool recoveryOnly,
        PilotRunControl runControl, SyncAuthorityCommandAdmission admission,
        CancellationToken cancellationToken, bool reserveOnly = false,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        var allPairs = (await LoadPairsAsync(cancellationToken)).Pairs;
        var pairsById = allPairs.ToDictionary(pair => pair.Id, StringComparer.Ordinal);
        var rewindPausedPairIds = await _rewindStore.LoadActivePairIdsAsync(cancellationToken);
        var universe = allPairs
            .Where(pair => NewSyncAdmissionReason(pair) is null && pair.InitialReviewApproved != false &&
                           !rewindPausedPairIds.Contains(pair.Id))
            .OrderByDescending(pair => pair.Priority)
            .ThenBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var prepared = new Dictionary<string, PreparedPair>(StringComparer.Ordinal);
        var preparationFailures = new Dictionary<string, Exception>(StringComparer.Ordinal);
        var membershipObjectFences = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var membershipPathFences = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var observationSets = new Dictionary<string, SyncAuthorityPairObservationSet>(StringComparer.Ordinal);
        var crossPairPlans = new List<CrossPairPlannedExecution>();
        var crossPairWindowFull=false;
        var coordinatedObjectReservations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var failedActiveParticipants = new List<SyncPair>();
        var invalidatedConnectionIds = new HashSet<string>(StringComparer.Ordinal);

        void FenceObject(string pairId, string objectId, string reason)
        {
            if (!membershipObjectFences.TryGetValue(pairId, out var fences))
            {
                fences = new Dictionary<string, string>(StringComparer.Ordinal);
                membershipObjectFences[pairId] = fences;
            }
            fences[objectId] = reason;
        }

        void FencePath(string pairId, string relativePath, string reason)
        {
            if (!membershipPathFences.TryGetValue(pairId, out var fences))
            {
                fences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                membershipPathFences[pairId] = fences;
            }
            fences[PilotPath.NormalizeRelative(relativePath)] = reason;
        }

        void FenceOperation(SyncAuthorityOperationSnapshot operation, string reason)
        {
            foreach (var member in operation.Members.Where(member =>
                         !string.IsNullOrWhiteSpace(member.PairId) &&
                         !string.IsNullOrWhiteSpace(member.ObjectId)))
                FenceObject(member.PairId, member.ObjectId, reason);
            foreach (var reservation in operation.SubtreeReservations)
                FencePath(reservation.PairId, reservation.RelativePath, reason);
        }

        void ReserveOperationObjects(SyncAuthorityOperationSnapshot operation)
        {
            foreach (var member in operation.Members.Where(member =>
                         !string.IsNullOrWhiteSpace(member.PairId) &&
                         !string.IsNullOrWhiteSpace(member.ObjectId)))
            {
                if (!coordinatedObjectReservations.TryGetValue(member.PairId, out var objects))
                {
                    objects = new HashSet<string>(StringComparer.Ordinal);
                    coordinatedObjectReservations[member.PairId] = objects;
                }
                objects.Add(member.ObjectId);
            }
            foreach (var reservation in operation.SubtreeReservations)
                FencePath(reservation.PairId, reservation.RelativePath,
                    $"Cross-pair transaction {operation.OperationId} retains exclusive subtree-prefix authority until it reaches a safe terminal state.");
        }

        void FenceFailedParticipant(SyncPair failed, Exception exception)
        {
            BatchItemFailureForTest?.Invoke(exception);
            preparationFailures.TryAdd(failed.Id, exception);
            if (failedActiveParticipants.All(item => !string.Equals(item.Id, failed.Id,
                    StringComparison.Ordinal)))
                failedActiveParticipants.Add(failed);
        }

        async Task<bool> EnsurePreparedAsync(SyncPair pair, bool activeParticipant)
        {
            if (prepared.ContainsKey(pair.Id)) return true;
            if (preparationFailures.ContainsKey(pair.Id)) return false;
            try
            {
                prepared[pair.Id] = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                               !cancellationToken.IsCancellationRequested)
            {
                preparationFailures[pair.Id] = exception;
                if (activeParticipant) FenceFailedParticipant(pair, exception);
                else BatchItemFailureForTest?.Invoke(exception);
                return false;
            }
        }

        async Task FenceRetainedSubtreesAsync()
        {
            foreach (var reservation in await _authority.ListRetainedSubtreeReservationsAsync(cancellationToken))
                FencePath(reservation.PairId, reservation.RelativePath,
                    $"Cross-pair transaction {reservation.OperationId} is {reservation.Status} and retains exclusive subtree-prefix authority across restart.");
        }

        foreach (var pair in universe)
        {
            if (await _authority.LoadPairStateAsync(pair.Id, cancellationToken) != "active") continue;
            await EnsurePreparedAsync(pair, activeParticipant: true);
        }
        // Cross-pair correlation is provider-account scoped. A failed participant can only
        // make membership effects ambiguous for another pair in that same retained account;
        // unrelated account domains remain fully runnable. Retained cross-pair operations
        // below fence their two exact participants regardless of this discovery-time rule.

        // Recover a retained two-pair transaction before taking a new observation. Its source
        // identity may already have left the old root, so reconstruct it from frozen preconditions.
        foreach (var source in allPairs)
        {
            var queue = await _authority.ListRecoveryOperationsForPairAsync(source.Id, 500,
                cancellationToken);
            foreach (var item in queue.Where(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove))
            {
                var operation = await _authority.LoadOperationAsync(item.OperationId, cancellationToken);
                if (operation.Status == SyncAuthorityOperationStatus.Ready &&
                    operation.PlanningAuthorities.Count > 0)
                {
                    var current = true;
                    foreach (var planningAuthority in operation.PlanningAuthorities)
                        current &= await _authority.IsPlanningAuthorityCurrentAsync(planningAuthority,
                            cancellationToken);
                    if (!current)
                    {
                        if (await _authority.SupersedeReadyOperationIfPlanningAuthorityChangedAsync(
                                operation.OperationId, operation.PlanningAuthorities, cancellationToken))
                            continue;
                        FenceOperation(operation,
                            $"Cross-pair transaction {operation.OperationId} has stale unissued planning authority and cannot be safely superseded; no effect was issued.");
                        continue;
                    }
                }
                var plan = RehydrateCrossPairPlan(operation);
                if (!pairsById.TryGetValue(plan.DestinationPairId, out var destination))
                {
                    FenceOperation(operation,
                        $"Cross-pair transaction {operation.OperationId} cannot resolve its retained destination pair; only its exact object participants are fenced.");
                    continue;
                }
                if (SyncCloudPairConfiguration.IsCloudOnly(source) ||
                    SyncCloudPairConfiguration.IsCloudOnly(destination))
                {
                    FenceOperation(operation,
                        $"Cross-pair transaction {operation.OperationId} requires physical local roots; a cloud-only endpoint cannot enter that retained NTFS transaction.");
                    continue;
                }
                var sourceReady = await EnsurePreparedAsync(source, activeParticipant: false);
                var destinationReady = await EnsurePreparedAsync(destination, activeParticipant: false);
                if (!sourceReady || !destinationReady)
                {
                    FenceOperation(operation,
                        $"Cross-pair transaction {operation.OperationId} is waiting for both exact pair roots; only its retained object participants are fenced.");
                    continue;
                }
                var destinationPrepared = prepared[destination.Id];
                var coordinator = new SyncAuthorityCrossPairMoveCoordinator(source,
                    prepared[source.Id].Remote, prepared[source.Id].Options, destination,
                    destinationPrepared.Remote, destinationPrepared.Options, _authority, admission);
                try
                {
                    await RunCrossPairPlanAsync(coordinator, plan, runControl, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException ||
                                                   !cancellationToken.IsCancellationRequested)
                {
                    BatchItemFailureForTest?.Invoke(exception);
                    FenceOperation(operation,
                        $"Cross-pair transaction {operation.OperationId} remains recoverable after {exception.GetType().Name}; only its retained object participants are fenced.");
                    // The exact transaction remains durable and will be presented by the pair runner.
                }
            }
        }
        if (recoveryOnly || runControl.IsStopRequested)
        {
            await FenceRetainedSubtreesAsync();
            return BuildCrossPairCoordinationResult("recovery-only", new string('0', 64));
        }

        var observationCandidates = universe.Where(pair => prepared.ContainsKey(pair.Id) &&
                                                            failedActiveParticipants.All(failed =>
                                                                !string.Equals(failed.Id, pair.Id,
                                                                    StringComparison.Ordinal)))
            .ToArray();
        var observationAttempts = new CoordinatedObservationAttempt?[observationCandidates.Length];
        var nextObservation = -1;
        var observationConcurrency = Math.Max(1, Math.Min(observationCandidates.Length,
            LoadTransferSettings().MaximumConcurrentPairs));
        async Task ObserveWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextObservation);
                if (index >= observationCandidates.Length) return;
                var pair = observationCandidates[index];
                var value = prepared[pair.Id];
                try
                {
                    var observed = await CreateObserver(pair, value, progress =>
                            NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                                "Checking", null, null, null, Comparison: progress)))
                        .ObserveAsync("monitor", cancellationToken);
                    observationAttempts[index] = new CoordinatedObservationAttempt(pair,
                        await _authority.LoadPlanningObservationSetAsync(pair.Id,
                            value.Options.ConfigurationRevision, value.LocalRootBindingId,
                            value.DropboxRootBindingId, observed.LocalScan.ScanGenerationId,
                            observed.DropboxScan.ScanGenerationId, cancellationToken), null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException ||
                                                   !cancellationToken.IsCancellationRequested)
                {
                    observationAttempts[index] = new CoordinatedObservationAttempt(pair, null, exception);
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, observationConcurrency).Select(_ => ObserveWorkerAsync()));
        foreach (var attempt in observationAttempts.Where(item => item is not null).Select(item => item!))
        {
            if (attempt.Failure is not null)
            {
                FenceFailedParticipant(attempt.Pair, attempt.Failure);
                continue;
            }
            observationSets[attempt.Pair.Id] = attempt.ObservationSet ??
                                               throw new InvalidDataException(
                                                   "A coordinated observation set is absent.");
        }

        // Recovering issued effects and replanning unissued reservations are distinct.
        // Fresh complete observations invalidate old Ready cross-pair reservations;
        // the database's existing zero-attempt/zero-preparation guard alone decides
        // whether they can be retired. Uncertain/issued work keeps its fences.
        await _authority.SupersedeStaleUnissuedCrossPairReservationsAsync(cancellationToken);
        await FenceRetainedSubtreesAsync();
        var contentFingerprint = HashObservationSetContent(observationSets.Values);
        var observationSetId = Id("observation-set", contentFingerprint,
            string.Join("\n", observationSets.Values.OrderBy(item => item.PairId, StringComparer.Ordinal)
                .Select(item => string.Join("|", item.PairId, item.LocalScanGenerationId,
                    item.DropboxScanGenerationId, item.ConfigurationRevision.ToString(CultureInfo.InvariantCulture)))));
        await ReserveTemporalCutIdentityDuplicatesAsync(universe, observationSets,
            coordinatedObjectReservations, cancellationToken);
        if (CoordinatedPlanningPhaseForTest is not null)
            await CoordinatedPlanningPhaseForTest("after-observation-set-bound-before-cross-pair-correlation");

        // A failed peer no longer disables membership effects for its whole Dropbox account.
        // Bind uncertainty only to logical objects whose current/retained identity can actually
        // participate in an unresolved cross-pair move: a baseline member missing on one side,
        // or a newly observed physical identity retained by the failed peer.
        foreach (var pair in universe.Where(pair => !SyncCloudPairConfiguration.IsCloudOnly(pair) &&
                                                     prepared.ContainsKey(pair.Id) &&
                                                     failedActiveParticipants.All(failed =>
                                                         !string.Equals(failed.Id, pair.Id,
                                                             StringComparison.Ordinal))))
        {
            var failedPeers = failedActiveParticipants.Where(failed =>
                    !SyncCloudPairConfiguration.IsCloudOnly(failed) &&
                    string.Equals(failed.ConnectionId, pair.ConnectionId, StringComparison.Ordinal))
                .ToArray();
            if (failedPeers.Length == 0) continue;
            var scoped = await BuildFailedParticipantObjectFencesAsync(pair, failedPeers,
                cancellationToken);
            foreach (var item in scoped)
                FenceObject(pair.Id, item.Key, item.Value);
        }

        for (var sourceIndex = 0; sourceIndex < universe.Length; sourceIndex++)
        for (var destinationIndex = 0; destinationIndex < universe.Length; destinationIndex++)
        {
            if (sourceIndex == destinationIndex || runControl.IsStopRequested) continue;
            var source = universe[sourceIndex];
            var destination = universe[destinationIndex];
            // Cross-account remote relocation is a separate capability from ordinary pair sync.
            // A local endpoint must never inherit it merely because both connection IDs are empty.
            if (IsLocalPair(source) || IsLocalPair(destination) ||
                SyncCloudPairConfiguration.IsCloudOnly(source) ||
                SyncCloudPairConfiguration.IsCloudOnly(destination)) continue;
            if (invalidatedConnectionIds.Contains(source.ConnectionId)) continue;
            if (!prepared.TryGetValue(source.Id, out var sourcePrepared) ||
                !prepared.TryGetValue(destination.Id, out var destinationPrepared) ||
                !observationSets.TryGetValue(source.Id, out var sourceObservationSet) ||
                !observationSets.TryGetValue(destination.Id, out var destinationObservationSet) ||
                failedActiveParticipants.Any(failed => string.Equals(failed.Id, source.Id,
                    StringComparison.Ordinal) || string.Equals(failed.Id, destination.Id,
                    StringComparison.Ordinal)))
                continue;
            var coordinator = new SyncAuthorityCrossPairMoveCoordinator(source, sourcePrepared.Remote,
                sourcePrepared.Options, destination, destinationPrepared.Remote,
                destinationPrepared.Options, _authority, admission);
            try
            {
                var participantSets = observationSets.Values.Where(item =>
                        item.ConnectionId is null || sourceObservationSet.ConnectionId is null
                            ? string.Equals(item.PairId, sourceObservationSet.PairId, StringComparison.Ordinal) ||
                              string.Equals(item.PairId, destinationObservationSet.PairId, StringComparison.Ordinal)
                            : string.Equals(item.ConnectionId, sourceObservationSet.ConnectionId,
                                StringComparison.Ordinal))
                    .OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray();
                var admittedPlans=await coordinator.PlanAsync(sourceObservationSet,
                    destinationObservationSet,observationSetId,participantSets,cancellationToken);
                crossPairWindowFull |= coordinator.WorkWindowFull;
                foreach (var plan in admittedPlans)
                {
                    if (reserveOnly)
                    {
                        crossPairPlans.Add(new CrossPairPlannedExecution(coordinator, plan));
                        ReserveOperationObjects(await _authority.LoadOperationAsync(plan.OperationId,
                            cancellationToken));
                    }
                    else
                    {
                        await RunCrossPairPlanAsync(coordinator, plan, runControl, cancellationToken);
                    }
                }
            }
            catch (InvalidOperationException exception) when (IsPlanningInvalidation(exception))
            {
                invalidatedConnectionIds.Add(source.ConnectionId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                               !cancellationToken.IsCancellationRequested)
            {
                BatchItemFailureForTest?.Invoke(exception);
                foreach (var pair in new[] { source, destination })
                foreach (var item in await BuildFailedParticipantObjectFencesAsync(pair,
                             [pair.Id == source.Id ? destination : source], cancellationToken))
                    FenceObject(pair.Id, item.Key,
                        $"Cross-pair correlation between {source.Name} and {destination.Name} failed with {exception.GetType().Name}; " +
                        item.Value);
            }
        }
        if (invalidatedConnectionIds.Count > 0)
        {
            foreach (var value in crossPairPlans.Where(item => invalidatedConnectionIds.Contains(
                         pairsById[item.Plan.SourcePairId].ConnectionId)).ToArray())
            {
                _ = await _authority.SupersedeReadyOperationIfPlanningAuthorityChangedAsync(
                    value.Plan.OperationId,
                    (await _authority.LoadOperationAsync(value.Plan.OperationId, cancellationToken))
                    .PlanningAuthorities, cancellationToken);
                crossPairPlans.Remove(value);
            }
        }
        return BuildCrossPairCoordinationResult(observationSetId, contentFingerprint);

        CrossPairCoordinationResult BuildCrossPairCoordinationResult(string setId, string fingerprint) =>
            new(preparationFailures,
                membershipObjectFences.ToDictionary(item => item.Key,
                    item => (IReadOnlyDictionary<string, string>)item.Value, StringComparer.Ordinal),
                membershipPathFences.ToDictionary(item => item.Key,
                    item => (IReadOnlyDictionary<string, string>)item.Value, StringComparer.Ordinal),
                prepared, observationSets, setId, fingerprint, crossPairPlans,
                coordinatedObjectReservations.ToDictionary(item => item.Key,
                    item => (IReadOnlySet<string>)item.Value, StringComparer.Ordinal),
                invalidatedConnectionIds) { WorkWindowFull=crossPairWindowFull };
    }

    private static bool IsPlanningInvalidation(InvalidOperationException exception) =>
        exception.Message.Contains("observation set changed", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("observation-set authority changed", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("planning observation set is no longer current",
            StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyDictionary<string, string>> BuildFailedParticipantObjectFencesAsync(
        SyncPair healthyPair, IReadOnlyList<SyncPair> failedPeers, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (failedPeers.Count == 0) return result;
        var peerLabel = string.Join(", ", failedPeers
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Name} ({item.Id})"));
        var local = await _authority.LoadLatestCompleteObservationAsync(healthyPair.Id, "local",
            cancellationToken);
        var dropbox = await _authority.LoadLatestCompleteObservationAsync(healthyPair.Id, "dropbox",
            cancellationToken);
        var points = await _authority.LoadLatestSynchronizationPointsAsync(healthyPair.Id,
            cancellationToken);
        var localObjects = local.Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal);
        var dropboxObjects = dropbox.Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal);

        foreach (var point in points.Where(point =>
                     localObjects.Contains(point.ObjectId) != dropboxObjects.Contains(point.ObjectId)))
            result[point.ObjectId] =
                $"This exact object has an unresolved one-sided membership while same-account peer {peerLabel} is unavailable. It may be a cross-pair move participant, so only this object is fenced; independent objects remain runnable.";

        var failedLocalIdentities = new HashSet<string>(StringComparer.Ordinal);
        var failedDropboxIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failed in failedPeers)
        {
            var versions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in await _authority.LoadLatestCompleteObservationAsync(failed.Id, "local",
                         cancellationToken))
                versions.Add(entry.ObjectVersionId);
            foreach (var entry in await _authority.LoadLatestCompleteObservationAsync(failed.Id, "dropbox",
                         cancellationToken))
                versions.Add(entry.ObjectVersionId);
            foreach (var point in await _authority.LoadLatestSynchronizationPointsAsync(failed.Id,
                         cancellationToken))
            {
                if (point.LocalObjectVersionId is not null) versions.Add(point.LocalObjectVersionId);
                if (point.DropboxObjectVersionId is not null) versions.Add(point.DropboxObjectVersionId);
            }
            foreach (var versionId in versions)
            {
                var version = await _authority.LoadObjectVersionAsync(versionId, cancellationToken);
                var identity = MembershipPhysicalIdentity(version);
                (version.Side == "local" ? failedLocalIdentities : failedDropboxIdentities).Add(identity);
            }
        }

        foreach (var entry in local)
        {
            var version = await _authority.LoadObjectVersionAsync(entry.ObjectVersionId, cancellationToken);
            if (failedLocalIdentities.Contains(MembershipPhysicalIdentity(version)))
                result[entry.ObjectId] =
                    $"This exact NTFS identity is retained by unavailable same-account peer {peerLabel}; its cross-pair move must be reconciled before a membership effect. Independent objects remain runnable.";
        }
        foreach (var entry in dropbox)
        {
            var version = await _authority.LoadObjectVersionAsync(entry.ObjectVersionId, cancellationToken);
            if (failedDropboxIdentities.Contains(MembershipPhysicalIdentity(version)))
                result[entry.ObjectId] =
                    $"This exact Dropbox identity is retained by unavailable same-account peer {peerLabel}; its cross-pair move must be reconciled before a membership effect. Independent objects remain runnable.";
        }
        return result;
    }

    private async Task ReserveTemporalCutIdentityDuplicatesAsync(IReadOnlyList<SyncPair> universe,
        IReadOnlyDictionary<string, SyncAuthorityPairObservationSet> observationSets,
        IDictionary<string, HashSet<string>> reservations, CancellationToken cancellationToken)
    {
        var pairs = universe.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var identities = new Dictionary<string, List<(string PairId, string ObjectId)>>(StringComparer.Ordinal);
        foreach (var observationSet in observationSets.Values)
        {
            // A cloud-only pair still contributes its complete observation set to ordinary
            // planning. Its compatibility "local" port is not an NTFS identity, however,
            // and cannot participate in this physical cross-pair move reservation.
            if (!pairs.TryGetValue(observationSet.PairId, out var pair) ||
                SyncCloudPairConfiguration.IsCloudOnly(pair)) continue;
            foreach (var item in observationSet.Local.Select(entry => (Side: "local", Entry: entry))
                         .Concat(observationSet.Dropbox.Select(entry => (Side: "dropbox", Entry: entry))))
            {
                var version = await _authority.LoadObjectVersionAsync(item.Entry.ObjectVersionId,
                    cancellationToken);
                var key = string.Join("|", pair.ConnectionId, item.Side,
                    MembershipPhysicalIdentity(version));
                if (!identities.TryGetValue(key, out var members))
                {
                    members = [];
                    identities[key] = members;
                }
                members.Add((pair.Id, item.Entry.ObjectId));
            }
        }

        foreach (var members in identities.Values.Where(items =>
                     items.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() > 1))
        foreach (var member in members)
        {
            if (!reservations.TryGetValue(member.PairId, out var objects))
            {
                objects = new HashSet<string>(StringComparer.Ordinal);
                reservations[member.PairId] = objects;
            }
            objects.Add(member.ObjectId);
        }
    }

    private static (IReadOnlySet<string> CurrentKeys, IReadOnlySet<string> DeferredPairIds)
        ReserveUnconfirmedOneSidedMemberships(
        IReadOnlyDictionary<string, SyncAuthorityPairObservationSet> observationSets,
        IDictionary<string, HashSet<string>> reservations,
        IReadOnlySet<string> previousPotentialMoveKeys)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        var deferredPairIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observationSet in observationSets.Values)
        {
            var localByObject = observationSet.Local.GroupBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.Ordinal);
            var dropboxByObject = observationSet.Dropbox.GroupBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.Ordinal);
            foreach (var point in observationSet.SynchronizationPoints.Where(item =>
                         item.LocalObjectVersionId is not null && item.DropboxObjectVersionId is not null &&
                         item.LocalMembershipId is not null && item.DropboxMembershipId is not null))
            {
                localByObject.TryGetValue(point.ObjectId, out var local);
                dropboxByObject.TryGetValue(point.ObjectId, out var dropbox);
                local ??= [];
                dropbox ??= [];
                if ((local.Length == 0) == (dropbox.Length == 0)) continue;
                var key = string.Join("|", observationSet.PairId, point.ObjectId,
                    string.Join(",", local.Select(item => string.Join(":", item.RelativePath,
                        item.ObjectVersionId))),
                    string.Join(",", dropbox.Select(item => string.Join(":", item.RelativePath,
                        item.ObjectVersionId))));
                current.Add(key);
                if (previousPotentialMoveKeys.Contains(key)) continue;
                deferredPairIds.Add(observationSet.PairId);
                if (!reservations.TryGetValue(observationSet.PairId, out var objects))
                {
                    objects = new HashSet<string>(StringComparer.Ordinal);
                    reservations[observationSet.PairId] = objects;
                }
                objects.Add(point.ObjectId);
            }
        }
        return (current, deferredPairIds);
    }

    private static string MembershipPhysicalIdentity(SyncAuthorityObjectVersion version)
    {
        using var identity = JsonDocument.Parse(version.IdentityJson);
        var fileId = identity.RootElement.GetProperty("fileId").GetString()
                     ?? throw new InvalidDataException("A retained membership identity has no file ID.");
        if (version.Side == "dropbox") return "dropbox:" + fileId;
        if (version.Side != "local")
            throw new InvalidDataException("A retained membership identity has an unsupported side.");
        var volume = identity.RootElement.GetProperty("volumeSerial").GetString()
                     ?? throw new InvalidDataException("A retained local membership identity has no volume ID.");
        return "local:" + volume + ":" + fileId;
    }

    internal static async Task<SyncAuthorityExecutionResult> RunCrossPairPlanAsync(
        SyncAuthorityCrossPairMoveCoordinator coordinator,
        SyncAuthorityCrossPairMovePlan plan, PilotRunControl runControl, CancellationToken cancellationToken,
        Func<SyncAuthorityLiveAction, Task>? onAction = null)
    {
        var path = plan.SourcePath + " → " + plan.DestinationPath;
        var itemKind = plan.Kind == PilotEntryKind.Directory ? "Folder" : "File";
        var fileType = itemKind == "Folder"
            ? "Folder"
            : string.IsNullOrWhiteSpace(Path.GetExtension(plan.DestinationPath))
                ? "File"
                : Path.GetExtension(plan.DestinationPath).TrimStart('.').ToUpperInvariant();
        if (onAction is not null)
            await onAction(new SyncAuthorityLiveAction(plan.OperationId, path, "Move", "Starting",
                "The source, destination and retained identity are being checked before the move is issued.",
                DateTimeOffset.UtcNow, itemKind, fileType, SyncAuthorityOperationKind.CrossPairMove,
                SyncActivityPhase.Running));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await runControl.WaitBeforeNextEffectAsync(cancellationToken);
            SyncAuthorityExecutionResult result;
            try
            {
                result = await coordinator.ExecuteAsync(plan,
                    "cross-pair-runner-" + Guid.NewGuid().ToString("N"), cancellationToken);
            }
            catch (SyncAuthorityPlanningInvalidatedException)
            {
                var superseded = new SyncAuthorityExecutionResult(plan.OperationId,
                    SyncAuthorityOperationStatus.Superseded, false,
                    "A fresher coordinated observation superseded this cross-pair move at final effect admission.");
                if (onAction is not null)
                    await onAction(new SyncAuthorityLiveAction(plan.OperationId, path, "Move", "Superseded",
                        superseded.Reason, DateTimeOffset.UtcNow, itemKind, fileType,
                        SyncAuthorityOperationKind.CrossPairMove, SyncActivityPhase.Superseded));
                return superseded;
            }
            if (result.Status is not (SyncAuthorityOperationStatus.Ready or
                SyncAuthorityOperationStatus.EffectUnknown))
            {
                if (onAction is not null)
                    await onAction(new SyncAuthorityLiveAction(plan.OperationId, path, "Move",
                        result.Status == SyncAuthorityOperationStatus.Completed ? "Completed" : "Needs attention",
                        result.Reason, DateTimeOffset.UtcNow, itemKind, fileType,
                        SyncAuthorityOperationKind.CrossPairMove,
                        SyncEndpointActivityPresentation.PhaseFor(result.Status)));
                return result;
            }
        }
        if (onAction is not null)
            await onAction(new SyncAuthorityLiveAction(plan.OperationId, path, "Move", "Needs attention",
                "The move did not reach a known state within its bounded recovery attempts.",
                DateTimeOffset.UtcNow, itemKind, fileType, SyncAuthorityOperationKind.CrossPairMove,
                SyncActivityPhase.EffectUnknown));
        throw new IOException(
            "The cross-pair move did not reach a known state within its bounded recovery attempts.");
    }

    private static SyncAuthorityCrossPairMovePlan RehydrateCrossPairPlan(
        SyncAuthorityOperationSnapshot operation)
    {
        using var document = JsonDocument.Parse(operation.PreconditionsJson);
        var root = document.RootElement;
        static string Required(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()!
                : throw new InvalidDataException($"The retained cross-pair operation has no {name}.");
        var direction = Enum.Parse<SyncAuthorityCrossPairMoveDirection>(Required(root, "direction"));
        var kind = Enum.Parse<PilotEntryKind>(Required(root, "entryKind"));
        return new SyncAuthorityCrossPairMovePlan(Required(root, "operationGroupId"),
            operation.OperationId, Required(root, "sourcePairId"), Required(root, "destinationPairId"),
            Required(root, "sourcePath"), Required(root, "destinationPath"), direction, kind);
    }

    // A caller that reserved its run identity before admission passes that exact id here. The cold and
    // normal branches then accept that one id, so the control published before admission and the durable
    // accepted command are the same identity and no second id is generated at acceptance.
    /// <summary>
    /// The typed admission of an already accepted command, for producer entries that must register an operation.
    /// A command without a retained typed admission fails closed: it can never be turned into new effect permission.
    /// </summary>
    private async Task<SyncAuthorityCommandAdmission> RequireRetainedCommandAdmissionAsync(string commandId,
        CancellationToken cancellationToken)
        => await _authority.LoadRetainedCommandAdmissionAsync(commandId, cancellationToken)
           ?? throw new InvalidOperationException(
               "The accepted command retains no typed admission; no operation may be registered from it.");

    private async Task<string> CreateAcceptedCommandAsync(string commandKind, string? pairId, string? targetId,
        string payloadJson, CancellationToken cancellationToken, bool authorizesFileEffects = true,
        Action<string>? admissionProgress = null, string? reservedCommandId = null,
        SyncAuthorityCommandReservation? reservation = null, string? consentId = null)
        => (await CreateAcceptedCommandAdmissionAsync(commandKind, pairId, targetId, payloadJson, cancellationToken,
            authorizesFileEffects, admissionProgress, reservedCommandId, reservation, consentId)).CommandRequestId;

    private async Task<SyncAuthorityCommandAdmission> CreateAcceptedCommandAdmissionAsync(string commandKind, string? pairId, string? targetId,
        string payloadJson, CancellationToken cancellationToken, bool authorizesFileEffects = true,
        Action<string>? admissionProgress = null, string? reservedCommandId = null,
        SyncAuthorityCommandReservation? reservation = null, string? consentId = null)
    {
        // The ONE purpose classifier. A command carries the ordinary normal_effect admission only when it
        // authorizes file effects and is not one of the control kinds that never may, and it carries
        // read_only -- which authorizes no request at all, regardless of its admission sequence -- for a
        // scan (which passes authorizesFileEffects: false) and for the control kinds that authorize no file
        // effect. The purpose is never read from request JSON.
        var purpose = authorizesFileEffects && commandKind is not ("pause" or "resume" or "stop" or
            "cancel_unstarted" or "integrity_check" or "backup")
            ? SyncAuthorityCommandPurpose.NormalEffect
            : SyncAuthorityCommandPurpose.ReadOnly;
        // ONE ticket per accepted command. A caller that already published a pending identity supplies its
        // own reservation; every other entry point mints and RETAINS exactly one here, so the cold attempt,
        // the cold-ineligible fallback and the normal protected path all carry the SAME single-use ticket
        // and the same published identity. A bare reserved identity with no matching reservation keeps no
        // insertion authority, because no public factory can mint a reservation for an arbitrary string.
        if (reservation is null && reservedCommandId is null)
        {
            reservation = SyncAuthorityCommandReservation.Mint();
            reservedCommandId = reservation.CommandId;
        }

        if (_authority.TentativeCommandInstallation is { } tentative)
        {
            var coldCommandId = reservedCommandId ?? "command-" + Guid.NewGuid().ToString("N");
            var revision = BuildIdentity.SourceRevision;
            var coldAdmission = consentId is null && authorizesFileEffects &&
                commandKind is not ("pause" or "resume" or "stop")
                ? await _authority.TryAdmitColdCommandAdmissionAsync(coldCommandId, commandKind, pairId, targetId,
                    payloadJson, Id("profile", revision, CapabilityJson), revision, CapabilityJson,
                    ColdCommandNeedsNoLegacyStartup, cancellationToken, admissionProgress, reservation, purpose)
                : null;
            if (coldAdmission is not null)
            {
                _installationId = tentative.Id;
                _commandsRecovered = true;
                await RegisterRunActivityWriterAsync(coldAdmission, commandKind, cancellationToken);
                return coldAdmission;
            }
            _authority.EndColdCommandPreparation();
            admissionProgress?.Invoke("Admission: retained state changed or startup recovery is required; using normal protected startup and fresh admission.");
        }
        await EnsureAuthorityInitializedAsync(cancellationToken, admissionProgress);
        SyncAuthorityDatabase.CommandAdmissionCheckedForTest?.Invoke(_authority.DatabasePath);
        // Ordinary command admission retains per-checkout schema/connection guards
        // and each executor's row-level preconditions, not a full retained-data scan.
        // Full integrity checks are explicit diagnostics or schema migration checks.
        admissionProgress?.Invoke("Admission: recording the exact command request and accepted state after schema and identity checks.");
        // The reserved ticket keeps the published pending identity, and creation plus acceptance commit as
        // one transaction, so the durable accepted command and the identity the user could already see are
        // always the same command.
        var admission = consentId is null
            ? await _authority.CreateAcceptedCommandAdmissionAsync(reservation, commandKind, pairId,
                targetId, null, payloadJson, purpose, cancellationToken)
            : await _authority.CreateAcceptedCommandAdmissionWithConsentAsync(reservation, consentId,
                commandKind, pairId, targetId, null, payloadJson, purpose, cancellationToken);
        if (reservedCommandId is not null &&
            !string.Equals(reservedCommandId, admission.CommandRequestId, StringComparison.Ordinal))
            throw new InvalidOperationException("The accepted command identity is not the reserved pending identity.");
        await RegisterRunActivityWriterAsync(admission, commandKind, cancellationToken);
        return admission;
    }

    private async Task TryBlockCommandAsync(string commandId, string reason, object? detail = null,
        string detailProperty = "firstSyncProblem")

    {
        try
        {
            var payload = detail is null
                ? JsonSerializer.Serialize(new { reason })
                : JsonSerializer.Serialize(new Dictionary<string, object?>
                    { ["reason"] = reason, [detailProperty] = detail });
            await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                payload, cancellationToken: CancellationToken.None);
        }
        catch
        {
            // A process-restart recovery pass will retain any nonterminal command truth.
        }
    }

    // Ordinary runs, retained-path retries and manual restore keep the same safe
    // command-level failure shape. It is separate from individual file outcomes and uses only
    // the accepted command and captured target identities.
    private async Task RetainRunCommandFailureAsync(string commandId, IEnumerable<SyncPair> targets,
        string code, string stage, Exception exception, string? relativePath = null,
        string? nextAction = null, string? eventTitle = null)
    {
        var message = SyncActivityText.Failure(exception);
        if (!string.IsNullOrWhiteSpace(relativePath))
            message = "Path: " + SyncActivityText.Safe(relativePath) + ". " + message;
        var failure = new
        {
            Code = code, Stage = stage, Message = message, CommandId = commandId,
            RelativePath = string.IsNullOrWhiteSpace(relativePath) ? null : SyncActivityText.Safe(relativePath),
            NextAction = nextAction ?? "Open this run's operation log and retained file outcomes before retrying. Completed files are not assumed to have failed."
        };
        await TryBlockCommandAsync(commandId, code, failure, "commandFailure");
        try
        {
            foreach (var target in targets)
                await RetainRunActivityAsync(new SyncActivityEvent(
                    "", DateTimeOffset.UtcNow,
                    [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics], "Error",
                    "Application", eventTitle ?? "Sync command reported an error", message,
                    target.ConnectionId, target.Id, commandId,
                    EndpointContext: SafeEndpointContext(target)));
        }
        catch (Exception loggingError)
        {
            // Never substitute a logging error for the original failure or
            // change an already recorded file effect.
            RecordActivityLogFailure(loggingError, permanent: true);
        }
    }


    // R474-3: the one typed-problem builder. The phase is the actual product stage, so a validation refusal, an
    // observation failure, a dispatch failure and a follow-up failure are never described by the same claim.
    private SyncFirstSyncProblem FirstSyncProblemJson(SyncPair pair, string commandId, string code, string stage,
        string message, string? relativePath, string nextAction) =>
        new(code, string.IsNullOrWhiteSpace(stage) ? "Reviewed first sync" : stage, SyncActivityText.Safe(message),
            relativePath, SyncActivityText.Safe(nextAction), commandId, pair.Id, pair.Name,
            string.IsNullOrWhiteSpace(pair.ConnectionId) ? null : pair.ConnectionId, DateTimeOffset.UtcNow,
            SafeEndpointContext(pair));

    // R474-3: the failure reason is classified by the actual product stage and the known completed-effect count.
    // Only a demonstrated pre-effect validation refusal may claim that nothing ran.
    private SyncFirstSyncProblem FirstSyncFailureProblem(SyncPair pair, string commandId, string stage,
        int completedEffects, string? affectedPath, Exception error)
    {
        var problem = FirstSyncFailureSummary(pair, commandId, stage, completedEffects, affectedPath);
        return problem with { Message = problem.Message + " Cause: " + SyncActivityText.Safe(
            string.Join(" → ", EnumerateErrors(error).Select(item => item.GetType().Name + ": " + item.Message))) };
    }

    private static IEnumerable<Exception> EnumerateErrors(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            yield return current;
    }

    private SyncFirstSyncProblem FirstSyncFailureSummary(SyncPair pair, string commandId, string stage,
        int completedEffects, string? affectedPath) => stage switch
    {
        "validation" => FirstSyncProblemJson(pair, commandId, "first_sync_plan_rejected", stage,
            "The reviewed choices could not be validated against this pair's retained inventory. No reviewed " +
            "file action was started.", affectedPath,
            "Scan again, review the updated plan, then confirm choices and sync."),
        "preparation" => FirstSyncProblemJson(pair, commandId, "first_sync_preparation_failed", stage,
            "First sync could not prepare the selected locations. No reviewed file action was started.", affectedPath,
            "Check the pair's folder availability and account connection, then try again."),
        "follow-up" => FirstSyncProblemJson(pair, commandId, "first_sync_followup_failed", stage,
            "The reviewed first-sync actions finished, but the follow-up check could not be completed, so this pair " +
            "has no verified final first-sync result.", affectedPath,
            "Check Activity for the completed actions, then scan again to continue first sync."),
        _ when completedEffects > 0 => FirstSyncProblemJson(pair, commandId, "first_sync_partial_failure", stage,
            "The reviewed first sync stopped after " + completedEffects.ToString("N0") + " completed action(s). " +
            "Those completed actions are retained in Activity and every remaining item stays pending review.",
            affectedPath,
            "Check Activity for the completed actions, then open First sync review to continue."),
        _ => FirstSyncProblemJson(pair, commandId, "first_sync_failed", stage,
            "The reviewed first sync stopped before any reviewed action was recorded as complete, so no reviewed " +
            "effect is recorded as complete. Check Activity before continuing.", affectedPath,
            "Check Activity, then open First sync review to continue.")
    };

    // R474-3: the still-pending outcome is stated from the actual decisions, so a blocked, conflicted or stopped
    // reviewed run is never collapsed into generic "items remain" advice and the exact affected path is named.
    private SyncFirstSyncProblem IncompleteFirstSyncProblem(SyncPair pair, string commandId, SyncRunResult result,
        IReadOnlyList<SyncBootstrapReviewItem> remaining)
    {
        var path = FirstPendingPath(result, remaining);
        if (result.NeedsVerification > 0)
            return FirstSyncProblemJson(pair, commandId, "first_sync_needs_attention", "verification",
                $"Completed {result.Uploaded + result.Downloaded:N0} file transfer(s); {result.NeedsVerification:N0} outcome(s) need verification, " +
                $"{result.Failed:N0} failed and {result.NotAttempted:N0} were not attempted. " +
                string.Join("; ", result.Decisions.Where(item => item.Kind is SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending)
                    .Take(3).Select(item => item.RelativePath + ": " + item.Reason)), path,
                "Open this run's operation log and choose Verify outcomes before retrying. Sync Shark must establish what actually completed so it does not copy a completed file again.");
        if (result.Stopped)
            return FirstSyncProblemJson(pair, commandId, "first_sync_stopped", "approval",
                "The reviewed first sync was stopped before it finished. Completed actions are retained and every " +
                "remaining item stays pending review.", path,
                "Review the remaining items, then confirm choices and sync when the pair is ready.");
        if (result.Blocked > 0 || result.Conflicts > 0)
            return FirstSyncProblemJson(pair, commandId, "first_sync_needs_attention", "approval",
                $"Completed {result.Uploaded + result.Downloaded:N0} file transfer(s); {result.Failed:N0} failed, {result.NotAttempted:N0} not attempted, {result.Conflicts:N0} conflicting. " +
                string.Join("; ", result.Decisions.Where(item => item.Kind is SyncDecisionKind.Blocked or SyncDecisionKind.Conflict)
                    .Take(3).Select(item => item.RelativePath + ": " + item.Reason)), path,
                "Open this run's operation log. Resolve the recorded cause, then retry its failed or unattempted files; completed files will not be copied again.");
        var pending = remaining.Count(item => SyncBootstrapActions.IsActionable(item.Group)) + result.Blocked +
                      result.Conflicts;
        return FirstSyncProblemJson(pair, commandId, "first_sync_incomplete", "approval",
            "First sync is not complete: " + pending.ToString("N0") + " item(s) still need review before " +
            "they are copied, and every item that is still waiting stays unchanged.", path,
            "Open First sync review, review the remaining items, then confirm choices and sync.");
    }

    // A captured endpoint context is presentation evidence only; a pair whose endpoints cannot be described is
    // still retained with its exact pair, account and run identity instead of failing the retention.
    private static SyncEndpointContext? SafeEndpointContext(SyncPair pair)
    {
        try { return SyncEndpointContext.Capture(pair); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return null; }
    }

    private static string? FirstPendingPath(SyncRunResult result, IReadOnlyList<SyncBootstrapReviewItem> remaining)
    {
        var uncertain = result.Decisions.FirstOrDefault(decision =>
            decision.Kind is SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending)?.RelativePath;
        if (!string.IsNullOrWhiteSpace(uncertain)) return uncertain;
        var blocked = result.Decisions.FirstOrDefault(decision =>
            decision.Kind is SyncDecisionKind.Blocked or SyncDecisionKind.Conflict)?.RelativePath;
        if (!string.IsNullOrWhiteSpace(blocked)) return blocked;
        return remaining.FirstOrDefault(item => SyncBootstrapActions.IsActionable(item.Group))?.RelativePath;
    }


    // ENG4: one append-only presentation event per terminal first-sync outcome. Activity, its severity and run
    // filters and the Runs surface therefore name the same exact problem and the same exact run.
    private async Task AppendFirstSyncProblemEventAsync(SyncPair pair, string commandId, string severity,
        string message, string? action = null)
    {
        try
        {
            await RetainRunActivityAsync(new SyncActivityEvent(
                "",
                DateTimeOffset.UtcNow,
                [SyncActivityCategories.Sync, SyncActivityCategories.Pairs],
                severity,
                "Folder pair",
                action ?? (severity == "Success" ? "First sync completed" : "First sync needs attention"),
                SyncActivityText.Safe(message),
                string.IsNullOrWhiteSpace(pair.ConnectionId) ? null : pair.ConnectionId,
                pair.Id,
                commandId,
                EndpointContext: SafeEndpointContext(pair)));
        }
        catch (Exception error)
        {
            // Presentation retention never changes a committed command's outcome.
            RecordActivityLogFailure(error, permanent: true);
        }
    }

    public async Task<IReadOnlyList<SyncCommandProblem>> LoadRetainedCommandProblemsAsync(
        CancellationToken cancellationToken = default) =>
        (await LoadRetainedCommandProblemProjectionAsync(cancellationToken)).Problems;

    public async Task<(IReadOnlyList<SyncCommandProblem> Problems,
        IReadOnlyDictionary<string, DateTimeOffset> SuccessfulRunStartedUtcByPair)> LoadRetainedCommandProblemProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!AuthorityStateExists) return ([], new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        var outcomes = await _authority.LoadCommandProblemOutcomeRowsAsync(cancellationToken);
        var problems = new List<SyncCommandProblem>();
        var successes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (pairId, run) in outcomes)
        {
            if (run.ResultJson is null) continue;
            try
            {
                using var document = JsonDocument.Parse(run.ResultJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                var isCommandFailure = document.RootElement.TryGetProperty("commandFailure", out var failure) &&
                                       failure.ValueKind == JsonValueKind.Object;
                if (!isCommandFailure)
                {
                    if (!document.RootElement.TryGetProperty("pairs", out var pairs) ||
                        pairs.ValueKind != JsonValueKind.Array) continue;
                    failure = pairs.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("pairId", out var target) && target.ValueKind == JsonValueKind.String &&
                        target.GetString() == pairId && item.TryGetProperty("failureMessage", out var message) &&
                        message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()));
                    if (failure.ValueKind != JsonValueKind.Object)
                    {
                        // The query admits a nonproblem row only after proving
                        // this exact target's ordinary known-success outcome.
                        // Expose its real start time, not a UI refresh time, so
                        // a repaired CLI/background run can retire only an
                        // older transient window error for the same target.
                        successes[pairId] = run.StartedUtc;
                        continue;
                    }
                }
                string? Read(string name) => failure.EnumerateObject()
                    .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                       property.Value.ValueKind == JsonValueKind.String)
                    .Select(property => property.Value.GetString()).FirstOrDefault();
                problems.Add(new SyncCommandProblem(
                    SyncActivityText.Safe(Read("Code") ?? (isCommandFailure ? "batch_command_failed" : "pair_run_failed")),
                    SyncActivityText.Safe(Read("Stage") ?? (isCommandFailure ? "Sync command" : "Checking or syncing this pair")),
                    SyncActivityText.Safe(Read(isCommandFailure ? "Message" : "failureMessage") ??
                        "The sync command reported an error."),
                    SyncActivityText.Safe(Read("NextAction") ??
                        "Open this run's operation log and retained file outcomes before retrying."),
                    run.Id, pairId, run.CompletedUtc ?? run.StartedUtc));
            }
            catch (JsonException)
            {
                // Malformed history does not become authority or hide another
                // target's independently retained failure.
            }
        }
        return (problems, successes);
    }

    // ENG4: the latest retained first-sync terminal problem of each pair, read from the existing retained command
    // result JSON so it survives a restart without a new table. A newer completed first sync resolves the current
    // problem while the append-only history keeps every earlier failure.
    public async Task<IReadOnlyList<SyncFirstSyncProblem>> LoadRetainedFirstSyncProblemsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!AuthorityStateExists) return [];
        // R474-2: the query is filtered to retained first-sync outcomes, so unrelated run volume can never evict an
        // unresolved problem from the projection, and it is independent of the generic history pagination.
        var history = await _authority.LoadFirstSyncOutcomeRowsAsync(cancellationToken);
        var problems = new List<SyncFirstSyncProblem>();
        var decided = new HashSet<string>(StringComparer.Ordinal);
        var completedPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in history.Where(item => string.Equals(item.Kind, "run_pair", StringComparison.Ordinal)))
        {
            if (run.ResultJson is null) continue;
            try
            {
                using var document = JsonDocument.Parse(run.ResultJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                // The retained JSON is read case-insensitively: the anonymous fields are lower camel case and the
                // typed problem record keeps its declared property names, and both must resolve to one answer.
                var isScan = TryFind(document.RootElement, "firstSyncScanComplete", out var scanComplete);
                var completionKnown = TryFind(document.RootElement, "firstSyncComplete", out var complete);
                var hasProblem = TryFind(document.RootElement, "firstSyncProblem", out var problem) &&
                                 problem.ValueKind == JsonValueKind.Object;
                if (!completionKnown && !isScan && !hasProblem) continue;
                var pairId = TryFind(document.RootElement, "pairId", out var pairValue) &&
                             pairValue.ValueKind == JsonValueKind.String
                    ? pairValue.GetString()
                    : hasProblem ? Text(problem, "pairId") : null;
                if (string.IsNullOrWhiteSpace(pairId) || !decided.Add(pairId + (isScan ? ":scan" : ":apply"))) continue;
                // Rows are newest first. Completed application resolves earlier scan problems too; a completed
                // scan resolves only its own scan problem and never stands in for confirmed/applied choices.
                if (completionKnown && complete.ValueKind == JsonValueKind.True)
                {
                    completedPairs.Add(pairId!);
                    continue;
                }
                if (completedPairs.Contains(pairId!) || isScan && scanComplete.ValueKind == JsonValueKind.True) continue;
                if (!hasProblem) continue;
                // R474-2: the exact captured validated endpoint context, command, phase, code and path are read back
                // from the retained record itself and are never inferred from the current pair.
                var typed = RetainedFirstSyncProblem(problem);
                problems.Add(typed is null
                    ? new SyncFirstSyncProblem(
                        Text(problem, "code") ?? "first_sync_failed",
                        Text(problem, "phase") ?? "Reviewed first sync",
                        Text(problem, "message") ?? "First sync needs attention.",
                        Text(problem, "relativePath"),
                        Text(problem, "nextAction") ?? "Open First sync review to continue.",
                        Text(problem, "commandId") ?? run.Id,
                        pairId!,
                        Text(problem, "pairName") ?? pairId!,
                        Text(problem, "connectionId"),
                        DateTimeOffset.TryParse(Text(problem, "reportedUtc"), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var reported) ? reported : run.StartedUtc,
                        null)
                    : typed with
                    {
                        PairId = string.IsNullOrWhiteSpace(typed.PairId) ? pairId! : typed.PairId,
                        PairName = string.IsNullOrWhiteSpace(typed.PairName) ? pairId! : typed.PairName,
                        CommandId = string.IsNullOrWhiteSpace(typed.CommandId) ? run.Id : typed.CommandId,
                        ReportedUtc = typed.ReportedUtc == default ? run.StartedUtc : typed.ReportedUtc
                    });
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or
                                              ArgumentException)
            {
                // A malformed or legacy result is skipped without dropping any other pair's problem.
                continue;
            }
        }
        return problems;

        static SyncFirstSyncProblem? RetainedFirstSyncProblem(JsonElement problem)
        {
            try
            {
                var value = JsonSerializer.Deserialize<SyncFirstSyncProblem>(problem.GetRawText());
                if (value is not null) SyncEndpointContext.RequireRetainable(value.EndpointContext, value.PairId);
                return value;
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidDataException
                                               or ArgumentException or InvalidOperationException) { return null; }
        }

        static bool TryFind(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            value = default;
            return false;
        }

        static string? Text(JsonElement element, string name) =>
            TryFind(element, name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
    }

    private async Task<PilotRunControl> BeginRunAsync(CancellationToken cancellationToken,
        string? commandId = null, string kind = "operation", string? pairId = null)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another sync command is already active.");
        try { return CreateRunControl(commandId, kind, pairId); }
        catch { _runGate.Release(); throw; }
    }

    private PilotRunControl CreateRunControl(string? commandId, string kind, string? pairId, bool preparing = false)
    {
        var runControl = new PilotRunControl();
        lock (_runControlGate)
        {
            _activeRunControl = runControl;
            _activeRunCheckpoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRunSnapshot = new SyncActiveRunSnapshot(commandId ?? "command-" + Guid.NewGuid().ToString("N"),
                kind, pairId, DateTimeOffset.UtcNow, false, false, null, preparing);
        }
        return runControl;
    }

    // The reserved control stops advertising itself as preparing only when its exact command
    // has been durably accepted, so no reader claims an accepted command before its DB commit.
    private void MarkRunAccepted(PilotRunControl runControl)
    {
        lock (_runControlGate)
            if (ReferenceEquals(_activeRunControl, runControl) && _activeRunSnapshot is { Preparing: true } active)
                _activeRunSnapshot = active with { Preparing = false };
    }

    internal Task<PilotRunControl> BeginRunForTestAsync(CancellationToken cancellationToken = default,
        string? commandId = null) => BeginRunAsync(cancellationToken, commandId);

    private void EndRun(PilotRunControl runControl)
    {
        TaskCompletionSource<bool>? checkpoint = null;
        var owned = false;
        lock (_runControlGate)
        {
            if (ReferenceEquals(_activeRunControl, runControl))
            {
                _activeRunControl = null;
                _activeRunSnapshot = null;
                checkpoint = _activeRunCheckpoint;
                _activeRunCheckpoint = null;
                owned = true;
            }
        }
        if (!owned) return;
        _runGate.Release();
        checkpoint?.TrySetResult(true);
    }

    internal void EndRunForTest(PilotRunControl runControl) => EndRun(runControl);

    private Task NotifyProgressAsync(Func<SyncAuthorityBatchProgress, Task>? onProgress,
        SyncAuthorityBatchProgress progress, Func<Task>? afterProgress = null)
    {
        // Capture, durable admission and presentation-tail assignment have one
        // submission order even when different producers arrive concurrently.
        lock (_progressDeliveryGate)
        {
            string? runId;
            lock (_runControlGate)
            {
                runId = _activeRunSnapshot?.Id;
                if (_activeRunSnapshot is { } active)
                    _activeRunSnapshot = active with { PairId = progress.Pair.Id, Progress = progress };
            }
            var retention = RetainProgressAsync(progress, runId);
            return _progressDeliveryTail = DeliverProgressAsync(_progressDeliveryTail, retention,
                onProgress, progress, afterProgress);
        }
    }

    // Change 1: the console/UI sink is fed at most once per second for each repeated transient progress
    // phase, so per-file and per-path progress no longer costs a formatted, awaited line each. The latest
    // reading is the one forwarded, so the counts carried in the events stay accurate. A simple lock and
    // one monotonic Stopwatch are enough; no timer or background task is introduced.
    private readonly object _progressForwardGate = new();
    private readonly System.Diagnostics.Stopwatch _progressForwardClock = System.Diagnostics.Stopwatch.StartNew();
    private string? _progressForwardPhase;
    private long _progressForwardTicks;
    private const long ProgressForwardIntervalMilliseconds = 1000;

    // Non-transient records, warnings/errors, phase completions and the first event of a new phase always
    // reach the sink. Only a repeated transient phase within one second is folded away; the retained
    // history still receives every event.
    private bool ShouldForwardProgress(SyncAuthorityBatchProgress progress)
    {
        var (_, phase, transient) = ClassifyProgress(progress);
        if (!transient) return true;
        var now = _progressForwardClock.ElapsedMilliseconds;
        lock (_progressForwardGate)
        {
            if (!string.Equals(_progressForwardPhase, phase, StringComparison.Ordinal) ||
                now - _progressForwardTicks >= ProgressForwardIntervalMilliseconds)
            {
                _progressForwardPhase = phase;
                _progressForwardTicks = now;
                return true;
            }
            return false;
        }
    }

    private async Task DeliverProgressAsync(Task predecessor, Task retention,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, SyncAuthorityBatchProgress progress,
        Func<Task>? afterProgress)
    {
        // Do not run callbacks under either state lock. A previous caller's failed
        // optional sink cannot suppress this event or poison all later delivery.
        await Task.Yield();
        try { await predecessor.ConfigureAwait(false); }
        catch { /* The original caller observes its own callback failure. */ }
        await retention.ConfigureAwait(false);
        try
        {
            lock (_cloudProviderGate)
                if (_cloudProviders.TryGetValue(progress.Pair.Id, out var provider))
                    provider.PublishSyncStatus(progress.State is "Checking" or "Comparing" or "Running" ||
                        progress.Action?.Stage == "Starting", progress.FailureMessage is not null || progress.State is "Blocked" or "Failed");
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        { /* Shell presentation must never interrupt durable sync or change a file's in-sync flag. */ }
        if (onProgress is not null && ShouldForwardProgress(progress))
            try { await onProgress(progress); }
            catch { /* Presentation never owns durable synchronization authority. */ }
        if (afterProgress is not null) await afterProgress();
    }

    // Preparation belongs to the same retained CLI/GUI operation stream as observation and effects.
    // Every executing consumer uses this projection; a missing presentation callback must not
    // suppress the journal. Read-only consumers may still call the preparation primitive directly.
    private Task<PreparedPair> PreparePairWithProgressAsync(SyncPair pair, CancellationToken cancellationToken,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, bool forceReviewedDeletionEffects = false) =>
        PreparePairAsync(pair, cancellationToken, forceReviewedDeletionEffects, progress =>
            NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Checking", null, null,
                null, Comparison: progress)));

    private async Task<PreparedPair> PreparePairAsync(SyncPair pair, CancellationToken cancellationToken,
        bool forceReviewedDeletionEffects = false,
        Func<SyncComparisonProgress, Task>? onPreparationProgress = null)
    {
        stateAuthority.VerifyAuthority();
        // CC4B-3: an explicit cloud/cloud pair routes to the CC4A prepared context BEFORE any NTFS-local-root
        // code below, so neither endpoint is ever opened, verified or interpreted as pair.LocalRoot.
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "An explicit cloud/cloud pair requires the Windows private staging allocator.");
            return await ReadStepAsync("Verifying both saved cloud endpoint accounts, folder identities and private staging bindings",
                () =>
                {
                    // The analyzer cannot carry the outer platform guard into a callback.
                    if (!OperatingSystem.IsWindows())
                        throw new PlatformNotSupportedException(
                            "An explicit cloud/cloud pair requires the Windows private staging allocator.");
                    return PrepareCloudPairAsync(pair, cancellationToken);
                });
        }
        if (IsLocalPair(pair))
            return await ReadStepAsync("Verifying both saved local root identities, overlap rules and private staging/recovery bindings",
                () => PrepareLocalPairAsync(pair, cancellationToken, forceReviewedDeletionEffects));
        if (string.Equals(SyncProviders.Normalize(pair.Provider), SyncProviders.GoogleDrive,
                StringComparison.Ordinal))
            return await ReadStepAsync("Verifying the saved Google Drive account, folder identity, local root and staging/recovery bindings",
                () => PrepareGoogleDrivePairAsync(pair, cancellationToken, forceReviewedDeletionEffects));
        async Task<T> ReadStepAsync<T>(string description, Func<Task<T>> operation)
        {
            if (onPreparationProgress is null) return await operation();
            var started = DateTimeOffset.UtcNow;
            async Task ReportAsync(string detail)
            {
                try
                {
                    await onPreparationProgress(new SyncComparisonProgress("Checking endpoint bindings",
                        pair.Name, 0, 0, started, DateTimeOffset.UtcNow, detail, "checks"));
                }
                catch { /* Diagnostic presentation cannot replace an operation's result or failure. */ }
            }
            await ReportAsync("Starting: " + description + " No file transfer is running.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await operation();
                elapsed.Stop();
                await ReportAsync($"Completed: {description} Operation elapsed {elapsed.Elapsed.TotalSeconds:F3}s; feedback delivery is excluded.");
                return result;
            }
            catch
            {
                elapsed.Stop();
                // Do not include exception/provider/credential payloads here. The caller's existing
                // sanitized retained failure remains authoritative, including cancellation.
                await ReportAsync($"Stopped before completion: {description} Operation elapsed {elapsed.Elapsed.TotalSeconds:F3}s; no successful check is claimed.");
                throw;
            }
        }
        async Task RunStepAsync(string description, Func<Task> operation)
        {
            await ReadStepAsync(description, async () => { await operation(); return true; });
        }

        var legacy = legacyRuntimeStore.LoadPair(pair.Id);
        if (legacy.PendingTransfer is not null)
            throw new InvalidOperationException(
                "This pair retains a legacy pending transfer. The full-client runtime will neither import nor replay it; use the signed v0.2.4 rollback path before migration.");
        if (!await ReadStepAsync("Checking the retained folder-pair definition",
                () => _authority.IsPairDefinitionRetainedAsync(pair.Id, cancellationToken)))
            throw new InvalidOperationException(
                "This is a read-only v0.2.4 migration candidate. Rebind and verify its roots before the full-client runtime can plan or execute work.");
        var connection = connectionStore.Load(pair.ConnectionId)
                         ?? throw new InvalidOperationException("Connect the verified Dropbox account first.");
        if (!string.IsNullOrWhiteSpace(pair.ConnectionId) &&
            !string.Equals(pair.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This pair belongs to a different Dropbox connection. Reconnect that exact account; account identities are never rebound silently.");
        if (string.IsNullOrWhiteSpace(await ReadStepAsync("Checking protected credential availability for the saved account",
                () => credentialStore.ForConnection(connection).ReadAsync(cancellationToken))))
            throw new InvalidOperationException("The protected Dropbox credential is unavailable.");
        var initializationWasWarm = _installationId is not null;
        var installationId = await ReadStepAsync(initializationWasWarm
                ? "Reading this running host's initialized installation identity"
                : "Initializing the retained installation and recovering command records",
            () => EnsureAuthorityInitializedAsync(cancellationToken));
        using var localRoot = await ReadStepAsync("Opening the local folder against its saved native volume and file identity",
            () => Task.FromResult(PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId))));
        var remote = remoteFactory?.Invoke(pair, connection) ??
                     new DropboxPilotClient(httpClient,
                         AccountTokenSource(connection),
                         pair.DropboxRoot, connection, pair.DropboxFolderId, _transferLimiter);
        await RunStepAsync("Verifying the selected Dropbox account, namespace and folder identity with the provider",
            () => remote.VerifyRootIdentityAsync(cancellationToken));
        if (pair.ControlNamespace is null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "Migrated pair activation requires the Windows same-volume control allocator.");
            // A v0.2.4 pair has verified local/Dropbox root identities but predates the full-client
            // same-volume control binding. Allocate that private control area lazily on its first
            // real run after both roots have been reverified. This is a local metadata transition;
            // it performs no Dropbox content effect and leaves first discovery effect-free.
            var provisional = await RegisterPairAuthorityAsync(pair, connection, installationId, localRoot,
                cancellationToken, RunStepAsync);
            var allocatedControl = await new SyncAuthorityLocalControlNamespaceAllocator(_authority).BindAsync(
                localRoot, installationId, pair.Id, provisional.LocalRootBindingId, pair.PolicyRevision,
                provisional.ProfileId, cancellationToken, partitionCandidate: pair);
            pair = pair with
            {
                ControlNamespace = new SyncControlNamespaceBinding(installationId, pair.Id, allocatedControl)
            };
            var retained = await _authority.LoadPairSetAsync(cancellationToken);
            var selected = string.Equals(retained.SelectedPairId, pair.Id, StringComparison.Ordinal);
            await _authority.RetainPairDefinitionAsync(pair, selected, cancellationToken);
        }
        using var localControl = SyncControlNamespace.OpenLocal(pair.ControlNamespace, localRoot);
        if (pair.RecoveryRoot is not null)
        {
            SyncDropboxArtifactPaths.RequireRecoveryContainer(pair.RecoveryRoot.RootPath);
            await RunStepAsync("Verifying the dedicated recovery folder's provider account, namespace and identity",
                () => remote.VerifyRecoveryRootAsync(pair.RecoveryRoot, cancellationToken));
        }
        var registration = await RegisterPairAuthorityAsync(pair, connection, installationId, localRoot,
            cancellationToken, RunStepAsync);

        var recoveryReady = pair.RecoveryRoot is not null;
        var options = new SyncAuthorityPlannerOptions(pair.PolicyRevision, registration.ProfileId,
            pair.ControlNamespace.NamespaceId, registration.DropboxRecoveryAuthorityId,
            EnableCreateEffects: true,
            EnableReplacementEffects: recoveryReady,
            EnableDeletionEffects: recoveryReady && (pair.PropagateDeletions || forceReviewedDeletionEffects),
            EnableMoveEffects: true,
            DeletionMinimumCompleteObservations: 1,
            DeletionMinimumInterval: TimeSpan.Zero,
            Direction: SyncEndpointEngineLayout.EngineDirection(pair),
            RemoteUploadSessionPolicy: SyncUploadLimits.ForStore(remote));
        var cloudFiles = pair.FilesOnDemandEnabled
            ? GetOrStartCloudProvider(pair, remote, out _)
            : null;
        return new PreparedPair(remote, registration.LocalRootBindingId,
            registration.DropboxRootBindingId, options, cloudFiles);
    }

    /// <summary>
    /// CC4B-3 correction: the ONE pair-policy projection. Both a cloud/cloud policy edit and the existing
    /// legacy registration write exactly these requested facts through this one mapping, so a pair's chosen
    /// retention and deletion policy is never silently replaced by a default configuration. It reads only the
    /// retained pair, opens no local root and calls no provider.
    /// </summary>
    private static SyncAuthorityPairConfiguration PairPolicyConfiguration(SyncPair pair) =>
        SyncAuthorityPairConfiguration.FromPair(pair);

    private async Task<PairPolicyPublicationResult> RegisterPairPolicyAsync(SyncPair pair,
        CancellationToken cancellationToken, string? replacingPairId = null, bool select = true)
    {
        await EnsureAuthorityInitializedAsync(cancellationToken);
        // The ONE caller that authors an unpublished replacement candidate supplies the pair this operation
        // replaces atomically inside the final publication transaction; the guard still compares the candidate
        // against EVERY other retained pair, so a genuinely separate owner, unresolved work, active lease,
        // private control or ambiguous authority still refuses.
        await RequireVerifiedPairPartitionsAsync(pair, replacingPairId, cancellationToken);
        // root726/727: the authoritative PUBLISHED prior is read ONCE here from the same published-definition
        // filter the whole-set reader applies (pair_json IS NOT NULL, retired rows included), now bound to this
        // exact pair, and drives whether this edit is a published same-root edit that must take the ONE atomic
        // publication. Reading it up front lets the cloud/cloud branch use the same predicate.
        var authoritativePolicyPrior = await _authority.LoadPairDefinitionAsync(pair.Id, cancellationToken);
        var publishedPolicyPrior = replacingPairId is null && authoritativePolicyPrior is not null &&
            SameRootIdentity(authoritativePolicyPrior, pair) &&
            pair.PolicyRevision > authoritativePolicyPrior.PolicyRevision ? authoritativePolicyPrior : null;
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
        {
            // CC4B-3: a cloud/cloud pair has no selected local root, no NTFS volume and no single provider
            // connection, so a policy edit retains only the pair's own new configuration revision beside the
            // endpoints and root bindings the CC4A preparation already retained. It opens no local root,
            // browses no provider and performs no sync. Every local and legacy caller keeps the identical
            // path below unchanged.
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "An explicit cloud/cloud pair requires the Windows private staging allocator.");
            stateAuthority.VerifyAuthority();
            await EnsureAuthorityInitializedAsync(cancellationToken);
            RequireCloudOnlyProjection(pair);
            if (publishedPolicyPrior is not null)
            {
                // root727: a published cloud/cloud policy edit commits the revision advance AND the definition in
                // ONE transaction on its OWN path, so it cannot leave the revision ahead of the definition. The
                // caller's intended selection is carried into that one write; no local root is invented.
                var cloudCommitted = await _authority.CommitCloudPairPolicyEditAsync(pair,
                    PairPolicyConfiguration(pair), publishedPolicyPrior, publishedPolicyPrior.PolicyRevision,
                    select, cancellationToken);
                return new PairPolicyPublicationResult(cloudCommitted, true);
            }
            await _authority.RegisterPairAsync(pair.Id, pair.PolicyRevision, PairPolicyConfiguration(pair),
                cancellationToken);
            return PairPolicyPublicationResult.Retained;
        }
        if (IsLocalPair(pair))
        {
            await PrepareBoundLocalEndpointsAsync(pair, cancellationToken);
            return PairPolicyPublicationResult.Retained;
        }
        // A policy edit preserves the exact provider/account and local root binding. It does not
        // browse the provider, allocate storage or perform a sync while approving a reviewed plan.
        using var local = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var installationId = await EnsureAuthorityInitializedAsync(cancellationToken);
        // root726/727: this entry point publishes a pair and both roots, so a published same-root edit must take
        // the SAME atomic publication as the authoring path. The authoritative prior was read once above; the
        // caller's INTENDED selection is threaded into that ONE write, and the committed pair set is propagated so
        // no caller writes a second, stale definition.
        if (IsGooglePair(pair))
        {
            var binding = _googleDriveConnections.Load(pair.ConnectionId)
                ?? throw new GoogleDriveBlockedException("Reconnect this pair's exact Google Drive account first.");
            GoogleDriveProviderConfiguration.ValidateConnection(binding);
            if (binding.ConnectionId != pair.ConnectionId)
                throw new GoogleDriveBlockedException("The pair belongs to a different Google Drive account.");
            var googleRegistration = await RegisterGooglePairAuthorityAsync(pair, binding, installationId, local,
                cancellationToken, publishedPrior: publishedPolicyPrior, select: select);
            return googleRegistration.DefinitionCommitted && googleRegistration.CommittedPairSet is not null
                ? new PairPolicyPublicationResult(googleRegistration.CommittedPairSet, true)
                : PairPolicyPublicationResult.Retained;
        }
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        var registration = await RegisterPairAuthorityAsync(pair, connection, installationId, local,
            cancellationToken, publishedPrior: publishedPolicyPrior, select: select);
        return registration.DefinitionCommitted && registration.CommittedPairSet is not null
            ? new PairPolicyPublicationResult(registration.CommittedPairSet, true)
            : PairPolicyPublicationResult.Retained;
    }

    /// <summary>root723: the shared Host action for the interrupted-publication recovery, so the same repair the
    /// database performs is reachable through the ordinary consumers rather than only through the DB type. It
    /// takes the existing configuration-mutation gate because it writes authoritative state.</summary>
    public async Task<bool> RecoverInterruptedPublicationAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await _authority.RecoverInterruptedSameRootPublicationAsync(pairId, cancellationToken);
    }

    /// <summary>
    /// root727: the READ-ONLY companion to the guarded repaired action above, so the startup surface can decide
    /// whether to OFFER the repair (and name the affected pair) without performing it. It takes no mutation gate,
    /// writes nothing and opens no provider.
    /// </summary>
    public Task<IReadOnlyList<SyncAuthorityDatabase.SyncInterruptedPublicationIncident>>
        InterruptedPublicationIncidentsAsync(CancellationToken cancellationToken = default) =>
        _authority.DetectInterruptedSameRootPublicationsAsync(cancellationToken);

    private async Task<PairAuthorityRegistration> RegisterPairAuthorityAsync(SyncPair pair,
        DropboxConnectionBinding connection, string installationId, PilotDirectoryLease localRoot,
        CancellationToken cancellationToken, Func<string, Func<Task>, Task>? onStep = null,
        SyncPair? replacingPair = null, long? recoveredExpectedUnpublishedRevision = null,
        SyncPair? publishedPrior = null, bool select = true)
    {
        Task StepAsync(string description, Func<Task> operation) => onStep is null
            ? operation() : onStep(description, operation);
        localRoot.VerifyBinding();
        var sourceRevision = BuildIdentity.SourceRevision;
        var profileId = Id("profile", sourceRevision, CapabilityJson);
        await StepAsync("Validating the current database schema, receipts and installation/capability registration",
            () => _authority.InitializeAsync(installationId, profileId, sourceRevision, CapabilityJson,
                cancellationToken));

        var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox,
            connection.AccountId, connection.RootNamespaceId, connection.HomeNamespaceId);
        await StepAsync("Registering and confirming the exact saved provider-account tuple",
            () => _authority.RegisterProviderAccountAsync(providerAccountId, installationId, connection.AccountId,
                connection.RootNamespaceId, connection.HomeNamespaceId, "Dropbox", 1, cancellationToken,
                SyncProviders.Dropbox));
        // F15/V2: the Dropbox Host registration resolves the volume id through the ONE shared resolver so
        // it reuses the exact volume row and id a local/local pair (or any earlier caller) already retained
        // for this installation and normalized serial.
        string volumeId = null!;
        await StepAsync("Checking the local volume's journal capability and resolving its retained identity",
            async () =>
            {
                volumeId = await _authority.ResolveVolumeIdAsync(installationId,
                    pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture), "NTFS", null,
                    supportsFileId128: false,
                    supportsUsn: NtfsUsnJournal.TryReadCheckpoint(localRoot.Path, out _),
                    supportsAtomicRename: true, cancellationToken);
            });
        var volumeSerialHex = pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
        var localFileIdHex = pair.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture);
        var localRootId = Id("root-local", pair.Id, volumeId, localFileIdHex);
        var dropboxRootId = Id("root-dropbox", pair.Id, connection.RootNamespaceId, pair.DropboxFolderId);
        SyncAuthorityDatabase.AccountPairReplacementRoots? replacementRoots = replacingPair is null ? null : new(
            installationId, replacingPair.Id, replacingPair.PolicyRevision, localRootId, volumeId,
            volumeSerialHex, localFileIdHex, dropboxRootId, providerAccountId, connection.RootNamespaceId);
        var definitionCommitted = false;
        SyncLocalControlNamespaceBinding? createdControl = null;
        SyncPairSet? committedPairSet = null;
        var recoveryRootBindingId = pair.RecoveryRoot is null ? null : Id("recovery-root", providerAccountId,
            pair.RecoveryRoot.RootFolderId, pair.RecoveryRoot.RootNamespaceId);
        var dropboxRecoveryAuthorityId = recoveryRootBindingId ?? Id("recovery-disabled", pair.Id);
        if (replacementRoots is not null)
            await StepAsync("Preparing unpublished replacement roots; the current pair remains active",
                () => _authority.PrepareAccountPairReplacementAsync(pair, PairPolicyConfiguration(pair),
                    replacementRoots, cancellationToken));
        else if (recoveredExpectedUnpublishedRevision is { } recoveredRevision)
        {
        // R668: the private retained-control recovery publication is the ONLY caller that reaches here with a
        // revision. This pair's own two roots are still the exact blocked preimages preparation wrote, and the
        // already-authenticated retained recovery of this pair/side/root/generation is re-proved inside the same
        // transaction, which also registers the policy and activates BOTH roots atomically. A second-side refusal
        // rolls back the first side, the pair row and the policy registration.
        var recoveredRoots = new SyncAuthorityDatabase.RecoveredCandidateOwnRoots(installationId, pair.Id,
            recoveredRevision, localRootId, volumeId, volumeSerialHex, localFileIdHex, dropboxRootId,
            providerAccountId, connection.RootNamespaceId, pair.DropboxFolderId);
        await StepAsync("Activating the recovered unpublished pair's exact own blocked preimages and policy revision",
            () => _authority.ActivateRecoveredCandidateOwnRootsAsync(pair, recoveredRoots,
                PairPolicyConfiguration(pair), cancellationToken));
        }
        else if (publishedPrior is not null)
        {
        // OV702/703: a published same-root edit commits the revision advance, both guarded root registrations
        // and the pair definition in ONE transaction, so an overlap/identity refusal rolls the revision back
        // with it instead of leaving the definition uncommitted behind an advanced revision. The retained
        // private-control preimage is read here and re-proved INSIDE that transaction.
        var controlPreimage = await _authority.LoadPairControlNamespacePreimageAsync(pair.Id, "local",
            cancellationToken);
        var preimageNamespace = pair.Id;
        var preimageAllocation = "control-prepared";
        var preimageIdentity = "{}";
        var preimageMarker = "{}";
        var preimageHash = new string('0', 64);
        var preimageGeneration = 1L;
        string? preparedIdentity = null;
        string? preparedMarker = null;
        string? preparedHash = null;
        string? preparedPath = null;
        SyncAuthorityLocalControlNamespaceAllocator.SyncPreparedControlPublication? prepared = null;
        if (controlPreimage is not null)
        {
            preimageNamespace = controlPreimage.NamespaceId;
            preimageAllocation = controlPreimage.AllocationId;
            preimageIdentity = controlPreimage.IdentityJson;
            preimageMarker = controlPreimage.MarkerIdentityJson;
            preimageHash = controlPreimage.OwnerMarkerHash;
            preimageGeneration = controlPreimage.BindingGeneration;
        }
        else
        {
            // root723: the pair has no ACTIVE control ownership yet. Prepare it - the physical directory and
            // marker, with the durable operation/lease/allocation authorisation and truthful receipt/reconcile -
            // WITHOUT publishing, then let the atomic transaction publish that ownership and terminalise the
            // prepared operation together with the revision, roots and definition.
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "Preparing a local control namespace requires Windows folder identities.");
            prepared = await new SyncAuthorityLocalControlNamespaceAllocator(_authority)
                .PrepareForPublicationAsync(localRoot, installationId, pair.Id, localRootId,
                    publishedPrior.PolicyRevision, profileId, cancellationToken);
            var preparedControl = prepared.Binding;
            preparedIdentity = JsonSerializer.Serialize(new
            {
                volumeSerial = preparedControl.RootVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                fileId = preparedControl.RootFileId.ToString("x16", CultureInfo.InvariantCulture)
            });
            preparedMarker = preparedControl.MarkerFileId;
            preparedHash = preparedControl.MarkerContentHash;
            preparedPath = preparedControl.RootPath;
            createdControl = preparedControl;
            // root708-1 generalised to the prepared path: the published definition must carry the newly created
            // private-control ownership, so bind it into the candidate BEFORE the atomic serialization.
            pair = pair with
            {
                ControlNamespace = new SyncControlNamespaceBinding(installationId, pair.Id, preparedControl)
            };
        }
        try
        {
            await StepAsync("Committing the published same-root edit atomically (revision, roots and definition)",
                async () => committedPairSet = await _authority.CommitPublishedSameRootEditAsync(pair,
                    PairPolicyConfiguration(pair), publishedPrior,
                    publishedPrior.PolicyRevision, localRootId, 1, volumeId, volumeSerialHex, localFileIdHex,
                    dropboxRootId, 1, installationId, providerAccountId, connection.RootNamespaceId,
                    pair.DropboxFolderId, select: select, preimageNamespace, preimageAllocation, preimageIdentity,
                    preimageMarker, preimageHash, preimageGeneration, recoveryRootBindingId, pair.RecoveryRoot?.RootPath,
                    pair.RecoveryRoot?.RootFolderId, pair.RecoveryRoot?.RootNamespaceId, 1,
                    preparedIdentityJson: preparedIdentity, preparedMarkerFileId: preparedMarker,
                    preparedMarkerHash: preparedHash, preparedControlRootPath: preparedPath,
                    preparedLease: prepared?.Renewal.Lease, cancellationToken: cancellationToken));
            definitionCommitted = true;
        }
        finally
        {
            // root723: the prepared allocation's live fence is released by the caller after the publication, so
            // a refusal cannot strand it.
            if (prepared is not null)
            {
                await prepared.Renewal.DisposeAsync();
                if (prepared.Admission.Purpose == SyncAuthorityCommandPurpose.ControlAllocation)
                    await _authority.TransitionCommandRequestAsync(prepared.Admission.CommandRequestId,
                        definitionCommitted ? "completed" : "blocked", cancellationToken: CancellationToken.None);
            }
        }
        }
        else
        {
        await StepAsync("Registering the pair's exact policy revision and configuration guards",
            () => _authority.RegisterPairAsync(pair.Id, pair.PolicyRevision, PairPolicyConfiguration(pair),
                cancellationToken));
        await StepAsync("Registering the local folder identity and checking duplicate/overlapping roots",
            () => _authority.RegisterLocalRootBindingAsync(localRootId, pair.Id, 1, installationId, volumeId,
                volumeSerialHex, localFileIdHex, pair.LocalRoot, cancellationToken, partitionCandidate: pair));
        await StepAsync("Registering the Dropbox folder identity and checking account/namespace/overlap guards",
            () => _authority.RegisterDropboxRootBindingAsync(dropboxRootId, pair.Id, 1, installationId,
                providerAccountId, connection.RootNamespaceId, pair.DropboxFolderId, pair.DropboxRoot,
                cancellationToken, partitionCandidate: pair));
        }

        // root708-r8(2): the atomic branch already validated and upserted the dedicated recovery root INSIDE
        // its own transaction, so a refusal there rolls the whole edit back rather than reporting failure
        // after the revision and definition committed. Only the non-atomic paths register it here.
        if (recoveryRootBindingId is not null && !definitionCommitted)
            await StepAsync("Registering the verified provider recovery folder authority",
                () => _authority.RegisterDropboxRecoveryRootAsync(recoveryRootBindingId, installationId,
                    providerAccountId, pair.RecoveryRoot!.RootPath, pair.RecoveryRoot.RootFolderId,
                    pair.RecoveryRoot.RootNamespaceId, 1, cancellationToken));
        return new PairAuthorityRegistration(profileId, localRootId, dropboxRootId,
            dropboxRecoveryAuthorityId, replacementRoots, definitionCommitted, createdControl, committedPairSet);
    }

    public const string GoogleDriveActivationRequirement =
        "Google Drive source integration is present in this build, but activation requires a registered " +
        "Google Cloud OAuth client, the restricted drive scope consent, a protected per-account refresh " +
        "token store and a token endpoint. Until those are supplied the Google Drive provider fails " +
        "closed and no Dropbox behaviour is affected.";

    public IReadOnlyList<GoogleDriveConnectionBinding> LoadGoogleDriveConnections() =>
        _googleDriveConnections.LoadActiveBindings();

    public GoogleDriveConnectionBinding? LoadGoogleDriveConnection(string? connectionId = null) =>
        _googleDriveConnections.Load(connectionId);

    public GoogleDriveCapabilities GoogleDriveCapabilitiesFor(GoogleDriveConnectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return GoogleDriveProviderProfiles.Capabilities(binding.SharedDrivesEnabled);
    }

    private GoogleDriveAccessTokenSource GoogleTokenSource(GoogleDriveConnectionBinding binding) =>
        _googleTokens.GetOrAdd(binding.ConnectionId, _ => new GoogleDriveAccessTokenSource(
            GoogleCredentials, GoogleEndpoint, binding));

    // Single provider-routed entry point so a pair's provider decides the adapter. Dropbox keeps its
    // existing client and behaviour; the override factory stays available for controlled tests.
    private async Task<ISyncRemoteStore> CreateRemoteStoreForPairAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        if (IsLocalPair(pair)) return (await PrepareLocalPairAsync(pair, cancellationToken, false)).Remote;
        var kind = RequireRegisteredCloudPairKind(pair);
        var connection = kind == SyncEndpointKind.Dropbox
            ? await RequireConnectionAsync(cancellationToken, pair.ConnectionId) : null;
        return CreateRemoteStoreForPair(pair, connection);
    }

    // Account selection and transport dispatch both use exact provider identity, never display text
    // or a catch-all Dropbox branch. New providers must supply an adapter before effects are enabled.
    private static SyncEndpointKind RequireRegisteredCloudPairKind(SyncPair pair)
    {
        if (!SyncEndpointProviders.TryGet(SyncProviders.Normalize(pair.Provider), out var provider))
            throw new NotSupportedException("The saved pair provider has no registered cloud adapter.");
        return provider!.Kind;
    }

    private async Task RequirePairAccountAsync(SyncPair pair, string expectedConnectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedConnectionId))
            throw new InvalidOperationException("Select the exact account belonging to this folder pair.");
        if (IsGooglePair(pair))
        {
            var binding = _googleDriveConnections.Load(pair.ConnectionId)
                ?? throw new GoogleDriveBlockedException("Reconnect this pair's Google Drive account first.");
            GoogleDriveProviderConfiguration.ValidateConnection(binding);
            if (pair.ConnectionId != expectedConnectionId || binding.ConnectionId != expectedConnectionId)
                throw new InvalidOperationException("The selected account does not own this folder pair.");
        }
        else
        {
            var account = await RequireConnectionAsync(cancellationToken, expectedConnectionId);
            if ((connectionStore.Load(pair.ConnectionId)?.ConnectionId ?? pair.ConnectionId) != account.ConnectionId)
                throw new InvalidOperationException("The selected account does not own this folder pair.");
        }
    }

    internal ISyncRemoteStore CreateRemoteStoreForPair(SyncPair pair, DropboxConnectionBinding? connection)
    {
        return RequireRegisteredCloudPairKind(pair) switch
        {
            SyncEndpointKind.GoogleDrive => CreateGoogleDriveClient(pair),
            SyncEndpointKind.Dropbox when connection is not null =>
                remoteFactory?.Invoke(pair, connection) ?? CreateDropboxClient(pair, connection),
            SyncEndpointKind.Dropbox => throw new InvalidOperationException("Connect the verified Dropbox account first."),
            _ => throw new NotSupportedException("The selected provider has no cloud adapter in this build.")
        };
    }

    private GoogleDrivePilotClient CreateGoogleDriveClient(SyncPair pair)
    {
        if (string.IsNullOrWhiteSpace(pair.ConnectionId))
            throw new GoogleDriveBlockedException("This pair has no retained Google Drive account identity.");
        var binding = _googleDriveConnections.Load(pair.ConnectionId)
                      ?? throw new GoogleDriveBlockedException(
                          "This pair is bound to a Google Drive account that is not connected. Reconnect " +
                          "the account before syncing this pair.");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        if (binding.ConnectionId != pair.ConnectionId)
            throw new GoogleDriveBlockedException("The Google Drive account no longer matches the retained pair.");
        return new GoogleDrivePilotClient(httpClient, GoogleTokenSource(binding), binding, pair.DropboxFolderId,
            pair.DropboxRoot, _transferLimiter, allowSharedDrives: binding.SharedDrivesEnabled);
    }

    // A connection id that belongs to the Google Drive store and not to the Dropbox store selects the
    // Drive provider for provider-routed read surfaces.
    internal bool IsGoogleConnection(string? connectionId) =>
        _googleDriveConnections.Load(connectionId) is not null && connectionStore.Load(connectionId) is null;

    internal static bool IsGooglePair(SyncPair pair) =>
        string.Equals(SyncProviders.Normalize(pair.Provider), SyncProviders.GoogleDrive,
            StringComparison.Ordinal);

    // Drive-specific capability explanation for a surface this build does not implement for Drive.
    internal static GoogleDriveBlockedException DriveCapabilityRefusal(string outcome, string detail) =>
        new("Google Drive does not provide " + outcome + " in this build. " + detail);

    private static void RequireRecoveryBrowserSupported(SyncPair pair)
    {
        if (SyncProviders.Normalize(pair.Provider) is not (SyncProviders.Dropbox or SyncProviders.GoogleDrive))
            throw new NotSupportedException("This provider has no retained-copy recovery browser.");
    }

    private async Task<string> RecoveryAccountDisplayAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        if (!IsGooglePair(pair))
            return ConnectionDisplay(await RequireConnectionAsync(cancellationToken, pair.ConnectionId));
        await RequirePairAccountAsync(pair, pair.ConnectionId, cancellationToken);
        var account = LoadAccountBinding(pair.ConnectionId)!;
        return account.Email + " (" + account.DisplayName + ")";
    }

    private static Task<SyncArchiveResult?> GetBoundRecoveryAsync(SyncPair pair, ISyncRemoteStore remote,
        string path, CancellationToken cancellationToken)
    {
        var binding = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        if (IsGooglePair(pair)) return remote.GetRecoveryAsync(binding, path, cancellationToken);

        var exactPath = PilotPath.NormalizeDropboxRoot(path);
        var prefix = PilotPath.NormalizeDropboxRoot(binding.RootPath) + "/";
        if (!exactPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained file is outside its exact Recovery root.");
        var name = exactPath[prefix.Length..];
        // Current writers create a single deterministic name under the bound root. Older retained
        // artifacts use pair/operation paths and must keep their strict legacy path validation.
        return name.Contains('/')
            ? remote.GetRecoveryAsync(SyncDropboxArtifactPaths.NormalizeRecoveryOperationPath(exactPath), cancellationToken)
            : remote.GetRecoveryAsync(binding, name, cancellationToken);
    }

    // Google Drive uses the same product Recovery layout with its own account and folder identities.
    // Files On-Demand uses provider-stamped identities and verified Google binary hydration.
    [SupportedOSPlatform("windows")]
    private async Task<SyncPairBindingResult> BindGoogleDrivePairAsync(SyncPairBindingRequest request,
        string installationId, CancellationToken cancellationToken, Action<string>? progress = null,
        long? recoveredExpectedUnpublishedRevision = null)
    {
        var binding = _googleDriveConnections.Load(request.ConnectionId)
                      ?? throw new GoogleDriveBlockedException(
                          "This Google Drive account is not connected. Reconnect it before binding a pair.");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        var retained = await LoadPairsAsync(cancellationToken);
        var prior = request.ReplacingPairId is null ? null : retained.Pairs.SingleOrDefault(p => p.Id == request.ReplacingPairId);
        if (request.ReplacingPairId is not null && prior is null)
            throw new InvalidDataException("The selected pair is no longer retained.");
        if (request.ExpectedPolicyRevision is { } revision && (revision < 1 || prior?.PolicyRevision != revision))
            throw new InvalidOperationException("The pair policy changed. Read the current pair before saving again.");
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 80 || retained.Pairs.Any(p => p.Id != request.ReplacingPairId &&
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Enter a unique pair name of 1 to 80 characters.");
        ArgumentNullException.ThrowIfNull(request.RecoveryFolder);
        SyncDropboxArtifactPaths.RequireRecoveryContainer(request.RecoveryFolder.Path);
        if (!RecoveryPlacementAllowed(request.DropboxFolder.Path, request.RecoveryFolder.Path))
            throw new InvalidDataException("Recovery cannot be the same as, or contain, the paired cloud folder.");
        var normalizedLocalRoot = Path.GetFullPath(request.LocalRoot);
        using var local = PilotDirectoryLease.Open(normalizedLocalRoot);
        var remote = new GoogleDrivePilotClient(httpClient, GoogleTokenSource(binding), binding,
            request.DropboxFolder.FolderId, request.DropboxFolder.Path, _transferLimiter,
            allowSharedDrives: binding.SharedDrivesEnabled);
        progress?.Invoke("Pair save: asking Google Drive to verify the selected data folder's exact identity and access.");
        await remote.VerifyRootIdentityAsync(cancellationToken);
        var recoveryBinding = new SyncRemoteRecoveryRootBinding(request.RecoveryFolder.Path,
            request.RecoveryFolder.FolderId, binding.AccountKey);
        progress?.Invoke("Pair save: asking Google Drive to verify the retained Recovery folder's exact identity and access.");
        await remote.VerifyRecoveryRootAsync(recoveryBinding, cancellationToken);
        var settings = new PilotSettings(local.Path, local.Identity.VolumeSerialNumber, local.Identity.FileId,
            request.DropboxFolder.Path, request.DropboxFolder.FolderId);
        var pair = SyncPair.FromPilot(settings, name, binding.AccountKey) with
        {
            Enabled = request.Enabled,
            Priority = request.Priority,
            PropagateDeletions = request.PropagateDeletions,
            SelectionRules = ProtectedSelectionRules(request.DropboxFolder.Path, request.RecoveryFolder, request.SelectionRules),
            SelectionDefaultIncluded = request.SelectionDefaultIncluded ?? true,
            ConnectionId = binding.ConnectionId,
            Provider = SyncProviders.GoogleDrive,
            InitialReviewApproved = false,
            FilesOnDemandEnabled = request.FilesOnDemandEnabled,
            RecoveryRetentionDays = request.RecoveryRetentionDays,
            Direction = request.Direction,
            DefaultStorage = request.DefaultStorage,
            DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(request.DirectoryStorageRules),
            IgnoreRules = SyncIgnorePolicy.Normalize(request.IgnoreRules),
            RecoveryRoot = recoveryBinding
        };
        pair = RetainVerifiedEndpoints(pair,
            RetainedPolicyEditEndpointDocument(pair, prior, request.Endpoints), binding.AccountKey,
            binding.PermissionId);
        if (request.ReplacingPairId is null && retained.Pairs.Any(existing => SameRootIdentity(existing, pair)))
            throw new GoogleDriveBlockedException(
                "Those local and Google Drive folders already belong to another folder pair.");
        if (prior is not null && SameRootIdentity(prior, pair))
            pair = pair with { Id = prior.Id, InitialReviewApproved = prior.InitialReviewApproved,
                SelectionRules = ProtectedSelectionRules(request.DropboxFolder.Path, request.RecoveryFolder, request.SelectionRules ?? prior.SelectionRules),
                SelectionDefaultIncluded = request.SelectionDefaultIncluded ?? prior.SelectionDefaultIncluded,
                IgnoreRules = SyncIgnorePolicy.Normalize(request.IgnoreRules ?? prior.IgnoreRules),
                DefaultStorage = request.DefaultStorage ?? prior.DefaultStorage,
                DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(request.DirectoryStorageRules ?? prior.DirectoryStorageRules),
                DirectoryStorageApplyPaths = prior.DirectoryStorageApplyPaths,
                FileStorageRules = prior.FileStorageRules,
                FilesOnDemandEnabled = request.FilesOnDemandEnabled || prior.FilesOnDemandEnabled };
        var identityChanged = request.ReplacingPairId is not null && request.ReplacingPairId != pair.Id;
        var partitions = await RequireVerifiedPairPartitionsAsync(pair, request.ReplacingPairId,
            cancellationToken, progress);
        ValidateLocalEndpointOverlap(pair, retained.Pairs.Where(other => other.Id != request.ReplacingPairId), partitions);
        if (identityChanged && prior?.FilesOnDemandEnabled == true)
        {
            await DisableFilesOnDemandCoreAsync(prior.Id, cancellationToken);
            retained = await LoadPairsAsync(cancellationToken);
            prior = retained.Pairs.Single(item => item.Id == request.ReplacingPairId);
        }
        progress?.Invoke("Pair save: verifying Google Drive directory storage rules and retained pair policy.");
        pair = await BindDirectoryStorageRulesAsync(pair, prior, request.DirectoryStorageRules, null, cancellationToken);
        var current = retained.Pairs.SingleOrDefault(p => p.Id == pair.Id);
        pair = pair with { PolicyRevision = current is null ? 1 : SamePolicy(current, pair)
            ? current.PolicyRevision : checked(current.PolicyRevision + 1) };
        var retainedControl = prior is not null && prior.Id == pair.Id && SameRootIdentity(prior, pair)
            ? prior.ControlNamespace : null;
        if (retainedControl is not null && (retainedControl.InstallationId != installationId || retainedControl.NamespaceId != pair.Id))
            throw new IOException("The retained staging identity belongs to a different installation or pair.");
        using var verifiedControl = retainedControl is null ? null : SyncControlNamespace.OpenLocal(retainedControl, local);
        progress?.Invoke("Pair save: registering the verified Google Drive root bindings and policy revision in the retained database.");
        var registration = await RegisterGooglePairAuthorityAsync(pair, binding, installationId, local,
            cancellationToken, replacingPair: identityChanged ? prior : null,
            recoveredExpectedUnpublishedRevision: recoveredExpectedUnpublishedRevision,
            publishedPrior: !identityChanged && recoveredExpectedUnpublishedRevision is null &&
                current is not null && SameRootIdentity(current, pair) &&
                pair.PolicyRevision > current.PolicyRevision ? current : null);
        var localControl = retainedControl?.Local ?? registration.CreatedControl ??
            await new SyncAuthorityLocalControlNamespaceAllocator(_authority).BindAsync(local,
                installationId, pair.Id, registration.LocalRootBindingId, pair.PolicyRevision,
                registration.ProfileId, cancellationToken, partitionCandidate: pair);
        pair = pair with
        {
            ControlNamespace = retainedControl ?? new SyncControlNamespaceBinding(installationId, pair.Id, localControl)
        };
        WindowsCloudFilesProvider? activated = null;
        var started = false;
        if (pair.FilesOnDemandEnabled) activated = GetOrStartCloudProvider(pair, remote, out started);
        try
        {
            progress?.Invoke("Pair save: committing the verified Google Drive pair definition; existing approval and owned staging are preserved for unchanged roots.");
            if (identityChanged)
            {
                local.VerifyBinding();
                await _authority.PublishAccountPairReplacementAsync(pair,
                    registration.ReplacementRoots ?? throw new InvalidOperationException("The replacement roots were not prepared."),
                    cancellationToken);
            }
            else if (!registration.DefinitionCommitted)
                await _authority.RetainPairDefinitionAsync(pair, select: true, cancellationToken);
        }
        catch
        {
            if (started && activated is not null) RollBackCloudProviderStart(pair.Id, activated);
            throw;
        }
        _selectedPairId = pair.Id;
        progress?.Invoke("Pair save: reading back the committed Google Drive pair definition and current selection.");
        var pairSet = await LoadPairsAsync(cancellationToken, progress);
        progress?.Invoke("Pair save: committed Google Drive pair definition read back successfully; no sync was started by this save.");
        return new SyncPairBindingResult(pairSet, pair, settings, request.ReplacingPairId is not null &&
            !string.Equals(request.ReplacingPairId, pair.Id, StringComparison.Ordinal));
    }

    // Pair-draft preview for a Google account: directory presence only, no content hashing, same result
    // shape the Dropbox preview already returns so the existing picker UI is unchanged.
    private async Task<SyncPairDraftPreview> LoadGoogleDriveDraftPreviewAsync(SyncPair draft,
        string localBrowseRoot, DropboxFolderEntry browseRoot, GoogleDriveConnectionBinding binding,
        CancellationToken cancellationToken)
    {
        var normalizedLocalRoot = Path.GetFullPath(localBrowseRoot);
        using var root = PilotDirectoryLease.Open(normalizedLocalRoot);
        var local = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
            includePath: _ => false, traverseDirectory: _ => true);
        var remote = new GoogleDrivePilotClient(httpClient, GoogleTokenSource(binding), binding,
            browseRoot.FolderId, browseRoot.Path, _transferLimiter,
            allowSharedDrives: binding.SharedDrivesEnabled);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        var cloud = await remote.ListEntriesAsync(cancellationToken);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        var localFolders = local.Entries.Values.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteFolders = cloud.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = localFolders.Keys.Concat(remoteFolders.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nodes = paths.Select(path =>
        {
            localFolders.TryGetValue(path, out var localFolder);
            remoteFolders.TryGetValue(path, out var remoteFolder);
            var presence = localFolder is not null && remoteFolder is not null
                ? SyncFolderPresence.Mixed
                : remoteFolder is not null ? SyncFolderPresence.CloudOnly : SyncFolderPresence.LocalOnly;
            var localIdentity = localFolder?.VolumeSerialNumber is not null && localFolder.FileId is not null
                ? LocalSelectionIdentity(localFolder)
                : null;
            var providerIdentity = remoteFolder?.Id;
            return new SyncFolderSelectionNode(path,
                Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                path.Count(character => character == '/') + 1, false, presence,
                paths.Count(candidate => candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)),
                providerIdentity is null ? localIdentity : "google-drive:" + providerIdentity,
                new SyncFolderRollup(0, 0, 0, 0, 0, 0), localIdentity, providerIdentity);
        }).ToArray();
        int localRootFiles;
        try
        {
            localRootFiles = Directory.EnumerateFiles(root.Path, "*", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            localRootFiles = -1;
        }
        var remoteRootFiles = cloud.Count(item => item.Kind == PilotEntryKind.File &&
                                                  !item.RelativePath.Contains('/'));
        return new SyncPairDraftPreview(normalizedLocalRoot, browseRoot, nodes, localRootFiles,
            remoteRootFiles, local.Decisions.Count == 0 && localRootFiles >= 0);
    }

    private async Task<PreparedPair> PrepareGoogleDrivePairAsync(SyncPair pair,
        CancellationToken cancellationToken, bool forceReviewedDeletionEffects)
    {
        var binding = _googleDriveConnections.Load(pair.ConnectionId)
                      ?? throw new GoogleDriveBlockedException(
                          "This pair is bound to a Google Drive account that is not connected.");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        var installationId = await EnsureAuthorityInitializedAsync(cancellationToken);
        using var localRoot = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var remote = CreateGoogleDriveClient(pair);
        await remote.VerifyRootIdentityAsync(cancellationToken);
        if (pair.ControlNamespace is null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "Migrated pair activation requires the Windows same-volume control allocator.");
            var provisional = await RegisterGooglePairAuthorityAsync(pair, binding, installationId, localRoot,
                cancellationToken);
            var allocatedControl = await new SyncAuthorityLocalControlNamespaceAllocator(_authority).BindAsync(
                localRoot, installationId, pair.Id, provisional.LocalRootBindingId, pair.PolicyRevision,
                provisional.ProfileId, cancellationToken, partitionCandidate: pair);
            pair = pair with
            {
                ControlNamespace = new SyncControlNamespaceBinding(installationId, pair.Id, allocatedControl)
            };
            var retained = await _authority.LoadPairSetAsync(cancellationToken);
            var selected = string.Equals(retained.SelectedPairId, pair.Id, StringComparison.Ordinal);
            await _authority.RetainPairDefinitionAsync(pair, selected, cancellationToken);
        }
        using var localControl = SyncControlNamespace.OpenLocal(pair.ControlNamespace, localRoot);
        if (pair.RecoveryRoot is not null)
            await remote.VerifyRecoveryRootAsync(pair.RecoveryRoot, cancellationToken);
        var registration = await RegisterGooglePairAuthorityAsync(pair, binding, installationId, localRoot,
            cancellationToken);
        var recoveryReady = pair.RecoveryRoot is not null;
        var options = new SyncAuthorityPlannerOptions(pair.PolicyRevision, registration.ProfileId,
            pair.ControlNamespace.NamespaceId, registration.DropboxRecoveryAuthorityId,
            EnableCreateEffects: true,
            EnableReplacementEffects: recoveryReady,
            EnableDeletionEffects: recoveryReady && (pair.PropagateDeletions || forceReviewedDeletionEffects),
            EnableMoveEffects: true,
            DeletionMinimumCompleteObservations: 1,
            DeletionMinimumInterval: TimeSpan.Zero,
            Direction: SyncEndpointEngineLayout.EngineDirection(pair),
            RemoteUploadSessionPolicy: SyncUploadLimits.ForStore(remote));
        var cloudFiles = pair.FilesOnDemandEnabled
            ? GetOrStartCloudProvider(pair, remote, out _)
            : null;
        return new PreparedPair(remote, registration.LocalRootBindingId,
            registration.DropboxRootBindingId, options, cloudFiles);
    }

    // The authority provider_account row keeps the exact grandfathered Google columns: account_id is
    // the native permission id, and root_namespace_id and home_namespace_id are both the account key.
    // The provider literal is Google Drive, so the row states its real provider rather than the
    // legacy Dropbox default. The id comes from the one shared identity helper; no branch here decides
    // a derivation, and no existing Dropbox row's identity is rewritten.
    private async Task<PairAuthorityRegistration> RegisterGooglePairAuthorityAsync(SyncPair pair,
        GoogleDriveConnectionBinding binding, string installationId, PilotDirectoryLease localRoot,
        CancellationToken cancellationToken, SyncPair? replacingPair = null,
        long? recoveredExpectedUnpublishedRevision = null, SyncPair? publishedPrior = null, bool select = true)
    {
        localRoot.VerifyBinding();
        SyncLocalControlNamespaceBinding? createdControl = null;
        var definitionCommitted = false;
        SyncPairSet? committedPairSet = null;
        await RequireVerifiedPairPartitionsAsync(pair, replacingPair?.Id, cancellationToken);
        var sourceRevision = BuildIdentity.SourceRevision;
        var profileId = Id("profile", sourceRevision, CapabilityJson);
        await _authority.InitializeAsync(installationId, profileId, sourceRevision, CapabilityJson,
            cancellationToken);
        var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive,
            binding.PermissionId, binding.AccountKey, binding.AccountKey);
        await _authority.RegisterProviderAccountAsync(providerAccountId, installationId, binding.PermissionId,
            binding.AccountKey, binding.AccountKey, "GoogleDrive", 1, cancellationToken,
            SyncProviders.GoogleDrive);
        // F15/V2: the Google Drive Host registration resolves the volume id through the same ONE shared
        // resolver as the Dropbox Host and the local/local runtime, so the three actual consumers can never
        // derive two ids for one natural key.
        var volumeId = await _authority.ResolveVolumeIdAsync(installationId,
            pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture), "NTFS", null,
            supportsFileId128: false,
            supportsUsn: NtfsUsnJournal.TryReadCheckpoint(localRoot.Path, out _),
            supportsAtomicRename: true, cancellationToken);
        var configuration = new SyncAuthorityPairConfiguration(pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1,
                "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false,
                RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue);
        var volumeSerialHex = pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
        var localFileIdHex = pair.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture);
        var localRootId = Id("root-local", pair.Id, volumeId, localFileIdHex);
        var providerRootId = Id("root-dropbox", pair.Id, binding.AccountKey, pair.DropboxFolderId);
        SyncAuthorityDatabase.AccountPairReplacementRoots? replacementRoots = replacingPair is null ? null : new(
            installationId, replacingPair.Id, replacingPair.PolicyRevision, localRootId, volumeId,
            volumeSerialHex, localFileIdHex, providerRootId, providerAccountId, binding.AccountKey);
        if (replacementRoots is not null)
            await _authority.PrepareAccountPairReplacementAsync(pair, configuration, replacementRoots, cancellationToken);
        else
        {
        var googleEndpoint = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, binding.ConnectionId,
            binding.PermissionId, pair.DropboxFolderId, pair.DropboxRoot);
        var side = ResolveCloudSide(SyncEndpointRecord.From(googleEndpoint));
        var hierarchy = await ReadCandidateHierarchyAsync(side, providerRootId, cancellationToken);
        if (recoveredExpectedUnpublishedRevision is { } recoveredRevision)
        {
        // R671: the local activation and the typed cloud candidate root are ONE transaction, so a refused cloud
        // side can never leave a committed local root or policy change. Google keeps its own typed identity here;
        // this never substitutes the Dropbox registrar.
        var recoveredChains = new List<SyncAuthorityCloudHierarchySnapshot> { hierarchy };
        foreach (var authority in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
        {
            if (string.Equals(authority.AuthorityId, providerRootId, StringComparison.Ordinal)) continue;
            if (!string.Equals(authority.Provider, side.Facts.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            recoveredChains.Add(await ReadRetainedAuthorityHierarchyAsync(authority, cancellationToken));
        }
        await _authority.ActivateRecoveredLocalAndTypedCloudRootAsync(pair, recoveredRevision, installationId,
            localRootId, volumeId, volumeSerialHex, localFileIdHex, side.Facts.Provider,
            side.Facts.ProviderAccountId, providerRootId, SyncAuthorityCloudSides.LogicalRight,
            side.Record.NamespaceId!, side.Record.FolderId!, side.Record.FolderPath!, recoveredChains,
            configuration, cancellationToken);
        }
        else if (publishedPrior is not null)
        {
        // root726: a published same-root GOOGLE edit publishes through the SAME reusable atomic publication, with
        // the typed cloud root bound to its own retained preimage - no per-provider transaction and no Dropbox
        // registrar substitution. New, recovered and account-replacement paths above/below are unchanged.
        var chains = new List<SyncAuthorityCloudHierarchySnapshot> { hierarchy };
        foreach (var authority in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
        {
            if (string.Equals(authority.AuthorityId, providerRootId, StringComparison.Ordinal)) continue;
            if (!string.Equals(authority.Provider, side.Facts.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            chains.Add(await ReadRetainedAuthorityHierarchyAsync(authority, cancellationToken));
        }
        var controlPreimage = await _authority.LoadPairControlNamespacePreimageAsync(pair.Id, "local", cancellationToken);
        var preimageNamespace = pair.Id;
        var preimageAllocation = "control-prepared";
        var preimageIdentity = "{}";
        var preimageMarker = "{}";
        var preimageHash = new string('0', 64);
        long preimageGeneration = 1;
        string? preparedIdentity = null;
        string? preparedMarker = null;
        string? preparedHash = null;
        string? preparedPath = null;
        SyncAuthorityLocalControlNamespaceAllocator.SyncPreparedControlPublication? prepared = null;
        if (controlPreimage is not null)
        {
            preimageNamespace = controlPreimage.NamespaceId;
            preimageAllocation = controlPreimage.AllocationId;
            preimageIdentity = controlPreimage.IdentityJson;
            preimageMarker = controlPreimage.MarkerIdentityJson;
            preimageHash = controlPreimage.OwnerMarkerHash;
            preimageGeneration = controlPreimage.BindingGeneration;
        }
        else
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "Preparing a local control namespace requires Windows folder identities.");
            prepared = await new SyncAuthorityLocalControlNamespaceAllocator(_authority)
                .PrepareForPublicationAsync(localRoot, installationId, pair.Id, localRootId,
                    publishedPrior.PolicyRevision, profileId, cancellationToken);
            var preparedControl = prepared.Binding;
            preparedIdentity = JsonSerializer.Serialize(new
            {
                volumeSerial = preparedControl.RootVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                fileId = preparedControl.RootFileId.ToString("x16", CultureInfo.InvariantCulture)
            });
            preparedMarker = preparedControl.MarkerFileId;
            preparedHash = preparedControl.MarkerContentHash;
            preparedPath = preparedControl.RootPath;
            createdControl = preparedControl;
            pair = pair with
            {
                ControlNamespace = new SyncControlNamespaceBinding(installationId, pair.Id, preparedControl)
            };
        }
        try
        {
            committedPairSet = await _authority.CommitPublishedSameRootEditAsync(pair, configuration, publishedPrior,
                publishedPrior.PolicyRevision, localRootId, 1, volumeId, volumeSerialHex, localFileIdHex,
                providerRootId, 1, installationId, side.Facts.ProviderAccountId, side.Record.NamespaceId!,
                side.Record.FolderId!, select: select, preimageNamespace, preimageAllocation, preimageIdentity,
                preimageMarker, preimageHash, preimageGeneration,
                preparedIdentityJson: preparedIdentity, preparedMarkerFileId: preparedMarker,
                preparedMarkerHash: preparedHash, preparedControlRootPath: preparedPath,
                preparedLease: prepared?.Renewal.Lease, typedCloudRootBindingId: providerRootId,
                typedCloudCompatibilitySide: SyncAuthorityCloudSides.LogicalRight,
                typedCloudProvider: side.Facts.Provider,
                typedCloudProviderAccountId: side.Facts.ProviderAccountId,
                typedCloudNamespaceId: side.Record.NamespaceId,
                typedCloudFolderId: side.Record.FolderId,
                typedCloudDisplayPath: side.Record.FolderPath,
                typedCloudHierarchies: chains, cancellationToken: cancellationToken);
            definitionCommitted = true;
        }
        finally
        {
            if (prepared is not null)
            {
                await prepared.Renewal.DisposeAsync();
                if (prepared.Admission.Purpose == SyncAuthorityCommandPurpose.ControlAllocation)
                    await _authority.TransitionCommandRequestAsync(prepared.Admission.CommandRequestId,
                        definitionCommitted ? "completed" : "blocked", cancellationToken: CancellationToken.None);
            }
        }
        }
        else
        {
        await _authority.RegisterPairAsync(pair.Id, pair.PolicyRevision, configuration, cancellationToken);
        await _authority.RegisterLocalRootBindingAsync(localRootId, pair.Id, 1, installationId, volumeId,
            volumeSerialHex, localFileIdHex, pair.LocalRoot, cancellationToken, partitionCandidate: pair);
        await RegisterCandidateCloudRootAsync(pair.Id, SyncAuthorityCloudSides.LogicalRight, side,
            providerRootId, 1, installationId, hierarchy, cancellationToken, pair);
        }
        }
        string recoveryAuthorityId;
        if (pair.RecoveryRoot is not null)
        {
            var recoveryId = Id("recovery-root", providerAccountId, pair.RecoveryRoot.RootFolderId,
                binding.AccountKey);
            await _authority.RegisterDropboxRecoveryRootAsync(recoveryId, installationId, providerAccountId,
                pair.RecoveryRoot.RootPath, pair.RecoveryRoot.RootFolderId, binding.AccountKey, 1,
                cancellationToken);
            recoveryAuthorityId = recoveryId;
        }
        else
            recoveryAuthorityId = Id("recovery-disabled", pair.Id);
        return new PairAuthorityRegistration(profileId, localRootId, providerRootId, recoveryAuthorityId, replacementRoots,
            definitionCommitted, createdControl, committedPairSet);
    }

    private async Task<string> EnsureAuthorityInitializedAsync(CancellationToken cancellationToken,
        Action<string>? startupProgress = null)
    {
        if (_installationId is not null) return _installationId;
        if (_authority.TentativeCommandInstallation is { } tentative)
        {
            stateAuthority.VerifyAuthority();
            return tentative.Id; // Tentative metadata only; do not set initialized/healthy flags.
        }
        startupProgress?.Invoke("Startup: waiting for the retained-state initialization lock.");
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_installationId is not null) return _installationId;
            var stage = System.Diagnostics.Stopwatch.StartNew();
            stateAuthority.VerifyAuthority();
            startupProgress?.Invoke("Startup: checking the retained database schema and reading its installation identity.");
            var existing = File.Exists(_authority.DatabasePath)
                ? await _authority.LoadInstallationIdAsync(cancellationToken, startupProgress)
                : null;
            startupProgress?.Invoke($"Startup: database admission completed in {stage.Elapsed.TotalSeconds:F3}s.");
            var installationId = existing ?? pairStore.LoadInstallationId() ?? Guid.NewGuid().ToString("N");
            installationId = SyncControlNamespace.NormalizeInstallationId(installationId);
            var sourceRevision = BuildIdentity.SourceRevision;
            var profileId = Id("profile", sourceRevision, CapabilityJson);
            stage.Restart();
            startupProgress?.Invoke("Startup: initializing the retained installation and capability profile.");
            await _authority.InitializeAsync(installationId, profileId, sourceRevision, CapabilityJson,
                cancellationToken);
            startupProgress?.Invoke($"Startup: installation initialization completed in {stage.Elapsed.TotalSeconds:F3}s.");
            stage.Restart();
            startupProgress?.Invoke("Startup: checking failed unpublished pair reservations; no folders or private controls are changed.");
            var releasedAuthoringRoots = await _authority.ReleaseFailedUnpublishedPairRootsAsync(cancellationToken);
            startupProgress?.Invoke($"Startup: released {releasedAuthoringRoots} positively refused unpublished data-root reservations; private-control failures remain retained.");
            stage.Restart();
            startupProgress?.Invoke("Startup: reconciling legacy pair definitions and activity history.");
            await ImportLegacyStateAsync(cancellationToken, startupProgress);
            startupProgress?.Invoke($"Startup: legacy-state reconciliation completed in {stage.Elapsed.TotalSeconds:F3}s.");
            stage.Restart();
            startupProgress?.Invoke("Startup: publishing any retained legacy Rewind collection into the authority database; no provider, credential or local file effect runs.");
            await EnsureRewindStoragePublishedAsync(cancellationToken, installationId);
            startupProgress?.Invoke($"Startup: Rewind storage publication completed in {stage.Elapsed.TotalSeconds:F3}s.");
            if (!_commandsRecovered)
            {
                stage.Restart();
                startupProgress?.Invoke("Startup: reconciling interrupted command records; no file transfer has started.");
                await _authority.RecoverInterruptedCommandRequestsAsync(cancellationToken);
                await _authority.CloseInterruptedActivityWritersAsync(cancellationToken);
                startupProgress?.Invoke($"Startup: command-record recovery completed in {stage.Elapsed.TotalSeconds:F3}s.");
                _commandsRecovered = true;
            }
            _installationId = installationId;
            return installationId;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task ImportLegacyStateAsync(CancellationToken cancellationToken,
        Action<string>? startupProgress = null)
    {
        var legacy = pairStore.Load();
        if (legacy.Pairs.Count == 0) return;
        var connection = connectionStore.Load();
        if (connection is null) return;

        var recoveryOrder = legacyRuntimeStore.LoadRecoveryOrder().ToHashSet(StringComparer.Ordinal);
        var retained = await _authority.LoadPairSetAsync(cancellationToken);
        var retainedIds = retained.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var source in legacy.Pairs)
        {
            startupProgress?.Invoke($"Startup: reconciling legacy state for pair {source.Id}.");
            if (!string.IsNullOrWhiteSpace(source.ConnectionId) &&
                !string.Equals(source.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "A legacy pair belongs to a different Dropbox connection and was not rebound during migration.");
            var runtime = legacyRuntimeStore.LoadPair(source.Id);
            var imported = source with
            {
                ConnectionId = connection.ConnectionId,
                Provider = "dropbox",
                InitialReviewApproved = source.InitialReviewApproved ?? false,
                Enabled = source.Enabled && !recoveryOrder.Contains(source.Id)
            };
            imported = SyncPairStore.NormalizePair(imported);
            if (!retainedIds.Contains(imported.Id))
            {
                await _authority.RegisterPairAsync(imported.Id, imported.PolicyRevision,
                    new SyncAuthorityPairConfiguration(
                        imported.PropagateDeletions ? "recoverable" : "disabled", 0, 1,
                        "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                        AutomaticPruning: false, RecoveryRetentionDays: imported.RecoveryRetentionDays ?? int.MaxValue), cancellationToken);
                await _authority.RetainPairDefinitionAsync(imported,
                    string.Equals(legacy.SelectedPairId, source.Id, StringComparison.Ordinal), cancellationToken);
                retainedIds.Add(imported.Id);
            }
            await _authority.RecordAcceptedLegacyPairImportAsync(source, imported, runtime,
                cancellationToken);
        }

        var migratablePairs = retainedIds;
        startupProgress?.Invoke("Startup: reading retained legacy activity history.");
        var history = _legacyHistoryStore.Load();
        if (history.Entries.Count != 0)
        {
            startupProgress?.Invoke($"Startup: reconciling {history.Entries.Count} legacy history entries.");
            await _authority.ImportLegacyHistoryAsync(new SyncHistory(history.Entries
                .Where(item => migratablePairs.Contains(item.PairId)).ToArray()), cancellationToken);
        }
    }

    private bool ColdCommandNeedsNoLegacyStartup()
    {
        stateAuthority.VerifyAuthority();
        // Nonempty legacy input retains its existing import/recovery path. A
        // read-only preview is never evidence that its receipts were imported.
        return _installationId is null && pairStore.Load().Pairs.Count == 0;
    }

    private async Task<SyncAuthorityDatabase.ColdInstallation?> InspectColdCommandPreparationAsync(
        CancellationToken cancellationToken)
    {
        if (_authority.TentativeCommandInstallation is not null || !ColdCommandNeedsNoLegacyStartup()) return null;
        return await _authority.InspectColdCommandEligibilityAsync(cancellationToken);
    }

    private async Task<DropboxConnectionBinding> RequireConnectionAsync(CancellationToken cancellationToken, string? connectionId = null)
    {
        var connection = connectionStore.Load(connectionId)
                         ?? throw new InvalidOperationException("Connect Dropbox first.");
        if (string.IsNullOrWhiteSpace(await credentialStore.ForConnection(connection).ReadAsync(cancellationToken)))
            throw new InvalidOperationException("The protected Dropbox credential is unavailable.");
        return connection;
    }

    private static bool DropboxPathsOverlap(string left, string right)
    {
        var first = PilotPath.NormalizeDropboxRoot(left);
        var second = PilotPath.NormalizeDropboxRoot(right);
        if (first == "/" || second == "/") return true;
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith(second + "/", StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RecoveryPlacementAllowed(string pairRoot, string recoveryRoot)
    {
        var pair = PilotPath.NormalizeDropboxRoot(pairRoot);
        var recovery = PilotPath.NormalizeDropboxRoot(recoveryRoot);
        if (string.Equals(pair, recovery, StringComparison.OrdinalIgnoreCase)) return false;
        return pair == "/" || recovery.StartsWith(pair + "/", StringComparison.OrdinalIgnoreCase) ||
               !pair.StartsWith(recovery + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SyncSelectionRule> ProtectedSelectionRules(string pairRoot,
        DropboxFolderEntry recoveryRoot, IReadOnlyList<SyncSelectionRule>? requested)
    {
        var rules = SyncSelectionPolicy.Normalize(requested);
        var root = PilotPath.NormalizeDropboxRoot(pairRoot);
        var recovery = PilotPath.NormalizeDropboxRoot(recoveryRoot.Path);
        if (root == "/")
            rules = SyncSelectionPolicy.SetFolder(rules, SyncControlNamespace.RemoteContainerPath.Trim('/'),
                included: false);
        if (root == "/" || recovery.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = root == "/" ? recovery.TrimStart('/') : recovery[(root.Length + 1)..];
            rules = SyncSelectionPolicy.SetFolder(rules, relative, included: false,
                "dropbox:" + recoveryRoot.FolderId);
        }
        return rules;
    }

    private static bool SamePolicy(SyncPair left, SyncPair right) =>
        left.Enabled == right.Enabled && left.Priority == right.Priority &&
        left.Direction == right.Direction &&
        SyncDirectoryStoragePolicy.Default(left) == SyncDirectoryStoragePolicy.Default(right) &&
        SyncDirectoryStoragePolicy.Normalize(left.DirectoryStorageRules).SequenceEqual(SyncDirectoryStoragePolicy.Normalize(right.DirectoryStorageRules)) &&
        left.PropagateDeletions == right.PropagateDeletions &&
        left.FilesOnDemandEnabled == right.FilesOnDemandEnabled &&
        left.RecoveryRetentionDays == right.RecoveryRetentionDays &&
        left.SelectionDefaultIncluded == right.SelectionDefaultIncluded &&
        SyncIgnorePolicy.Normalize(left.IgnoreRules).SequenceEqual(SyncIgnorePolicy.Normalize(right.IgnoreRules)) &&
        SyncSelectionPolicy.Normalize(left.SelectionRules)
            .SequenceEqual(SyncSelectionPolicy.Normalize(right.SelectionRules));

    private WindowsCloudFilesProvider GetOrStartCloudProvider(SyncPair pair, ISyncRemoteStore remote,
        out bool started)
    {
        lock (_cloudProviderGate)
        {
            if (_cloudProviders.TryGetValue(pair.Id, out var existing))
            {
                existing.UpdatePairPolicy(pair);
                started = false;
                return existing;
            }
            var provider = new WindowsCloudFilesProvider(pair, remote,
                SyncSharkRuntimePaths.ReplacementStagingRoot);
            try
            {
                provider.RegisterAndConnect();
                _cloudProviders.Add(pair.Id, provider);
                started = true;
                return provider;
            }
            catch
            {
                provider.Dispose();
                throw;
            }
        }
    }

    private void RollBackCloudProviderStart(string pairId, WindowsCloudFilesProvider provider)
    {
        lock (_cloudProviderGate)
        {
            if (_cloudProviders.TryGetValue(pairId, out var retained) &&
                ReferenceEquals(retained, provider))
                _cloudProviders.Remove(pairId);
        }
        try { provider.RollBackNewRegistration(); }
        finally { provider.Dispose(); }
    }

    private void DisposeCloudProviders()
    {
        WindowsCloudFilesProvider[] providers;
        lock (_cloudProviderGate)
        {
            providers = _cloudProviders.Values.ToArray();
            _cloudProviders.Clear();
        }
        foreach (var provider in providers) provider.Dispose();
    }

    private static bool SameRootIdentity(SyncPair left, SyncPair right) =>
        SyncEndpointEngineLayout.IsReversed(left) == SyncEndpointEngineLayout.IsReversed(right) &&
        left.LocalVolumeSerialNumber == right.LocalVolumeSerialNumber &&
        left.LocalRootFileId == right.LocalRootFileId &&
        string.Equals(left.ConnectionId, right.ConnectionId, StringComparison.Ordinal) &&
        string.Equals(left.DropboxFolderId, right.DropboxFolderId, StringComparison.Ordinal);

    /// <summary>
    /// The endpoint document one binding save must carry. A caller that supplied its own explicit document
    /// keeps it unchanged. A legacy policy-only save of an UNCHANGED binding otherwise carries the retained
    /// pair's own explicit document, so a policy edit can never rewrite an explicit local+cloud definition
    /// into a namespace-incomplete one. The carry is gated on the freshly verified binding still being the
    /// retained pair's own root identity, and the shared endpoint comparison below then re-verifies the whole
    /// document against that binding: a changed or mismatched account, namespace, folder or ordering fails
    /// closed instead of inheriting the document into a changed pair, and a pair with no retained document
    /// keeps the legacy shape exactly as before.
    /// </summary>
    internal static SyncEndpointDocument? RetainedPolicyEditEndpointDocument(SyncPair verifiedPair,
        SyncPair? prior, SyncEndpointDocument? requested) =>
        requested ?? (prior is { EndpointDocument: not null } && SameRetainedRootIdentity(prior, verifiedPair)
            ? prior.EndpointDocument
            : null);

    /// <summary>
    /// The root/account identity of a retained pair against a freshly verified binding, WITHOUT the endpoint
    /// orientation: a legacy save carries no orientation of its own (its ordered document is exactly what is
    /// being re-verified), so the orientation is decided by the document the carry supplies, never by the
    /// document-less draft. Every identity term that makes the binding the retained pair's own root is still
    /// required, and a changed local identity, account or cloud folder never carries a document.
    /// </summary>
    private static bool SameRetainedRootIdentity(SyncPair retained, SyncPair verified) =>
        retained.LocalVolumeSerialNumber == verified.LocalVolumeSerialNumber &&
        retained.LocalRootFileId == verified.LocalRootFileId &&
        string.Equals(retained.ConnectionId, verified.ConnectionId, StringComparison.Ordinal) &&
        string.Equals(retained.DropboxFolderId, verified.DropboxFolderId, StringComparison.Ordinal);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_runControlGate) _activeRunControl?.Stop();
        // AUTH1: releasing the runtime ends its one pending authorization attempt through the same
        // exact-target operation the CLI and the Connections panel use; no second cancellation authority
        // is created and the listener the provider flow owns is closed by that flow's own cleanup.
        if (PendingAuthorization is { } pendingAuthorization)
            RequestAuthorizationAttemptCancellation(pendingAuthorization.AttemptId);
        lock (_monitorDebounceGate)
        {
            _onMonitorRunRequested = null;
            ++_monitorScopeGeneration;
            _pendingMonitorPairs.Clear();
            _monitorDelivery = null;
            _monitorDebounce?.Cancel();
            _monitorDebounce?.Dispose();
            _monitorDebounce = null;
        }
        foreach (var monitor in _dropboxMonitors) monitor.Dispose();
        _dropboxMonitors.Clear();
        foreach (var monitor in _localMonitors) monitor.Dispose();
        _localMonitors.Clear();
        DisposeCloudProviders();
        _ownedGoogleEndpoint?.Dispose();
    }

    private static string HashObservationSetContent(
        IEnumerable<SyncAuthorityPairObservationSet> observationSets)
    {
        var material = string.Join("\n", observationSets
            .OrderBy(item => item.PairId, StringComparer.Ordinal)
            .Select(item => string.Join("|", item.PairId, item.ConnectionId ?? "<unbound-connection>",
                item.ConfigurationRevision, item.LocalRootBindingId, item.DropboxRootBindingId,
                item.ContentFingerprint, item.PlanningStateFingerprint)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string Id(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant()[..32];

    private sealed class FolderRollupBuilder
    {
        public int FolderCount { get; set; }
        public int IncludedFolderCount { get; set; }
        private int LocalItems { get; set; }
        private int OnlineOnlyItems { get; set; }
        private int MixedItems { get; set; }
        private int ExcludedItems { get; set; }
        private int PendingItems { get; set; }
        private int ErrorItems { get; set; }

        public void AddItem(bool local, bool onlineOnly, bool mixed, bool excluded)
        {
            if (local) LocalItems++;
            if (onlineOnly) OnlineOnlyItems++;
            if (mixed) MixedItems++;
            if (excluded) ExcludedItems++;
        }

        public void AddState(bool pending, bool error)
        {
            if (pending) PendingItems++;
            if (error) ErrorItems++;
        }

        public SyncFolderRollup ToRollup() => new(LocalItems, OnlineOnlyItems, MixedItems,
            ExcludedItems, PendingItems, ErrorItems);
    }

    private sealed record PreparedPair(ISyncRemoteStore Remote, string LocalRootBindingId,
        string DropboxRootBindingId, SyncAuthorityPlannerOptions Options,
        WindowsCloudFilesProvider? CloudFiles, SyncAuthorityPreparedCloudPair? Cloud = null);
    private sealed record CoordinatedPreparationAttempt(
        SyncPair Pair,
        SyncAuthorityPairPreparationResult? Preparation,
        Exception? Failure,
        SyncDecision? BlockingDecision,
        SyncAuthorityPreparedCloudPair? Cloud = null);
    private sealed record CoordinatedObservationAttempt(
        SyncPair Pair,
        SyncAuthorityPairObservationSet? ObservationSet,
        Exception? Failure);
    private sealed record CrossPairPlannedExecution(
        SyncAuthorityCrossPairMoveCoordinator Coordinator,
        SyncAuthorityCrossPairMovePlan Plan);
    private sealed record CrossPairCoordinationResult(
        IReadOnlyDictionary<string, Exception> PreparationFailures,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MembershipObjectFences,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MembershipPathFences,
        IReadOnlyDictionary<string, PreparedPair> PreparedPairs,
        IReadOnlyDictionary<string, SyncAuthorityPairObservationSet> ObservationSets,
        string ObservationSetId,
        string ContentFingerprint,
        IReadOnlyList<CrossPairPlannedExecution> CrossPairPlans,
        IReadOnlyDictionary<string, IReadOnlySet<string>> CoordinatedObjectReservations,
        IReadOnlySet<string> InvalidatedConnectionIds)
    {
        internal bool WorkWindowFull { get; init; }
    }
    private sealed record PairAuthorityRegistration(string ProfileId, string LocalRootBindingId,
        string DropboxRootBindingId, string DropboxRecoveryAuthorityId,
        SyncAuthorityDatabase.AccountPairReplacementRoots? ReplacementRoots = null,
        bool DefinitionCommitted = false, SyncLocalControlNamespaceBinding? CreatedControl = null,
        SyncPairSet? CommittedPairSet = null);

    /// <summary>
    /// root726/727: the outcome of the shared policy-publication entry point. <see cref="DefinitionCommitted"/> is
    /// true exactly when the pair_json definition is ALREADY durable - the ONE atomic publication read its own
    /// committed state back - so the caller must NOT write a second, stale definition that could deselect the
    /// edited pair or overwrite a newly created private-control binding. <see cref="CommittedPairSet"/> is that
    /// exact read-back, so the caller propagates the real committed pair and selection instead of reconstructing
    /// success.
    /// </summary>
    private sealed record PairPolicyPublicationResult(SyncPairSet? CommittedPairSet, bool DefinitionCommitted)
    {
        public static readonly PairPolicyPublicationResult Retained = new(null, false);
    }



    /// <summary>
    /// Prepares and seals the ONE normalized original consent of a new reviewed run. The immutable header is
    /// serialized outside every write transaction, the exact original choices are retained in bounded contiguous
    /// chunks of at most 256 items or 1 MiB, and one atomic seal binds the exact total, count, ordered digest and
    /// the exact pending command reservation. It authorizes nothing on its own: the reviewed command is accepted
    /// separately and only against that exact sealed header, and a Stop leaves at most a non-authorizing
    /// building candidate that the next command never silently resumes.
    /// </summary>
    private async Task PrepareReviewedOriginalConsentAsync(string consentId, string reservedCommandId,
        SyncPair pair, SyncBootstrapActionPlan normalizedPlan, string originalRequestJson,
        string originalRequestSha256, IReadOnlyList<SyncAuthorityConsentPreparedChoice> preparedChoices,
        CancellationToken cancellationToken, Action<string>? onPreparationProgress)
    {
        var headerJson = SyncReviewedConsentReference.BuildConsentHeaderJson(originalRequestJson,
            normalizedPlan, consentId);
        var digest = SyncAuthorityConsentEncoding.OrderedDigest(preparedChoices);
        onPreparationProgress?.Invoke("Preparing the exact original review of " +
            preparedChoices.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) +
            " file choices in bounded chunks; no file transfer has started.");
        await _authority.BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation(consentId,
            reservedCommandId, reservedCommandId, pair.Id, originalRequestSha256, digest, headerJson,
            preparedChoices.Count, ContinueExistingPreparation: true), cancellationToken);
        var offset = 0;
        while (offset < preparedChoices.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkBytes = 0L;
            var count = 0;
            while (offset + count < preparedChoices.Count &&
                   count < SyncAuthorityConsentEncoding.MaximumItemsPerChunk &&
                   (count == 0 || chunkBytes + preparedChoices[offset + count].EncodedBytes <=
                       SyncAuthorityConsentEncoding.MaximumChunkEncodedBytes))
            {
                chunkBytes += preparedChoices[offset + count].EncodedBytes;
                count++;
            }
            var chunk = new SyncAuthorityConsentPreparedChoice[count];
            for (var index = 0; index < count; index++) chunk[index] = preparedChoices[offset + index];
            await _authority.AppendConsentItemsAsync(consentId, offset, chunk, cancellationToken);
            offset += count;
        }
        onPreparationProgress?.Invoke("Sealing the exact original review; the reviewed command is accepted only against this sealed approval.");
        await _authority.SealConsentAsync(consentId, cancellationToken);
    }

}

public sealed record SyncPairBindingRequest(
    string? ReplacingPairId,
    string Name,
    string LocalRoot,
    DropboxFolderEntry DropboxFolder,
    DropboxFolderEntry? RecoveryFolder,
    bool Enabled,
    int Priority,
    bool PropagateDeletions,
    bool FilesOnDemandEnabled = false,
    IReadOnlyList<SyncSelectionRule>? SelectionRules = null,
    bool? SelectionDefaultIncluded = null,
    int? RecoveryRetentionDays = 30,
    SyncMode Direction = SyncMode.TwoWay,
    IReadOnlyList<SyncDirectoryStorageRule>? DirectoryStorageRules = null,
    string? ConnectionId = null,
    SyncDirectoryStorage? DefaultStorage = null,
    IReadOnlyList<SyncIgnoreRule>? IgnoreRules = null,
    long? ExpectedPolicyRevision = null,
    SyncEndpointDocument? Endpoints = null);

public sealed record SyncPairBindingResult(
    SyncPairSet PairSet,
    SyncPair Pair,
    PilotSettings? Settings,
    bool IdentityChanged);

public sealed record SyncAuthorityMonitorStartResult(
    string PairId,
    string State,
    string? FailureType,
    string? FailureMessage);

public sealed record SyncActiveRunSnapshot(string Id, string Kind, string? PairId, DateTimeOffset StartedUtc,
    bool Paused, bool StopRequested, SyncAuthorityBatchProgress? Progress, bool Preparing = false);

// ENG4: bounded typed presentation data for a first-sync terminal problem. It carries only product-owned text, a
// class-mapped code and the exact pair, account, command and retained endpoint identity - never a provider
// exception, payload, URL or stack.
public sealed record SyncFirstSyncProblem(string Code, string Phase, string Message, string? RelativePath,
    string NextAction, string CommandId, string PairId, string PairName, string? ConnectionId,
    DateTimeOffset ReportedUtc, SyncEndpointContext? EndpointContext);

// Presentation of an accepted command's retained failure. PairId is an exact
// requested target, not a claim that this pair's files all failed.
public sealed record SyncCommandProblem(string Code, string Phase, string Message, string NextAction,
    string CommandId, string PairId, DateTimeOffset ReportedUtc)
{
    public string Title => Code switch
    {
        "pair_run_failed" => "Sync needs attention",
        "recovery_restore_failed" => "Recovery restore needs attention",
        _ => "Sync command needs attention"
    };
    public string Description => Message + " " + NextAction + " Run " + CommandId + ".";
}

public sealed record SyncAuthorityBatchProgress(
    SyncPair Pair,
    string State,
    SyncRunResult? Result,
    string? FailureType,
    string? FailureMessage,
    SyncAuthorityLiveAction? Action = null,
    SyncComparisonProgress? Comparison = null);

public sealed record SyncComparisonProgress(string Phase, string RelativePath, int CompletedChecks,
    int TotalChecks, DateTimeOffset StartedUtc, DateTimeOffset ReportedUtc, string Detail = "",
    string ProgressUnit = "items checked");

public sealed record SyncAuthorityLiveAction(
    string OperationId,
    string RelativePath,
    string Action,
    string Stage,
    string Detail,
    DateTimeOffset OccurredUtc,
    string ItemKind = "Unknown",
    string FileType = "Unknown",
    SyncAuthorityOperationKind? OperationKind = null,
    SyncActivityPhase? Phase = null,
    SyncActivityProgress? Progress = null,
    SyncTransferBatchProgress? Batch = null);

public sealed record SyncAuthorityBatchItemResult(
    SyncPair Pair,
    SyncRunResult? Result,
    string? FailureType,
    string? FailureMessage);

public sealed record SyncAuthorityBatchResult(
    IReadOnlyList<SyncAuthorityBatchItemResult> Items,
    bool Stopped);

public sealed record DropboxConnectionStatus(
    DropboxConnectionBinding? Binding,
    bool HasProtectedCredential,
    bool LiveVerified,
    string Health,
    string? FailureType,
    int DependentPairCount,
    DateTimeOffset? LastSuccessfulSyncUtc = null,
    DropboxSpaceUsage? SpaceUsage = null);

public sealed record DropboxSpaceUsage(long UsedBytes, long AllocatedBytes, bool IsTeam, DateTimeOffset CheckedUtc,
    bool IsUnlimited = false);

public sealed record SyncConflictComparison(
    string ConflictId,
    string PairId,
    string RelativePath,
    long Generation,
    long? LocalSize,
    long? DropboxSize,
    string? LocalHash,
    string? DropboxHash,
    DateTimeOffset? LocalModifiedUtc,
    DateTimeOffset? DropboxModifiedUtc,
    string Group,
    string Recommendation,
    string Status,
    IReadOnlyList<SyncNoticeOption>? Options = null);

public sealed record SyncNoticeOption(string Choice, string Label);

public sealed record SyncConflictSelection(
    string ConflictId,
    string PairId,
    string RelativePath,
    long Generation);

public sealed record SyncConflictContent(
    string ConflictId,
    string PairId,
    string RelativePath,
    long? LocalSize,
    long? DropboxSize,
    string? LocalHash,
    string? DropboxHash,
    DateTimeOffset? LocalModifiedUtc,
    DateTimeOffset? DropboxModifiedUtc,
    byte[]? LocalBytes,
    byte[]? DropboxBytes,
    string? Note,
    // The exact endpoint context of the pair the content was loaded from: the describing context of the
    // pair's own ordered endpoints at the moment the preview was read. A consumer compares it with the pair
    // it is about to resolve against, so a pair rebind between preview and resolution is visible instead of
    // being resolved against the wrong pair. It is presentation evidence only and authorizes nothing; every
    // legacy member keeps its exact existing meaning.
    SyncEndpointContext? EndpointContext = null, string? MergeBaseText = null, string? MergedText = null,
    string? SnapshotA = null, string? SnapshotB = null);

public sealed record SyncBootstrapReviewItem(
    string PairId,
    string RelativePath,
    string Group,
    string ItemKind,
    string FileType,
    string Recommendation,
    string Effect,
    long? LocalSize,
    long? DropboxSize,
    DateTimeOffset? LocalModifiedUtc,
    DateTimeOffset? DropboxModifiedUtc,
    SyncBootstrapMoveReview? Move = null);

public sealed record SyncBootstrapReviewPage(
    IReadOnlyList<SyncBootstrapReviewItem> Items,
    int Offset,
    int TotalMatchingItems,
    int TotalItems,
    int LocalOnlyItems,
    int DropboxOnlyItems,
    int ConflictItems,
    bool HasMore,
    bool InventoryComplete,
    string? LocalScanGenerationId,
    string? DropboxScanGenerationId,
    int LocalUpdatedItems = 0,
    int DropboxUpdatedItems = 0);

public sealed record SyncBootstrapActionChoice(
    string RelativePath,
    string ExpectedGroup,
    string Action,
    SyncBootstrapMoveReview? Move = null);

public sealed record SyncBootstrapActionPlan(
    string PairId,
    string LocalScanGenerationId,
    string DropboxScanGenerationId,
    IReadOnlyList<SyncBootstrapActionChoice> Choices)
{
    internal SyncConsentTerminalLayout? ConsentLayout { get; init; }
}

public sealed record SyncBootstrapActionResult(
    SyncPair Pair,
    SyncRunResult Result,
    int AppliedItems,
    int SkippedItems,
    bool FirstSyncComplete);

public enum SyncRecoverySource
{
    LocalArchive,
    DropboxArchive,
    DropboxRevision,
    DropboxDeleted,
    CloudTrash
}

public sealed record SyncRecoveryCatalogItem(
    string Key,
    string PairId,
    string PairName,
    SyncRecoverySource Source,
    string RelativePath,
    string Connection,
    string Location,
    long? Size,
    DateTimeOffset? RetainedUtc,
    string Retention,
    string Health,
    string? ContentHash,
    string? Identity,
    string? Revision,
    string IdentityJson,
    bool CanRestore,
    string ObjectKind = "file",
    bool CanRemove = false,
    string? Provider = null,
    string? LogicalSide = null,
    string? ConnectionId = null)
{
    public bool CanRestoreCopy => CanRestore && Source != SyncRecoverySource.CloudTrash;
}

public sealed record SyncRecoveryCatalogPage(
    IReadOnlyList<SyncRecoveryCatalogItem> Items,
    int NextArchiveOffset,
    bool HasMoreArchives,
    string? DeletedCursor,
    bool HasMoreDeleted,
    IReadOnlyList<string>? Warnings = null);

public sealed record SyncRecoveryUsageSummary(
    string PairId,
    int ItemCount,
    long Size,
    DateTimeOffset? OldestRetainedUtc,
    int CleanupEligibleCount,
    int? RetentionDays);

public sealed record SyncRecoveryCleanupResult(string PairId, string Key, string Detail);

public sealed record SyncRecoveryPairChoice(string Id, string Name, bool Active)
{
    public string DisplayName => Active ? Name : Name + " (deleted pair)";
}

public sealed record SyncRecoveryEffectPreview(
    string PairId,
    string Key,
    SyncRecoverySource Source,
    string SourceRelativePath,
    string TargetRelativePath,
    bool RestoreAsCopy,
    bool DestinationExists,
    string Summary);

public sealed record SyncRecoveryRestoreResult(
    string PairId,
    string Key,
    string TargetRelativePath,
    string Detail);

public sealed record SyncRewindHandoff(
    SyncRewindSession Session,
    Uri DropboxFolderUrl,
    string ExpectedConnection,
    string Instruction);

public sealed record SyncRewindApplyResult(
    SyncRewindSession Session,
    SyncRunResult Result);
