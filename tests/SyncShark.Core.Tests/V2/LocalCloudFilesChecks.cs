using System.Runtime.InteropServices;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Another provider's Windows cloud-files placeholders fully local ones are ordinary items, an
/// online-only stub is inventoried (never opened or hashed) and replaced by a verified download with the provider
/// absent. Uses real CfApi placeholders under a temporary sync root that this check registers and removes.
/// </summary>
internal static class LocalCloudFilesChecks
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const uint DropboxTag = 0x9000601A;

    public static async Task RunAsync()
    {
        AttributePredicates();
        await JunctionIsLinkAsync().ConfigureAwait(false);
        await RealPlaceholdersAsync().ConfigureAwait(false);
    }

    // Decision 59: a junction is inventoried as one Link item, never followed, and makes nothing incomplete.
    private static async Task JunctionIsLinkAsync()
    {
        var root = LocalChecks.FreshRoot("junction-link");
        LocalChecks.Seed(root, "real/a.txt", "inside");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");
        LocalChecks.Seed(outside, "secret.txt", "outside");
        var junction = Path.Combine(root, "real", "node_modules");
        LocalChecks.CreateJunction(junction, outside);
        try
        {
            using var known = new InMemoryStateStore();
            var snapshot = await LocalChecks.Inventory(root, known, TimeProvider.System)
                .FullAsync(1, _ => true, null!, CancellationToken.None).ConfigureAwait(false);
            LocalChecks.Require(snapshot.IncompleteSubtrees.Count == 0 &&
                snapshot.Items.Any(item => item.Path == "real/node_modules" && item.Flags == ItemFlags.Link &&
                    item.Kind == ItemKind.Folder) &&
                !snapshot.Items.Any(item => item.Path.StartsWith("real/node_modules/", StringComparison.Ordinal)),
                "a junction is one Link item, never followed, and nothing is incomplete");
            LocalChecks.Require(snapshot.Items.Any(item => item.Path == "real/a.txt" && item.Flags == ItemFlags.None),
                "its siblings are inventoried as usual");
        }
        finally
        {
            LocalChecks.RemoveJunction(junction);
        }
    }

    // Fake attribute sources: the classification is a pure function of (attributes, reparse tag).
    private static void AttributePredicates()
    {
        var pinnedDir = FileAttributes.Directory | FileAttributes.Archive | FileAttributes.ReparsePoint | Pinned;
        LocalChecks.Require(PilotPath.IsLocalCloudFilesObject(pinnedDir, DropboxTag) &&
            !PilotPath.IsUnsafeObject(pinnedDir, DropboxTag),
            "a pinned, fully local cloud-files folder (0x80430, CLOUD_6) is an ordinary item");
        LocalChecks.Require(PilotPath.IsCloudFilesReparseTag(0x9000001A) && PilotPath.IsCloudFilesReparseTag(0x9000F01A) &&
            !PilotPath.IsCloudFilesReparseTag(0x9000001B) && !PilotPath.IsCloudFilesReparseTag(0xA0000003),
            "the cloud tag family is IO_REPARSE_TAG_CLOUD and CLOUD_1..F only");
        foreach (var online in new[] { RecallOnDataAccess, RecallOnOpen, FileAttributes.Offline })
        {
            var stub = FileAttributes.Archive | FileAttributes.ReparsePoint | online;
            LocalChecks.Require(PilotPath.IsOnlineOnlyStub(stub, DropboxTag) &&
                !PilotPath.IsLocalCloudFilesObject(stub, DropboxTag) && PilotPath.IsUnsafeObject(stub, DropboxTag),
                $"a cloud placeholder with 0x{(uint)online:x} is an online-only stub, never an ordinary item");
        }
        LocalChecks.Require(PilotPath.IsOnlineOnlyStub(FileAttributes.Archive | RecallOnDataAccess, 0),
            "a registered provider's disguised stub (0x400020, reparse point hidden) is a stub");
        LocalChecks.Require(!PilotPath.IsOnlineOnlyStub(FileAttributes.Archive | FileAttributes.ReparsePoint |
                FileAttributes.Offline, 0x80000004) && !PilotPath.IsOnlineOnlyStub(FileAttributes.Directory | RecallOnOpen, 0),
            "an offline file with another (HSM) tag or an online-only folder is not a stub");
        foreach (var tag in new[] { 0xA0000003u, 0xA000000Cu, 0x80000017u })
            LocalChecks.Require(PilotPath.IsUnsafeObject(FileAttributes.Directory | FileAttributes.ReparsePoint, tag),
                $"reparse tag 0x{tag:x8} (junction, symlink, other) stays unsafe");
        LocalChecks.Require(!PilotPath.IsUnsafeObject(FileAttributes.Archive | Pinned, 0),
            "a pinned plain file with no reparse point is safe");
    }

    private static async Task RealPlaceholdersAsync()
    {
        if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out _))
        {
            Console.WriteLine("SKIP real cloud-files placeholders: the Cloud Filter API is unavailable");
            return;
        }
        var root = LocalChecks.FreshRoot("cloud-files");
        var syncRoot = Path.GetDirectoryName(root)!;
        try { WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(syncRoot); }
        catch (IOException error) when (error.InnerException?.HResult == unchecked((int)0x80070001))
        {
            Console.WriteLine("SKIP real cloud-files placeholders: the cloud filter is not attached to this volume");
            return;
        }
        var hydratedBytes = LocalChecks.Bytes("hydrated cloud bytes");
        var stubBytes = LocalChecks.Bytes("stub cloud bytes that must never be read");
        LocalChecks.Seed(root, "hyd.txt", hydratedBytes);
        LocalChecks.Seed(root, "stub.txt", stubBytes);
        LocalChecks.Seed(root, "stub2.txt", stubBytes);
        LocalChecks.Seed(root, "folder/inner.txt", "inner");
        var registered = false;
        long connection = 0;
        try
        {
            Register(syncRoot);
            registered = true;
            connection = Connect(syncRoot);
            foreach (var path in new[] { root, Path.Combine(root, "folder"), Path.Combine(root, "hyd.txt"),
                         Path.Combine(root, "stub.txt"), Path.Combine(root, "stub2.txt"), Path.Combine(root, "folder", "inner.txt") })
                Convert(path);
            Pin(Path.Combine(root, "hyd.txt"));
            Dehydrate(Path.Combine(root, "stub.txt"));
            Dehydrate(Path.Combine(root, "stub2.txt"));
            Disconnect(ref connection);                            // the provider is now absent

            var stubListed = PilotPath.ListedEntry(Path.Combine(root, "stub.txt"));
            var hydListed = PilotPath.ListedEntry(Path.Combine(root, "hyd.txt"));
            Console.WriteLine($"fixture: stub attrs=0x{(uint)stubListed.Attributes:x} tag=0x{stubListed.ReparseTag:x8}; " +
                $"hydrated attrs=0x{(uint)hydListed.Attributes:x} tag=0x{hydListed.ReparseTag:x8}");
            LocalChecks.Require(PilotPath.IsOnlineOnlyStub(stubListed.Attributes, stubListed.ReparseTag) &&
                !PilotPath.IsUnsafeObject(hydListed.Attributes, hydListed.ReparseTag),
                "fixture: a real online-only stub and a real fully local placeholder exist");

            await InventoryAndReplaceAsync(root, stubBytes, hydratedBytes, "registered, provider disconnected", "stub.txt")
                .ConfigureAwait(false);

            Unregister(syncRoot, ref registered);                  // orphaned like an uninstalled provider
            foreach (var entry in WindowsDirectoryListing.List(root).Concat(WindowsDirectoryListing.List(Path.Combine(root, "folder"))))
                Console.WriteLine($"after unregister: {entry.Name} attrs=0x{(uint)entry.Attributes:x} tag=0x{entry.ReparseTag:x8}");
            var orphan = WindowsDirectoryListing.List(root).FirstOrDefault(entry => entry.Name == "stub2.txt");
            if (orphan is not null && PilotPath.IsOnlineOnlyStub(orphan.Attributes, orphan.ReparseTag))
                await InventoryAndReplaceAsync(root, stubBytes, null, "orphaned (no sync root)", "stub2.txt")
                    .ConfigureAwait(false);
            else
            {
                Console.WriteLine("SKIP orphaned stub: unregistering removed the online-only placeholder on this build");
                using var known = new InMemoryStateStore();
                var orphaned = await LocalChecks.Inventory(root, known, TimeProvider.System)
                    .FullAsync(1, _ => true, null!, CancellationToken.None).ConfigureAwait(false);
                LocalChecks.Require(orphaned.IncompleteSubtrees.Count == 0 &&
                    orphaned.Items.Count(item => item.Path is "folder" or "hyd.txt" && item.Flags == ItemFlags.None) == 2,
                    "after the sync root is removed the remaining items are ordinary, nothing incomplete");
            }
        }
        finally
        {
            if (connection != 0) Disconnect(ref connection);
            if (registered) Unregister(syncRoot, ref registered);
        }
    }

    private static async Task InventoryAndReplaceAsync(string root, byte[] stubBytes, byte[]? hydratedBytes,
        string scenario, string stubPath)
    {
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        LocalChecks.Require(snapshot.IncompleteSubtrees.Count == 0,
            $"{scenario}: placeholders make no incomplete subtree [{string.Join("; ", snapshot.IncompleteSubtrees)}]");
        var stub = snapshot.Items.Single(item => item.Path == stubPath);
        LocalChecks.Require(stub.Flags == ItemFlags.Placeholder && stub.Hash is null && stub.Size == stubBytes.Length,
            $"{scenario}: the online-only stub is inventoried as a placeholder with its logical size, not as absent");
        LocalChecks.Require(snapshot.Items.Any(item => item.Path == "folder/inner.txt" && item.Flags == ItemFlags.None),
            $"{scenario}: a fully local placeholder folder is traversed like a normal folder");
        if (hydratedBytes is not null)
        {
            var hydrated = snapshot.Items.Single(item => item.Path == "hyd.txt");
            LocalChecks.Require(hydrated.Flags == ItemFlags.None && hydrated.Size == hydratedBytes.Length,
                $"{scenario}: a pinned, hydrated placeholder is an ordinary file");
            var hashes = await inventory.HashAsync(new[] { "hyd.txt", stubPath }, CancellationToken.None);
            LocalChecks.Require(hashes.Count == 1 && hashes["hyd.txt"] == LocalChecks.BlockHash(hydratedBytes),
                $"{scenario}: the hydrated placeholder is hashed; the stub is never hashed");
        }
        RequireStillStub(root, stubPath, $"{scenario}: scanning and hashing did not hydrate the stub");

        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        var writer = await LocalChecks.OpenWriter(root, recovery, CancellationToken.None);
        var download = LocalChecks.Bytes("downloaded content from the cloud side, " + scenario);
        var stale = new SyncAction("act-stub-stale", Planner.StubRule, ActionKind.Update, Side.A, stubPath, null,
            LocalWriterChecks.Source(download), stub with { ObjectId = "id:local:00000001:0000000000000001" }, false, null);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            writer.PutAsync(stale, new MemoryStream(download), CancellationToken.None));
        RequireStillStub(root, stubPath, $"{scenario}: a stale stub expectation changes nothing");
        var wrong = new SyncAction("act-stub-wrong", Planner.StubRule, ActionKind.Update, Side.A, stubPath, null,
            LocalWriterChecks.Source(download), stub, false, null);
        await LocalWriterChecks.ExpectAsync<IOException>(() =>
            writer.PutAsync(wrong, new MemoryStream(LocalChecks.Bytes("not the source")), CancellationToken.None));
        RequireStillStub(root, stubPath, $"{scenario}: bytes that do not match the cloud copy never replace the stub");

        var replace = new SyncAction("act-stub", Planner.StubRule, ActionKind.Update, Side.A, stubPath, null,
            LocalWriterChecks.Source(download), stub, false, null);
        var state = await writer.PutAsync(replace, new MemoryStream(download), CancellationToken.None);
        var full = Path.Combine(root, stubPath);
        var listed = PilotPath.ListedEntry(full);
        LocalChecks.Require(File.ReadAllBytes(full).SequenceEqual(download) && state.Hash == LocalChecks.BlockHash(download),
            $"{scenario}: the stub is replaced by the verified downloaded bytes");
        LocalChecks.Require((listed.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline |
                RecallOnDataAccess | RecallOnOpen)) == 0 && listed.ReparseTag == 0 && state.ObjectId != stub.ObjectId,
            $"{scenario}: the result is a fresh regular file with no reparse point or placeholder attribute");
        LocalChecks.Require(!Directory.Exists(recovery) || !Directory.EnumerateFiles(recovery, "*", SearchOption.AllDirectories).Any(),
            $"{scenario}: no Recovery copy is made for a stub");

        if (hydratedBytes is null) return;
        // The writer on a fully local placeholder: update in place, then preserve-to-Recovery and delete.
        var current = (await writer.ObserveAsync("hyd.txt", CancellationToken.None))!;
        var update = LocalChecks.Bytes("updated over a hydrated placeholder");
        var updated = await writer.PutAsync(new SyncAction("act-hyd-update", 2, ActionKind.Update, Side.A, "hyd.txt",
            null, LocalWriterChecks.Source(update), current, false, null), new MemoryStream(update), CancellationToken.None);
        LocalChecks.Require(File.ReadAllBytes(Path.Combine(root, "hyd.txt")).SequenceEqual(update),
            $"{scenario}: a conditional update replaces a pinned hydrated placeholder");
        var inner = (await writer.ObserveAsync("folder/inner.txt", CancellationToken.None))!;
        var archived = await writer.PreserveAsync(inner, "act-inner-preserve", CancellationToken.None);
        LocalChecks.Require(File.Exists(archived.Location) && !File.Exists(Path.Combine(root, "folder", "inner.txt")),
            $"{scenario}: preserve-to-Recovery moves a placeholder out of a placeholder folder");
        await writer.DeleteAsync(new SyncAction("act-inner-preserve", 6, ActionKind.Delete, Side.A, "folder/inner.txt",
            null, null, inner, true, null), CancellationToken.None);
        LocalChecks.Require(updated.Hash == LocalChecks.BlockHash(update), $"{scenario}: the updated state carries the new hash");
    }

    private static void RequireStillStub(string root, string relative, string label)
    {
        var listed = PilotPath.ListedEntry(Path.Combine(root, relative));
        LocalChecks.Require(PilotPath.IsOnlineOnlyStub(listed.Attributes, listed.ReparseTag), label);
    }

    // ---- native fixture (test-only registration of a fresh temporary root) ----

    private static void Register(string syncRoot)
    {
        LocalChecks.Require(WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(syncRoot) is null,
            "fixture: the temporary folder is not already a sync root");
        var identity = Encoding.UTF8.GetBytes("v2-cloudfiles-fixture");
        var pinned = GCHandle.Alloc(identity, GCHandleType.Pinned);
        try
        {
            var registration = new WindowsCloudFilesProvider.Native.SyncRegistration
            {
                StructSize = checked((uint)Marshal.SizeOf<WindowsCloudFilesProvider.Native.SyncRegistration>()),
                ProviderName = "Sync Shark v2 fixture", ProviderVersion = "1",
                SyncRootIdentity = pinned.AddrOfPinnedObject(), SyncRootIdentityLength = (uint)identity.Length,
                FileIdentity = pinned.AddrOfPinnedObject(), FileIdentityLength = (uint)identity.Length
            };
            var policies = new WindowsCloudFilesProvider.Native.SyncPolicies
            {
                StructSize = checked((uint)Marshal.SizeOf<WindowsCloudFilesProvider.Native.SyncPolicies>()),
                Hydration = new WindowsCloudFilesProvider.Native.HydrationPolicy { Primary = 2, Modifier = 0 },
                Population = new WindowsCloudFilesProvider.Native.PopulationPolicy { Primary = 3, Modifier = 0 },
                InSync = 0x00000100, HardLink = 0
            };
            Check(WindowsCloudFilesProvider.Native.CfRegisterSyncRoot(syncRoot, ref registration, ref policies, 0), "register");
        }
        finally { pinned.Free(); }
    }

    private static long Connect(string syncRoot)
    {
        var table = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsCloudFilesProvider.Native.CallbackRegistration>());
        try
        {
            // No callbacks at all: nothing can be fetched even while connected.
            Marshal.StructureToPtr(new WindowsCloudFilesProvider.Native.CallbackRegistration { Type = -1, Callback = IntPtr.Zero },
                table, false);
            Check(WindowsCloudFilesProvider.Native.CfConnectSyncRoot(syncRoot, table, IntPtr.Zero, 0x00000004, out var key),
                "connect");
            return key;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static void Disconnect(ref long connection)
    {
        var key = connection;
        connection = 0;
        Check(WindowsCloudFilesProvider.Native.CfDisconnectSyncRoot(key), "disconnect");
    }

    private static void Unregister(string syncRoot, ref bool registered)
    {
        registered = false;
        Check(WindowsCloudFilesProvider.Native.CfUnregisterSyncRoot(syncRoot), "unregister");
    }

    private static void Convert(string path)
    {
        using var handle = Open(path);
        var identity = Encoding.UTF8.GetBytes("fixture:" + Path.GetFileName(path));
        var pinned = GCHandle.Alloc(identity, GCHandleType.Pinned);
        try
        {
            Check(WindowsCloudFilesProvider.Native.CfConvertToPlaceholder(handle.DangerousGetHandle(),
                pinned.AddrOfPinnedObject(), (uint)identity.Length, 0x00000001, IntPtr.Zero, IntPtr.Zero), "convert " + path);
        }
        finally { pinned.Free(); }
    }

    private static void Pin(string path)
    {
        using var handle = Open(path);
        Check(WindowsCloudFilesProvider.Native.CfSetPinState(handle.DangerousGetHandle(), 1, 0, IntPtr.Zero), "pin");
    }

    private static void Dehydrate(string path)
    {
        using var handle = Open(path);
        Check(WindowsCloudFilesProvider.Native.CfDehydratePlaceholder(handle.DangerousGetHandle(), 0, -1, 0, IntPtr.Zero),
            "dehydrate " + path);
    }

    // Attribute + WRITE_DAC access on the object itself (no data access): what CfApi needs to convert/pin/dehydrate.
    private static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path)
    {
        var handle = WindowsCloudFilesProvider.Native.CreateFile(path, 0x00000080 | 0x00040000, FileShare.None,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("fixture open failed: " + path,
            Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return handle;
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new IOException("Cloud-files fixture step failed: " + step, Marshal.GetExceptionForHR(hr));
    }
}
