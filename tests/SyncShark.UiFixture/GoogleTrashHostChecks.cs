using Massdex.SyncShark;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SyncShark.UiFixture;

// Real persisted authority and running-app CLI adapter; only provider HTTP and credentials are synthetic.
internal static class GoogleTrashHostChecks
{
    internal static async Task RunAsync(string root)
    {
        var previousClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try { await CheckAsync(fixture); }
        finally
        {
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = previousClient;
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static async Task CheckAsync(string root)
    {
        using var owner = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = owner.StateAuthority;
        var binding = new GoogleDriveConnectionBinding("test-client",
            GoogleDriveConnectionBinding.CreateAccountKey("perm-account-one"), "perm-account-one",
            "test@example.invalid", "Test", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        await new GoogleDriveConnectionStore(authority).SaveAsync(binding);
        var server = new Server();
        using var http = new HttpClient(server);
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority),
            new UnusedDropboxCredentials(),
            new SyncAuthorityDatabase(Path.Combine(root, "authority.db")),
            googleDriveCredentialStore: new Credentials(), googleDriveTokenEndpoint: new TokenEndpoint());
        await host.LoadPairsAsync();
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("google-trash-fixture", "Google Trash fixture", local,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/Pair", "root-folder",
            ConnectionId: binding.ConnectionId, Provider: SyncProviders.GoogleDrive);
        await host.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await host.Authority.RetainPairDefinitionAsync(pair, true);
        await using var cli = new CliAppRuntime(host, authority, () => false, () => false, () => { },
            () => Task.CompletedTask);
        async Task<object> Command(params string[] args) => await SyncCliRegistry.DispatchAsync(cli,
            SyncCliRegistry.Parse(["--cli", .. args]), TextWriter.Null, CancellationToken.None);

        var store = new GoogleDriveConnectionStore(authority);
        var second = new GoogleDriveConnectionBinding("test-client",
            GoogleDriveConnectionBinding.CreateAccountKey("perm-account-two"), "perm-account-two",
            "other@example.invalid", "Other", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        await store.SaveAsync(second);
        Check(!store.Load(binding.ConnectionId)!.SharedDrivesEnabled, "shared drives default off per account");
        await Command("accounts", "shared-drives", "--account", binding.ConnectionId, "--enable", "--yes");
        Check(store.Load(binding.ConnectionId)!.SharedDrivesEnabled && !store.Load(second.ConnectionId)!.SharedDrivesEnabled &&
            host.LoadProviderConnections().Single(c => c.ConnectionId == binding.ConnectionId).Capabilities.SharedDrives,
            "real CLI-to-host persists exact-account shared-drive opt-in and capability");
        await store.SaveAsync(binding with { DisplayName = "Freshly verified" });
        Check(store.Load(binding.ConnectionId)!.SharedDrivesEnabled,
            "older in-flight health snapshot cannot silently revert shared-drive preference");
        await host.SetGoogleSharedDrivesEnabledAsync(second.ConnectionId, true);
        await store.SaveAsync(second with { DisplayName = "Other verified" });
        Check(store.Load(second.ConnectionId)!.SharedDrivesEnabled,
            "additional-account refresh preserves its independently selected preference");
        await Command("accounts", "shared-drives", "--account", binding.ConnectionId, "--disable", "--yes");
        await Refused(() => host.SetGoogleSharedDrivesEnabledAsync("foreign-account", true));
        Check(!store.Load(binding.ConnectionId)!.SharedDrivesEnabled && store.Load(second.ConnectionId)!.SharedDrivesEnabled &&
            server.Requests == 0, "opt-out, foreign-account refusal and preference updates have no provider/credential effects");

        var page = await host.LoadRecoveryCatalogPageAsync(pair.Id, includeDropboxDeletedItems: true);
        var item = page.Items.Single();
        Check(item.Source == SyncRecoverySource.CloudTrash && item.Key == "trash:deleted-file" &&
            item.CanRestore && !item.CanRestoreCopy, "host exposes exact original-only Google Trash entry");
        var preview = await host.PreviewRecoveryAsync(item, false);
        Check(preview.TargetRelativePath == "notes.txt", "host preview preserves reviewed cloud destination");
        await Refused(() => host.PreviewRecoveryAsync(item, true));
        await Refused(() => host.PreviewRecoveryAsync(item with { PairId = "foreign-pair" }, false));
        await Refused(() => host.PreviewRecoveryAsync(item with { Key = "trash:other" }, false));
        await Refused(() => host.PreviewRecoveryAsync(item with { Revision = "999" }, false));
        Check(server.Writes == 0, "copy and tampered previews have no provider effect");
        var listing = (SyncCliRecoveryListData)await Command("recovery", "list", "--pair", pair.Id, "--deleted");
        Check(listing.Items.Single().Key == item.Key && listing.Items.Single().CanRestore &&
            !listing.Items.Single().CanRestoreCopy && listing.Warnings?.Count == 1,
            "registry and running-app adapter retain Trash identity and incomplete-search warning");
        var revisions = (SyncCliRecoveryListData)await Command("recovery", "revisions", "--pair", pair.Id,
            "--account", binding.ConnectionId, "--path", "versioned.bin");
        var revision = revisions.Items.Single();
        Check(revision.Key == "revision:old-version" && revision.PairId == pair.Id && revision.CanRestore &&
            revision.CanRestoreCopy && revision.ContentHash == "md5:" + new string('b', 32) &&
            server.Writes == 0, "real CLI-to-host lists Google binary revisions and restore capabilities without effects");
        await Refused(() => Command("recovery", "restore", "--pair", pair.Id, "--key", item.Key, "--copy", "--yes"));
        Check(server.Writes == 0, "CLI copy refusal cannot untrash the original");
        var restored = (SyncCliRecoveryActionData)await Command("recovery", "restore", "--pair", pair.Id,
            "--key", item.Key, "--yes");
        Check(restored.Key == item.Key && server.Writes == 1 && !server.Trashed && !host.HasActiveRun,
            "real CLI-to-host authority completes one conditional untrash and releases run ownership");
        Check((await host.LoadRecoveryCatalogPageAsync(pair.Id, includeDropboxDeletedItems: true)).Items.Count == 0,
            "restored item disappears from refreshed Recovery");
        await Refused(() => Command("recovery", "restore", "--pair", pair.Id, "--key", item.Key, "--yes"));
        Check(server.Writes == 1 && !File.Exists(Path.Combine(local, "notes.txt")),
            "repeat refuses without second cloud write or unrequested local download");
        server.Account = "foreign-account";
        await Refused(() => host.LoadRecoveryCatalogPageAsync(pair.Id, includeDropboxDeletedItems: true));
        Check(server.Writes == 1, "fresh account mismatch refuses without an effect");
    }

    private static void Check(bool value, string description)
    { if (!value) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); }
    private static async Task Refused(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or SyncCliTargetException) { return; }
        throw new InvalidOperationException("Expected safe refusal.");
    }
    private sealed class UnusedDropboxCredentials : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken token = default) =>
            throw new InvalidOperationException("Google Recovery must not request Dropbox credentials.");
        public Task WriteAsync(string value, CancellationToken token = default) => throw new InvalidOperationException("No credential mutation allowed.");
        public Task DeleteAsync(CancellationToken token = default) => throw new InvalidOperationException("No credential mutation allowed.");
    }
    private sealed class Credentials : IGoogleDriveRefreshTokenStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken token = default) => Task.FromResult<string?>("synthetic-refresh");
        public Task WriteAsync(string id, string value, CancellationToken token = default) => throw new InvalidOperationException("No credential mutation allowed.");
        public Task DeleteAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("No credential mutation allowed.");
    }
    private sealed class TokenEndpoint : IGoogleDriveTokenEndpoint
    {
        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string access, CancellationToken token = default) =>
            Task.FromResult(new GoogleDriveAccountIdentity("perm-account-one", "test@example.invalid", "Test", ""));
        public Task<GoogleDriveTokenResult> RefreshAsync(string id, string refresh, CancellationToken token = default) =>
            Task.FromResult(new GoogleDriveTokenResult("synthetic-access", DateTime.UtcNow.AddHours(1),
                [GoogleDriveProviderConfiguration.DriveScope]));
    }
    private sealed class Server : HttpMessageHandler
    {
        internal string Account = "perm-account-one";
        internal bool Trashed = true;
        internal int Writes;
        internal int Requests;
        private int Version => Trashed ? 4 : 5;
        private static object VersionedFile() => new { id = "versioned-file", name = "versioned.bin",
            mimeType = "application/octet-stream", parents = new[] { "root-folder" }, trashed = false,
            size = "5", sha256Checksum = new string('a', 64), version = "8",
            capabilities = new { canDownload = true } };
        private object Entry() => new { id = "deleted-file", name = "notes.txt", mimeType = "application/octet-stream",
            parents = new[] { "root-folder" }, trashed = Trashed, explicitlyTrashed = Trashed,
            size = "5", sha256Checksum = new string('a', 64), version = Version.ToString(),
            capabilities = new { canUntrash = true, canDownload = true } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            object response;
            if (request.Method == HttpMethod.Patch && path == "/drive/v2/files/deleted-file")
            {
                Check(Trashed && request.Headers.IfMatch.Single().Tag == "\"deleted-file-4\"", "provider receives exact strong If-Match guard");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Check(body.RootElement.GetProperty("labels").GetProperty("trashed").ValueKind == JsonValueKind.False,
                    "provider receives v2 labels.trashed=false");
                Writes++; Trashed = false; response = new { id = "deleted-file" };
            }
            else if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Unexpected provider mutation.");
            else if (path == "/drive/v3/about") response = new { user = new { permissionId = Account } };
            else if (path == "/drive/v3/files/root-folder") response = new { id = "root-folder", name = "Pair",
                mimeType = "application/vnd.google-apps.folder", trashed = false, version = "1" };
            else if (path == "/drive/v3/files/deleted-file") response = Entry();
            else if (path == "/drive/v3/files/versioned-file") response = VersionedFile();
            else if (path == "/drive/v3/files/versioned-file/revisions") response = new { revisions = new[] {
                new { id = "old-version", mimeType = "application/octet-stream", size = "5",
                    md5Checksum = new string('b', 32), keepForever = false } } };
            else if (path == "/drive/v2/files/deleted-file") response = new { id = "deleted-file",
                version = Version.ToString(), etag = "\"deleted-file-" + Version + "\"",
                mimeType = "application/octet-stream", labels = new { trashed = Trashed },
                parents = new[] { new { id = "root-folder" } } };
            else if (path == "/drive/v3/files")
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                response = new { files = query.Contains("name = 'versioned.bin'", StringComparison.Ordinal)
                    ? new[] { VersionedFile() }
                    : Trashed && query.Contains("trashed = true", StringComparison.Ordinal)
                        ? new[] { Entry() } : Array.Empty<object>(),
                    incompleteSearch = query.Contains("trashed = true", StringComparison.Ordinal) };
            }
            else throw new InvalidOperationException("Unexpected provider route: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }
    }
}
