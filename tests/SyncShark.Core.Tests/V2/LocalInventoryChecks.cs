using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Massdex.SyncShark.V2;

/// <summary>The local inventory checks: full scans, completeness, cursors and hashing.</summary>
internal static class LocalInventoryChecks
{
    public static async Task RunAsync()
    {
        await LocalWalkChecks.RunAsync().ConfigureAwait(false);
        await RemovalSubtreeAsync();
        await FullMapsItemsAsync().ConfigureAwait(false);
        await FullIncludePredicateAsync().ConfigureAwait(false);
        await FullExcludedOfflineDirectoryAsync().ConfigureAwait(false);
        await NoPreOptInOwnedPlaceholderProbeAsync().ConfigureAwait(false);
        await FullNestedPairPrunedAsync().ConfigureAwait(false);
        await RefreshExcludedPathsNotGuardedAsync().ConfigureAwait(false);
        await FullSkipsArtifactsAsync().ConfigureAwait(false);
        await FullAccessDeniedIncompleteAsync().ConfigureAwait(false);
        await FullMissingRootWholeIncompleteAsync().ConfigureAwait(false);
        await FullReplacedRootWholeIncompleteAsync().ConfigureAwait(false);
        await FullCursorAsync().ConfigureAwait(false);
        await DeltaBadCursorInvalidAsync().ConfigureAwait(false);
        await DeltaForeignJournalInvalidAsync().ConfigureAwait(false);
        await HashReturnsBlockHashOmitsChangedAsync().ConfigureAwait(false);
        await HashParallelMatchesSerialAsync().ConfigureAwait(false);
        await HashHardLinkNamesBothHashedAsync().ConfigureAwait(false);
        await HashCancelledThrowsAsync().ConfigureAwait(false);
        HashSettingsRejectedAtConstruction();
    }

    private sealed class ReviewProgress : IProgress<string>
    {
        internal readonly List<string> Messages = [];
        public void Report(string value) => Messages.Add(value);
    }

