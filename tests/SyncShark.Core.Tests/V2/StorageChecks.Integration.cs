using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static readonly string[] IntegratedTables =
    [
        "quarantine_upgrade", "defender_deferral", "defender_grant", "defender_grant_path",
        "local_gap", "local_gap_item", "listing_checkpoint", "listing_page", "pair_pause",
        "local_walk", "local_walk_page", "choice_victim", "choice_restore_receipt", "choice_merge",
        "removal_review"
    ];

    private static void IntegratedSchemaChecks(string root)
    {
        var fresh = Fresh(root, "integrated-fresh");
        using (var store = SqliteStateStore.Open(fresh, Fast, new TestClock())) { }
        var expected = Inspect(fresh);
        foreach (var table in IntegratedTables)
            Require(expected.Contains(table), "fresh integration includes " + table);

        // Real v12 shape plus every interrupted/preview version through the current one.
        // Missing additive objects are repaired even when a preview already wrote the current stamp.
        for (var version = 12; version <= StoreSchema.Version; version++)
        {
            var folder = Fresh(root, "integrated-from-" + version);
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath(folder))!);
            using (var db = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False"))
            {
                db.Open();
                StoreSchema.Execute(db, null, LegacyV12Schema);
                StoreSchema.Execute(db, null, """
                    INSERT INTO root(root_no,root_id,cursor) VALUES(1,'root-a','retained-cursor');
                    INSERT INTO dir(dir_no,path,path_key) VALUES(1,'','');
                    INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags)
                    VALUES(1,1,'KEPT.TXT','kept.txt',0,'entity-a','revision-a',X'AABB',4,0,0);
                    INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES(1,'KEPT.TXT','{}',123);
                    """);
                StoreSchema.Execute(db, null, $"PRAGMA user_version={version};");
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
                    Require(store.Cursor("root-a") == "retained-cursor", "upgrade preserves root cursor");
                Require(expected.SetEquals(Inspect(folder)), "upgraded and fresh object sets match exactly");
                using var db = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
                db.Open();
                Require((string?)StoreSchema.Scalar(db, null, "SELECT object_id FROM file_state;") == "entity-a",
                    "upgrade preserves entity identity");
                Require((long?)StoreSchema.Scalar(db, null, "SELECT retry_after FROM sync_retry;") == 123,
                    "upgrade preserves pending retry state");
            }
        }

        static HashSet<string> Inspect(string folder)
        {
            using var db = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False");
            db.Open();
            Require((long?)StoreSchema.Scalar(db, null, "PRAGMA user_version;") == StoreSchema.Version,
                "integration schema has final version");
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index') AND name NOT LIKE 'sqlite_%';";
            using var rows = cmd.ExecuteReader();
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (rows.Read()) names.Add(rows.GetString(0));
            return names;
        }
    }
}
