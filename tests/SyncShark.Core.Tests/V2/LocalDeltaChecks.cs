namespace Massdex.SyncShark.V2;

/// <summary>The local delta checks: a live journal delta and the pure journal-change resolver table.</summary>
internal static class LocalDeltaChecks
{
    private const ulong RootId = 1;
    private const uint Create = 0x100;
    private const uint RenameOld = 0x1000;
    private const uint RenameNew = 0x2000;
    private const uint HardLink = 0x10000;
    private const uint BasicInfo = 0x8000;

    public static async Task RunAsync()
    {
        await LiveJournalNoWrongDeletesAsync().ConfigureAwait(false);
        await RefreshDeleteConstructionAsync().ConfigureAwait(false);
        await RefreshJunctionAncestorIncompleteAsync().ConfigureAwait(false);
        await RefreshAncestorSwapRefusedAsync().ConfigureAwait(false);
        await CaseOnlyRenameThenDelete_ParentRefreshDeletesAsync().ConfigureAwait(false);
        await FolderReplacedByFile_DeletesStoredSubtreeAsync().ConfigureAwait(false);
        await GoneFolder_KeepsUndecidableChildAsync().ConfigureAwait(false);
        await DeepChain_OneDeleteAsync().ConfigureAwait(false);
        ResolverTable();
    }

    // Snapshots the root into a fresh store and returns the inventory, the store and the snapshot cursor.
    private static async Task<(LocalInventory Inventory, InMemoryStateStore Known, string Cursor, Snapshot Snapshot)>
        StoredAsync(string root)
    {
        var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        return (inventory, known, snapshot.CursorAfter, snapshot);
    }

    private static async Task<Delta> RefreshAndWriteAsync(LocalInventory inventory, InMemoryStateStore known,
        string cursor, IReadOnlyList<string> paths, Func<string, bool> include)
    {
        var gen = known.BeginGeneration("root-local", "delta");
        var delta = await inventory.ChangesForPathsAsync(paths, cursor, gen, include, CancellationToken.None);
        known.WriteDelta(delta);
        return delta;
    }

    // Astra re-review 1: a stored folder the refresh finds replaced by a file loses its stored subtree.
    private static async Task FolderReplacedByFile_DeletesStoredSubtreeAsync()
    {
        var root = LocalChecks.FreshRoot("folder-to-file");
        LocalChecks.Seed(root, "sub/F/a.txt", "alpha");
        var (inventory, known, cursor, snapshot) = await StoredAsync(root);
        using var store = known;
        Directory.Delete(Path.Combine(root, "sub", "F"), true);
        LocalChecks.Seed(root, "sub/F", "now a file");
        var delta = await RefreshAndWriteAsync(inventory, known, cursor, ["sub/F"], _ => true);
        var folderId = snapshot.Items.Single(item => item.Path == "sub/F").ObjectId;
        LocalChecks.Require(Deletes(delta).Select(change => (change.Path, change.ObjectId)).SequenceEqual([("sub/F", folderId)]),
            "the replaced folder is deleted by its identity, once, with its stored subtree");
        LocalChecks.Require(known.Live("root-local").Select(item => item.Path).SequenceEqual(["sub", "sub/F"]) &&
            known.Live("root-local", ["sub/F"]).Single().Kind == ItemKind.File,
            "only the new file stays live at sub/F; sub/F/a.txt is gone");
    }

    // Astra re-review 2: a gone folder holding a stored row this refresh may not decide on (outside the selection)
    // is kept, so Storage's cascading folder delete never removes that row; its other children are deleted.
    private static async Task GoneFolder_KeepsUndecidableChildAsync()
    {
        var root = LocalChecks.FreshRoot("gone-protected");
        LocalChecks.Seed(root, "sub/gone/excluded.txt", "kept");
        LocalChecks.Seed(root, "sub/gone/x.txt", "gone");
        var (inventory, known, cursor, _) = await StoredAsync(root);
        using var store = known;
        Directory.Delete(Path.Combine(root, "sub", "gone"), true);
        bool Include(string path) => !path.EndsWith("excluded.txt", StringComparison.Ordinal);
        var delta = await RefreshAndWriteAsync(inventory, known, cursor, ["sub/gone"], Include);
        LocalChecks.Require(Deletes(delta).Select(change => change.Path).SequenceEqual(["sub/gone/x.txt"]),
            "only the decidable child is deleted; the folder holding an excluded row is not cascaded");
        LocalChecks.Require(known.Live("root-local", ["sub/gone/excluded.txt"]).Count == 1,
            "the excluded stored row survives");
    }

