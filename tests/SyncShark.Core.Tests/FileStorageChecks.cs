using Massdex.SyncShark;

internal static class FileStorageChecks
{
    internal static async Task RunAsync(string evidenceRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-shark-file-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool condition, string title)
        { if (!condition) throw new InvalidDataException(title); count++; Console.WriteLine("PASS: " + title); }
        void Reject(Action action, string title)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            { Check(true, title); return; }
            throw new InvalidDataException("Expected rejection: " + title);
        }
        try
        {
            using var lease = PilotDirectoryLease.Open(root);
            var pair = new SyncPair("file-storage-check", "File Storage Check", root, lease.Identity.VolumeSerialNumber,
                lease.Identity.FileId, "/fixture", "id:root", DefaultStorage: SyncDirectoryStorage.CloudOnly);
            var path = Path.Combine(root, "chosen.txt");
            await File.WriteAllTextAsync(path, "fixture data");
            var identity = WindowsCloudFilesProvider.ReadStorageFileIdentity(path, pair.Id);
            pair = pair with { FileStorageRules = [new("chosen.txt", SyncDirectoryStorage.AlwaysAvailable, identity.LocalId, null)] };
            Check(identity.LocalId != 0, "Real ordinary file identity is readable without conversion");
            Check(SyncDirectoryStoragePolicy.Resolve(pair, "chosen.txt") == SyncDirectoryStorage.AlwaysAvailable, "Actual selected file uses its explicit offline preference");
            Check(SyncDirectoryStoragePolicy.Resolve(pair, "new-download.txt") == SyncDirectoryStorage.CloudOnly, "Unrelated missing download still inherits its cloud-only folder default");
            File.Move(path, Path.Combine(root, "renamed.txt"));
            Check(SyncDirectoryStoragePolicy.Resolve(pair, "renamed.txt") == SyncDirectoryStorage.AlwaysAvailable, "Preference follows the real file identity after a rename");
            Check(SyncDirectoryStoragePolicy.Resolve(pair, "chosen.txt") == SyncDirectoryStorage.AlwaysAvailable, "Unresolved old binding is conservatively retained offline");
            await File.WriteAllTextAsync(path, "a different file");
            Check(SyncDirectoryStoragePolicy.Resolve(pair, "chosen.txt") == SyncDirectoryStorage.CloudOnly, "A different file at the old name does not inherit the old file preference");
            Directory.CreateDirectory(Path.Combine(root, "folder"));
            Check(SyncDirectoryStoragePolicy.Resolve(pair, "folder") == SyncDirectoryStorage.CloudOnly, "Directory does not accidentally match a file-only identity");
            Check(WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(root) is null, "Native API distinguishes an ordinary directory from a registered cloud root");
            Check(await File.ReadAllTextAsync(Path.Combine(root, "renamed.txt")) == "fixture data", "Preference inspection leaves file content unchanged");
            Reject(() => SyncDirectoryStoragePolicy.NormalizeFiles([new("x", (SyncDirectoryStorage)19, 1, null)]), "Unknown storage choice is rejected");
            Reject(() => SyncDirectoryStoragePolicy.NormalizeFiles([new("../escape", SyncDirectoryStorage.CloudOnly, 1, null)]), "File preference cannot escape its pair");
            Reject(() => SyncDirectoryStoragePolicy.NormalizeFiles([new("x", SyncDirectoryStorage.CloudOnly, 0, null)]), "Unbound file preference is rejected");
            Reject(() => SyncDirectoryStoragePolicy.NormalizeFiles([new("x", SyncDirectoryStorage.CloudOnly, 1, null), new("y", SyncDirectoryStorage.AlwaysAvailable, 1, null)]), "Conflicting same-file local identities are rejected");
            Reject(() => SyncDirectoryStoragePolicy.NormalizeFiles([new("x", SyncDirectoryStorage.CloudOnly, 1, "id:one"), new("y", SyncDirectoryStorage.AlwaysAvailable, 2, "id:one")]), "Conflicting same-file cloud identities are rejected");
            Reject(() => SyncDirectoryStoragePolicy.NormalizeFiles([new("Ä.txt", SyncDirectoryStorage.CloudOnly, 1, null), new("ä.txt", SyncDirectoryStorage.AlwaysAvailable, 2, null)]), "File paths use Windows-compatible non-ASCII ordinal case semantics");
            Check(SyncDirectoryStoragePolicy.NormalizeFiles([new("folder\\item.txt", SyncDirectoryStorage.CloudOnly, 1, null)])[0].RelativePath == "folder/item.txt", "Saved preference paths are canonicalized");
            var initial = new SyncFileStorageRule("chosen.txt", SyncDirectoryStorage.AlwaysAvailable, identity.LocalId, null);
            var promoted = SyncFileStorageBinding.Rebind([initial], [new("renamed.txt", identity.LocalId, "id:uploaded")]).Single();
            Check(promoted.RelativePath == "renamed.txt" && promoted.RemoteFileId == "id:uploaded", "A completed upload binds the actual cloud identity and follows a rename");
            var replaced = SyncFileStorageBinding.Rebind([promoted], [new("renamed.txt", 999, "id:uploaded")]).Single();
            Check(replaced.LocalFileId == 999 && replaced.Storage == SyncDirectoryStorage.AlwaysAvailable, "An exact same-cloud-file replacement retains its offline preference");
            Check(SyncFileStorageBinding.Rebind([promoted], [new("renamed.txt", 999, "id:other")]).Single() == promoted,
                "A different file at a reused path cannot steal a retained preference");
            Check(SyncFileStorageBinding.Rebind([promoted], [new("one.txt", identity.LocalId, "id:uploaded"), new("two.txt", identity.LocalId, "id:uploaded")]).Single() == promoted,
                "Ambiguous hard-link paths do not rebind an explicit preference");
            Check(SyncDirectoryStoragePolicy.Resolve(pair with { FileStorageRules = null }, "renamed.txt") == SyncDirectoryStorage.CloudOnly,
                "Removing a file override reveals the folder preference without changing participation");
            var scoped = await new PilotLocalScanner().ScanAsync(lease, refreshPaths: ["renamed.txt"], includePath: item => item == "renamed.txt");
            Check(scoped.FilesHashed == 1 && scoped.PathsInspected == 1 && scoped.Entries.Count == 1 && scoped.Entries.ContainsKey("renamed.txt"),
                "Selected-file availability inventory neither visits nor hashes unrelated files or directories");
            Directory.CreateDirectory(evidenceRoot);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "file-storage-checks.json"),
                System.Text.Json.JsonSerializer.Serialize(new { passed = count, source = "production storage policy and native attribute/root APIs", providerEffects = 0 }));
            Console.WriteLine($"File storage checks passed: {count}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