    private static async Task RemovalSubtreeAsync()
    {
        var root = LocalChecks.FreshRoot("removal-subtree");
        LocalChecks.Seed(root, "folder/nested/file.txt", "alpha");
        LocalChecks.Seed(root, "outside.txt", "outside");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var rows = new List<ItemState>();
        var progress = new ReviewProgress();
        Task Accept(IReadOnlyList<ItemState> page) { rows.AddRange(page); return Task.CompletedTask; }
        await inventory.ReadSubtreeAsync("folder", Accept, default, progress);
        LocalChecks.Require(rows.Select(r => r.Path).Order().SequenceEqual(new[] { "folder", "folder/nested", "folder/nested/file.txt" }),
            "real subtree listing includes nested files but excludes outside siblings");
        LocalChecks.Require(rows.Single(r => r.Kind == ItemKind.File).Hash == LocalChecks.BlockHash(LocalChecks.Bytes("alpha")), "real subtree hashes unknown content");
        known.WriteSnapshot(new Snapshot("root-local", Side.A, 1, "", rows.ToArray(), []));
        rows.Clear(); progress.Messages.Clear();
        await inventory.ReadSubtreeAsync("folder", Accept, default, progress);
        LocalChecks.Require(progress.Messages.Any(m => m.Contains("hashed 0, reused 1")), "unchanged identity size and mtime reuse stored hashes");
        LocalChecks.Seed(root, "folder/nested/file.txt", "changed content");
        rows.Clear(); progress.Messages.Clear();
        await inventory.ReadSubtreeAsync("folder", Accept, default, progress);
        LocalChecks.Require(rows.Single(r => r.Kind == ItemKind.File).Hash == LocalChecks.BlockHash(LocalChecks.Bytes("changed content")) &&
            progress.Messages.Any(m => m.Contains("hashed 1, reused 0")), "changed files are rehashed with visible progress");
        LocalChecks.Seed(root, "folder/locked.txt", "locked");
        using (var locked = new FileStream(Path.Combine(root, "folder/locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            rows.Clear();
            await inventory.ReadSubtreeAsync("folder", Accept, default, progress);
            LocalChecks.Require(rows.Any(r => r.Path == "folder/locked.txt" && r.IncompleteReason is not null), "unreadable child is retained as unverified or incomplete");
            LocalChecks.Require(rows.Any(r => r.Path == "folder/nested/file.txt" && r.Hash is not null), "unreadable child does not stop sibling review");
        }
        rows.Clear();
        await inventory.ReadSubtreeAsync("vanished", Accept, default, progress);
        LocalChecks.Require(rows.Count == 0, "vanished subtree is an empty observation");
    }

    private static void HashSettingsRejectedAtConstruction()
    {
        var root = LocalChecks.FreshRoot("hash-settings");
        using var known = new InMemoryStateStore();
        foreach (var settings in new[]
        {
            V2Settings.Default with { LocalHashWorkers = 0 }, V2Settings.Default with { LocalHashWorkers = -1 },
            V2Settings.Default with { LocalHashBufferBytes = 0 }, V2Settings.Default with { LocalHashBufferBytes = -1 }
        })
        {
            var refused = false;
            try { LocalChecks.Inventory(root, known, TimeProvider.System, settings: settings); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            LocalChecks.Require(refused, $"the constructor refuses LocalHashWorkers={settings.LocalHashWorkers}, " +
                $"LocalHashBufferBytes={settings.LocalHashBufferBytes}");
        }
    }

    private static async Task FullMapsItemsAsync()
    {
        var root = LocalChecks.FreshRoot("full-maps");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, "d/b.txt", "bravo");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Count == 3 &&
            snapshot.Items.Select(item => item.Path).SequenceEqual(LocalChecks.Ordinal("a.txt", "d", "d/b.txt")),
            "a complete local scan maps every file and folder in ordinal order");
        var rootId = LocalChecks.ObjectIdOf(root);
        var volumePrefix = rootId[..(rootId.LastIndexOf(':') + 1)];
        LocalChecks.Require(snapshot.Items.All(item => item.ObjectId.StartsWith(volumePrefix, StringComparison.Ordinal)),
            "every local object id carries the root volume serial and its NTFS file id");
        LocalChecks.Require(snapshot.Items.All(item => item.Hash is null && item.VersionId is null),
            "a metadata scan carries no hash and no version id");
        LocalChecks.Require(snapshot.Items.Single(item => item.Path == "d").Size == 0,
            "a folder is inventoried with size zero");
        var ticks = File.GetLastWriteTimeUtc(Path.Combine(root, "a.txt")).Ticks;
        LocalChecks.Require(snapshot.Items.Single(item => item.Path == "a.txt").Mtime.UtcTicks == ticks,
            "a file's mtime is its exact on-disk write time in UTC ticks");
    }

    private static async Task FullIncludePredicateAsync()
    {
        var root = LocalChecks.FreshRoot("include");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, "d/b.txt", "bravo");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var snapshot = await inventory.FullAsync(1,
            path => path != "d" && !path.StartsWith("d/", StringComparison.Ordinal), null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Count == 1 && snapshot.Items[0].Path == "a.txt",
            "the include predicate removes an excluded folder and its subtree from a full scan");
    }

    // RQ-021/RQ-073: an excluded online-only directory must not become an incomplete subtree.
    private static async Task FullExcludedOfflineDirectoryAsync()
    {
        var root = LocalChecks.FreshRoot("excluded-offline");
        LocalChecks.Seed(root, "visible.txt", "visible");
        LocalChecks.Seed(root, "offline/hidden.txt", "hidden");
        var offline = Path.Combine(root, "offline");
        var attributes = File.GetAttributes(offline);
        File.SetAttributes(offline, attributes | FileAttributes.Offline | FileAttributes.Directory);
        try
        {
            LocalChecks.Require(File.GetAttributes(offline).HasFlag(FileAttributes.Offline),
                "the fixture directory carries the online-only Offline attribute");
            using var known = new InMemoryStateStore();
            var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
            var included = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
            LocalChecks.Require(included.IncompleteSubtrees.Contains("offline"),
                "an unexcluded online-only directory is incomplete rather than silently absent");
            var excluded = await inventory.FullAsync(2,
                path => !PairPath.IsSelfOrUnder(path, "offline"), null!, CancellationToken.None);
            LocalChecks.Require(excluded.IncompleteSubtrees.Count == 0 &&
                excluded.Items.Select(item => item.Path).SequenceEqual(["visible.txt"]),
                "pruning the online-only directory leaves a complete inventory of its visible sibling");
        }
        finally { File.SetAttributes(offline, attributes); }
    }

