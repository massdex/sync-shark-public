using Massdex.SyncShark;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class AccountRoutingChecks
{
    internal static async Task RunAsync(string root)
    {
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new DropboxConnectionStore(instance.StateAuthority);
        var credentials = new AccountCredentials();
        var handler = new AccountTransport(); using var client = new HttpClient(handler);
        var coordinator = new DropboxConnectionCoordinator(client, new FixtureAuthorizationReceiver(), credentials, store);
        var first = await coordinator.ConnectAsync(_ => Task.CompletedTask);
        Require(first.CredentialReference == WindowsDropboxRefreshTokenStore.RegisteredTarget, "Primary credential target remains unchanged");
        await AuthorizationRecoveryAsync(client, credentials, store, first);
        handler.AuthorizeAccount = "B";
        var second = await coordinator.ConnectAsync(_ => Task.CompletedTask, addAccount: true);
        Require(store.LoadActiveBindings().Count == 2 && store.Load()!.AccountId == first.AccountId, "Two active accounts coexist without replacing the primary");
        Require(second.CredentialReference == WindowsDropboxRefreshTokenStore.AccountTarget(second.ConnectionId), "Additional account has its registered isolated target");
        Require(store.Load(first.ConnectionId)!.AccountId != store.Load(second.ConnectionId)!.AccountId, "Exact connection lookup returns the requested account");
        var tokenA = new RefreshingDropboxAccessTokenSource(client, credentials, first);
        var tokenB = new RefreshingDropboxAccessTokenSource(client, credentials, second);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => tokenA.GetAccessTokenAsync())));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => tokenB.GetAccessTokenAsync())));
        Require(handler.RefreshedAccounts.SequenceEqual(new[] { "A", "B" }), "Production refresh path reads each account's own credential");
        Require((await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => tokenA.GetAccessTokenAsync())))
                .All(value => value == "fixture-access-A") && handler.RefreshedAccounts.Count == 2,
            "Concurrent file requests single-flight each refresh and retain the correct cached account");
        await RejectAsync(() => coordinator.ConnectAsync(_ => Task.CompletedTask, addAccount: true), "Duplicate account authorization is rejected before replacing credentials");
        handler.AuthorizeAccount = "A";
        await RejectAsync(() => coordinator.ConnectAsync(_ => Task.CompletedTask, reauthorize: true, connectionId: second.ConnectionId), "Wrong-account reconnect cannot reassign an existing connection");
        handler.MisrouteRefresh = true;
        await RejectAsync(() => new RefreshingDropboxAccessTokenSource(client, credentials, second).GetAccessTokenAsync(), "Wrong-account token response is rejected before any file request");
        handler.OmitTokenAccountId = true;
        await RejectAsync(() => new RefreshingDropboxAccessTokenSource(client, credentials, second).GetAccessTokenAsync(), "Account verification also rejects a misrouted refresh without token account_id");
        handler.MisrouteRefresh = false;
        await credentials.ForConnection(second).DeleteAsync();
        var refreshCount = handler.RefreshedAccounts.Count;
        await RejectAsync(() => new RefreshingDropboxAccessTokenSource(client, credentials, second).GetAccessTokenAsync(), "Missing secondary credential never falls back to primary");
        Require(handler.RefreshedAccounts.Count == refreshCount && await credentials.ForConnection(first).ReadAsync() is not null,
            "Secondary disconnect leaves primary credential untouched");
        await RejectAsync(() => store.SaveAsync(second with { CredentialReference = first.CredentialReference }), "Cross-account credential reference is rejected at persistence");
        Require(store.LoadActiveBindings().Count == 2, "Rejection preserves both account identities and reconnect paths");
        handler.OmitTokenAccountId = false; handler.AuthorizeAccount = "A"; handler.IncludeFileRequests = true;
        var upgraded = await coordinator.ConnectAsync(_ => Task.CompletedTask, reauthorize: true,
            connectionId: first.ConnectionId, includeFileRequests: true);
        Require(DropboxProviderConfiguration.HasFileRequestAccess(upgraded.Scopes) && !DropboxProviderConfiguration.HasFileRequestAccess(store.Load(second.ConnectionId)!.Scopes),
            "File-request consent upgrades only the selected account");
        var reconnected = await coordinator.ConnectAsync(_ => Task.CompletedTask, reauthorize: true, connectionId: first.ConnectionId);
        Require(DropboxProviderConfiguration.HasFileRequestAccess(reconnected.Scopes), "Ordinary reconnect preserves granted file-request permissions");
        await new RefreshingDropboxAccessTokenSource(client, credentials, reconnected).GetAccessTokenAsync();
        Require(handler.LastRefreshScope?.Contains("file_requests.write", StringComparison.Ordinal) == true,
            "Token refresh retains the selected account's extended scope set");
        Require(!DropboxProviderConfiguration.IsSupportedGrant(first.Scopes.Append("file_requests.read").ToArray()) &&
            !DropboxProviderConfiguration.IsSupportedGrant(first.Scopes.Append("unexpected.scope").ToArray()),
            "Partial or unknown permission sets are rejected, while legacy accounts remain valid");
        using var nativeFirst = new NativeScopeProbe(first);
        using var nativeSecond = new NativeScopeProbe(second);
        Require(nativeFirst.Target != nativeSecond.Target, "Registered native credential target derivation is distinct");
        await CloudRootIsolationAsync(root);
        await AccountRecoveryMigrationAsync(root);
    }

    private static async Task AccountRecoveryMigrationAsync(string root)
    {
        var path = Path.Combine(root, "predecessor-v3.db");
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using (var old = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString()))
        {
            await old.OpenAsync(); await using var cmd = old.CreateCommand();
            cmd.CommandText = SyncAuthoritySchema.Sql + SyncAuthoritySchemaSupplement.Sql + SyncAuthoritySchemaMigration3.Sql + """
                INSERT INTO installation VALUES('migration-installation','generation','2026-09-14T00:00:00Z');
                INSERT INTO schema_migration VALUES(3,'2026-09-14T00:00:00Z','old-source');
                INSERT INTO provider_account VALUES('migration-provider','migration-installation','dropbox','account','namespace','home','Account',1,'2026-09-14T00:00:00Z','active');
                INSERT INTO recovery_root_binding VALUES('kept-recovery','migration-installation','migration-provider','dropbox','/Recovery','/Recovery','{"folderId":"id:retained","rootNamespaceId":"namespace"}',1,'active','2026-09-14T00:00:00Z');
                PRAGMA user_version=3;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var db = new SyncAuthorityDatabase(path);
        SyncAuthorityDatabase.MigrationBoundaryForTest = boundary => { if (boundary == "v4-schema-applied-before-version-commit") throw new IOException("Injected migration interruption"); };
        try { await db.InitializeAsync("migration-installation", "new-profile", "new-source", "{}"); throw new InvalidOperationException("Expected interruption"); }
        catch (IOException error) when (error.Message == "Injected migration interruption") { }
        finally { SyncAuthorityDatabase.MigrationBoundaryForTest = null; }
        async Task<long> Number(string file, string sql) {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        Require(await Number(path, "PRAGMA user_version;") == 3 && await Number(path, "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='kept-recovery';") == 1,
            "Interrupted account-recovery migration rolls back its schema and retains the original recovery row");
        Require(File.Exists(db.Version3RollbackDatabasePath) && await Number(db.Version3RollbackDatabasePath, "PRAGMA user_version;") == 3 &&
            await Number(db.Version3RollbackDatabasePath, "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='kept-recovery';") == 1,
            "Version-3 recovery rollback is preserved and readable before migration");
        var retainedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(db.Version3RollbackDatabasePath)));
        await using (var changed = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString()))
        {
            await changed.OpenAsync(); await using var command = changed.CreateCommand();
            command.CommandText = "UPDATE recovery_root_binding SET display_path='/Renamed Recovery' WHERE recovery_root_binding_id='kept-recovery';";
            await command.ExecuteNonQueryAsync();
        }
        await RejectAsync(() => db.InitializeAsync("migration-installation", "new-profile", "new-source", "{}"),
            "Same-installation rollback with different retained rows is rejected before migration");
        Require(await Number(path, "PRAGMA user_version;") == 3 && retainedHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(db.Version3RollbackDatabasePath))),
            "Rejected stale rollback preserves the user database and original rollback bytes");
        await using (var restored = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString()))
        {
            await restored.OpenAsync(); await using var command = restored.CreateCommand();
            command.CommandText = "UPDATE recovery_root_binding SET display_path='/Recovery' WHERE recovery_root_binding_id='kept-recovery';";
            await command.ExecuteNonQueryAsync();
        }
        await db.InitializeAsync("migration-installation", "new-profile", "new-source", "{}");
        Require(await Number(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version && await Number(path, "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='kept-recovery' AND generation=1 AND display_path='/Recovery';") == 1,
            "The account migration converges to the current schema version and preserves recovery identity, path and generation");
        await db.RegisterProviderAccountAsync("second-provider", "migration-installation", "second-account", "second-namespace", "second-home", "Second", 1);
        await db.RegisterDropboxRecoveryRootAsync("second-recovery", "migration-installation", "second-provider", "/Recovery", "id:second", "second-namespace", 1);
        Require(await Number(path, "SELECT count(*) FROM recovery_root_binding;") == 2, "Migrated database accepts the second account's independent recovery root");
        await db.VerifyHealthAsync(); Require(true, "Migrated schema and foreign keys pass actual database health checks");
    }

    private static async Task CloudRootIsolationAsync(string root)
    {
        var db = new SyncAuthorityDatabase(Path.Combine(root, "root-isolation.db"));
        await db.InitializeAsync("account-root-installation", "account-root-profile", "account-root-check", "{}");
        foreach (var pair in new[] { "a", "b", "same", "shared", "unbound", "local-a", "local-b" }) await db.RegisterPairAsync(pair, 1);
        foreach (var (id, ns) in new[] { ("account-a", "namespace-a"), ("account-b", "namespace-b"), ("account-shared", "namespace-a") })
            await db.RegisterProviderAccountAsync(id, "account-root-installation", "dbid:" + id, ns, "home-" + id, id, 1);
        await db.RegisterDropboxRootBindingAsync("root-a", "a", 1, "account-root-installation", "account-a", "namespace-a", "id:a", "/Documents");
        await db.RegisterDropboxRootBindingAsync("root-b", "b", 1, "account-root-installation", "account-b", "namespace-b", "id:b", "/Documents");
        Require(true, "Equal Dropbox paths coexist in two verified account namespaces");
        await RejectAsync(() => db.RegisterDropboxRootBindingAsync("root-same", "same", 1, "account-root-installation", "account-a", "namespace-a", "id:child", "/Documents/Child"),
            "Parent/child overlap is still rejected inside one account namespace");
        await RejectAsync(() => db.RegisterDropboxRootBindingAsync("root-shared", "shared", 1, "account-root-installation", "account-shared", "namespace-a", "id:shared", "/Documents"),
            "A shared namespace is not duplicated through a different account");
        await RejectAsync(() => db.RegisterDropboxRootBindingAsync("root-unbound", "unbound", 1, "account-root-installation", "account-b", "invented-namespace", "id:x", "/Documents"),
            "An unverified namespace cannot bypass the overlap fence");
        await db.RegisterDropboxRecoveryRootAsync("recovery-a", "account-root-installation", "account-a", "/Recovery", "id:recovery-a", "namespace-a", 1);
        await db.RegisterDropboxRecoveryRootAsync("recovery-b", "account-root-installation", "account-b", "/Recovery", "id:recovery-b", "namespace-b", 1);
        Require(true, "Identical dedicated recovery paths coexist across distinct accounts");
        await RejectAsync(() => db.RegisterDropboxRootBindingAsync("root-recovery-overlap", "same", 1, "account-root-installation", "account-a", "namespace-a", "id:inside-recovery", "/Recovery/Child"),
            "Recovery authority still excludes pair roots in the same namespace");
        await RejectAsync(() => db.RegisterRootBindingAsync("root-legacy", "unbound", "dropbox", 1, "{}", "/Documents"),
            "Legacy unbound cloud registration remains conservatively overlapping");
        var local = Path.Combine(root, "local-pair");
        await db.RegisterRootBindingAsync("local-root-a", "local-a", "local", 1, "{}", local);
        await RejectAsync(() => db.RegisterRootBindingAsync("local-root-b", "local-b", "local", 1, "{}", Path.Combine(local, "Child")),
            "Local folders retain the global physical-path overlap fence");
    }
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }

    private static async Task AuthorizationRecoveryAsync(HttpClient client, AccountCredentials credentials,
        DropboxConnectionStore store, DropboxConnectionBinding first)
    {
        var receiver = new RecoveryReceiver();
        var coordinator = new DropboxConnectionCoordinator(client, receiver, credentials, store);
        var before = JsonSerializer.Serialize(store.LoadActiveBindings());
        using var cancel = new CancellationTokenSource();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = coordinator.ConnectAsync(_ => { opened.SetResult(); return Task.CompletedTask; },
            reauthorize: true, cancellationToken: cancel.Token, connectionId: first.ConnectionId);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await RejectAsync(() => new DropboxConnectionCoordinator(client, receiver, credentials, store)
            .ConnectAsync(_ => throw new InvalidOperationException("A second browser must not open"), addAccount: true),
            "A competing connection fails immediately instead of silently waiting");
        cancel.Cancel();
        try { await waiting; throw new InvalidOperationException("Cancelled callback unexpectedly succeeded"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS Cancel closes the real callback listener"); }
        Require(before == JsonSerializer.Serialize(store.LoadActiveBindings()), "Cancelling leaves saved connections unchanged");
        var timeout = new DropboxConnectionCoordinator(client, receiver, credentials, store, TimeSpan.FromMilliseconds(150));
        try
        {
            await timeout.ConnectAsync(_ => Task.CompletedTask, reauthorize: true, connectionId: first.ConnectionId);
            throw new InvalidOperationException("Abandoned callback unexpectedly succeeded");
        }
        catch (TimeoutException) { Console.WriteLine("PASS Abandoned browser authorization times out"); }
        Require(before == JsonSerializer.Serialize(store.LoadActiveBindings()), "Timeout keeps the original account binding");
        try
        {
            await coordinator.ConnectAsync(_ => throw new IOException("Fixture browser launch failed"), reauthorize: true, connectionId: first.ConnectionId);
            throw new InvalidOperationException("Browser launch failure unexpectedly succeeded");
        }
        catch (IOException) { Console.WriteLine("PASS Browser launch failure cleans up its listener"); }
        var result = await coordinator.ConnectAsync(async _ =>
        {
            using var callbackClient = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await callbackClient.GetAsync(receiver.Callback);
            response.EnsureSuccessStatusCode();
        }, reauthorize: true, connectionId: first.ConnectionId);
        Require(result.AccountId == first.AccountId && store.LoadActiveBindings().Count == 1,
            "Fresh real callback succeeds on the same port after cancel, timeout and browser failure");
    }

    private sealed class RecoveryReceiver : IDropboxAuthorizationReceiver
    {
        private readonly int _port;
        public Uri Callback { get; private set; } = null!;
        public RecoveryReceiver()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            _port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        }
        public Task<DropboxLoopbackSession> ReceiveAsync(Uri redirectUri, string state, CancellationToken token = default)
        {
            var redirect = new UriBuilder(redirectUri) { Port = _port }.Uri;
            Callback = new UriBuilder(redirect) { Query = "code=fixture-code&state=" + Uri.EscapeDataString(state) }.Uri;
            return new DropboxLoopbackReceiver().ReceiveAsync(redirect, state, token);
        }
    }
    private static async Task RejectAsync(Func<Task> action, string label)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { Console.WriteLine("PASS " + label); return; } throw new InvalidOperationException(label); }
    private sealed class NativeScopeProbe : IDisposable
    {
        public string Target { get; }
        public NativeScopeProbe(DropboxConnectionBinding binding)
        { _ = new WindowsDropboxRefreshTokenStore().ForConnection(binding); Target = binding.CredentialReference; }
        public void Dispose() { }
    }
    private sealed class AccountCredentials : IDropboxRefreshTokenStore
    {
        private readonly Dictionary<string, string> _values;
        private readonly string _target;
        public AccountCredentials() : this(new(), WindowsDropboxRefreshTokenStore.RegisteredTarget) { }
        private AccountCredentials(Dictionary<string, string> values, string target) { _values = values; _target = target; }
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => new AccountCredentials(_values, binding.CredentialReference);
        public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(_target));
        public Task WriteAsync(string value, CancellationToken ct = default) { _values[_target] = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct = default) { _values.Remove(_target); return Task.CompletedTask; }
    }
    private sealed class AccountTransport : HttpMessageHandler
    {
        public string AuthorizeAccount { get; set; } = "A";
        public bool MisrouteRefresh { get; set; }
        public bool OmitTokenAccountId { get; set; }
        public bool IncludeFileRequests { get; set; }
        public string? LastRefreshScope { get; private set; }
        public List<string> RefreshedAccounts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token"))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var refresh = body.Contains("grant_type=refresh_token", StringComparison.Ordinal);
                if (refresh) LastRefreshScope = Uri.UnescapeDataString(body.Replace('+', ' '));
                var account = refresh ? body.Contains("fixture-refresh-B", StringComparison.Ordinal) ? "B" : "A" : AuthorizeAccount;
                if (refresh) RefreshedAccounts.Add(account);
                if (refresh && MisrouteRefresh) account = "A";
                var payload = new Dictionary<string, object> { ["access_token"] = "fixture-access-" + account,
                    ["refresh_token"] = "fixture-refresh-" + account, ["expires_in"] = 3600,
                    ["scope"] = string.Join(' ', DropboxProviderConfiguration.RequestedScopes(IncludeFileRequests)) };
                if (!OmitTokenAccountId) payload["account_id"] = "dbid:" + account;
                result = payload;
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/users/get_current_account"))
            {
                var account = request.Headers.Authorization?.Parameter?.EndsWith('B') == true ? "B" : "A";
                result = new { account_id = "dbid:" + account, root_info = new { root_namespace_id = "root-" + account, home_namespace_id = "home-" + account } };
            }
            else throw new InvalidOperationException("Unexpected provider effect during account isolation check.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
}
