using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Massdex.SyncShark;

internal static class GoogleDriveAccountFlowChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Google account flow: " + message); }

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var owner = PilotInstanceLock.Acquire(Path.Combine(root, "instance.lock"));
        var connections = new GoogleDriveConnectionStore(owner.StateAuthority);
        await connections.DeleteAsync(); // Reset only this explicitly isolated test account store.
        var store = new Credentials();
        var handler = new Endpoint();
        using var http = new HttpClient(handler);
        using var endpoint = new GoogleDriveOAuthEndpoint(http, "test-client", "synthetic-client");
        var receiver = new Receiver();
        var coordinator = new GoogleDriveConnectionCoordinator(endpoint, receiver, store, connections,
            TimeSpan.FromSeconds(2));
        var first = await coordinator.ConnectAsync(_ => Task.CompletedTask);
        Require(connections.Load()?.PermissionId == "account-one" && store.Values.Count == 1,
            "verified account persisted through the real coordinator");
        var saved = JsonSerializer.Serialize(connections.Load());
        await RejectAsync(() => coordinator.ConnectAsync(_ => Task.CompletedTask));
        Require(saved == JsonSerializer.Serialize(connections.Load()) && store.Writes == 1,
            "duplicate account rejected before custody mutation");
        handler.Identity = "account-two";
        await RejectAsync(() => coordinator.ConnectAsync(_ => Task.CompletedTask, first.ConnectionId));
        Require(saved == JsonSerializer.Serialize(connections.Load()) && store.Writes == 1,
            "wrong-account reconnect leaves exact old binding and credential unchanged");
        var second = await coordinator.ConnectAsync(_ => Task.CompletedTask);
        Require(connections.LoadActiveBindings().Count == 2 && store.Values.Count == 2,
            "additional Google account does not replace the first");
        handler.Identity = "account-three";
        store.FailNextWrite = true;
        await RejectAsync(() => coordinator.ConnectAsync(_ => Task.CompletedTask));
        Require(connections.LoadActiveBindings().Count == 2 && store.Values.Count == 2,
            "partially failed credential write rolled back without losing existing accounts");
        receiver.CancelNext = true;
        await RejectAsync(() => coordinator.ConnectAsync(_ => Task.CompletedTask));
        await coordinator.ConnectAsync(_ => Task.CompletedTask);
        Require(connections.LoadActiveBindings().Count == 3,
            "failure and cancellation release the gate so Add Account can retry");
        handler.Identity = "account-two";
        await coordinator.ConnectAsync(_ => Task.CompletedTask, second.ConnectionId);
        Require(connections.LoadActiveBindings().Count == 3 && connections.Load(first.ConnectionId) is not null,
            "reauthorizing an additional account preserves all bindings");
        await BrowserAsync();
        if (OperatingSystem.IsWindows()) await PairHostAsync(Path.Combine(root, "host"));
        else throw new PlatformNotSupportedException("Google pair-host acceptance requires Windows.");
        Console.WriteLine("PASS Google real account coordinator: multi-account connect, reconnect, wrong identity, duplicate, rollback, retry; folder paging and ancestry");
    }

    [SupportedOSPlatform("windows")]
    private static async Task PairHostAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pair host checks require Windows directory identity.");
        Directory.CreateDirectory(root);
        using var owner = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = owner.StateAuthority;
        var connections = new GoogleDriveConnectionStore(authority);
        var binding = new GoogleDriveConnectionBinding("test-client", GoogleDriveConnectionBinding.CreateAccountKey("perm-account-one"),
            "perm-account-one", "test@example.invalid", "Test", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        await connections.SaveAsync(binding);
        var credentials = new Credentials();
        await credentials.WriteAsync(binding.ConnectionId, "synthetic-refresh");
        var refreshEndpoint = new HostTokenEndpoint();
        var concurrentTokens = new GoogleDriveAccessTokenSource(credentials, refreshEndpoint, binding);
        var values = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => concurrentTokens.GetAccessTokenAsync())));
        Require(values.All(value => value == "synthetic-access") && refreshEndpoint.RefreshCalls == 1 &&
                refreshEndpoint.AccountCalls == 1,
            "32 concurrent Google requests share one verified refresh and account check");
        var server = new SyntheticDriveServer { MyDriveRootIdForTest = "account-flow-root" };
        server.AddFolder("account-flow-root", "My Drive", null);
        server.AddFolder("root-folder", "Pair", "account-flow-root");
        server.AddFolder("recovery", "Recovery", "account-flow-root");
        using var http = new HttpClient(server);
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority),
            new MemoryDropboxRefreshTokenStore("synthetic-unused-dropbox"),
            new SyncAuthorityDatabase(Path.Combine(root, "authority.db")),
            googleDriveCredentialStore: credentials, googleDriveTokenEndpoint: new HostTokenEndpoint());
        Require(await host.IsAnyAccountConnectedAsync(), "Google-only runtime passes the account gate without Dropbox");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var request = new SyncPairBindingRequest(null, "Google pair", local,
            new("Pair", "/Pair", "root-folder"), new("Recovery", "/Apps/Sync Shark/Recovery", "recovery"),
            false, 0, false, ConnectionId: binding.ConnectionId);
        var created = (await host.BindPairAsync(request)).Pair;
        Require(created.Provider == SyncProviders.GoogleDrive && created.RecoveryRoot?.RootFolderId == "recovery" &&
            created.ConnectionId == binding.ConnectionId && created.ControlNamespace is not null,
            "real host binds Google pair, account, Recovery and local staging");
        await RejectAsync(() => Task.FromResult(SyncPairStore.NormalizePair(created with { DropboxFolderId = "root" })));
        await RejectAsync(() => Task.FromResult(SyncPairStore.NormalizePair(created with { DropboxFolderId = "../other" })));
        await RejectAsync(() => Task.FromResult(SyncPairStore.NormalizePair(created with { Provider = SyncProviders.Dropbox })));
        await RejectAsync(() => Task.FromResult(SyncPairStore.NormalizePair(created with
            { RecoveryRoot = created.RecoveryRoot! with { RootFolderId = "", RootNamespaceId = "" } })));
        Require(SyncPairStore.NormalizePair(created with { Provider = SyncProviders.Dropbox,
            DropboxFolderId = "id:dropbox-root", RecoveryRoot = created.RecoveryRoot! with
            { RootFolderId = "id:dropbox-recovery" } }).Provider == SyncProviders.Dropbox,
            "Google identity correction preserves Dropbox ID validation");
        var updated = (await host.BindPairAsync(request with { ReplacingPairId = created.Id, Name = "Renamed Google pair",
            Priority = 1, ExpectedPolicyRevision = created.PolicyRevision })).Pair;
        Require(updated.Id == created.Id && updated.PolicyRevision == created.PolicyRevision + 1 &&
            updated.ControlNamespace == created.ControlNamespace, "editing keeps pair/control identity and advances policy");
        await RejectAsync(() => host.BindPairAsync(request with { ReplacingPairId = created.Id,
            ExpectedPolicyRevision = created.PolicyRevision }));
        await RejectAsync(() => host.BindPairAsync(request with { Name = "Overlapping pair" }));
        var status = await host.GetAccountStatusAsync(false, connectionId: binding.ConnectionId);
        Require(status.HasProtectedCredential, "Google account status reads its own protected credential");
        await host.DisconnectGoogleDriveAsync(binding.ConnectionId);
        Require(!await host.IsAnyAccountConnectedAsync() && (await host.LoadPairsAsync()).Pairs.Single().Id == created.Id &&
            connections.Load(binding.ConnectionId) is not null, "disconnect removes only credential; pair and account identity survive");
        Console.WriteLine("PASS real Google runtime host pair binding, Recovery, edits, stale-policy/overlap guards and disconnect retention");
    }

    private sealed class HostTokenEndpoint : IGoogleDriveTokenEndpoint
    {
        internal int RefreshCalls, AccountCalls;
        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken, CancellationToken token = default)
        {
            Interlocked.Increment(ref AccountCalls);
            return Task.FromResult(new GoogleDriveAccountIdentity("perm-account-one", "test@example.invalid", "Test", ""));
        }
        public async Task<GoogleDriveTokenResult> RefreshAsync(string id, string refresh, CancellationToken token = default)
        {
            Interlocked.Increment(ref RefreshCalls);
            await Task.Delay(15, token);
            return new GoogleDriveTokenResult("synthetic-access", DateTime.UtcNow.AddHours(1),
                [GoogleDriveProviderConfiguration.DriveScope]);
        }
    }

    private static async Task BrowserAsync()
    {
        var handler = new Folders();
        using var http = new HttpClient(handler);
        var binding = new GoogleDriveConnectionBinding("test-client", GoogleDriveConnectionBinding.CreateAccountKey("account-one"),
            "account-one", "test@example.invalid", "Test", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        var browser = new GoogleDriveFolderBrowserClient(http, new Tokens(), binding);
        var first = await browser.ListFoldersAsync();
        Require(first.Folders.Single().Path == "/Parent" && first.ContinueCursor is not null,
            "real root ID mapped and first page retained");
        Require(handler.AccountReads == 1,
            "the disabled browser verifies the signed-in account exactly once before any folder read");
        var next = await browser.ContinueFoldersAsync(first.ContinueCursor!);
        Require(next.Folders.Single().Path == "/Parent/Child" && handler.Queries[0] == handler.Queries[1],
            "continuation preserves exact query and nested full path");
        var search = await browser.SearchFoldersAsync("O'Brien\\test");
        await browser.ContinueFolderSearchAsync(search.ContinueCursor!);
        Require(handler.Queries[2] == handler.Queries[3] && handler.Queries[2].Contains("O\\'Brien\\\\test"),
            "search escaping and continuation keep original search terms");
        await RejectAsync(() => new GoogleDriveFolderBrowserClient(http, new Tokens(), binding)
            .ContinueFoldersAsync(first.ContinueCursor!));
        handler.Incomplete = true;
        var writes = handler.Writes;
        await RejectAsync(() => browser.CreateFolderAsync("root", "new"));
        Require(handler.Writes == writes, "incomplete existence search cannot authorise folder creation");
        handler.Incomplete = false; handler.Orphan = true;
        await RejectAsync(() => browser.ListFoldersAsync());
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected rejection was not produced.");
    }

    private sealed class Receiver : IDropboxAuthorizationReceiver
    {
        internal bool CancelNext;
        public Task<DropboxLoopbackSession> ReceiveAsync(Uri uri, string state, CancellationToken token)
        {
            if (CancelNext) { CancelNext = false; return Task.FromCanceled<DropboxLoopbackSession>(new CancellationToken(true)); }
            return Task.FromResult(new DropboxLoopbackSession("synthetic-code"));
        }
    }
    private sealed class Credentials : IGoogleDriveRefreshTokenStore
    {
        internal Dictionary<string, string> Values = [];
        internal int Writes; internal bool FailNextWrite;
        public Task<string?> ReadAsync(string id, CancellationToken token = default) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task WriteAsync(string id, string value, CancellationToken token = default)
        {
            Values[id] = value; Writes++;
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Synthetic write failure"); }
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken token = default) { Values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class Endpoint : HttpMessageHandler
    {
        internal string Identity = "account-one";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Method == HttpMethod.Get
                ? JsonSerializer.Serialize(new { user = new { permissionId = Identity, emailAddress = "test@example.invalid", displayName = "Test" } })
                : JsonSerializer.Serialize(new { access_token = "synthetic-access", refresh_token = "synthetic-refresh", token_type = "Bearer",
                    expires_in = 3600, scope = GoogleDriveProviderConfiguration.DriveScope });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class Tokens : IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken token = default) => Task.FromResult("synthetic-access"); }
    private sealed class Folders : HttpMessageHandler
    {
        internal List<string> Queries = [];
        internal bool Incomplete, Orphan; internal int Writes; internal int AccountReads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) { Writes++; throw new InvalidOperationException("Unexpected folder mutation"); }
            var uri = request.RequestUri!;
            string body;
            if (uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal))
            {
                AccountReads++;
                body = JsonSerializer.Serialize(new { user = new { permissionId = "account-one" } });
            }
            else if (uri.AbsolutePath.EndsWith("/root", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { id = "real-root", mimeType = GoogleDrivePilotClient.FolderMimeType });
            else if (uri.AbsolutePath.EndsWith("/orphan-parent", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { id = "orphan-parent", name = "Orphan", mimeType = GoogleDrivePilotClient.FolderMimeType });
            else
            {
                var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                    .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                Queries.Add(query["q"]);
                var next = query.ContainsKey("pageToken");
                body = JsonSerializer.Serialize(new { nextPageToken = next ? null : "page-two", incompleteSearch = Incomplete,
                    files = new[] { new { id = next ? "child" : "parent", name = next ? "Child" : "Parent",
                        mimeType = GoogleDrivePilotClient.FolderMimeType,
                        parents = new[] { Orphan ? "orphan-parent" : next ? "parent" : "real-root" } } } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
