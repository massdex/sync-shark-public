using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Real CFAPI and real Google transport; only the provider's HTTP input is synthetic.
// Owns one new child of the admitted acceptance allocation, never an existing user root.
internal static class GoogleNativeCloudFilesChecks
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 8193).Select(i => (byte)(i % 251)).ToArray();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    internal static async Task RunAsync(string parent, bool approvedLocation = false)
    {
        parent = Path.GetFullPath(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Use the admitted existing acceptance allocation.");
        for (var ancestor = parent; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            Require((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0, "No redirected fixture ancestor");
        var volume = new DriveInfo(Path.GetPathRoot(parent)!);
        Require(volume.DriveFormat == "NTFS" && volume.AvailableFreeSpace > 20L * 1024 * 1024 * 1024,
            "Native fixture uses NTFS with the required free-space reserve");
        var folder = approvedLocation
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "sync-shark-cf-40cfa42f-f91c-433c-90cf-4dd5bd66147b")
            : Path.Combine(parent, "google-cf-" + Guid.NewGuid().ToString("N"));
        Require(!Directory.Exists(folder) && !File.Exists(folder), "Exact fixture allocation is absent");
        for (var ancestor = Path.GetDirectoryName(folder); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            Require((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0, "Exact fixture ancestor is ordinary");
        Require(!Windows.Storage.Provider.StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item =>
        {
            var existing = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path.Path));
            return folder.Equals(existing, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(existing + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                existing.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }), "Native registered-root inventory has no overlap");
        Directory.CreateDirectory(folder);
        PilotDirectoryIdentity identity;
        using (var root = PilotDirectoryLease.Open(folder)) identity = root.Identity;
        var binding = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("fixture-account"), "fixture-account", "fixture@example.invalid", "Fixture",
            [GoogleDriveProviderConfiguration.DriveScope], CredentialReference: "synthetic-only").Normalize();
        var pair = new SyncPair("fixture-" + Guid.NewGuid().ToString("N"), "Google native fixture", folder,
            identity.VolumeSerialNumber, identity.FileId, "/Fixture", "fixture-root", FilesOnDemandEnabled: true,
            Provider: SyncProviders.GoogleDrive, ConnectionId: binding.ConnectionId, DefaultStorage: SyncDirectoryStorage.CloudOnly);
        using var protocol = new Protocol(); using var http = new HttpClient(protocol);
        var remote = new GoogleDrivePilotClient(http, new Tokens(), binding, "fixture-root", "/Fixture")
            { VerifiedRangeStagingRootForTest = Path.Combine(parent, "native-google-stage") };
        var expected = new PilotRemoteEntry("sample.bin", PilotEntryKind.File, "fixture-file",
            JsonSerializer.Serialize(new[] { "fixture-file", "1" }), Bytes.Length, Hash);
        WindowsCloudFilesProvider? provider = new(pair, remote, Path.Combine(parent, "native-google-stage"));
        var file = Path.Combine(folder, expected.RelativePath);
        try
        {
            provider.RegisterAndConnect();
            Require(WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id, SyncProviders.GoogleDrive), "Native root is bound to Google, not Dropbox");
            var online = provider.CreatePlaceholder(expected);
            Require(online.Identity.IsBoundTo(pair.Id, SyncProviders.GoogleDrive) &&
                online.State.Availability == SyncCloudFileAvailability.OnlineOnly, "Exact Google identity starts online-only");
            var pinned = await provider.SetAvailabilityAsync(expected.RelativePath, true, false);
            Require(pinned.Failures.Count == 0 && protocol.MediaCalls > 0 &&
                (await File.ReadAllBytesAsync(file)).SequenceEqual(Bytes), "Real Windows hydration receives only verified Google bytes");
            var dehydrated = await provider.SetAvailabilityAsync(expected.RelativePath, false, false);
            Require(dehydrated.Failures.Count == 0 && WindowsCloudFilesProvider.TryReadOwnedPlaceholder(file, out var state) &&
                state!.State.Availability == SyncCloudFileAvailability.OnlineOnly, "Online-only releases the hydrated file data");
            provider.Dispose(); provider = new(pair, remote, Path.Combine(parent, "native-google-stage"));
            Require(WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id, SyncProviders.GoogleDrive), "Reconnect retains exact ownership");
            // Reconnect the existing registration: the one-run allocation permits only one register call.
            typeof(WindowsCloudFilesProvider).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(provider, [folder]);
            Require((await File.ReadAllBytesAsync(file)).SequenceEqual(Bytes), "A reconnect hydrates the same retained Google placeholder");
            await provider.UnregisterAsync();
            Require(!WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id) &&
                (await File.ReadAllBytesAsync(file)).SequenceEqual(Bytes), "Disable preserves all bytes and removes only this fixture root");
            Console.WriteLine("PASS native Google CFAPI create, verified hydration, pin, dehydrate, reconnect and hydrated unregister");
        }
        finally
        {
            // Do not delete any still-managed or uncertain root after a failed cleanup.
            if (provider is not null && WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id, SyncProviders.GoogleDrive))
                await provider.UnregisterAsync();
            provider?.Dispose();
            using (var root = PilotDirectoryLease.Open(folder, identity)) root.VerifyBinding();
            Require(!WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id) &&
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0, "Fixture root is ordinary and exact before removal");
            var children = Directory.GetFileSystemEntries(folder);
            Require(children.All(p => p.Equals(file, StringComparison.OrdinalIgnoreCase)), "Only the generated sample is present");
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
    private static void Require(bool value, string label)
    { if (!value) throw new IOException(label); Console.WriteLine("PASS " + label); }
    private sealed class Tokens : IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken token = default) => Task.FromResult("synthetic-only"); }
    private sealed class Protocol : HttpMessageHandler
    {
        internal int MediaCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.Method == HttpMethod.Get && request.RequestUri!.Host == "www.googleapis.com" &&
                request.RequestUri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal), "Only Google metadata/media GET input is permitted");
            var id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            Require(id is "fixture-root" or "fixture-file", "Only exact fixture IDs are read");
            if (request.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal))
            { Interlocked.Increment(ref MediaCalls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) }); }
            object body = id == "fixture-root"
                ? new { id, name = "Fixture", mimeType = GoogleDrivePilotClient.FolderMimeType, version = "1", trashed = false }
                : new { id, name = "sample.bin", mimeType = "application/octet-stream", version = "1", trashed = false,
                    parents = new[] { "fixture-root" }, size = Bytes.Length.ToString(), sha256Checksum = Hash,
                    headRevisionId = "fixture-head", capabilities = new { canDownload = true } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }
}
