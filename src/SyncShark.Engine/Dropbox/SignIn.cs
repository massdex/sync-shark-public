using System.Diagnostics;
using System.Net;
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
    private const string AccountRoute = "users/get_current_account";
    private const int MaximumJsonBytes = 1024 * 1024;
    private const int MaximumCallbackBytes = 16 * 1024;
    private const int MaximumCallbackLineBytes = 4096;
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
        ConnectAccountAsync(DropboxApi api, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api, nameof(api));
        return RunAuthorizationAttemptAsync(ct, AuthorizationBound, async attempt =>
        {
            var redirect = RedirectUri;
            var (verifier, challenge, state) = CreatePkceFlow();
            var code = await ReceiveCallbackAsync(redirect, redirect.AbsolutePath, state, attempt,
                () => StartBrowser(BuildAuthorizationUri(ClientId, redirect, challenge, state)));
            var token = await TokenAsync(api.SendAsync, new Dictionary<string, string>
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
        if (!await AuthorizationGate.WaitAsync(0, ct)) throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry));
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

    /// <summary>The authorization code of a verified loopback return; a wrong path, state, error return or absent code refuses.</summary>
    public static string ParseCallbackTarget(string target, string expectedPath, string expectedState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target, nameof(target));
        var uri = new Uri(RedirectUri.GetLeftPart(UriPartial.Authority) + target);
        if (!uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal))
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        var query = ParseQuery(uri.Query);
        if (query.ContainsKey("error")) throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry));
        if (!query.TryGetValue("state", out var state) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        return code;
    }

    /// <summary>The fixed receipt page: receipt when the return verified, retry when it did not; no callback values.</summary>
    public static string AcknowledgementPage(bool received) =>
        "<!doctype html><html lang=en><head><meta charset=utf-8>" +
        "<meta name=viewport content=\"width=device-width,initial-scale=1\"><title>Sync Shark</title></head><body>" +
        "<main><p>" + WebUtility.HtmlEncode(Text.Get(received ? TextKey.SignInReturnReceived : TextKey.SignInReturnRetry)) +
        "</p></main></body></html>";

    /// <summary>The grant's scopes must be exactly the registered envelope (order-insensitive) before any credential is saved.</summary>
    public static bool GrantAccepted(IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted, nameof(granted));
        return granted.Where(scope => !string.IsNullOrWhiteSpace(scope)).ToHashSet(StringComparer.Ordinal)
            .SetEquals(RequiredScopes);
    }

    /// <summary>Receives one loopback return on the fixed callback: starts listening, optionally opens the browser,
    /// verifies the return and serves the receipt page. A malformed or incomplete request serves the retry page when the
    /// socket is still writable. The supplied token bounds the whole attempt.</summary>
    public static async Task<string> ReceiveCallbackAsync(Uri redirectUri, string expectedPath, string expectedState,
        CancellationToken ct, Action? browser = null)
    {
        ArgumentNullException.ThrowIfNull(redirectUri, nameof(redirectUri));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath, nameof(expectedPath));
        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        try { listener.Start(1); }
        catch (SocketException) { throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry)); }
        try
        {
            using var registration = ct.Register(listener.Stop);
            browser?.Invoke();
            var client = await AcceptAsync(listener, ct);
            try
            {
                var stream = client.GetStream();
                try
                {
                    string target;
                    try { target = await ReadCallbackTargetAsync(stream, ct); }
                    catch
                    {
                        await TryWriteRetryAsync(stream, ct);
                        throw;
                    }
                    string code;
                    try { code = ParseCallbackTarget(target, expectedPath, expectedState); }
                    catch
                    {
                        await TryWriteRetryAsync(stream, ct);
                        throw;
                    }
                    await WritePageAsync(stream, AcknowledgementPage(received: true), ct);
                    return code;
                }
                finally { await stream.DisposeAsync(); }
            }
            finally { client.Dispose(); }
        }
        finally { listener.Stop(); }
    }

    /// <summary>Opens a retained connection by id and returns the account namespaces it actually holds; a credential that
    /// does not derive the requested connection id is refused. A missing credential throws <see cref="DropboxCredentialUnavailableException"/>.</summary>
    public static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> VerifyAccountAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials, nameof(credentials));
        _ = CredentialStore.AccountTarget(connectionId);
        var refresh = credentials.Read(connectionId);
        if (string.IsNullOrWhiteSpace(refresh)) throw new DropboxCredentialUnavailableException(connectionId);
        var account = await OpenIdentityAsync(send, connectionId, refresh, ct);
        RequireOwnedConnection(connectionId, account.AccountId);
        return account;
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
        var token = await TokenAsync(send, new Dictionary<string, string>
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

    /// <summary>Posts a token form through the supplied sender and returns the parsed token facts and granted scopes.</summary>
    internal static async Task<(string AccessToken, string? RefreshToken, DateTime ExpiresUtc, string? AccountId,
            IReadOnlyList<string> Scopes)> TokenAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        IReadOnlyDictionary<string, string> form, string? connectionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(DropboxProtocol.TokenPath))
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await send(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (connectionId is not null && await IsInvalidGrantAsync(response, ct))
                throw new DropboxCredentialUnavailableException(connectionId);
            throw new HttpRequestException(Text.Get(TextKey.SignInReturnRetry), null, response.StatusCode);
        }
        using var document = await ReadJsonAsync(response, ct);
        var root = document.RootElement;
        try
        {
            var accessToken = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 14400;
            var accountId = root.TryGetProperty("account_id", out var account) ? account.GetString() : null;
            var scopes = root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String
                ? (scope.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            return (accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn), accountId, scopes);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        }
    }

    /// <summary>Reads the opened account's identity; incomplete identity information refuses.</summary>
    internal static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> AccountAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(AccountRoute))
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);
        using var response = await send(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Text.Get(TextKey.SignInReturnRetry), null, response.StatusCode);
        using var document = await ReadJsonAsync(response, ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("root_info", out var rootInfo) || rootInfo.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        return (RequiredIdentity(root, "account_id"), RequiredIdentity(rootInfo, "root_namespace_id"),
            RequiredIdentity(rootInfo, "home_namespace_id"));
    }

    /// <summary>The one acceptance rule for an opened account: its identity must derive the requested connection id.</summary>
    internal static void RequireOwnedConnection(string connectionId, string accountId)
    {
        if (Connections.CreateConnectionId(accountId) != connectionId)
            throw new InvalidOperationException(Text.Get(TextKey.RootDropboxScopeChanged));
    }

    private static string RequiredIdentity(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null) throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaximumJsonBytes)
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumJsonBytes)
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        try
        {
            var document = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        }
        catch (JsonException) { throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid)); }
    }

    private static async Task<bool> IsInvalidGrantAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await ReadJsonAsync(response, ct);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String && error.GetString() is "invalid_grant";
        }
        catch (InvalidDataException) { return false; }
    }

    private static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try { return await listener.AcceptTcpClientAsync(ct); }
        catch (SocketException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private static async Task<string> ReadCallbackTargetAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaximumCallbackBytes];
        var length = 0;
        var lineLength = 0;
        var complete = false;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0) break;
            length += read;
            if (lineLength == 0 && length >= 2 && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                lineLength = length - 2;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                complete = true;
                break;
            }
        }
        if (!complete || lineLength == 0 || lineLength > MaximumCallbackLineBytes)
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        var parts = Encoding.ASCII.GetString(buffer, 0, lineLength).Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || parts[2] != "HTTP/1.1")
            throw new InvalidDataException(Text.Get(TextKey.SignInReturnRetry));
        return parts[1];
    }

    // Best effort for a return that cannot be verified, bounded by the same attempt token; a closed socket or a
    // cancelled attempt simply ends it without waiting.
    private static async Task TryWriteRetryAsync(Stream stream, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try { await WritePageAsync(stream, AcknowledgementPage(received: false), ct); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task WritePageAsync(Stream stream, string html, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
            "Connection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static void StartBrowser(Uri uri)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0].Replace('+', ' '))] =
                pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : string.Empty;
        }
        return result;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

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
            var refresh = _credentials.Read(_connectionId);
            if (string.IsNullOrWhiteSpace(refresh)) throw new DropboxCredentialUnavailableException(_connectionId);
            var account = await SignIn.OpenVerifiedAsync(_send, _connectionId, refresh, ct);
            SignIn.RequireOwnedConnection(_connectionId, account.AccountId);
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
