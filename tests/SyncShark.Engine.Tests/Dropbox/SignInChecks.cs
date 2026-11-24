using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for SignIn: PKCE and authorization construction from the endpoint data, loopback-return
/// verification and its receipt/retry pages, the granted-scope envelope, the one-flow bounded attempt that frees the
/// gate for a retry, and disconnect removal of the legacy credential only for its proven owner.</summary>
public static class SignInChecks
{
    public static Task PreSaveGrantAsync() => throw new NotImplementedException();
    public static Task RetainedIdentityGuardAsync() => throw new NotImplementedException();
    public static Task ContradictoryRefreshAsync() => throw new NotImplementedException();

    public static async Task RunAsync()
    {
        var (verifier, challenge, state) = SignIn.CreatePkceFlow();
        Check.Require(verifier.Length >= 43 && challenge == Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            "PKCE challenge is not the S256 verifier hash");
        var fresh = SignIn.CreatePkceFlow();
        Check.Require(fresh.Verifier != verifier && fresh.State != state && fresh.State.Length > 0,
            "A new flow repeated its verifier or state");

        var authorization = Query(SignIn.BuildAuthorizationUri(SignIn.ClientId, SignIn.RedirectUri, challenge, state));
        Check.Require(authorization["response_type"] == "code" && authorization["code_challenge"] == challenge &&
                      authorization["code_challenge_method"] == "S256" && authorization["state"] == state &&
                      authorization["token_access_type"] == "offline", "Authorization parameters were lost");
        Check.Require(authorization["client_id"] == SignIn.ClientId &&
                      authorization["redirect_uri"] == SignIn.RedirectUri.AbsoluteUri, "Client or redirect was lost");
        Check.Require(SignIn.RequiredScopes.All(scope => authorization["scope"].Split(' ').Contains(scope)),
            "The full-account scope envelope was lost");

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

        var failures = new List<string>();
        foreach (var receipt in new[] { true, false })
        {
            var page = SignIn.AcknowledgementPage(receipt);
            var body = page[(page.IndexOf("<body>", StringComparison.Ordinal) + "<body>".Length)..];
            if (!body.Contains(">Sync Shark<", StringComparison.Ordinal))
                failures.Add(receipt ? "Receipt body did not identify Sync Shark" : "Retry body did not identify Sync Shark");
        }
        await BoundedAttemptAsync(failures);
        await DisconnectOwnershipAsync(failures);
        Check.Require(failures.Count == 0, string.Join("; ", failures));
        var port = await AbandonedReturnAsync();
        await MalformedRequestsAsync(port);
    }

