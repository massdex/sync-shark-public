using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Full-account Dropbox sign-in: opens the browser, receives the loopback return, exchanges the code and reads
/// the account (all HTTP through <see cref="DropboxApi"/> and the approved endpoint data), checks the granted scope
/// envelope, then saves the refresh token through <see cref="CredentialStore"/> before returning the connection id.
/// One bounded attempt covers receipt, exchange and account read; browser pages carry the catalog receipt copy, never a
/// success claim, and never reflect callback values.</summary>
public sealed class SignIn
{
    /// <summary>The registered Dropbox application key.</summary>
    public const string ClientId = "fxzcfcfkv6f7gt6";

    // The one registered loopback callback fact, as 0.3.95 registered it.
    private const string RegisteredCallback = "http://127.0.0.1:47821/oauth/dropbox/callback/";
    private const string BearerScheme = "Bearer";
    private const string OAuthScope = "offline";

    /// <summary>Every coordinator uses the same registered loopback callback.</summary>
    public static Uri RedirectUri { get; } = new(RegisteredCallback);

    /// <summary>The full-account permission envelope the registered application requests and requires.</summary>
    public static readonly IReadOnlyList<string> RequiredScopes =
    [
        "account_info.read",
        "files.content.read",
        "files.content.write",
        "files.metadata.read",
        "files.metadata.write",
        "sharing.read",
        "sharing.write"
    ];

    // 0.3.95's default bound on one whole authorization attempt (DropboxOAuth.RunOperations).
    private static readonly TimeSpan AuthorizationBound = new(0, 5, 0);

