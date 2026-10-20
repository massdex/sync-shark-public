using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static readonly string[] IntegratedTables =
    [
        "quarantine_upgrade", "defender_deferral", "defender_grant", "defender_grant_path",
        "local_gap", "local_gap_item", "listing_checkpoint", "listing_page", "pair_pause",
        "local_walk", "local_walk_page", "choice_victim", "choice_restore_receipt", "choice_merge",
        "removal_review", "journal_volume", "run_summary", "nt_pair_reason"
    ];

    private static void IntegratedSchemaChecks(string root)
    {
        Require(StoreSchema.Version >= 18, "integrated schema never regresses to an isolated preview version");
        var fresh = Fresh(root, "integrated-fresh");
        using (var store = SqliteStateStore.Open(fresh, Fast, new TestClock())) { }
        var expected = Inspect(fresh);
        foreach (var table in IntegratedTables)
            Require(expected.Contains(table), "fresh integration includes " + table);

        // Real v12 shape plus every interrupted/preview version through the current one.
        // Missing additive objects are repaired even when a preview already wrote the current stamp.
        foreach (var version in new[] { 5 }.Concat(Enumerable.Range(12, StoreSchema.Version - 11)))
        {
            var folder = Fresh(root, "integrated-from-" + version);
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath(folder))!);
            using (var db = new SqliteConnection($"Data Source={DbPath(folder)};Pooling=False"))
            {
                db.Open();
                var ddl = version == 5 ? LegacyV12Schema
                    .Replace(", mapped_key TEXT", "")
                    .Replace("CREATE INDEX dir_mapped_key ON dir(mapped_key) WHERE mapped_key IS NOT NULL;", "")
                    .Replace(", basis_cursor TEXT, content_hash TEXT, failure_json TEXT", "") : LegacyV12Schema;
                StoreSchema.Execute(db, null, ddl);
                if (version == 5)
                {
                    // Historical additions append in upgrade order, unlike the fresh-store column order.
                    StoreSchema.Execute(db, null, "ALTER TABLE deletion_evidence ADD COLUMN failure_json TEXT;" +
                        "ALTER TABLE deletion_evidence ADD COLUMN basis_cursor TEXT;" +
                        "ALTER TABLE deletion_evidence ADD COLUMN content_hash TEXT;");
                }
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
                Require(expected.SetEquals(Inspect(folder)), "upgraded and fresh objects, columns and index definitions match exactly");
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
            rows.Close();
            foreach (var name in names.ToArray())
            {
                using var schema = db.CreateCommand();
                schema.CommandText = "SELECT type,sql FROM sqlite_master WHERE name=$name;";
                schema.Parameters.AddWithValue("$name", name);
                using var definition = schema.ExecuteReader();
                definition.Read();
                var kind = definition.GetString(0);
                var sql = definition.GetString(1);
                definition.Close();
                if (kind == "index")
                    names.Add("index:" + name + ":" + System.Text.RegularExpressions.Regex.Replace(
                        sql.Replace("IF NOT EXISTS", "", StringComparison.OrdinalIgnoreCase), @"\s+", "").ToLowerInvariant());
                else
                {
                    using var columns = db.CreateCommand();
                    columns.CommandText = "SELECT name,type,\"notnull\",dflt_value,pk FROM pragma_table_info($name) ORDER BY name;";
                    columns.Parameters.AddWithValue("$name", name);
                    using var column = columns.ExecuteReader();
                    while (column.Read())
                        names.Add("column:" + name + ":" + string.Join("|", Enumerable.Range(0, 5).Select(i => column.IsDBNull(i) ? "<null>" : column.GetValue(i).ToString())));
                }
            }
            return names;
        }
    }
}
