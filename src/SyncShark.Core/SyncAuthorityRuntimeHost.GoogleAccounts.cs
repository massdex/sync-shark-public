using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private GoogleDriveOAuthEndpoint? _ownedGoogleEndpoint;
    private IGoogleDriveRefreshTokenStore GoogleCredentials =>
        _googleDriveCredentials ?? new WindowsGoogleDriveRefreshTokenStore();
    private IGoogleDriveTokenEndpoint GoogleEndpoint => _googleDriveEndpoint ??
        (_ownedGoogleEndpoint ??= WindowsGoogleDriveRefreshTokenStore.OpenRegisteredClient());

    public async Task<bool> IsAnyAccountConnectedAsync(CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        if (await IsDropboxConnectedAsync(cancellationToken)) return true;
        foreach (var account in _googleDriveConnections.LoadActiveBindings())
            if (!string.IsNullOrWhiteSpace(await GoogleCredentials.ReadAsync(account.ConnectionId, cancellationToken)))
                return true;
        return false;
    }

    public SyncAccountBinding? LoadAccountBinding(string? connectionId = null)
    {
        if (connectionId is not null && _googleDriveConnections.Load(connectionId) is { } google)
            return SyncAccountBinding.From(google);
        if (connectionStore.Load(connectionId) is { } dropbox) return SyncAccountBinding.From(dropbox);
        return connectionId is null && _googleDriveConnections.Load() is { } primary
            ? SyncAccountBinding.From(primary) : null;
    }

    public async Task<GoogleDriveConnectionBinding> ConnectGoogleDriveAsync(Func<Uri, Task> openPage,
        CancellationToken cancellationToken = default, string? connectionId = null)
    {
        stateAuthority.VerifyAuthority();
        if (HasActiveRun) throw new InvalidOperationException("Wait for the active sync before connecting an account.");
        if (GoogleEndpoint is not GoogleDriveOAuthEndpoint endpoint)
            throw new InvalidOperationException("Google authorization is not available on this runtime.");
        // AUTH1: the same one-pending-attempt controller covers this provider too. An existing connection
        // id is a reauthorization of that exact account; a null id is a new connection.
        using var attempt = BeginAuthorizationAttempt(SyncProviders.GoogleDrive,
            connectionId is null ? SyncAuthorizationAction.Connect : SyncAuthorizationAction.Reauthorize,
            connectionId, cancellationToken);
        var coordinator = new GoogleDriveConnectionCoordinator(endpoint, CreateAuthorizationReceiver(),
            GoogleCredentials, _googleDriveConnections);
        var binding = await coordinator.ConnectAsync(openPage, connectionId, attempt.Token);
        attempt.MarkCommitted();
        _googleTokens.TryRemove(binding.ConnectionId, out _);
        return binding;
    }

    public async Task<SyncAccountStatus> GetAccountStatusAsync(bool verifyLive = true,
        CancellationToken cancellationToken = default, string? connectionId = null)
    {
        if (!IsGoogleConnection(connectionId))
            return await GetDropboxConnectionStatusAsync(verifyLive, cancellationToken, connectionId);
        stateAuthority.VerifyAuthority();
        var binding = _googleDriveConnections.Load(connectionId)!;
        var credential = await GoogleCredentials.ReadAsync(binding.ConnectionId, cancellationToken);
        var count = (await LoadPairsAsync(cancellationToken)).Pairs.Count(p => p.ConnectionId == binding.ConnectionId);
        var last = await LoadLastSuccessfulSyncSafeAsync(cancellationToken, binding.ConnectionId);
        if (string.IsNullOrWhiteSpace(credential))
            return new(SyncAccountBinding.From(binding), false, false, "Disconnected. Reconnect this Google account.", null, count, last);
        if (!verifyLive)
            return new(SyncAccountBinding.From(binding), true, false, "Connected; live verification pending.", null, count, last,
                GoogleQuotaUsage(binding.StorageQuotaJson, binding.LastVerifiedUtc));
        try
        {
            var access = await GoogleTokenSource(binding).GetAccessTokenAsync(cancellationToken);
            var account = await GoogleEndpoint.GetAccountAsync(access, cancellationToken);
            if (account.PermissionId != binding.PermissionId)
                throw new InvalidDataException("Google authorization does not match the retained account.");
            binding = binding with { Email = account.Email, DisplayName = account.DisplayName,
                LastVerifiedUtc = DateTimeOffset.UtcNow, StorageQuotaJson = account.StorageQuotaJson };
            await _googleDriveConnections.SaveAsync(binding, cancellationToken);
            var usage = GoogleQuotaUsage(account.StorageQuotaJson, binding.LastVerifiedUtc);
            return new(SyncAccountBinding.From(binding), true, true, "Connected and verified.", null, count, last, usage);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { return new(SyncAccountBinding.From(binding), true, false, "Google connection needs attention. Reconnect or check again.", error.GetType().Name, count, last); }
    }

    internal static DropboxSpaceUsage? GoogleQuotaUsage(string? json, DateTimeOffset? checkedUtc)
    {
        if (string.IsNullOrWhiteSpace(json) || checkedUtc is null) return null;
        try
        {
            using var quota = JsonDocument.Parse(json);
            if (!quota.RootElement.TryGetProperty("usage", out var used) ||
                !long.TryParse(used.ToString(), out var bytes) || bytes < 0) return null;
            if (!quota.RootElement.TryGetProperty("limit", out var total))
                return new(bytes, 0, false, checkedUtc.Value, IsUnlimited: true);
            return long.TryParse(total.ToString(), out var capacity) && capacity >= 0
                ? new(bytes, capacity, false, checkedUtc.Value) : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<GoogleDriveConnectionBinding> SetGoogleSharedDrivesEnabledAsync(string connectionId,
        bool enabled, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (_googleDriveConnections.Load(connectionId) is null)
            throw new InvalidOperationException("Choose an exact connected Google Drive account.");
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Wait for the active sync before changing shared-drive access.");
        try
        {
            // Close clients holding the old preference before changing it. This does not scan, sync,
            // change provider permissions or edit any pair; shared-drive pairs fail closed when off.
            await StopAccountRuntimeAsync(connectionId, cancellationToken);
            return await _googleDriveConnections.SetSharedDrivesEnabledAsync(connectionId, enabled, cancellationToken);
        }
        finally { _runGate.Release(); }
    }

    public async Task DisconnectGoogleDriveAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var binding = _googleDriveConnections.Load(connectionId) ?? throw new InvalidOperationException("Google account is not retained.");
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Wait for the active sync before disconnecting an account.");
        try
        {
            await StopAccountRuntimeAsync(connectionId, cancellationToken);
            await GoogleCredentials.DeleteAsync(binding.ConnectionId, cancellationToken);
            _googleTokens.TryRemove(connectionId, out _);
            // Retain identity and pair history. Missing credentials never authorise deletion or
            // allow a different account to inherit these pairs. Provider grant revocation is separate.
        }
        finally { _runGate.Release(); }
    }
}
