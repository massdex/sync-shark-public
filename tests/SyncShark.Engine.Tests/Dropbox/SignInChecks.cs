using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for SignIn: PKCE and loopback-return verification, the receipt/retry catalog copy, the
/// granted-scope envelope, an abandoned browser return followed by a working retry, and disconnect removal of the
/// legacy credential only for its proven owner.</summary>
public static class SignInChecks
{
    public static async Task RunAsync()
    {
        var (verifier, challenge, state) = SignIn.CreatePkceFlow();
        Check.Require(verifier.Length >= 43 && challenge == Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            "PKCE challenge is not the S256 verifier hash");
        var fresh = SignIn.CreatePkceFlow();
        Check.Require(fresh.Verifier != verifier && fresh.State != state && fresh.State.Length > 0,
            "A new flow repeated its verifier or state");

        var redirectPath = SignIn.RedirectUri.AbsolutePath;
        Check.Require(SignIn.ParseCallbackTarget(redirectPath + "?code=abc&state=" + Uri.EscapeDataString(state),
            redirectPath, state) == "abc", "A verified callback was refused");
        Rejects(() => SignIn.ParseCallbackTarget("/other?code=abc&state=" + state, redirectPath, state));
        Rejects(() => SignIn.ParseCallbackTarget(redirectPath + "?code=abc&state=wrong", redirectPath, state));
        Rejects(() => SignIn.ParseCallbackTarget(redirectPath + "?error=access_denied&state=" + state, redirectPath, state));
        Rejects(() => SignIn.ParseCallbackTarget(redirectPath + "?state=" + state, redirectPath, state));

        var received = Text.Get(TextKey.SignInReturnReceived);
        var retry = Text.Get(TextKey.SignInReturnRetry);
        Check.Require(SignIn.AcknowledgementPage(received: true).Contains(received, StringComparison.Ordinal) &&
                      !SignIn.AcknowledgementPage(received: true).Contains(retry, StringComparison.Ordinal),
            "Receipt page used the wrong copy");
        Check.Require(SignIn.AcknowledgementPage(received: false).Contains(retry, StringComparison.Ordinal) &&
                      !SignIn.AcknowledgementPage(received: false).Contains(received, StringComparison.Ordinal),
            "Retry page used the wrong copy");
        Check.Require(!SignIn.AcknowledgementPage(received: true).Contains("abc", StringComparison.Ordinal) &&
                      !SignIn.AcknowledgementPage(received: true).Contains(state, StringComparison.Ordinal),
            "The receipt page reflected a callback value");

        Check.Require(SignIn.GrantAccepted(SignIn.RequiredScopes), "The registered grant envelope was rejected");
        Check.Require(SignIn.GrantAccepted(SignIn.RequiredScopes.Reverse()), "The grant check was order-sensitive");
        Check.Require(!SignIn.GrantAccepted(SignIn.RequiredScopes.Take(SignIn.RequiredScopes.Count - 1)),
            "A reduced grant was accepted");
        Check.Require(!SignIn.GrantAccepted(SignIn.RequiredScopes.Append("extra.scope")), "An extra grant was accepted");
        Check.Require(!SignIn.GrantAccepted(Array.Empty<string>()), "An empty grant was accepted");

        await RetryAfterAbandonedReturnAsync();
        await DisconnectOwnershipAsync();
    }