    // One authorization flow at a time: never queue an invisible second browser flow behind an abandoned first one.
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);

    /// <summary>Receives the browser return on loopback; token exchange and account requests go through DropboxApi.
    /// Uses the catalog acknowledgement on receipt, not a success claim, and does not echo callback values. Saves the
    /// refresh token through CredentialStore before returning the connection id.</summary>
    public async Task<string> ConnectAsync(DropboxApi api, CancellationToken ct) =>
        (await ConnectAccountAsync(api, ct)).ConnectionId;

    /// <summary>The first-bind form of <see cref="ConnectAsync"/>: also returns the verified account and namespace ids,
    /// so a caller can build the pair's saved root identity before one exists. The refresh token is saved first.</summary>
    public Task<(string ConnectionId, string AccountId, string RootNamespaceId, string HomeNamespaceId)>
        ConnectAccountAsync(DropboxApi api, CancellationToken ct) => ConnectAccountAsync(api, ct, StartBrowser);

    /// <summary>Connects using the supplied browser launcher; the callback and grant checks are unchanged.</summary>
    public Task<(string ConnectionId, string AccountId, string RootNamespaceId, string HomeNamespaceId)>
        ConnectAccountAsync(DropboxApi api, CancellationToken ct, Action<Uri> openBrowser)
    {
        ArgumentNullException.ThrowIfNull(api, nameof(api));
        ArgumentNullException.ThrowIfNull(openBrowser);
        return RunAuthorizationAttemptAsync(ct, AuthorizationBound, async attempt =>
        {
            var redirect = RedirectUri;
            var (verifier, challenge, state) = CreatePkceFlow();
            var code = await SignInCallback.ReceiveCallbackAsync(redirect, redirect.AbsolutePath, state, attempt,
                () => openBrowser(BuildAuthorizationUri(ClientId, redirect, challenge, state)));
            var token = await SignInTokens.TokenAsync(api.SendAsync, new Dictionary<string, string>
            {
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["redirect_uri"] = redirect.AbsoluteUri,
                ["code_verifier"] = verifier
            }, connectionId: null, attempt);
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            if (!GrantAccepted(token.Scopes)) throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
            var account = await AccountAsync(api.SendAsync, token.AccessToken, attempt);
            if (token.AccountId is not null && !string.Equals(token.AccountId, account.AccountId, StringComparison.Ordinal))
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            var connectionId = Connections.CreateConnectionId(account.AccountId);
            new CredentialStore().Save(connectionId, token.RefreshToken!);
            return (connectionId, account.AccountId, account.RootNamespaceId, account.HomeNamespaceId);
        });
    }

    /// <summary>Runs one authorization flow under the one-flow gate and the predecessor's bound. The bound covers the
    /// whole attempt (receipt, exchange and account read); an expired bound ends it with a retryable timeout and frees
    /// the gate for the next attempt.</summary>
    public static async Task<T> RunAuthorizationAttemptAsync<T>(CancellationToken ct, TimeSpan bound,
        Func<CancellationToken, Task<T>> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt, nameof(attempt));
        if (!await AuthorizationGate.WaitAsync(TimeSpan.Zero, ct)) throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry));
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(bound);
            try { return await attempt(bounded.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Text.Get(TextKey.SignInReturnRetry));
            }
        }
        finally { AuthorizationGate.Release(); }
    }

    /// <summary>An access-token delegate for one connection, for the composition root to pass to <see cref="DropboxApi"/>.
    /// The named type stays inside this module, so the root needs only the allowed SignIn name.</summary>
    public static Func<CancellationToken, ValueTask<string?>> AccessTokenSource(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, string accountId, string rootNamespaceId,
        string homeNamespaceId) =>
        new RefreshingAccessTokenSource(send, credentials, connectionId, accountId, rootNamespaceId, homeNamespaceId)
            .GetAccessTokenAsync;

    /// <summary>A fresh S256 PKCE verifier and challenge plus an unguessable state.</summary>
    public static (string Verifier, string Challenge, string State) CreatePkceFlow()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge, Base64Url(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>The authorization page URL for one flow from the approved endpoint data, requesting offline access.</summary>
    public static Uri BuildAuthorizationUri(string clientId, Uri redirectUri, string challenge, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId, nameof(clientId));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["token_access_type"] = OAuthScope,
            ["scope"] = string.Join(' ', RequiredScopes)
        };
        return new Uri(Routes.Endpoint(DropboxProtocol.AuthorizationPath).AbsoluteUri + "?" +
                       string.Join('&', query.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value))));
    }

    /// <summary>The grant's scopes must be exactly the registered envelope (order-insensitive) before any credential is saved.</summary>
    public static bool GrantAccepted(IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted, nameof(granted));
        return granted.Where(scope => !string.IsNullOrWhiteSpace(scope)).ToHashSet(StringComparer.Ordinal)
            .SetEquals(RequiredScopes);
    }

    /// <summary>Opens a retained connection by id and returns the account namespaces it actually holds; a credential that
    /// does not derive the requested connection id is refused. A missing credential reports the reconnect action.</summary>
    public static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> VerifyAccountAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, CancellationToken ct)
    {
        var account = await OpenRetainedAsync(send, credentials, connectionId, ct);
        return (account.AccountId, account.RootNamespaceId, account.HomeNamespaceId);
    }

    /// <summary>Refreshes a supplied credential and reads its account identity; a revoked refresh token is unavailable.</summary>
    public static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> OpenIdentityAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string connectionId, string refreshToken, CancellationToken ct)
    {
        var opened = await OpenVerifiedAsync(send, connectionId, refreshToken, ct);
        return (opened.AccountId, opened.RootNamespaceId, opened.HomeNamespaceId);
    }

    internal static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId,
            string AccessToken, DateTime ExpiresUtc)> OpenVerifiedAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string connectionId, string refreshToken, CancellationToken ct)
    {
        _ = CredentialStore.AccountTarget(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken, nameof(refreshToken));
        var token = await SignInTokens.TokenAsync(send, new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["scope"] = string.Join(' ', RequiredScopes)
        }, connectionId, ct);
        var account = await AccountAsync(send, token.AccessToken, ct);
        // The predecessor rejected a token response naming a different account before trusting the account read.
        if (token.AccountId is not null && !string.Equals(token.AccountId, account.AccountId, StringComparison.Ordinal))
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        return (account.AccountId, account.RootNamespaceId, account.HomeNamespaceId, token.AccessToken, token.ExpiresUtc);
    }

    /// <summary>Disconnect: removes the connection's own credential, and the fixed legacy credential only once it is
    /// proven (by the account it opens) to belong to this connection. The host/composition owner must call this for a
    /// disconnect. When the legacy owner cannot be reached (offline or an unproven ownership), the local disconnect still
    /// completes and the fixed entry is left in place (R41); a revoked token is removed as unusable; caller cancellation
    /// and native-store failures are not swallowed.</summary>
    public static async Task DisconnectAsync(
        Func<string, CancellationToken, Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)>> verify,
        CredentialStore credentials, string connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verify, nameof(verify));
        ArgumentNullException.ThrowIfNull(credentials, nameof(credentials));
        _ = CredentialStore.AccountTarget(connectionId);
        var observed = CredentialStore.ObserveOwn(connectionId);
        var legacy = credentials.ReadLegacy();
        if (legacy is not null)
        {
            try
            {
                var account = await verify(legacy, ct);
                if (Connections.CreateConnectionId(account.AccountId) == connectionId) credentials.RemoveLegacy();
            }
            catch (DropboxCredentialUnavailableException)
            {
                // A revoked token cannot be used again; it belongs to no live account and is removed.
                credentials.RemoveLegacy();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled the disconnect; it is never swallowed.
                throw;
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or
                InvalidDataException or HttpRequestException or IOException or SocketException)
            {
                // Offline or unproven ownership (including an HTTP timeout or an interrupted response body):
                // the local disconnect completes and the fixed entry is left in place (R41).
            }
        }
        if (observed is { } entry) credentials.RemoveObserved(connectionId, entry);
    }

    /// <summary>Reads the opened account's identity; incomplete identity information refuses.</summary>
    internal static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> AccountAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(DropboxProtocol.GetCurrentAccount))
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };
        try { request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken); }
        catch (FormatException) { throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid)); }
        using var response = await send(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Text.Get(TextKey.SignInReturnRetry), null, response.StatusCode);
        using var document = await SignInTokens.ReadJsonAsync(response, ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("root_info", out var rootInfo) || rootInfo.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        return (SignInTokens.RequiredString(root, "account_id"), SignInTokens.RequiredString(rootInfo, "root_namespace_id"),
            SignInTokens.RequiredString(rootInfo, "home_namespace_id"));
    }

    /// <summary>Opens the retained connection or reports that it needs sign-in, without changing credential custody.</summary>
    internal static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId,
            string AccessToken, DateTime ExpiresUtc)> OpenRetainedAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var refresh = credentials.Read(connectionId, out var fromLegacy);
        if (!string.IsNullOrWhiteSpace(refresh))
        {
            try
            {
                var account = await OpenVerifiedAsync(send, connectionId, refresh, ct);
                if (Connections.CreateConnectionId(account.AccountId) == connectionId) return account;
                if (!fromLegacy) throw new InvalidOperationException(Text.Get(TextKey.RootDropboxScopeChanged));
            }
            catch (DropboxCredentialUnavailableException) { /* Revoked access also needs sign-in. */ }
        }
        throw new ItemFailedException(string.Empty, TextKey.SignInReturnRetry, null);
    }

    private static void StartBrowser(Uri uri)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
