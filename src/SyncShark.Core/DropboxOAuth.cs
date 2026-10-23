using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record DropboxPkceFlow(string Verifier, string Challenge, string State);
public sealed record DropboxTokenResult(string AccessToken, string? RefreshToken, DateTime ExpiresUtc, string? AccountId, IReadOnlyList<string> Scopes);
public sealed record DropboxAccountIdentity(
    string AccountId,
    string RootNamespaceId,
    string HomeNamespaceId,
    string DisplayName,
    string Email,
    string AccountType);

public static class DropboxOAuth
{
    private const int MaximumOAuthJsonBytes = 1024 * 1024;

    public static DropboxPkceFlow CreatePkceFlow()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new DropboxPkceFlow(verifier, challenge, Base64Url(RandomNumberGenerator.GetBytes(32)));
    }

    public static Uri BuildAuthorizationUri(string clientId, Uri redirectUri, DropboxPkceFlow flow,
        bool includeFileRequests = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_challenge"] = flow.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = flow.State,
            ["token_access_type"] = "offline",
            ["scope"] = string.Join(' ', DropboxProviderConfiguration.RequestedScopes(includeFileRequests))
        };
        return new Uri("https://www.dropbox.com/oauth2/authorize?" + string.Join('&', query.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value))));
    }

    public static Task<DropboxTokenResult> ExchangeCodeAsync(HttpClient client, string clientId, Uri redirectUri, string code, string verifier, CancellationToken cancellationToken,
        bool includeFileRequests = false) =>
        TokenRequestAsync(client, new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        }, requireRefresh: true, requireScopes: true, cancellationToken, DropboxProviderConfiguration.RequestedScopes(includeFileRequests));

    public static Task<DropboxTokenResult> RefreshAsync(HttpClient client, string clientId, string refreshToken, CancellationToken cancellationToken,
        IReadOnlyList<string>? scopes = null) =>
        TokenRequestAsync(client, new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["scope"] = string.Join(' ', scopes ?? DropboxProviderConfiguration.RequiredScopes)
        }, requireRefresh: false, requireScopes: false, cancellationToken);

    public static async Task<DropboxAccountIdentity> GetCurrentAccountAsync(HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new DropboxApiException(response.StatusCode, $"Dropbox account verification returned HTTP {(int)response.StatusCode}.");
        using var document = await ReadBoundedJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var rootInfo = root.GetProperty("root_info");
        var displayName = root.TryGetProperty("name", out var name) &&
                          name.TryGetProperty("display_name", out var display)
            ? display.GetString() ?? string.Empty
            : string.Empty;
        var email = root.TryGetProperty("email", out var emailValue)
            ? emailValue.GetString() ?? string.Empty
            : string.Empty;
        var accountType = root.TryGetProperty("account_type", out var type) &&
                          type.TryGetProperty(".tag", out var tag)
            ? tag.GetString() ?? string.Empty
            : string.Empty;
        return new DropboxAccountIdentity(
            root.GetProperty("account_id").GetString() ?? throw new InvalidDataException("Dropbox account ID is absent."),
            rootInfo.GetProperty("root_namespace_id").GetString() ?? throw new InvalidDataException("Dropbox root namespace is absent."),
            rootInfo.GetProperty("home_namespace_id").GetString() ?? throw new InvalidDataException("Dropbox home namespace is absent."),
            displayName,
            email,
            accountType);
    }

    private static async Task<DropboxTokenResult> TokenRequestAsync(
        HttpClient client,
        IReadOnlyDictionary<string, string> form,
        bool requireRefresh,
        bool requireScopes,
        CancellationToken cancellationToken, IReadOnlyList<string>? expectedScopes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/oauth2/token") { Content = new FormUrlEncodedContent(form) };
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new DropboxApiException(response.StatusCode,
                $"Dropbox token exchange returned HTTP {(int)response.StatusCode}.")
            {
                // A revoked refresh token answers 400 invalid_grant; the typed code, never the prose, drives the
                // account-access decision (continuous-sync.md section 5). The refusal has already arrived, so the
                // classification is best-effort and never replaces this exception (v1 threw it immediately).
                OAuthError = await ClassifyOAuthRefusalAsync(response)
            };
        using var document = await ReadBoundedJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var refresh = root.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() : null;
        if (requireRefresh && string.IsNullOrWhiteSpace(refresh)) throw new InvalidDataException("Dropbox did not return an offline refresh token.");
        var scopes = root.TryGetProperty("scope", out var scopeValue)
            ? (scopeValue.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (requireScopes && !scopes.Order(StringComparer.Ordinal).SequenceEqual((expectedScopes ?? DropboxProviderConfiguration.RequiredScopes).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Dropbox granted scopes differ from the registered product permission envelope.");
        var expires = root.TryGetProperty("expires_in", out var expiresValue) ? expiresValue.GetInt32() : 14400;
        return new DropboxTokenResult(
            root.GetProperty("access_token").GetString() ?? throw new InvalidDataException("Dropbox access token is absent."),
            refresh,
            DateTime.UtcNow.AddSeconds(expires),
            root.TryGetProperty("account_id", out var account) ? account.GetString() : null,
            scopes);
    }

    // Reads only the refusal's typed OAuth error; nothing else from the body is kept, logged or surfaced. The body is
    // already buffered (SendAsync reads content by default), so it is read under its own short bound rather than the
    // caller's token: a cancellation or a body fault here cannot turn the HTTP refusal into a different exception.
    private static readonly TimeSpan OAuthRefusalReadBound = TimeSpan.FromSeconds(2);

    private static async Task<DropboxOAuthError> ClassifyOAuthRefusalAsync(HttpResponseMessage response)
    {
        try
        {
            using var bound = new CancellationTokenSource(OAuthRefusalReadBound);
            using var document = await ReadBoundedJsonAsync(response, bound.Token);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString() switch
                {
                    "invalid_grant" => DropboxOAuthError.InvalidGrant,
                    _ => DropboxOAuthError.Other
                }
                : DropboxOAuthError.Other;
        }
        catch (Exception)
        {
            // Not swallowed: the caller still throws the HTTP refusal; an unreadable body only means "not invalid_grant".
            return DropboxOAuthError.Other;
        }
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<JsonDocument> ReadBoundedJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const string oversized = "Dropbox returned an oversized OAuth or account response.";
        SyncResponseBody.RequireWithinSize(response, MaximumOAuthJsonBytes, oversized);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await SyncResponseBody.ReadJsonAsync(input, MaximumOAuthJsonBytes,
            SyncResponseBody.DefaultIdleTimeout, oversized, cancellationToken, maximumDepth: 32);
    }

    public static async Task<DropboxSpaceUsage> GetSpaceUsageAsync(HttpClient client, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_space_usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new DropboxApiException(response.StatusCode, $"Dropbox storage check returned HTTP {(int)response.StatusCode}.");
        using var document = await ReadBoundedJsonAsync(response, cancellationToken);
        var root = document.RootElement; var allocation = root.GetProperty("allocation");
        var team = allocation.GetProperty(".tag").GetString() == "team";
        var used = team ? allocation.GetProperty("used").GetInt64() : root.GetProperty("used").GetInt64();
        var total = allocation.GetProperty("allocated").GetInt64();
        if (used < 0 || total < 0) throw new InvalidDataException("Dropbox returned invalid storage counts.");
        return new(used, total, team, DateTimeOffset.UtcNow);
    }
}

public sealed class DropboxLoopbackSession(string code)
{
    public string Code { get; } = code;
}

public interface IDropboxAuthorizationReceiver
{
    Task<DropboxLoopbackSession> ReceiveAsync(Uri redirectUri, string expectedState,
        CancellationToken cancellationToken);
}

public sealed class DropboxLoopbackReceiver(Action? returnToApp = null) : IDropboxAuthorizationReceiver
{
    private static readonly Lazy<string> BrandMark = new(() =>
    {
        using var stream = typeof(DropboxLoopbackReceiver).Assembly.GetManifestResourceStream("SyncShark.BrandMark.svg")
            ?? throw new InvalidOperationException("The canonical product mark is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    public async Task<DropboxLoopbackSession> ReceiveAsync(Uri redirectUri, string expectedState, CancellationToken cancellationToken)
    {
        ValidateRedirect(redirectUri);
        OAuthReturnBridge.StopPrevious(redirectUri.Port);
        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        listener.Start(1);
        var handedOff = false;
        try
        {
            using var registration = cancellationToken.Register(listener.Stop);
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            try
            {
                var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, cancellationToken);
                string code;
                try { code = ParseTarget(target, redirectUri.AbsolutePath, expectedState); }
                catch (InvalidOperationException)
                {
                    await RespondAsync(stream, "Connection not approved", "This connection was cancelled or not approved. Return to the app to try again.");
                    throw;
                }
                catch (InvalidDataException)
                {
                    await RespondAsync(stream, "Connection could not be completed", "This response could not be verified. Return to the app to start a new connection.");
                    throw;
                }
                await RespondAsync(stream, "Response received", "Sync Shark is checking this connection. The verified result will appear in the app.");
                await stream.DisposeAsync();
                client.Dispose();
                return new DropboxLoopbackSession(code);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        finally { if (!handedOff) listener.Stop(); }

        async Task RespondAsync(NetworkStream stream, string heading, string description)
        {
            // The independent nonce grants only one foreground request, never credentials or provider effects.
            var nonce = returnToApp is null ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await WriteAcknowledgementAsync(stream, ReturnPage(heading, description, nonce), cancellationToken);
            if (nonce is not null)
            {
                OAuthReturnBridge.Start(listener, redirectUri, nonce, returnToApp!);
                handedOff = true;
            }
        }
    }

    internal static string AcknowledgementHtml => ReturnPage("Response received",
        "Sync Shark is checking this connection. You can close this page; the result will appear in the app.");
    internal static string NotApprovedHtml => ReturnPage("Connection not approved",
        "This connection was cancelled or not approved. Return to the app to try again. You can close this page.");
    internal static string ErrorHtml => ReturnPage("Connection could not be completed",
        "This response could not be verified. Return to the app to start a new connection. You can close this page.");

    // V7/F16: the one fixed, non-secret acknowledgement for a bounded but unverifiable return request. The
    // same copy answers every rejection, so it never states which check failed, carries no nonce, offers no
    // activation control and cannot be used to complete a return.
    internal static string ReturnRejectedHtml => ReturnPage("Return to Sync Shark was not completed",
        "This return request could not be verified, so the app was not asked to open. Start the connection again from Sync Shark. You can close this page.");

    // V7/F16: the exact fragment ReturnPage emits only when the return form is present.
    internal const string ReturnFormMarker = "action=/return";

    // Only fixed product copy reaches this page. Never render the callback target, provider error,
    // code, state, account identity or exception; verified success belongs to the app after binding.
    internal static string ReturnPage(string heading, string description, string? returnNonce = null)
    {
        var action = returnNonce is null ? "<p class=return>Return to Sync Shark to continue.</p>" :
            "<form method=post action=/return><input type=hidden name=nonce value=\"" +
            WebUtility.HtmlEncode(returnNonce) + "\"><button type=submit>Return to Sync Shark <span aria-hidden=true>↗</span></button></form>";
        return $$$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sync Shark</title>
            <style>html{color-scheme:dark}*{box-sizing:border-box}body{margin:0;padding:24px;background:#000;color:#f7f5f2;font:16px "Segoe UI",Arial,sans-serif;display:grid;min-height:100vh;place-items:center}.card{width:100%;max-width:38rem;padding:40px 42px 32px;border:1px solid #303236;border-radius:20px;background:#101113;text-align:center}.mark{display:block;width:96px;height:96px;margin:0 auto 12px}.brand{margin:0 0 32px;font-size:25px;font-weight:600;letter-spacing:-.4px;color:#f7f5f2}h1{margin:0 0 16px;font-size:28px;line-height:1.25;overflow-wrap:anywhere}p{margin:0;color:#bfc3cb;font-size:16px;line-height:1.6}.return,form{margin:26px 0 0}button{background:#0061fe;border:1px solid #438bff;border-radius:8px;padding:12px 20px;color:white;font:600 16px "Segoe UI",Arial,sans-serif;cursor:pointer}button:hover{background:#267bff}button:focus-visible{outline:3px solid #b4c8e1;outline-offset:4px}button span{margin-left:10px}.foot{margin-top:24px;font-size:14px;color:#939da9}@media(max-width:420px){body{padding:16px}.card{padding:28px 22px}.brand{margin-bottom:26px}.mark{width:80px;height:80px}h1{font-size:25px}}</style></head>
            <body><main class="card"><div class="mark">{{{BrandMark.Value}}}</div><p class="brand">Sync Shark</p><h1>{{{WebUtility.HtmlEncode(heading)}}}</h1><p>{{{WebUtility.HtmlEncode(description)}}}</p>{{{action}}}<p class="foot">You can close this page.</p></main></body></html>
            """;
    }

    internal static async Task WriteAcknowledgementAsync(NetworkStream stream, string html, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        // V7/F16: a page that carries the Return to Sync Shark form must not be served with no-referrer.
        // Under the Fetch/HTML Origin serialization a request whose referrer policy is no-referrer sends
        // Origin: null, so the browser's real same-origin form POST could never satisfy the bridge's exact
        // Origin check. Origin-only keeps that check exact and still never sends the callback path or query.
        var referrerPolicy = html.Contains(ReturnFormMarker, StringComparison.Ordinal) ? "origin" : "no-referrer";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: {referrerPolicy}\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Consume the bounded GET headers before closing the accepted socket. Leaving the
        // browser's headers unread can turn Dispose into a TCP reset and truncate our page.
        var buffer = new byte[16 * 1024];
        var length = 0;
        var lineLength = 0;
        var headersComplete = false;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (read == 0) break;
            length += read;
            if (lineLength == 0 && length >= 2 && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                lineLength = length - 2;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            { headersComplete = true; break; }
        }
        if (!headersComplete || lineLength == 0 || lineLength > 4096)
            throw new InvalidDataException("OAuth callback headers are incomplete or too large.");
        var line = Encoding.ASCII.GetString(buffer, 0, lineLength);
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || parts[2] != "HTTP/1.1") throw new InvalidDataException("OAuth callback method is invalid.");
        return parts[1];
    }

    internal static string ParseTarget(string target, string expectedPath, string expectedState)
    {
        var uri = new Uri("http://127.0.0.1" + target);
        if (!uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal)) throw new InvalidDataException("OAuth callback path differs from the registered path.");
        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("error", out _)) throw new InvalidOperationException("Dropbox authorization was not granted.");
        if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new InvalidDataException("OAuth callback state is invalid.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("OAuth callback code is absent.");
        return code;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0].Replace('+', ' '))] = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : string.Empty;
        }
        return result;
    }

    private static void ValidateRedirect(Uri redirectUri)
    {
        if (redirectUri.Scheme != Uri.UriSchemeHttp || redirectUri.Host != "127.0.0.1" || redirectUri.Port <= 0 || redirectUri.AbsolutePath == "/")
            throw new ArgumentException("A fixed IPv4 loopback redirect with a non-root path is required.", nameof(redirectUri));
    }
}

public sealed class DropboxConnectionCoordinator(
    HttpClient httpClient,
    IDropboxAuthorizationReceiver receiver,
    IDropboxRefreshTokenStore credentialStore,
    DropboxConnectionStore connectionStore,
    TimeSpan? authorizationTimeout = null)
{
    // Every coordinator uses the same registered callback port. Do not queue an invisible
    // second browser flow behind an abandoned first flow, including across host instances.
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);

    public async Task<DropboxConnectionBinding> ConnectAsync(Func<Uri, Task> openAuthorizationPage,
        bool reauthorize = false, CancellationToken cancellationToken = default,
        string? connectionId = null, bool addAccount = false, bool includeFileRequests = false)
    {
        if (!DropboxProviderConfiguration.IsLiveProviderBound) throw new InvalidOperationException("The registered Dropbox app key is not bound in this build.");
        if (authorizationTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30)))
            throw new ArgumentOutOfRangeException(nameof(authorizationTimeout));
        if (!await AuthorizationGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A Dropbox connection is already waiting for authorization. Cancel it or finish it before trying again.");
        var callerCancellation = cancellationToken;
        DropboxLoopbackSession? session = null;
        Task<DropboxLoopbackSession>? receiveTask = null;
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveCancellation.CancelAfter(authorizationTimeout ?? TimeSpan.FromMinutes(5));
        cancellationToken = receiveCancellation.Token;
        var wroteCredential = false;
        var wroteBinding = false;
        string? existingCredential = null;
        DropboxConnectionBinding? existingBinding = null;
        IDropboxRefreshTokenStore selectedCredentialStore = credentialStore;
        string? newConnectionId = null;
        var hadPrimary = connectionStore.Load() is not null;
        try
        {
            existingBinding = addAccount ? null : connectionStore.Load(connectionId);
            includeFileRequests |= DropboxProviderConfiguration.HasFileRequestAccess(existingBinding?.Scopes);
            if (!addAccount && connectionId is not null && existingBinding is null)
                throw new InvalidOperationException("The selected Dropbox connection no longer exists.");
            if (existingBinding is not null) selectedCredentialStore = credentialStore.ForConnection(existingBinding);
            existingCredential = addAccount ? null : await selectedCredentialStore.ReadAsync(cancellationToken);
            if (!reauthorize && existingBinding is not null && !string.IsNullOrWhiteSpace(existingCredential))
                return existingBinding;
            if (!addAccount && !reauthorize && existingBinding is null &&
                (connectionStore.Exists || !string.IsNullOrWhiteSpace(existingCredential)))
            {
                await credentialStore.DeleteAsync(cancellationToken);
                await connectionStore.DeleteAsync(cancellationToken);
            }

            var redirect = new Uri(DropboxProviderConfiguration.RegisteredRedirectUri);
            var flow = DropboxOAuth.CreatePkceFlow();
            receiveTask = receiver.ReceiveAsync(redirect, flow.State, receiveCancellation.Token);
            // Bind failure must be reported before opening a browser that cannot call back.
            if (receiveTask.IsFaulted || receiveTask.IsCanceled) await receiveTask;
            await openAuthorizationPage(DropboxOAuth.BuildAuthorizationUri(DropboxProviderConfiguration.RegisteredClientId, redirect, flow, includeFileRequests)).WaitAsync(cancellationToken);
            session = await receiveTask;
            var token = await DropboxOAuth.ExchangeCodeAsync(httpClient, DropboxProviderConfiguration.RegisteredClientId, redirect, session.Code, flow.Verifier, cancellationToken, includeFileRequests);
            var account = await DropboxOAuth.GetCurrentAccountAsync(httpClient, token.AccessToken, cancellationToken);
            if (token.AccountId is not null && !token.AccountId.Equals(account.AccountId, StringComparison.Ordinal))
                throw new InvalidDataException("Dropbox token and account identities differ.");
            if (addAccount && connectionStore.LoadActiveBindings().Any(binding => binding.AccountId == account.AccountId))
                throw new InvalidOperationException("This Dropbox account is already connected. Its existing folders and credentials were left unchanged.");
            if (existingBinding is not null &&
                (!string.Equals(existingBinding.AccountId, account.AccountId, StringComparison.Ordinal) ||
                 !string.Equals(existingBinding.RootNamespaceId, account.RootNamespaceId,
                     StringComparison.Ordinal) ||
                 !string.Equals(existingBinding.HomeNamespaceId, account.HomeNamespaceId,
                     StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "This authorization belongs to a different Dropbox account or namespace. Existing pairs remain bound to their retained connection and were not reassigned.");
            var now = DateTimeOffset.UtcNow;
            var binding = existingBinding is null
                ? new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                    account.AccountId, account.RootNamespaceId, account.HomeNamespaceId,
                    DropboxProviderConfiguration.ContentAccess,
                    token.Scopes.Order(StringComparer.Ordinal).ToArray(),
                    DropboxConnectionBinding.CreateConnectionId(account.AccountId), account.DisplayName,
                    account.Email, account.AccountType, now, now,
                    hadPrimary ? WindowsDropboxRefreshTokenStore.AccountTarget(DropboxConnectionBinding.CreateConnectionId(account.AccountId))
                        : WindowsDropboxRefreshTokenStore.RegisteredTarget)
                : existingBinding with
                {
                    ClientId = DropboxProviderConfiguration.RegisteredClientId,
                    AccountId = account.AccountId,
                    RootNamespaceId = account.RootNamespaceId,
                    HomeNamespaceId = account.HomeNamespaceId,
                    ContentAccess = DropboxProviderConfiguration.ContentAccess,
                    Scopes = token.Scopes.Order(StringComparer.Ordinal).ToArray(),
                    DisplayName = account.DisplayName,
                    Email = account.Email,
                    AccountType = account.AccountType,
                    ConnectedUtc = existingBinding.ConnectedUtc ?? now,
                    LastVerifiedUtc = now,
                    CredentialReference = existingBinding.CredentialReference
                };
            DropboxProviderConfiguration.ValidateConnection(binding);
            newConnectionId = binding.ConnectionId;
            selectedCredentialStore = credentialStore.ForConnection(binding);
            existingCredential = await selectedCredentialStore.ReadAsync(cancellationToken);
            if (addAccount && existingCredential is not null)
                throw new InvalidOperationException("Protected credentials already exist for this account without a matching active connection. Nothing was overwritten.");
            wroteCredential = true;
            await selectedCredentialStore.WriteAsync(token.RefreshToken!, cancellationToken);
            wroteBinding = true;
            await connectionStore.SaveAsync(binding, cancellationToken);
            return binding;
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = null;
            if (wroteBinding)
            {
                try
                {
                    if (existingBinding is null && !hadPrimary) await connectionStore.DeleteAsync(CancellationToken.None);
                    else if (existingBinding is null && newConnectionId is not null)
                        await connectionStore.RemoveAdditionalAsync(newConnectionId, CancellationToken.None);
                    else if (existingBinding is not null) await connectionStore.SaveAsync(existingBinding, CancellationToken.None);
                }
                catch (Exception exception) { cleanupFailure = exception; }
            }
            if (wroteCredential)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(existingCredential))
                        await selectedCredentialStore.DeleteAsync(CancellationToken.None);
                    else
                        await selectedCredentialStore.WriteAsync(existingCredential, CancellationToken.None);
                }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
            if (cleanupFailure is not null)
                throw new InvalidOperationException("Dropbox connection failed and protected incomplete state could not be fully removed.", cleanupFailure);
            if (failure is OperationCanceledException && !callerCancellation.IsCancellationRequested && receiveCancellation.IsCancellationRequested)
                throw new TimeoutException("Dropbox authorization timed out. Existing accounts were kept. Choose Add Account or Reconnect to try again.");
            throw;
        }
        finally
        {
            receiveCancellation.Cancel();
            if (session is null && receiveTask is not null)
            {
                try { await receiveTask; } catch { /* Cancellation closes the bounded listener. */ }
            }
            AuthorizationGate.Release();
        }
    }
}

public sealed class RefreshingDropboxAccessTokenSource(
    HttpClient httpClient,
    IDropboxRefreshTokenStore credentialStore,
    DropboxConnectionBinding expectedConnection) : IDropboxAccessTokenSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDropboxRefreshTokenStore _accountCredentials = credentialStore.ForConnection(expectedConnection);
    private string? _accessToken;
    private DateTime _refreshAtUtc;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Read the token and its expiry as one protected cache entry. The gate
        // is released before provider file requests; it only single-flights refresh.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _refreshAtUtc) return _accessToken;
            DropboxProviderConfiguration.ValidateConnection(expectedConnection);
            var refresh = await _accountCredentials.ReadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("Dropbox is not connected.");
            DropboxTokenResult token;
            using (V2.RunOperations.Begin(V2.RunOperationKind.TokenRefresh, "Dropbox token"))
                token = await DropboxOAuth.RefreshAsync(httpClient, expectedConnection.ClientId, refresh, cancellationToken, expectedConnection.Scopes);
            if (token.AccountId is not null && !token.AccountId.Equals(expectedConnection.AccountId, StringComparison.Ordinal))
                throw new InvalidOperationException("Dropbox refresh account differs from the saved connection.");
            var account = await DropboxOAuth.GetCurrentAccountAsync(httpClient, token.AccessToken, cancellationToken);
            if (account.AccountId != expectedConnection.AccountId || account.RootNamespaceId != expectedConnection.RootNamespaceId ||
                account.HomeNamespaceId != expectedConnection.HomeNamespaceId)
                throw new InvalidOperationException("Dropbox authorization does not match the selected account and namespace; no file operation was issued.");
            _accessToken = token.AccessToken;
            _refreshAtUtc = token.ExpiresUtc.AddMinutes(-5);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }
}
