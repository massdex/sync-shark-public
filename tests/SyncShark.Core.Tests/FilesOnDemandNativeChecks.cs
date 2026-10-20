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
        Exception? originalFailure = null;
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
            var database = Path.Combine(Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT")
                ?? throw new InvalidOperationException("Set the disposable database test root on F:."), "fod-e2e-" + Guid.NewGuid().ToString("N"));
            var localRoot = new RootDef("fixture-local", SyncProviders.Local, null, root,
                SyncLocalEndpointStore.ObjectId(local.VolumeSerialNumber, local.FileId));
            var remoteRoot = new RootDef("fixture-remote", SyncProviders.Dropbox, "fixture-account", "/Fixture", "id:fixture-root");
            var pair = new PairDef(binding.PairId, "Pair A", localRoot, remoteRoot, [], [], true);
            var uploaded = new ItemState(remote.RelativePath, ItemKind.File,
                SyncLocalEndpointStore.ObjectId(uploadedIdentity.VolumeSerialNumber, uploadedIdentity.FileId),
                null, hash, remote.Size, new DateTimeOffset(File.GetLastWriteTimeUtc(path)), ItemFlags.None);
            using (var uploadStore = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System))
            {
                uploadStore.SavePair(pair);
                var generation = uploadStore.BeginGeneration(localRoot.RootId, "snapshot");
                uploadStore.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, generation, "cursor", [uploaded], []));
                uploadStore.SetStoragePolicy(localRoot.RootId, localRoot.RootObjectId, "online-only");
                var action = new SyncAction("fixture-upload", 1, ActionKind.CopyNew, Side.B, uploaded.Path,
                    null, uploaded, null, false, "fixture-object");
                uploadStore.RecordIntent(pair.PairId, action);
                await using var uploadBytes = File.OpenRead(path);
                var receipt = await source.UploadAsync(uploadBytes);
                uploadStore.CommitResult(pair.PairId, generation, action, new(action.ActionId, Outcome.Done,
                    uploaded, uploaded with { ObjectId = receipt.Id!, VersionId = receipt.Revision }, null, null));
            } // restart after the upload receipt, before production settlement
            provider.Dispose();
            using (var restarted = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System))
            using (var lifetime = new CloudFilesLifetime((_, _, _) => provider = new WindowsCloudFilesProvider(
                binding, source, root, SyncDirectoryStorage.CloudOnly), _ => true, restarted,
                (_, _, _) => Task.FromResult<PilotRemoteEntry?>(source.Current)))
            {
                await using var service = new StorageSettlementService(restarted, lifetime);
                await service.DrainAsync(CancellationToken.None);
                if (restarted.StorageSettlementPage().Count != 0 || source.Uploads != 1)
                    throw new IOException("Restarted production settlement did not acknowledge exactly one upload.");
            }
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly);
            provider.RegisterAndConnect();
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var converted) || converted is null ||
                !converted.State.IsInSync || converted.State.Availability != SyncCloudFileAvailability.OnlineOnly)
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
            var nextBytes = System.Text.Encoding.UTF8.GetBytes("new remote revision with a different size");
            using var nextStream = new MemoryStream(nextBytes);
            var nextHash = await SyncContentIdentity.ComputeAsync(SyncProviders.Dropbox, nextStream, CancellationToken.None);
            var next = remote with { Revision = "revision-2", Size = nextBytes.Length, ContentHash = nextHash };
            var refusedHydrated = false;
            try { provider.UpdateDehydratedPlaceholder(remote, next, uploadedIdentity); }
            catch (IOException) { refusedHydrated = true; }
            if (!refusedHydrated || !(await File.ReadAllBytesAsync(path)).SequenceEqual(content))
                throw new IOException("Remote pointer update overwrote hydrated content.");
            await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true, expectedLocalIdentity: uploadedIdentity);
            var beforeUpdateReads = source.Reads;
            provider.Dispose();
            source.Replace(nextBytes, next);
            using (var restarted = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System))
            using (var lifetime = new CloudFilesLifetime((_, _, _) => provider = new WindowsCloudFilesProvider(
                binding, source, root, SyncDirectoryStorage.CloudOnly), _ => true, restarted,
                (_, _, _) => Task.FromResult<PilotRemoteEntry?>(source.Current)))
            {
                var generation = restarted.BeginGeneration(remoteRoot.RootId, "snapshot");
                restarted.WriteSnapshot(new Snapshot(remoteRoot.RootId, Side.B, generation, "cursor-2",
                    [uploaded with { ObjectId = next.Id!, VersionId = next.Revision, Hash = next.ContentHash, Size = next.Size }], []));
                await using var service = new StorageSettlementService(restarted, lifetime);
                await service.DrainAsync(CancellationToken.None);
                if (restarted.ChangedStoragePointers().Count != 0)
                {
                    WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var pendingPointer);
                    throw new IOException("Production pointer update did not acknowledge the new remote receipt. " +
                        pendingPointer?.State + " Local: " + restarted.ByObject(localRoot.RootId, uploaded.ObjectId) + " " +
                        string.Join("; ", restarted.OpenNotices(pair.PairId).Select(n => n.Detail)));
                }
            }
            using var verifyStore = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System);
            using var verifyLifetime = new CloudFilesLifetime((_, _, _) => new WindowsCloudFilesProvider(
                binding, source, root, SyncDirectoryStorage.CloudOnly), _ => true, verifyStore);
            provider = verifyLifetime.RequireProvider(pair, CancellationToken.None);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var updatedPointer) || updatedPointer is null ||
                updatedPointer.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
                updatedPointer.Identity.DropboxRevision != next.Revision || updatedPointer.State.LogicalSize != next.Size ||
                source.Reads != beforeUpdateReads)
                throw new IOException("Remote revision update did not remain dehydrated without download.");
            provider.UpdateDehydratedPlaceholder(remote, next, uploadedIdentity); // lost-response retry
            source.FailReads = true;
            var failedIo = false;
            try { await File.ReadAllBytesAsync(path); } catch (IOException) { failedIo = true; }
            if (!failedIo) throw new IOException("Failed hydration did not fail Windows I/O.");
            var hydrationNotice = verifyStore.OpenNotices(pair.PairId).Single(n => n.Storage?.Operation == "hydration");
            source.FailReads = false;
            await StorageNotices.ApplyAsync(verifyStore, verifyLifetime, hydrationNotice, NoticeChoices.RetryNow, CancellationToken.None);
            if (verifyStore.OpenNotices(pair.PairId).Any(n => n.Storage?.Operation == "hydration"))
                throw new IOException("Successful hydration Retry did not close the durable notice.");
            if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(nextBytes))
                throw new IOException("Updated pointer did not hydrate the new exact revision.");
            await provider.ConvertVerifiedSyncedFileAsync(next, dehydrateOverride: true, expectedLocalIdentity: uploadedIdentity);
            source.FailReads = true;
            failedIo = false;
            try { await File.ReadAllBytesAsync(path); } catch (IOException) { failedIo = true; }
            if (!failedIo) throw new IOException("Second failed hydration did not fail Windows I/O.");
            hydrationNotice = verifyStore.OpenNotices(pair.PairId).Single(n => n.Storage?.Operation == "hydration");
            var readsBeforeIgnore = source.Reads;
            await StorageNotices.ApplyAsync(verifyStore, verifyLifetime, hydrationNotice, NoticeChoices.Ignore, CancellationToken.None);
            if (verifyStore.OpenNotices(pair.PairId).Any(n => n.Storage?.Operation == "hydration") ||
                !verifyStore.StorageOperationIgnored(pair.PairId, hydrationNotice.Storage!) ||
                !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var ignoredPointer) || ignoredPointer is null ||
                ignoredPointer.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
                ignoredPointer.Identity.DropboxRevision != next.Revision || source.Reads != readsBeforeIgnore)
                throw new IOException("Hydration Ignore did not close the notice while retaining the unchanged pointer.");
            source.FailReads = false;
            await provider.UnregisterAsync(CancellationToken.None);
            if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                throw new IOException("Disposable registration was retained after successful hydration.");
            Console.WriteLine($"PASS Files On-Demand native register/reconnect-before-conversion/edit-race/verified-convert/dehydrate/reconnect-after-conversion/idempotent-retry/callback-hydrate/dehydrated-revision-update/hydration-Retry/hydration-Ignore/unregister; platform {version}");
        }
        catch (Exception error)
        {
            originalFailure = error;
            throw;
        }
        finally
        {
            try
            {
                source.FailReads = false;
                // This fresh fixture owns only tiny synthetic bytes. Hydrate before removing its root.
                // If hydration fails, preserve the pointer rather than deleting a registered root.
                provider.Dispose();
                if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                {
                    using var cleanup = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly);
                    cleanup.RegisterAndConnect();
                    await cleanup.UnregisterAsync(CancellationToken.None);
                }
                if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                    throw new IOException("Disposable registration remains; root retained.");
                Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (originalFailure is not null)
            {
                throw new AggregateException("Native check failed and fixture cleanup also failed; root may remain.",
                    originalFailure, cleanupError);
            }
        }
    }

    // Explicit cleanup of an interrupted fixture, never a general root removal tool.
    internal static async Task CleanupAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(root), @"C:\SyncSharkFodTest", StringComparison.OrdinalIgnoreCase) ||
            !DateTime.TryParseExact(Path.GetFileName(root), "yyyyMMdd-HHmmss-fff",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != "sample.txt"))
            throw new IOException("Not an isolated known fixture; nothing removed.");
        var path = Path.Combine(root, "sample.txt");
        if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var pointer) || pointer is null ||
            !pointer.Identity.IsBoundTo("disposable-pair", SyncProviders.Dropbox) || pointer.Identity.DropboxId != "id:sample")
            throw new IOException("Unknown fixture identity; nothing removed.");
        var text = pointer.Identity.DropboxRevision switch
        {
            "revision-1" => "exact disposable hydration bytes",
            "revision-2" => "new remote revision with a different size",
            _ => throw new IOException("Unknown fixture revision; nothing removed.")
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var data = new MemoryStream(bytes);
        var hash = await SyncContentIdentity.ComputeAsync(SyncProviders.Dropbox, data, CancellationToken.None);
        if (hash != pointer.Identity.ContentHash || bytes.Length != pointer.Identity.LogicalSize)
            throw new IOException("Unknown fixture bytes; nothing removed.");
        var remote = new PilotRemoteEntry("sample.txt", PilotEntryKind.File, "id:sample", pointer.Identity.DropboxRevision, bytes.Length, hash);
        using var lease = PilotDirectoryLease.Open(root);
        var identity = lease.Identity;
        var binding = new WindowsCloudFilesBinding("disposable-pair", SyncProviders.Dropbox, "fixture-account",
            root, identity.VolumeSerialNumber, identity.FileId, "/Fixture", "id:fixture-root");
        using (var provider = new WindowsCloudFilesProvider(binding, new RangeSource(bytes, remote), root, SyncDirectoryStorage.CloudOnly))
        {
            provider.RegisterAndConnect();
            await provider.UnregisterAsync(CancellationToken.None);
        }
        lease.Dispose();
        if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)) throw new IOException("Changed fixture bytes retained.");
        Directory.Delete(root, true);
        Console.WriteLine("PASS interrupted native fixture hydrated, unregistered and removed");
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
        public int Uploads;
        public bool FailReads;
        public PilotRemoteEntry Current => entry;
        public async Task<PilotRemoteEntry> UploadAsync(Stream source)
        {
            using var uploaded = new MemoryStream();
            await source.CopyToAsync(uploaded);
            if (!uploaded.ToArray().SequenceEqual(bytes)) throw new IOException("Wrong fixture upload bytes.");
            Uploads++;
            return entry;
        }
        private readonly Dictionary<string, (byte[] Bytes, PilotRemoteEntry Entry)> _previous = new();
        public void Replace(byte[] content, PilotRemoteEntry remote)
        {
            _previous[entry.Revision!] = (bytes, entry);
            bytes = content; entry = remote;
        }
        public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset, long length,
            Stream destination, CancellationToken cancellationToken = default)
        {
            if (FailReads) throw new IOException("Synthetic unavailable remote revision; retry when available.");
            var selected = expected.Revision == entry.Revision ? (Bytes: bytes, Entry: entry) :
                _previous.GetValueOrDefault(expected.Revision!);
            if (selected.Entry is null || expected.Id != selected.Entry.Id || expected.ContentHash != selected.Entry.ContentHash)
                throw new IOException("Unexpected fixture revision.");
            Interlocked.Increment(ref Reads);
            await destination.WriteAsync(selected.Bytes.AsMemory(checked((int)offset), checked((int)length)), cancellationToken);
            return new(selected.Entry.Id!, selected.Entry.Revision!, selected.Entry.Size, selected.Entry.ContentHash!, offset, length);
        }
    }
}
