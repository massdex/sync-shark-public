using Massdex.SyncShark;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

internal static class FilesOnDemandCompatibilityChecks
{
    internal static void Run(string root)
    {
        FilesOnDemandV20Fixture.Create(SqliteStateStore.DatabasePath(root));
        string[] Schema()
        {
            using var connection = Open(root);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT type||'|'||name||'|'||COALESCE(sql,'') FROM sqlite_master ORDER BY type,name;";
            using var reader = cmd.ExecuteReader(); var rows = new List<string>();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows.ToArray();
        }
        var before = Schema();
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        {
            Require(store.ByObject("fixture-root", "fixture-item")?.Size == 17, "populated final26 fixture survives open");
            Require(!store.HasStorageWork() && !store.HasOwnedPointers("fixture-root") && store.StorageRegistrations().Count == 0 &&
                store.StorageSettlementPage().Count == 0 && store.ChangedStoragePointers().Count == 0 && store.StorageHydrationPage().Count == 0,
                "disabled feature APIs return defaults without creating schema");
            Require(before.SequenceEqual(Schema()), "no-command v20 open preserves every sqlite_master definition exactly");
            using (var connection = Open(root))
            {
                var reads = new FileStateReader(connection, new DirTable(connection));
                Require(reads.StorageFlagPlans(ItemFlags.NeedsRehash).All(p => p.Contains("fs_flagged")), "legacy flagged routes retain final26 index");
            }
            store.SetStoragePolicy("fixture-root", "fixture-item", "offline");
            Require(!before.SequenceEqual(Schema()), "explicit storage command creates feature objects");
            using var enabled = Open(root);
            Require(Convert.ToInt64(StoreSchema.Scalar(enabled, null, "PRAGMA user_version;")) == 20, "opt-in keeps base version");
            Require(StoreSchema.Scalar(enabled, null, "SELECT sql FROM sqlite_master WHERE name='fs_flagged';") is string sql && sql.EndsWith("WHERE flags != 0", StringComparison.Ordinal), "shared index unchanged");
            var enabledReads = new FileStateReader(enabled, new DirTable(enabled)) { StorageEnabled = true };
            foreach (var (mask, expected) in new[] { (ItemFlags.NeedsRehash, "fs_nonpointer"), (ItemFlags.OwnedPointer, "fs_owned"),
                (ItemFlags.Quarantined, "fs_quarantine"), (ItemFlags.ProviderNameRefused, "fs_refused") })
                Require(enabledReads.StorageFlagPlans(mask).All(p => p.Contains(expected)), "all flagged routes select " + expected);
            foreach (var template in new[] {
                "SELECT 1 FROM file_state f INDEXED BY fs_flagged WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0 LIMIT 1;",
                "SELECT 1 FROM file_state rf INDEXED BY fs_flagged WHERE rf.root_no=$r AND rf.flags!=0 AND (rf.flags & $flags)!=0 LIMIT 1;",
                "SELECT d.path,f.name FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0 LIMIT $limit;" })
                Require(StoreSchema.QueryPlan(enabled, "protection", store.StorageProtectionSql(template, ItemFlags.NeedsRehash), "$r", "$flags", "$limit").Any(p => p.Contains("fs_nonpointer")), "protection root, gap and projection choose narrow index");
            Require(StoreSchema.QueryPlan(enabled, "rehash", store.RehashRootSql, "$r").Any(p => p.Contains("fs_nonpointer")), "root rehash chooses narrow index");
            StoreSchema.Execute(enabled, null, "UPDATE file_state SET flags=768 WHERE object_id='fixture-item';");
            Require(store.LiveFlagged("fixture-root", ItemFlags.NeedsRehash).Single().ObjectId == "fixture-item" && store.RehashTouches("fixture-root", "", true), "mixed pointer+rehash remains protected");
            Require(store.ProtectionIndex("fixture-root", ItemFlags.NeedsRehash) is not null && store.ProtectionBatch("fixture-root", ItemFlags.NeedsRehash, [""]).Count == 1, "opt-in protection routes execute");
            StoreSchema.Execute(enabled, null, "UPDATE file_state SET flags=640 WHERE object_id='fixture-item';");
            Require(store.LiveFlagged("fixture-root", ItemFlags.Quarantined).Single().ObjectId == "fixture-item", "mixed pointer+quarantine remains protected");
            StoreSchema.Execute(enabled, null, "UPDATE file_state SET flags=512 WHERE object_id='fixture-item';");
            Require(store.LiveFlagged("fixture-root", ItemFlags.NeedsRehash).Count == 0 && store.LiveFlagged("fixture-root", ItemFlags.OwnedPointer).Count == 1, "pure pointers use owned route");
        }
        var optedIn = Schema();
        using (var reopened = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
            Require(optedIn.SequenceEqual(Schema()), "enabled replay preserves schema definitions");
    }

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(root), Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
