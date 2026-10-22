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
        string[] Columns(string table)
        {
            using var connection = Open(root);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
            cmd.Parameters.AddWithValue("$table", table);
            using var reader = cmd.ExecuteReader(); var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names.ToArray();
        }
        static string TypeOf(string row) => row.Split('|')[0];
        static string NameOf(string row) => row.Split('|')[1];
        // The integrated v2 build legitimately migrates final26.2 notice, recovery and local_gap tables and adds
        // these recovery indexes on every compatible open. They are part of this build's own baseline; Files
        // On-Demand must still add nothing of its own until it is enabled.
        string[] RecoveryIndexes = ["rc_page", "rc_path", "rc_path_missing", "nt_restore_page",
            "nt_restore_path", "nt_restore_path_ready", "nt_restore_hold_path", "nt_restore_reason_page"];
        var before = Schema();
        var beforeNoticeColumns = Columns("notice");
        var beforeRecoveryColumns = Columns("recovery");
        var beforeGapColumns = Columns("local_gap");
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        {
            Require(store.ByObject("fixture-root", "fixture-item")?.Size == 17, "populated final26 fixture survives open");
            Require(!store.HasStorageWork() && !store.HasOwnedPointers("fixture-root") && store.StorageRegistrations().Count == 0 &&
                store.StorageSettlementPage().Count == 0 && store.ChangedStoragePointers().Count == 0 && store.StorageHydrationPage().Count == 0,
                "disabled feature APIs return defaults without creating schema");
            // The no-command open produces the integrated build's v2 schema: final26.2 plus the approved
            // recovery additions and the forward-only v22 gap obligation. Neither is a FoD opt-in.
            // The pre-FoD backup restores this integrated baseline, not the older core schema.
            var integrated = Schema();
            var addedOpen = integrated.Except(before, StringComparer.Ordinal).ToArray();
            var removedOpen = before.Except(integrated, StringComparer.Ordinal).ToArray();
            Require(removedOpen.All(row => TypeOf(row) == "table" && (NameOf(row) is "notice" or "recovery" or "local_gap")),
                "no-command open only redefines the approved notice, recovery and local_gap tables");
            Require(addedOpen.All(row => (TypeOf(row) == "table" && (NameOf(row) is "notice" or "recovery" or "local_gap")) ||
                (TypeOf(row) == "index" && RecoveryIndexes.Contains(NameOf(row), StringComparer.Ordinal))),
                "no-command open adds only approved core recovery and gap changes");
            Require(addedOpen.Where(row => TypeOf(row) == "table").Select(NameOf).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "local_gap", "notice", "recovery" }), "the only redefined tables are local_gap, notice and recovery");
            var openedGapColumns = Columns("local_gap");
            Require(openedGapColumns.Take(beforeGapColumns.Length).SequenceEqual(beforeGapColumns) &&
                openedGapColumns.Skip(beforeGapColumns.Length).SequenceEqual(new[] { "inventory_pending" }),
                "local_gap keeps every existing column and gains only the v22 inventory obligation");
            Require(!addedOpen.Any(row => FilesOnDemandSchema.Names.Contains(NameOf(row), StringComparer.Ordinal)),
                "no-command open adds no Files On-Demand objects");
            var openedNoticeColumns = Columns("notice");
            var openedRecoveryColumns = Columns("recovery");
            Require(openedNoticeColumns.Take(beforeNoticeColumns.Length).SequenceEqual(beforeNoticeColumns) &&
                openedNoticeColumns.Skip(beforeNoticeColumns.Length).SequenceEqual(new[] { "path_key", "restore_hold" }),
                "notice keeps every final26.2 column and gains only the approved recovery columns");
            Require(openedRecoveryColumns.Take(beforeRecoveryColumns.Length).SequenceEqual(beforeRecoveryColumns) &&
                openedRecoveryColumns.Skip(beforeRecoveryColumns.Length).SequenceEqual(new[] { "path_key" }),
                "recovery keeps every final26.2 column and gains only the approved path key");
            using (var connection = Open(root))
            {
                var reads = new FileStateReader(connection, new DirTable(connection));
                Require(reads.StorageFlagPlans(ItemFlags.NeedsRehash).All(p => p.Contains("fs_flagged")), "legacy flagged routes retain final26 index");
            }
            var local = new RootDef("fixture-root", SyncProviders.Local, null, root, "0000000000000001");
            store.SavePair(new PairDef("fixture-pair", "Pair A", local,
                new RootDef("fixture-remote", SyncProviders.Dropbox, "account", "/Remote", "0000000000000002"), [], [], true));
            var cli = new V2CliRuntime(null, store, null!, null!, root);
            var listing = cli.ListStorageAsync("fixture-pair", 10, 0, CancellationToken.None).GetAwaiter().GetResult();
            Require(System.Text.Json.JsonSerializer.Serialize(listing).Contains("read-only"), "listing explains disabled read-only state");
            foreach (var mode in new[] { "offline", "inherit" })
            {
                store.SetStoragePolicy("fixture-root", "fixture-item", mode);
                store.SetStoragePolicyAtPath(local, "Missing", mode);
                var noChange = cli.SetStorageIntentAsync("fixture-pair", "Missing", mode, CancellationToken.None).GetAwaiter().GetResult();
                Require(System.Text.Json.JsonSerializer.Serialize(noChange).Contains("No change"), "CLI explains never-used no-op");
            }
            Require(integrated.SequenceEqual(Schema()), "list, inherit and offline preserve the integrated no-command schema exactly");
            var invalidBackup = Path.Combine(root, "not-a-directory");
            File.WriteAllText(invalidBackup, "retained fixture");
            var held = cli.SetStorageIntentAsync("fixture-pair", "", "online-only", CancellationToken.None,
                invalidBackup, false).GetAwaiter().GetResult();
            var heldText = System.Text.Json.JsonSerializer.Serialize(held);
            Require(heldText.Contains("Retry") && heldText.Contains("--backup-directory") && heldText.Contains("--confirm-without-backup"),
                "backup failure offers retry, other location and explicit proceed");
            Require(integrated.SequenceEqual(Schema()) && !store.IsStorageEnabled, "failed backup creates no feature schema");
            Require(File.ReadAllText(invalidBackup) == "retained fixture", "backup failure does not overwrite existing files");
            // Keep a WAL connection alive with auto-checkpoint disabled: the snapshot must contain committed WAL data.
            using var wal = Open(root);
            StoreSchema.Execute(wal, null, "PRAGMA wal_autocheckpoint=0; UPDATE root SET cursor='wal-committed' WHERE root_id='fixture-root';");
            Require(File.Exists(SqliteStateStore.DatabasePath(root) + "-wal"), "fixture has live WAL");
            var recovery = Path.Combine(root, "Recovery");
            var progressMessages = new List<string>();
            var saved = cli.SetStorageIntentAsync("fixture-pair", "", "online-only", CancellationToken.None,
                recovery, false, message =>
                {
                    Require(!store.IsStorageEnabled && !Directory.Exists(Path.Combine(recovery, "StateBackups")),
                        "backup progress is delivered before the snapshot and opt-in");
                    progressMessages.Add(message);
                }).GetAwaiter().GetResult();
            Require(progressMessages.Count == 1 && progressMessages[0].Contains("few minutes"),
                "first opt-in explains the potentially slow backup before blocking");
            var backups = Directory.GetFiles(Path.Combine(recovery, "StateBackups"), "*.db");
            Require(backups.Length == 1, "first online-only command takes one snapshot");
            var savedText = System.Text.Json.JsonSerializer.Serialize(saved);
            Require(savedText.Contains("Pre-storage state backup") && savedText.Contains("roll back") && savedText.Contains("sidecars"),
                "CLI identifies backup and safe rollback procedure");
            using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backups[0], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                snapshot.Open();
                Require((string?)StoreSchema.Scalar(snapshot, null, "SELECT cursor FROM root WHERE root_id='fixture-root';") == "wal-committed", "snapshot includes committed WAL pages");
                Require((string?)StoreSchema.Scalar(snapshot, null, "PRAGMA integrity_check;") == "ok", "snapshot is consistent");
                Require(!FilesOnDemandSchema.IsEnabled(snapshot), "backup predates opt-in schema");
                using var schema = snapshot.CreateCommand();
                schema.CommandText = "SELECT type||'|'||name||'|'||COALESCE(sql,'') FROM sqlite_master ORDER BY type,name;";
                using var reader = schema.ExecuteReader(); var definitions = new List<string>();
                while (reader.Read()) definitions.Add(reader.GetString(0));
                Require(integrated.SequenceEqual(definitions), "rollback backup preserves the complete integrated schema");
            }
            store.EnableStorageWithBackup(recovery);
            Require(Directory.GetFiles(Path.Combine(recovery, "StateBackups"), "*.db").Length == 1, "repeated opt-in does not copy database again");
            Require(!integrated.SequenceEqual(Schema()), "first online-only command creates feature objects after backup");
            using var enabled = Open(root);
            Require(Convert.ToInt64(StoreSchema.Scalar(enabled, null, "PRAGMA user_version;")) == StoreSchema.Version, "opt-in keeps the integrated core schema version");
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
            StoreSchema.Execute(enabled, null, "UPDATE file_state SET flags=1280 WHERE object_id='fixture-item';");
            Require(store.LiveFlagged("fixture-root", ItemFlags.NeedsRehash).Single().ObjectId == "fixture-item" && store.RehashTouches("fixture-root", "", true), "mixed pointer+rehash remains protected");
            Require(store.ProtectionIndex("fixture-root", ItemFlags.NeedsRehash) is not null && store.ProtectionBatch("fixture-root", ItemFlags.NeedsRehash, [""]).Count == 1, "opt-in protection routes execute");
            StoreSchema.Execute(enabled, null, "UPDATE file_state SET flags=1152 WHERE object_id='fixture-item';");
            Require(store.LiveFlagged("fixture-root", ItemFlags.Quarantined).Single().ObjectId == "fixture-item", "mixed pointer+quarantine remains protected");
            StoreSchema.Execute(enabled, null, "UPDATE file_state SET flags=1024 WHERE object_id='fixture-item';");
            Require(store.LiveFlagged("fixture-root", ItemFlags.NeedsRehash).Count == 0 && store.LiveFlagged("fixture-root", ItemFlags.OwnedPointer).Count == 1, "pure pointers use owned route");
        }
        using (var confirmed = SqliteStateStore.Open(Path.Combine(root, "confirmed"), V2Settings.Default, TimeProvider.System))
        {
            var result = confirmed.EnableStorageWithBackup(null);
            Require(!result.Enabled && !confirmed.IsStorageEnabled, "missing configured directory requires a choice");
            result = confirmed.EnableStorageWithBackup(null, confirmWithoutBackup: true);
            Require(result.Enabled && result.BackupPath is null && result.Warning?.Contains("explicitly supplied") == true,
                "explicit confirmation enables after failure and warns no rollback copy exists");
        }
        var parsed = SyncCliRegistry.Parse(["--cli", "storage", "online-only", "--pair", "fixture-pair", "--path", "",
            "--backup-directory", Path.Combine(root, "Recovery"), "--confirm-without-backup"]);
        Require(parsed.Flag("--confirm-without-backup") && parsed.Value("--backup-directory") == Path.Combine(root, "Recovery"),
            "CLI transports backup selection and explicit confirmation");
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
