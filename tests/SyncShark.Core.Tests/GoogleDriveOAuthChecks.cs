using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class GoogleDriveOAuthChecks
{
    private static void Require(bool value, string label)
    { if (!value) throw new InvalidOperationException("Google OAuth check failed: " + label); }

    internal static async Task RunAsync()
    {
        var flow = GoogleDriveOAuthEndpoint.CreateFlow();
        Require(flow.Verifier.Length is >= 43 and <= 128 && flow.State.Length >= 32, "PKCE entropy shape");
        Require(flow.Challenge == Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'), "real S256 derivation");
        Require(flow.State != GoogleDriveOAuthEndpoint.CreateFlow().State, "fresh state");
        Require(!flow.ToString().Contains(flow.Verifier) && !flow.ToString().Contains(flow.State) &&
            !JsonSerializer.Serialize(flow).Contains(flow.Verifier), "no private flow values in diagnostics");
        var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        using var endpoint = new GoogleDriveOAuthEndpoint(http, "test-client", "synthetic-client-secret");
        var redirect = new Uri("http://127.0.0.1:48671/callback/");
        var authorization = endpoint.BuildAuthorizationUri(redirect, flow);
        var query = ParseForm(authorization.Query.TrimStart('?'));
        Require(authorization.Host == "accounts.google.com" && authorization.Scheme == "https" &&
            query["scope"] == GoogleDriveProviderConfiguration.DriveScope &&
            query["code_challenge_method"] == "S256" && query["code_challenge"] == flow.Challenge &&
            query["state"] == flow.State && query["access_type"] == "offline" &&
            !query.ContainsKey("client_secret") && !query.ContainsKey("code_verifier"), "exact consent request");
        foreach (var bad in new[] { "https://example.invalid/callback/", "http://localhost:48671/callback/",
            "http://127.0.0.1:48671/callback/?secret=value", "http://127.0.0.1/callback/" })
            await RejectAsync(() => Task.FromResult(endpoint.BuildAuthorizationUri(new(bad), flow)));

        var grant = await endpoint.ExchangeCodeAsync(redirect, "synthetic-code", flow.Verifier);
        Require(handler.LastForm["grant_type"] == "authorization_code" &&
            handler.LastForm["code_verifier"] == flow.Verifier &&
            handler.LastForm["redirect_uri"] == redirect.AbsoluteUri &&
            handler.LastForm["client_secret"] == "synthetic-client-secret", "code exchange form");
        Require(grant.RefreshToken == "synthetic-refresh" && grant.AccessToken == "synthetic-access" &&
            grant.ExpiresUtc > DateTime.UtcNow.AddMinutes(50), "usable offline grant");
        Require(!grant.ToString().Contains("synthetic-") &&
            !JsonSerializer.Serialize(grant).Contains("synthetic-"), "token diagnostics redacted");
        var account = await endpoint.GetAccountAsync(grant.AccessToken);
        Require(account.PermissionId == "synthetic-permission" && account.Email == "test@example.invalid",
            "bootstrap stable account identity before custody writes");
        handler.AccountBody = "{\"user\":{\"displayName\":\"Not an identity\"}}";
        await RejectAsync(() => endpoint.GetAccountAsync(grant.AccessToken));
        handler.AccountBody = "{}";
        await RejectAsync(() => endpoint.GetAccountAsync(grant.AccessToken));
        await endpoint.RefreshAsync("test-client", "synthetic-refresh");
        Require(handler.LastForm["grant_type"] == "refresh_token" &&
            handler.LastForm["refresh_token"] == "synthetic-refresh" &&
            !handler.LastForm.ContainsKey("code"), "background refresh");
        var calls = handler.Calls;
        await RejectAsync(() => endpoint.RefreshAsync("different-client", "synthetic-refresh"));
        Require(calls == handler.Calls, "client mismatch refused before transport");

        var valid = handler.Body;
        foreach (var bad in new[]
        {
            valid.Replace("Bearer", "DPoP"), valid.Replace("3600", "0"),
            valid.Replace("3600", "900000"), valid.Replace("3600", "\"not-a-number\""),
            valid.Replace(GoogleDriveProviderConfiguration.DriveScope, "unapproved-scope"),
            valid.Replace("synthetic-access", ""), valid.Replace("synthetic-refresh", ""),
            "[]", "{not-json synthetic-private-echo}", new string('x', 65537)
        })
        {
            handler.Body = bad;
            await RejectAsync(() => endpoint.ExchangeCodeAsync(redirect, "synthetic-code", flow.Verifier));
        }
        handler.Body = "synthetic-private-echo";
        handler.Status = HttpStatusCode.BadRequest;
        await RejectAsync(() => endpoint.RefreshAsync("test-client", "synthetic-refresh"));
        handler.Status = HttpStatusCode.Redirect;
        await RejectAsync(() => endpoint.RefreshAsync("test-client", "synthetic-refresh"));
        handler.ThrowTransport = true;
        await RejectAsync(() => endpoint.RefreshAsync("test-client", "synthetic-refresh"));

        var binding = new GoogleDriveConnectionBinding("test-client", GoogleDriveConnectionBinding.CreateAccountKey("p1"),
            "p1", "test@example.invalid", "Test", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        var store = new TokenStore(binding.ConnectionId);
        var fake = new TokenEndpoint();
        var source = new GoogleDriveAccessTokenSource(store, fake, binding);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => source.GetAccessTokenAsync()));
        Require(fake.Calls == 1 && store.Reads == 1 && store.Writes == 1 && store.Value == "synthetic-rotated",
            "concurrent refresh coalesced and rotated token persisted for exact account");
        foreach (var invalid in new[]
        {
            fake.Result with { Scopes = [] }, fake.Result with { ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) },
            fake.Result with { ExpiresUtc = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(1), DateTimeKind.Unspecified) },
            fake.Result with { AccessToken = "" }
        })
        {
            var rejected = new TokenEndpoint { Result = invalid };
            var target = new TokenStore(binding.ConnectionId);
            var badSource = new GoogleDriveAccessTokenSource(target, rejected, binding);
            await RejectAsync(() => badSource.GetAccessTokenAsync());
            await RejectAsync(() => badSource.GetAccessTokenAsync());
            Require(rejected.Calls == 2 && target.Writes == 0, "invalid token never cached or persisted");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await new GoogleDriveAccessTokenSource(store, fake, binding).GetAccessTokenAsync(cancelled.Token);
            throw new InvalidOperationException("cancel was ignored"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS Google OAuth real PKCE/request/parser, client and scope guards, bounded responses, redaction, account refresh/cache/rotation and cancellation");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            Require(!error.ToString().Contains("synthetic-private-echo"), "provider secrets not echoed on rejection");
            return;
        }
        throw new InvalidOperationException("Expected Google OAuth refusal was not produced.");
    }

    private static Dictionary<string, string> ParseForm(string text) => text.Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')),
            x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));

    private sealed class TokenHandler : HttpMessageHandler
    {
        public string Body = JsonSerializer.Serialize(new { access_token = "synthetic-access", refresh_token = "synthetic-refresh",
            token_type = "Bearer", expires_in = 3600, scope = GoogleDriveProviderConfiguration.DriveScope });
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool ThrowTransport;
        public string AccountBody = "{\"user\":{\"permissionId\":\"synthetic-permission\",\"emailAddress\":\"test@example.invalid\",\"displayName\":\"Test\"}}";
        public int Calls;
        public Dictionary<string, string> LastForm = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.Method == HttpMethod.Get)
            {
                Require(request.RequestUri?.Host == "www.googleapis.com" &&
                    request.RequestUri.AbsolutePath == "/drive/v3/about" &&
                    !request.RequestUri.Query.Contains("synthetic-access") &&
                    request.Headers.Authorization?.Scheme == "Bearer" &&
                    request.Headers.Authorization.Parameter == "synthetic-access", "account bootstrap header routing");
                return new(Status) { Content = new StringContent(AccountBody) };
            }
            Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://oauth2.googleapis.com/token",
                "no credentials in URL, exact HTTPS endpoint");
            LastForm = ParseForm(await request.Content!.ReadAsStringAsync(token));
            if (ThrowTransport) throw new HttpRequestException("synthetic-private-echo");
            return new(Status) { Content = new StringContent(Body) };
        }
    }

    private sealed class TokenStore(string expected) : IGoogleDriveRefreshTokenStore
    {
        public int Reads, Writes;
        public string Value = "synthetic-refresh";
        public Task<string?> ReadAsync(string id, CancellationToken token = default)
        { Require(id == expected, "credential reads route exact account"); Reads++; return Task.FromResult<string?>(Value); }
        public Task WriteAsync(string id, string value, CancellationToken token = default)
        { Require(id == expected, "credential writes route exact account"); Writes++; Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("Unexpected deletion");
    }

    private sealed class TokenEndpoint : IGoogleDriveTokenEndpoint
    {
        public int Calls;
        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken, CancellationToken token = default) =>
            Task.FromResult(new GoogleDriveAccountIdentity("p1", "test@example.invalid", "Test", ""));
        public GoogleDriveTokenResult Result = new("synthetic-access", DateTime.UtcNow.AddHours(1),
            [GoogleDriveProviderConfiguration.DriveScope], "synthetic-rotated");
        public async Task<GoogleDriveTokenResult> RefreshAsync(string id, string refresh, CancellationToken token = default)
        { Calls++; await Task.Yield(); return Result; }
    }
}
