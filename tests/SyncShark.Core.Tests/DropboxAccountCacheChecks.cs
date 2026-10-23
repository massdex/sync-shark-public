using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// M21: one real Host, connection coordinator, authority SQLite and registered loopback callback.
// Only OAuth/provider inputs and per-connection credentials are synthetic. Refresh/revoke counts
// expose cache behavior without private reflection or token output. No real provider/store is used.
internal static class DropboxAccountCacheChecks
{
    internal static async Task RunAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        using var provider = new SyntheticProvider();
        var credentials = new SyntheticCredentials(provider.AcceptCredential);
        using var http = new HttpClient(provider);
        var pairs = new SyncPairStore(instance.StateAuthority);
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairs,
            new SyncRuntimeStateStore(instance.StateAuthority), connections, credentials,
            new SyncAuthorityDatabase(Path.Combine(root, "authority.db")));

        var a = await AuthorizeAsync((callback, attemptToken) => host.ConnectDropboxAsync(callback, attemptToken), "cache-a", token);
        var b = await AuthorizeAsync((callback, attemptToken) => host.ConnectDropboxAsync(callback, attemptToken, addAccount: true),
            "cache-b", token);
        Check(a.AccountId == SyntheticProvider.AccountA && b.AccountId == SyntheticProvider.AccountB &&
              a.ConnectionId != b.ConnectionId && a.CredentialReference != b.CredentialReference,
            "M21.1 actual Host connect binds two isolated synthetic accounts and credential routes");

        var pairRoot = Path.Combine(root, "retained-pair");
        Directory.CreateDirectory(pairRoot);
        var retainedFile = Path.Combine(pairRoot, "retained.txt");
        await File.WriteAllTextAsync(retainedFile, "M21 retained fixture bytes", token);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(pairRoot)) identity = lease.Identity;
        var pair = new SyncPair("m21-retained-pair", "M21 retained pair", pairRoot,
            identity.VolumeSerialNumber, identity.FileId, "/M21Fixture", "id:m21-fixture",
            ConnectionId: a.ConnectionId);
        await pairs.UpsertAsync(pair, cancellationToken: token);

        await VerifyAsync(host, a, token);
        await VerifyAsync(host, b, token);
        await VerifyAsync(host, a, token);
        await VerifyAsync(host, b, token);
        Counts(provider, 1, 1, "M21.2 public live status warms and reuses both composite account caches");

        // The retained-credential shortcut is a successful public reconnect too. It must evict A,
        // even though no browser/code exchange is needed, and must not evict the unrelated B entry.
        var retained = await host.ConnectDropboxAsync(_ => throw new InvalidOperationException(
            "A retained connected account unexpectedly opened authorization."), token);
        Check(retained.ConnectionId == a.ConnectionId, "M21.3 retained reconnect keeps the exact selected identity");
        await VerifyAsync(host, a, token);
        await VerifyAsync(host, b, token);
        Counts(provider, 2, 1, "M21.3 successful same-Host reconnect evicts only A's composite cache");

        var beforeReauthorize = await credentials.ForConnection(a).ReadAsync(token);
        a = await AuthorizeAsync((callback, attemptToken) => host.ReauthorizeDropboxAsync(callback, attemptToken, a.ConnectionId),
            "cache-a", token);
        Check(beforeReauthorize != await credentials.ForConnection(a).ReadAsync(token),
            "M21.4 successful authorization commits a rotated synthetic refresh credential");
        await VerifyAsync(host, a, token);
        await VerifyAsync(host, b, token);
        Counts(provider, 3, 1, "M21.4 reauthorization refreshes A without clearing B's cached token");

        var bindingBeforeRefusal = JsonSerializer.Serialize(connections.Load());
        var credentialABeforeRefusal = await credentials.ForConnection(a).ReadAsync(token);
        var credentialBBeforeRefusal = await credentials.ForConnection(b).ReadAsync(token);
        var writesBeforeRefusal = credentials.Writes;
        var deletesBeforeRefusal = credentials.Deletes;
        await ExpectWrongAccountAsync(() => AuthorizeAsync(
            (callback, attemptToken) => host.ReauthorizeDropboxAsync(callback, attemptToken, a.ConnectionId), "cache-b", token));
        Check(JsonSerializer.Serialize(connections.Load()) == bindingBeforeRefusal &&
              await credentials.ForConnection(a).ReadAsync(token) == credentialABeforeRefusal &&
              await credentials.ForConnection(b).ReadAsync(token) == credentialBBeforeRefusal &&
              credentials.Writes == writesBeforeRefusal && credentials.Deletes == deletesBeforeRefusal,
            "M21.5 wrong-account reauthorization refuses before any binding or credential mutation");
        await VerifyAsync(host, a, token);
        await VerifyAsync(host, b, token);
        Counts(provider, 3, 1, "M21.5 wrong-account refusal preserves both warmed account caches");

        provider.FailRevokeForA = true;
        await host.DisconnectDropboxAsync(token, a.ConnectionId);
        var disconnected = await host.GetDropboxConnectionStatusAsync(false, token, a.ConnectionId);
        Check(!disconnected.HasProtectedCredential && !disconnected.LiveVerified &&
              disconnected.Binding?.AccountId == a.AccountId &&
              await credentials.ForConnection(a).ReadAsync(token) is null &&
              await credentials.ForConnection(b).ReadAsync(token) == credentialBBeforeRefusal &&
              provider.RevokeA == 1,
            "M21.6 offline revoke still commits selected local disconnect and preserves B's credential");
        await host.DisconnectDropboxAsync(token, a.ConnectionId);
        await VerifyAsync(host, b, token);
        Check(provider.RevokeA == 1,
            "M21.6 repeated disconnect cannot reuse or revoke the old A access token a second time");
        Counts(provider, 3, 1, "M21.6 selected local eviction survives revoke failure and leaves B cached");
        Check((await host.LoadPairsAsync(token)).Pairs.Any(item => item.Id == pair.Id &&
                  item.ConnectionId == a.ConnectionId) &&
              await File.ReadAllTextAsync(retainedFile, token) == "M21 retained fixture bytes",
            "M21.6 local disconnect retains the real authority pair, account binding and local file bytes");

        var beforeWrongReconnect = JsonSerializer.Serialize(connections.Load());
        writesBeforeRefusal = credentials.Writes;
        deletesBeforeRefusal = credentials.Deletes;
        await ExpectWrongAccountAsync(() => AuthorizeAsync(
            (callback, attemptToken) => host.ConnectDropboxAsync(callback, attemptToken), "cache-b", token));
        Check(JsonSerializer.Serialize(connections.Load()) == beforeWrongReconnect &&
              await credentials.ForConnection(a).ReadAsync(token) is null &&
              await credentials.ForConnection(b).ReadAsync(token) == credentialBBeforeRefusal &&
              credentials.Writes == writesBeforeRefusal && credentials.Deletes == deletesBeforeRefusal,
            "M21.7 wrong-account reconnect cannot reassign retained A or alter either credential");
        await host.DisconnectDropboxAsync(token, a.ConnectionId);
        await VerifyAsync(host, b, token);
        Check(provider.RevokeA == 1, "M21.7 rejected reconnect does not resurrect A's prior cached token");
        Counts(provider, 3, 1, "M21.7 rejected reconnect leaves the unrelated B cache intact");

        var reconnected = await AuthorizeAsync((callback, attemptToken) => host.ConnectDropboxAsync(callback, attemptToken),
            "cache-a", token);
        Check(reconnected.ConnectionId == a.ConnectionId && reconnected.AccountId == a.AccountId &&
              reconnected.RootNamespaceId == a.RootNamespaceId && reconnected.HomeNamespaceId == a.HomeNamespaceId,
            "M21.8 disconnected same-Host reconnect retains exact account and namespace identity");
        await VerifyAsync(host, reconnected, token);
        await VerifyAsync(host, b, token);
        Counts(provider, 4, 1, "M21.8 reconnect uses newly authorized A credentials, never its revoked cache");
        Check(provider.RefreshUsedCurrentCredential && provider.UnexpectedRequests == 0 &&
              connections.LoadActiveBindings().Count == 2 && host.PendingAuthorization is null,
            "M21.8 every refresh used its current synthetic credential and no unrecognized provider route ran");
        Console.WriteLine("PASS M21 same-Host two-account Dropbox composite cache lifecycle proof");
    }

    private static async Task VerifyAsync(SyncAuthorityRuntimeHost host, DropboxConnectionBinding expected,
        CancellationToken token)
    {
        var status = await host.GetDropboxConnectionStatusAsync(true, token, expected.ConnectionId);
        Check(status.LiveVerified && status.HasProtectedCredential &&
              status.Binding is { } binding && binding.ConnectionId == expected.ConnectionId &&
              binding.AccountId == expected.AccountId,
            "M21 public live verification retains the selected account identity");
    }

    private static void Counts(SyntheticProvider provider, int a, int b, string message) =>
        Check(provider.RefreshA == a && provider.RefreshB == b, message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Dropbox account cache: " + message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task ExpectWrongAccountAsync(Func<Task<DropboxConnectionBinding>> action)
    {
        try { _ = await action(); }
        catch (InvalidOperationException exception) when (exception.Message.Contains(
                   "different Dropbox account or namespace", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Dropbox account cache: wrong-account authorization was not refused.");
    }

    private static async Task<DropboxConnectionBinding> AuthorizeAsync(
        Func<Func<Uri, Task>, CancellationToken, Task<DropboxConnectionBinding>> start, string code,
        CancellationToken token)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        attemptCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        token = attemptCancellation.Token;
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = start(uri => { opened.TrySetResult(uri); return Task.CompletedTask; }, token);
        try
        {
            var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            // Same real registered-loopback transport as AuthorizationCancellationChecks. Never
            // inspect a real browser/session: this URI and code are generated by this fixture only.
            var redirect = new Uri(Field(authorization.Query, "redirect_uri"));
            if (redirect.AbsoluteUri != DropboxProviderConfiguration.RegisteredRedirectUri ||
                redirect.Host != "127.0.0.1")
                throw new InvalidDataException("The fixture callback escaped the registered loopback endpoint.");
            var target = redirect.AbsolutePath + "?code=" + Uri.EscapeDataString(code) +
                         "&state=" + Uri.EscapeDataString(Field(authorization.Query, "state"));
            using var client = new TcpClient();
            await client.ConnectAsync(redirect.Host, redirect.Port, token);
            var stream = client.GetStream();
            var request = "GET " + target + " HTTP/1.1\r\nHost: " + redirect.Authority +
                          "\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
            await stream.FlushAsync(token);
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, token);
            return await operation.WaitAsync(token);
        }
        finally
        {
            // Observe operation failures as well as callback failures; the bounded caller token
            // is also the actual Host attempt token, rather than merely timing out an observer.
            attemptCancellation.Cancel();
            try { await operation; }
            catch (Exception) { /* Observe cleanup only; the original callback/operation failure propagates. */ }
        }
    }

    private static string Field(string encoded, string name)
    {
        foreach (var part in encoded.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (Uri.UnescapeDataString(pieces[0]) == name)
                return pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : string.Empty;
        }
        throw new InvalidDataException("The synthetic protocol input is missing a required field.");
    }

    private sealed class SyntheticCredentials : IDropboxRefreshTokenStore
    {
        private readonly State _state;
        private readonly string _reference;
        internal SyntheticCredentials(Action<string> accepted) : this(new State(accepted), WindowsDropboxRefreshTokenStore.RegisteredTarget) { }
        private SyntheticCredentials(State state, string reference) { _state = state; _reference = reference; }
        internal int Writes => _state.Writes;
        internal int Deletes => _state.Deletes;
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_state.Values.GetValueOrDefault(_reference));
        }
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Accepted(refreshToken);
            _state.Values[_reference] = refreshToken;
            _state.Writes++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Values.Remove(_reference);
            _state.Deletes++;
            return Task.CompletedTask;
        }
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) =>
            new SyntheticCredentials(_state, binding.Normalize().CredentialReference);
        private sealed class State(Action<string> accepted)
        {
            internal readonly Action<string> Accepted = accepted;
            internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
            internal int Writes;
            internal int Deletes;
        }
    }

    private sealed class SyntheticProvider : HttpMessageHandler
    {
        internal const string AccountA = "m21-synthetic-account-a";
        internal const string AccountB = "m21-synthetic-account-b";
        private readonly Dictionary<string, string> _accessAccounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _refreshAccounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _currentRefresh = new(StringComparer.Ordinal);
        private int _issued;
        internal int RefreshA { get; private set; }
        internal int RefreshB { get; private set; }
        internal int RevokeA { get; private set; }
        internal int UnexpectedRequests { get; private set; }
        internal bool FailRevokeForA { get; set; }
        internal bool RefreshUsedCurrentCredential { get; private set; } = true;

        internal void AcceptCredential(string credential)
        {
            if (!_refreshAccounts.TryGetValue(credential, out var account))
                throw new InvalidDataException("An unknown synthetic credential reached commit.");
            _currentRefresh[account] = credential;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidDataException("Missing fixture request URI.");
            if (uri.Scheme != "https" || uri.Host != "api.dropboxapi.com" || request.Method != HttpMethod.Post)
                return Unexpected();
            if (uri.AbsolutePath == "/oauth2/token")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var grant = Field(body, "grant_type");
                var refresh = grant == "refresh_token";
                string account;
                string? issuedRefresh = null;
                if (refresh)
                {
                    var credential = Field(body, "refresh_token");
                    if (!_refreshAccounts.TryGetValue(credential, out var routed)) return Unexpected();
                    account = routed;
                    RefreshUsedCurrentCredential &= _currentRefresh.GetValueOrDefault(account) == credential;
                    if (account == AccountA) RefreshA++; else RefreshB++;
                }
                else if (grant == "authorization_code")
                {
                    account = Field(body, "code") switch
                    {
                        "cache-a" => AccountA,
                        "cache-b" => AccountB,
                        _ => throw new InvalidDataException("Unknown synthetic authorization code.")
                    };
                    issuedRefresh = "synthetic-cache-refresh-" + (++_issued);
                    _refreshAccounts.Add(issuedRefresh, account);
                    // A rejected authorization may issue an unused grant. Do not model it as
                    // revoking the existing account's accepted refresh credential.
                }
                else return Unexpected();
                var access = "synthetic-cache-access-" + (++_issued);
                _accessAccounts.Add(access, account);
                // Accepted credentials are observed only at the injected store's actual write
                // boundary. Issuing a grant cannot pretend that a refused coordinator committed it.
                return Json(new
                {
                    access_token = access, refresh_token = issuedRefresh, expires_in = 3600,
                    account_id = account, scope = string.Join(' ', DropboxProviderConfiguration.RequestedScopes(true))
                });
            }
            var accessToken = request.Headers.Authorization?.Parameter;
            if (request.Headers.Authorization?.Scheme != "Bearer" || accessToken is null ||
                !_accessAccounts.TryGetValue(accessToken, out var selected)) return Unexpected();
            if (uri.AbsolutePath == "/2/users/get_current_account")
                return Json(new
                {
                    account_id = selected,
                    root_info = new { root_namespace_id = "root-" + selected, home_namespace_id = "home-" + selected },
                    name = new { display_name = "M21 synthetic account" }, email = "m21@example.invalid",
                    account_type = new Dictionary<string, string> { [".tag"] = "basic" }
                });
            if (uri.AbsolutePath == "/2/users/get_space_usage")
                return Json(new { used = 1, allocation = new Dictionary<string, object>
                    { [".tag"] = "individual", ["allocated"] = 100 } });
            if (uri.AbsolutePath == "/2/auth/token/revoke" && selected == AccountA)
            {
                RevokeA++;
                if (FailRevokeForA) throw new IOException("Synthetic offline revoke failure.");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return Unexpected();
        }

        private HttpResponseMessage Unexpected()
        {
            UnexpectedRequests++;
            throw new InvalidOperationException("Unexpected synthetic account-cache request.");
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