    // N10-a: before the storage feature is opted in, an ordinary pair's scan must take no extra native
    // attribute/CfGetPlaceholderInfo open per placeholder or reparse entry. After opt-in (an owned
    // pointer row, the same flag the writer's N10 gate uses) the probe is enabled.
    private static async Task NoPreOptInOwnedPlaceholderProbeAsync()
    {
        var root = LocalChecks.FreshRoot("storage-probe-optin");
        var offline = Path.Combine(root, "legacy-offline");
        Directory.CreateDirectory(offline);
        var attributes = File.GetAttributes(offline);
        File.SetAttributes(offline, attributes | FileAttributes.Offline | FileAttributes.Directory);
        try
        {
            LocalChecks.Require(PilotPath.IsUnsafeAttributes(File.GetAttributes(offline)),
                "the fixture entry is an unsafe-attribute placeholder candidate");
            using var known = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
            LocalInventory Inventory()
            {
                var inventory = new LocalInventory(
                    new RootDef("root-local", SyncProviders.Local, null, root, LocalChecks.ObjectIdOf(root)),
                    Side.A, known, new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default,
                    TimeProvider.System, pairId: "pair-local-0001");
                return inventory;
            }
            var probes = new List<string>();
            var preOptIn = Inventory();
            preOptIn.OwnedPlaceholderProbeForTest = probes.Add;
            await preOptIn.FullAsync(1, _ => true, null!, CancellationToken.None);
            LocalChecks.Require(probes.Count == 0,
                "with no storage opt-in the scan takes no owned-placeholder native probe");

            known.EnableStorage();
            var generation = known.BeginGeneration("root-local", "snapshot");
            known.WriteSnapshot(new Snapshot("root-local", Side.A, generation, "",
                [LocalChecks.Item("owned.txt", ItemKind.File) with { Flags = ItemFlags.OwnedPointer }], []));
            known.CompleteGeneration(generation, "", true);
            LocalChecks.Require(known.HasOwnedPointers("root-local"),
                "the synthetic owned pointer row makes the root's opt-in flag true");
            probes.Clear();
            var optedIn = Inventory();
            optedIn.OwnedPlaceholderProbeForTest = probes.Add;
            await optedIn.FullAsync(2, _ => true, null!, CancellationToken.None);
            LocalChecks.Require(probes.Count == 1 && probes[0] == "legacy-offline",
                "after opt-in exactly the unsafe entry is probed for an owned placeholder");
        }
        finally { File.SetAttributes(offline, attributes); }
    }

    private static async Task FullNestedPairPrunedAsync()
    {
        var root = LocalChecks.FreshRoot("nested-pair-pruned");
        LocalChecks.Seed(root, "parent/nested/hidden.txt", "hidden");
        LocalChecks.Seed(root, "parent/visible.txt", "visible");
        var local = new RootDef("root-local", "Local", null, root, LocalChecks.ObjectIdOf(root));
        var remote = new RootDef("remote", "Dropbox", "account", "/Pilot", "id:pilot");
        var pair = new PairDef("parent", "parent", local, remote, [], [], true);
        var nested = new PairDef("nested", "nested",
            local with { RootId = "nested-local", RootPath = Path.Combine(root, "parent", "nested"),
                RootObjectId = LocalChecks.ObjectIdOf(Path.Combine(root, "parent", "nested")) },
            remote with { RootId = "nested-remote", RootPath = "/Elsewhere", RootObjectId = "id:elsewhere" },
            [], [], true);
        var include = PairInclude.For(pair, Side.A, [pair, nested], []);
        var visited = new List<string>();
        using var known = new InMemoryStateStore();
        var snapshot = await LocalChecks.Inventory(root, known, TimeProvider.System).FullAsync(1,
            path => { visited.Add(path); return include(path); }, null!, CancellationToken.None);
        LocalChecks.Require(include("parent") && !include("parent/nested") &&
            snapshot.Items.Select(item => item.Path).SequenceEqual(["parent", "parent/visible.txt"]),
            "the nested pair root is pruned while its ancestor and sibling stay visible");
        LocalChecks.Require(!visited.Any(path => path.StartsWith("parent/nested/", StringComparison.Ordinal)),
            "the scan never visits descendants of the nested pair root");
    }

    private static async Task RefreshExcludedPathsNotGuardedAsync()
    {
        var root = LocalChecks.FreshRoot("refresh-excluded-guard");
        LocalChecks.Seed(root, "parent/private/hidden.txt", "hidden");
        using var known = await SnapshotAsync(root);
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var parent = Path.Combine(root, "parent");
        var moved = Path.Combine(root, "parent-moved");
        var checkedGuard = false;
        inventory.BeforeRefreshScan = () =>
        {
            // A guard for the excluded path would hold even this included ancestor against renaming.
            Directory.Move(parent, moved);
            try { checkedGuard = true; }
            finally { Directory.Move(moved, parent); }
        };
        var delta = await inventory.ChangesForPathsAsync(["parent/private/hidden.txt"], "test-cursor", 2,
            path => !PairPath.IsSelfOrUnder(path, "parent/private"), CancellationToken.None);
        LocalChecks.Require(checkedGuard && !delta.CursorInvalid && delta.Changes.Count == 0 &&
            delta.IncompleteSubtrees.Count == 0,
            "an explicit excluded refresh path acquires no ancestor guard and produces no changes or incompleteness");
    }

