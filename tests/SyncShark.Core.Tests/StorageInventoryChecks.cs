using Massdex.SyncShark;

internal static class StorageInventoryChecks
{
    internal static void DeviceSpaceChecks()
    {
        var queried = new List<string>();
        var rows = SyncStorageInventory.ReadDeviceSpace(["local", "duplicate", "cloud", "unreadable", "network", "unknown"],
            root => root == "network" ? DriveType.Network : DriveType.Fixed,
            root => root switch { "local" => "volume-a", "duplicate" => "VOLUME-A", "cloud" => null,
                "unknown" => throw new IOException("fixture"), _ => "volume-b" },
            root => { queried.Add(root); return root == "unreadable" ? throw new UnauthorizedAccessException("fixture") : (100L, 40L); });
        Check(rows.Count == 2 && rows[0].Drive == "local" && rows[0].CapacityBytes == 100 && rows[0].AvailableBytes == 40,
            "Only identified fixed local volumes contribute capacity, once per case-insensitive volume identity");
        Check(rows[1].Drive == "unreadable" && rows[1].CapacityBytes is null && rows[1].Error is not null,
            "An identified local volume with unavailable capacity keeps an honest unavailable row");
        Check(queried.SequenceEqual(["local", "unreadable"]),
            "Network, duplicate, virtual and unknown roots never invoke capacity reads");
        var ordinary = SyncStorageInventory.ReadDeviceSpace(["/"], _ => DriveType.Fixed, root => root, _ => (200L, 90L));
        Check(ordinary.Count == 1 && ordinary[0].AvailableBytes == 90,
            "The existing non-Windows root identity policy preserves fixed-volume reporting");
        var descriptor = new byte[40];
        BitConverter.GetBytes(40U).CopyTo(descriptor, 0); BitConverter.GetBytes(40U).CopyTo(descriptor, 4);
        BitConverter.GetBytes(17U).CopyTo(descriptor, 28);
        Check(SyncStorageInventory.IsDiskDescriptor(descriptor, 40) &&
            !SyncStorageInventory.IsDiskDescriptor(descriptor, 0) &&
            !SyncStorageInventory.IsDiskDescriptor(descriptor, 32) &&
            !SyncStorageInventory.IsDiskDescriptor(descriptor, 41), "Only a complete bounded disk descriptor is accepted");
        descriptor[28] = 0;
        Check(!SyncStorageInventory.IsDiskDescriptor(descriptor, 40), "An unknown storage bus is not proven disk capacity");
        Console.WriteLine("PASS device-space identity, deduplication and safe rejection");
    }

