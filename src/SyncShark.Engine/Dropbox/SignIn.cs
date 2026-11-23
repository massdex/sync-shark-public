namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>SignIn operations for the dropbox module.</summary>
public sealed class SignIn
{
    /// <summary>Receives the browser return on loopback; token exchange and account requests go through DropboxApi. Use the catalog acknowledgement on receipt, not a success claim; do not echo callback values.</summary>
    public Task<string> ConnectAsync(DropboxApi api, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>CreatePkceFlow for the selected SignIn operation.</summary>
    public static (string Verifier, string Challenge, string State) CreatePkceFlow() => throw new NotImplementedException();
    /// <summary>BuildAuthorizationUri for the selected SignIn operation.</summary>
    public static Uri BuildAuthorizationUri(string clientId, Uri redirectUri, string challenge, string state) => throw new NotImplementedException();
    /// <summary>ParseCallbackTarget for the selected SignIn operation.</summary>
    public static string ParseCallbackTarget(string target, string expectedPath, string expectedState) => throw new NotImplementedException();
    /// <summary>AcknowledgementPage for the selected SignIn operation.</summary>
    public static string AcknowledgementPage(bool received) => throw new NotImplementedException();
    /// <summary>Registered public application key.</summary>
    public const string ClientId = "fxzcfcfkv6f7gt6";
    /// <summary>Registered redirect URI.</summary>
    public static Uri RedirectUri => throw new NotImplementedException();
    /// <summary>Scopes required by the registered application.</summary>
    public static IReadOnlyList<string> RequiredScopes => throw new NotImplementedException();
    /// <summary>Connect and return the verified connection and namespaces.</summary>
    public Task<(string ConnectionId, string AccountId, string RootNamespaceId, string HomeNamespaceId)> ConnectAccountAsync(DropboxApi api, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Verify the supplied credential and return its account and namespace identities.</summary>
    public static Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> OpenIdentityAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string connectionId, string refreshToken, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Disconnect the retained account without deleting an unverified legacy credential.</summary>
    public static Task DisconnectAsync(
        Func<string, CancellationToken, Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)>> verify,
        CredentialStore credentials, string connectionId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Verify the retained account before first bind.</summary>
    public static Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> VerifyAccountAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CredentialStore credentials, string connectionId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Create a refreshable token delegate for a bound namespace.</summary>
    public static Func<CancellationToken, ValueTask<string?>> AccessTokenSource(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CredentialStore credentials, string connectionId, string accountId, string rootNamespaceId, string homeNamespaceId) => throw new NotImplementedException();
}

/// <summary>Supplies refreshed access tokens for one verified account.</summary>
public sealed class RefreshingAccessTokenSource
{
    /// <summary>Bind to the saved root identity.</summary>
    public RefreshingAccessTokenSource(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CredentialStore credentials, string connectionId, DropboxRootIdentity expected) => throw new NotImplementedException();
    /// <summary>Bind to the verified account before selecting a folder.</summary>
    public RefreshingAccessTokenSource(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CredentialStore credentials, string connectionId, string accountId, string rootNamespaceId, string homeNamespaceId) => throw new NotImplementedException();
    /// <summary>Get the current token for this account.</summary>
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default) => throw new NotImplementedException();
}