    private static async Task RetryAfterAbandonedReturnAsync()
    {
        var port = FreePort();
        var redirect = new Uri("http://127.0.0.1:" + port + "/oauth/dropbox/callback/");
        var path = redirect.AbsolutePath;

        var timedOut = false;
        using (var watch = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try { await SignIn.ReceiveCallbackAsync(redirect, path, "state-first", watch.Token, TimeSpan.FromMilliseconds(250)); }
            catch (TimeoutException) { timedOut = true; }
        }
        Check.Require(timedOut, "An abandoned browser return did not end with a retryable timeout");

        var state = "state-second";
        var pending = SignIn.ReceiveCallbackAsync(redirect, path, state, CancellationToken.None, TimeSpan.FromSeconds(10));
        var page = await SendCallbackAsync(port, path + "?code=abc&state=" + Uri.EscapeDataString(state));
        Check.Require(await pending == "abc", "A retry after an abandoned return did not complete");
        Check.Require(page.Contains(Text.Get(TextKey.SignInReturnReceived), StringComparison.Ordinal),
            "A verified return did not serve the receipt copy");

        var malformedState = "state-third";
        var malformed = SignIn.ReceiveCallbackAsync(redirect, path, malformedState, CancellationToken.None, TimeSpan.FromSeconds(10));
        var rejectedPage = await SendCallbackAsync(port, path + "?code=abc&state=wrong");
        var rejected = false;
        try { await malformed; }
        catch (InvalidDataException) { rejected = true; }
        Check.Require(rejected, "A malformed return was accepted");
        Check.Require(rejectedPage.Contains(Text.Get(TextKey.SignInReturnRetry), StringComparison.Ordinal) &&
                      !rejectedPage.Contains("abc", StringComparison.Ordinal),
            "A malformed return did not serve the retry copy or reflected a callback value");
    }

    private static async Task DisconnectOwnershipAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        CredentialStoreChecks.LegacyFixture.RequireAbsent();
        var store = new CredentialStore();
        const string accountOwner = "acct-owner-a";
        const string accountOther = "acct-owner-b";
        var connectionOwner = Connections.CreateConnectionId(accountOwner);
        var connectionOther = Connections.CreateConnectionId(accountOther);
        Func<string, CancellationToken, Task<(string AccountId, string RootNamespaceId, string HomeNamespaceId)>> verify =
            (token, _) =>
            {
                Check.Require(token == "legacy-owner-a", "Unexpected legacy token passed to verification");
                return Task.FromResult((accountOwner, "root-a", "home-a"));
            };
        try
        {
            CredentialStoreChecks.LegacyFixture.Write("legacy-owner-a");
            store.Save(connectionOther, "own-b");
            await SignIn.DisconnectAsync(verify, store, connectionOther, default);
            Check.Require(store.ReadOwn(connectionOther) is null && store.ReadLegacy() == "legacy-owner-a",
                "Disconnecting another account deleted the legacy owner's credential or kept its own");

            await SignIn.DisconnectAsync(verify, store, connectionOwner, default);
            Check.Require(store.ReadLegacy() is null, "The proven legacy credential survived its owner's disconnect");

            CredentialStoreChecks.LegacyFixture.Write("legacy-dead");
            await SignIn.DisconnectAsync((_, _) => throw new DropboxCredentialUnavailableException(connectionOwner),
                store, connectionOwner, default);
            Check.Require(store.ReadLegacy() is null, "A revoked legacy credential was kept");

            CredentialStoreChecks.LegacyFixture.Write("legacy-unreachable");
            store.Save(connectionOwner, "own-a");
            var failed = false;
            try
            {
                await SignIn.DisconnectAsync(
                    (_, _) => throw new HttpRequestException(Text.Get(TextKey.SignInReturnRetry)), store, connectionOwner, default);
            }
            catch (HttpRequestException) { failed = true; }
            Check.Require(failed && store.ReadLegacy() == "legacy-unreachable" && store.ReadOwn(connectionOwner) == "own-a",
                "An unreachable legacy credential was removed or the own credential was deleted first");
        }
        finally
        {
            CredentialStoreChecks.LegacyFixture.Delete();
            store.Remove(connectionOwner);
            store.Remove(connectionOther);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> SendCallbackAsync(int port, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                await using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\nHost: localhost\r\n\r\n"));
                using var reader = new StreamReader(stream, Encoding.ASCII);
                return await reader.ReadToEndAsync();
            }
            catch (SocketException) when (attempt < 400) { await Task.Delay(25); }
        }
    }

    private static void Rejects(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("An invalid callback was accepted");
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
