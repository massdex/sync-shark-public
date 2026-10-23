using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static readonly string[] IntegratedTables =
    [
        "quarantine_upgrade", "defender_deferral", "defender_grant", "defender_grant_path",
        "local_gap", "local_gap_item", "listing_checkpoint", "listing_page", "pair_pause",
        "local_walk", "local_walk_page", "choice_victim", "choice_restore_receipt", "choice_merge",
        "removal_review", "journal_volume", "run_summary", "nt_pair_reason", "pe_intent_order"
    ];

    private static void IntegratedSchemaChecks(string root)
    {
        GapStagingChecks(root);
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
                    INSERT INTO root(root_no,root_id,cursor) VALUES(1,'root-a','0000000000000001:100');
                    INSERT INTO dir(dir_no,path,path_key) VALUES(1,'','');
                    INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags)
                    VALUES(1,1,'KEPT.TXT','kept.txt',0,'entity-a','revision-a',X'AABB',4,0,0);
                    INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES(1,'KEPT.TXT','{}',123);
                    """);
                StoreSchema.Execute(db, null, $"PRAGMA user_version={version};");
            }
            var expectedCursor = "0000000000000001:100";
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
                {
                    Require(store.Cursor("root-a") == expectedCursor, "upgrade preserves exact previously committed root cursor");
                    var retained = store.ByObject("root-a", "entity-a")!;
                    Require(retained.Hash == "aabb" && retained.Flags == ItemFlags.None &&
                        store.RehashWatermark("root-a") == 0 && !((ILocalGapStore)store).HasGapProtection("root-a"),
                        "upgrade preserves reusable content hash without inventing foreground or gap debt");
                    // A metadata-only reconciliation after upgrade must reuse the retained hash,
                    // not convert every unchanged file into a foreground content read.
                    var generation = store.BeginGeneration("root-a", "full");
                    store.WriteSnapshot(new("root-a", Side.A, generation, expectedCursor,
                        [retained with { Hash = null }], []));
                    store.CompleteGeneration(generation, expectedCursor, true);
                    Require(store.ByObject("root-a", "entity-a")!.Hash == retained.Hash &&
                        store.RehashWatermark("root-a") == 0,
                        "post-upgrade metadata snapshot retains unchanged hash and no rehash debt");
                    if (version == 12 && reopen == 0)
                    {
                        // Exact legacy shape: same journal, cursor older than retained history,
                        // reusable hash, and no pre-existing foreground verification obligation.
                        var through = new NtfsUsnCheckpoint(1, 150, 200, 150);
                        Require(through.RequiresFullReconciliation(store.Cursor("root-a")),
                            "migrated saved cursor is outside the retained journal range");
                        var local = new RootDef("root-a", SyncProviders.Local, null, folder,
                            SyncLocalEndpointStore.ObjectId(1, 1));
                        var inventory = new LocalInventory(local, Side.A, store, new HashSet<string>(), Fast, new TestClock());
                        inventory.BeginJournalGap(local.RootId, 1, 100, through);
                        expectedCursor = through.Cursor;
                        Require(store.Cursor(local.RootId) == expectedCursor && store.GapInventoryPending(local.RootId),
                            "post-migration gap advances cursor with a durable inventory obligation");
                        inventory.BeginFullGap(local.RootId, through.Cursor, journalDeleted: false);
                        var gaps = (ILocalGapStore)store;
                        Require(gaps.GapEpoch(local.RootId) is { TrustFileUsn: true, Boundary: 100 } &&
                            store.RehashWatermark(local.RootId) == 0 && store.ByObject(local.RootId, "entity-a")!.Hash == retained.Hash,
                            "v12 migration plus same-journal wrap starts trusted metadata proof, never foreground rehash");
                        // Complete the synthetic proof so the second open tests a settled predecessor.
                        gaps.GapDiscovered(local.RootId, gaps.GapEpoch(local.RootId)!, retained, false);
                        gaps.GapDiscoveryDone(local.RootId, gaps.GapEpoch(local.RootId)!);
                        gaps.PromoteGapDiscovery(local.RootId);
                    }
                }
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
