using System.Text;
using Massdex.SyncShark;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class LocalEndpointExactLookupChecks
{
    internal static async Task RunAsync(string fixtureRoot, Action<string, string> createJunction)
    {
        fixtureRoot = Path.GetFullPath(fixtureRoot);
        if (Directory.Exists(fixtureRoot)) throw new IOException("Refuse to reuse an exact-lookup fixture.");
        var rootPath = Directory.CreateDirectory(Path.Combine(fixtureRoot, "endpoint")).FullName;
        var foreignPath = Directory.CreateDirectory(Path.Combine(fixtureRoot, "foreign")).FullName;
        var bytes = Encoding.UTF8.GetBytes("healthy exact metadata payload");
        await File.WriteAllBytesAsync(Path.Combine(rootPath, "healthy.txt"), bytes);
        await File.WriteAllTextAsync(Path.Combine(rootPath, "unrelated.txt"), "unrelated locked payload");
        Directory.CreateDirectory(Path.Combine(rootPath, "folder"));
        ISyncRemoteStore store;
        using (var root = PilotDirectoryLease.Open(rootPath))
        {
            const string installation = "exact-local-installation";
            const string controlId = "exact-local-control";
            var local = SyncControlNamespace.BindLocalForTest(root, installation, controlId);
            store = new SyncLocalEndpointStore(new(rootPath, root.Identity.VolumeSerialNumber,
                root.Identity.FileId), new(installation, controlId, local), SyncProviders.Local);
        }
        var checks = 0;
        void Check(bool condition, string text)
        {
            if (!condition) throw new InvalidDataException(text);
            checks++;
            Console.WriteLine("PASS exact local: " + text);
        }
        async Task Refuses(Func<Task> action, string text)
        {
            var refused = false;
            try { await action(); }
            catch (Exception error) when (error is IOException or DropboxApiException or ArgumentException)
            { refused = true; }
            Check(refused, text);
        }

        PilotRemoteEntry healthy;
        using (var unrelated = new FileStream(Path.Combine(rootPath, "unrelated.txt"), FileMode.Open,
                   FileAccess.Read, FileShare.None))
        {
            healthy = await store.FindEntryAtPathAsync("healthy.txt") ??
                throw new InvalidDataException("A healthy exact file was mistaken for absence.");
            Check(healthy.Kind == PilotEntryKind.File && healthy.Size == bytes.Length &&
                  healthy.ContentHash == await SyncContentIdentity.ComputeAsync(SyncProviders.Local,
                      new MemoryStream(bytes), CancellationToken.None),
                "healthy requested file succeeds while an unrelated file is exclusively locked");
            var exact = await store.ReadEntryAsync("healthy.txt", healthy.Id!);
            Check(exact.Id == healthy.Id && exact.Revision == healthy.Revision,
                "required exact identity and content match through the public store interface");
            var directory = await store.FindEntryAtPathAsync("folder");
            Check(directory is { Kind: PilotEntryKind.Directory, Size: 0 },
                "requested directory is described without scanning unrelated children");
            Check(await store.FindEntryAtPathAsync("missing.txt") is null,
                "explicit missing leaf under the verified root is absence");
            Check(await store.FindEntryAtPathAsync("missing-parent/missing.txt") is null,
                "explicit missing ancestor under the verified root is absence");
        }
        using (var locked = new FileStream(Path.Combine(rootPath, "healthy.txt"), FileMode.Open,
                   FileAccess.Read, FileShare.None))
            await Refuses(async () => _ = await store.FindEntryAtPathAsync("healthy.txt"),
                "sharing failure on the requested target is an error, not absence");
        await Refuses(async () => _ = await store.ReadEntryAsync("healthy.txt", "id:local:stale"),
            "stale expected native object identity is rejected");
        await Refuses(async () => _ = await store.FindEntryAtPathAsync("../foreign/escape.txt"),
            "malformed escaping path is rejected");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var rejected = false;
            try { await store.FindEntryAtPathAsync("healthy.txt", cancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "cancellation remains cancellation, never absence");
        }
        var junction = Path.Combine(rootPath, "foreign-link");
        createJunction(junction, foreignPath);
        try
        {
            await Refuses(async () => _ = await store.FindEntryAtPathAsync("foreign-link"),
                "foreign reparse target is rejected");
            await Refuses(async () => _ = await store.FindEntryAtPathAsync("foreign-link/missing.txt"),
                "foreign reparse ancestor cannot turn an external missing path into local absence");
        }
        finally
        {
            // Remove only this newly created fixture junction, not its target or children.
            if (!junction.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Fixture junction escaped its owned root.");
            Directory.Delete(junction, recursive: false);
        }
        Check(await File.ReadAllTextAsync(Path.Combine(rootPath, "unrelated.txt")) == "unrelated locked payload" &&
              (await File.ReadAllBytesAsync(Path.Combine(rootPath, "healthy.txt"))).SequenceEqual(bytes),
            "all requested reads/refusals preserved both exact user payloads");
        Directory.Move(rootPath, Path.Combine(fixtureRoot, "retained-original"));
        await Refuses(async () => _ = await store.FindEntryAtPathAsync("missing.txt"),
            "missing bound root is an error, not missing target");
        Directory.CreateDirectory(rootPath);
        await Refuses(async () => _ = await store.FindEntryAtPathAsync("missing.txt"),
            "substituted root identity is an error, not missing target");
        // Native long-path regression: an ordinary nested directory whose full path exceeds the classic Win32
        // limit must open, verify and reopen with its exact saved identity, because the lease now sends the
        // extended-length form to CreateFile while every stored/compared path stays normal.
        var longPath = rootPath;
        while (longPath.Length < 300)
            longPath = Directory.CreateDirectory(Path.Combine(longPath, "segment" + longPath.Length)).FullName;
        Check(longPath.Length >= 300,
            "the long-path fixture really exceeds 300 characters (" + longPath.Length + " characters)");
        using (var held = PilotDirectoryLease.Open(longPath))
        {
            held.VerifyBinding();
            var savedLongIdentity = held.Identity;
            using var reopenedLong = PilotDirectoryLease.Open(longPath, savedLongIdentity);
            reopenedLong.VerifyBinding();
            Check(held.Path == longPath && reopenedLong.Path == longPath,
                "long directory opens retain the exact unextended canonical path");
            await Refuses(() =>
            {
                using var wrong = PilotDirectoryLease.Open(longPath,
                    savedLongIdentity with { FileId = savedLongIdentity.FileId ^ 1UL });
                return Task.CompletedTask;
            }, "wrong retained directory identity still refuses at a long path");
            var longFile = Path.Combine(longPath, "exact.txt");
            await File.WriteAllBytesAsync(longFile, bytes);
            using var heldFile = SyncLocalFileAuthority.OpenExistingReadGuard(longFile);
            var savedFileIdentity = heldFile.ReadIdentity();
            var heldDigest = await SyncContentIdentity.ComputeAsync(SyncProviders.Local,
                heldFile.Stream, CancellationToken.None);
            using var reopenedFile = SyncLocalFileAuthority.OpenExistingReadGuard(longFile);
            Check(reopenedFile.ReadIdentity() == savedFileIdentity && reopenedFile.FinalPath == longFile &&
                  heldDigest == await SyncContentIdentity.ComputeAsync(SyncProviders.Local,
                      reopenedFile.Stream, CancellationToken.None) &&
                  heldDigest == await SyncContentIdentity.ComputeAsync(SyncProviders.Local,
                      new MemoryStream(bytes), CancellationToken.None),
                "long file reopens with the exact native identity and matching content digest");
        }
        Check(true, "a >260-character directory opens, verifies and reopens with its exact saved identity");

        var nestedLong = Directory.CreateDirectory(Path.Combine(longPath, "nested")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(nestedLong, "child.txt"), bytes);
        using (var scanRoot = PilotDirectoryLease.Open(longPath))
        {
            var scan = await new PilotLocalScanner().ScanAsync(scanRoot);
            Check(scan.Decisions.Count == 0 && scan.Entries.ContainsKey("nested") &&
                  scan.Entries.ContainsKey("nested/child.txt") && scan.Entries.ContainsKey("exact.txt") &&
                  scan.FilesHashed == 2 && scan.TotalBytes == bytes.Length * 2 &&
                  scan.Entries["nested"].FileId is not null,
                "actual scanner accepts long nested directory identity and both exact file payloads");
        }
        var longMetadata = StorageInventoryWindows.Inspect(Path.Combine(longPath, "exact.txt"));
        Check(!longMetadata.Directory && longMetadata.LogicalBytes == bytes.Length &&
              longMetadata.Path == Path.Combine(longPath, "exact.txt"),
            "no-recall storage metadata opens the long exact file and retains its canonical path");

        Console.WriteLine("PASS local exact lookup: " + checks + " assertions; native targeted success, isolation and safe rejection.");
    }
}
