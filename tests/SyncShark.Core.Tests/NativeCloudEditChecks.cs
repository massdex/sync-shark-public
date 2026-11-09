using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

/// <summary>
/// Original H3: CFAPI supplies the real placeholder state; the production scanner, validated reader,
/// public Host and authority planner decide what changed. FakeSyncRemoteStore supplies remote input only.
/// This source is not a native/runtime PASS. Root must run it on an approved fresh NTFS fixture root.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeCloudEditChecks
{
    private const string Subject = "edited.txt";
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("H3 original cloud bytes\n");
    private static readonly byte[] Edited = Encoding.UTF8.GetBytes("H3 genuinely edited hydrated local bytes, longer than the identity blob\n");

    internal static Task RunRegistrationPrerequisiteAsync(string root)
    {
        if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out var platform))
            throw new PlatformNotSupportedException("H3 native registration prerequisite unavailable.");
        root = FreshRoot(root);
        Directory.CreateDirectory(root);
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "registration prerequisite", platform,
            binary = typeof(WindowsCloudFilesProvider).Assembly.Location,
            parentRegistration = WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) }));
        foreach (var retainOuterLease in new[] { false, true })
        {
            var local = Path.Combine(root, retainOuterLease ? "with-outer-lease" : "without-outer-lease");
            Directory.CreateDirectory(local);
            PilotDirectoryIdentity identity;
            using (var initial = PilotDirectoryLease.Open(local)) identity = initial.Identity;
            var prior = WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(local);
            Require(prior is null && !WindowsCloudFilesProvider.IsCloudFilesPlaceholder(local),
                "H3 prerequisite has no native root/ancestor registration before effects");
            var pair = new SyncPair(retainOuterLease ? "61700000000000000000000000000305" : "61700000000000000000000000000304",
                "H3 registration prerequisite", local, identity.VolumeSerialNumber, identity.FileId,
                "/H3Prerequisite", "id:h3-prerequisite", FilesOnDemandEnabled: true);
            using var outer = retainOuterLease ? PilotDirectoryLease.Open(local, identity) : null;
            var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            using var provider = new WindowsCloudFilesProvider(pair, remote,
                Path.Combine(root, retainOuterLease ? "cache-with-lease" : "cache-without-lease"));
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "before registration", retainOuterLease,
                local, identity, prior, attributes = File.GetAttributes(local),
                registrationIdentityHash = DropboxContentHash.Compute(WindowsCloudFileIdentity.Root(pair.Id, pair.Provider).Serialize()) }));
            try
            {
                provider.RegisterAndConnect();
                Require(provider.RegisteredByThisInstance && provider.IsConnected &&
                        WindowsCloudFilesProvider.IsOwnedSyncRoot(local, pair.Id, pair.Provider),
                    "H3 actual direct registration succeeds " + (retainOuterLease ? "with" : "without") + " outer attribute lease");
                outer?.VerifyBinding();
            }
            finally
            {
                if (provider.RegisteredByThisInstance)
                {
                    provider.RollBackNewRegistration();
                    Require(WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(local) is null &&
                            !WindowsCloudFilesProvider.IsOwnedSyncRoot(local, pair.Id, pair.Provider),
                        "H3 prerequisite rolls back only its exact new native registration");
                }
                Require(remote.DownloadCount == 0 && remote.RangeDownloadCount == 0 && remote.UploadCount == 0,
                    "H3 prerequisite performs no transfer or real provider effect");
            }
        }
        return Task.CompletedTask;
    }

    internal static async Task RunAsync(string root, Action<string, string> createDirectoryJunction,
        bool partialOnly = false)
    {
        if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out var platform))
            throw new PlatformNotSupportedException("H3 native Cloud Files prerequisite unavailable; no required case is a PASS.");
        root = FreshRoot(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        Console.WriteLine(JsonSerializer.Serialize(new { original = "H3", platform, root,
            producer = "Windows CFAPI", consumer = "PilotLocalScanner -> Host.RunPairsAsync -> authority planner/executor" }));
        Directory.CreateDirectory(root);
        if (!partialOnly) await PublicHostEditAsync(Path.Combine(root, "public-host"), token);
        await PartialAndForeignRefusalsAsync(Path.Combine(root, "partial-refusal"), createDirectoryJunction, token);
        Console.WriteLine(partialOnly ? "PASS H3 native partial/foreign cases completed; not a repeated public upload proof" :
            "PASS H3 all native cases completed; fixture bytes retained for Root's scoped cleanup");
    }

    private static async Task PublicHostEditAsync(string root, CancellationToken token)
    {
        using var fixture = await HostFixture.CreateAsync(root, token);
        try
        {
            // The first public run creates the real online-only placeholder and its durable baseline.
            // This is the only initial remote input: there is no remote.Put between local edit and upload.
            fixture.Remote.PutBytes(Subject, Original);
            var beforeInitial = Fetches(fixture.Remote);
            var initial = await fixture.RunAsync("initial-online-only", token);
            Require(initial.Decisions.All(d => d.Kind is not (SyncDecisionKind.Blocked or SyncDecisionKind.Conflict)),
                "H3 public initial run is admitted and creates the native placeholder");
            var online = Placeholder(fixture.Target, fixture.Pair);
            Require(online.State.IsInSync && online.State.Availability == SyncCloudFileAvailability.OnlineOnly &&
                    online.State.OnDiskBytes == 0 && online.Identity.ContentHash == DropboxContentHash.Compute(Original),
                "H3 actual native clean online-only state retains the original revision/hash");
            Require(Fetches(fixture.Remote) == beforeInitial && fixture.Remote.UploadCount == 0,
                "H3 native placeholder creation recalls no cloud bytes and uploads nothing");

            using var lease = PilotDirectoryLease.Open(fixture.Pair.LocalRoot,
                new PilotDirectoryIdentity(fixture.Pair.LocalVolumeSerialNumber, fixture.Pair.LocalRootFileId));
            var scanner = new PilotLocalScanner();
            var noRecall = Fetches(fixture.Remote);
            foreach (var incremental in new[] { false, true })
            {
                var scan = await ScanAsync(scanner, lease, fixture.Pair, incremental, token);
                Require(scan.Entries.TryGetValue(Subject, out var entry) && entry.FileId == online.FileId &&
                        entry.ContentHash == online.Identity.ContentHash && entry.Size == Original.Length &&
                        entry.CloudAvailability == SyncCloudFileAvailability.OnlineOnly && scan.FilesHashed == 0,
                    "H3 clean " + (incremental ? "incremental" : "full") + " scan uses native identity metadata without hashing");
            }
            var wrongScan = await scanner.ScanAsync(lease, cancellationToken: token,
                refreshPaths: [Subject], contentProvider: fixture.Pair.Provider, expectedPairId: "h3-wrong-pair");
            Require(!wrongScan.Entries.ContainsKey(Subject) && wrongScan.Decisions.Any(d => d.RelativePath == Subject &&
                    d.Kind is PilotDecisionKind.Blocked or PilotDecisionKind.OnlineOnly),
                "H3 another pair's scanner refuses the owned placeholder instead of claiming its bytes");
            Require(Fetches(fixture.Remote) == noRecall && Placeholder(fixture.Target, fixture.Pair).FileId == online.FileId,
                "H3 clean and wrong-pair scans perform no provider fetch or native identity replacement");

            var hydration = await fixture.Host.SetPathAvailabilityAsync(fixture.Pair.Id, Subject, true, token);
            Require(hydration.Failures.Count == 0, "H3 public Host hydration succeeds through native CFAPI callbacks");
            var hydrated = Placeholder(fixture.Target, fixture.Pair);
            Require(hydrated.State.IsInSync && hydrated.State.Availability == SyncCloudFileAvailability.AvailableOffline &&
                    hydrated.FileId == online.FileId && File.ReadAllBytes(fixture.Target).SequenceEqual(Original) &&
                    Fetches(fixture.Remote) != noRecall,
                "H3 legitimate hydration obtains exact original bytes without changing physical identity");
            var staleIdentity = hydrated.Identity.Serialize();
            EditInPlace(fixture.Target, Edited);
            var dirty = Placeholder(fixture.Target, fixture.Pair);
            Require(!dirty.State.IsInSync && dirty.State.Availability == SyncCloudFileAvailability.AvailableOffline &&
                    dirty.FileId == online.FileId && dirty.Identity.Serialize().SequenceEqual(staleIdentity),
                "H3 real in-place edit leaves the old placeholder identity untouched and clears native in-sync state");
            var editFetches = Fetches(fixture.Remote);
            PilotLocalEntry? editedEntry = null;
            foreach (var incremental in new[] { false, true })
            {
                var scan = await ScanAsync(scanner, lease, fixture.Pair, incremental, token);
                Require(scan.Entries.TryGetValue(Subject, out editedEntry) && editedEntry.FileId == online.FileId &&
                        editedEntry.VolumeSerialNumber == fixture.Pair.LocalVolumeSerialNumber &&
                        editedEntry.ContentHash == DropboxContentHash.Compute(Edited) && editedEntry.Size == Edited.Length &&
                        editedEntry.LastWriteUtc == File.GetLastWriteTimeUtc(fixture.Target) && scan.FilesHashed >= 1,
                    "H3 dirty " + (incremental ? "incremental" : "full") + " scanner records actual bytes/hash/size/mtime and same NTFS ID");
            }
            Require(Fetches(fixture.Remote) == editFetches, "H3 dirty fully resident scans perform no recall");
            await using (var source = await PilotLocalScanner.OpenValidatedForUploadAsync(editedEntry!, lease, token,
                             fixture.Pair.Provider, fixture.Pair.Id))
            {
                using var readback = new MemoryStream();
                await source.CopyToAsync(readback, token);
                Require(readback.ToArray().SequenceEqual(Edited), "H3 pair-bound validated upload reader holds the actual edited bytes");
                await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(source, editedEntry!, lease, token, fixture.Pair.Provider);
            }
            await RequireReaderRefusalAsync(editedEntry!, lease, fixture.Pair.Provider, "h3-wrong-pair", token);
            Require(Fetches(fixture.Remote) == editFetches && Placeholder(fixture.Target, fixture.Pair).FileId == online.FileId,
                "H3 wrong-pair validated reader neither recalls nor replaces the file");

            var uploadBefore = fixture.Remote.UploadCount;
            var uploaded = await fixture.RunAsync("actual-edited-upload", token);
            Require(uploaded.Decisions.Any(d => d.RelativePath == Subject && d.Kind == SyncDecisionKind.Uploaded) &&
                    uploaded.Decisions.All(d => d.RelativePath != Subject || d.Kind is not (SyncDecisionKind.Blocked or SyncDecisionKind.Conflict)),
                "H3 real Host/planner/executor observes the edit as an upload, not unchanged or an unsafe reparse");
            Require(fixture.Remote.UploadCount == uploadBefore + 1 && fixture.Remote.ReadText(Subject) == Encoding.UTF8.GetString(Edited) &&
                    File.ReadAllBytes(fixture.Target).SequenceEqual(Edited),
                "H3 actual transfer transmits edited bytes; the fixture never writes the expected upload into remote input");
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "edited upload readback", before = uploadBefore,
                after = fixture.Remote.UploadCount, actualRemoteHash = DropboxContentHash.Compute(Encoding.UTF8.GetBytes(fixture.Remote.ReadText(Subject))),
                expectedEditedHash = DropboxContentHash.Compute(Edited), fetches = Fetches(fixture.Remote) }));
            var synchronized = Placeholder(fixture.Target, fixture.Pair);
            Require(synchronized.FileId == online.FileId && synchronized.State.IsInSync,
                "H3 successful public upload re-synchronizes the same native file");
            var repeatUploads = fixture.Remote.UploadCount;
            var repeated = await fixture.RunAsync("post-upload-repeat", token);
            Require(fixture.Remote.UploadCount == repeatUploads && repeated.Decisions.All(d =>
                    d.RelativePath != Subject || d.Kind is not (SyncDecisionKind.Uploaded or SyncDecisionKind.Conflict or SyncDecisionKind.Blocked)),
                "H3 durable updated baseline repeats without duplicate upload or false conflict");

            var localConflict = Encoding.UTF8.GetBytes("H3 subsequent in-place local edit conflicts with independently changed remote\n");
            var beforeConflictIdentity = synchronized.Identity.Serialize();
            EditInPlace(fixture.Target, localConflict);
            Require(Placeholder(fixture.Target, fixture.Pair).Identity.Serialize().SequenceEqual(beforeConflictIdentity),
                "H3 concurrent-local fixture still leaves placeholder identity unmodified");
            // This Put is a separately changed SOURCE input, not the expected outcome of a local upload.
            const string remoteConflict = "H3 independent concurrent remote revision\n";
            fixture.Remote.Put(Subject, remoteConflict);
            var effects = Mutations(fixture.Remote);
            var conflict = await fixture.RunAsync("concurrent-edit-conflict", token);
            Require(conflict.Decisions.Any(d => d.RelativePath == Subject && d.Kind == SyncDecisionKind.Conflict),
                "H3 real Host/planner reports both changed sides as Conflict rather than unchanged");
            Require(Mutations(fixture.Remote) == effects && fixture.Remote.ReadText(Subject) == remoteConflict &&
                    File.ReadAllBytes(fixture.Target).SequenceEqual(localConflict) &&
                    Placeholder(fixture.Target, fixture.Pair).FileId == online.FileId,
                "H3 conflict leaves both legitimate changed byte versions and native identity intact, with no remote mutation");
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "conflict no effects", before = effects,
                after = Mutations(fixture.Remote), fetches = Fetches(fixture.Remote) }));
            Require(fixture.Network.Calls == 0 && fixture.Credentials.Mutations == 0,
                "H3 public path never reaches real HTTP or protected credential mutation");
        }
        finally
        {
            // Host owns this registration. Its public disable path preserves fully resident dirty bytes;
            // do not delete reparse children or call another provider's rollback on a Host-owned root.
            if (WindowsCloudFilesProvider.IsOwnedSyncRoot(fixture.Pair.LocalRoot, fixture.Pair.Id, fixture.Pair.Provider))
            {
                var cleanupFetches = Fetches(fixture.Remote);
                await fixture.Host.DisableFilesOnDemandAsync(fixture.Pair.Id, CancellationToken.None);
                Require(!WindowsCloudFilesProvider.IsOwnedSyncRoot(fixture.Pair.LocalRoot, fixture.Pair.Id, fixture.Pair.Provider),
                    "H3 Host-owned native root is unregistered through its actual public lifecycle");
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "Host-owned native cleanup", before = cleanupFetches,
                    after = Fetches(fixture.Remote), retainedRoot = fixture.Pair.LocalRoot }));
            }
        }
    }

    private static async Task PartialAndForeignRefusalsAsync(string root, Action<string, string> createJunction,
        CancellationToken token)
    {
        root = FreshRoot(root);
        var local = Path.Combine(root, "local");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(local); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "H3 foreign subtree sentinel", token);
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("61700000000000000000000000000303", "H3 native partial refusal", local,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/H3Partial", "id:h3-partial-root",
            FilesOnDemandEnabled: true, InitialReviewApproved: true);
        var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        remote.PutBytes("partial.bin", bytes);
        var junction = Path.Combine(local, "foreign");
        var registered = false;
        long connectionKey = 0;
        try
        {
            // A connected FULL root keeps its connected policy even after a registration
            // update. Construct this adversarial input with PROGRESSIVE before its first
            // connection; the production provider's FULL policy must stay unchanged.
            SetPartialFixturePolicy(lease, pair, ref registered);
            var table = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsCloudFilesProvider.Native.CallbackRegistration>());
            try
            {
                Marshal.StructureToPtr(new WindowsCloudFilesProvider.Native.CallbackRegistration
                    { Type = -1, Callback = IntPtr.Zero }, table, false);
                var connect = WindowsCloudFilesProvider.Native.CfConnectSyncRoot(local, table,
                    IntPtr.Zero, 0x00000004, out connectionKey);
                if (connect < 0) throw new IOException("H3 partial fixture connection refused.", Marshal.GetExceptionForHR(connect));
            }
            finally { Marshal.FreeHGlobal(table); }
            Require(connectionKey != 0 && WindowsCloudFilesProvider.IsOwnedSyncRoot(local, pair.Id, pair.Provider),
                "H3 partial fixture owns one fresh exact native root registration");
            var entry = (await remote.ListEntriesAsync(token)).Single(e => e.RelativePath == "partial.bin");
            var target = Path.Combine(local, "partial.bin");
            await File.WriteAllBytesAsync(target, bytes, token);
            var identity = WindowsCloudFileIdentity.FromRemote(pair.Id, entry, pair.Provider).Serialize();
            // Convert ordinary synthetic bytes to a genuinely clean placeholder, then
            // dehydrate only its clean tail. No fetch callback or simulated state exists.
            using (var attributes = WindowsCloudFilesProvider.Native.CreateFile(target, 0x00000080 | 0x00040000,
                       FileShare.None, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                if (attributes.IsInvalid) throw new IOException("H3 partial attribute handle failed.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                var pinned = GCHandle.Alloc(identity, GCHandleType.Pinned);
                try
                {
                    var convert = WindowsCloudFilesProvider.Native.CfConvertToPlaceholder(attributes.DangerousGetHandle(),
                        pinned.AddrOfPinnedObject(), checked((uint)identity.Length), 0x00000001, IntPtr.Zero, IntPtr.Zero);
                    if (convert < 0) throw new IOException("H3 native seeded placeholder conversion failed.", Marshal.GetExceptionForHR(convert));
                }
                finally { pinned.Free(); }
                var hr = WindowsCloudFilesProvider.Native.CfDehydratePlaceholder(attributes.DangerousGetHandle(),
                    65536, bytes.Length - 65536, 0, IntPtr.Zero);
                if (hr < 0) throw new IOException("H3 native clean-tail dehydration failed.", Marshal.GetExceptionForHR(hr));
            }
            var before = Placeholder(target, pair);
            var partial = Placeholder(target, pair);
            Require(partial.State.OnDiskBytes > 0 && partial.State.OnDiskBytes < bytes.Length,
                "H3 actual CFAPI clean-tail dehydration produced genuinely partial resident bytes; no simulated state");
            var beforeEditFetches = Fetches(remote);
            using (var editHandle = WindowsCloudFilesProvider.Native.CreateFile(target, 0x40000000,
                       FileShare.None, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (editHandle.IsInvalid) throw new IOException("H3 no-recall partial edit handle failed.",
                    Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                using var edit = new FileStream(editHandle, FileAccess.Write, 4096, isAsync: false);
                var editedPrefix = new byte[4096];
                Encoding.UTF8.GetBytes("H3 partial dirty bytes", editedPrefix);
                edit.Write(editedPrefix); edit.Flush(flushToDisk: true);
            }
            var dirty = Placeholder(target, pair);
            Require(!dirty.State.IsInSync && dirty.FileId == before.FileId && dirty.State.OnDiskBytes < bytes.Length &&
                    dirty.Identity.Serialize().SequenceEqual(before.Identity.Serialize()) && Fetches(remote) == beforeEditFetches,
                "H3 partial in-place edit is genuinely dirty/partial and does not recall the missing bytes");
            createJunction(junction, outside);
            Require((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0,
                "H3 foreign subtree input is a real native directory reparse, not a mock decision");
            var noRecall = Fetches(remote);
            var effects = Mutations(remote);
            var scanner = new PilotLocalScanner();
            foreach (var refresh in new IReadOnlyList<string>?[] { null, new[] { "partial.bin", "foreign" } })
            {
                var scan = await scanner.ScanAsync(lease, cancellationToken: token, refreshPaths: refresh,
                    contentProvider: pair.Provider, expectedPairId: pair.Id);
                Require(!scan.Entries.ContainsKey("partial.bin") && scan.Decisions.Any(d => d.RelativePath == "partial.bin" && d.Kind == PilotDecisionKind.Blocked),
                    "H3 dirty partial " + (refresh is null ? "full" : "incremental") + " scanner refuses without hashing/recall");
                Require(!scan.Entries.Keys.Any(p => p.StartsWith("foreign/", StringComparison.OrdinalIgnoreCase)) &&
                        scan.Decisions.Any(d => d.RelativePath == "foreign" && d.Kind is PilotDecisionKind.Blocked or PilotDecisionKind.OnlineOnly),
                    "H3 scanner does not follow the real foreign reparse subtree");
            }
            var claimed = new PilotLocalEntry("partial.bin", PilotEntryKind.File, target, bytes.Length,
                File.GetLastWriteTimeUtc(target), entry.ContentHash, lease.Identity.VolumeSerialNumber, dirty.FileId,
                dirty.State.Availability, entry.Revision, entry.Id);
            await RequireReaderRefusalAsync(claimed, lease, pair.Provider, pair.Id, token);
            Require(Fetches(remote) == noRecall && Mutations(remote) == effects &&
                    Placeholder(target, pair).FileId == before.FileId &&
                    await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt"), token) == "H3 foreign subtree sentinel",
                "H3 partial/foreign rejection performs no provider recall/mutation or physical identity/subtree disappearance");
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false); // unlink only our new reparse, never traverse it
            if (connectionKey != 0)
            {
                var disconnect = WindowsCloudFilesProvider.Native.CfDisconnectSyncRoot(connectionKey);
                if (disconnect < 0) throw new IOException("H3 fixture disconnect failed.", Marshal.GetExceptionForHR(disconnect));
            }
            if (registered)
            {
                var cleanupFetches = Fetches(remote);
                lease.VerifyBinding();
                Require(WindowsCloudFilesProvider.IsOwnedSyncRoot(local, pair.Id, pair.Provider),
                    "H3 cleanup rebinds only its fresh exact native registration");
                var unregister = WindowsCloudFilesProvider.Native.CfUnregisterSyncRoot(local);
                if (unregister < 0) throw new IOException("H3 fixture unregister failed.", Marshal.GetExceptionForHR(unregister));
                Require(!WindowsCloudFilesProvider.IsOwnedSyncRoot(local, pair.Id, pair.Provider) && Fetches(remote) == cleanupFetches,
                    "H3 partial fixture unregisters only its owned registration with no recall; synthetic placeholder retention is not asserted");
            }
        }
    }

    private static Task<PilotLocalScan> ScanAsync(PilotLocalScanner scanner, PilotDirectoryLease lease,
        SyncPair pair, bool incremental, CancellationToken token) => scanner.ScanAsync(lease,
        cancellationToken: token, refreshPaths: incremental ? [Subject] : null,
        contentProvider: pair.Provider, expectedPairId: pair.Id);

    private static void SetPartialFixturePolicy(PilotDirectoryLease lease, SyncPair pair, ref bool registered)
    {
        // Adversarial native input only: FULL intentionally completes user access.
        // Do not change the production provider's FULL policy to construct this
        // incomplete byte-edit refusal case. Register only our fresh ordinary root.
        lease.VerifyBinding();
        Require(WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(lease.Path) is null &&
                !WindowsCloudFilesProvider.IsCloudFilesPlaceholder(lease.Path),
            "H3 fixture policy setup starts with no native root or ancestor registration");
        var identity = WindowsCloudFileIdentity.Root(pair.Id, pair.Provider).Serialize();
        var pinned = GCHandle.Alloc(identity, GCHandleType.Pinned);
        try
        {
            var registration = new WindowsCloudFilesProvider.Native.SyncRegistration
            {
                StructSize = checked((uint)Marshal.SizeOf<WindowsCloudFilesProvider.Native.SyncRegistration>()),
                ProviderName = "Sync Shark", ProviderVersion = BuildIdentity.SourceRevision,
                SyncRootIdentity = pinned.AddrOfPinnedObject(), SyncRootIdentityLength = checked((uint)identity.Length),
                FileIdentity = pinned.AddrOfPinnedObject(), FileIdentityLength = checked((uint)identity.Length)
            };
            var policies = new WindowsCloudFilesProvider.Native.SyncPolicies
            {
                StructSize = checked((uint)Marshal.SizeOf<WindowsCloudFilesProvider.Native.SyncPolicies>()),
                Hydration = new WindowsCloudFilesProvider.Native.HydrationPolicy { Primary = 1, Modifier = 0x0004 },
                Population = new WindowsCloudFilesProvider.Native.PopulationPolicy { Primary = 3, Modifier = 0 },
                InSync = 0x00000100, HardLink = 0
            };
            var result = WindowsCloudFilesProvider.Native.CfRegisterSyncRoot(lease.Path,
                ref registration, ref policies, 0x00000004);
            if (result < 0) throw new IOException("H3 test-only PROGRESSIVE registration refused.", Marshal.GetExceptionForHR(result));
            registered = true; // outer finally also rolls back a readback/prerequisite failure
            lease.VerifyBinding();
            var buffer = new byte[8192];
            result = WindowsCloudFilesProvider.Native.CfGetSyncRootInfoByPath(lease.Path, 1, buffer,
                buffer.Length, out var returned);
            if (result < 0) throw new IOException("H3 fixture policy readback refused.", Marshal.GetExceptionForHR(result));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "partial fixture actual native policy readback", result, returned,
                expectedRootId = lease.Identity.FileId,
                nativeRootId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                hydrationPrimary = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)),
                hydrationModifier = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10)),
                owned = WindowsCloudFilesProvider.IsOwnedSyncRoot(lease.Path, pair.Id, pair.Provider)
            }));
            Require(returned is >= 16 and <= 8192 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer) == lease.Identity.FileId &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)) == 1 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10)) == 0x0004 &&
                    WindowsCloudFilesProvider.IsOwnedSyncRoot(lease.Path, pair.Id, pair.Provider),
                "H3 native readback confirms only this exact fixture root uses PROGRESSIVE; production FULL is unchanged");
        }
        finally { pinned.Free(); }
    }

    private static async Task RequireReaderRefusalAsync(PilotLocalEntry entry, PilotDirectoryLease lease,
        string provider, string pairId, CancellationToken token)
    {
        var refused = false;
        try { await using var unexpected = await PilotLocalScanner.OpenValidatedForUploadAsync(entry, lease, token, provider, pairId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { refused = true; }
        Require(refused, "H3 actual validated reader refuses wrong pair or incomplete dirty residency before recall");
    }

    private static WindowsCloudPlaceholderInfo Placeholder(string path, SyncPair pair)
    {
        Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var info) && info is not null &&
                info.Identity.IsBoundTo(pair.Id, pair.Provider), "H3 exact pair/provider-bound native placeholder is readable without recall");
        Console.WriteLine(JsonSerializer.Serialize(new { path, info }));
        return info!;
    }
    private static void EditInPlace(string path, byte[] bytes)
    {
        var oldTime = File.GetLastWriteTimeUtc(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(flushToDisk: true); }
        File.SetLastWriteTimeUtc(path, oldTime.AddSeconds(3));
    }
    private sealed record FetchSnapshot(int Full, int Range, long Bytes);
    private sealed record MutationSnapshot(int Upload, int Archive, int Delete, int Copy, int Move, int Directory);
    private static FetchSnapshot Fetches(FakeSyncRemoteStore remote) =>
        new(remote.DownloadCount, remote.RangeDownloadCount, remote.RangeDownloadBytes);
    private static MutationSnapshot Mutations(FakeSyncRemoteStore remote) =>
        new(remote.UploadCount, remote.ArchiveCount, remote.DeleteCount, remote.VisibleCopyCount, remote.MoveCount, remote.DirectoryCreateCount);
    private static string FreshRoot(string root)
    {
        root = Path.GetFullPath(root);
        var allocation = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build", "home-activity-tray", "ui-integration")) + Path.DirectorySeparatorChar;
        // CFAPI is exercised by the existing Cloud Files contract in a fresh GUID-named
        // Temp fixture. The same binary registers there but rejects the build allocation.
        // Permit that exact existing fixture scheme, not an arbitrary caller-supplied root.
        var temporaryAllocation = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var temporaryMember = root.StartsWith(temporaryAllocation, StringComparison.OrdinalIgnoreCase)
            ? root[temporaryAllocation.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]
            : string.Empty;
        var ownedTemporaryFixture = temporaryMember.StartsWith("sync-shark-cf-", StringComparison.Ordinal) &&
            Guid.TryParseExact(temporaryMember["sync-shark-cf-".Length..], "N", out _);
        if ((!root.StartsWith(allocation, StringComparison.OrdinalIgnoreCase) && !ownedTemporaryFixture) ||
            File.Exists(root) || Directory.Exists(root))
            throw new IOException("H3 requires a fresh controlled allocation or GUID-named Cloud Files Temp fixture.");
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("H3 fixture ancestry is a reparse point; no native effects permitted.");
        return root;
    }
    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidDataException(detail); Console.WriteLine("PASS " + detail); }

    private sealed class HostFixture : IDisposable
    {
        private PilotInstanceLock instance = null!;
        private HttpClient http = null!;
        internal SyncAuthorityRuntimeHost Host = null!;
        internal SyncPair Pair = null!;
        internal readonly FakeSyncRemoteStore Remote = new() { ExactPathMetadataForTest = true };
        internal readonly DenyNetwork Network = new();
        internal readonly SyntheticCredentials Credentials = new();
        internal string Target => Path.Combine(Pair.LocalRoot, Subject);

        internal static async Task<HostFixture> CreateAsync(string root, CancellationToken token)
        {
            root = FreshRoot(root);
            var f = new HostFixture();
            try
            {
                var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
                f.instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
                var store = new SyncPairStore(f.instance.StateAuthority);
                var installation = await store.GetOrCreateInstallationIdAsync(token);
                var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                    "h3-controlled-account", "h3-controlled-root", "h3-controlled-home",
                    DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
                const string pairId = "61700000000000000000000000000302";
                using var lease = PilotDirectoryLease.Open(local);
                var localControl = SyncControlNamespace.BindLocalForTest(lease, installation, pairId);
                var remotePath = SyncControlNamespace.RemoteContainerPath + "/" + pairId;
                var remoteControl = new SyncRemoteControlNamespaceBinding(remotePath, "id:h3-control",
                    remotePath + "/.syncshark-owner.json", "id:h3-marker", "rev:h3-marker",
                    DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installation, pairId)));
                f.Pair = new SyncPair(pairId, "Original H3 actual native edit", local,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/H3Proof", "id:h3-root",
                    ConnectionId: connection.ConnectionId, InitialReviewApproved: true, FilesOnDemandEnabled: true,
                    ControlNamespace: new SyncControlNamespaceBinding(installation, pairId, localControl, remoteControl),
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                        "id:h3-recovery", connection.RootNamespaceId));
                await store.SaveAsync(new SyncPairSet([f.Pair], f.Pair.Id), token);
                await new DropboxConnectionStore(f.instance.StateAuthority).SaveNewAsync(connection, token);
                await new SyncAutomationStore(f.instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false), token);
                f.http = new HttpClient(f.Network, disposeHandler: false);
                var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
                f.Host = new SyncAuthorityRuntimeHost(f.instance.StateAuthority, f.http, store,
                    new SyncRuntimeStateStore(f.instance.StateAuthority), new DropboxConnectionStore(f.instance.StateAuthority),
                    f.Credentials, authority, (_, _) => f.Remote, googleDriveCredentialStore: f.Credentials);
                await f.Host.LoadPairsAsync(token);
                await f.Host.SaveTransferSettingsAsync(f.Host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
                await authority.RetainPairDefinitionAsync(f.Pair, true, token);
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal async Task<SyncRunResult> RunAsync(string phase, CancellationToken token)
        {
            var batch = await Host.RunPairsAsync([Pair], "Manual", cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { phase, entrypoint = "SyncAuthorityRuntimeHost.RunPairsAsync", batch }));
            var item = batch.Items.Single();
            Require(!batch.Stopped && item.FailureType is null && item.Result is not null,
                "H3 public Host bounded run completed without memory admission or runtime failure: " + phase);
            return item.Result!;
        }
        public void Dispose() { Host?.Dispose(); http?.Dispose(); instance?.Dispose(); Network.Dispose(); }
    }
    internal sealed class DenyNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("H3 fixture denies every real HTTP request."); }
    }
    internal sealed class SyntheticCredentials : IDropboxRefreshTokenStore, IGoogleDriveRefreshTokenStore
    {
        internal int Mutations;
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding selected) => this;
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>("h3-controlled-input-not-a-credential");
        public Task<string?> ReadAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("H3 fixture denies Google credentials.");
        public Task WriteAsync(string value, CancellationToken token = default) => Refuse();
        public Task WriteAsync(string id, string value, CancellationToken token = default) => Refuse();
        public Task DeleteAsync(CancellationToken token = default) => Refuse();
        public Task DeleteAsync(string id, CancellationToken token = default) => Refuse();
        private Task Refuse() { Mutations++; throw new InvalidOperationException("H3 fixture denies protected credential mutation."); }
    }
}
