using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Full-account Dropbox sign-in: opens the browser, receives the loopback return, exchanges the code and reads
/// the account (all HTTP through <see cref="DropboxApi"/>), then saves the refresh token through
/// <see cref="CredentialStore"/> before returning the connection id. Browser pages carry the catalog receipt copy,
/// never a success claim, and never reflect callback values.</summary>
public sealed class SignIn
{
    /// <summary>The registered Dropbox application key.</summary>
    public const string ClientId = "fxzcfcfkv6f7gt6";

    private const int MaximumJsonBytes = 1024 * 1024;
    private const int MaximumCallbackBytes = 16 * 1024;
    private const int MaximumCallbackLineBytes = 4096;
    private const string BearerScheme = "Bearer";
    private const string OAuthScope = "offline";

    /// <summary>Every coordinator uses the same registered loopback callback.</summary>
    public static Uri RedirectUri { get; } =
        new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", 47821, "/oauth/dropbox/callback/").Uri;

    /// <summary>The full-account permission envelope the registered application requests.</summary>
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

    // One authorization flow at a time: never queue an invisible second browser flow behind an abandoned first one.
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);

    /// <summary>Receives the browser return on loopback; token exchange and account requests go through DropboxApi.
    /// Uses the catalog acknowledgement on receipt, not a success claim, and does not echo callback values. Saves the
    /// refresh token through CredentialStore before returning the connection id.</summary>
    public async Task<string> ConnectAsync(DropboxApi api, CancellationToken ct) =>
        (await ConnectAccountAsync(api, ct)).ConnectionId;

    /// <summary>The first-bind form of <see cref="ConnectAsync"/>: also returns the verified account and namespace ids,
    /// so a caller can build the pair's saved root identity before one exists. The refresh token is saved first.</summary>
    public async Task<(string ConnectionId, string AccountId, string RootNamespaceId, string HomeNamespaceId)>
        ConnectAccountAsync(DropboxApi api, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api, nameof(api));
        if (!await AuthorizationGate.WaitAsync(0, ct)) throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry));
        try
        {
            var redirect = RedirectUri;
            var (verifier, challenge, state) = CreatePkceFlow();
            var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
            try { listener.Start(1); }
            catch (SocketException) { throw new InvalidOperationException(Text.Get(TextKey.SignInReturnRetry)); }
            try
            {
                using var registration = ct.Register(listener.Stop);
                StartBrowser(BuildAuthorizationUri(ClientId, redirect, challenge, state));
                string code;
                var client = await AcceptAsync(listener, ct);
                try
                {
                    var stream = client.GetStream();
                    try
                    {
                        var target = await ReadCallbackTargetAsync(stream, ct);
                        try { code = ParseCallbackTarget(target, redirect.AbsolutePath, state); }
                        catch
                        {
                            await WritePageAsync(stream, AcknowledgementPage(received: false), ct);
                            throw;
                        }
                        await WritePageAsync(stream, AcknowledgementPage(received: true), ct);
                    }
                    finally { await stream.DisposeAsync(); }
                }
                finally { client.Dispose(); }

                var token = await TokenAsync(api.SendAsync, new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = ClientId,
                    ["redirect_uri"] = redirect.AbsoluteUri,
                    ["code_verifier"] = verifier
                }, connectionId: null, ct);
                if (string.IsNullOrWhiteSpace(token.RefreshToken))
                    throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                var account = await AccountAsync(api.SendAsync, token.AccessToken, ct);
                if (token.AccountId is not null && !string.Equals(token.AccountId, account.AccountId, StringComparison.Ordinal))
                    throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
                var connectionId = Connections.CreateConnectionId(account.AccountId);
                new CredentialStore().Save(connectionId, token.RefreshToken!);
                return (connectionId, account.AccountId, account.RootNamespaceId, account.HomeNamespaceId);
            }
            finally { listener.Stop(); }
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

    /// <summary>The authorization page URL for one flow, requesting offline (refresh-token) access.</summary>
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
        return new Uri(AuthorizationEndpoint().AbsoluteUri + "?" +
                       string.Join('&', query.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value))));
    }

    /// <summary>The authorization code of a verified loopback return; a wrong path, state, error return or absent code refuses.</summary>
    public static string ParseCallbackTarget(string target, string expectedPath, string expectedState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target, nameof(target));
        var uri = new Uri("http://127.0.0.1" + target);
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

    /// <summary>Posts a token form through the supplied sender and returns the parsed token facts.</summary>
    internal static async Task<(string AccessToken, string? RefreshToken, DateTime ExpiresUtc, string? AccountId)> TokenAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        IReadOnlyDictionary<string, string> form, string? connectionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint()) { Content = new FormUrlEncodedContent(form) };
        using var response = await send(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (connectionId is not null && await IsInvalidGrantAsync(response, ct))
                throw new DropboxCredentialUnavailableException(connectionId);
            throw new HttpRequestException(Text.Get(TextKey.SignInReturnRetry), null, response.StatusCode);
        }
        using var document = await ReadJsonAsync(response, ct);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 14400;
        var accountId = root.TryGetProperty("account_id", out var account) ? account.GetString() : null;
        return (accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn), accountId);
    }

    /// <summary>Reads the opened account's identity; incomplete identity information refuses.</summary>
    internal static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> AccountAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AccountEndpoint())
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

    /// <summary>Opens an already-retained connection by its id: refreshes from the protected token and returns the
    /// account namespaces it actually holds. A missing credential throws <see cref="DropboxCredentialUnavailableException"/>.</summary>
    public static async Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> VerifyAccountAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CredentialStore credentials, string connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send, nameof(send));
        ArgumentNullException.ThrowIfNull(credentials, nameof(credentials));
        _ = CredentialStore.AccountTarget(connectionId);
        var refresh = credentials.Read(connectionId);
        if (string.IsNullOrWhiteSpace(refresh)) throw new DropboxCredentialUnavailableException(connectionId);
        var token = await TokenAsync(send, new Dictionary<string, string>
        {
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["scope"] = string.Join(' ', RequiredScopes)
        }, connectionId, ct);
        var account = await AccountAsync(send, token.AccessToken, ct);
        if (token.AccountId is not null && !string.Equals(token.AccountId, account.AccountId, StringComparison.Ordinal))
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        return account;
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
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct); }
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
        catch (JsonException) { return false; }
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

    private static Uri AuthorizationEndpoint() => Endpoint("www.dropbox.com", "oauth2/authorize");
    private static Uri TokenEndpoint() => Endpoint("api.dropboxapi.com", "oauth2/token");
    private static Uri AccountEndpoint() => Endpoint("api.dropboxapi.com", "2/users/get_current_account");
    private static Uri Endpoint(string host, string path) => new UriBuilder(Uri.UriSchemeHttps, host, -1, path).Uri;

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
/// refresh that the authorization still belongs to the pair's saved account and namespaces. The composition root
/// passes its "send" as <see cref="DropboxApi.SendAsync"/> and gives the source to the API for authenticated requests.</summary>
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
            var token = await SignIn.TokenAsync(_send, new Dictionary<string, string>
            {
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
                ["client_id"] = SignIn.ClientId,
                ["scope"] = string.Join(' ', SignIn.RequiredScopes)
            }, _connectionId, ct);
            if (token.AccountId is not null && !string.Equals(token.AccountId, _expected.AccountId, StringComparison.Ordinal))
                throw new InvalidOperationException(Text.Get(TextKey.RootDropboxScopeChanged));
            var account = await SignIn.AccountAsync(_send, token.AccessToken, ct);
            if (!string.Equals(account.AccountId, _expected.AccountId, StringComparison.Ordinal) ||
                !string.Equals(account.RootNamespaceId, _expected.RootNamespaceId, StringComparison.Ordinal) ||
                !string.Equals(account.HomeNamespaceId, _expected.HomeNamespaceId, StringComparison.Ordinal))
                throw new InvalidOperationException(Text.Get(TextKey.RootDropboxScopeChanged));
            _accessToken = token.AccessToken;
            _refreshAtUtc = token.ExpiresUtc.AddMinutes(-5);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }
}
