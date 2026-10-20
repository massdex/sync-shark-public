namespace Massdex.SyncShark;

// Protected credential abstraction for Google Drive. No credential is read or written by this lane:
// the store is injected, and the registered per-account target name is derived rather than created.
public interface IGoogleDriveRefreshTokenStore
{
    Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default);

    Task WriteAsync(string connectionId, string refreshToken, CancellationToken cancellationToken = default);

    Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default);
}

// Fails closed: no credential is read or written until the activation owner supplies a protected store.
public sealed class UnconfiguredGoogleDriveCredentialStore : IGoogleDriveRefreshTokenStore
{
    public Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task WriteAsync(string connectionId, string refreshToken,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No protected Google Drive credential store is configured in this build.");

    public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public interface IGoogleDriveTokenEndpoint
{
    Task<GoogleDriveTokenResult> RefreshAsync(string clientId, string refreshToken,
        CancellationToken cancellationToken = default);
    Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Google account identity verification is unavailable.");
}

public sealed record GoogleDriveTokenResult(
    [property: System.Text.Json.Serialization.JsonIgnore] string AccessToken,
    DateTime ExpiresUtc, IReadOnlyList<string> Scopes,
    [property: System.Text.Json.Serialization.JsonIgnore] string? RefreshToken = null)
{
    public override string ToString() => "Google Drive token result (credentials redacted)";
}

// Fails closed until an activation owner injects a real token endpoint. This keeps the lane free of
// hard-coded client identity, client secret and consent capture while leaving the data path testable.
public sealed class UnconfiguredGoogleDriveTokenEndpoint : IGoogleDriveTokenEndpoint
{
    public Task<GoogleDriveTokenResult> RefreshAsync(string clientId, string refreshToken,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No Google Drive token endpoint is configured. Activation requires a registered Google Cloud " +
            "OAuth client, the drive scope consent, and a protected refresh token. This build cannot " +
            "authenticate until those are bound and verified.");
}

public sealed class GoogleDriveAccessTokenSource(
    IGoogleDriveRefreshTokenStore credentialStore,
    IGoogleDriveTokenEndpoint endpoint,
    GoogleDriveConnectionBinding expectedConnection) : IGoogleDriveAccessTokenSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _refreshAtUtc = DateTime.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Keep the token/expiry pair coherent under concurrent file requests.
        // Cached reads release this gate immediately; file I/O never holds it.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _refreshAtUtc) return _accessToken;
            var binding = expectedConnection.Normalize();
            GoogleDriveProviderConfiguration.ValidateConnection(binding);
            var refreshToken = await credentialStore.ReadAsync(binding.ConnectionId, cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException(
                    "This Google account needs to be connected or reconnected before syncing continues.");
            var result = await endpoint.RefreshAsync(binding.ClientId, refreshToken!, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.AccessToken))
                throw new InvalidDataException("The Google Drive token response contained no access token.");
            if (!GoogleDriveProviderConfiguration.HasRequiredGrant(result.Scopes))
                throw new InvalidDataException("Google Drive access was not granted. Reconnect this account.");
            if (result.ExpiresUtc.Kind != DateTimeKind.Utc || result.ExpiresUtc <= DateTime.UtcNow.AddSeconds(5))
                throw new InvalidDataException("The Google Drive token has no usable lifetime.");
            var account = await endpoint.GetAccountAsync(result.AccessToken, cancellationToken);
            if (account.PermissionId != binding.PermissionId)
                throw new InvalidDataException("Google authorization belongs to a different account. No file operation was issued.");
            if (!string.IsNullOrWhiteSpace(result.RefreshToken) && result.RefreshToken != refreshToken)
                await credentialStore.WriteAsync(binding.ConnectionId, result.RefreshToken, cancellationToken);
            _accessToken = result.AccessToken;
            // Start a new request with a one-minute margin. Transfer retry handles expiry mid-request.
            _refreshAtUtc = result.ExpiresUtc.ToUniversalTime().AddMinutes(-1);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }
}

