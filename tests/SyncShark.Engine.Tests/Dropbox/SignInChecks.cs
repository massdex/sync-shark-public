using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for SignIn: PKCE and authorization construction, loopback return verification, the receipt
/// and retry catalog copy, and the refreshing token source against a supplied sender.</summary>
public static class SignInChecks
{
    public static async Task RunAsync()
    {
        var (verifier, challenge, state) = SignIn.CreatePkceFlow();
        Check.Require(verifier.Length >= 43 && challenge == Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            "PKCE challenge is not the S256 verifier hash");
        var fresh = SignIn.CreatePkceFlow();
        Check.Require(fresh.Verifier != verifier && fresh.State != state && fresh.State.Length > 0, "A new flow repeated its verifier or state");

        var redirect = SignIn.RedirectUri;
        var authorization = Query(SignIn.BuildAuthorizationUri(SignIn.ClientId, redirect, challenge, state).Query);
        Check.Require(SignIn.BuildAuthorizationUri(SignIn.ClientId, redirect, challenge, state).Scheme == Uri.UriSchemeHttps,
            "Authorization page is not https");
        Check.Require(authorization["response_type"] == "code" && authorization["code_challenge"] == challenge &&
                      authorization["code_challenge_method"] == "S256" && authorization["state"] == state &&
                      authorization["token_access_type"] == "offline", "Authorization parameters were lost");
        Check.Require(authorization["client_id"] == SignIn.ClientId && authorization["redirect_uri"] == redirect.AbsoluteUri,
            "Client or redirect was lost");
        Check.Require(SignIn.RequiredScopes.All(scope => authorization["scope"].Split(' ').Contains(scope)),
            "The full-account scope envelope was lost");

        var path = redirect.AbsolutePath;
        Check.Require(SignIn.ParseCallbackTarget(path + "?code=abc&state=" + Uri.EscapeDataString(state), path, state) == "abc",
            "A verified callback was refused");
        Rejects(() => SignIn.ParseCallbackTarget("/other?code=abc&state=" + state, path, state));
        Rejects(() => SignIn.ParseCallbackTarget(path + "?code=abc&state=wrong", path, state));
        Rejects(() => SignIn.ParseCallbackTarget(path + "?error=access_denied&state=" + state, path, state));
        Rejects(() => SignIn.ParseCallbackTarget(path + "?state=" + state, path, state));

        var received = Text.Get(TextKey.SignInReturnReceived);
        var retry = Text.Get(TextKey.SignInReturnRetry);
        Check.Require(SignIn.AcknowledgementPage(received: true).Contains(received, StringComparison.Ordinal) &&
                      !SignIn.AcknowledgementPage(received: true).Contains(retry, StringComparison.Ordinal), "Receipt page used the wrong copy");
        Check.Require(SignIn.AcknowledgementPage(received: false).Contains(retry, StringComparison.Ordinal) &&
                      !SignIn.AcknowledgementPage(received: false).Contains(received, StringComparison.Ordinal), "Retry page used the wrong copy");
        Check.Require(!SignIn.AcknowledgementPage(received: true).Contains("abc", StringComparison.Ordinal) &&
                      !SignIn.AcknowledgementPage(received: true).Contains(state, StringComparison.Ordinal),
            "The receipt page reflected a callback value");

        if (!OperatingSystem.IsWindows()) return;
        var connectionId = "connection-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var credentials = new CredentialStore();
        var expected = new DropboxRootIdentity("acct-fixture", "root-fixture", "home-fixture", "folder-fixture");
        var sends = new List<string>();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (request, _) =>
        {
            var target = request.RequestUri!.AbsolutePath;
            sends.Add(target);
            if (target.EndsWith("oauth2/token", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"access_token\":\"access-fixture\",\"expires_in\":3600,\"account_id\":\"acct-fixture\"}"));
            if (target.EndsWith("get_current_account", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"account_id\":\"acct-fixture\",\"root_info\":{\"root_namespace_id\":\"root-fixture\",\"home_namespace_id\":\"home-fixture\"}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };
        try
        {
            credentials.Save(connectionId, "refresh-fixture");
            var source = new RefreshingAccessTokenSource(send, credentials, connectionId, expected);
            Check.Require(await source.GetAccessTokenAsync(default) == "access-fixture", "Refresh did not return the access token");
            Check.Require(sends.Count == 2, "Refresh did not exchange and verify exactly once");
            Check.Require(await source.GetAccessTokenAsync(default) == "access-fixture" && sends.Count == 2,
                "A cached token was refreshed again");

            var wrongNamespace = new RefreshingAccessTokenSource(send, credentials, connectionId,
                expected with { RootNamespaceId = "other-fixture" });
            var refused = false;
            try { await wrongNamespace.GetAccessTokenAsync(default); }
            catch (InvalidOperationException) { refused = true; }
            Check.Require(refused, "A different namespace was accepted for the saved connection");

            var absentId = "connection-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            if (credentials.Read(absentId) is null)
            {
                var absent = new RefreshingAccessTokenSource(send, credentials, absentId, expected);
                refused = false;
                try { await absent.GetAccessTokenAsync(default); }
                catch (DropboxCredentialUnavailableException) { refused = true; }
                Check.Require(refused, "A missing protected credential was accepted");
            }
        }
        finally { credentials.Remove(connectionId); }
    }

    private static void Rejects(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("An invalid callback was accepted");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Query(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }
        return result;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
