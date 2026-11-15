namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    // ---- Settings: app preference stores, not sync state (forwarded to the v1 host). ----

    /// <inheritdoc />
    public SyncAutomationSettings LoadAutomation() => _v1.LoadAutomation();

    /// <inheritdoc />
    public Task SaveAutomationAsync(SyncAutomationSettings settings, CancellationToken cancellationToken = default) =>
        _v1.SaveAutomationAsync(settings, cancellationToken);

    /// <inheritdoc />
    public SyncTransferSettings LoadTransferSettings() => _v1.LoadTransferSettings();

    /// <inheritdoc />
    public Task SaveTransferSettingsAsync(SyncTransferSettings settings, CancellationToken cancellationToken = default) =>
        _v1.SaveTransferSettingsAsync(settings, cancellationToken);

    /// <inheritdoc />
    public SyncTransferResourceStatus TransferResourceStatus => _v1.TransferResourceStatus;

    /// <inheritdoc />
    public SyncPresentationSnapshot LoadPresentationSnapshot() => _v1.LoadPresentationSnapshot();

    // ---- Authorization-attempt state (forwarded to the v1 host). ----

    /// <inheritdoc />
    public SyncPendingAuthorizationSnapshot? PendingAuthorization => _v1.PendingAuthorization;

    /// <inheritdoc />
    public event Action? AuthorizationReturnRequested
    {
        add => _v1.AuthorizationReturnRequested += value;
        remove => _v1.AuthorizationReturnRequested -= value;
    }

    /// <inheritdoc />
    public event Action? AuthorizationAttemptChanged
    {
        add => _v1.AuthorizationAttemptChanged += value;
        remove => _v1.AuthorizationAttemptChanged -= value;
    }

    /// <inheritdoc />
    public bool RequestAuthorizationAttemptCancellation(string attemptId) =>
        _v1.RequestAuthorizationAttemptCancellation(attemptId);

    // ---- Accounts (forwarded to the v1 host). ----

    /// <inheritdoc />
    public SyncAccountBinding? LoadAccountBinding(string? connectionId = null) => _v1.LoadAccountBinding(connectionId);

    /// <inheritdoc />
    public Task<bool> IsAnyAccountConnectedAsync(CancellationToken cancellationToken = default) =>
        _v1.IsAnyAccountConnectedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<GoogleDriveConnectionBinding> ConnectGoogleDriveAsync(Func<Uri, Task> openPage,
        CancellationToken cancellationToken = default, string? connectionId = null) =>
        _v1.ConnectGoogleDriveAsync(openPage, cancellationToken, connectionId);

    /// <inheritdoc />
    public Task<SyncAccountStatus> GetAccountStatusAsync(bool verifyLive = true,
        CancellationToken cancellationToken = default, string? connectionId = null) =>
        _v1.GetAccountStatusAsync(verifyLive, cancellationToken, connectionId);

    /// <inheritdoc />
    public Task<GoogleDriveConnectionBinding> SetGoogleSharedDrivesEnabledAsync(string connectionId, bool enabled,
        CancellationToken cancellationToken = default) =>
        _v1.SetGoogleSharedDrivesEnabledAsync(connectionId, enabled, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<ProviderConnectionRecord> LoadProviderConnections() => _v1.LoadProviderConnections();

    /// <inheritdoc />
    public DropboxConnectionBinding? LoadDropboxConnection(string? connectionId = null) =>
        _v1.LoadDropboxConnection(connectionId);

    /// <inheritdoc />
    public Task<bool> IsDropboxConnectedAsync(CancellationToken cancellationToken = default) =>
        _v1.IsDropboxConnectedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DropboxConnectionBinding> ReauthorizeDropboxAsync(Func<Uri, Task> openAuthorizationPage,
        CancellationToken cancellationToken = default, string? connectionId = null, bool includeFileRequests = false) =>
        _v1.ReauthorizeDropboxAsync(openAuthorizationPage, cancellationToken, connectionId, includeFileRequests);

    /// <inheritdoc />
    public Task<DropboxConnectionBinding> ConnectDropboxAsync(Func<Uri, Task> openAuthorizationPage,
        CancellationToken cancellationToken = default, bool addAccount = false) =>
        _v1.ConnectDropboxAsync(openAuthorizationPage, cancellationToken, addAccount);

    /// <inheritdoc />
    public Task<IDropboxFolderBrowserService> CreateFolderBrowserAsync(CancellationToken cancellationToken = default,
        string? connectionId = null) => _v1.CreateFolderBrowserAsync(cancellationToken, connectionId);

    /// <inheritdoc />
    public GoogleDriveConnectionBinding? LoadGoogleDriveConnection(string? connectionId = null) =>
        _v1.LoadGoogleDriveConnection(connectionId);

    /// <inheritdoc />
    public IReadOnlyList<GoogleDriveConnectionBinding> LoadGoogleDriveConnections() =>
        _v1.LoadGoogleDriveConnections();

    /// <inheritdoc />
    public bool IsAccountPaused(string? connectionId) => _v1.IsAccountPaused(connectionId);

    /// <inheritdoc />
    public bool IsPairAccountPaused(SyncPair pair) => _v1.IsPairAccountPaused(pair);

    /// <inheritdoc />
    public async Task DisconnectDropboxAsync(CancellationToken cancellationToken = default, string? connectionId = null)
    {
        RefuseDisconnectWhileV2PairsUseAccount(connectionId, SyncProviders.Dropbox);
        await _v1.DisconnectDropboxAsync(cancellationToken, connectionId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisconnectGoogleDriveAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        RefuseDisconnectWhileV2PairsUseAccount(connectionId, SyncProviders.GoogleDrive);
        await _v1.DisconnectGoogleDriveAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    // ---- Sharing: only the account-scoped members forward; the pair-scoped members (and Discovery and Explorer
    // status) are answered in NotInV2.cs, so a v2 pair id never reaches the v1 pair store. ----

    /// <inheritdoc />
    public Task<IAccountSharingClient> CreateAccountSharingClientAsync(string connectionId,
        CancellationToken cancellationToken = default) =>
        _v1.CreateAccountSharingClientAsync(connectionId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SharedLinkRevokeResult>> RevokeSharedLinksAsync(IReadOnlyList<SharedLinkRevokeTarget> targets,
        CancellationToken cancellationToken = default) => _v1.RevokeSharedLinksAsync(targets, cancellationToken);

    // A disconnect must not silently break the v2 pairs that use the account: v1's own dependent-pair check only
    // sees v1 pairs, so a v2 root bound to the connection (a null Dropbox id means the primary) refuses first.
    private void RefuseDisconnectWhileV2PairsUseAccount(string? connectionId, string provider)
    {
        foreach (var pair in _store.Pairs())
            foreach (var root in new[] { pair.A, pair.B })
            {
                if (!string.Equals(root.Provider, provider, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(root.AccountId, connectionId, StringComparison.Ordinal) ||
                    (string.Equals(provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase) &&
                     connectionId is null))
                    throw new InvalidOperationException("Remove or switch the v2 pairs that use this account first.");
            }
    }
}