    private static async Task FullSkipsArtifactsAsync()
    {
        var root = LocalChecks.FreshRoot("skip-artifacts");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, ".syncshark-recovery-pair00001/old.txt", "old");
        LocalChecks.Seed(root, ".syncshark-transfer-abcdef.partial", "stage");
        // A crashed direct download leaves its partial in the destination's own directory, not the pair root.
        LocalChecks.Seed(root, "d/.syncshark-transfer-abcdef.partial", "nested stage");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Select(item => item.Path).SequenceEqual(LocalChecks.Ordinal("a.txt", "d")),
            "recovery folders and transfer artifacts never appear in a local inventory, including a nested " +
            "crashed-download partial");
    }

    private static async Task FullAccessDeniedIncompleteAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The access-denied completeness check requires Windows NTFS ACLs.");
        var root = LocalChecks.FreshRoot("access-denied");
        LocalChecks.Seed(root, "a.txt", "alpha");
        var locked = Path.Combine(root, "locked");
        LocalChecks.Seed(root, "locked/hidden.txt", "hidden");
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        AddRule(locked, deny);
        try
        {
            using var known = new InMemoryStateStore();
            var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
            var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
            LocalChecks.Require(snapshot.IncompleteSubtrees.Contains("locked"),
                "an access-denied folder is reported as an incomplete subtree");
            LocalChecks.Require(snapshot.Items.Any(item => item.Path == "a.txt"),
                "a sibling of the denied folder is still inventoried");
        }
        finally
        {
            RemoveRule(locked, deny);
        }
    }

    private static async Task FullMissingRootWholeIncompleteAsync()
    {
        var root = LocalChecks.FreshRoot("missing-root");
        LocalChecks.Seed(root, "a.txt", "alpha");
        var rootObjectId = LocalChecks.ObjectIdOf(root);
        Directory.Delete(root, true);
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, rootObjectId, known, TimeProvider.System);
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Count == 0 && snapshot.CursorAfter.Length == 0 &&
            snapshot.IncompleteSubtrees.Count == 1 && snapshot.IncompleteSubtrees[0].Length == 0,
            "a missing root reports the whole root as one incomplete subtree, never a deletion");
    }

    private static async Task FullReplacedRootWholeIncompleteAsync()
    {
        var root = LocalChecks.FreshRoot("replaced-root");
        LocalChecks.Seed(root, "a.txt", "alpha");
        var rootObjectId = LocalChecks.ObjectIdOf(root);
        Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, rootObjectId, known, TimeProvider.System);
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Count == 0 && snapshot.IncompleteSubtrees.Count == 1 &&
            snapshot.IncompleteSubtrees[0].Length == 0,
            "a replaced root identity reports the whole root incomplete, never a deletion");
    }

    private static async Task FullCursorAsync()
    {
        var root = LocalChecks.FreshRoot("cursor");
        LocalChecks.Seed(root, "a.txt", "alpha");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        if (snapshot.CursorAfter.Length > 0)
            LocalChecks.Require(NtfsUsnCheckpoint.TryParseCursor(snapshot.CursorAfter, out _, out _),
                "a full scan's cursor parses as an NTFS journal cursor");
        else
            LocalChecks.Require(!NtfsUsnJournal.TryReadCheckpoint(root, out _),
                "a full scan reports an empty cursor only when the journal checkpoint is unavailable");
    }

    private static async Task DeltaBadCursorInvalidAsync()
    {
        var root = LocalChecks.FreshRoot("bad-cursor");
        LocalChecks.Seed(root, "a.txt", "alpha");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        LocalChecks.Require((await inventory.ChangesSinceAsync("", 1, _ => true, CancellationToken.None)).CursorInvalid,
            "an empty cursor is invalid");
        LocalChecks.Require((await inventory.ChangesSinceAsync("zz", 1, _ => true, CancellationToken.None)).CursorInvalid,
            "a malformed cursor is invalid");
    }

    private static async Task DeltaForeignJournalInvalidAsync()
    {
        var root = LocalChecks.FreshRoot("foreign-journal");
        LocalChecks.Seed(root, "a.txt", "alpha");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var delta = await inventory.ChangesSinceAsync("0000000000000001:0", 1, _ => true, CancellationToken.None);
        LocalChecks.Require(delta.CursorInvalid,
            "a cursor from another journal is invalid, forcing a full scan");
    }

    private static async Task HashReturnsBlockHashOmitsChangedAsync()
    {
        var root = LocalChecks.FreshRoot("hash");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, "b.txt", "bravo");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        LocalChecks.Seed(root, "b.txt", "bravo-changed");
        var hashes = await inventory.HashAsync(new[] { "a.txt", "b.txt" }, CancellationToken.None);
        LocalChecks.Require(hashes.Count == 1 && hashes["a.txt"] == LocalChecks.BlockHash(LocalChecks.Bytes("alpha")),
            "hashing returns the Dropbox block hash of an unchanged file");
        LocalChecks.Require(!hashes.ContainsKey("b.txt"),
            "hashing omits a file whose size or identity changed since the inventory");
    }

    private static async Task HashParallelMatchesSerialAsync()
    {
        var root = LocalChecks.FreshRoot("hash-parallel");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Sizes cross the small read chunk and the 4 MiB Dropbox block boundary; one file is empty.
        int[] sizes = [0, 1, 4095, 4096, 4097, 65_536, 300_000, DropboxContentHash.BlockSize + 1];
        for (var index = 0; index < 24; index++)
        {
            var bytes = new byte[sizes[index % sizes.Length]];
            new Random(index).NextBytes(bytes);
            var path = $"d{index % 3}/f{index:D2}.bin";
            LocalChecks.Seed(root, path, bytes);
            expected[path] = LocalChecks.BlockHash(bytes);
        }
        using var known = await SnapshotAsync(root).ConfigureAwait(false);
        var paths = expected.Keys.ToArray();
        var serial = await LocalChecks.Inventory(root, known, TimeProvider.System, settings: V2Settings.Default with
            { LocalHashWorkers = 1, LocalHashBufferBytes = 4096 }).HashAsync(paths, CancellationToken.None);
        var parallel = await LocalChecks.Inventory(root, known, TimeProvider.System, settings: V2Settings.Default with
            { LocalHashWorkers = 8 }).HashAsync(paths, CancellationToken.None);
        LocalChecks.Require(serial.Count == expected.Count && expected.All(pair => serial[pair.Key] == pair.Value),
            "one hashing worker with a small read chunk returns every file's exact block hash");
        LocalChecks.Require(parallel.Count == expected.Count && expected.All(pair => parallel[pair.Key] == pair.Value),
            "parallel hashing workers return the same hashes, independent of order and read chunk");
    }

    private static async Task HashHardLinkNamesBothHashedAsync()
    {
        var root = LocalChecks.FreshRoot("hash-hardlink");
        LocalChecks.Seed(root, "a.bin", LocalChecks.Bytes("one physical file"));
        LocalChecks.CreateHardLink(Path.Combine(root, "b.bin"), Path.Combine(root, "a.bin"));
        using var known = await SnapshotAsync(root).ConfigureAwait(false);
        var hashes = await LocalChecks.Inventory(root, known, TimeProvider.System, settings: V2Settings.Default with
            { LocalHashWorkers = 2 }).HashAsync(new[] { "a.bin", "b.bin" }, CancellationToken.None);
        var hash = LocalChecks.BlockHash(LocalChecks.Bytes("one physical file"));
        LocalChecks.Require(hashes.Count == 2 && hashes["a.bin"] == hash && hashes["b.bin"] == hash,
            "two names of one physical file in different workers are both hashed (a refused open is retried alone)");
    }

    private static async Task HashCancelledThrowsAsync()
    {
        var root = LocalChecks.FreshRoot("hash-cancel");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, "b.txt", "bravo");
        using var known = await SnapshotAsync(root).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var thrown = false;
        try
        {
            await LocalChecks.Inventory(root, known, TimeProvider.System)
                .HashAsync(new[] { "a.txt", "b.txt" }, cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            thrown = true;
        }
        LocalChecks.Require(thrown, "cancelled hashing throws instead of returning a partial result");
    }

    /// <summary>A store holding one complete snapshot of the root, as Run writes it before asking for hashes.</summary>
    private static async Task<InMemoryStateStore> SnapshotAsync(string root)
    {
        var known = new InMemoryStateStore();
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await LocalChecks.Inventory(root, known, TimeProvider.System)
            .FullAsync(gen, _ => true, null!, CancellationToken.None).ConfigureAwait(false);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        return known;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRule(string path, FileSystemAccessRule rule)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(rule);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveRule(string path, FileSystemAccessRule rule)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.RemoveAccessRule(rule);
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
