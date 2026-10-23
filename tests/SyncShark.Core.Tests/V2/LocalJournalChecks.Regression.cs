using System.Runtime.InteropServices;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    internal static Task RegressionAsync(string name, string folder) => name switch
    {
        "protection-index" => ProtectionIndexAsync(folder),
        "gap-store" => GapStoreAsync(folder),
        "hardlinks" => HardLinksAsync(folder), "rehash" => RehashDebtAsync(folder),
        "crash" => CrashDrainAsync(folder), "protection" => ProtectionScaleAsync(folder), "drain" => DrainAsync(folder), "vanish" => VanishAsync(folder),
        "physical-large" => PhysicalFolderAsync(folder, 200_000), "physical" => PhysicalFolderAsync(folder), _ => throw new ArgumentException("Unknown regression", nameof(name))
    };

    private static async Task<Snapshot> SaveFull(LocalInventory inventory, SqliteStateStore store)
    {
        var snapshot = await inventory.FullAsync(store.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(snapshot.IncompleteSubtrees.Count == 0 && snapshot.CursorAfter.Length > 0, "complete real journal fixture");
        store.WriteSnapshot(snapshot); store.CompleteGeneration(snapshot.Gen, snapshot.CursorAfter, true);
        return snapshot;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    private static async Task HardLinksAsync(string folder)
    {
        await OutsideAliasAsync(Path.Combine(folder, "outside-case"));
        await MovedInAliasesAsync(Path.Combine(folder, "moved-in"));
        var root = Path.Combine(folder, "root");
        Directory.CreateDirectory(Path.Combine(root, "b"));
        LocalChecks.Seed(root, "a/one.txt", "aaa");
        LocalChecks.Require(CreateHardLink(Path.Combine(root, "b/two.txt"), Path.Combine(root, "a/one.txt"), IntPtr.Zero), "create real NTFS hard link");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        var snapshot = await SaveFull(inventory, store);
        var one = store.Live("root-local", ["a/one.txt"]).Single();
        store.WriteDelta(new Delta("root-local", Side.A, store.BeginGeneration("root-local", "delta"), "", "",
            [new Change(ChangeKind.Upsert, one.ObjectId, one.Path, one, null)], false, []));
        LocalChecks.Require(store.Live("root-local", ["b/two.txt"]).Count == 1, "direct upsert never removes a hard-link sibling");
        var mtime = File.GetLastWriteTimeUtc(Path.Combine(root, "a/one.txt"));
        LocalChecks.Seed(root, "a/one.txt", "bbb"); File.SetLastWriteTimeUtc(Path.Combine(root, "a/one.txt"), mtime);
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Contains("a/one.txt") && delta.IncompleteSubtrees.Contains("b/two.txt"), "edit of a multiple-link ID retains scoped debt without rejecting replay");
        snapshot = await SaveFull(inventory, store);
        LocalChecks.Require(store.Live("root-local", ["a/one.txt", "b/two.txt"]).Count == 2, "full reconciliation retains both aliases");
        LocalChecks.Require(store.Live("root-local", ["a/one.txt", "b/two.txt"]).All(i => (i.Flags & ItemFlags.NeedsRehash) != 0), "authoritative hidden edit debt survives hardlink fallback for both aliases");
        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var aliasRetry), "alias retry checkpoint");
        delta = await inventory.ChangesSinceAsync(aliasRetry!.Cursor, store.BeginGeneration("root-local", "retry"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid, "retry-only known aliases do not force endless full scans");
        store.WriteDelta(delta);
        await inventory.VerifyIndexingPassAsync(default);
        await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
        LocalChecks.Require(store.Live("root-local", ["a/one.txt", "b/two.txt"]).All(i => i.Hash == DropboxContentHash.Compute(LocalChecks.Bytes("bbb")) && (i.Flags & ItemFlags.NeedsRehash) == 0), "verified alias hash clears debt for all reconciled names");
        snapshot = snapshot with { CursorAfter = delta.CursorAfter };
        File.Move(Path.Combine(root, "b/two.txt"), Path.Combine(root, "b/renamed.txt"));
        delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Contains("a/one.txt") && delta.IncompleteSubtrees.Contains("b/two.txt") && delta.IncompleteSubtrees.Contains("b/renamed.txt"), "rename alias retains old names and discovers the new name as scoped debt");
        snapshot = await SaveFull(inventory, store);
        LocalChecks.Require(store.Live("root-local", ["a/one.txt", "b/renamed.txt"]).Count == 2 && store.Live("root-local", ["b/two.txt"]).Count == 0, "rename does not remove surviving alias or retain phantom");
        File.Delete(Path.Combine(root, "b/renamed.txt")); // test-owned fixture only
        delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count > 0, "link removal retains scoped reconciliation debt");
        await SaveFull(inventory, store);
        LocalChecks.Require(store.Live("root-local", ["a/one.txt"]).Count == 1 && store.Live("root-local", ["b/renamed.txt"]).Count == 0, "deleting one link retains the other");
    }

    private static async Task OutsideAliasAsync(string folder)
    {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "one.txt", "aaa");
        LocalChecks.Require(CreateHardLink(Path.Combine(folder, "outside.txt"), Path.Combine(root, "one.txt"), IntPtr.Zero), "create outside-root alias");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        var snapshot = await SaveFull(inventory, store);
        var one = store.Live("root-local", ["one.txt"]).Single();
        LocalChecks.Require(!store.HasMultipleNames("root-local", one.ObjectId), "only one alias is inventoried");
        LocalChecks.Seed(root, "one.txt", "bbb");
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Contains("one.txt"), "live NumberOfLinks scopes outside-root alias debt");
        var def = LocalChecks.RootOf(root, "root-local"); var identity = LocalItems.ParseObjectId(def.RootObjectId);
        var id = LocalItems.ParseObjectId(one.ObjectId).FileId;
        var change = new NtfsUsnChange(id, identity.FileId, 1, 0x100, FileAttributes.Normal, "one.txt");
        var names = new LocalJournalNames(def, store, [change], 100);
        var context = new ResolveContext(identity.FileId, identity.Volume, _ => null, 100, names.Find, names.Ambiguous);
        LocalChecks.Require(await LocalDeltaResolver.ResolveAsync([change], context, default) is null, "named new records also check live link count");
    }

    private static async Task MovedInAliasesAsync(string folder)
    {
        foreach (var bothInside in new[] { true, false })
        {
            var home = Path.Combine(folder, bothInside ? "both" : "external");
            var root = Path.Combine(home, "root"); LocalChecks.Seed(root, "keep.txt", "keep");
            var incoming = Path.Combine(home, "incoming"); LocalChecks.Seed(incoming, "one.txt", "same");
            var alias = bothInside ? Path.Combine(incoming, "two.txt") : Path.Combine(home, "outside.txt");
            LocalChecks.Require(CreateHardLink(alias, Path.Combine(incoming, "one.txt"), IntPtr.Zero), "create incoming aliases before checkpoint");
            using var store = SqliteStateStore.Open(Path.Combine(home, "store"), StorageChecks.Fast, TimeProvider.System);
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
            var snapshot = await SaveFull(inventory, store);
            Directory.Move(incoming, Path.Combine(root, "incoming"));
            var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
            LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Contains("incoming/one.txt"), "moved-in ambiguous files stay protected without rejecting unrelated replay");
            await SaveFull(inventory, store);
            LocalChecks.Require(store.Live("root-local", ["incoming/one.txt", "incoming/two.txt"]).Count == (bothInside ? 2 : 1), "full reconciliation preserves all incoming aliases");
        }
    }

    private static void RehashMigration(string folder)
    {
        var item = new ItemState("file.txt", ItemKind.File, "id:local:00000001:0000000000000001", null,
            new string('a', 64), 3, DateTimeOffset.UnixEpoch, ItemFlags.None);
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            var gen = store.BeginGeneration("migrate", "full");
            store.WriteSnapshot(new Snapshot("migrate", Side.A, gen, "v8-cursor", [item], []));
            store.CompleteGeneration(gen, "v8-cursor", true);
        }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE local_rehash; PRAGMA user_version=8;"; command.ExecuteNonQuery();
        }
        using var migrated = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        LocalChecks.Require(migrated.Cursor("migrate") == "v8-cursor" && migrated.Live("migrate").Single() == item, "v8 migration retains row hash and root cursor");
        migrated.RequireRehash("migrate", item, "migration retry");
        LocalChecks.Require(migrated.RehashCandidates("migrate", 1).Single().ObjectId == item.ObjectId && migrated.CompleteRehash("migrate", item, new string('b', 64)), "v8 migration creates usable debt table and retry index");
    }

    private static async Task RehashDebtAsync(string folder)
    {
        RehashMigration(Path.Combine(folder, "migration"));
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "held.txt", "aaa");
        var data = Path.Combine(folder, "store");
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
            var snapshot = await SaveFull(inventory, store);
            var path = Path.Combine(root, "held.txt"); var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "bbb"); File.SetLastWriteTimeUtc(path, time);
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
            LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0 && delta.CursorAfter != snapshot.CursorAfter, "failed hash does not hold whole journal cursor");
            store.WriteDelta(delta); store.CompleteGeneration(delta.Gen, delta.CursorAfter, true);
            var item = store.Live("root-local", ["held.txt"]).Single();
            LocalChecks.Require(item.Hash is null && (item.Flags & ItemFlags.NeedsRehash) != 0, "failed hash persists debt without stale hash");
            await SaveFull(inventory, store);
            LocalChecks.Require((store.Live("root-local", ["held.txt"]).Single().Flags & ItemFlags.NeedsRehash) != 0, "ordinary full refresh preserves debt");
        }
        using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
            LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var empty), "empty batch checkpoint");
            var delta = await inventory.ChangesSinceAsync(empty!.Cursor, store.BeginGeneration("root-local", "retry"), _ => true, default);
            store.WriteDelta(delta);
            await inventory.VerifyIndexingPassAsync(default);
            await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
            var item = store.Live("root-local", ["held.txt"]).Single();
            LocalChecks.Require(item.Hash == DropboxContentHash.Compute(LocalChecks.Bytes("bbb")) && (item.Flags & ItemFlags.NeedsRehash) == 0, "restart and empty journal retry validates hash and atomically clears debt");
            store.RequireRehash("root-local", item, "test debt");
            File.Move(Path.Combine(root, "held.txt"), Path.Combine(root, "moved.txt"));
            await SaveFull(inventory, store);
            item = store.Live("root-local", ["moved.txt"]).Single();
            LocalChecks.Require((item.Flags & ItemFlags.NeedsRehash) != 0, "debt follows identity through full-snapshot rename");
            LocalChecks.Require(!store.CompleteRehash("root-local", item with { ObjectId = "unrelated" }, new string('a', 64)), "wrong identity never clears debt");
            var unicode = item with { Path = "\U00010400/file.txt", ObjectId = "unicode-local" };
            store.WriteSnapshot(new Snapshot("unicode", Side.A, store.BeginGeneration("unicode", "full"), "", [unicode], []));
            store.RequireRehash("unicode", unicode, "unicode hold");
            LocalChecks.Require(store.RehashTouches("unicode", "", true), "root effect protects supplementary-Unicode descendant debt");
            var cloud = item with { Path = "first.txt", ObjectId = "id:cloud-object", Flags = ItemFlags.None };
            var cloudGen = store.BeginGeneration("cloud-move", "full");
            store.WriteSnapshot(new Snapshot("cloud-move", Side.A, cloudGen, "", [cloud, cloud with { Path = "second.txt" }], []));
            store.WriteDelta(new Delta("cloud-move", Side.A, cloudGen, "", "", [new Change(ChangeKind.Upsert, cloud.ObjectId, "third.txt", cloud with { Path = "third.txt" }, null)], false, []));
            LocalChecks.Require(store.Live("cloud-move").Single().Path == "third.txt", "nonlocal duplicate object positions still collapse on move");
            var rows = Enumerable.Range(0, 130).Select(n => item with { Path = $"queue/{n:D3}", ObjectId = $"fake-{n:D3}" }).ToArray();
            store.WriteSnapshot(new Snapshot("queue", Side.A, store.BeginGeneration("queue", "full"), "", rows, []));
            foreach (var row in rows) store.RequireRehash("queue", row, "test sharing violation");
            var pair = new PairDef("rehash-protection", "test", LocalChecks.RootOf(root, "root-local"), new RootDef("queue", SyncProviders.Dropbox, null, "/", "remote"), [], [], true);
            var protection = LinkExclusions.Of(store, pair, stubsLeftAlone: true);
            LocalChecks.Require(protection.NarrowInventory(_ => true)("queue/000"), "hash debt does not suppress retry inventory");
            LocalChecks.Require(!protection.Narrow(_ => true)("queue/000") && !protection.Narrow(_ => true)("moved.txt"), "prepared copy source and opposite-side target remain protected");
            LocalChecks.Require(!protection.NarrowAction(_ => true)("queue") && !protection.NarrowRebuildableDelete(_ => true)("queue"), "containing folder delete and rebuildable exception cannot bypass hash debt");
            LocalChecks.Require(protection.Narrow(_ => true)("unrelated.txt"), "unrelated copy remains eligible");
            LocalChecks.Require(protection.Summaries(pair.PairId).Single(n => n.ReasonKey == RehashProtection.Reason).Detail.Contains("test sharing violation"), "standing notice contains actual bounded failure reasons");
            LocalChecks.Require(store.RehashCandidates("queue", 128).Count == 128, "bounded page selected without attempting excluded/blocked items");
        }
        using (var reopened = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
        {
            LocalChecks.Require(reopened.RehashCandidates("queue", 2).Select(i => i.ObjectId).SequenceEqual(["fake-128", "fake-129"]), "bounded retry fairness advances past skipped selections and survives restart");
            foreach (var row in reopened.RehashCandidates("queue", 128)) reopened.RequireRehash("queue", row, "failed again");
            LocalChecks.Require(reopened.RehashCandidates("queue", 2).Select(i => i.ObjectId).SequenceEqual(["fake-128", "fake-129"]), "repeated failures also rotate behind unattempted candidates");
        }
    }

    private static async Task RenameCasesAsync(string folder)
    {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "old/child.txt", "child");
        LocalChecks.Seed(root, "other/keep.txt", "keep"); LocalChecks.Seed(root, "Case.txt", "case");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System); var snapshot = await SaveFull(inventory, store);
        Directory.Move(Path.Combine(root, "old"), Path.Combine(root, "new")); LocalChecks.Seed(root, "old/replacement.txt", "replacement");
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        if (delta.CursorInvalid) snapshot = await SaveFull(inventory, store);
        else { store.WriteDelta(delta); snapshot = snapshot with { CursorAfter = delta.CursorAfter }; }
        LocalChecks.Require(store.Live("root-local", ["new/child.txt", "old/replacement.txt"]).Count == 2 && store.Live("root-local", ["old/child.txt"]).Count == 0, "renamed parent and new folder at old name reconcile end to end");
        File.Move(Path.Combine(root, "new/child.txt"), Path.Combine(root, "other/child.txt"));
        delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid, "move between known parents remains incremental"); store.WriteDelta(delta);
        LocalChecks.Require(store.Live("root-local", ["other/child.txt"]).Count == 1 && store.Live("root-local", ["new/child.txt"]).Count == 0, "between-parent move retires only old location");
        File.Move(Path.Combine(root, "Case.txt"), Path.Combine(root, "case.txt"));
        delta = await inventory.ChangesSinceAsync(delta.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(delta.CursorInvalid, "root case-only rename requests full reconciliation"); await SaveFull(inventory, store);
        LocalChecks.Require(store.Live("root-local", ["case.txt"]).Single().Path == "case.txt", "full scan records actual root child casing");
    }
}