    private static async Task BoundedAttemptAsync(List<string> failures)
    {
        var timedOut = false;
        using (var watch = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try
            {
                await SignIn.RunAuthorizationAttemptAsync(watch.Token, TimeSpan.FromMilliseconds(250), async attempt =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, attempt);
                    return "unreachable";
                });
            }
            catch (TimeoutException) { timedOut = true; }
            catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
        }
        if (!timedOut) failures.Add("The authorization bound did not end a stalled attempt");
        Check.Require(await SignIn.RunAuthorizationAttemptAsync(CancellationToken.None, TimeSpan.FromSeconds(10),
                _ => Task.FromResult("ok")) == "ok", "The gate was not released after a timed-out attempt");
    }

    private static async Task<int> AbandonedReturnAsync()
    {
        var port = FreePort();
        var redirect = new Uri("http://127.0.0.1:" + port + "/oauth/dropbox/callback/");
        var path = redirect.AbsolutePath;
        var timedOut = false;
        using (var watch = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try
            {
                await SignIn.RunAuthorizationAttemptAsync(watch.Token, TimeSpan.FromMilliseconds(250),
                    attempt => SignIn.ReceiveCallbackAsync(redirect, path, "state-first", attempt));
            }
            catch (TimeoutException) { timedOut = true; }
        }
        Check.Require(timedOut, "An abandoned browser return did not end with a retryable timeout");

        var state = "state-second";
        var pending = SignIn.RunAuthorizationAttemptAsync(CancellationToken.None, TimeSpan.FromSeconds(10),
            attempt => SignIn.ReceiveCallbackAsync(redirect, path, state, attempt));
        var page = await SendAsync(port, "GET " + path + "?code=abc&state=" + Uri.EscapeDataString(state) + " HTTP/1.1\r\nHost: localhost\r\n\r\n");
        Check.Require(await pending == "abc", "A retry after an abandoned return did not complete");
        Check.Require(page.Contains(Text.Get(TextKey.SignInReturnReceived), StringComparison.Ordinal),
            "A verified return did not serve the receipt copy");
        return port;
    }

    private static async Task MalformedRequestsAsync(int port)
    {
        var redirect = new Uri("http://127.0.0.1:" + port + "/oauth/dropbox/callback/");
        var path = redirect.AbsolutePath;

        var pending = SignIn.RunAuthorizationAttemptAsync(CancellationToken.None, TimeSpan.FromSeconds(10),
            attempt => SignIn.ReceiveCallbackAsync(redirect, path, "state-third", attempt));
        var invalidPage = await SendAsync(port, "BOGUS / HTTP/1.1\r\n\r\n");
        var rejected = false;
        try { await pending; }
        catch (InvalidDataException) { rejected = true; }
        Check.Require(rejected && invalidPage.Contains(Text.Get(TextKey.SignInReturnRetry), StringComparison.Ordinal),
            "An invalid request line did not serve the retry copy");

        pending = SignIn.RunAuthorizationAttemptAsync(CancellationToken.None, TimeSpan.FromSeconds(10),
            attempt => SignIn.ReceiveCallbackAsync(redirect, path, "state-fourth", attempt));
        var wrongStatePage = await SendAsync(port, "GET " + path + "?code=abc&state=wrong HTTP/1.1\r\nHost: localhost\r\n\r\n");
        rejected = false;
        try { await pending; }
        catch (InvalidDataException) { rejected = true; }
        Check.Require(rejected && wrongStatePage.Contains(Text.Get(TextKey.SignInReturnRetry), StringComparison.Ordinal) &&
                      !wrongStatePage.Contains("abc", StringComparison.Ordinal),
            "A malformed return did not serve the retry copy or reflected a callback value");
    }

    private static async Task DisconnectOwnershipAsync(List<string> failures)
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
                Check.Require(token == "legacy-fixture", "Unexpected legacy token passed to verification");
                return Task.FromResult((accountOwner, "root-a", "home-a"));
            };
        try
        {
            foreach (var entry in new[] { "missing", "fixed", "own" })
            {
                if (entry == "fixed") CredentialStoreChecks.LegacyFixture.Write("legacy-fixture");
                if (entry == "own") store.Save(connectionOther, "own-b");
                foreach (var refresh in new[] { false, true })
                {
                    var sends = 0;
                    Task<HttpResponseMessage> Refused(HttpRequestMessage _, CancellationToken __)
                    {
                        sends++;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                            { Content = new StringContent("""{"error":"invalid_grant"}""") });
                    }
                    var classified = false;
                    try
                    {
                        if (refresh) await SignIn.AccessTokenSource(Refused, store, connectionOther, accountOther, "root-b", "home-b")(default);
                        else await SignIn.VerifyAccountAsync(Refused, store, connectionOther, default);
                    }
                    catch (ItemFailedException error) { classified = error.Cause == TextKey.SignInReturnRetry; }
                    catch (DropboxCredentialUnavailableException) { }
                    if (!classified || sends != (entry == "missing" ? 0 : 1))
                        failures.Add(entry + (refresh ? " refresh" : " first-bind") + " credential lost the typed sign-in cause");
                }
            }
            await SignIn.DisconnectAsync(verify, store, connectionOther, default);
            Check.Require(store.ReadOwn(connectionOther) is null && store.ReadLegacy() == "legacy-fixture",
                "Disconnecting another account deleted the legacy owner's credential or kept its own");

            await SignIn.DisconnectAsync(verify, store, connectionOwner, default);
            Check.Require(store.ReadLegacy() is null, "The proven legacy credential survived its owner's disconnect");

            // A revoked legacy credential is unusable and removed.
            CredentialStoreChecks.LegacyFixture.Write("legacy-fixture");
            await SignIn.DisconnectAsync((_, _) => throw new DropboxCredentialUnavailableException(connectionOwner),
                store, connectionOwner, default);
            Check.Require(store.ReadLegacy() is null, "A revoked legacy credential was kept");

            // Offline/unproven legacy ownership completes the local disconnect and leaves the fixed entry (R41).
            // An HTTP timeout surfaces as a non-caller OperationCanceledException with an inner TimeoutException.
            CredentialStoreChecks.LegacyFixture.Write("legacy-fixture");
            foreach (var (error, cause) in new (Exception, string)[]
            {
                (new OperationCanceledException("timeout", new TimeoutException()), "An HTTP timeout"),
                (new IOException(), "An interrupted response body"),
                (new HttpRequestException(), "An offline legacy owner"),
                (new InvalidDataException(), "An incomplete identity response")
            })
            {
                store.Save(connectionOwner, "own-a");
                var completed = false;
                try
                {
                    await SignIn.DisconnectAsync((_, _) => throw error, store, connectionOwner, default);
                    completed = true;
                }
                catch (Exception observed) when (ReferenceEquals(observed, error)) { }
                if (!completed || store.ReadOwn(connectionOwner) is not null || store.ReadLegacy() != "legacy-fixture")
                    failures.Add(cause + " aborted the local disconnect or removed the unproven fixed entry");
            }

            await MalformedDisconnectAsync(store, connectionOwner, failures,
            [
                (HttpStatusCode.OK, "[]", null),
                (HttpStatusCode.OK, """{"access_token":1}""", null),
                (HttpStatusCode.OK, """{"access_token":"fixture","expires_in":999999999999}""", null),
                (HttpStatusCode.BadRequest, """{"error":"\uD800"}""", null)
            ]);

            store.Save(connectionOwner, "before-reconnect");
            var held = new TaskCompletionSource<(string, string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnect = SignIn.DisconnectAsync((_, _) => held.Task, store, connectionOwner, default);
            new CredentialStore().Save(connectionOwner, "after-reconnect");
            held.SetResult((accountOther, "root-b", "home-b"));
            await disconnect;
            Check.Require(store.ReadOwn(connectionOwner) == "after-reconnect",
                "Delayed disconnect removed the credential of a successfully reconnected account");

            // Caller cancellation is not swallowed: nothing is removed.
            store.Save(connectionOwner, "own-c");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var propagated = false;
            try
            {
                await SignIn.DisconnectAsync((_, _) => throw new OperationCanceledException(cancelled.Token),
                    store, connectionOwner, cancelled.Token);
            }
            catch (OperationCanceledException) { propagated = true; }
            Check.Require(propagated && store.ReadOwn(connectionOwner) == "own-c" && store.ReadLegacy() == "legacy-fixture",
                "Caller cancellation removed credentials or was swallowed");
        }
        finally
        {
            CredentialStoreChecks.LegacyFixture.Delete();
            store.Remove(connectionOwner);
            store.Remove(connectionOther);
        }
    }

    private const string ValidTokenReply = """{"access_token":"fixture"}""";

    /// <summary>Verify both credential consumers with an unrelated fixed credential and the real account route.</summary>
    public static async Task UnrelatedCredentialAsync()
    {
        CredentialStoreChecks.LegacyFixture.RequireAbsent();
        var store = new CredentialStore();
        const string accountOther = "acct-owner-b";
        var connectionOther = Connections.CreateConnectionId(accountOther);
        Check.Require(store.ReadOwn(connectionOther) is null, "The synthetic account fixture is occupied");
        try
        {
            CredentialStoreChecks.LegacyFixture.Write("legacy-fixture");
            const string otherIdentity = """{"account_id":"acct-owner-a","root_info":{"root_namespace_id":"root-a","home_namespace_id":"home-a"}}""";
            foreach (var refresh in new[] { false, true })
            {
                var calls = 0;
                Task<HttpResponseMessage> Reply(HttpRequestMessage _, CancellationToken __) => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(++calls == 1 ? ValidTokenReply : otherIdentity) });
                var classified = false;
                try
                {
                    if (refresh) await SignIn.AccessTokenSource(Reply, store, connectionOther, accountOther, "root-b", "home-b")(default);
                    else await SignIn.VerifyAccountAsync(Reply, store, connectionOther, default);
                }
                catch (ItemFailedException error) { classified = error.Cause == TextKey.SignInReturnRetry; }
                catch (InvalidOperationException) { }
                Check.Require(classified && calls == 2 && store.ReadLegacy() == "legacy-fixture",
                    "Unrelated legacy credential masked missing own credential: " + (refresh ? "refresh" : "first bind"));
            }
        }
        finally { CredentialStoreChecks.LegacyFixture.Delete(); }
    }

    /// <summary>Verify local removal when a malformed response reaches the account route.</summary>
    public static async Task MalformedAccountDisconnectAsync()
    {
        CredentialStoreChecks.LegacyFixture.RequireAbsent();
        var store = new CredentialStore();
        var connection = Connections.CreateConnectionId("acct-owner-a");
        Check.Require(store.ReadOwn(connection) is null, "The synthetic account fixture is occupied");
        var failures = new List<string>();
        try
        {
            CredentialStoreChecks.LegacyFixture.Write("legacy-fixture");
            await MalformedDisconnectAsync(store, connection, failures,
            [
                (HttpStatusCode.OK, """{"access_token":"bad\r\nheader"}""", null),
                (HttpStatusCode.OK, ValidTokenReply, """{"account_id":"\uD800","root_info":{"root_namespace_id":"r","home_namespace_id":"h"}}"""),
            ]);
            Check.Require(failures.Count == 0, string.Join("; ", failures));
        }
        finally { CredentialStoreChecks.LegacyFixture.Delete(); store.Remove(connection); }
    }

    private static async Task MalformedDisconnectAsync(CredentialStore store, string connectionOwner, List<string> failures,
        (HttpStatusCode Status, string Token, string? Account)[] replies)
    {
        foreach (var (status, tokenBody, accountBody) in replies)
        {
            store.Save(connectionOwner, "own-a");
            var completed = false;
            var calls = 0;
            try
            {
                await SignIn.DisconnectAsync((legacy, token) => SignIn.OpenIdentityAsync((_, _) =>
                    Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(++calls == 1 ? tokenBody : accountBody!) }),
                    connectionOwner, legacy, token), store, connectionOwner, default);
                completed = true;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { }
            if (!completed || calls != (accountBody is null ? 1 : 2) || store.ReadOwn(connectionOwner) is not null || store.ReadLegacy() != "legacy-fixture")
                failures.Add(status == HttpStatusCode.BadRequest ? "Malformed OAuth error Unicode aborted disconnect or changed unproven ownership" :
                    "Malformed provider reply aborted disconnect or changed unproven ownership");
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

    private static async Task<string> SendAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    private static void Rejects(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("An invalid callback was accepted");
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }
        return result;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
