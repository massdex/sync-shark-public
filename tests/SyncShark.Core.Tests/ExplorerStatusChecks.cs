using Massdex.SyncShark;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ExplorerStatusChecks
{
    internal static async Task RunAsync(string root)
    {
        var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
        Directory.CreateDirectory(Path.Combine(local, "folder"));
        await File.WriteAllTextAsync(Path.Combine(local, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(local, "folder", "b.txt"), "also same");
        var remote = new FakeSyncRemoteStore(); remote.Put("a.txt", "same"); remote.Put("folder/b.txt", "also same");
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("explorer-pair", "Explorer", local, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            "/Fixture", "id:fixture");
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("explorer-install", "explorer-profile", "test", "{}");
        await db.RegisterPairAsync(pair.Id, 1); await db.RetainPairDefinitionAsync(pair, true);
        await db.RegisterRootBindingAsync("explorer-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { lease.Identity.VolumeSerialNumber, lease.Identity.FileId }), local);
        await db.RegisterRootBindingAsync("explorer-cloud", pair.Id, "dropbox", 1, "{\"folderId\":\"id:fixture\"}", pair.DropboxRoot);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "explorer-local", "explorer-cloud");
        await observer.ObserveAsync("initial"); await observer.ObserveAsync("initial");
        var unaccepted = await db.LoadExplorerStatusPageAsync(pair.Id);
        Check(unaccepted.Any(item => item.RelativePath == "a.txt" && item.Status == "Ready to Check"),
            "Matching observations alone do not falsely claim an accepted synchronization");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        var accepted = await db.LoadExplorerStatusPageAsync(pair.Id);
        Check(accepted.Any(item => item.RelativePath == "a.txt" && item.Status == "Up to Date"),
            "The actual adopted synchronization point publishes Up to Date");
        var pages = new List<SyncExplorerItemStatus>(); string? after = null;
        while (true)
        {
            var page = await db.LoadExplorerStatusPageAsync(pair.Id, after, 1); if (page.Count == 0) break;
            pages.AddRange(page); after = page[^1].RelativePath;
        }
        Check(pages.SequenceEqual(accepted) && pages.Select(item => item.RelativePath).Distinct().Count() == pages.Count,
            "Keyset paging includes files and folders exactly once");
        Check((await db.LoadExplorerStatusPageAsync("different-pair")).Count == 0,
            "A different pair cannot receive these retained item statuses");
        await File.WriteAllTextAsync(Path.Combine(local, "a.txt"), "changed locally");
        await observer.ObserveAsync("manual");
        Check((await db.LoadExplorerStatusPageAsync(pair.Id)).Single(item => item.RelativePath == "a.txt").Status == "Ready to Check",
            "A changed observed version does not reuse the old synchronized status");
        foreach (var value in new[] { "Ready to Check", "Up to Date", "Needs Attention", "Waiting to Sync" })
        {
            Check(SyncExplorerStatus.Display(value, "Paused") == "Paused" && SyncExplorerStatus.Display(value, "Disconnected") == "Disconnected" &&
                SyncExplorerStatus.Display(value, "Up to Date") == value, "Pause/disconnect overrides restore item state: " + value);
        }
        foreach (var value in new[] { "Needs Attention", "Not Selected or Ignored", "Waiting to Sync" })
            Check(SyncExplorerStatus.Combine(value, "Up to Date") == value, "Retained state supersedes stale completed-event status: " + value);
        Check(await File.ReadAllTextAsync(Path.Combine(local, "a.txt")) == "changed locally",
            "Status queries leave actual local content unchanged");
    }

    internal static async Task NativeRootAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-shark-cf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        SyncPair? pair = null;
        try
        {
            using (var root = PilotDirectoryLease.Open(folder))
                pair = new SyncPair("fixture-" + Guid.NewGuid().ToString("N"), "Ordinary Explorer Fixture", folder,
                    root.Identity.VolumeSerialNumber, root.Identity.FileId, "/Fixture", "id:fixture");
            var file = Path.Combine(folder, "sample.txt"); await File.WriteAllTextAsync(file, "ordinary exact fixture bytes");
            var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
            WindowsCloudFilesProvider.RegisterStatusRoot(pair);
            Check(WindowsCloudFilesProvider.IsStatusOnlySyncRoot(pair), "Native root is exact-owned and ALWAYS_FULL");
            Check(!WindowsCloudFilesProvider.IsCloudFilesPlaceholder(file), "Ordinary child remains an ordinary file");
            WindowsCloudFilesProvider.RegisterStatusRoot(pair);
            Check(WindowsCloudFilesProvider.IsStatusOnlySyncRoot(pair), "Status registration safely repeats without changing policy");
            try { WindowsCloudFilesProvider.RegisterStatusRoot(pair with { Id = "different-pair" }); throw new InvalidDataException("Wrong pair accepted"); }
            catch (IOException) { Check(true, "A different pair cannot adopt the registered root"); }
            try { WindowsCloudFilesProvider.UnregisterStatusRoot(pair with { FilesOnDemandEnabled = true }); throw new InvalidDataException("Managed pair removed"); }
            catch (InvalidOperationException) { Check(true, "Managed storage cannot bypass hydration through status cleanup"); }
            WindowsCloudFilesProvider.UnregisterStatusRoot(pair);
            Check(WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(folder) is null &&
                before == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))),
                "Removing native status registration preserves exact file bytes and leaves no root");
            WindowsCloudFilesProvider.UnregisterStatusRoot(pair);
            Check(true, "Exact ordinary-root cleanup is safe to repeat");
        }
        finally
        {
            if (pair is not null && WindowsCloudFilesProvider.IsOwnedSyncRoot(folder, pair.Id))
                WindowsCloudFilesProvider.UnregisterStatusRoot(pair);
            if (pair is not null)
            {
                using (var lease = PilotDirectoryLease.Open(folder, new(pair.LocalVolumeSerialNumber, pair.LocalRootFileId))) lease.VerifyBinding();
                if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(folder) is not null)
                    throw new IOException("Fixture root retained for safe cleanup; registration still exists.");
            }
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); Console.WriteLine("PASS " + message); }
}