    // Astra re-review 4: a removed deep tree whose every level was requested is one Delete, not one per folder.
    private static async Task DeepChain_OneDeleteAsync()
    {
        const int Depth = 40;
        var root = LocalChecks.FreshRoot("deep-chain");
        var levels = Enumerable.Range(1, Depth).Select(depth => string.Join("/", Enumerable.Repeat("c", depth))).ToArray();
        LocalChecks.Seed(root, levels[^1] + "/f.txt", "leaf");
        var (inventory, known, cursor, _) = await StoredAsync(root);
        using var store = known;
        Directory.Delete(Path.Combine(root, "c"), true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var delta = await RefreshAndWriteAsync(inventory, known, cursor, [.. levels, levels[^1] + "/f.txt"], _ => true);
        watch.Stop();
        LocalChecks.Require(Deletes(delta).Select(change => change.Path).SequenceEqual(["c"]),
            "the whole removed tree is one Delete of its top folder");
        LocalChecks.Require(known.Live("root-local").Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(2),
            $"the tree is gone and the refresh stays fast ({watch.ElapsedMilliseconds} ms for {Depth} levels)");
    }

    // B-126 rework 2: a case-only rename then a delete in one journal interval resolves to a refresh of the parent
    // folder; the child was never requested, so the parent's scan must still delete its stored row.
    private static async Task CaseOnlyRenameThenDelete_ParentRefreshDeletesAsync()
    {
        var root = LocalChecks.FreshRoot("rename-then-delete");
        LocalChecks.Seed(root, "sub/File.txt", "alpha");
        LocalChecks.Seed(root, "sub/keep.txt", "kept");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        var stored = snapshot.Items.Single(item => item.Path == "sub/File.txt");
        File.Move(Path.Combine(root, "sub", "File.txt"), Path.Combine(root, "sub", "file.txt"));
        File.Delete(Path.Combine(root, "sub", "file.txt"));

        var context = new ResolveContext(RootId, 1,
            id => id == 7 ? stored : id == 10 ? snapshot.Items.Single(item => item.Path == "sub") : null,
            100, (_, _, _) => null);
        var paths = await LocalDeltaResolver.ResolveAsync(
            [Change(7, 10, "File.txt", RenameOld), Change(7, 10, "file.txt", RenameNew, 11)], context, CancellationToken.None);
        LocalChecks.Require(paths is not null && paths.SequenceEqual(["sub"]), "the case-only rename resolves to its parent");
        var delta = await inventory.ChangesForPathsAsync(paths!, snapshot.CursorAfter, gen + 1, _ => true,
            CancellationToken.None);
        var deletes = Deletes(delta);
        LocalChecks.Require(deletes.Count == 1 && deletes[0].Path == "sub/File.txt" && deletes[0].ObjectId == stored.ObjectId,
            "the parent refresh deletes the stored child it no longer holds, by its exact spelling and identity");
    }

    private static async Task LiveJournalNoWrongDeletesAsync()
    {
        var root = LocalChecks.FreshRoot("live-journal");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, "b.txt", "bravo");
        LocalChecks.Seed(root, "d/b.txt", "nested");
        using var known = new InMemoryStateStore();
        var clock = new LocalChecks.FixedClock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var inventory = LocalChecks.Inventory(root, known, clock);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        if (snapshot.CursorAfter.Length == 0)
        {
            Console.WriteLine("SKIP no journal");
            return;
        }
        LocalChecks.Seed(root, "a.txt", "alpha-edited-longer");
        LocalChecks.Seed(root, "c.txt", "charlie");
        File.Delete(Path.Combine(root, "b.txt"));
        Directory.Move(Path.Combine(root, "d"), Path.Combine(root, "e"));

        var deltaGen = known.BeginGeneration("root-local", "delta");
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, deltaGen, _ => true, CancellationToken.None);
        // A live journal may legitimately fail closed (unnamed records without discovery, wrap or reset); the
        // refresh and Delete construction are proven deterministically below, so only a usable delta is asserted.
        if (delta.CursorInvalid)
        {
            Console.WriteLine("SKIP live-journal delta failed closed (cursor-invalid)");
            return;
        }
        AssertDelta(delta);
        LocalChecks.Require(Deletes(delta).All(change => change.DeletedUtc == clock.GetUtcNow()),
            "every live-journal delete carries the exact scan time as its deletion time");
    }

    // The refresh and Delete construction run through the same seam the journal path uses, without a journal.
    private static async Task RefreshDeleteConstructionAsync()
    {
        var root = LocalChecks.FreshRoot("refresh-delete");
        LocalChecks.Seed(root, "a.txt", "alpha");
        LocalChecks.Seed(root, "b.txt", "bravo");
        LocalChecks.Seed(root, "d/b.txt", "nested");
        using var known = new InMemoryStateStore();
        // Every clock read advances it, so a read before the scan is strictly earlier than one at the scan's start.
        var clock = new LocalChecks.SteppingClock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var inventory = LocalChecks.Inventory(root, known, clock);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        LocalChecks.Seed(root, "a.txt", "alpha-edited-longer");
        LocalChecks.Seed(root, "c.txt", "charlie");
        File.Delete(Path.Combine(root, "b.txt"));
        Directory.Move(Path.Combine(root, "d"), Path.Combine(root, "e"));

        DateTimeOffset? scanStart = null;
        inventory.BeforeRefreshScan = () => scanStart = clock.GetUtcNow();
        var deltaGen = known.BeginGeneration("root-local", "delta");
        var delta = await inventory.ChangesForPathsAsync(
            new[] { "a.txt", "b.txt", "c.txt", "d", "e" }, snapshot.CursorAfter, deltaGen, _ => true, CancellationToken.None);
        AssertDelta(delta);
        var times = Deletes(delta).Select(change => change.DeletedUtc).Distinct().ToList();
        LocalChecks.Require(scanStart is not null && times.Count == 1 && times[0] < scanStart,
            "every local delete carries one deletion time, sampled before the scan begins");
    }

    // An ancestor validated for the refresh stays held, so it cannot be swapped for a junction before the listing.
    private static async Task RefreshAncestorSwapRefusedAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The ancestor-swap check requires Windows reparse points.");
        var root = LocalChecks.FreshRoot("refresh-swap");
        LocalChecks.Seed(root, "p/file.txt", "inside");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");
        LocalChecks.Seed(outside, "file.txt", "outside");
        var folder = Path.Combine(root, "p");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        var expectedId = snapshot.Items.Single(item => item.Path == "p/file.txt").ObjectId;
        var swapped = false;
        inventory.BeforeRefreshScan = () => swapped = TrySwapForJunction(folder, outside);
        try
        {
            var delta = await inventory.ChangesForPathsAsync(
                new[] { "p/file.txt" }, snapshot.CursorAfter, gen + 1, _ => true, CancellationToken.None);
            LocalChecks.Require(!swapped, "a held refresh ancestor cannot be renamed away for a junction swap");
            LocalChecks.Require(delta.Changes.Count == 1 && delta.Changes[0].Kind == ChangeKind.Upsert &&
                delta.Changes[0].ObjectId == expectedId,
                "the refresh lists the validated folder only: no outside item and no false delete");
        }
        finally
        {
            if (swapped) LocalChecks.RemoveJunction(folder);
        }
    }

    private static bool TrySwapForJunction(string folder, string target)
    {
        try
        {
            Directory.Move(folder, folder + "-aside");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        LocalChecks.CreateJunction(folder, target);
        return true;
    }

    // A requested path whose ancestor is replaced by a junction must never be refreshed or deleted.
    private static async Task RefreshJunctionAncestorIncompleteAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The junction-refresh check requires Windows reparse points.");
        var root = LocalChecks.FreshRoot("refresh-junction");
        LocalChecks.Seed(root, "p/file.txt", "inside");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");
        LocalChecks.Seed(outside, "secret.txt", "outside");
        var junction = Path.Combine(root, "p");
        using var known = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(root, known, TimeProvider.System);
        var gen = known.BeginGeneration("root-local", "snapshot");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        known.WriteSnapshot(snapshot);
        known.CompleteGeneration(gen, snapshot.CursorAfter, true);
        Directory.Delete(junction, true);
        LocalChecks.CreateJunction(junction, outside);
        try
        {
            var delta = await inventory.ChangesForPathsAsync(
                new[] { "p/file.txt" }, snapshot.CursorAfter, gen + 1, _ => true, CancellationToken.None);
            LocalChecks.Require(delta.IncompleteSubtrees.Contains("p"),
                "a requested path under a junction-replaced ancestor reports that subtree incomplete");
            LocalChecks.Require(delta.Changes.Count == 0,
                "nothing under an unsafe refresh ancestor is inventoried and no child is deleted");
        }
        finally
        {
            LocalChecks.RemoveJunction(junction);
        }
    }

    private static List<Change> Deletes(Delta delta) =>
        delta.Changes.Where(change => change.Kind == ChangeKind.Delete).ToList();

    private static void AssertDelta(Delta delta)
    {
        var upserts = delta.Changes.Where(change => change.Kind == ChangeKind.Upsert).Select(change => change.Path).ToList();
        var deletes = Deletes(delta);
        LocalChecks.Require(upserts.Contains("a.txt") && upserts.Contains("c.txt") &&
            upserts.Contains("e") && upserts.Contains("e/b.txt"),
            "the delta upserts the edited file, the created file and the renamed folder with its subtree");
        LocalChecks.Require(deletes.Any(change => change.Path == "b.txt") && deletes.Any(change => change.Path == "d"),
            "the delta deletes the removed file and the renamed-away folder path");
        LocalChecks.Require(!deletes.Any(change => change.Path is "a.txt" or "c.txt"),
            "no edit or creation is ever reported as a deletion");
        LocalChecks.Require(delta.Changes.Single(change => change.Kind == ChangeKind.Upsert && change.Path == "a.txt")
                .State!.Size == LocalChecks.Bytes("alpha-edited-longer").Length,
            "an edited file's upsert carries its new size, not a stale one");
    }

    private static void ResolverTable()
    {
        foreach (var row in BuildRows())
        {
            var context = new ResolveContext(RootId, 1,
                id => row.Known.TryGetValue(id, out var state) ? state : null,
                row.MaxPaths, (change, _, _) => row.Discovery is not null &&
                    row.Discovery.TryGetValue(change.FileId, out var hint) && hint.Usn >= change.Usn
                    ? new[] { hint.RelativePath } : null);
            var resolved = LocalDeltaResolver.ResolveAsync(row.Changes, context, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (row.Expected is null)
                LocalChecks.Require(resolved is null, "resolve " + row.Name + " refuses the whole delta");
            else
                LocalChecks.Require(resolved is not null && resolved.SequenceEqual(row.Expected),
                    "resolve " + row.Name + " -> " + string.Join(", ", row.Expected));
        }
    }

    private static List<ResolverRow> BuildRows()
    {
        var empty = new Dictionary<ulong, ItemState>();
        var knownFile = new Dictionary<ulong, ItemState> { [2] = LocalChecks.Item("a.txt", ItemKind.File) };
        var knownFolder = new Dictionary<ulong, ItemState> { [10] = LocalChecks.Item("sub", ItemKind.Folder) };
        var hint = new Dictionary<ulong, SyncDiscoveryItem> { [4] = Hint("sub/hint.txt", 20) };
        return new List<ResolverRow>
        {
            new("KnownContentChange_EmptyName", [Change(2, RootId, "", BasicInfo)], knownFile, null, 100, ["a.txt"]),
            new("NewFileNamed", [Change(3, RootId, "new.txt", Create)], empty, null, 100, ["new.txt"]),
            new("NewFileUnnamed_ParentListing", [Change(4, RootId, "", Create)], empty, hint, 100, ["sub/hint.txt"]),
            new("NewFileUnnamed_NoParentListing", [Change(4, RootId, "", Create)], empty, null, 100, null),
            new("StaleHint", [Change(4, RootId, "", Create, 30)], empty, hint, 100, null),
            new("HardLink", [Change(2, RootId, "x", HardLink)], empty, null, 100, null),
            new("RootRenamed", [Change(RootId, 0, "x", RenameOld)], empty, null, 100, null),
            new("RootTouched", [Change(RootId, 0, "", BasicInfo)], empty, null, 100, []),
            new("UnknownParent", [Change(5, 99, "x", Create)], empty, null, 100, []),
            new("RenameOldNew",
                [Change(6, RootId, "old.txt", RenameOld), Change(6, RootId, "new.txt", RenameNew, 11)],
                empty, null, 100, ["new.txt", "old.txt"]),
            new("CaseOnlyRename",
                [Change(7, 10, "File.txt", RenameOld), Change(7, 10, "file.txt", RenameNew, 11)],
                knownFolder, null, 100, ["sub"]),
            new("CaseOnlyRenameAtRoot",
                [Change(7, RootId, "File.txt", RenameOld), Change(7, RootId, "file.txt", RenameNew, 11)],
                empty, null, 100, null),
            new("InvalidName", [Change(8, RootId, "bad:name", Create)], empty, null, 100, null),
            new("OverMax",
                [Change(20, RootId, "a", Create), Change(21, RootId, "b", Create), Change(22, RootId, "c", Create)],
                empty, null, 2, null),
        };
    }

    private static NtfsUsnChange Change(ulong fileId, ulong parentId, string name, uint reason, long usn = 10) =>
        new(fileId, parentId, usn, reason, FileAttributes.Normal, name);

    private static SyncDiscoveryItem Hint(string path, long usn, uint linkCount = 1) =>
        new(path, 0, 0, usn, FileAttributes.Normal, linkCount, 0, DateTime.UnixEpoch);

    private sealed record ResolverRow(string Name, NtfsUsnChange[] Changes, Dictionary<ulong, ItemState> Known,
        IReadOnlyDictionary<ulong, SyncDiscoveryItem>? Discovery, int MaxPaths, string[]? Expected);
}