// One state file per provider keeps account isolation explicit and avoids rewriting Dropbox state.
public sealed class GoogleDriveConnectionStore
{
    private readonly MigratingMutableJsonStore<GoogleDriveConnectionBinding> _store;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeGate;

    public GoogleDriveConnectionStore(PilotStateAuthority authority)
    {
        _writeGate = WriteGates.GetOrAdd(Path.GetFullPath(authority.StateRoot), _ => new SemaphoreSlim(1, 1));
        _store = new MigratingMutableJsonStore<GoogleDriveConnectionBinding>(authority,
            SyncSharkRuntimePaths.GoogleDriveConnectionFileName);
    }

    public bool Exists => _store.Exists;

    // Load never validates the registration state: an unregistered build must still be able to read the
    // stored account list and report the activation requirement instead of failing to enumerate state.
    public GoogleDriveConnectionBinding? Load()
    {
        var binding = _store.Load()?.Normalize();
        if (binding is null) return null;
        ValidateCollection(binding);
        return binding;
    }

    private static void ValidateIdentity(GoogleDriveConnectionBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.PermissionId) ||
            string.IsNullOrWhiteSpace(binding.ClientId) ||
            binding.AccountKey != GoogleDriveConnectionBinding.CreateAccountKey(binding.PermissionId) ||
            binding.ConnectionId != GoogleDriveConnectionBinding.CreateConnectionId(binding.AccountKey))
            throw new InvalidDataException("The Google Drive connection is not bound to its account identity.");
    }

    private static void ValidateCollection(GoogleDriveConnectionBinding primary)
    {
        ValidateIdentity(primary);
        var ids = new HashSet<string>(StringComparer.Ordinal) { primary.ConnectionId };
        var accounts = new HashSet<string>(StringComparer.Ordinal) { primary.PermissionId };
        foreach (var raw in primary.AdditionalConnections ?? [])
        {
            var binding = raw.Normalize();
            ValidateIdentity(binding);
            if (binding.AdditionalConnections?.Count > 0 || binding.RetainedConnections?.Count > 0)
                throw new InvalidDataException("Nested Google Drive connection state is not permitted.");
            if (!ids.Add(binding.ConnectionId) || !accounts.Add(binding.PermissionId) ||
                binding.ClientId != primary.ClientId)
                throw new InvalidDataException("Duplicate or incorrectly bound Google Drive account.");
        }
    }

    public IReadOnlyList<GoogleDriveConnectionBinding> LoadActiveBindings()
    {
        var primary = Load();
        if (primary is null) return [];
        var result = new List<GoogleDriveConnectionBinding> { primary };
        var ids = new HashSet<string>(StringComparer.Ordinal) { primary.ConnectionId };
        var accounts = new HashSet<string>(StringComparer.Ordinal) { primary.PermissionId };
        foreach (var raw in primary.AdditionalConnections ?? [])
        {
            var binding = raw.Normalize();
            if (binding.AdditionalConnections?.Count > 0 || binding.RetainedConnections?.Count > 0)
                throw new InvalidDataException("Nested Google Drive connection state is not permitted.");
            if (string.IsNullOrWhiteSpace(binding.PermissionId) ||
                !ids.Add(binding.ConnectionId) || !accounts.Add(binding.PermissionId) ||
                binding.ConnectionId != GoogleDriveConnectionBinding.CreateConnectionId(binding.AccountKey))
                throw new InvalidDataException("Duplicate or incorrectly bound Google Drive account.");
            result.Add(binding);
        }
        return result;
    }

    public GoogleDriveConnectionBinding? Load(string? connectionId)
    {
        var primary = Load();
        if (primary is null) return null;
        if (string.IsNullOrWhiteSpace(connectionId)) return primary;
        if (string.Equals(primary.ConnectionId, connectionId, StringComparison.Ordinal)) return primary;
        return (primary.AdditionalConnections ?? [])
            .Select(item => item.Normalize())
            .SingleOrDefault(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal));
    }

    public async Task SaveAsync(GoogleDriveConnectionBinding binding,
        CancellationToken cancellationToken = default)
    {
        binding = binding.Normalize();
        ValidateCollection(binding);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var primary = Load();
            if (primary is null)
            {
                await _store.SaveAsync(binding, cancellationToken);
                return;
            }
            if (string.Equals(primary.ConnectionId, binding.ConnectionId, StringComparison.Ordinal))
            {
                if (!string.Equals(primary.PermissionId, binding.PermissionId, StringComparison.Ordinal) ||
                    !string.Equals(primary.ClientId, binding.ClientId, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "An existing Google Drive connection cannot be reassigned to a different account.");
                var replacement = binding with { AdditionalConnections = primary.AdditionalConnections,
                    SharedDrivesEnabled = primary.SharedDrivesEnabled };
                ValidateCollection(replacement);
                await _store.SaveAsync(replacement, cancellationToken);
                return;
            }
            var retained = primary.AdditionalConnections?.SingleOrDefault(item => item.ConnectionId == binding.ConnectionId);
            var additional = (primary.AdditionalConnections ?? [])
                .Where(item => !string.Equals(item.ConnectionId, binding.ConnectionId, StringComparison.Ordinal))
                .Append(binding with { AdditionalConnections = null, RetainedConnections = null,
                    SharedDrivesEnabled = retained?.SharedDrivesEnabled ?? binding.SharedDrivesEnabled })
                .ToArray();
            var updated = primary with { AdditionalConnections = additional };
            ValidateCollection(updated);
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    // This local preference has one writer. An in-flight health refresh or reauthorization must not
    // revert it by saving an older account snapshot. Credentials, scopes and other accounts are unchanged.
    public async Task<GoogleDriveConnectionBinding> SetSharedDrivesEnabledAsync(string connectionId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var primary = Load() ?? throw new InvalidOperationException("No Google connection is retained.");
            var selected = primary.ConnectionId == connectionId ? primary :
                primary.AdditionalConnections?.SingleOrDefault(item => item.ConnectionId == connectionId)
                ?? throw new InvalidOperationException("The exact Google account is not retained.");
            var updated = selected with { SharedDrivesEnabled = enabled };
            if (selected.SharedDrivesEnabled == enabled) return updated;
            var collection = primary.ConnectionId == connectionId ? updated : primary with {
                AdditionalConnections = primary.AdditionalConnections!.Select(item => item.ConnectionId == connectionId
                    ? updated : item).ToArray() };
            ValidateCollection(collection);
            await _store.SaveAsync(collection, cancellationToken);
            return updated;
        }
        finally { _writeGate.Release(); }
    }

    public async Task RemoveAdditionalAsync(string connectionId,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var primary = Load() ?? throw new InvalidOperationException("No Google connection is retained.");
            if (string.Equals(primary.ConnectionId, connectionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The primary Google connection cannot be removed here.");
            await _store.SaveAsync(primary with
            {
                AdditionalConnections = (primary.AdditionalConnections ?? [])
                    .Where(item => !string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
                    .ToArray()
            }, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try { await _store.DeleteAsync(cancellationToken); }
        finally { _writeGate.Release(); }
    }
}

// Source-only OAuth exchange shape. No client secret, consent capture or browser flow is implemented in
// this lane; the plan below is the exact activation prerequisite recorded for the owning task.
public sealed record GoogleDriveOAuthPlan(
    string RedirectUriKind,
    string CodeChallengeMethod,
    bool RequiresOfflineAccess,
    IReadOnlyList<string> Scopes,
    string RequiredConsent)
{
    public static GoogleDriveOAuthPlan Registered() => new(
        RedirectUriKind: "loopback",
        CodeChallengeMethod: "S256",
        RequiresOfflineAccess: true,
        Scopes: GoogleDriveProviderConfiguration.RequiredScopes,
        RequiredConsent: GoogleDriveProviderConfiguration.RestrictedScopeDisclosure);
}
