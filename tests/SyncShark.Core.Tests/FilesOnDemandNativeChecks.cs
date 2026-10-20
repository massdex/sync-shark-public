using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandNativeChecks
{
    // Caller supplies a fresh disposable directory. Never follows an existing directory or reparse ancestor.
    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out var version))
        {
            Console.WriteLine("SKIP Files On-Demand: Windows Cloud Files is unavailable.");
            return;
        }
        if (!string.Equals(Path.GetDirectoryName(root), @"C:\SyncSharkFodTest", StringComparison.OrdinalIgnoreCase) ||
            !DateTime.TryParseExact(Path.GetFileName(root), "yyyyMMdd-HHmmss-fff",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ||
            Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException("Native validation requires a fresh timestamp directory directly under C:\\SyncSharkFodTest.");
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0 ||
                WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(parent.FullName) is not null)
                throw new InvalidOperationException("Disposable root has a reparse or registered ancestor; nothing registered.");
        }
        if (!string.Equals(new DriveInfo(Path.GetPathRoot(root)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("SKIP Files On-Demand: disposable volume is not NTFS.");
            return;
        }
        Directory.CreateDirectory(root);
        var content = System.Text.Encoding.UTF8.GetBytes("exact disposable hydration bytes");
        using var bytes = new MemoryStream(content);
        var hash = await SyncContentIdentity.ComputeAsync(SyncProviders.Dropbox, bytes, CancellationToken.None);
        var remote = new PilotRemoteEntry("sample.txt", PilotEntryKind.File, "id:sample", "revision-1", content.Length, hash);
        var source = new RangeSource(content, remote);
        PilotDirectoryIdentity local;
        using (var lease = PilotDirectoryLease.Open(root)) local = lease.Identity;
        var binding = new WindowsCloudFilesBinding("disposable-pair", SyncProviders.Dropbox, "fixture-account",
            root, local.VolumeSerialNumber, local.FileId, "/Fixture", "id:fixture-root");
        var path = Path.Combine(root, remote.RelativePath);
        await File.WriteAllBytesAsync(path, content);
        using var initialProvider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly);
        var provider = initialProvider;
        try { provider.RegisterAndConnect(); }
        catch (Exception error) when (error is PlatformNotSupportedException ||
            error is IOException && error.InnerException?.HResult == unchecked((int)0x80070032))
        {
            provider.RollBackNewRegistration();
            provider.Dispose();
            Directory.Delete(root, recursive: true);
            Console.WriteLine("SKIP Files On-Demand: native registration is unsupported: " + error.Message);
            return;
        }
        try
        {
            // Simulate host loss after receipt persistence but before conversion.
            provider.Dispose();
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly);
            provider.RegisterAndConnect();
            WindowsCloudFilesProvider.BeforeDehydrateForTest = file => File.WriteAllBytes(file, new byte[content.Length]);
            var rejectedEdit = false;
            try { await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true); }
            catch (IOException) { rejectedEdit = true; }
            finally { WindowsCloudFilesProvider.BeforeDehydrateForTest = null; }
            if (!rejectedEdit || !(await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[content.Length]))
                throw new IOException("An edit at conversion was not retained safely.");
            await File.WriteAllBytesAsync(path, content); // restore only this synthetic fixture
            PilotDirectoryIdentity uploadedIdentity;
            using (var file = SyncLocalFileAuthority.OpenExisting(path)) uploadedIdentity = file.ReadIdentity();
            var rejectedReplacement = false;
            try
            {
                await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true,
                    expectedLocalIdentity: uploadedIdentity with { FileId = uploadedIdentity.FileId + 1 });
            }
            catch (IOException) { rejectedReplacement = true; }
            if (!rejectedReplacement || WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out _))
                throw new IOException("A mismatched uploaded identity authorized conversion.");
            var rejectedPolicy = false;
            try
            {
                await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true,
                    expectedLocalIdentity: uploadedIdentity, revalidate: () => throw new IOException("Policy changed."));
            }
            catch (IOException) { rejectedPolicy = true; }
            if (!rejectedPolicy || WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out _))
                throw new IOException("A changed policy authorized conversion.");
            var converted = await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true,
                expectedLocalIdentity: uploadedIdentity);
            if (!converted.State.IsInSync || converted.State.Availability != SyncCloudFileAvailability.OnlineOnly)
                throw new IOException("Verified conversion did not dehydrate and mark in sync.");
            if (source.Reads != 0) throw new IOException("Conversion unexpectedly fetched bytes.");
            // Simulate loss after native conversion but before durable settlement acknowledgement.
            provider.Dispose();
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly);
            provider.RegisterAndConnect();
            var resumed = await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true);
            if (!resumed.State.IsInSync || resumed.State.Availability != SyncCloudFileAvailability.OnlineOnly || source.Reads != 0)
                throw new IOException("Post-conversion retry was not idempotent after reconnect.");
            var pointer = await ScanOwnedAsync(root, binding.PairId);
            if (!pointer.Flags.HasFlag(ItemFlags.OwnedPointer) || pointer.Hash != hash || source.Reads != 0)
                throw new IOException("Owned pointer was not inventoried as present with its verified hash without downloading.");
            var pointerPlan = Planner.Decide(binding.PairId, 1,
                [new PlanRow(null, pointer, null, new Evidence(false, false, true, true))],
                new PlanOptions(TimeSpan.Zero, true, new LabelRule("{name}", "yyyyMMdd", TimeSpan.Zero, "A", "B"), 10));
            if (pointerPlan.Actions.Any(action => action.Source?.ObjectId == pointer.ObjectId || action.Kind == ActionKind.Delete))
                throw new IOException("Planner used a pointer as an upload source or deletion evidence.");
            byte[] read;
            using (PairPause.Begin(null!, binding.PairId, () => true))
            {
                var paused = false;
                try { PairPause.Check(); } catch (PairPausedException) { paused = true; }
                if (!paused) throw new IOException("Fixture sync checkpoint was not paused.");
                read = await File.ReadAllBytesAsync(path); // Windows I/O must not wait on the sync pause gate
            }
            if (!read.SequenceEqual(content) || source.Reads == 0)
                throw new IOException("Hydrate-on-open did not return exact callback bytes while sync was paused.");
            var hydrated = await ScanOwnedAsync(root, binding.PairId);
            if (hydrated.Flags.HasFlag(ItemFlags.OwnedPointer) || hydrated.ObjectId != pointer.ObjectId ||
                hydrated.Hash != pointer.Hash || hydrated.Size != pointer.Size || hydrated.Mtime != pointer.Mtime)
                throw new IOException("Hydration-only transition changed the content fingerprint.");
            await provider.UnregisterAsync(CancellationToken.None);
            if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                throw new IOException("Disposable registration was retained after successful hydration.");
            Console.WriteLine($"PASS Files On-Demand native register/reconnect-before-conversion/edit-race/verified-convert/dehydrate/reconnect-after-conversion/idempotent-retry/callback-hydrate/unregister; platform {version}");
        }
        finally
        {
            // This fresh fixture owns only tiny synthetic bytes. Hydrate before removing its root.
            // If hydration fails, preserve the pointer rather than deleting a registered root.
            if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                await provider.UnregisterAsync(CancellationToken.None);
            provider.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ItemState> ScanOwnedAsync(string root, string pairId)
    {
        using var lease = PilotDirectoryLease.Open(root);
        var scan = await new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true }.ScanAsync(
            lease, new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None,
            expectedPairId: pairId, contentProvider: SyncProviders.Dropbox);
        var entry = scan.Entries.Values.Single(item => item.RelativePath == "sample.txt");
        return LocalItems.FromScan(entry);
    }

    private sealed class RangeSource(byte[] bytes, PilotRemoteEntry entry) : IWindowsCloudFilesRangeSource
    {
        public int Reads;
        public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset, long length,
            Stream destination, CancellationToken cancellationToken = default)
        {
            if (expected.Id != entry.Id || expected.Revision != entry.Revision || expected.ContentHash != entry.ContentHash)
                throw new IOException("Unexpected fixture revision.");
            Interlocked.Increment(ref Reads);
            await destination.WriteAsync(bytes.AsMemory(checked((int)offset), checked((int)length)), cancellationToken);
            return new(entry.Id!, entry.Revision!, entry.Size, entry.ContentHash!, offset, length);
        }
    }
}
