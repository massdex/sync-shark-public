namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Refreshes and caches one connection's access token from its stored refresh token, and verifies on every
/// refresh that the authorization still belongs to the requested connection, account and namespaces. The composition
/// root passes its "send" as <see cref="DropboxApi.SendAsync"/> or builds the delegate with <see cref="SignIn.AccessTokenSource"/>.</summary>
public sealed class RefreshingAccessTokenSource
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly CredentialStore _credentials;
    private readonly string _connectionId;
    private readonly DropboxRootIdentity _expected;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _refreshAtUtc;

    public RefreshingAccessTokenSource(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, DropboxRootIdentity expected)
        : this(send, credentials, connectionId, expected.AccountId, expected.RootNamespaceId, expected.HomeNamespaceId) { }

    /// <summary>The first-bind form, for a connection whose pair root (folder id) is not saved yet.</summary>
    public RefreshingAccessTokenSource(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, string accountId, string rootNamespaceId,
        string homeNamespaceId)
    {
        ArgumentNullException.ThrowIfNull(send, nameof(send));
        ArgumentNullException.ThrowIfNull(credentials, nameof(credentials));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId, nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootNamespaceId, nameof(rootNamespaceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(homeNamespaceId, nameof(homeNamespaceId));
        _ = CredentialStore.AccountTarget(connectionId);
        _send = send;
        _credentials = credentials;
        _connectionId = connectionId;
        _expected = new DropboxRootIdentity(accountId, rootNamespaceId, homeNamespaceId, string.Empty);
    }

    /// <summary>A valid access token for this connection, refreshed and re-verified only when the cached one nears expiry.
    /// Its shape fits <see cref="DropboxApi"/>'s access-token delegate; it throws rather than returning null when signed out.</summary>
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _refreshAtUtc) return _accessToken;
            var account = await SignIn.OpenRetainedAsync(_send, _credentials, _connectionId, ct);
            if (!string.Equals(account.AccountId, _expected.AccountId, StringComparison.Ordinal) ||
                !string.Equals(account.RootNamespaceId, _expected.RootNamespaceId, StringComparison.Ordinal) ||
                !string.Equals(account.HomeNamespaceId, _expected.HomeNamespaceId, StringComparison.Ordinal))
                throw new InvalidOperationException(Text.Get(TextKey.RootDropboxScopeChanged));
            _accessToken = account.AccessToken;
            _refreshAtUtc = account.ExpiresUtc.AddMinutes(-5);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }
}
