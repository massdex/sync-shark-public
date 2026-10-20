namespace Massdex.SyncShark.V2;

/// <summary>Offline NTFS checks for ignored children carried by enclosing folder operations (RQ-025).</summary>
internal static class LocalIgnoredChildrenChecks
{
    public static async Task RunAsync()
    {
        await DeleteIgnoredChildrenAsync();
        await UnexpectedChildBlocksAsync();
        await RealKindMattersAsync();
        await MoveCarriesIgnoredChildrenAsync();
        await RedirectedChildBlocksAsync();
        await SyncRunLocalFolderDeletionAsync();
        await RunChecks.HeldRecovery_RealIgnoredOnlyFolderAsync();
    }

    public static async Task SyncRunCloudFolderDeletionAsync()
    {
        var setup = await SetupAsync("ignored-sync-run", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/synced.txt", "synced bytes");
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "ignored shell metadata");
        using var store = new InMemoryStateStore();
        var localRoot = LocalChecks.RootOf(setup.Root, "root-local");
        var remoteRoot = new RootDef("root-cloud", SyncProviders.Dropbox, "test-account", "/remote", "id:remote");
        var pair = new PairDef(LocalChecks.PairId, "ignored deletion", localRoot, remoteRoot,
            [], ["FileName:desktop.ini"], true);
        store.SavePair(pair);
        var remote = new FakeEndpoint(Side.B, remoteRoot.RootId) { DropboxHashes = true, RequireHashes = true };
        var provider = new FakeEndpointProvider().Add(remoteRoot, remote)
            .AddWriter(localRoot, setup.Writer)
            .AddInventory(localRoot, LocalChecks.Inventory(setup.Root, store, TimeProvider.System));
        var settings = V2Settings.Default with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()),
            provider, settings, TimeProvider.System);
        var progress = new RecordingProgress();
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, progress, CancellationToken.None);
        var first = await run.ApplyAsync(pair.PairId, plan, progress, CancellationToken.None);
        LocalChecks.Require(first.Outcome == RunOutcomes.Complete &&
            await remote.ObserveAsync("folder/synced.txt", CancellationToken.None) is not null &&
            await remote.ObserveAsync("folder/desktop.ini", CancellationToken.None) is null &&
            File.Exists(Path.Combine(setup.Root, "folder", "desktop.ini")),
            "SyncRun first sync copies ordinary content but leaves desktop.ini local and untouched");
        foreach (var path in new[] { "folder/synced.txt", "folder" })
        {
            var victim = (await remote.ObserveAsync(path, CancellationToken.None))!;
            await remote.DeleteAsync(new SyncAction("external-delete-" + path, 6, ActionKind.Delete,
                Side.B, path, null, null, victim, false, null), CancellationToken.None);
        }
        var deleted = await run.IncrementalAsync(pair.PairId, progress, CancellationToken.None);
        LocalChecks.Require(deleted.Outcome == RunOutcomes.Complete &&
            !Directory.Exists(Path.Combine(setup.Root, "folder")),
            "SyncRun cloud folder deletion physically removes synced files, desktop.ini, and the local folder");
    }

    private static async Task SyncRunLocalFolderDeletionAsync()
    {
        var setup = await SetupAsync("ignored-local-delete-run", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/synced.txt", "synced bytes");
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "ignored shell metadata");
        using var store = new InMemoryStateStore();
        var localRoot = LocalChecks.RootOf(setup.Root, "root-local");
        var remoteRoot = new RootDef("root-cloud", SyncProviders.Dropbox, "test-account", "/remote", "id:remote");
        var pair = new PairDef(LocalChecks.PairId, "local ignored deletion", localRoot, remoteRoot,
            [], ["FileName:desktop.ini"], true);
        store.SavePair(pair);
        var remote = new FakeEndpoint(Side.B, remoteRoot.RootId) { DropboxHashes = true, RequireHashes = true };
        var inventory = new ExplicitFolderDeltaInventory(LocalChecks.Inventory(setup.Root, store, TimeProvider.System));
        var provider = new FakeEndpointProvider().Add(remoteRoot, remote)
            .AddWriter(localRoot, setup.Writer).AddInventory(localRoot, inventory);
        var settings = V2Settings.Default with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()),
            provider, settings, TimeProvider.System);
        var progress = new RecordingProgress();
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, progress, CancellationToken.None);
        var first = await run.ApplyAsync(pair.PairId, plan, progress, CancellationToken.None);
        LocalChecks.Require(first.Outcome == RunOutcomes.Complete &&
            store.Live(localRoot.RootId).Any(item => item.Path == "folder/desktop.ini") &&
            await remote.ObserveAsync("folder/synced.txt", default) is not null &&
            await remote.ObserveAsync("folder/desktop.ini", default) is null,
            "first sync inventories the real ignored local child without uploading it");

        // Preview closes its generations without committing cursors; Apply consumes that plan, not a scan.
        // Establish a committed checkpoint through an ordinary incremental pass before testing the delta.
        var checkpoint = await run.IncrementalAsync(pair.PairId, progress, CancellationToken.None);
        LocalChecks.Require(checkpoint.Outcome == RunOutcomes.Complete && store.Cursor(localRoot.RootId) is not null,
            "ordinary incremental pass commits the local delta checkpoint after first sync");
        var deltaReadsBefore = inventory.DeltaReads;
        Directory.Delete(Path.Combine(setup.Root, "folder"), recursive: true);
        inventory.Deleted = true;
        var deleted = await run.IncrementalAsync(pair.PairId, progress, CancellationToken.None);
        LocalChecks.Require(inventory.DeltaReads > deltaReadsBefore,
            "external local folder deletion is read through the real local delta refresher");
        LocalChecks.Require(deleted.Outcome == RunOutcomes.Complete,
            "external local folder deletion run completes: " + deleted.Outcome);
        LocalChecks.Require(await remote.ObserveAsync("folder", default) is null &&
            await remote.ObserveAsync("folder/synced.txt", default) is null,
            "real external local folder deletion propagates through SyncRun to cloud folder and content");
        LocalChecks.Require(!store.Live(localRoot.RootId).Any(item => PairPath.IsSelfOrUnder(item.Path, "folder")),
            "local folder delta removes ignored inventory ghosts");
    }

    // Feed the real local delta refresher the deleted directory, independent of journal permissions.
    // This deliberately does not request desktop.ini itself: parent deletion must clear descendants.
    private sealed class ExplicitFolderDeltaInventory(LocalInventory inner) : IInventorySource
    {
        public bool Deleted { get; set; }
        public int DeltaReads { get; private set; }
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress,
            CancellationToken ct) => (await inner.FullAsync(gen, include, progress, ct)) with { CursorAfter = "fixture-cursor" };
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
        {
            DeltaReads++;
            return inner.ChangesForPathsAsync(Deleted ? ["folder"] : [], cursor, gen, include, ct);
        }
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
    }

    private static async Task DeleteIgnoredChildrenAsync()
    {
        var setup = await SetupAsync("ignored-delete", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"),
            new SyncIgnoreRule(SyncIgnoreKind.FileName, "large.ini"),
            new SyncIgnoreRule(SyncIgnoreKind.FileName, "small.ini"),
            new SyncIgnoreRule(SyncIgnoreKind.FileExtension, "tmp"),
            new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache"));
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", new byte[64 * 1024]);
        LocalChecks.Seed(setup.Root, "folder/small.ini", new byte[64 * 1024 - 1]);
        var expected = new Dictionary<string, byte[]>
        {
            ["folder/large.ini"] = Enumerable.Repeat((byte)83, 64 * 1024 + 1).ToArray(),
            ["folder/cache/nested/large.bin"] = Enumerable.Repeat((byte)84, 64 * 1024 + 1).ToArray(),
            ["folder/cache/small.bin"] = LocalChecks.Bytes("small folder-rule bytes"),
            ["folder/cache/desktop.ini"] = LocalChecks.Bytes("overlapping FileName and FolderName"),
            ["folder/cache/empty.bin"] = [],
            ["folder/small.tmp"] = LocalChecks.Bytes("extension-only bytes"),
            ["folder/boundary.tmp"] = Enumerable.Repeat((byte)85, 64 * 1024).ToArray()
        };
        foreach (var (path, bytes) in expected) LocalChecks.Seed(setup.Root, path, bytes);
        Directory.CreateDirectory(Path.Combine(setup.Root, "folder", "cache", "empty"));
        await setup.Writer.DeleteAsync(await DeleteActionAsync(setup.Writer), CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(setup.Root, "folder")),
            "enclosing delete physically removes desktop.ini and ignored folder descendants");
        var copies = Directory.GetFiles(setup.Recovery, "*.recovery", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).ToArray();
        LocalChecks.Require(copies.Length == expected.Count &&
            expected.Values.All(bytes => copies.Count(copy => copy.SequenceEqual(bytes)) == 1),
            "Recovery keeps every carried file exactly once except FileName matches at or below 64KiB; " +
            "FolderName wins overlapping small FileName matches and extension-only files always recover");
    }

    private static async Task UnexpectedChildBlocksAsync()
    {
        var setup = await SetupAsync("ignored-unexpected", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "ignored");
        LocalChecks.Seed(setup.Root, "folder/ordinary.txt", "unexpected");
        var action = await DeleteActionAsync(setup.Writer);
        ExpectedTargetMismatchException? mismatch = null;
        try { await setup.Writer.DeleteAsync(action, CancellationToken.None); }
        catch (ExpectedTargetMismatchException error) { mismatch = error; }
        LocalChecks.Require(mismatch is not null &&
            mismatch.Message.Contains("unexpected child 'folder/ordinary.txt'", StringComparison.Ordinal) &&
            !mismatch.Message.Contains("changed kind", StringComparison.Ordinal),
            "an ordinary unexpected child is explained truthfully, not reported as a kind change");
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "folder", "desktop.ini")) == "ignored" &&
            File.ReadAllText(Path.Combine(setup.Root, "folder", "ordinary.txt")) == "unexpected",
            "unexpected nonignored descendants block before any ignored child is removed");
    }

    private static async Task RealKindMattersAsync()
    {
        var folder = await SetupAsync("ignored-folder-kind", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        Directory.CreateDirectory(Path.Combine(folder.Root, "folder", "desktop.ini"));
        var folderAction = await DeleteActionAsync(folder.Writer);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            folder.Writer.DeleteAsync(folderAction, CancellationToken.None));
        LocalChecks.Require(Directory.Exists(Path.Combine(folder.Root, "folder", "desktop.ini")),
            "file-name ignore never treats a same-name folder as ignored");
        var file = await SetupAsync("ignored-file-kind", new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache"));
        LocalChecks.Seed(file.Root, "folder/cache", "ordinary file");
        var fileAction = await DeleteActionAsync(file.Writer);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            file.Writer.DeleteAsync(fileAction, CancellationToken.None));
        LocalChecks.Require(File.Exists(Path.Combine(file.Root, "folder", "cache")),
            "folder-name ignore never treats a same-name file as ignored");
    }

    private static async Task MoveCarriesIgnoredChildrenAsync()
    {
        var setup = await SetupAsync("ignored-move", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "carried");
        var target = (await setup.Writer.ObserveAsync("folder", CancellationToken.None))!;
        await setup.Writer.MoveAsync(new SyncAction("move-ignored", 3, ActionKind.Move, Side.A,
            "moved", "folder", target, target, false, null), CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(setup.Root, "folder")) &&
            File.ReadAllText(Path.Combine(setup.Root, "moved", "desktop.ini")) == "carried",
            "whole-directory move carries ignored bytes without individual actions");
    }

    private static async Task RedirectedChildBlocksAsync()
    {
        var setup = await SetupAsync("ignored-junction", new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache"));
        var outside = Path.Combine(Path.GetDirectoryName(setup.Root)!, "outside");
        LocalChecks.Seed(outside, "sentinel.txt", "unchanged");
        Directory.CreateDirectory(Path.Combine(setup.Root, "folder"));
        var link = Path.Combine(setup.Root, "folder", "cache");
        LocalChecks.CreateJunction(link, outside);
        try
        {
            var action = await DeleteActionAsync(setup.Writer);
            await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
                setup.Writer.DeleteAsync(action, CancellationToken.None));
            LocalChecks.Require(File.ReadAllText(Path.Combine(outside, "sentinel.txt")) == "unchanged",
                "ignored junction does not authorize deletion outside the folder");
        }
        finally { LocalChecks.RemoveJunction(link); }
    }

    private static async Task<SyncAction> DeleteActionAsync(LocalWriter writer) =>
        new("delete-ignored", 6, ActionKind.Delete, Side.A, "folder", null, null,
            (await writer.ObserveAsync("folder", CancellationToken.None))!, false, null);

    private static async Task<LocalWriterChecks.WriterSetup> SetupAsync(string name,
        params SyncIgnoreRule[] rules)
    {
        var root = LocalChecks.FreshRoot(name);
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "root-local"), Side.A,
            LocalChecks.PairId, LocalChecks.InstallationId, recovery, CancellationToken.None, ignoreRules: rules);
        return new(root, recovery, writer);
    }
}
