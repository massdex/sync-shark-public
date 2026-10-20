using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

public sealed record GoogleDrivePkceFlow(
    [property: JsonIgnore] string Verifier, string Challenge,
    [property: JsonIgnore] string State)
{
    public override string ToString() => "Google Drive authorization flow (private values redacted)";
}

// Uses Google's installed-app PKCE protocol. This adapter neither reads nor creates credentials.
// The activation owner supplies the exact registered client from protected local custody.
public sealed class GoogleDriveOAuthEndpoint : IGoogleDriveTokenEndpoint, IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string? _clientSecret;
    private readonly bool _ownsHttp;

    public GoogleDriveOAuthEndpoint(string clientId, string? clientSecret = null)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) }, clientId, clientSecret, true) { }

    internal GoogleDriveOAuthEndpoint(HttpClient http, string clientId, string? clientSecret = null,
        bool ownsHttp = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _ownsHttp = ownsHttp;
    }

    public static GoogleDrivePkceFlow CreateFlow()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return new(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            Base64Url(RandomNumberGenerator.GetBytes(32)));
    }

    public Uri BuildAuthorizationUri(Uri redirectUri, GoogleDrivePkceFlow flow)
    {
        RequireClient(_clientId);
        RequireRedirect(redirectUri);
        if (string.IsNullOrWhiteSpace(flow.State) || flow.State.Length < 32 ||
            flow.Challenge != Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier))))
            throw new ArgumentException("A fresh, bound PKCE flow is required.", nameof(flow));
        RequireVerifier(flow.Verifier);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _clientId, ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = GoogleDriveProviderConfiguration.DriveScope,
            ["code_challenge"] = flow.Challenge, ["code_challenge_method"] = "S256",
            ["state"] = flow.State, ["access_type"] = "offline", ["prompt"] = "consent select_account"
        };
        return new Uri("https://accounts.google.com/o/oauth2/v2/auth?" +
            string.Join('&', query.Select(item => Uri.EscapeDataString(item.Key) + "=" +
                Uri.EscapeDataString(item.Value))));
    }

    public Task<GoogleDriveTokenResult> ExchangeCodeAsync(Uri redirectUri, string code,
        string verifier, CancellationToken cancellationToken = default)
    {
        RequireRedirect(redirectUri);
        RequireVerifier(verifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return RequestAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri.AbsoluteUri
        }, true, cancellationToken);
    }

    public Task<GoogleDriveTokenResult> RefreshAsync(string clientId, string refreshToken,
        CancellationToken cancellationToken = default)
    {
        RequireClient(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return RequestAsync(new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
        }, false, cancellationToken);
    }

    // Bootstrap identity before creating a connection or persisting any grant. The stable permission
    // id, not a display name/email selected in the browser, binds subsequent reconnects and pairs.
    public async Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        RequireClient(_clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about?fields=user(permissionId,emailAddress,displayName),storageQuota(limit,usage,usageInDrive)");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException)
        { throw new IOException("Google Drive account verification could not reach the service. Try again."); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google Drive account verification returned HTTP {(int)response.StatusCode}.");
            using var document = await ReadResponseAsync(response, cancellationToken);
            try
            {
                var root = document.RootElement;
                var user = root.GetProperty("user");
                var permissionId = Text(user, "permissionId");
                if (string.IsNullOrWhiteSpace(permissionId))
                    throw new InvalidDataException("Google Drive did not supply a stable account identity.");
                return new(permissionId, Text(user, "emailAddress") ?? "", Text(user, "displayName") ?? "",
                    root.TryGetProperty("storageQuota", out var quota) && quota.ValueKind == JsonValueKind.Object
                        ? quota.GetRawText() : "");
            }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
            { throw new InvalidDataException("Google Drive returned an invalid account identity."); }
        }
    }

    private void RequireClient(string clientId)
    {
        if (!GoogleDriveProviderConfiguration.IsLiveProviderBound || clientId != _clientId ||
            _clientId != GoogleDriveProviderConfiguration.RegisteredClientId)
            throw new InvalidOperationException("The registered Sync Shark Google client is required.");
    }

    private async Task<GoogleDriveTokenResult> RequestAsync(Dictionary<string, string> form,
        bool requireRefresh, CancellationToken cancellationToken)
    {
        RequireClient(_clientId);
        form["client_id"] = _clientId;
        if (!string.IsNullOrWhiteSpace(_clientSecret)) form["client_secret"] = _clientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            { Content = new FormUrlEncodedContent(form) };
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException)
        {
            // Never include transport diagnostics that might echo a request body or secret.
            throw new IOException("Google authorization could not reach the token service. Try again.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode == HttpStatusCode.BadRequest
                    ? "Google authorization expired or was refused. Reconnect this account."
                    : $"Google authorization returned HTTP {(int)response.StatusCode}. Try again.");
            using var document = await ReadResponseAsync(response, cancellationToken);
            try
            {
                var root = document.RootElement;
                var access = Text(root, "access_token");
                var refresh = Text(root, "refresh_token");
                var scopes = (Text(root, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (string.IsNullOrWhiteSpace(access) || Text(root, "token_type") != "Bearer" ||
                    !root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) ||
                    seconds <= 5 || seconds > 86400 ||
                    !GoogleDriveProviderConfiguration.HasRequiredGrant(scopes) ||
                    (requireRefresh && string.IsNullOrWhiteSpace(refresh)))
                    throw new InvalidDataException("Google returned an incomplete or unsupported authorization grant.");
                return new(access, DateTime.UtcNow.AddSeconds(seconds), scopes, refresh);
            }
            catch (InvalidOperationException)
            { throw new InvalidDataException("Google returned an invalid authorization response."); }
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Google authorization response exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaximumResponseBytes + 1];
        try
        {
            var used = 0;
            while (used < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken);
                if (count == 0) break;
                used += count;
            }
            if (used > MaximumResponseBytes)
                throw new InvalidDataException("Google authorization response exceeded its size limit.");
            // JsonDocument owns a copy: the temporary plaintext buffer is wiped before return.
            try { return JsonDocument.Parse(buffer.AsSpan(0, used).ToArray(), new() { MaxDepth = 16 }); }
            catch (JsonException)
            { throw new InvalidDataException("Google returned an invalid authorization response."); }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static void RequireRedirect(Uri redirect)
    {
        if (!redirect.IsAbsoluteUri || redirect.Scheme != "http" || redirect.Host != "127.0.0.1" ||
            redirect.IsDefaultPort || redirect.Port <= 0 || redirect.Query.Length != 0 ||
            redirect.Fragment.Length != 0 || redirect.UserInfo.Length != 0)
            throw new ArgumentException("A plain IPv4 loopback redirect with an explicit port is required.");
    }

    private static void RequireVerifier(string verifier)
    {
        if (verifier.Length is < 43 or > 128 ||
            verifier.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')))
            throw new ArgumentException("A valid PKCE verifier is required.");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