    internal static async Task RunAsync(string root, Action<string, string> createJunction)
    {
        DeviceSpaceChecks();
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh isolated storage-inventory fixture root.");
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "ordinary"); Directory.CreateDirectory(local);
        Directory.CreateDirectory(Path.Combine(local, "Child"));
        await File.WriteAllTextAsync(Path.Combine(local, "kept.txt"), "do not read content during metadata inspection");
        var before = File.GetLastWriteTimeUtc(Path.Combine(local, "kept.txt"));
        var page = SyncStorageInventory.Inspect(local, null, 100);
        Check(page.Rows.Count == 2 && page.Rows.All(r => r.PairId is null && r.Provider == "Unpaired"), "native directory enumeration and unpaired identity");
        Check(page.Rows.Single(r => r.RelativePath == ".").LogicalBytes == new FileInfo(Path.Combine(local, "kept.txt")).Length &&
            before == File.GetLastWriteTimeUtc(Path.Combine(local, "kept.txt")), "native metadata sizes without changing content");
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(outside, "NeverTraverse"));
        var junction = Path.Combine(local, "Redirect");
        if (!Directory.Exists(junction)) createJunction(junction, outside);
        page = SyncStorageInventory.Inspect(local, null, 100);
        Check(page.Rows.Any(r => r.LocalPath == junction && r.Availability == "Unknown / not inspected") &&
            page.Rows.All(r => !r.LocalPath.Contains("NeverTraverse")), "native junction refusal; no redirected enumeration");
        Check(SyncStorageInventory.Inspect(local, null, 1).Notice.StartsWith("Partial"), "bounded folder budget is disclosed");
        try { SyncStorageInventory.Inspect(Path.GetPathRoot(local)!, null, 100); throw new Exception("Drive-root scan was accepted"); }
        catch (InvalidDataException) { Check(true, "whole-drive scan refused"); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { SyncStorageInventory.Inspect(local, null, 100, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(true, "cancelled inspection performs no enumeration"); }
        }
        Check(StorageInventoryWindows.ClassifyCloud(true, 0x31, 0, 0) == "Cloud folder; contents may be mixed" &&
            StorageInventoryWindows.ClassifyCloud(false, 0x31, 100, 0) == "Online only" &&
            StorageInventoryWindows.ClassifyCloud(false, 0x31, 100, 20).StartsWith("Partly") &&
            StorageInventoryWindows.ClassifyCloud(false, 1, 100, 100) == "Available offline", "folder/file partial-state classifications do not overclaim offline availability");

        // Actual native placeholder outside the host's saved pairs stands in for a retained foreign
        // provider root. The inventory must not rely on the Sync Shark identity or issue a download.
        var cloud = RecoveryNativeTestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root, "stage"));
        var remote = new FakeSyncRemoteStore(); remote.Put("online.txt", "fixture cloud content");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(cloud)) identity = lease.Identity;
        var pair = new SyncPair("inventory-native-fixture", "Cloud fixture", cloud, identity.VolumeSerialNumber,
            identity.FileId, "/fixture", "id:fixture", FilesOnDemandEnabled: true);
        using (var provider = new WindowsCloudFilesProvider(pair, remote, Path.Combine(root, "stage")))
        {
            try
            {
                provider.RegisterAndConnect();
                provider.CreatePlaceholder((await remote.ListEntriesAsync()).Single());
                page = SyncStorageInventory.Inspect(cloud, null, 100);
                Check(page.Rows.Single().Availability == "Online-only files observed" && page.Rows.Single().ResidentBytes == 0 &&
                    page.Rows.Single().PairId is null && remote.DownloadCount == 0 && remote.RangeDownloadCount == 0,
                    "actual native online-only placeholder is observed as unpaired without hydration");
                var observed = StorageInventoryWindows.Inspect(Path.Combine(cloud, "online.txt"));
                Check(observed.Availability == "Online only" && observed.ResidentBytes == 0,
                    "native availability remains online-only after inventory");
            }
            finally
            {
                provider.Dispose();
                if (WindowsCloudFilesProvider.IsOwnedSyncRoot(cloud, pair.Id))
                {
                    var result = WindowsCloudFilesProvider.Native.CfUnregisterSyncRoot(cloud);
                    if (result < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
                }
                RecoveryNativeTestRoot.Remove(cloud);
            }
        }
        var saved = pair with { SelectionRules = [new("AbsentExcluded", false)],
            DirectoryStorageRules = [new("RemoteOnly", SyncDirectoryStorage.CloudOnly)] };
        Check(SyncStorageInventory.Saved(saved, "AbsentExcluded", "Not observed", null, "Unknown").Participation == "Excluded from sync" &&
            SyncStorageInventory.Saved(saved, "RemoteOnly", "Not observed", null, "Unknown").DesiredPolicy == "Online only", "saved selection and desired storage are independent of presence");
        Check(SyncCliRegistry.Rows.Any(r => r.Command == "storage inventory") && SyncCliRegistry.Rows.Any(r => r.Command == "storage inspect"), "CLI registry contains both read-only inventory actions");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var http = new HttpClient(new NoNetwork());
        var store = new SyncPairStore(instance.StateAuthority);
        using var firstLease = PilotDirectoryLease.Open(local);
        using var secondLease = PilotDirectoryLease.Open(outside);
        var first = saved with { Id = "inventory-a", ConnectionId = "account-a", LocalRoot = local,
            LocalVolumeSerialNumber = firstLease.Identity.VolumeSerialNumber, LocalRootFileId = firstLease.Identity.FileId };
        var second = saved with { Id = "inventory-b", ConnectionId = "account-b", Provider = SyncProviders.GoogleDrive, LocalRoot = outside,
            DropboxFolderId = "inventory-google-folder", FilesOnDemandEnabled = false, DirectoryStorageRules = [],
            LocalVolumeSerialNumber = secondLease.Identity.VolumeSerialNumber, LocalRootFileId = secondLease.Identity.FileId };
        await store.SaveAsync(new([first, second], first.Id));
        var db = new SyncAuthorityDatabase(Path.Combine(root, "inventory-authority.db"));
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, store,
            new SyncRuntimeStateStore(instance.StateAuthority), new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), db);
        var runtime = new InventoryRuntime(host, instance.StateAuthority);
        page = (SyncStorageInventoryPage)await SyncCliRegistry.DispatchAsync(runtime,
            SyncCliRegistry.Parse(["--cli", "storage", "inventory", "--account", "account-a"]), TextWriter.Null, CancellationToken.None);
        Check(page.Rows.All(r => r.PairId == first.Id) && page.Rows.Any(r => r.RelativePath == "AbsentExcluded" && r.Participation == "Excluded from sync"),
            "real CLI/authority inventory retains absent excluded policy and isolates accounts without provider calls");
        Check(!File.Exists(db.DatabasePath), "fresh policy-only inventory never creates or migrates the authority database");
        page = await host.LoadStorageInventoryAsync();
        Check(page.Rows.Any(r => r.PairId == second.Id && r.Provider == SyncProviders.GoogleDrive), "saved inventory includes Google pairs without a Dropbox lookup");
        try { await host.InspectStorageRootAsync(local, first.Id, "account-b"); throw new Exception("Wrong account accepted"); }
        catch (InvalidOperationException) { Check(true, "wrong-account inspection rejected before filesystem enumeration"); }
        page = (SyncStorageInventoryPage)await SyncCliRegistry.DispatchAsync(runtime,
            SyncCliRegistry.Parse(["--cli", "storage", "inspect", "--local", local, "--pair", first.Id, "--account", "account-a"]),
            TextWriter.Null, CancellationToken.None);
        Check(page.Rows.Count > 0 && page.Rows.All(r => r.PairId == first.Id), "actual CLI inspect reaches the shared native metadata implementation");
    }
    private static void Check(bool pass, string message)
    { if (!pass) throw new InvalidDataException(message); Console.WriteLine("PASS STORAGE " + message); }
    private sealed class InventoryRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority state) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => state;
        public override string Mode => "inventory-fixture";
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not contact a provider.");
    }
    private sealed class NoTokens : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No token reads allowed.");
        public Task WriteAsync(string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DeleteAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
