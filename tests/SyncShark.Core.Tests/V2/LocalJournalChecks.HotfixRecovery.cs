namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task ScopeOverflowAsync(string folder)
    {
        try
        {
            var root = Path.Combine(folder, "root");
            LocalChecks.Seed(root, "one.txt", "same");
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System,
                settings: V2Settings.Default with { LocalDeltaMaxPaths = 2 });
            inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
            var saved = await SaveFull(inventory, store);
            for (var n = 0; n < 3; n++)
                LocalChecks.Require(CreateHardLink(Path.Combine(root, $"alias-{n}.txt"), Path.Combine(root, "one.txt"), IntPtr.Zero), "overflow alias");
            var item = store.Live("root-local").Single();
            var id = LocalItems.ParseObjectId(item.ObjectId).FileId;
            var parent = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(root)).FileId;
            ((ILocalGapStore)store).BeginGap("root-local", saved.CursorAfter, 50, true);
            inventory.WalkCheckpointForTest = () => (new(1, 0, 200, 0), false);
            inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
                out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
            {
                changes = Enumerable.Range(0, 3).Select(n => new NtfsUsnChange(id, parent, 110 + n, 0x10000,
                    FileAttributes.Normal, $"alias-{n}.txt")).ToArray();
                failure = null; kind = NtfsUsnReadFailure.Transient; return true;
            };
            var rejected = await inventory.ChangesSinceAsync(saved.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
            LocalChecks.Require(rejected.CursorInvalid, "ambiguous scope overflow requests immediate full walk, not root retry stall");
            var full = await SaveFull(inventory, store);
            store.SettleGapInventory(full.Gen);
            LocalChecks.Require(store.Cursor("root-local") == "0000000000000001:200" && !store.GapInventoryPending("root-local"),
                "next full walk advances cursor and settles gap inventory after scope overflow");
        }
        finally { RemoveFixture(folder); }
    }

    private static async Task AncestorRetryAsync(string folder)
    {
        try
        {
            var root = Path.Combine(folder, "root");
            LocalChecks.Seed(root, "old/file.txt", "same");
            LocalChecks.Require(CreateHardLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "old/file.txt"), IntPtr.Zero), "rename alias");
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
            var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
            inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
            var saved = await SaveFull(inventory, store);
            var file = store.Live("root-local", ["old/file.txt"]).Single();
            var dir = store.Live("root-local", ["old"]).Single();
            var parent = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(root)).FileId;
            Directory.Move(Path.Combine(root, "old"), Path.Combine(root, "new"));
            inventory.WalkCheckpointForTest = () => (new(1, 0, 200, 0), false);
            inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
                out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
            {
                changes = [new(LocalItems.ParseObjectId(file.ObjectId).FileId, LocalItems.ParseObjectId(dir.ObjectId).FileId,
                    110, 0x10000, FileAttributes.Normal, "file.txt"),
                    new(LocalItems.ParseObjectId(dir.ObjectId).FileId, parent, 120, 0x1000, FileAttributes.Directory, "old"),
                    new(LocalItems.ParseObjectId(dir.ObjectId).FileId, parent, 130, 0x2000, FileAttributes.Directory, "new")];
                failure = null; kind = NtfsUsnReadFailure.Transient; return true;
            };
            var delta = await inventory.ChangesSinceAsync(saved.CursorAfter, store.BeginGeneration("root-local", "delta"), _ => true, default);
            LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Contains("old"),
                "filtered parent rename/delete is UNREAD for targeted retry");
            store.WriteDelta(delta);
            // Exercise the same targeted inventory primitive used by the next run's scan retry.
            var reread = await inventory.ChangesForPathsAsync(delta.IncompleteSubtrees, delta.CursorAfter,
                store.BeginGeneration("root-local", "delta"), _ => true, default);
            store.WriteDelta(reread);
            LocalChecks.Require(store.Live("root-local", ["old", "old/file.txt"]).Count == 0 &&
                store.Live("root-local", ["new"]).Count == 1 && store.Live("root-local", ["new/file.txt"]).Count == 1 &&
                store.Live("root-local", ["alias.txt"]).Single().ObjectId == file.ObjectId &&
                File.ReadAllText(Path.Combine(root, "alias.txt")) == "same",
                "parent retry removes stale old rows and retains one renamed subtree, no duplicate copy");
        }
        finally { RemoveFixture(folder); }
    }

    private static async Task DeletedAliasAsync(string folder)
    {
        try
        {
            var root = Path.Combine(folder, "root");
            LocalChecks.Seed(root, "gone.txt", "same");
            LocalChecks.Require(CreateHardLink(Path.Combine(root, "survivor.txt"), Path.Combine(root, "gone.txt"), IntPtr.Zero), "two live aliases");
            var data = Path.Combine(folder, "store");
            string objectId;
            using (var store = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
            {
                var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
                inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
                await SaveFull(inventory, store);
                objectId = store.Live("root-local", ["gone.txt"]).Single().ObjectId;
                var survivor = store.Live("root-local", ["survivor.txt"]).Single();
                // RQ-036 / decision-table row 3: Adopt needs verified content, not an unhashed inventory seed.
                // Hash the real alias bytes; keep all alias-retirement assertions below unchanged.
                await using (var bytes = File.OpenRead(Path.Combine(root, survivor.Path)))
                    survivor = survivor with { Hash = await DropboxContentHash.ComputeAsync(bytes, default) };
                LocalChecks.Require(survivor.Hash is not null && survivor.Size == new FileInfo(Path.Combine(root, survivor.Path)).Length,
                    "alias adoption seed has measured content evidence");
                var pair = new PairDef("alias-pair", "Pair A", LocalChecks.RootOf(root, "root-local"),
                    new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
                store.SavePair(pair);
                var remote = survivor with { ObjectId = "remote-survivor" };
                var adopt = new SyncAction("alias-adopt", 3, ActionKind.Adopt, Side.B, survivor.Path, null, survivor, remote, false, "alias-link");
                store.RecordIntent(pair.PairId, adopt);
                store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, survivor, remote, null, null));
                File.Delete(Path.Combine(root, "gone.txt"));
                var delta = await inventory.ChangesForPathsAsync(["gone.txt"], store.Cursor("root-local")!,
                    store.BeginGeneration("root-local", "delta"), _ => true, default);
                store.WriteDelta(delta);
                LocalChecks.Require(store.Live("root-local", ["gone.txt"]).Count == 0 &&
                    store.Live("root-local", ["survivor.txt"]).Single().ObjectId == objectId,
                    "deleting one alias retires only its exact row; live sibling survives");
            }
            using (var reopened = SqliteStateStore.Open(data, StorageChecks.Fast, TimeProvider.System))
            {
                LocalChecks.Require(reopened.ByObject("root-local", objectId)?.Path == "survivor.txt" &&
                    reopened.ObjectPaths("root-local", objectId, 0, 10).SequenceEqual(["survivor.txt"]),
                    "cold reopen retains live identity and no stale alias");
                using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = SqliteStateStore.DatabasePath(data), Pooling = false }.ToString());
                db.Open();
                using var query = db.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM tombstone WHERE object_id=$id";
                query.Parameters.AddWithValue("$id", objectId);
                LocalChecks.Require(Convert.ToInt64(query.ExecuteScalar()) == 0, "surviving alias never creates entity tombstone");
                var inventory = LocalChecks.Inventory(root, reopened, TimeProvider.System);
                inventory.WalkCheckpointForTest = () => (new(1, 0, 200, 0), false);
                await SaveFull(inventory, reopened);
                LocalChecks.Require(File.ReadAllText(Path.Combine(root, "survivor.txt")) == "same" &&
                    reopened.ObjectPaths("root-local", objectId, 0, 10).SequenceEqual(["survivor.txt"]),
                    "next full observation preserves surviving alias without duplicate");
                LocalChecks.Require(CreateHardLink(Path.Combine(root, "again.txt"), Path.Combine(root, "survivor.txt"), IntPtr.Zero),
                    "recreate second alias for combined delete and rename");
                await SaveFull(inventory, reopened);
                var before = reopened.Live("root-local", ["survivor.txt"]).Single();
                File.Delete(Path.Combine(root, "again.txt"));
                File.Move(Path.Combine(root, "survivor.txt"), Path.Combine(root, "renamed.txt"));
                var renamed = before with { Path = "renamed.txt" };
                reopened.WriteDelta(new Delta("root-local", Side.A, reopened.BeginGeneration("root-local", "delta"),
                    reopened.Cursor("root-local")!, "combined-alias", [
                        new Change(ChangeKind.Delete, objectId, "again.txt", null, DateTimeOffset.UtcNow),
                        new Change(ChangeKind.Delete, objectId, "survivor.txt", null, DateTimeOffset.UtcNow),
                        new Change(ChangeKind.Upsert, objectId, "renamed.txt", renamed, null)], false, []));
                LocalChecks.Require(reopened.ObjectPaths("root-local", objectId, 0, 10).SequenceEqual(["renamed.txt"]) &&
                    File.ReadAllText(Path.Combine(root, "renamed.txt")) == "same" && Convert.ToInt64(query.ExecuteScalar()) == 0,
                    "same batch delete plus rename retires both old names without tombstoning the live identity");
            }
        }
        finally { RemoveFixture(folder); }
    }

    internal static void RemoveFixture(string folder)
    {
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) RemoveFixture(entry);
                else File.Delete(entry);
        Directory.Delete(folder, false);
    }
}
