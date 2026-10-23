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
            !DateTime.TryParseExact(Path.GetFileName(root), ["yyyyMMdd-HHmmss", "yyyyMMdd-HHmmss-fff"],
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
        // Own the timestamp container so the staging sibling inherits a user-owned parent.
        // The public argument remains the fresh timestamp directory, never an existing root.
        Directory.CreateDirectory(root);
        try { await RunInContainerAsync(Path.Combine(root, "root"), version); }
        finally
        {
            // Non-recursive: never remove unexpected content or a failed registered fixture.
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
    }

    private static async Task RunInContainerAsync(string root, string version)
    {
        if (FilesOnDemandNativeEdgeChecks.ControlNamespaceExists(root))
            throw new IOException("Fixture control namespace already exists; nothing registered.");
        Directory.CreateDirectory(root);
        var content = System.Text.Encoding.UTF8.GetBytes("exact disposable hydration bytes");
        using var bytes = new MemoryStream(content);
        var hash = await SyncContentIdentity.ComputeAsync(SyncProviders.Dropbox, bytes, CancellationToken.None);
        var remote = new PilotRemoteEntry("sample.txt", PilotEntryKind.File, "id:sample", "revision-1", content.Length, hash)
            { ModifiedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var source = new RangeSource(content, remote);
        PilotDirectoryIdentity local;
        using (var lease = PilotDirectoryLease.Open(root)) local = lease.Identity;
        var binding = new WindowsCloudFilesBinding("disposable-pair", SyncProviders.Dropbox, "fixture-account",
            root, local.VolumeSerialNumber, local.FileId, "/Fixture", "id:fixture-root");
        var path = Path.Combine(root, remote.RelativePath);
        await File.WriteAllBytesAsync(path, content);
        File.SetLastWriteTimeUtc(path, remote.ModifiedUtc.Value.UtcDateTime);
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
        // The lifetime ownership gate records only a registration this process created.
        if (!initialProvider.CreatedRegistration)
            throw new IOException("The disposable fixture root was not created by this process.");
        var registrationId = initialProvider.RegistrationId ??
            throw new IOException("Windows did not return the disposable sync-root ownership GUID.");
        if (!Guid.TryParseExact(registrationId, "D", out var ownershipGuid) || ownershipGuid == Guid.Empty)
            throw new IOException("The disposable registration did not contain a fresh v2 ownership GUID.");
        Exception? originalFailure = null;
        try
        {
            // Simulate host loss after receipt persistence but before conversion.
            provider.Dispose();
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly)
                { ExpectedRegistrationId = registrationId };
            provider.RegisterAndConnect();
            WindowsCloudFilesProvider.BeforeDehydrateForTest = file => File.WriteAllBytes(file, new byte[content.Length]);
            var rejectedEdit = false;
            try { await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true); }
            catch (IOException) { rejectedEdit = true; }
            finally { WindowsCloudFilesProvider.BeforeDehydrateForTest = null; }
            if (!rejectedEdit || !(await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[content.Length]))
                throw new IOException("An edit at conversion was not retained safely.");
            await File.WriteAllBytesAsync(path, content); // restore only this synthetic fixture
            File.SetLastWriteTimeUtc(path, remote.ModifiedUtc.Value.UtcDateTime);
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
            using (var uploadStore = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                uploadStore.EnableStorage();
                uploadStore.SavePair(pair);
                uploadStore.RecordStorageRegistration(localRoot.RootId, root, registrationId);
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
            using (var restarted = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
            using (var lifetime = new CloudFilesLifetime((_, _, _) => provider = new WindowsCloudFilesProvider(
                binding, source, root, SyncDirectoryStorage.CloudOnly), _ => true, restarted,
                (_, _, _) => Task.FromResult<PilotRemoteEntry?>(source.Current)))
            {
                await using var service = new StorageSettlementService(restarted, lifetime);
                await service.DrainAsync(CancellationToken.None);
                if (restarted.StorageSettlementPage().Count != 0 || source.Uploads != 1)
                    throw new IOException("Restarted production settlement did not acknowledge exactly one upload.");
            }
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly)
                { ExpectedRegistrationId = registrationId };
            provider.RegisterAndConnect();
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var converted) || converted is null ||
                !converted.State.IsInSync || converted.State.Availability != SyncCloudFileAvailability.OnlineOnly)
                throw new IOException("Verified conversion did not dehydrate and mark in sync.");
            if (source.Reads != 0) throw new IOException("Conversion unexpectedly fetched bytes.");
            // Only one Cloud Files connection may exist at a time for this disposable root. The
            // metadata checks own a service-created provider, so close this one first and let the
            // existing reconnect below own the next connection.
            provider.Dispose();
            // Round-5 metadata-only checks: a transient registration failure must not create debt, and a
            // dirty in-sync-cleared pointer must self-heal through one entity acknowledgment.
            using (var pointerStore = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                var metadataReads = source.Reads;
                await FilesOnDemandNativePointerChecks.VerifyTransientRegistrationAsync(uploaded.Path,
                    uploaded.ObjectId, pair.PairId, pointerStore, pair, () => source.Reads, CancellationToken.None);
                Console.WriteLine("PASS native transient registration-unreadable scan keeps the owned row without verification debt");
                await FilesOnDemandNativePointerChecks.VerifyDirtyPointerSelfHealsAsync(root, uploaded.Path,
                    uploaded.ObjectId, pair.PairId, pointerStore, pair, () => source.Reads,
                    () => new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly),
                    source.Current, CancellationToken.None);
                if (source.Reads != metadataReads)
                    throw new IOException("Metadata-only pointer recovery downloaded bytes.");
                Console.WriteLine("PASS native dirty in-sync-cleared pointer self-heals through the real acknowledgment service");
            }
            // Simulate loss after native conversion but before durable settlement acknowledgement.
            provider.Dispose();
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly)
                { ExpectedRegistrationId = registrationId };
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
            Exception? renameFailure = null;
            try
            {
                using var renameStore = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
                // The acknowledgment service creates and owns the provider for this root; close the
                // foreground provider first so the fixture never double-connects the same root.
                provider.Dispose();
                await FilesOnDemandNativePointerChecks.VerifyRenameAsync(root, path, binding.PairId, pointer,
                    () => source.Reads, remote, entry => source.Replace(content, entry), renameStore, pair,
                    registrationId,
                    () => new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly),
                    content, CancellationToken.None);
                Console.WriteLine("PASS native rename, remote Move, real acknowledgment service and hydration through the new path");
            }
            catch (Exception error)
            {
                renameFailure = error;
                Console.WriteLine("FAIL native rename: " + error.Message);
            }
            provider = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly)
                { ExpectedRegistrationId = registrationId };
            provider.RegisterAndConnect();
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
                (hydrated.Hash is not null && hydrated.Hash != pointer.Hash) || hydrated.Size != pointer.Size || hydrated.Mtime != pointer.Mtime)
                throw new IOException("Hydration-only transition changed the content fingerprint. Before: " + pointer + " After: " + hydrated);
            var readsBeforeCreate = source.Reads;
            var directReceipt = NewPointerNativeChecks.CheckCreateOnly(provider, local, remote, path, () =>
            {
                if (source.Reads != readsBeforeCreate)
                    throw new IOException("Creating a new pointer downloaded bytes.");
            });
            source.AddExactObject(content, directReceipt);
            var nextBytes = System.Text.Encoding.UTF8.GetBytes("new remote revision with a different size");
            using var nextStream = new MemoryStream(nextBytes);
            var nextHash = await SyncContentIdentity.ComputeAsync(SyncProviders.Dropbox, nextStream, CancellationToken.None);
            var next = remote with { Revision = "revision-2", Size = nextBytes.Length, ContentHash = nextHash };
            var refusedHydrated = false;
            try { provider.UpdateDehydratedPlaceholder(remote, next, uploadedIdentity); }
            catch (IOException) { refusedHydrated = true; }
            if (!refusedHydrated || !(await File.ReadAllBytesAsync(path)).SequenceEqual(content))
                throw new IOException("Remote pointer update overwrote hydrated content.");
            // C45: reconverting an already-owned hydrated object reads back on the held exclusive handle.
            var reconverted = await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true,
                expectedLocalIdentity: uploadedIdentity);
            if (reconverted.State.Availability != SyncCloudFileAvailability.OnlineOnly || !reconverted.State.IsInSync)
                throw new IOException("Owned hydrated reconversion did not return verified online-only state.");
            var beforeUpdateReads = source.Reads;
            provider.Dispose();
            using (var restarted = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
            using (var lifetime = new CloudFilesLifetime((_, _, _) => provider = new WindowsCloudFilesProvider(
                binding, source, root, SyncDirectoryStorage.CloudOnly), _ => true, restarted,
                (_, _, _) => Task.FromResult<PilotRemoteEntry?>(source.Current)))
            {
                // Direct native calls above do not publish store observations. Re-settle through
                // the real service before testing the remote-cursor trigger against OnlineOnly state.
                restarted.QueueStorageSettlement(localRoot.RootId, uploaded.ObjectId, remoteRoot.RootId, remote);
                await using (var settlement = new StorageSettlementService(restarted, lifetime))
                    await settlement.DrainAsync(CancellationToken.None);
                source.Replace(nextBytes, next);
                var generation = restarted.BeginGeneration(remoteRoot.RootId, "snapshot");
                restarted.WriteDelta(new Delta(remoteRoot.RootId, Side.B, generation, "", "cursor-2",
                    [new Change(ChangeKind.Upsert, next.Id!, next.RelativePath,
                        uploaded with { ObjectId = next.Id!, VersionId = next.Revision, Hash = next.ContentHash, Size = next.Size }, null)], false, []));
                if (restarted.ChangedStoragePointers().Count != 1)
                    throw new IOException("The real remote delta did not queue exactly one pointer refresh.");
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
            using var verifyStore = SqliteStateStore.Open(database, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
            using var verifyLifetime = new CloudFilesLifetime((_, _, _) => new WindowsCloudFilesProvider(
                binding, source, root, SyncDirectoryStorage.CloudOnly), _ => true, verifyStore);
            provider = verifyLifetime.RequireProvider(pair, CancellationToken.None);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var updatedPointer) || updatedPointer is null ||
                updatedPointer.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
                updatedPointer.Identity.DropboxRevision != next.Revision || updatedPointer.State.LogicalSize != next.Size ||
                source.Reads != beforeUpdateReads)
                throw new IOException("Remote revision update did not remain dehydrated without download. Pointer: " + updatedPointer +
                    " Reads before/after: " + beforeUpdateReads + "/" + source.Reads +
                    " Notices: " + string.Join("; ", verifyStore.OpenNotices(pair.PairId).Select(n => n.Detail)));
            provider.UpdateDehydratedPlaceholder(remote, next, uploadedIdentity); // lost-response retry
            // F4 regression: an owned online-only placeholder whose identity no longer matches the
            // settlement receipt must refuse before hashing instead of recalling its bytes.
            var staleReads = source.Reads;
            var staleConversionRefused = false;
            try
            {
                await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true,
                    expectedLocalIdentity: uploadedIdentity);
            }
            catch (IOException) { staleConversionRefused = true; }
            if (!staleConversionRefused || source.Reads != staleReads ||
                !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var stillOnlineOnly) ||
                stillOnlineOnly is null ||
                stillOnlineOnly.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
                stillOnlineOnly.Identity.DropboxRevision != next.Revision)
                throw new IOException(
                    "Settlement hashed or hydrated an online-only owned placeholder for a stale receipt.");
            source.FailReads = true;
            var failedIo = false;
            try { await File.ReadAllBytesAsync(path); } catch (IOException) { failedIo = true; }
            if (!failedIo) throw new IOException("Failed hydration did not fail Windows I/O.");
            var hydrationNotice = await WaitForHydrationNoticeAsync(verifyStore, pair.PairId);
            source.FailReads = false;
            await StorageNoticeChoices.ApplyAsync(verifyStore, verifyLifetime, hydrationNotice, NoticeChoices.RetryNow, CancellationToken.None);
            await WaitUntilHydrationNoticesClosedAsync(verifyStore, pair.PairId);
            if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(nextBytes))
                throw new IOException("Updated pointer did not hydrate the new exact revision.");
            await provider.ConvertVerifiedSyncedFileAsync(next, dehydrateOverride: true, expectedLocalIdentity: uploadedIdentity);
            source.FailReads = true;
            failedIo = false;
            try { await File.ReadAllBytesAsync(path); } catch (IOException) { failedIo = true; }
            if (!failedIo) throw new IOException("Second failed hydration did not fail Windows I/O.");
            hydrationNotice = await WaitForHydrationNoticeAsync(verifyStore, pair.PairId);
            // The notice worker is asynchronous: a successful hydration must eventually close the open notice.
            source.FailReads = false;
            if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(nextBytes))
                throw new IOException("A successful hydration after a reported failure returned the wrong bytes.");
            await WaitUntilHydrationNoticesClosedAsync(verifyStore, pair.PairId);
            await provider.ConvertVerifiedSyncedFileAsync(next, dehydrateOverride: true, expectedLocalIdentity: uploadedIdentity);
            source.FailReads = true;
            failedIo = false;
            try { await File.ReadAllBytesAsync(path); } catch (IOException) { failedIo = true; }
            if (!failedIo) throw new IOException("Third failed hydration did not fail Windows I/O.");
            hydrationNotice = await WaitForHydrationNoticeAsync(verifyStore, pair.PairId);
            var readsBeforeIgnore = source.Reads;
            await StorageNoticeChoices.ApplyAsync(verifyStore, verifyLifetime, hydrationNotice, NoticeChoices.Ignore, CancellationToken.None);
            await WaitUntilHydrationNoticesClosedAsync(verifyStore, pair.PairId);
            if (!verifyStore.StorageCauseIgnored(pair.PairId, hydrationNotice.ReasonKey) ||
                !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var ignoredPointer) || ignoredPointer is null ||
                ignoredPointer.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
                ignoredPointer.Identity.DropboxRevision != next.Revision || source.Reads != readsBeforeIgnore)
                throw new IOException("Hydration Ignore did not close the notice while retaining the unchanged pointer.");
            source.FailReads = false;
            // Real callback outage burst: repeated failed opens share one pair cause and backoff.
            source.NetworkOutage = true;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                try { await File.ReadAllBytesAsync(path); }
                catch (IOException) { return; }
                throw new IOException("Offline fixture unexpectedly hydrated.");
            }));
            await WaitForHydrationNoticeAsync(verifyStore, pair.PairId);
            var pairNotices = verifyStore.OpenNotices(pair.PairId)
                .Where(n => n.ReasonKey.StartsWith("storage:hydration-pair:", StringComparison.Ordinal)).ToArray();
            if (pairNotices.Length != 1)
                throw new IOException("Offline burst did not persist exactly one pair hydration notice: " +
                    string.Join("; ", verifyStore.OpenNotices(pair.PairId).Select(n => n.ReasonKey)));
            source.NetworkOutage = false;
            if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(nextBytes))
                throw new IOException("Outage recovery returned wrong bytes.");
            await WaitUntilHydrationNoticesClosedAsync(verifyStore, pair.PairId);
            Console.WriteLine("PASS native offline callback burst: one pair notice, successful read closes it");
            source.Replace(FilesOnDemandNativeEdgeChecks.LargeEdgeContent(), FilesOnDemandNativeEdgeChecks.LargeEdgeRemote());
            var edge = await FilesOnDemandNativeEdgeChecks.CheckLargeFirstRangeReadAsync(root, provider, local,
                () => source.Reads, binding.PairId);
            Console.WriteLine($"Native first 4KiB read: PARTIALLY_ON_DISK={edge.NativePartiallyOnDiskBit}; " +
                $"resident={edge.NativeAfter.OnDiskBytes}; availability={edge.AvailabilityAfter}; partial={edge.PartiallyHydrated}");
            // Demand a bounded native range and let the fake source serve only that first request.
            // This must leave real partial residency in the Windows stub, never a simulated state.
            var partial = await FilesOnDemandNativeEdgeChecks.CheckTruePartialRangeAsync(root, provider,
                FilesOnDemandNativeEdgeChecks.LargeEdgeRemote(), () => source.Reads,
                source.ServeOnly, source.RangeRequests, binding.PairId,
                async () =>
                {
                    await FilesOnDemandNativePartialChecks.VerifyAsync(root, pair, verifyStore, edge.PointerBefore,
                        FilesOnDemandNativeEdgeChecks.LargeEdgeRemote(), () => source.Reads,
                        CancellationToken.None);
                    // F4 regression: while genuine partial residency exists, settlement must refuse before
                    // hashing. The refusal must not fetch bytes or change native residency.
                    if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                            Path.Combine(root, edge.PointerBefore.Path), out var beforePartial) ||
                        beforePartial is null ||
                        beforePartial.State.Availability != SyncCloudFileAvailability.PartiallyHydrated)
                        throw new IOException("The partial pointer was not partially hydrated before settlement.");
                    var partialReads = source.Reads;
                    var partialConversionRefused = false;
                    try
                    {
                        await provider.ConvertVerifiedSyncedFileAsync(
                            FilesOnDemandNativeEdgeChecks.LargeEdgeRemote(), dehydrateOverride: true);
                    }
                    catch (IOException) { partialConversionRefused = true; }
                    if (!partialConversionRefused || source.Reads != partialReads ||
                        !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                            Path.Combine(root, edge.PointerBefore.Path), out var stillPartial) ||
                        stillPartial is null ||
                        stillPartial.State.Availability != SyncCloudFileAvailability.PartiallyHydrated ||
                        stillPartial.State.OnDiskBytes != beforePartial.State.OnDiskBytes)
                        throw new IOException(
                            "Settlement hashed a partially hydrated owned placeholder or changed its native residency.");
                },
                CancellationToken.None);
            Console.WriteLine($"PASS native true partial range: resident={partial.NativeAfter.OnDiskBytes} of " +
                $"{FilesOnDemandNativeEdgeChecks.LargeEdgeRemote().Size}; availability={partial.Availability}");
            var recovery = Path.Combine(database, "edge-recovery");
            await FilesOnDemandNativeEdgeChecks.CheckHydratedPointerMutationRefusedAsync(root, local, binding.PairId,
                edge.PointerBefore, () => source.Reads, recovery);
            Console.WriteLine("PASS native hydrated-pointer stale Move/Delete refuse without recall");
            await FilesOnDemandNativeEdgeChecks.CheckFullyHydratedPointerReplacementAsync(root, local, binding.PairId,
                () => source.Reads, recovery);
            Console.WriteLine("PASS native ordinary LocalWriter replacement of a fully hydrated pointer");
            if (!provider.IsConnected)
                throw new IOException("The provider disconnected before exact create-only object hydration.");
            var directBytes = await File.ReadAllBytesAsync(Path.Combine(root, directReceipt.RelativePath));
            if (!directBytes.SequenceEqual(content) || DropboxContentHash.Compute(directBytes) != directReceipt.ContentHash)
                throw new IOException("The explicit create-only fixture object did not hydrate its exact original revision.");
            Console.WriteLine("PASS native create-only object hydrates after the main source changes revision and path");
            await provider.UnregisterAsync(CancellationToken.None);
            if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                throw new IOException("Disposable registration was retained after successful hydration.");
            Console.WriteLine($"PASS Files On-Demand native register/reconnect-before-conversion/edit-race/verified-convert/dehydrate/reconnect-after-conversion/idempotent-retry/callback-hydrate/dehydrated-revision-update/hydration-Retry/hydration-Ignore/unregister; platform {version}");
            if (renameFailure is not null) throw new IOException("Native rename acceptance failed; independent checks continued.", renameFailure);
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
                source.NetworkOutage = false;
                // This fresh fixture owns only tiny synthetic bytes. Hydrate before removing its root.
                // If hydration fails, preserve the pointer rather than deleting a registered root.
                provider.Dispose();
                if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                {
                    using var cleanup = new WindowsCloudFilesProvider(binding, source, root, SyncDirectoryStorage.CloudOnly)
                        { ExpectedRegistrationId = registrationId };
                    cleanup.RegisterAndConnect();
                    await cleanup.UnregisterAsync(CancellationToken.None);
                }
                if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is not null)
                    throw new IOException("Disposable registration remains; root retained.");
                Directory.Delete(root, recursive: true);
                FilesOnDemandNativeEdgeChecks.RemoveControlNamespace(root);
                if (FilesOnDemandNativeEdgeChecks.ControlNamespaceExists(root))
                    throw new IOException("Fixture control namespace remains after cleanup.");
            }
            catch (Exception cleanupError) when (originalFailure is not null)
            {
                throw new AggregateException("Native check failed and fixture cleanup also failed; root may remain.",
                    originalFailure, cleanupError);
            }
        }
    }

    // Hydration notices are persisted by a bounded background writer, so a native read cannot assume the row is
    // visible the instant Windows reports the failed I/O. These waits are bounded, never unconditional sleeps.
    private static async Task<Notice> WaitForHydrationNoticeAsync(SqliteStateStore store, string pairId)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            if (store.OpenNotices(pairId).FirstOrDefault(notice => notice.Storage?.Operation is "hydration" or "hydration-pair") is { } found)
                return found;
            if (Environment.TickCount64 >= deadline)
                throw new IOException("The hydration notice was not persisted within the bounded wait.");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilHydrationNoticesClosedAsync(SqliteStateStore store, string pairId)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            if (!store.OpenNotices(pairId).Any(notice => notice.Storage?.Operation is "hydration" or "hydration-pair")) return;
            if (Environment.TickCount64 >= deadline)
                throw new IOException("The hydration notice did not close within the bounded wait.");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    // Explicit cleanup of an interrupted fixture, never a general root removal tool.
    internal static async Task CleanupAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(root), @"C:\SyncSharkFodTest", StringComparison.OrdinalIgnoreCase) ||
            !DateTime.TryParseExact(Path.GetFileName(root), ["yyyyMMdd-HHmmss", "yyyyMMdd-HHmmss-fff"],
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != "sample.txt" &&
                Path.GetFileName(path) != "root" && !string.Equals(path,
                    FilesOnDemandNativeEdgeChecks.ControlNamespacePath(Path.Combine(root, "root")),
                    StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Not an isolated known fixture; nothing removed.");
        var container = root;
        if (Directory.Exists(Path.Combine(root, "root")))
        {
            root = Path.Combine(root, "root");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) != "sample.txt"))
                throw new IOException("Not an isolated known fixture; nothing removed.");
        }
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
            provider.ExpectedRegistrationId = provider.RegistrationId ??
                throw new IOException("The fixture has no v2 ownership GUID; nothing removed.");
            provider.RegisterAndConnect();
            if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(bytes))
                throw new IOException("Changed fixture bytes retained.");
            await provider.UnregisterAsync(CancellationToken.None);
        }
        lease.Dispose();
        if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)) throw new IOException("Changed fixture bytes retained.");
        Directory.Delete(root, true);
        FilesOnDemandNativeEdgeChecks.RemoveControlNamespace(root);
        if (container != root) Directory.Delete(container, recursive: false);
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
        public bool NetworkOutage;
        private int _serveLimit = -1;
        private int _serveBytesLimit = -1;
        private int _served;
        private int _servedBytes;
        public PilotRemoteEntry Current => entry;
        // Range-subset fixture: -1 serves every requested range; a non-negative limit serves only
        // the first N native range requests, and a positive byte budget bounds the transferred bytes.
        // A FULL-policy root therefore cannot quietly complete the object during the partial check.
        public void ServeOnly(int ranges, int bytes = 0)
        {
            _serveLimit = ranges;
            _serveBytesLimit = bytes > 0 ? bytes : -1;
            _served = 0;
            _servedBytes = 0;
        }
        public async Task<PilotRemoteEntry> UploadAsync(Stream source)
        {
            using var uploaded = new MemoryStream();
            await source.CopyToAsync(uploaded);
            if (!uploaded.ToArray().SequenceEqual(bytes)) throw new IOException("Wrong fixture upload bytes.");
            Uploads++;
            return entry;
        }
        private readonly Dictionary<string, (byte[] Bytes, PilotRemoteEntry Entry)> _previous = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<FilesOnDemandNativeEdgeChecks.RangeRequest> _rangeRequests = new();
        public FilesOnDemandNativeEdgeChecks.RangeRequest[] RangeRequests() => _rangeRequests.ToArray();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Bytes, PilotRemoteEntry Entry)> _extraObjects = new(StringComparer.Ordinal);
        public void AddExactObject(byte[] content, PilotRemoteEntry remote)
        {
            if (remote.Size != content.Length || remote.ContentHash != DropboxContentHash.Compute(content))
                throw new IOException("Additional fixture object does not match its exact receipt.");
            if (!_extraObjects.TryAdd(remote.RelativePath, (content.ToArray(), remote)))
                throw new IOException("Additional fixture path was already registered.");
        }
        public void Replace(byte[] content, PilotRemoteEntry remote)
        {
            _previous[entry.Revision!] = (bytes, entry);
            bytes = content; entry = remote;
        }
        public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset, long length,
            Stream destination, CancellationToken cancellationToken = default)
        {
            if (NetworkOutage) throw new System.Net.Http.HttpRequestException("Synthetic offline provider.");
            if (FailReads) throw new IOException("Synthetic unavailable remote revision; retry when available.");
            var additional = _extraObjects.TryGetValue(expected.RelativePath, out var extra);
            var selected = additional ? extra : expected.Revision == entry.Revision ? (Bytes: bytes, Entry: entry) :
                _previous.GetValueOrDefault(expected.Revision!);
            if (selected.Entry is null || expected.Id != selected.Entry.Id ||
                expected.Revision != selected.Entry.Revision || expected.ContentHash != selected.Entry.ContentHash ||
                expected.Size != selected.Entry.Size)
                throw new IOException("Unexpected fixture revision.");
            // A renamed pointer must request the path of the current moved object. The old native
            // identity keeps the pre-Move path (and may keep the old revision), so a stale request
            // fails hydration instead of being served from the previous-revision entry.
            if (!string.Equals(expected.RelativePath, additional ? selected.Entry.RelativePath : entry.RelativePath,
                    StringComparison.Ordinal))
                throw new IOException("Synthetic provider received a stale placeholder path: " + expected.RelativePath);
            _rangeRequests.Enqueue(new(expected, offset, length));
            if (_serveLimit >= 0 && Interlocked.Increment(ref _served) > _serveLimit)
                throw new IOException("Synthetic provider served only the requested hydration range.");
            var available = length;
            if (_serveBytesLimit >= 0)
            {
                var remaining = _serveBytesLimit - _servedBytes;
                if (remaining <= 0)
                    throw new IOException("Synthetic provider hydration byte budget is exhausted.");
                available = Math.Min(length, remaining);
                _servedBytes += checked((int)available);
            }
            // Count the validated range request before transferring bytes. CfExecute inside
            // WriteAsync can release the native reader before this callback returns, so counting
            // afterwards races the read's assertion that the NEW path reached this source.
            // Path/revision checks above still reject stale identities before they are counted.
            Interlocked.Increment(ref Reads);
            if (available > 0)
                await destination.WriteAsync(selected.Bytes.AsMemory(checked((int)offset),
                    checked((int)available)), cancellationToken);
            if (available != length)
                throw new IOException("Synthetic provider stopped after the requested partial range.");
            return new(selected.Entry.Id!, selected.Entry.Revision!, selected.Entry.Size, selected.Entry.ContentHash!, offset, length);
        }
    }
}
