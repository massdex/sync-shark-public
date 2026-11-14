namespace Massdex.SyncShark.Engine.Storage;

// One owning SQL catalogue: the text and parameter and column ordinals name the same bindings, shared through fragments such as PairRow and KeyOf.
/// <summary>All SQL text of the engine (section 3.2; literals rule). Parameters: $p pair id, $side 0 = Local / 1 = Dropbox,
/// $k a key; descendants use BINARY key ranges, never LIKE.</summary>
public static class StateSchema
{
    /// <summary>Pragmas that select WAL, full durability, foreign keys and incremental vacuuming.</summary>
    public const string Pragmas = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";

    /// <summary>The schema version this build writes and accepts.</summary>
    public const int Version = 14;
    public const int RenameContinuationUpgradeVersion = 13;
    public const int IssuePathKeyUpgradeVersion = 12;
    public const int DiscoveryRequiredUpgradeVersion = 11;
    public const int LocalPublishedUpgradeVersion = 10;
    public const int SpentHistoryUpgradeVersion = 9;
    public const int DeletedBlocksUpgradeVersion = 8;
    public const int IssueVisibilityUpgradeVersion = 7;
    public const int DeletedHistoryUpgradeVersion = 6;
    public const int HashHexLength = 64;
    /// <summary>One row per statement keeps a cursor step from holding a reader across a yield.</summary>
    public const int OneRow = 1;
    /// <summary>The persisted work-origin value of change_work rows acquired from a source page.</summary>
    public const string AcquiredWorkOrigin = "Acquired";
    public const string ReadVersion = "PRAGMA user_version;";
    public const string HasSchema = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table');";
    /// <summary>Returns free pages to the file; the caller must read the result.</summary>
    public const string Reclaim = "PRAGMA incremental_vacuum;";
    public const string Checkpoint = "PRAGMA wal_checkpoint(TRUNCATE);";
    public const string PathKeyFunction = "pathkey";

    // One owner for the repeated lookup rules; composition is compile-time text only.
    private const string PairRow = "(SELECT id FROM pair WHERE pair_id = $p)";
    private const string KeyOf = PathKeyFunction + "(";

    /// <summary>The saved row holding one effective key: an explicit Dropbox delta, else its inherited local key.</summary>
    private const string SyncedDropboxSelf = "rowid IN (SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = " +
        PairRow + " AND dropbox_key_delta = $k UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " +
        PairRow + " AND " + KeyOf + "local_path) = $k AND dropbox_key_delta IS NULL)";

    /// <summary>The saved rows under one effective key on either representation.</summary>
    private const string SyncedDropboxSubtree = "rowid IN (SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = " +
        PairRow + " AND dropbox_key_delta >= $k || '/' AND dropbox_key_delta < $k || '0' UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " +
        PairRow + " AND " + KeyOf + "local_path) >= $k || '/' AND " + KeyOf + "local_path) < $k || '0' AND dropbox_key_delta IS NULL)";

    /// <summary>The schema-4 tables and indexes, without a version: the base of a fresh creation and of the upgrade check.</summary>
    public const string Create = "CREATE TABLE IF NOT EXISTS pair(id INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL, pending_json TEXT,\r\n  last_repair_json TEXT, first_repair_done INTEGER NOT NULL DEFAULT 0) STRICT;\r\nCREATE TABLE IF NOT EXISTS account(connection_id TEXT PRIMARY KEY, limiter_level INTEGER NOT NULL) STRICT;\r\nCREATE TABLE IF NOT EXISTS synced_entry(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,\r\n  local_path TEXT NOT NULL, dropbox_key_delta TEXT, dropbox_path_delta TEXT,\r\n  dropbox_key TEXT GENERATED ALWAYS AS (coalesce(dropbox_key_delta, " + KeyOf + "local_path))) VIRTUAL,\r\n  dropbox_path TEXT GENERATED ALWAYS AS (coalesce(dropbox_path_delta, local_path)) VIRTUAL,\r\n  is_folder INTEGER NOT NULL, local_id ANY NOT NULL, local_size INTEGER NOT NULL, local_mtime INTEGER NOT NULL,\r\n  dropbox_id TEXT NOT NULL, dropbox_hash ANY NOT NULL) STRICT;\r\nCREATE UNIQUE INDEX IF NOT EXISTS synced_local_key ON synced_entry(pair_id, " + KeyOf + "local_path));\r\nCREATE UNIQUE INDEX IF NOT EXISTS synced_dropbox_key ON synced_entry(pair_id, dropbox_key_delta) WHERE dropbox_key_delta IS NOT NULL;\r\nCREATE INDEX IF NOT EXISTS synced_local_id ON synced_entry(pair_id, local_id);\r\nCREATE INDEX IF NOT EXISTS synced_dropbox_id ON synced_entry(pair_id, dropbox_id);\r\nCREATE TABLE IF NOT EXISTS issue(issue_id INTEGER PRIMARY KEY, pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,\r\n  kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,\r\n  at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,\r\n  UNIQUE(pair_id, path, kind)) STRICT;";

    /// <summary>Canonical issue lookup, installed after every issue-table rebuild.</summary>
    public const string IssuePathKeyIndex = "CREATE INDEX issue_path_key ON issue(pair_id, " + KeyOf + "path), kind);";

    public const string AttachScan = "ATTACH DATABASE $path AS scan_state;";
    public const string DetachScan = "DETACH DATABASE scan_state;";
    public const string EmptyScanPragmas = "PRAGMA scan_state.journal_mode=MEMORY; PRAGMA scan_state.synchronous=OFF;";
    public const string ScanWal = "PRAGMA scan_state.journal_mode=WAL;";
    public const string ScanPragmas = "PRAGMA scan_state.synchronous=OFF;";
    public const string WalSuffix = "-wal";
    public const string ShmSuffix = "-shm";
    public const string WalMode = "wal";
    public const string ParamScan = "$scan";
    /// <summary>In-memory database used by the read-only scan connection.</summary>
    public const string ReadRoot = "file::memory:";
    public const string AttachState = "ATTACH DATABASE $path AS durable_state;";
    /// <summary>Infix between the database name and the pair hash in a scan file name.</summary>
    public const string ScanFileSuffix = ".scan-";
    public const string DatabaseExtension = ".db";
    /// <summary>In-memory stand-in for a pair with no scan.</summary>
    public const string EmptyScan = ":memory:";
    /// <summary>Read-only in-memory stand-in for a pair with no scan.</summary>
    public const string EmptyScanRead = "file::memory:?mode=memory";
    public const string CreateScan = "CREATE TABLE IF NOT EXISTS scan_state.scan(pair_id INTEGER NOT NULL, scan_id BLOB NOT NULL, side INTEGER NOT NULL,\r\n  own_key TEXT GENERATED ALWAYS AS (" + KeyOf + "path)) VIRTUAL, key_delta TEXT,\r\n  key TEXT GENERATED ALWAYS AS (coalesce(key_delta, own_key)) VIRTUAL, path TEXT NOT NULL, is_folder INTEGER NOT NULL, size INTEGER NOT NULL,\r\n  mtime INTEGER NOT NULL, id ANY NOT NULL, rev TEXT NOT NULL, hash ANY NOT NULL, skip TEXT) STRICT;\r\nCREATE UNIQUE INDEX IF NOT EXISTS scan_state.scan_own ON scan(pair_id, scan_id, side, " + KeyOf + "path));\r\nCREATE INDEX IF NOT EXISTS scan_state.scan_key ON scan(pair_id, scan_id, side, key_delta) WHERE side=1 AND key_delta IS NOT NULL;";

    // pair and account
    public const string CountSynced = "SELECT count(*) FROM synced_entry WHERE pair_id = " + PairRow + ";";
    public const string SelectPairs = "SELECT def_json FROM pair ORDER BY pair_id;";
    public const string SelectPair = "SELECT def_json FROM pair WHERE pair_id = $p;";
    /// <summary>Drive evidence changes only the current definition, without advancing execution or source fences.</summary>
    internal const string UpdatePairDefinition = "UPDATE pair SET def_json=$definition WHERE id=$pair AND execution_generation=$generation;";
    public const string PairIdByRow = "SELECT pair_id FROM pair WHERE id = $pair;";
    public const string UpsertPair = """
        INSERT INTO pair(pair_id, def_json) VALUES($p, $def)
        ON CONFLICT(pair_id) DO UPDATE SET def_json = excluded.def_json;
        """;
    public const string DeletePair = "DELETE FROM pair AS p WHERE p.pair_id=$p AND p.pending_json IS NULL AND NOT EXISTS(SELECT 1 FROM folder_removal f WHERE f.pair_id=p.id AND " +
        FolderUnresolved + ") AND NOT EXISTS(SELECT 1 FROM folder_removal_item i JOIN folder_removal f ON f.pair_id=i.pair_id AND f.operation_id=i.operation_id WHERE i.pair_id=p.id AND (" +
        FolderCustody + " OR " + FolderUndisposedEffect + "));";
    public const string SelectFirstRepairDone = "SELECT first_repair_done FROM pair WHERE pair_id = $p;";
    public const string SelectLastRepair = "SELECT last_repair_json FROM pair WHERE pair_id = $p;";
    public const string ParamRunId = "$runId";
    public const string MarkRepairLogFailed = """
        UPDATE pair SET last_repair_json = json_set(last_repair_json, '$.LogFailed', json('true'))
        WHERE pair_id = $p AND json_extract(last_repair_json, '$.RunId') = $runId;
        """;
    public const string FinishRepair = """
        UPDATE pair SET last_repair_json = $summary,
          first_repair_done = CASE WHEN $completed THEN 1 ELSE first_repair_done END
        WHERE pair_id = $p;
        """;
    public const string SelectLimiterLevel = "SELECT limiter_level FROM account WHERE connection_id = $c;";
    public const string UpsertLimiterLevel = """
        INSERT INTO account(connection_id, limiter_level) VALUES($c, $level)
        ON CONFLICT(connection_id) DO UPDATE SET limiter_level = excluded.limiter_level;
        """;

    public const string ParamScope = "$scope";
    public const string ParamScopeLow = "$scopeLow";
    public const string ParamScopeHigh = "$scopeHigh";

    // synced_entry reads
    public const string SyncedColumns = KeyOf + "local_path), local_path, dropbox_key_delta, dropbox_path, is_folder, local_id, local_size, local_mtime,\r\ndropbox_id, dropbox_hash";
    public const string SelectSyncedByLocalKey = "SELECT " + SyncedColumns + " FROM synced_entry WHERE pair_id = " + PairRow + " AND " + KeyOf + "local_path) = $k;";
    public const string SelectSyncedByDropboxKey = "SELECT " + SyncedColumns + " FROM synced_entry WHERE " + SyncedDropboxSelf + ";";
    /// <summary>The saved rows strictly under one effective Dropbox key, on either representation.</summary>
    public const string SelectSyncedUnderDropboxKey = "SELECT " + SyncedColumns + " FROM synced_entry WHERE " + SyncedDropboxSubtree + ";";
    /// <summary>The saved rows strictly under one local key.</summary>
    public const string SelectSyncedUnderLocalKey = "SELECT " + SyncedColumns + " FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow + " AND " + KeyOf + "local_path) >= $k || '/' AND " + KeyOf + "local_path) < $k || '0';";
    /// <summary>Explicit Dropbox bindings resume on their unique indexed key.</summary>
    public const string SelectSyncedUnderDropboxKeyPaged = "SELECT " + SyncedColumns + " FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id=$pair AND dropbox_key_delta>$after AND dropbox_key_delta<$high ORDER BY dropbox_key_delta LIMIT $take;";
    private const string SyncedLocalPage = " FROM synced_entry INDEXED BY synced_local_key WHERE pair_id=$pair AND " + KeyOf + "local_path)>$after AND " + KeyOf + "local_path)<$high";
    /// <summary>Local bindings resume on their unique indexed key.</summary>
    public const string SelectSyncedUnderLocalKeyPaged = "SELECT " + SyncedColumns + SyncedLocalPage + " ORDER BY " + KeyOf + "local_path) LIMIT $take;";
    /// <summary>Inherited Dropbox bindings resume on the local index without rebuilding subtree membership.</summary>
    public const string SelectSyncedUnderInheritedKeyPaged = "SELECT " + SyncedColumns + SyncedLocalPage + " AND dropbox_key_delta IS NULL ORDER BY " + KeyOf + "local_path) LIMIT $take;";
    /// <summary>Whether a planned saved-only member occupies a native identity.</summary>
    public const string FolderItemNative = "SELECT EXISTS(SELECT 1 FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND native_key=$k AND provenance='Planned' AND present=0);";
    /// <summary>The saved boundary at a recorded local key, only while the row still holds that local identity.</summary>
    public const string SyncedLocalPublished = "SELECT local_published_journal, local_published_usn FROM synced_entry " +
        "INDEXED BY synced_local_key WHERE pair_id = $pair AND " + KeyOf + "local_path) = $k AND local_id = $localId " +
        "AND local_published_usn IS NOT NULL;";
    public const int PublishedJournal = 0;
    public const int PublishedUsn = 1;
    public const string SelectSyncedByLocalId = "SELECT " + SyncedColumns + " FROM synced_entry INDEXED BY synced_local_id WHERE pair_id = " + PairRow + " AND local_id = $id;";
    public const string SelectSyncedByDropboxId = "SELECT " + SyncedColumns + " FROM synced_entry INDEXED BY synced_dropbox_id WHERE pair_id = " + PairRow + " AND dropbox_id = $id;";
    public const string ForgetAllSynced = "DELETE FROM synced_entry WHERE pair_id = " + PairRow +
        "; UPDATE pair SET first_repair_done = 0 WHERE pair_id = $p;";

    // commit (section 3.5)
    public const string RenameSyncedRow = "UPDATE synced_entry SET local_path = $newLocalPath, dropbox_key_delta = nullif($newDropboxKey, $newLocalKey),\r\n  dropbox_path_delta = nullif($newDropboxPath, $newLocalPath), local_id = $localId, dropbox_id = $dropboxId,\r\n  local_published_journal = iif(local_id = $localId, local_published_journal, NULL),\r\n  local_published_usn = iif(local_id = $localId, local_published_usn, NULL)\r\nWHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "local_path) = $oldLocalKey;";
    public const string RebaseDescendantsLocal = "UPDATE synced_entry SET dropbox_key_delta = nullif(dropbox_key, " + KeyOf + "$newPath || substr(local_path, length($oldPath) + 1))),\r\n  dropbox_path_delta = nullif(dropbox_path, $newPath || substr(local_path, length($oldPath) + 1)),\r\n  local_path = $newPath || substr(local_path, length($oldPath) + 1)\r\nWHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "local_path) >= $oldKey || '/' AND " + KeyOf + "local_path) < $oldKey || '0';";
    public const string RebaseDescendantsDropbox = "UPDATE synced_entry SET dropbox_key_delta = nullif($newKey || substr(dropbox_key, length($oldKey) + 1), " + KeyOf + "local_path)),\r\n  dropbox_path_delta = nullif($newPath || substr(dropbox_path, length($oldPath) + 1), local_path)\r\nWHERE rowid IN (SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND dropbox_key_delta >= $oldKey || '/' AND dropbox_key_delta < $oldKey || '0'\r\n    UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "local_path) >= $oldKey || '/' AND " + KeyOf + "local_path) < $oldKey || '0' AND dropbox_key_delta IS NULL);";
    public const string MoveScanSubtree = "UPDATE scan SET key_delta = nullif($newKey || substr(key, length($oldKey) + 1),\r\n    $newOwnKey || substr(" + KeyOf + "path), length($oldOwnKey) + 1)),\r\n  path = $newPath || substr(path, length($oldPath) + 1)\r\nWHERE rowid IN (SELECT rowid FROM scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side AND " + KeyOf + "path) = $oldOwnKey\r\n    UNION ALL SELECT rowid FROM scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side\r\n      AND " + KeyOf + "path) >= $oldOwnKey || '/' AND " + KeyOf + "path) < $oldOwnKey || '0');";
    // Freeze old keys before either side moves, so overlapping mapped ranges cannot rebase a row twice.
    public const string RebaseDescendantsBoth = """
        WITH old_rows AS MATERIALIZED (
          SELECT rowid AS id, local_path, dropbox_path, dropbox_key_delta,
        """ + KeyOf + "local_path) AS local_key FROM synced_entry WHERE rowid IN (" +
        "SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow +
        " AND " + KeyOf + "local_path) >= $oldLocalKey || '/' AND " + KeyOf + "local_path) < $oldLocalKey || '0' " +
        "UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = " + PairRow +
        " AND dropbox_key_delta >= $oldDropboxKey || '/' AND dropbox_key_delta < $oldDropboxKey || '0' " +
        "UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow +
        " AND " + KeyOf + "local_path) >= $oldDropboxKey || '/' AND " + KeyOf + "local_path) < $oldDropboxKey || '0' AND dropbox_key_delta IS NULL)), " + """
        final_rows AS MATERIALIZED (
          SELECT id,
            CASE WHEN local_key >= $oldLocalKey || '/' AND local_key < $oldLocalKey || '0'
              THEN $newLocalPath || substr(local_path, length($oldLocalPath) + 1)
              ELSE local_path END AS local_path,
            CASE WHEN local_key >= $oldLocalKey || '/' AND local_key < $oldLocalKey || '0'
              THEN $newLocalKey || substr(local_key, length($oldLocalKey) + 1)
              ELSE local_key END AS local_key,
            CASE WHEN coalesce(dropbox_key_delta, local_key) >= $oldDropboxKey || '/'
                AND coalesce(dropbox_key_delta, local_key) < $oldDropboxKey || '0'
              THEN $newDropboxPath || substr(dropbox_path, length($oldDropboxPath) + 1)
              ELSE dropbox_path END AS dropbox_path,
            CASE WHEN coalesce(dropbox_key_delta, local_key) >= $oldDropboxKey || '/'
                AND coalesce(dropbox_key_delta, local_key) < $oldDropboxKey || '0'
              THEN $newDropboxKey || substr(coalesce(dropbox_key_delta, local_key), length($oldDropboxKey) + 1)
              ELSE coalesce(dropbox_key_delta, local_key) END AS dropbox_key
          FROM old_rows)
        UPDATE synced_entry SET local_path = f.local_path,
          dropbox_key_delta = nullif(f.dropbox_key, f.local_key),
          dropbox_path_delta = nullif(f.dropbox_path, f.local_path)
        FROM final_rows f WHERE synced_entry.rowid = f.id;
        """;
    private const string SyncedLocalSelf = "pair_id=" + PairRow + " AND " + KeyOf + "local_path)=$k";
    private const string SyncedLocalDescendants = "pair_id=" + PairRow + " AND " + KeyOf + "local_path)>=$k||'/' AND " + KeyOf + "local_path)<$k||'0'";
    private const string SyncedBothSubtrees = "rowid IN (" +
        "SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id=" + PairRow + " AND " + KeyOf + "local_path)=$localKey UNION ALL " +
        "SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id=" + PairRow + " AND " + KeyOf + "local_path)>=$localKey||'/' AND " + KeyOf + "local_path)<$localKey||'0')" +
        " AND (dropbox_key=$dropboxKey OR (dropbox_key>=$dropboxKey||'/' AND dropbox_key<$dropboxKey||'0'))";
    private const string ReturningSynced = " RETURNING " + SyncedColumns + ";";
    public const string DeleteSyncedLocalKey = "DELETE FROM synced_entry WHERE " + SyncedLocalSelf + ReturningSynced;
    public const string DeleteSyncedDropboxKey = "DELETE FROM synced_entry WHERE " + SyncedDropboxSelf + ReturningSynced;
    // Subtrees spend with INSERT SELECT, avoiding SQLite's in-memory RETURNING embargo for a whole tree.
    private const string SpendSynced = "INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) SELECT pair_id,historykey(local_path),1 FROM synced_entry WHERE ";
    private const string SpendRetired = " AND ($retained='[]' OR " + KeyOf + "local_path) NOT IN (SELECT value FROM json_each($retained))) ON CONFLICT(pair_id,path_key) DO UPDATE SET spent=1 WHERE spent=0;";
    public const string ParamRetained = "$retained";
    public const string HistoryKeyFunction = "historykey";
    public const string DeleteSyncedSubtreeLocal = SpendSynced + SyncedLocalSelf + SpendRetired +
        SpendSynced + SyncedLocalDescendants + SpendRetired +
        "DELETE FROM synced_entry WHERE " + SyncedLocalSelf + ";DELETE FROM synced_entry WHERE " + SyncedLocalDescendants + ";";
    public const string DeleteSyncedSubtreeDropbox = SpendSynced + SyncedDropboxSelf + SpendRetired +
        SpendSynced + SyncedDropboxSubtree + SpendRetired +
        "DELETE FROM synced_entry WHERE " + SyncedDropboxSelf + ";DELETE FROM synced_entry WHERE " + SyncedDropboxSubtree + ";";
    public const string DeleteSyncedBothSubtrees = SpendSynced + SyncedBothSubtrees + SpendRetired +
        "DELETE FROM synced_entry WHERE " + SyncedBothSubtrees + ";";
    public const string DeleteSyncedEitherKey = "DELETE FROM synced_entry WHERE pair_id=" + PairRow + " AND " + KeyOf + "local_path)=$localKey" + ReturningSynced +
        "DELETE FROM synced_entry WHERE rowid IN (SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id=" + PairRow + " AND dropbox_key_delta=$dropboxKey UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id=" + PairRow + " AND " + KeyOf + "local_path)=$dropboxKey AND dropbox_key_delta IS NULL AND $dropboxKey<>$localKey)" + ReturningSynced;
    public const string InsertSynced =
        "INSERT INTO synced_entry(pair_id, local_path, dropbox_key_delta, dropbox_path_delta, " +
        "is_folder, local_id, local_size, local_mtime, dropbox_id, dropbox_hash, local_published_journal, local_published_usn) " +
        "VALUES(" + PairRow + ", $localPath, nullif($dropboxKey, $localKey), nullif($dropboxPath, $localPath), " +
        "$isFolder, $localId, $localSize, $localMtime, $dropboxId, $dropboxHash, $publishedJournal, $publishedUsn);";

    // The two unique indexes cannot exclude a collision between an inherited and an overridden key.
    public const string ClashWithInherited = "SELECT EXISTS(SELECT 1 FROM synced_entry WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "local_path) = $dropboxKey AND dropbox_key_delta IS NULL AND " + KeyOf + "local_path) <> $localKey);";
    public const string ClashWithOverride = "SELECT EXISTS(SELECT 1 FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND dropbox_key_delta = $dropboxKey AND " + KeyOf + "local_path) <> $localKey);";
    public const string ClashInDropboxSubtree = "SELECT EXISTS(\r\n  SELECT 1 FROM synced_entry a INDEXED BY synced_dropbox_key\r\n  CROSS JOIN synced_entry b INDEXED BY synced_local_key\r\n  WHERE a.pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND a.dropbox_key_delta >= $k || '/' AND a.dropbox_key_delta < $k || '0'\r\n    AND b.pair_id = a.pair_id AND " + KeyOf + "b.local_path) = +a.dropbox_key_delta AND b.dropbox_key_delta IS NULL\r\n  UNION ALL\r\n  SELECT 1 FROM synced_entry a INDEXED BY synced_local_key\r\n  CROSS JOIN synced_entry b INDEXED BY synced_dropbox_key\r\n  WHERE a.pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "a.local_path) >= $k || '/' AND " + KeyOf + "a.local_path) < $k || '0'\r\n    AND a.dropbox_key_delta IS NULL AND b.pair_id = a.pair_id AND b.dropbox_key_delta = " + KeyOf + "a.local_path));";

    // issues (section 2.4)
    public const string IssueColumns = "issue_id, kind, cause, path, subject_json, at_ticks, dismissed, ignored";
    internal const string IssueFactsTouchedFunction = "issuefactstouched";
    private const string IssueFactKindSeparator = ",";
    private static readonly string IssueFactKinds = string.Join(IssueFactKindSeparator,
        IssuePolicy.HoldingKinds.Select(kind => (int)kind));
    private const string IssueFactsTriggerTemplate =
        "CREATE TEMP TRIGGER issue_facts_insert AFTER INSERT ON main.issue WHEN NEW.kind IN ({0}) " +
        "BEGIN SELECT " + IssueFactsTouchedFunction + "(NEW.issue_id,NEW.pair_id); END;" +
        "CREATE TEMP TRIGGER issue_facts_update AFTER UPDATE OF kind,cause,path,subject_json,at_ticks,dismissed ON main.issue " +
        "WHEN OLD.kind IN ({0}) OR NEW.kind IN ({0}) BEGIN SELECT " +
        IssueFactsTouchedFunction + "(NEW.issue_id,NEW.pair_id); END;" +
        "CREATE TEMP TRIGGER issue_facts_delete AFTER DELETE ON main.issue WHEN OLD.kind IN ({0}) " +
        "BEGIN SELECT " + IssueFactsTouchedFunction + "(OLD.issue_id,OLD.pair_id); END;";
    internal static readonly string CreateIssueFactsTriggers =
        string.Format(System.Globalization.CultureInfo.InvariantCulture, IssueFactsTriggerTemplate, IssueFactKinds);
    private const string IssueFactsSelectTemplate = "SELECT " + IssueColumns + ",p.id FROM pair p LEFT JOIN issue " +
        "ON issue.pair_id=p.id AND kind IN ({0}) WHERE p.pair_id=$p ORDER BY issue_id;";
    internal static readonly string SelectIssueFacts =
        string.Format(System.Globalization.CultureInfo.InvariantCulture, IssueFactsSelectTemplate, IssueFactKinds);
    internal const string SelectIssueFact = "SELECT " + IssueColumns + ",pair_id FROM issue WHERE issue_id=$id;";
    internal const int IssueFactPair = IssueIgnored + 1;

    public const string SelectIssues = "SELECT " + IssueColumns + " FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) ORDER BY issue_id;";
    public const string SelectIssuesByKinds = "SELECT " + IssueColumns + " FROM issue WHERE pair_id = " + PairRow +
        " AND kind IN (SELECT value FROM json_each($kinds)) ORDER BY issue_id;";
    // One bounded page of open issues (pair, account or all pairs, optional kinds and path search) and its
    // separate count in the same read transaction. The pair id trails the issue columns, at IssueIgnored + 1.
    // The path search calls the registered PathContainsFunction, which is backed by PathNames.Contains.
    public const string PathContainsFunction = "pathcontains";
    public const string PublicIssueKindExpression =
        "CASE WHEN i.kind = $publicSourceKind AND i.cause IN (SELECT value FROM json_each($publicCauses))" +
        " THEN $publicTargetKind ELSE i.kind END";
    private const string PublicIssueKindFilter =
        " AND ($kinds IS NULL OR (" + PublicIssueKindExpression + ") IN (SELECT value FROM json_each($kinds)))";
    public const string SelectIssuePage = "SELECT " + IssueColumns +
        ",p.pair_id FROM issue i JOIN pair p ON p.id = i.pair_id WHERE i.dismissed = 0 AND i.ignored = $ignored" +
        " AND ($p IS NULL OR p.pair_id = $p)" +
        " AND ($account IS NULL OR json_extract(p.def_json,'$.ConnectionId') = $account)" +
        PublicIssueKindFilter +
        " AND ($search IS NULL OR " + PathContainsFunction + "(i.path, $search))" +
        " ORDER BY i.issue_id LIMIT $limit OFFSET $offset;";
    public const string CountIssuePage = "SELECT count(*) FROM issue i JOIN pair p ON p.id = i.pair_id" +
        " WHERE i.dismissed = 0 AND i.ignored = $ignored AND ($p IS NULL OR p.pair_id = $p)" +
        " AND ($account IS NULL OR json_extract(p.def_json,'$.ConnectionId') = $account)" +
        PublicIssueKindFilter +
        " AND ($search IS NULL OR " + PathContainsFunction + "(i.path, $search));";
    // One keyset page of open issues after the last seen id. It carries no count, so a full listing counts once
    // instead of once per page. The pair id trails the issue columns, at IssueIgnored + 1.
    public const string SelectIssuePageAfter = "SELECT " + IssueColumns +
        ",p.pair_id FROM issue i JOIN pair p ON p.id = i.pair_id WHERE i.dismissed = 0 AND i.ignored = $ignored" +
        PublicIssueKindFilter +
        " AND i.issue_id > $after ORDER BY i.issue_id LIMIT $limit;";
    public const string SelectClosureKeys = "SELECT path, kind FROM issue WHERE pair_id = " + PairRow +
        " AND kind IN (SELECT value FROM json_each($kinds));";
    public const string UpdateIssueSubject = "UPDATE issue SET subject_json = $subject WHERE pair_id = " + PairRow +
        " AND path = $path AND kind = $kind;";
    public const string SelectIssueById = "SELECT " + IssueColumns + " FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND issue_id = $id;";
    public const string SelectIssueByPathKind = "SELECT " + IssueColumns + " FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND path = $path AND kind = $kind;";
    public const string SelectRootIssueByKind = "SELECT " + IssueColumns + " FROM issue WHERE pair_id=$pair AND path='' AND kind=$kind;";
    public const string SelectIssueByPathKeyKind = "SELECT " + IssueColumns + " FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND " + KeyOf + "path) = $path AND kind = $kind;";
    public const string SelectHoldingSubjects = "SELECT kind, subject_json, cause, issue_id, path FROM issue WHERE pair_id = " +
        PairRow + " ORDER BY issue_id;";
    public const string SelectIssuePathsByKind = "SELECT path FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND kind = $kind;";
    public const string HasOpenIssue = "SELECT EXISTS(SELECT 1 FROM issue WHERE pair_id = " + PairRow +
        " AND kind = $kind AND dismissed = 0);";
    public const string CountIssues =
        "SELECT " + PublicIssueKindExpression + ", count(*) FROM issue i WHERE i.pair_id = " + PairRow +
        " AND i.dismissed = 0 AND ($ignored IS NULL OR i.ignored=$ignored) GROUP BY 1;";
    public const string InsertIssue = """
        INSERT INTO issue(pair_id, kind, cause, path, subject_json, at_ticks, dismissed)
        VALUES((SELECT id FROM pair WHERE pair_id = $p), $kind, $cause, $path, $subject, $at, 0);
        """;
    public const string ReplaceIssue = """
        UPDATE issue SET cause = $cause, subject_json = $subject, at_ticks = $at, dismissed = 0, ignored = 0
        WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND path = $path AND kind = $kind;
        """;
    public const string DeleteIssueByPathKind = "DELETE FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND path = $path AND kind = $kind;";
    private const string ExactIssueFence = "pair_id=$pair AND issue_id=$issue AND path=$path AND kind=$kind " +
        "AND cause=$expectedCause AND subject_json=$expected AND at_ticks=$expectedAt AND dismissed=$expectedDismissed " +
        "AND item_retry_episode IS $episode AND item_retry_revision IS $revision AND item_retry_work_id IS $work";
    /// <summary>The issue owner authorizes closure; zero affected rows authorizes no association cleanup or fallback.</summary>
    public const string CloseIssueExact = "DELETE FROM issue WHERE " + ExactIssueFence + ";";
    /// <summary>Replaces only the authorized snapshot and ends its old retry binding in the same transition.</summary>
    public const string ReplaceIssueExact = "UPDATE issue SET cause=$cause,subject_json=$subject,at_ticks=$at,dismissed=0,ignored=0," +
        "item_retry_episode=NULL,item_retry_revision=NULL,item_retry_work_id=NULL WHERE " + ExactIssueFence + ";";
    /// <summary>Removes only captured associations detached by the successful guarded issue deletion.</summary>
    private const string ClosedMemberFence = "pair_id=$pair AND issue_id IS NULL AND member_id IN (SELECT value FROM json_each($ids))";
    public const string DeleteClosedIssueMembers = "DELETE FROM issue_member WHERE " + ClosedMemberFence + ";";
    public const string SetIssueIgnored = "UPDATE issue SET ignored=$ignored WHERE pair_id=" + PairRow + " AND issue_id=$id;";
    public const string ForeignKeysOff = "PRAGMA foreign_keys=OFF;";
    public const string ForeignKeysOn = "PRAGMA foreign_keys=ON;";
    public const string ReadForeignKeys = "PRAGMA foreign_keys;";
    public const string RebuildIssues = """
        CREATE TABLE issue_new(issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
         at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
         item_retry_episode TEXT,
         item_retry_revision INTEGER CHECK(item_retry_revision IS NULL OR item_retry_revision>=0),
         item_retry_work_id INTEGER REFERENCES change_work(work_id) ON DELETE NO ACTION
          CHECK((item_retry_episode IS NULL AND item_retry_revision IS NULL AND item_retry_work_id IS NULL) OR
                (item_retry_episode IS NOT NULL AND length(item_retry_episode)>0
                 AND item_retry_revision IS NOT NULL AND item_retry_work_id IS NOT NULL
                 AND kind=0 AND localaccessretrycause(cause)=1)),
         ignored INTEGER NOT NULL DEFAULT 0 CHECK(ignored IN (0,1)),
         UNIQUE(pair_id,path,kind)) STRICT;
        INSERT INTO issue_new(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,
         item_retry_episode,item_retry_revision,item_retry_work_id)
         SELECT issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,
          item_retry_episode,item_retry_revision,item_retry_work_id FROM issue;
        DROP TABLE issue;
        ALTER TABLE issue_new RENAME TO issue;
        DELETE FROM sqlite_sequence WHERE name='issue' AND seq=0;
        CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;
        """;

    public const string DismissIssue = """
        DELETE FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND issue_id = $id AND kind = $kind;
        UPDATE issue SET dismissed = 1 WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND issue_id = $id;
        """;

    // scan (sections 3.3, 3.4)
    /// <summary>The scan row holding one effective key: its explicit Dropbox mapping, else its own inherited key.</summary>
    private const string ScanKeyUnion = "SELECT rowid FROM scan INDEXED BY scan_key WHERE pair_id = " + PairRow +
        " AND scan_id = $scan AND side = 1 AND key_delta = $k UNION ALL SELECT rowid FROM scan INDEXED BY scan_own WHERE pair_id = " + PairRow +
        " AND scan_id = $scan AND side = 1 AND " + KeyOf + "path) = $k AND key_delta IS NULL";
    // The one BINARY range holding every strict descendant of $k, shared by the scan delete and its existence probe.
    private const string ScanDescendantRange = KeyOf + "path) >= $k || '/' AND " + KeyOf + "path) < $k || '0'";
    public const string ScanDeleteSelf = "DELETE FROM scan_state.scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side AND " + KeyOf + "path) = $k\r\nRETURNING EXISTS(SELECT 1 FROM scan_state.scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side\r\n  AND " + ScanDescendantRange + ");";
    public const string ScanDeleteDescendants = "DELETE FROM scan_state.scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side AND " + ScanDescendantRange + ";";
    /// <summary>True when this scan already holds a descendant of $k, in an earlier committed batch or the current one (#129).</summary>
    public const string ScanHasDescendants = "SELECT EXISTS(SELECT 1 FROM scan_state.scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side AND " + ScanDescendantRange + ");";
    public const string ScanUpsert = "INSERT INTO scan_state.scan(pair_id, scan_id, side, key_delta, path, is_folder, size, mtime, id, rev, hash, skip)\r\nVALUES((SELECT id FROM pair WHERE pair_id = $p), $scan, $side, nullif($key, $ownKey), $path, $isFolder, $size, $mtime, $id, $rev, $hash, $skip)\r\nON CONFLICT(pair_id, scan_id, side, " + KeyOf + "path)) DO UPDATE SET key_delta = excluded.key_delta, path = excluded.path,\r\n  is_folder = excluded.is_folder, size = excluded.size, mtime = excluded.mtime, id = excluded.id,\r\n  rev = excluded.rev, hash = excluded.hash, skip = excluded.skip\r\nWHERE scan.key_delta IS NOT excluded.key_delta OR scan.path IS NOT excluded.path\r\n  OR scan.is_folder IS NOT excluded.is_folder OR scan.size IS NOT excluded.size\r\n  OR scan.mtime IS NOT excluded.mtime OR scan.id IS NOT excluded.id\r\n  OR scan.rev IS NOT excluded.rev OR scan.hash IS NOT excluded.hash OR scan.skip IS NOT excluded.skip;";
    public const string InScanLocal = "SELECT EXISTS(SELECT 1 FROM scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 0 AND " + KeyOf + "path) = $k);";
    public const string InScanDropbox = "SELECT EXISTS(" + ScanKeyUnion + ");";

    // One key is the natural page: both linked/free groups and every mapped candidate stay together.
    public const string NextScanKey = "SELECT min(k) FROM (\r\n  SELECT k FROM (SELECT " + KeyOf + "local_path) AS k FROM synced_entry WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p)\r\n    AND " + KeyOf + "local_path) >= $after AND ($first OR " + KeyOf + "local_path) > $after) ORDER BY " + KeyOf + "local_path) LIMIT 1)\r\n  UNION ALL\r\n  SELECT k FROM (SELECT " + KeyOf + "path) AS k FROM scan INDEXED BY scan_own WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 0\r\n    AND " + KeyOf + "path) >= $after AND ($first OR " + KeyOf + "path) > $after) ORDER BY " + KeyOf + "path) LIMIT 1)\r\n  UNION ALL\r\n  SELECT k FROM (SELECT key_delta AS k FROM scan INDEXED BY scan_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 1\r\n    AND key_delta >= $after AND ($first OR key_delta > $after) ORDER BY key_delta LIMIT 1)\r\n  UNION ALL\r\n  SELECT k FROM (SELECT " + KeyOf + "path) AS k FROM scan INDEXED BY scan_own WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 1\r\n    AND " + KeyOf + "path) >= $after AND ($first OR " + KeyOf + "path) > $after) ORDER BY " + KeyOf + "path) LIMIT 1));";
    public const string NextScopedScanKey = "SELECT min(k) FROM (\r\n  SELECT k FROM (SELECT " + KeyOf + "local_path) AS k FROM synced_entry WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p)\r\n    AND " + KeyOf + "local_path) >= $after AND ($first OR " + KeyOf + "local_path) > $after) AND " + KeyOf + "local_path) < $scopeHigh AND (" + KeyOf + "local_path) = $scope OR " + KeyOf + "local_path) >= $scopeLow) ORDER BY " + KeyOf + "local_path) LIMIT 1)\r\n  UNION ALL\r\n  SELECT k FROM (SELECT " + KeyOf + "path) AS k FROM scan INDEXED BY scan_own WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 0\r\n    AND " + KeyOf + "path) >= $after AND ($first OR " + KeyOf + "path) > $after) AND " + KeyOf + "path) < $scopeHigh AND (" + KeyOf + "path) = $scope OR " + KeyOf + "path) >= $scopeLow) ORDER BY " + KeyOf + "path) LIMIT 1)\r\n  UNION ALL\r\n  SELECT k FROM (SELECT key_delta AS k FROM scan INDEXED BY scan_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 1\r\n    AND key_delta >= $after AND ($first OR key_delta > $after) AND key_delta < $scopeHigh AND (key_delta = $scope OR key_delta >= $scopeLow) ORDER BY key_delta LIMIT 1)\r\n  UNION ALL\r\n  SELECT k FROM (SELECT " + KeyOf + "path) AS k FROM scan INDEXED BY scan_own WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = 1\r\n    AND " + KeyOf + "path) >= $after AND ($first OR " + KeyOf + "path) > $after) AND " + KeyOf + "path) < $scopeHigh AND (" + KeyOf + "path) = $scope OR " + KeyOf + "path) >= $scopeLow) ORDER BY " + KeyOf + "path) LIMIT 1));";

    // Unary + removes TEXT affinity on correlated keys so SQLite can seek the pathkey expression index.
    public const string ScanRows =
        "WITH free_l AS NOT MATERIALIZED (SELECT x.* FROM scan x INDEXED BY scan_own WHERE x.pair_id = " + PairRow + " AND x.scan_id = $scan AND x.side = 0 AND " + KeyOf + "x.path) = $k AND " +
        "NOT EXISTS (SELECT 1 FROM synced_entry s WHERE s.pair_id = " + PairRow + " AND " + KeyOf + "s.local_path) = $k)), " +
        "free_d AS NOT MATERIALIZED (SELECT x.* FROM scan x WHERE x.rowid IN (" + ScanKeyUnion + ") AND " +
        "NOT EXISTS (SELECT 1 FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = " + PairRow + " AND dropbox_key_delta = $k " +
        "UNION ALL SELECT 1 FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow + " AND " + KeyOf + "local_path) = $k AND dropbox_key_delta IS NULL)), " +
        "free_k AS (SELECT $k AS k FROM free_l UNION SELECT $k FROM free_d) " +
        "SELECT 1 AS synced, $k AS k, s.local_path, s.dropbox_key_delta, s.dropbox_path, s.is_folder, s.local_id, s.local_size, s.local_mtime, s.dropbox_id, s.dropbox_hash, " +
        KeyOf + "l.path) AS l_own, l.path AS l_path, l.is_folder AS l_folder, l.size AS l_size, l.mtime AS l_mtime, l.id AS l_id, l.skip AS l_skip, " +
        KeyOf + "d.path) AS d_own, d.path AS d_path, d.is_folder AS d_folder, d.size AS d_size, d.mtime AS d_mtime, d.id AS d_id, d.rev AS d_rev, d.hash AS d_hash, d.skip AS d_skip " +
        "FROM synced_entry s " +
        "LEFT JOIN scan l INDEXED BY scan_own ON l.pair_id = " + PairRow + " AND l.scan_id = $scan AND l.side = 0 AND " + KeyOf + "l.path) = $k " +
        "LEFT JOIN scan d ON d.rowid IN (SELECT rowid FROM scan INDEXED BY scan_key WHERE pair_id = " + PairRow + " AND scan_id = $scan AND side = 1 AND key_delta = coalesce(s.dropbox_key_delta, $k) " +
        "UNION ALL SELECT rowid FROM scan INDEXED BY scan_own WHERE pair_id = " + PairRow + " AND scan_id = $scan AND side = 1 AND " + KeyOf + "path) = +coalesce(s.dropbox_key_delta, $k) AND key_delta IS NULL) " +
        "WHERE s.pair_id = " + PairRow + " AND " + KeyOf + "s.local_path) = $k " +
        "UNION ALL " +
        "SELECT 0, f.k, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, " +
        KeyOf + "l.path), l.path, l.is_folder, l.size, l.mtime, l.id, l.skip, " +
        KeyOf + "d.path), d.path, d.is_folder, d.size, d.mtime, d.id, d.rev, d.hash, d.skip " +
        "FROM free_k f LEFT JOIN free_l l ON 1 LEFT JOIN free_d d ON 1 " +
        "ORDER BY k, synced DESC, l_own, d_own;";

    /// <summary>One scan entry by its side's own key, with the leading ScanRenames columns.</summary>
    public const string SelectScanEntry = "SELECT side, path, is_folder, size, mtime, id, rev, hash, skip " +
        "FROM scan INDEXED BY scan_own WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND scan_id = $scan AND side = $side AND " + KeyOf + "path) = $k;";

    private const string ScanRenameColumns = "SELECT x.side, x.path, x.is_folder, x.size, x.mtime, x.id, x.rev, x.hash, x.skip, " +
        KeyOf + "s.local_path), s.local_path, s.dropbox_key_delta, s.dropbox_path, s.is_folder, s.local_id, s.local_size, s.local_mtime, " +
        "s.dropbox_id, s.dropbox_hash ";
    /// <summary>Rename candidates: a matched id whose whole path changed, compared exactly on both sides.</summary>
    public const string ScanRenames = ScanRenameColumns + """
        FROM scan x CROSS JOIN synced_entry s INDEXED BY synced_local_id
        WHERE x.pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND x.scan_id = $scan AND x.side = 0 AND x.id <> ''
          AND s.pair_id = x.pair_id AND s.local_id = x.id AND x.path <> s.local_path
        """ + " UNION ALL " + ScanRenameColumns + """
        FROM scan x CROSS JOIN synced_entry s INDEXED BY synced_dropbox_id
        WHERE x.pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND x.scan_id = $scan AND x.side = 1 AND x.id <> ''
          AND s.pair_id = x.pair_id AND s.dropbox_id = x.id AND x.path <> s.dropbox_path;
        """;

    public const string PlanFileSuffix = ".plan";
    public const string PlanDepthFunction = "plandepth";
    public const string PlanPragmas = "PRAGMA scan_state.journal_mode=OFF; PRAGMA scan_state.synchronous=OFF;";
    // StoredFolderPlan constructor callbacks: plandepth(string path) => PathNames.Depth(path).

    private const string PlanPair = "(SELECT id FROM durable_state.pair WHERE pair_id=$p)";
    private const string PlanPayload = "policy_key,native_key,present,entry_path,entry_folder,entry_size,entry_mtime,entry_id,entry_rev,entry_hash,entry_skip,saved_local_key,saved_local_path,saved_dropbox_delta,saved_dropbox_path,saved_folder,saved_local_id,saved_size,saved_mtime,saved_dropbox_id,saved_hash";
    private const string PlanFields = "policy_key TEXT NOT NULL,native_key TEXT NOT NULL,present INTEGER NOT NULL,entry_path TEXT,entry_folder INTEGER,entry_size INTEGER,entry_mtime INTEGER,entry_id ANY,entry_rev TEXT,entry_hash ANY,entry_skip TEXT,saved_local_key TEXT,saved_local_path TEXT,saved_dropbox_delta TEXT,saved_dropbox_path_delta TEXT,saved_dropbox_path TEXT GENERATED ALWAYS AS(coalesce(saved_dropbox_path_delta,saved_local_path)) VIRTUAL,saved_folder INTEGER,saved_local_id ANY,saved_size INTEGER,saved_mtime INTEGER,saved_dropbox_id TEXT,saved_hash ANY";

    private const string PlanPresentColumns = "coalesce(x.key_delta,pathkey(x.path)) AS policy_key,pathkey(x.path) AS native_key,1 AS present,x.path AS entry_path,x.is_folder AS entry_folder,x.size AS entry_size,x.mtime AS entry_mtime,x.id AS entry_id,x.rev AS entry_rev,x.hash AS entry_hash,x.skip AS entry_skip,pathkey(s.local_path) AS saved_local_key,s.local_path AS saved_local_path,s.dropbox_key_delta AS saved_dropbox_delta,s.dropbox_path AS saved_dropbox_path,s.is_folder AS saved_folder,s.local_id AS saved_local_id,s.local_size AS saved_size,s.local_mtime AS saved_mtime,s.dropbox_id AS saved_dropbox_id,s.dropbox_hash AS saved_hash";
    private const string PlanPresentFrom = " FROM scan_state.scan x INDEXED BY scan_own LEFT JOIN durable_state.synced_entry s ON s.rowid IN (SELECT rowid FROM durable_state.synced_entry INDEXED BY synced_local_key WHERE pair_id=" + PlanPair + " AND pathkey(local_path)=+coalesce(x.key_delta,pathkey(x.path)) AND ($side=0 OR dropbox_key_delta IS NULL) UNION ALL SELECT rowid FROM durable_state.synced_entry INDEXED BY synced_dropbox_key WHERE $side=1 AND pair_id=" + PlanPair + " AND dropbox_key_delta=coalesce(x.key_delta,pathkey(x.path))) WHERE x.scan_id=$scan AND x.pair_id=" + PlanPair + " AND x.side=$side AND pathkey(x.path)<>$ownKey";
    private const string PlanSavedColumns = "CASE WHEN $side=0 THEN pathkey(s.local_path) ELSE s.dropbox_key END AS policy_key,pathkey(CASE WHEN $side=0 THEN s.local_path ELSE s.dropbox_path END) AS native_key,0 AS present,NULL AS entry_path,NULL AS entry_folder,NULL AS entry_size,NULL AS entry_mtime,NULL AS entry_id,NULL AS entry_rev,NULL AS entry_hash,NULL AS entry_skip,pathkey(s.local_path) AS saved_local_key,s.local_path AS saved_local_path,s.dropbox_key_delta AS saved_dropbox_delta,s.dropbox_path AS saved_dropbox_path,s.is_folder AS saved_folder,s.local_id AS saved_local_id,s.local_size AS saved_size,s.local_mtime AS saved_mtime,s.dropbox_id AS saved_dropbox_id,s.dropbox_hash AS saved_hash";
    private const string PlanNoScan = " AND NOT EXISTS(SELECT 1 FROM scan_state.scan x INDEXED BY scan_own WHERE x.scan_id=$scan AND x.pair_id=" + PlanPair + " AND x.side=$side AND pathkey(x.path)=+(CASE WHEN $side=0 THEN pathkey(s.local_path) ELSE coalesce(s.dropbox_key_delta,pathkey(s.local_path)) END) AND ($side=0 OR x.key_delta IS NULL) UNION ALL SELECT 1 FROM scan_state.scan x INDEXED BY scan_key WHERE $side=1 AND x.scan_id=$scan AND x.pair_id=" + PlanPair + " AND x.side=1 AND x.key_delta=coalesce(s.dropbox_key_delta,pathkey(s.local_path)))";
    private const string PlanSavedInheritedFrom = " FROM durable_state.synced_entry s INDEXED BY synced_local_key WHERE s.pair_id=" + PlanPair + " AND ($side=0 OR s.dropbox_key_delta IS NULL) AND pathkey(s.local_path)>=$k||'/' AND pathkey(s.local_path)<$k||'0'" + PlanNoScan;
    private const string PlanSavedOverrideFrom = " FROM durable_state.synced_entry s INDEXED BY synced_dropbox_key WHERE $side=1 AND s.pair_id=" + PlanPair + " AND s.dropbox_key_delta>=$k||'/' AND s.dropbox_key_delta<$k||'0'" + PlanNoScan;

    // Inventory result: native cursor, followed by the 8 StateEncoding.ReadEntry fields.
    public const string PlanEntries = "SELECT pathkey(path),path,is_folder,size,mtime,id,rev,hash,skip FROM scan_state.scan INDEXED BY scan_own WHERE scan_id=$scan AND pair_id=" + PlanPair + " AND side=$side AND pathkey(path)>$after ORDER BY pathkey(path) LIMIT 1;";
    // Candidate result: the 21 PlanPayload columns, followed by its private pagination cursor.
    public const string PlanPresent = "SELECT " + PlanPresentColumns + ",pathkey(x.path) AS cursor" + PlanPresentFrom + " AND pathkey(x.path)>$after ORDER BY pathkey(x.path) LIMIT 1;";
    public const string PlanSaved = "WITH next_saved AS MATERIALIZED (SELECT " + PlanSavedColumns + ",pathkey(s.local_path) AS cursor,0 AS saved_override" + PlanSavedInheritedFrom + " AND $savedOverride=0 AND pathkey(s.local_path)>$after ORDER BY pathkey(s.local_path) LIMIT 1) SELECT * FROM next_saved UNION ALL SELECT * FROM (SELECT " + PlanSavedColumns + ",s.dropbox_key_delta AS cursor,1 AS saved_override" + PlanSavedOverrideFrom + " AND s.dropbox_key_delta>CASE WHEN $savedOverride THEN $after ELSE '' END ORDER BY s.dropbox_key_delta LIMIT CASE WHEN EXISTS(SELECT 1 FROM next_saved) THEN 0 ELSE 1 END);";

    public const string PlanParamSavedOverride = "$savedOverride";
    public const int PlanSavedOverrideColumn = 22;
    // Precedes SQLite's false/true presence values in a before-first keyset cursor.
    internal const int BeforeFirstPresence = -1;
    // Keyset start before the first member ordinal.
    internal const int BeforeFirstOrdinal = -1;
    // Keyset start before the first persisted retry deadline tick.
    internal const long BeforeFirstTicks = -1;
    // Keyset start before the first pair surrogate.
    internal const long BeforeFirstPairRow = 0;
    public const string PlanParamPresent = "$present";
    public const int PlanKeyColumn = 0;
    public const int PlanNativeColumn = 1;
    public const int PlanPresentColumn = 2;
    public const int PlanEntryColumn = 3;
    public const int PlanSavedKeyColumn = 11;
    public const int PlanSavedColumn = 12;
    public const int PlanCursorColumn = 21;
    public const int PlanIdColumn = 21;
    public const int PlanDepthColumn = 22;
    public const int PlanInventoryCursorColumn = 0;
    public const int PlanInventoryEntryColumn = 1;

    private const string FolderRootPath = "json_extract(root_entry_json,'$.Value.Path')";
    public const string CreateFolderRemovalRootIndex = "CREATE INDEX folder_removal_root ON folder_removal(pair_id," +
        FolderRootPath + ",operation_id) WHERE active=1;";
    public const string CreatePendingFolderSubmissionIndex = "CREATE INDEX folder_submission_pending ON folder_removal_submission(pair_id,submission_id) WHERE phase='Accepted';";

    /// <summary>Completed first-Repair discovery; the pair cursor is its sole publication marker.</summary>
    public const string DeletedHistorySchema = """
        ALTER TABLE pair ADD COLUMN deleted_history_cursor TEXT CHECK(deleted_history_cursor IS NULL OR length(deleted_history_cursor)>0);
        CREATE TABLE discovery_deleted_name(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation,first_repair_done ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation OR (new.first_repair_done=1 AND old.first_repair_done=0)
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL WHERE id=new.id;
        END;
        """;
    public const string DeletedHistoryBlocks = """
        ALTER TABLE pair ADD COLUMN deleted_history_owed INTEGER NOT NULL DEFAULT 1 CHECK(deleted_history_owed IN (0,1));
        CREATE TABLE discovery_deleted_block(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          last_key TEXT NOT NULL, name_count INTEGER NOT NULL CHECK(name_count BETWEEN 1 AND 5000),
          names BLOB NOT NULL, PRIMARY KEY(pair_id,last_key)) STRICT;
        CREATE TABLE discovery_deleted_delta(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        DROP TRIGGER invalidate_deleted_history;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id;
        END;
        UPDATE source_state SET repair_reason=coalesce(repair_reason,'NormalRepair'),repair_revision=repair_revision+1;
        """;
    public const string SpentDeletedHistory = """
        ALTER TABLE discovery_deleted_delta ADD COLUMN spent INTEGER NOT NULL DEFAULT 0 CHECK(spent IN (0,1));
        DROP TRIGGER invalidate_deleted_history;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id AND spent=0;
        END;
        """;
    public const string ReplaceDeletedHistoryIdentity = "DELETE FROM discovery_deleted_delta WHERE pair_id=" + PairRow + ";DELETE FROM discovery_deleted_block WHERE pair_id=" + PairRow + ";" + ClearDeletedHistory;
    private const string NotSpentDeletedKeyAt = "NOT EXISTS(SELECT 1 FROM main.discovery_deleted_delta d WHERE d.pair_id=$pair AND d.spent=1 AND d.path_key=";
    private const string NotSpentDeletedKey = NotSpentDeletedKeyAt + "$path)";
    public const string SpentDeletedKey = "SELECT EXISTS(SELECT 1 FROM main.discovery_deleted_delta WHERE pair_id=" + PairRow + " AND path_key=$path AND spent=1);";
    public const string SpendDeletedKey = "INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(" + PairRow + ",$path,1) ON CONFLICT(pair_id,path_key) DO UPDATE SET spent=1 WHERE spent=0;";
    public const string DeletedKeyDelta = "SELECT spent FROM main.discovery_deleted_delta WHERE pair_id=$pair AND path_key=$path;";
    public const string DeletedBlockTable = "discovery_deleted_block";
    public const string DeletedBlockPayload = "names";
    public const int DeletedBlockLast = 0;
    public const int DeletedBlockCount = 1;
    public const int DeletedBlockRow = 2;
    public const int DeletedBlockPrevious = 3;
    public const string DeletedKeyBlock = "SELECT last_key,name_count,rowid,(SELECT max(last_key) FROM main.discovery_deleted_block WHERE pair_id=$pair AND last_key<b.last_key) FROM main.discovery_deleted_block b WHERE pair_id=$pair AND last_key>=$path ORDER BY last_key LIMIT 1;";
    public const string DropDeletedHistoryNames = "DROP TABLE discovery_deleted_name;";
    /// <summary>The journal boundary of the engine's own last write of the row's Local file, NULL when none is proved;
    /// both columns are NULL together. ADD COLUMN with no default rewrites no row.</summary>
    public const string LocalPublishedColumns = "ALTER TABLE synced_entry ADD COLUMN local_published_journal INTEGER;" +
        "ALTER TABLE synced_entry ADD COLUMN local_published_usn INTEGER;";
    public const string LegacyDeletedNames = "SELECT pair_id,path_key FROM discovery_deleted_name ORDER BY pair_id,path_key;";
    public const string LegacyDeletedCount = "SELECT count(*) FROM discovery_deleted_name;";
    public const string ReadDeletedHistoryCursor = "SELECT deleted_history_cursor FROM pair WHERE pair_id=$p AND deleted_history_owed=0;";
    public const string ReadDeletedHistoryOwed = "SELECT deleted_history_owed FROM pair WHERE pair_id=$p;";
    public const string SaveDeletedHistory = "SELECT path_key FROM scan_state.deleted_name WHERE pair_id=$pair AND path_key>coalesce($after,'') AND NOT EXISTS(SELECT 1 FROM main.discovery_deleted_delta d WHERE d.pair_id=$pair AND d.path_key=deleted_name.path_key AND d.spent=1) ORDER BY path_key LIMIT $take;";
    public const string SaveDeletedBlock = "INSERT INTO main.discovery_deleted_block(pair_id,last_key,name_count,names) VALUES($pair,$path,$count,$names);";
    public const string ReadDeletedBlock = "SELECT last_key,name_count,rowid FROM main.discovery_deleted_block WHERE pair_id=$pair AND last_key>coalesce($after,'') ORDER BY last_key LIMIT 1;";
    public const string SavedDeletedHistoryKey = "SELECT max(last_key) FROM main.discovery_deleted_block WHERE pair_id=$pair;";
    public const string ClearDeletedHistory = "UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE pair_id=$p;";
    public const string PublishDeletedHistory = "UPDATE pair SET deleted_history_cursor=$cursor,deleted_history_owed=0 WHERE pair_id=$p;";
    public const string ReclaimDeletedHistory = "DELETE FROM main.discovery_deleted_block WHERE pair_id=$pair AND last_key IN (SELECT last_key FROM main.discovery_deleted_block WHERE pair_id=$pair ORDER BY last_key LIMIT $take) AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND deleted_history_cursor IS NULL);";
    public const string DeletedHistoryCount = "SELECT count(*) FROM scan_state.deleted_name WHERE pair_id=$pair;";
    public const string ParamNames = "$names";
    public const string ParamCount = "$count";
    public const string AppendDeletedHistory = "INSERT OR IGNORE INTO discovery_deleted_delta(pair_id,path_key) VALUES($pair,$path);";
    public const string DeletedDeltaPage = "SELECT path_key FROM discovery_deleted_delta WHERE pair_id=$pair AND spent=0 AND path_key>coalesce($after,'') ORDER BY path_key LIMIT $take;";
    public const string RetireFoldedDeletedNames = "DELETE FROM discovery_deleted_delta WHERE pair_id=$pair AND spent=0 AND path_key IN (SELECT path_key FROM scan_state.deleted_name WHERE pair_id=$pair);";
    public const string ClearScanDropbox = "DELETE FROM scan_state.scan WHERE pair_id=" + PairRow + " AND scan_id=$scan AND side=1;";
    public const string ClearScanDeletedNames = "DELETE FROM scan_state.deleted_name WHERE pair_id=" + PairRow + ";";
    public const string DeletedNameLive = "DELETE FROM scan_state.deleted_name WHERE pair_id=$pair AND path_key=$path;";


    // Schema 5 durable tables and indexes, in execution order inside the one upgrade transaction.
    public const string SchemaAdditions = """
        ALTER TABLE pair ADD COLUMN execution_generation INTEGER NOT NULL DEFAULT 0 CHECK(execution_generation>=0);
        ALTER TABLE pair ADD COLUMN retry_attempts INTEGER CHECK(retry_attempts IS NULL OR retry_attempts BETWEEN 0 AND 2147483647);
        ALTER TABLE pair ADD COLUMN retry_after_ticks INTEGER CHECK((retry_attempts IS NULL AND retry_after_ticks IS NULL) OR (retry_attempts IS NOT NULL AND retry_after_ticks IS NOT NULL AND retry_after_ticks BETWEEN 0 AND 3155378975999999999));
        CREATE INDEX pair_retry_due ON pair(retry_after_ticks,id) WHERE retry_after_ticks IS NOT NULL;
        CREATE TABLE journal_position(
         volume_key TEXT PRIMARY KEY NOT NULL CHECK(length(volume_key)>0),
         journal_id INTEGER NOT NULL, next_usn INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE source_state(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         source_epoch INTEGER NOT NULL CHECK(source_epoch>=0),
         provenance_json TEXT CHECK(provenance_json IS NULL OR (json_valid(provenance_json) AND coalesce(json_extract(provenance_json,'$.Version'),0)=1)),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         repair_reason TEXT CHECK(repair_reason IS NULL OR length(repair_reason)>0),
         repair_revision INTEGER NOT NULL CHECK(repair_revision>=0),
         PRIMARY KEY(pair_id,side),
         CHECK(side=1 OR cursor IS NULL),
         CHECK(cursor IS NULL OR provenance_json IS NOT NULL)
        ) STRICT;
        CREATE TABLE change_work(
         work_id INTEGER PRIMARY KEY AUTOINCREMENT,
         work_origin TEXT NOT NULL DEFAULT 'Acquired' CHECK(work_origin IN ('Acquired','Issue')),
         issue_work_key TEXT,
         dispatch_kind TEXT NOT NULL DEFAULT 'OrdinaryItem' CHECK(dispatch_kind IN ('OrdinaryItem','LocalBranch','LocalRecovery')),
         pending_operation_id TEXT,
         pending_generation INTEGER CHECK(pending_generation IS NULL OR pending_generation>=0),
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         pair_generation INTEGER NOT NULL CHECK(pair_generation>=0),
         source_epoch INTEGER NOT NULL CHECK(source_epoch>=0),
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         domain TEXT NOT NULL CHECK(length(domain)>0),
         path_key TEXT NOT NULL, old_path_key TEXT,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         notice_json TEXT CHECK(notice_json IS NULL OR (json_valid(notice_json) AND coalesce(json_extract(notice_json,'$.Version'),0)=1)),
         before_json TEXT CHECK(before_json IS NULL OR (json_valid(before_json) AND coalesce(json_extract(before_json,'$.Version'),0)=1)),
         acquisition_work_id INTEGER CHECK(acquisition_work_id IS NULL OR (acquisition_work_id>0 AND acquisition_work_id<=work_id)),
         parent_anchor INTEGER, obligation_key TEXT,
         CHECK(work_id>0),
         CHECK((work_origin='Acquired' AND notice_json IS NOT NULL AND acquisition_work_id IS NOT NULL) OR
               (work_origin='Issue' AND notice_json IS NULL AND acquisition_work_id IS NULL AND parent_anchor IS NULL
                AND obligation_key IS NULL AND issue_work_key IS NOT NULL AND length(issue_work_key)>0)),
         CHECK((dispatch_kind='LocalRecovery' AND pending_operation_id IS NOT NULL AND length(pending_operation_id)>0 AND pending_generation IS NOT NULL) OR
               (dispatch_kind<>'LocalRecovery' AND pending_operation_id IS NULL AND pending_generation IS NULL)),
         CHECK((parent_anchor IS NULL AND obligation_key IS NULL) OR
               (parent_anchor IS NOT NULL AND parent_anchor>0 AND parent_anchor<work_id AND obligation_key IS NOT NULL AND length(obligation_key)>0)),
         UNIQUE(parent_anchor,obligation_key)
        ) STRICT;
        CREATE INDEX work_pair_order ON change_work(pair_id,pair_generation,work_id);
        CREATE INDEX work_source_order ON change_work(pair_id,pair_generation,side,source_epoch,work_id) WHERE work_origin='Acquired';
        CREATE INDEX work_identity ON change_work(pair_id,pair_generation,side,source_epoch,domain,identity,work_id);
        CREATE INDEX work_cross_identity ON change_work(side,domain,identity,pair_id,work_id);
        CREATE INDEX work_path ON change_work(pair_id,pair_generation,side,domain,path_key,work_id);
        CREATE INDEX work_old_path ON change_work(pair_id,pair_generation,side,domain,old_path_key,work_id) WHERE old_path_key IS NOT NULL;
        CREATE TABLE issue_member(
         member_id INTEGER PRIMARY KEY,
         issue_id INTEGER REFERENCES issue(issue_id) ON DELETE SET NULL,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         work_id INTEGER REFERENCES change_work(work_id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)), path_key TEXT NOT NULL,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         subject_json TEXT NOT NULL CHECK(json_valid(subject_json) AND coalesce(json_extract(subject_json,'$.Version'),0)=1),
         skip_cause TEXT,
         CHECK((work_id IS NOT NULL AND skip_cause IS NULL) OR
               (work_id IS NULL AND issue_id IS NOT NULL AND skip_cause IS NOT NULL AND length(skip_cause)>0))
        ) STRICT;
        CREATE UNIQUE INDEX issue_member_work ON issue_member(work_id) WHERE work_id IS NOT NULL;
        CREATE INDEX issue_member_issue ON issue_member(issue_id,side,path_key);
        CREATE UNIQUE INDEX issue_member_skip ON issue_member(pair_id,side,path_key) WHERE work_id IS NULL;
        CREATE TABLE folder_removal(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         operation_id TEXT NOT NULL CHECK(length(operation_id)>0),
         generation INTEGER NOT NULL CHECK(generation>=0),
         phase TEXT NOT NULL CHECK(phase IN ('Prepared','Observed','SubmitArmed','Submitted','Terminal','Checking','Recovering','Complete','Unknown','Retained')),
         deleting_side INTEGER NOT NULL CHECK(deleting_side IN (0,1)),
         root_entry_json TEXT NOT NULL CHECK(json_valid(root_entry_json) AND coalesce(json_extract(root_entry_json,'$.Version'),0)=1),
         source_evidence_json TEXT NOT NULL CHECK(json_valid(source_evidence_json) AND coalesce(json_extract(source_evidence_json,'$.Version'),0)=1),
         saved_root_json TEXT CHECK(saved_root_json IS NULL OR (json_valid(saved_root_json) AND coalesce(json_extract(saved_root_json,'$.Version'),0)=1)),
         root_context_json TEXT NOT NULL CHECK(json_valid(root_context_json) AND coalesce(json_extract(root_context_json,'$.Version'),0)=1),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         active INTEGER NOT NULL CHECK(active IN (0,1)), cause TEXT,
         PRIMARY KEY(pair_id,operation_id), CHECK(phase<>'Complete' OR active=0)
        ) STRICT;
        CREATE INDEX folder_removal_active ON folder_removal(pair_id,active,operation_id);
        """ + CreateFolderRemovalRootIndex + """
        CREATE TABLE folder_removal_submission(
         submission_id TEXT PRIMARY KEY NOT NULL CHECK(length(submission_id)>0),
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         generation INTEGER NOT NULL CHECK(generation>=0),
         phase TEXT NOT NULL CHECK(phase IN ('Armed','Accepted','Terminal','Unknown')),
         job_id TEXT CHECK(job_id IS NULL OR length(job_id)>0), cause TEXT,
         CHECK(phase<>'Accepted' OR job_id IS NOT NULL),
         CHECK(phase<>'Armed' OR job_id IS NULL)
        ) STRICT;
        """ + CreatePendingFolderSubmissionIndex + """
        CREATE TABLE folder_removal_item(
         pair_id INTEGER NOT NULL, operation_id TEXT NOT NULL,
         ordinal INTEGER NOT NULL CHECK(ordinal>=0),
         generation INTEGER NOT NULL CHECK(generation>=0),
         provenance TEXT NOT NULL CHECK(provenance IN ('Planned','Unexpected','BlockingParent')),
         path TEXT NOT NULL, policy_key TEXT NOT NULL,
         native_key TEXT GENERATED ALWAYS AS (pathkey(path)) VIRTUAL,
         present INTEGER GENERATED ALWAYS AS (expected_json IS NOT NULL) VIRTUAL,
         expected_json TEXT CHECK(expected_json IS NULL OR (json_valid(expected_json) AND coalesce(json_extract(expected_json,'$.Version'),0)=1)),
         saved_json TEXT CHECK(saved_json IS NULL OR (json_valid(saved_json) AND coalesce(json_extract(saved_json,'$.Version'),0)=1)),
         verdict TEXT NOT NULL CHECK(verdict IN ('Survives','Removed')),
         follow_up INTEGER NOT NULL CHECK(follow_up IN (0,1)),
         follow_up_state TEXT NOT NULL CHECK(follow_up_state IN ('None','Pending','Done')),
         selected_branch INTEGER NOT NULL CHECK(selected_branch IN (0,1)),
         effect_kind TEXT NOT NULL CHECK(effect_kind IN ('None','RecursiveFolder','ConditionalFile','StateOnly')),
         effect_state TEXT NOT NULL CHECK(effect_state IN ('None','Ready','Armed','Accepted','Verified','NoEffect','Unknown','Settled')),
         effect_generation INTEGER NOT NULL CHECK(effect_generation>=0),
         submission_id TEXT REFERENCES folder_removal_submission(submission_id) ON DELETE NO ACTION,
         submission_member INTEGER CHECK(submission_member IS NULL OR submission_member>=0),
         effect_result_json TEXT CHECK(effect_result_json IS NULL OR (json_valid(effect_result_json) AND coalesce(json_extract(effect_result_json,'$.Version'),0)=1)),
         phase TEXT NOT NULL CHECK(phase IN ('Planned','DeleteArmed','Deleted','RecoverySelected','AllocateArmed','StageArmed','Staged','PublishArmed','Published','Conflict','Unknown','DiscardArmed','Settled')),
         selected_json TEXT CHECK(selected_json IS NULL OR (json_valid(selected_json) AND coalesce(json_extract(selected_json,'$.Version'),0)=1)),
         stage_allocation_json TEXT CHECK(stage_allocation_json IS NULL OR (json_valid(stage_allocation_json) AND coalesce(json_extract(stage_allocation_json,'$.Version'),0)=1)),
         stage_json TEXT CHECK(stage_json IS NULL OR (json_valid(stage_json) AND coalesce(json_extract(stage_json,'$.Version'),0)=1)),
         result_json TEXT CHECK(result_json IS NULL OR (json_valid(result_json) AND coalesce(json_extract(result_json,'$.Version'),0)=1)),
         cause TEXT,
         PRIMARY KEY(pair_id,operation_id,ordinal),
         FOREIGN KEY(pair_id,operation_id) REFERENCES folder_removal(pair_id,operation_id) ON DELETE CASCADE,
         CHECK((follow_up=0 AND follow_up_state='None') OR (follow_up=1 AND follow_up_state IN ('Pending','Done'))),
         CHECK((effect_kind='None' AND effect_state='None') OR (effect_kind<>'None' AND effect_state<>'None')),
         CHECK((submission_id IS NULL AND submission_member IS NULL) OR
               (submission_id IS NOT NULL AND submission_member IS NOT NULL AND effect_kind IN ('RecursiveFolder','ConditionalFile') AND effect_state IN ('Armed','Accepted','Verified','NoEffect','Unknown','Settled'))),
         CHECK(effect_state NOT IN ('Armed','Accepted') OR submission_id IS NOT NULL),
         CHECK(effect_kind NOT IN ('RecursiveFolder','ConditionalFile') OR (expected_json IS NOT NULL AND verdict='Removed')),
         CHECK(phase NOT IN ('RecoverySelected','AllocateArmed','StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR selected_json IS NOT NULL),
         CHECK(phase NOT IN ('AllocateArmed','StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR stage_allocation_json IS NOT NULL),
         CHECK(phase NOT IN ('StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR stage_json IS NOT NULL),
         CHECK(phase<>'Published' OR result_json IS NOT NULL)
        ) STRICT;
        CREATE UNIQUE INDEX folder_item_identity ON folder_removal_item(pair_id,operation_id,native_key,provenance,present);
        CREATE INDEX folder_item_followup ON folder_removal_item(pair_id,operation_id,follow_up_state,ordinal);
        CREATE INDEX folder_item_effect ON folder_removal_item(pair_id,operation_id,effect_state,ordinal);
        CREATE UNIQUE INDEX folder_submission_member ON folder_removal_item(submission_id,submission_member) WHERE submission_id IS NOT NULL;
        CREATE INDEX folder_item_ready_order ON folder_removal_item(pair_id,operation_id,-plandepth(policy_key),policy_key,native_key,present,ordinal) WHERE effect_state='Ready';

        CREATE UNIQUE INDEX work_issue_registration ON change_work(pair_id,issue_work_key) WHERE issue_work_key IS NOT NULL;
        ALTER TABLE issue ADD COLUMN item_retry_episode TEXT;
        ALTER TABLE issue ADD COLUMN item_retry_revision INTEGER CHECK(item_retry_revision IS NULL OR item_retry_revision>=0);
        ALTER TABLE issue ADD COLUMN item_retry_work_id INTEGER REFERENCES change_work(work_id) ON DELETE NO ACTION
         CHECK((item_retry_episode IS NULL AND item_retry_revision IS NULL AND item_retry_work_id IS NULL) OR
               (item_retry_episode IS NOT NULL AND length(item_retry_episode)>0
                AND item_retry_revision IS NOT NULL AND item_retry_work_id IS NOT NULL
                AND kind=0 AND localaccessretrycause(cause)=1));
        CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;
        CREATE INDEX issue_member_retry_subject ON issue_member(pair_id,side,path_key,identity) WHERE work_id IS NOT NULL;
        ALTER TABLE folder_removal ADD COLUMN retirement_disposition TEXT
         CHECK(retirement_disposition IS NULL OR (retirement_disposition IN ('Acknowledged','NoEffect') AND active=0 AND phase IN ('Unknown','Retained')));
        """;
    /// <summary>The schema an existing database must hold before the upgrade runs.</summary>
    public const int UpgradeFromVersion = 4;
    /// <summary>The previous member schema, differing only by its unused phase index.</summary>
    public const int PhaseIndexVersion = 5;
    public const string CreateObsoletePhaseIndex = "CREATE INDEX folder_item_phase ON folder_removal_item(pair_id,operation_id,phase,ordinal);";
    public const string DropObsoletePhaseIndex = "DROP INDEX folder_item_phase;";
    public const string DiscoveryRequiredColumn = "ALTER TABLE change_work ADD COLUMN discovery_required INTEGER NOT NULL DEFAULT 0 CHECK(discovery_required IN (0,1));";
    /// <summary>The last modifying statement of the upgrade or a fresh creation.</summary>
    public const string RenameContinuationColumns = "ALTER TABLE change_work ADD COLUMN rename_continuation_json TEXT CHECK(rename_continuation_json IS NULL OR (json_valid(rename_continuation_json) AND coalesce(json_extract(rename_continuation_json,'$.Version'),0)=1)); ALTER TABLE folder_removal ADD COLUMN rename_work_id INTEGER REFERENCES change_work(work_id) ON DELETE RESTRICT; CREATE INDEX folder_rename_work ON folder_removal(rename_work_id) WHERE rename_work_id IS NOT NULL;";
    public const string PublishVersion = "PRAGMA user_version=14;";
    public const string LocalAccessRetryCauseFunction = "localaccessretrycause";

    // Upgrade validation: the actual structure is compared before and after the additions.
    public const string SchemaObjects = "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name;";
    public const string TableColumns = "SELECT name,type,[notnull],dflt_value,pk,hidden FROM pragma_table_xinfo($table) ORDER BY cid;";
    public const string TableForeignKeys = "SELECT id,seq,[table],[from],[to],on_update,on_delete,match FROM pragma_foreign_key_list($table) ORDER BY id,seq;";
    public const string TableIndexes = "SELECT name,[unique],origin,partial FROM pragma_index_list($table) ORDER BY name;";
    public const string IndexColumns = "SELECT seqno,cid,name,[desc],coll,key FROM pragma_index_xinfo($index) ORDER BY seqno;";
    public const string ForeignKeyViolations = "PRAGMA foreign_key_check;";
    /// <summary>The upgrade-only callback for the dormant name mapping; no index or generated column reads it.</summary>
    public const string MappedSpellingFunction = "mappedspelling";
    private const string MappedRow = "dropbox_path <> " + MappedSpellingFunction + "(dropbox_path)";
    /// <summary>Rewrites every mapped saved row's Dropbox join key to its raw key, inside the upgrade transaction.</summary>
    public const string ConvertMappedDropboxKeys = "UPDATE synced_entry SET dropbox_key_delta = nullif(" + KeyOf + "dropbox_path), " + KeyOf + "local_path)) WHERE " + MappedRow + ";";
    /// <summary>Whether two rows of one pair share a final effective Dropbox key, inherited, overridden or converted;
    /// true refuses the upgrade. The answer is the same before and after the conversion, so it runs first: a collision
    /// between converting rows would otherwise surface as the partial unique index's own failure.</summary>
    public const string EffectiveDropboxKeyCollisions = "SELECT EXISTS(SELECT 1 FROM (SELECT pair_id, CASE WHEN " + MappedRow +
        " THEN " + KeyOf + "dropbox_path) ELSE dropbox_key END AS final_key FROM synced_entry) GROUP BY pair_id, final_key HAVING count(*) > 1);";

    // Disposable scan relations; deleted_name exists only for discovery-enabled scans.
    public const string CreateSkipTouched = "CREATE TABLE IF NOT EXISTS scan_state.skip_touched(pair_id INTEGER NOT NULL,scan_id BLOB NOT NULL,side INTEGER NOT NULL CHECK(side IN (0,1)),path_key TEXT NOT NULL,PRIMARY KEY(pair_id,scan_id,side,path_key)) STRICT;";
    public const string DeleteScanBatch = "DELETE FROM scan_state.scan WHERE rowid IN (SELECT rowid FROM scan_state.scan INDEXED BY scan_own WHERE pair_id=$pair AND scan_id=$scan LIMIT $take);";
    public const string DeleteTouchBatch = "DELETE FROM scan_state.skip_touched WHERE rowid IN (SELECT rowid FROM scan_state.skip_touched WHERE pair_id=$pair AND scan_id=$scan LIMIT $take);";
    public const string ClearDeletedNames = "DELETE FROM scan_state.deleted_name WHERE pair_id=$pair;";
    public const string CreateScanIssues = "CREATE TABLE IF NOT EXISTS scan_state.scan_issue(scan_id BLOB NOT NULL,issue_id INTEGER NOT NULL,path TEXT NOT NULL,kind INTEGER NOT NULL,cause TEXT NOT NULL,subject_json TEXT NOT NULL,at_ticks INTEGER NOT NULL,dismissed INTEGER NOT NULL,episode TEXT,revision INTEGER,work_id INTEGER,PRIMARY KEY(scan_id,issue_id)) STRICT;";
    public const string CaptureScanIssues = "INSERT INTO scan_state.scan_issue SELECT $scan,issue_id,path,kind,cause,subject_json,at_ticks,dismissed,item_retry_episode,item_retry_revision,item_retry_work_id FROM issue WHERE pair_id=$pair;";
    public const string ScanIssueUnchanged = "SELECT EXISTS(SELECT 1 FROM scan_state.scan_issue s JOIN issue i ON i.issue_id=s.issue_id WHERE s.scan_id=$scan AND s.issue_id=$issue AND i.pair_id=$pair AND i.path=s.path AND i.kind=s.kind AND i.cause=s.cause AND i.subject_json=s.subject_json AND i.at_ticks=s.at_ticks AND i.dismissed=s.dismissed AND i.item_retry_episode IS s.episode AND i.item_retry_revision IS s.revision AND i.item_retry_work_id IS s.work_id);";
    public const string DeleteScanIssueBatch = "DELETE FROM scan_state.scan_issue WHERE rowid IN (SELECT rowid FROM scan_state.scan_issue WHERE scan_id=$scan LIMIT $take);";
    public const string CreateDeletedName = "CREATE TABLE IF NOT EXISTS scan_state.deleted_name(pair_id INTEGER NOT NULL,path_key TEXT NOT NULL,PRIMARY KEY(pair_id,path_key)) STRICT;";
    // The plan's one classified relation; create exactly one of the two root index variants.
    public const string PlanCreateClassified = "CREATE TABLE scan_state.classified(id INTEGER PRIMARY KEY," + PlanFields + ",verdict TEXT NOT NULL CHECK(verdict IN ('Survives','Removed')),follow_up INTEGER NOT NULL CHECK(follow_up IN (0,1)),selected INTEGER NOT NULL DEFAULT 0 CHECK(selected IN (0,1)),UNIQUE(policy_key,native_key,present)) STRICT;CREATE INDEX scan_state.classified_order ON classified(-plandepth(policy_key),policy_key,native_key,present);CREATE INDEX scan_state.classified_survivor ON classified(policy_key,native_key,present) WHERE verdict='Survives';CREATE INDEX scan_state.classified_selected_order ON classified(-plandepth(policy_key),policy_key,native_key,present) WHERE selected=1;";
    // SQLite's CREATE INDEX grammar qualifies the index name, not its table name; all targets are in scan_state.
    public const string PlanCreateClassifiedLocalRoots = "CREATE INDEX scan_state.classified_local_root ON classified(coalesce(saved_local_key,policy_key));CREATE INDEX scan_state.classified_dropbox_root ON classified(coalesce(saved_dropbox_delta,saved_local_key));";
    public const string PlanCreateClassifiedDropboxRoots = "CREATE INDEX scan_state.classified_local_root ON classified(saved_local_key);CREATE INDEX scan_state.classified_dropbox_root ON classified(coalesce(saved_dropbox_delta,saved_local_key,policy_key));";

    // A same-key re-record updates the one row holding both bindings in place; the NOT EXISTS arms are the clash
    // rules InsertSynced's follow-up applies. Zero rows changed means the caller takes the delete and insert path.
    public const string UpdateSyncedSameKeys = "UPDATE synced_entry SET is_folder = $isFolder, " +
        "local_id = $localId, local_size = $localSize, local_mtime = $localMtime,\r\n" +
        "  dropbox_id = $dropboxId, dropbox_hash = $dropboxHash,\r\n" +
        "  local_published_journal = $publishedJournal, local_published_usn = $publishedUsn\r\n" +
        "WHERE pair_id = " + PairRow + " AND " + KeyOf + "local_path) = $localKey AND local_path = $localPath\r\n" +
        "  AND coalesce(dropbox_key_delta, " + KeyOf + "local_path)) = $dropboxKey\r\n" +
        "  AND coalesce(dropbox_path_delta, local_path) = $dropboxPath AND is_folder = $isFolder\r\n" +
        "  AND NOT EXISTS (SELECT 1 FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = " + PairRow +
        " AND dropbox_key_delta = $dropboxKey AND " + KeyOf + "local_path) <> $localKey)\r\n" +
        "  AND NOT EXISTS (SELECT 1 FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = " + PairRow +
        " AND " + KeyOf + "local_path) = $dropboxKey AND dropbox_key_delta IS NULL AND " +
        KeyOf + "local_path) <> $localKey);";

    // Schema 5 statements. $pair is the integer row from PairFence, resolved once per operation. A mutation expected to
    // match one row must affect exactly 1 or its whole transaction rolls back; RetireCoveredWork alone may affect none.
    private const string WorkEvidence = "SELECT " + WorkColumns + " FROM change_work w WHERE w.pair_id=$pair AND w.pair_generation=$generation AND w.side=$side AND w.source_epoch=$epoch AND w.domain=$domain AND ";
    private const string ItemWorkFence = "EXISTS(SELECT 1 FROM pair p JOIN change_work w ON w.pair_id=p.id JOIN source_state s ON s.pair_id=w.pair_id AND s.side=w.side JOIN issue_member m ON m.work_id=w.work_id AND m.issue_id=issue.issue_id AND m.pair_id=p.id WHERE p.id=$pair AND p.execution_generation=$generation AND w.work_id=$work AND w.pair_generation=$generation AND w.source_epoch=$epoch AND s.source_epoch=$epoch AND w.dispatch_kind=$dispatch AND w.pending_operation_id IS $pendingOperation AND w.pending_generation IS $pendingGeneration";
    private const string ItemPendingFence = " AND (w.dispatch_kind<>'LocalRecovery' OR (p.pending_json=$pendingJson AND coalesce(json_extract(p.pending_json,'$.Version'),0)=1 AND json_extract(p.pending_json,'$.OperationId')=w.pending_operation_id AND json_extract(p.pending_json,'$.Generation')=w.pending_generation)))";
    private const string WorkSelectionFence = "w.work_origin='Acquired' AND w.pair_id=$pair AND w.pair_generation=$generation AND w.side=$side AND w.source_epoch=$epoch AND w.domain=$domain AND w.work_id>=$anchor AND w.work_id<=$through AND w.parent_anchor IS $parent AND w.obligation_key IS $obligation AND (w.before_json IS NULL OR w.before_json IS $before) AND";
    private const string IdentitySelection = WorkSelectionFence + " w.identity=$identity";
    private const string PathSelection = WorkSelectionFence + " w.identity IS NULL AND w.work_id=$anchor AND w.path_key=$path";
    private const string BothSourceFences = "EXISTS(SELECT 1 FROM source_state l WHERE l.pair_id=$pair AND l.side=0 AND l.source_epoch=$localEpoch AND l.repair_revision=$localRevision) AND EXISTS(SELECT 1 FROM source_state r WHERE r.pair_id=$pair AND r.side=1 AND r.source_epoch=$dropboxEpoch AND r.repair_revision=$dropboxRevision)";
    private const string PrerequisiteOutstanding = "(EXISTS(SELECT 1 FROM source_state s WHERE s.pair_id=$pair AND s.repair_reason IS NOT NULL) OR EXISTS(SELECT 1 FROM pair p WHERE p.id=$pair AND p.retry_after_ticks IS NOT NULL))";
    private const string FolderCustody = "(i.stage_allocation_json IS NOT NULL OR i.stage_json IS NOT NULL OR 0) OR EXISTS(SELECT 1 FROM folder_removal_submission s WHERE s.submission_id=i.submission_id AND s.phase IN ('Armed','Accepted'))";
    private const string FolderUndisposedEffect = "(f.retirement_disposition IS NULL AND (i.effect_state IN ('Armed','Accepted','Unknown') OR EXISTS(SELECT 1 FROM folder_removal_submission s WHERE s.submission_id=i.submission_id AND s.phase IN ('Armed','Accepted','Unknown'))))";
    private const string FolderUnresolved = "(f.active=1 OR (f.phase IN ('Unknown','Retained') AND f.retirement_disposition IS NULL))";
    private const string CurrentLocalWork = "EXISTS(SELECT 1 FROM change_work w INDEXED BY work_identity WHERE w.pair_id=$pair AND w.pair_generation=$generation AND w.side=0 AND w.work_origin='Acquired')";
    // One owner for the repeated projections and fences of the statements below; compile-time text only.
    private const string WorkColumns = "w.work_id,w.pair_id,w.pair_generation,w.source_epoch,w.side,w.domain,w.path_key,w.old_path_key,w.identity,w.notice_json,w.before_json,w.acquisition_work_id,w.parent_anchor,w.obligation_key,w.discovery_required";
    private const string FolderItemColumns = "pair_id,operation_id,ordinal,generation,provenance,path,policy_key,native_key,present,expected_json,saved_json,verdict,follow_up,follow_up_state,selected_branch,effect_kind,effect_state,effect_generation,submission_id,submission_member,effect_result_json,phase,selected_json,stage_allocation_json,stage_json,result_json,cause";
    private const string WorkFields = "work_id,pair_id,pair_generation,source_epoch,side,domain,path_key,old_path_key,identity,notice_json,before_json,acquisition_work_id,parent_anchor,obligation_key,discovery_required";
    private const string FolderColumns = "pair_id,operation_id,generation,phase,deleting_side,root_entry_json,source_evidence_json,saved_root_json,root_context_json,cursor,active,cause";
    public const string PairFence = "SELECT id,execution_generation,first_repair_done,def_json FROM pair WHERE pair_id=$p;";
    /// <summary>Read-only open of a schema-4 pair, which has no execution_generation column: a constant 0 holds that ordinal.</summary>
    public const string LegacyPairFence = "SELECT id,0,first_repair_done,def_json FROM pair WHERE pair_id=$p;";
    public const string SourceFence = "SELECT source_epoch,provenance_json,cursor,repair_reason,repair_revision FROM source_state WHERE pair_id=$pair AND side=$side;";
    public const string JournalRead = "SELECT journal_id,next_usn FROM journal_position WHERE volume_key=$volume;";
    public const string JournalInsert = "INSERT INTO journal_position(volume_key,journal_id,next_usn) VALUES($volume,$journal,$next);";
    public const string JournalAdvance = "UPDATE journal_position SET journal_id=$journal,next_usn=$next WHERE volume_key=$volume AND journal_id=$expectedJournal AND next_usn=$expectedNext;";
    private const string SeedSourceRows = "INSERT INTO source_state(pair_id,side,source_epoch,provenance_json,cursor,repair_reason,repair_revision) SELECT id,s.side,0,NULL,NULL,CASE WHEN first_repair_done=0 THEN $bootstrap ELSE $normal END,1 FROM pair CROSS JOIN (SELECT 0 AS side UNION ALL SELECT 1) s";
    public const string SeedSources = SeedSourceRows + ";";
    /// <summary>Seeds one pair's two sources in its save transaction; a pair that already has them keeps them.</summary>
    public const string SeedPairSources = SeedSourceRows + " WHERE pair_id=$p ON CONFLICT(pair_id,side) DO NOTHING;";
    public const string AdvanceSource = "UPDATE source_state SET source_epoch=$nextEpoch,provenance_json=$provenance,cursor=$cursor,repair_reason=CASE WHEN side=0 AND (repair_reason=$gapReason OR ($nextEpoch>source_epoch AND " + CurrentLocalWork + ")) THEN $gapReason ELSE $reason END,repair_revision=$nextRevision WHERE pair_id=$pair AND side=$side AND source_epoch=$epoch AND provenance_json IS $expectedProvenance AND cursor IS $expectedCursor AND repair_revision=$revision AND $nextEpoch>=source_epoch AND ($nextEpoch=source_epoch OR $nextRevision=repair_revision+1) AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND execution_generation=$generation);";
    public const string FinishObligation = "UPDATE source_state SET repair_reason=NULL WHERE pair_id=$pair AND side=$side AND source_epoch=$epoch AND repair_revision=$revision AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND execution_generation=$generation);";
    public const string AdvancePair = "UPDATE pair SET def_json=$definition,execution_generation=$nextGeneration WHERE id=$pair AND execution_generation=$generation AND (NOT " + CurrentLocalWork + " OR EXISTS(SELECT 1 FROM source_state WHERE pair_id=$pair AND side=0 AND repair_reason=$gapReason));";
    public const string WorkWatermark = "SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name='change_work'),0);";
    public const string InsertWork = "INSERT INTO change_work(" + WorkFields + ") VALUES($id,$pair,$generation,$epoch,$side,$domain,$path,$oldPath,$identity,$notice,$before,$acquisition,$parent,$obligation,0);";
    // The one eligibility rule and epoch lookup that selection and counting share, each declared once and
    // closed by its side value: pending Acquired rows of that side's current source. Selection reads the
    // source's epoch in the statement; counting binds the epochs it has already read.
    private const string EligibilityPrefix = "w.pair_id=$pair AND w.pair_generation=$generation AND w.side=";
    private const string EligibilitySuffix = " AND w.work_origin='Acquired' AND w.source_epoch=";
    private const string SourceEpochPrefix = "(SELECT source_epoch FROM source_state WHERE pair_id=$pair AND side=";
    private const string LocalEligibility = EligibilityPrefix + "0" + EligibilitySuffix;
    private const string RemoteEligibility = EligibilityPrefix + "1" + EligibilitySuffix;
    private const string LocalSourceEpoch = SourceEpochPrefix + "0)";
    private const string RemoteSourceEpoch = SourceEpochPrefix + "1)";

    public const string SelectWork = "WITH local_work AS MATERIALIZED (SELECT " + WorkColumns +
        " FROM change_work w INDEXED BY work_source_order WHERE " + LocalEligibility + LocalSourceEpoch +
        " AND w.work_id>$after AND w.work_id<=$through AND (w.parent_anchor IS NULL OR NOT EXISTS(SELECT 1 FROM change_work p WHERE p.work_id=w.parent_anchor)) ORDER BY w.work_id LIMIT $take),remote_work AS MATERIALIZED (SELECT " +
        WorkColumns + " FROM change_work w INDEXED BY work_source_order WHERE " + RemoteEligibility + RemoteSourceEpoch +
        " AND w.work_id>$after AND w.work_id<=$through AND (w.parent_anchor IS NULL OR NOT EXISTS(SELECT 1 FROM change_work p WHERE p.work_id=w.parent_anchor)) ORDER BY w.work_id LIMIT $take) SELECT " +
        WorkFields + " FROM (SELECT " + WorkFields + " FROM local_work UNION ALL SELECT " + WorkFields +
        " FROM remote_work) ORDER BY work_id LIMIT $take;";

    /// <summary>The queue snapshot: pending Acquired rows for both current sources at or below the pass watermark,
    /// one covering count per side. It reads only index columns, so the parent-anchor term, which only defers a
    /// selection, never changes it.</summary>
    public const string CountWork =
        "SELECT (SELECT count(*) FROM change_work w INDEXED BY work_source_order WHERE " +
        LocalEligibility + ParamLocalEpoch + " AND w.work_id<=$through)," +
        "(SELECT count(*) FROM change_work w INDEXED BY work_source_order WHERE " +
        RemoteEligibility + ParamRemoteEpoch + " AND w.work_id<=$through);";
    public const string Anchor = "SELECT " + WorkColumns + " FROM change_work w WHERE w.work_id=$anchor AND w.pair_id=$pair AND w.pair_generation=$generation AND w.source_epoch=$epoch;";
    // Without statistics the planner prefers UNIQUE(parent_anchor,obligation_key), which scans every top-level row in
    // range, so a top-level group seeks work_identity; a residual keeps that unique lookup's single exact entry.
    private const string TopLevelIdentityWork = " INDEXED BY work_identity";
    private const string ReadIdentityFrom = "SELECT " + WorkColumns + " FROM change_work w";
    private const string ReadIdentityWhere = " WHERE " + IdentitySelection + " AND w.work_id>$after ORDER BY w.work_id LIMIT $take;";
    public const string ReadIdentitySelection = ReadIdentityFrom + TopLevelIdentityWork + ReadIdentityWhere;
    public const string ReadResidualIdentitySelection = ReadIdentityFrom + ReadIdentityWhere;
    // B-203: work a live item episode still names stays with that episode (issue.item_retry_work_id, ON DELETE NO
    // ACTION); its own retry settles it. A selection covers it when the same identity is held at another path.
    // B-203: a row an issue still owns (a live episode names it, or it is an issue member) stays with that issue.
    private const string NoIssueOwned = " AND NOT EXISTS(SELECT 1 FROM issue i WHERE i.item_retry_work_id=w.work_id) AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.work_id=w.work_id)";
    private const string CompleteIdentityWhere = " WHERE " + IdentitySelection + NoRenameResponsibility + NoIssueOwned + ";";
    public const string CompleteIdentitySelection = "DELETE FROM change_work AS w" + TopLevelIdentityWork + CompleteIdentityWhere;
    public const string CompleteResidualIdentitySelection = "DELETE FROM change_work AS w" + CompleteIdentityWhere;
    private const string SaveBeforeIdentityWhere = " SET before_json=$nextBefore WHERE " + IdentitySelection + ";";
    public const string SaveBeforeIdentity = "UPDATE change_work AS w" + TopLevelIdentityWork + SaveBeforeIdentityWhere;
    public const string SaveBeforeResidualIdentity = "UPDATE change_work AS w" + SaveBeforeIdentityWhere;
    public const string ReadPathSelection = "SELECT " + WorkColumns + " FROM change_work w WHERE " + PathSelection + ";";
    private const string NoRenameResponsibility = " AND w.rename_continuation_json IS NULL AND NOT EXISTS(SELECT 1 FROM folder_removal f INDEXED BY folder_rename_work WHERE f.rename_work_id=w.work_id)";
    public const string CompletePathSelection = "DELETE FROM change_work AS w WHERE " + PathSelection + NoRenameResponsibility + NoIssueOwned + ";";
    public const string RequireIdentityDiscovery = "UPDATE change_work AS w SET discovery_required=1 WHERE " + IdentitySelection + " AND w.work_id=$work;";
    public const string RequirePathDiscovery = "UPDATE change_work AS w SET discovery_required=1 WHERE " + PathSelection + " AND w.work_id=$work;";
    public const string SaveBeforePath = "UPDATE change_work AS w SET before_json=$nextBefore WHERE " + PathSelection + ";";
    public const string PendingByIdentity = "SELECT " + WorkColumns + " FROM change_work w WHERE w.work_origin='Acquired' AND w.side=$side AND w.domain=$domain AND w.identity=$identity AND (w.pair_id,w.work_id)>($afterPair,$after) ORDER BY w.pair_id,w.work_id LIMIT $take;";
    public const string Registration = "SELECT " + WorkColumns + " FROM issue_member m INDEXED BY issue_member_retry_subject JOIN change_work w ON w.work_id=m.work_id WHERE m.pair_id=$pair AND m.side=$side AND m.path_key=$path AND m.identity IS $identity AND m.work_id IS NOT NULL AND m.issue_id=$issue AND w.work_origin='Acquired';";
    public const string EpisodeWork = "SELECT " + WorkColumns + " FROM issue i JOIN change_work w ON w.work_id=i.item_retry_work_id WHERE i.pair_id=$pair AND i.issue_id=$issue AND i.item_retry_episode IS NOT NULL;";
    public const string RegistrationByParent = "SELECT " + WorkColumns + " FROM change_work w WHERE w.parent_anchor=$parent AND w.obligation_key=$obligation;";
    public const string MoveRegistration = "UPDATE change_work SET pair_generation=$generation,source_epoch=$epoch,domain=$domain,parent_anchor=$parent,obligation_key=$obligation WHERE pair_id=$pair AND work_id=$work;";
    public const string DirtyIdentity = "SELECT w.work_id,w.acquisition_work_id,w.notice_json,w.before_json FROM change_work w WHERE w.pair_id=$pair AND w.pair_generation=$generation AND w.side=0 AND w.source_epoch=$epoch AND w.domain=$domain AND w.identity=$identity AND w.work_id>$after AND w.work_origin='Acquired' ORDER BY w.work_id LIMIT $take;";
    public const string LocalDirtyEvidence = "SELECT w.notice_json,w.acquisition_work_id FROM change_work w INDEXED BY work_source_order WHERE w.pair_id=$pair AND w.pair_generation=$generation AND w.side=0 AND w.source_epoch=$epoch AND w.domain=$domain AND w.work_origin='Acquired' ORDER BY w.work_id;";
    public const string EvidenceIdentity = WorkEvidence + "w.identity=$identity AND w.work_origin='Acquired' ORDER BY w.work_id;";
    public const string EvidencePathExact = WorkEvidence + "w.path_key=$key AND w.acquisition_work_id>$watermark AND w.work_origin='Acquired' ORDER BY w.path_key,w.work_id;";
    internal const string ResponsibilityOverlapsFunction = "responsibilityoverlaps";
    private const string EchoOverlap = ResponsibilityOverlapsFunction + "($localKey,$dropboxKey,$isFolder,";
    internal const string LocalEchoResponsibilities =
        "SELECT path,subject_json,NULL FROM issue WHERE pair_id=$pair AND (dismissed=0 OR item_retry_episode IS NOT NULL) " +
        "UNION ALL SELECT json_extract(f.root_entry_json,'$.Value.Path'),NULL,f.saved_root_json " +
        "FROM folder_removal f WHERE f.pair_id=$pair AND " + FolderUnresolved + ";";
    internal const string LocalEchoPending = "SELECT EXISTS(SELECT 1 FROM pair WHERE id=$pair AND pending_json IS NOT NULL AND (" +
        "json_extract(pending_json,'$.Version') IS NOT 1 OR " + EchoOverlap + "json_extract(pending_json,'$.Path')) OR " +
        EchoOverlap + "json_extract(pending_json,'$.Saved.LocalPath')) OR " + EchoOverlap + "json_extract(pending_json,'$.Saved.DropboxPath'))));";
    private const string PeerOwnership = "w.path_key=$key AND w.work_id<=$through AND w.work_origin='Acquired' AND w.old_path_key IS NULL AND (w.parent_anchor IS NULL OR NOT EXISTS(SELECT 1 FROM change_work parent WHERE parent.pair_id=w.pair_id AND parent.work_id=w.parent_anchor)) AND (w.before_json IS NULL OR w.before_json=$before) AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.work_id=w.work_id) AND NOT EXISTS(SELECT 1 FROM change_work child WHERE child.parent_anchor=w.work_id)";
    public const string PeerPathPage = WorkEvidence + "w.path_key=$key AND w.work_id>$after AND w.work_id<=$through AND w.work_origin='Acquired' ORDER BY w.path_key,w.work_id LIMIT $take;";
    public const string OwnedPeerPathPage = WorkEvidence + PeerOwnership + " AND w.work_id>$after ORDER BY w.path_key,w.work_id LIMIT $take;";
    private const string ClaimedWork = "SELECT w.work_id FROM change_work w INDEXED BY work_identity WHERE " + IdentitySelection + " UNION ALL SELECT w.work_id FROM change_work w WHERE " + PathSelection + " UNION ALL SELECT w.work_id FROM change_work w WHERE w.parent_anchor=$anchor";
    internal const int EchoSubject = 1;
    internal const int EchoSaved = 2;
    // Persisted deleted-name blocks accept up to this many keys (StateEncoding); lowering it rejects existing history.
    // The same bound batches copy/reclaim. Its original throughput/lock-hold measurement has not been established.
    internal const int DeletedHistoryBatchSize = 5000;
    internal const int DeletedKeyUtf8BytesPerUnit = 3;
    internal const int DeletedZlibMinimumBytes = 6;
    internal const int DeletedChecksumBytes = 4;
    internal const uint DeletedChecksumInitial = 1;
    internal const uint DeletedChecksumMask = 65535;
    internal const int DeletedChecksumShift = 16;
    internal const uint DeletedChecksumModulus = 65521;
    internal const int DeletedLengthMask = 127;
    internal const int DeletedLengthShift = 7;
    internal const int DeletedLengthContinuation = 128;
    internal const int DeletedStreamEnd = -1;
    internal const int LegacyDeletedKeyColumn = 1;
    private const string ClaimWorkColumn = "w.work_id";
    private const string ClaimWorkFrom = "FROM change_work w";
    private const string ClaimPeerFrom = "FROM change_work w INDEXED BY work_path";
    private const string ParameterPrefix = "$";
    private const string PeerParameterPrefix = "$peer_";
    private const string ClaimParameterPrefix = "$claim";
    private const string ParameterSeparator = "_";
    private const string ExcludeClaimedWork = " AND w.work_id NOT IN (";
    private const string UnionAll = " UNION ALL ";
    private const string StatementClose = ");";
    private static readonly string ClaimedPeers = (WorkEvidence.Replace(WorkColumns, ClaimWorkColumn, StringComparison.Ordinal)
        .Replace(ClaimWorkFrom, ClaimPeerFrom, StringComparison.Ordinal) + PeerOwnership)
        .Replace(ParameterPrefix, PeerParameterPrefix, StringComparison.Ordinal);

    internal static string ClaimParameter(int index, string parameter) =>
        ClaimParameterPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ParameterSeparator + parameter[1..];

    internal static string PeerParameter(string parameter) => PeerParameterPrefix + parameter[1..];

    internal static string RetireUnclaimedWork(int claims) => claims == 0 ? RetireCoveredWork :
        RetireCoveredWork.TrimEnd(';') + ExcludeClaimedWork +
        string.Join(UnionAll, Enumerable.Range(0, claims).Select(index =>
            (ClaimedWork + UnionAll + ClaimedPeers).Replace(ParameterPrefix, ClaimParameter(index, ParameterPrefix), StringComparison.Ordinal))) + StatementClose;
    public const string EvidencePathBelow = WorkEvidence + "w.path_key>=$low AND w.path_key<$high AND w.acquisition_work_id>$watermark AND w.work_origin='Acquired' ORDER BY w.path_key,w.work_id;";
    public const string EvidenceOldPathExact = WorkEvidence + "w.old_path_key IS NOT NULL AND w.old_path_key=$key AND w.acquisition_work_id>$watermark AND w.work_origin='Acquired' ORDER BY w.old_path_key,w.work_id;";
    public const string EvidenceOldPathBelow = WorkEvidence + "w.old_path_key IS NOT NULL AND w.old_path_key>=$low AND w.old_path_key<$high AND w.acquisition_work_id>$watermark AND w.work_origin='Acquired' ORDER BY w.old_path_key,w.work_id;";
    public const string ReadIssueMembers = "SELECT member_id,pair_id,work_id,side,path_key,identity,subject_json,skip_cause FROM issue_member WHERE issue_id=$issue ORDER BY side,path_key,member_id;";
    public const string WorkIssue = "SELECT member_id,issue_id,subject_json FROM issue_member WHERE work_id=$work;";
    public const string TransferableWork = "SELECT EXISTS(SELECT 1 FROM change_work w WHERE w.pair_id=$pair AND w.work_id=$work AND w.work_origin='Acquired' AND w.dispatch_kind='OrdinaryItem' AND w.pending_operation_id IS NULL AND w.pending_generation IS NULL" + NoRenameResponsibility + NoIssueOwned + " AND NOT EXISTS(SELECT 1 FROM change_work c WHERE c.parent_anchor=w.work_id));";
    public const string MergeTransferredWork = "UPDATE change_work SET notice_json=$notice,acquisition_work_id=max(acquisition_work_id,$acquisition),before_json=coalesce($before,before_json) WHERE pair_id=$pair AND work_id=$work;";
    public const string UpdateRegisteredNotice = "UPDATE change_work SET notice_json=$notice WHERE pair_id=$pair AND work_id=$work AND parent_anchor IS $parent;";
    // Acquired work not held by an issue: what an item-only wait still waits for after a Repair retires its covered work.
    public const string PairHasPendingWork = "SELECT EXISTS(SELECT 1 FROM change_work w WHERE w.pair_id=$pair AND w.work_origin='Acquired' AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.work_id=w.work_id));";
    public const string WorkHasChildren = "SELECT EXISTS(SELECT 1 FROM change_work WHERE parent_anchor=$parent);";
    public const string ReleaseCompletedRenameRemoval = "UPDATE folder_removal SET rename_work_id=NULL WHERE pair_id=$pair AND rename_work_id=$work AND phase='Complete' AND active=0;";
    public const string LinkRenameRemoval = "UPDATE folder_removal SET rename_work_id=$work WHERE pair_id=$pair AND operation_id=$operation AND rename_work_id IS NULL;";
    public const string ReadRenameRemoval = "SELECT operation_id FROM folder_removal INDEXED BY folder_rename_work WHERE pair_id=$pair AND rename_work_id=$work;";
    public const string ReadRenameRemovalWork = "SELECT rename_work_id FROM folder_removal WHERE pair_id=$pair AND operation_id=$operation;";
    public const string ReplaceRenameContinuation = "UPDATE change_work SET rename_continuation_json=$notice WHERE pair_id=$pair AND work_id=$work AND work_origin='Acquired' AND rename_continuation_json IS $expected;";
    public const string ClearRenameContinuation = "UPDATE change_work SET rename_continuation_json=NULL WHERE pair_id=$pair AND work_id=$work AND rename_continuation_json=$notice;";
    public const string RenameClashCurrent = "SELECT i.issue_id,i.kind,i.cause,i.path,i.subject_json,i.at_ticks,i.dismissed,i.ignored FROM issue i JOIN issue_member m ON m.issue_id=i.issue_id WHERE m.work_id=$work AND i.pair_id=$pair;";
    public const string FindRenameWork = "SELECT " + WorkColumns + " FROM change_work w WHERE w.pair_id=$pair AND w.pair_generation=$generation AND w.side=$side AND w.source_epoch=$epoch AND w.domain=$domain AND w.identity=$identity AND w.path_key=$path AND w.work_origin='Acquired' AND w.parent_anchor IS NULL ORDER BY w.work_id LIMIT 1;";
    public const string ReadRenameContinuation = "SELECT rename_continuation_json FROM change_work WHERE pair_id=$pair AND work_id=$work;";
    public const string SaveRenameContinuation = "UPDATE change_work SET rename_continuation_json=$notice WHERE pair_id=$pair AND work_id=$work AND work_origin='Acquired' AND rename_continuation_json IS NULL;";
    public const string DeleteWork = "DELETE FROM change_work AS w WHERE pair_id=$pair AND work_id=$work" + NoRenameResponsibility + ";";
    public const string ValidateIssue = "SELECT issue_id,kind,cause,path,subject_json,dismissed FROM issue WHERE pair_id=$pair AND issue_id=$issue;";
    public const string InsertIssueMember = "INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json,skip_cause) VALUES($issue,$pair,$work,$side,$path,$identity,$subject,$skip);";
    public const string ReassociateWork = "UPDATE issue_member SET issue_id=$nextIssue,subject_json=$subject WHERE work_id=$work AND issue_id IS $expectedIssue;";
    public const string DetachIssueMembers = "DELETE FROM issue_member WHERE issue_id=$issue;";
    public const string DetachPairMembers = "DELETE FROM issue_member WHERE pair_id=$pair;";
    public const string SkipMember = "SELECT member_id,issue_id,identity,subject_json,skip_cause FROM issue_member WHERE pair_id=$pair AND side=$side AND path_key=$path AND work_id IS NULL;";
    // Both read issue_member_skip; skip_cause is not in it, so a range visits every member inside it.
    public const string UnreadableAtOrAbove = "SELECT 1 FROM issue_member INDEXED BY issue_member_skip WHERE pair_id=" + PairRow + " AND side=$side AND work_id IS NULL AND path_key IN (SELECT value FROM json_each($keys)) AND skip_cause=$cause LIMIT 1;";
    public const string UnreadableBelow = "SELECT path_key FROM issue_member INDEXED BY issue_member_skip WHERE pair_id=" + PairRow + " AND side=$side AND work_id IS NULL AND path_key>$low AND path_key<$high AND skip_cause=$cause;";
    public const string MemberlessSkips = "SELECT i.issue_id,i.pair_id,i.path,i.cause,i.subject_json FROM issue i WHERE i.kind=$kind AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=i.issue_id);";
    public const string RemoveSkipMember = "DELETE FROM issue_member WHERE pair_id=$pair AND side=$side AND path_key=$path AND work_id IS NULL;";
    public const string TouchSkip = "INSERT INTO scan_state.skip_touched(pair_id,scan_id,side,path_key) VALUES($pair,$scan,$side,$path) ON CONFLICT DO NOTHING;";
    public const string SkipTouched = "SELECT EXISTS(SELECT 1 FROM scan_state.skip_touched WHERE pair_id=$pair AND scan_id=$scan AND side=$side AND path_key=$path);";
    public const string DeletedNameInsert = "INSERT INTO scan_state.deleted_name(pair_id,path_key) SELECT $pair,$path WHERE " + NotSpentDeletedKey + " ON CONFLICT DO NOTHING;";
    /// <summary>DeletedNameInsert for one bounded JSON array of well-formed keys in one statement.</summary>
    public const string DeletedNamesInsert = "INSERT INTO scan_state.deleted_name(pair_id,path_key) SELECT $pair,k.value FROM json_each($keys) k WHERE " + NotSpentDeletedKeyAt + "k.value) ON CONFLICT DO NOTHING;";
    public const string HasDeletedNames = "SELECT EXISTS(SELECT 1 FROM scan_state.deleted_name n WHERE pair_id=$pair AND NOT EXISTS(SELECT 1 FROM main.discovery_deleted_delta d WHERE d.pair_id=$pair AND d.path_key=n.path_key AND d.spent=1) UNION ALL SELECT 1 FROM main.discovery_deleted_delta WHERE pair_id=$pair AND spent=0);";
    public const string DeletedNameExact = "SELECT " + NotSpentDeletedKey + " AND EXISTS(SELECT 1 FROM scan_state.deleted_name WHERE pair_id=$pair AND path_key=$path UNION ALL SELECT 1 FROM main.discovery_deleted_delta WHERE pair_id=$pair AND path_key=$path AND spent=0);";
    public const string PendingRead = "SELECT pending_json FROM pair WHERE id=$pair;";
    public const string PendingArm = "UPDATE pair SET pending_json=$next WHERE id=$pair AND pending_json IS NULL;";
    public const string PendingAdvance = "UPDATE pair SET pending_json=$next WHERE id=$pair AND pending_json=$expected;";
    public const string PendingSettle = "UPDATE pair SET pending_json=NULL WHERE id=$pair AND pending_json=$expected;";
    public const string FolderRead = "SELECT " + FolderColumns + " FROM folder_removal WHERE pair_id=$pair AND operation_id=$operation;";
    public const string ActiveFolders = "SELECT " + FolderColumns + " FROM folder_removal WHERE pair_id=$pair AND active=1 AND operation_id>$afterOperation ORDER BY operation_id LIMIT $take;";
    /// <summary>First active operation at the exact root path; its frozen root payload reads with FolderRead's ordinals.</summary>
    public const string ActiveFolderByRoot = "SELECT " + FolderColumns + " FROM folder_removal INDEXED BY folder_removal_root WHERE pair_id=$pair AND active=1 AND " + FolderRootPath + "=$path ORDER BY operation_id LIMIT $take;";
    public const string FolderItems = "SELECT " + FolderItemColumns + " FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND ordinal>$after ORDER BY ordinal LIMIT $take;";
    public const string FolderItemsInScope = "SELECT " + FolderItemColumns + " FROM folder_removal_item INDEXED BY folder_item_identity WHERE pair_id=$pair AND operation_id=$operation AND (native_key=$k OR (native_key>=$low AND native_key<$high)) AND ordinal>$after ORDER BY ordinal LIMIT $take;";
    /// <summary>The lowest-ordinal member at one exact key. FolderItemsInScope's OR with a range cannot seek the key, so
    /// with an empty range it walks the whole operation; this form seeks (pair, operation, native key).</summary>
    public const string FolderItemByKey = "SELECT " + FolderItemColumns + " FROM folder_removal_item INDEXED BY folder_item_identity WHERE pair_id=$pair AND operation_id=$operation AND native_key=$k AND ordinal>$after ORDER BY ordinal LIMIT $take;";
    internal const string RecoveryMember = "SELECT " + FolderItemColumns + " FROM folder_removal_item INDEXED BY folder_item_identity WHERE pair_id=$pair AND operation_id=$operation AND native_key=$k AND provenance=$provenance;";
    internal const string SelectedFolderBranch = "SELECT " + FolderItemColumns + " FROM folder_removal_item INDEXED BY folder_item_identity WHERE pair_id=$pair AND operation_id=$operation AND native_key=$k AND provenance='Planned' AND selected_branch=1;";
    // Unary + keeps the fixed kind predicate from hiding the index's rowid ordering from SQLite.
    public const string HeldFolderMembers = "SELECT i.issue_id,i.kind,i.cause,i.path,i.subject_json,i.at_ticks,i.dismissed,i.ignored,m.saved_json FROM issue i INDEXED BY issue_path_key CROSS JOIN folder_removal_item m INDEXED BY folder_item_identity WHERE i.pair_id=(SELECT id FROM pair WHERE pair_id = $p) AND +i.kind=$kind AND pathkey(i.path)>=$low AND pathkey(i.path)<$high AND pathkey(i.path)>$low AND m.pair_id=i.pair_id AND m.operation_id=$operation AND m.native_key=pathkey(i.path) AND m.provenance='Planned' AND m.present=1 ORDER BY pathkey(i.path),i.kind,i.issue_id LIMIT $take;";
    internal const string LastFolderOrdinal = "SELECT ordinal FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation ORDER BY ordinal DESC LIMIT 1;";
    public const string FolderItem = "SELECT " + FolderItemColumns + " FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$ordinal;";
    public const string PendingFollowUps = "SELECT " + FolderItemColumns + " FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND follow_up_state='Pending' AND ordinal>$after ORDER BY ordinal LIMIT $take;";
    public const string CompleteFollowUp = "UPDATE folder_removal_item SET follow_up_state='Done',generation=$nextGeneration WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$ordinal AND generation=$generation AND follow_up_state='Pending';";
    public const string ReadyBoundary = "SELECT -plandepth(policy_key),policy_key,native_key,present,ordinal FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$after;";
    public const string ReadyFirst = "SELECT operation_id,ordinal,effect_generation,effect_kind,expected_json,(SELECT deleting_side FROM folder_removal WHERE pair_id=$pair AND operation_id=$operation) FROM folder_removal_item INDEXED BY folder_item_ready_order WHERE pair_id=$pair AND operation_id=$operation AND effect_state='Ready' AND phase<>'Settled' ORDER BY -plandepth(policy_key),policy_key,native_key,present,ordinal LIMIT $take;";
    public const string ReadyNext = "WITH same_depth AS MATERIALIZED (SELECT operation_id,ordinal,effect_generation,effect_kind,expected_json FROM folder_removal_item INDEXED BY folder_item_ready_order WHERE pair_id=$pair AND operation_id=$operation AND effect_state='Ready' AND phase<>'Settled' AND -plandepth(policy_key)=$negativeDepth AND (policy_key,native_key,present,ordinal)>($policy,$native,$present,$after) ORDER BY policy_key,native_key,present,ordinal LIMIT $take) SELECT operation_id,ordinal,effect_generation,effect_kind,expected_json,(SELECT deleting_side FROM folder_removal WHERE pair_id=$pair AND operation_id=$operation) FROM same_depth UNION ALL SELECT operation_id,ordinal,effect_generation,effect_kind,expected_json,(SELECT deleting_side FROM folder_removal WHERE pair_id=$pair AND operation_id=$operation) FROM (SELECT operation_id,ordinal,effect_generation,effect_kind,expected_json FROM folder_removal_item INDEXED BY folder_item_ready_order WHERE pair_id=$pair AND operation_id=$operation AND effect_state='Ready' AND phase<>'Settled' AND -plandepth(policy_key)>$negativeDepth ORDER BY -plandepth(policy_key),policy_key,native_key,present,ordinal LIMIT $take-(SELECT count(*) FROM same_depth));";
    /// <summary>All active operations at the exact notice spelling contribute, regardless of member effect phase.</summary>
    public const string HasInvalidFolderJob = "SELECT EXISTS(SELECT 1 FROM folder_removal AS f INDEXED BY folder_removal_root " +
        "CROSS JOIN folder_removal_item AS i INDEXED BY folder_item_effect CROSS JOIN folder_removal_submission AS s " +
        "WHERE f.pair_id=$pair AND f.active=1 AND " + FolderRootPath + "=$path " +
        "AND i.pair_id=f.pair_id AND i.operation_id=f.operation_id AND i.submission_id IS NOT NULL " +
        "AND s.submission_id=i.submission_id AND s.pair_id=$pair AND s.phase='Accepted' AND s.cause=$cause);";
    public const string FolderSubmissionRoots = "SELECT DISTINCT f.pair_id," + FolderRootPath +
        " FROM folder_removal_item AS i INDEXED BY folder_submission_member CROSS JOIN folder_removal AS f " +
        "WHERE i.submission_id=$submission AND i.submission_id IS NOT NULL " +
        "AND f.pair_id=i.pair_id AND f.operation_id=i.operation_id AND f.active=1;";
    /// <summary>One short keyset page; the pair column is the external ID expected by the submission decoder.</summary>
    public const string PendingFolderSubmissions = "SELECT s.submission_id,p.pair_id,s.generation,s.phase,s.job_id,s.cause " +
        "FROM folder_removal_submission AS s INDEXED BY folder_submission_pending CROSS JOIN pair AS p " +
        "WHERE s.pair_id=$pair AND s.phase='Accepted' AND s.submission_id>$after AND p.id=s.pair_id " +
        "AND ($progressOnly=0 OR s.cause IS NULL) " +
        "ORDER BY s.submission_id LIMIT $take;";
    public const string HasFolderJobProgress = "SELECT EXISTS(SELECT 1 FROM folder_removal_submission AS s " +
        "INDEXED BY folder_submission_pending WHERE s.pair_id=(SELECT id FROM pair WHERE pair_id=$p) " +
        "AND s.phase='Accepted' AND s.cause IS NULL);";
    public const string SubmissionRead = "SELECT submission_id,pair_id,generation,phase,job_id,cause FROM folder_removal_submission WHERE submission_id=$submission;";
    public const string SubmissionEffects = "SELECT operation_id,ordinal,effect_generation,effect_kind,expected_json,submission_member,(SELECT f.deleting_side FROM folder_removal f WHERE f.pair_id=folder_removal_item.pair_id AND f.operation_id=folder_removal_item.operation_id) FROM folder_removal_item WHERE submission_id=$submission ORDER BY submission_member;";
    public const string OperationSubmissions = "SELECT DISTINCT submission_id FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND submission_id IS NOT NULL;";
    public const string InsertSubmission = "INSERT INTO folder_removal_submission(submission_id,pair_id,generation,phase,job_id,cause) VALUES($submission,$pair,0,'Armed',NULL,NULL);";
    public const string ArmEffect = "UPDATE folder_removal_item SET submission_id=$submission,submission_member=$member,effect_state='Armed',effect_generation=$nextEffectGeneration,phase='DeleteArmed',generation=$nextGeneration WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$ordinal AND generation=$generation AND effect_generation=$effectGeneration AND effect_state='Ready' AND phase<>'Settled' AND submission_id IS NULL AND effect_kind IN ('RecursiveFolder','ConditionalFile') AND EXISTS(SELECT 1 FROM folder_removal f WHERE f.pair_id=$pair AND f.operation_id=$operation AND f.active=1) AND EXISTS(SELECT 1 FROM folder_removal_submission s WHERE s.submission_id=$submission AND s.pair_id=$pair AND s.phase='Armed');";
    public const string AdvanceSubmission = "UPDATE folder_removal_submission SET generation=$nextGeneration,phase=$nextPhase,job_id=$job,cause=$cause WHERE submission_id=$submission AND pair_id=$pair AND generation=$generation AND phase=$phase;";
    public const string AdvanceEffect = "UPDATE folder_removal_item SET effect_state=$nextState,effect_result_json=$receipt,effect_generation=$nextEffectGeneration,generation=$nextGeneration,phase=$nextPhase,cause=$cause WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$ordinal AND generation=$generation AND effect_generation=$effectGeneration AND effect_state=$state AND phase<>'Settled' AND submission_id=$submission AND submission_member=$member;";
    public const string AdvanceFolder = "UPDATE folder_removal SET generation=$nextGeneration,phase=$nextPhase,cursor=$cursor,cause=$cause WHERE pair_id=$pair AND operation_id=$operation AND generation=$generation AND phase=$phase AND active=1;";
    public const string AdvanceRecovery = "UPDATE folder_removal_item SET generation=$nextGeneration,phase=$nextPhase,selected_json=$selected,stage_allocation_json=$allocation,stage_json=$stage,result_json=$result,cause=$cause WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$ordinal AND generation=$generation AND phase=$phase;";
    public const string SettleEffect = "UPDATE folder_removal_item SET generation=$nextGeneration,effect_generation=$nextEffectGeneration,effect_state='Settled',phase='Settled',cause=$cause WHERE pair_id=$pair AND operation_id=$operation AND ordinal=$ordinal AND generation=$generation AND effect_generation=$effectGeneration AND effect_state=$state AND stage_allocation_json IS NULL AND stage_json IS NULL;";
    public const string TerminalFolder = "UPDATE folder_removal SET generation=$nextGeneration,phase=$nextPhase,active=0,cause=$cause WHERE pair_id=$pair AND operation_id=$operation AND generation=$generation AND active=1;";
    public const string FolderHasUnsettled = "SELECT EXISTS(SELECT 1 FROM folder_removal_item WHERE pair_id=$pair AND operation_id=$operation AND ((effect_kind<>'None' AND effect_state<>'Settled') OR follow_up_state='Pending' OR stage_allocation_json IS NOT NULL OR stage_json IS NOT NULL));";
    public const string InsertFolder = "INSERT INTO folder_removal(" + FolderColumns + ") VALUES($pair_id,$operation_id,$generation,$phase,$deleting_side,$root_entry_json,$source_evidence_json,$saved_root_json,$root_context_json,$cursor,$active,$cause);";
    public const string InsertFolderItem = "INSERT INTO folder_removal_item(pair_id,operation_id,ordinal,generation,provenance,path,policy_key,expected_json,saved_json,verdict,follow_up,follow_up_state,selected_branch,effect_kind,effect_state,effect_generation,submission_id,submission_member,effect_result_json,phase,selected_json,stage_allocation_json,stage_json,result_json,cause) VALUES($pair_id,$operation_id,$ordinal,$generation,$provenance,$path,$policy_key,$expected_json,$saved_json,$verdict,$follow_up,$follow_up_state,$selected_branch,$effect_kind,$effect_state,$effect_generation,$submission_id,$submission_member,$effect_result_json,$phase,$selected_json,$stage_allocation_json,$stage_json,$result_json,$cause);";
    public const string FrozenEvidence = "SELECT id," + PlanPayload + ",verdict,follow_up,selected FROM scan_state.classified WHERE (policy_key,native_key,present)>($policy,$native,$present) ORDER BY policy_key,native_key,present LIMIT $take;";
    public const string ClassifiedSelect = "UPDATE scan_state.classified SET selected=1 WHERE id=$id;";
    public const string ClassifiedHasSurvivor = "SELECT EXISTS(SELECT 1 FROM scan_state.classified INDEXED BY classified_survivor WHERE policy_key=$key AND verdict='Survives' UNION ALL SELECT 1 FROM scan_state.classified INDEXED BY classified_survivor WHERE policy_key>=$low AND policy_key<$high AND verdict='Survives');";
    // Frozen branch result: 21 payload columns, then id, then depth.
    public const string ClassifiedRemoved = "SELECT " + PlanPayload + ",id,plandepth(policy_key) AS depth FROM scan_state.classified INDEXED BY classified_order WHERE -plandepth(policy_key)=$negativeDepth AND (policy_key,native_key,present)>($policy,$native,$present) AND verdict='Removed' ORDER BY policy_key,native_key,present LIMIT $take;";
    public const string ClassifiedHasSelected = "SELECT EXISTS(SELECT 1 FROM scan_state.classified WHERE policy_key=$key AND selected=1);";
    public const string ClassifiedSurvivors = "SELECT " + PlanPayload + " FROM scan_state.classified INDEXED BY classified_survivor WHERE verdict='Survives' AND follow_up=1 AND (policy_key,native_key,present)>($policy,$native,$present) ORDER BY policy_key,native_key,present LIMIT $take;";
    public const string ClassifiedNextDepth = "SELECT -plandepth(policy_key) FROM scan_state.classified INDEXED BY classified_order WHERE -plandepth(policy_key)<$negativeDepth ORDER BY -plandepth(policy_key) DESC LIMIT 1;";
    public const string ClassifiedSelectedFirst = "SELECT " + PlanPayload + ",id,plandepth(policy_key) AS depth FROM scan_state.classified INDEXED BY classified_selected_order WHERE selected=1 ORDER BY -plandepth(policy_key),policy_key,native_key,present LIMIT $take;";
    public const string ClassifiedSelected = "WITH same_depth AS MATERIALIZED (SELECT " + PlanPayload + ",id,plandepth(policy_key) AS depth FROM scan_state.classified INDEXED BY classified_selected_order WHERE selected=1 AND -plandepth(policy_key)=$negativeDepth AND (policy_key,native_key,present)>($policy,$native,$present) ORDER BY policy_key,native_key,present LIMIT $take) SELECT " + PlanPayload + ",id,depth FROM same_depth UNION ALL SELECT " + PlanPayload + ",id,depth FROM (SELECT " + PlanPayload + ",id,plandepth(policy_key) AS depth FROM scan_state.classified INDEXED BY classified_selected_order WHERE selected=1 AND -plandepth(policy_key)>$negativeDepth ORDER BY -plandepth(policy_key),policy_key,native_key,present LIMIT $take-(SELECT count(*) FROM same_depth));";
    public const string ReadRetry = "SELECT execution_generation,retry_attempts,retry_after_ticks FROM pair WHERE id=$pair;";
    public const string EarliestRetry = "SELECT id,pair_id,execution_generation,def_json,retry_attempts,retry_after_ticks FROM pair INDEXED BY pair_retry_due WHERE retry_after_ticks IS NOT NULL AND (retry_after_ticks,id)>($afterTicks,$afterPair) ORDER BY retry_after_ticks,id LIMIT $take;";
    public const string RecordPrerequisiteFailure = "UPDATE pair SET retry_attempts=$attempts,retry_after_ticks=$retryAfter WHERE id=$pair AND execution_generation=$generation AND " + BothSourceFences + " AND retry_attempts IS $expectedAttempts AND retry_after_ticks IS $expectedRetryAfter;";
    public const string CompleteRecomposition = "UPDATE pair SET retry_attempts=NULL,retry_after_ticks=NULL WHERE id=$pair AND execution_generation=$generation AND " + BothSourceFences + " AND retry_attempts IS $expectedAttempts AND retry_after_ticks IS $expectedRetryAfter AND NOT EXISTS(SELECT 1 FROM source_state s WHERE s.pair_id=$pair AND s.repair_reason IS NOT NULL);";
    public const string CompletePrerequisitePair = "UPDATE pair SET retry_attempts=CASE WHEN $keepRetry THEN retry_attempts END,retry_after_ticks=CASE WHEN $keepRetry THEN retry_after_ticks END,last_repair_json=$summary,first_repair_done=CASE WHEN $completed THEN 1 ELSE first_repair_done END WHERE id=$pair AND execution_generation=$generation AND " + BothSourceFences + " AND retry_attempts IS $expectedAttempts AND retry_after_ticks IS $expectedRetryAfter;";
    public const string CloseRetryNotice = "DELETE FROM issue WHERE pair_id=$pair AND issue_id=$issue AND path='' AND kind=$kind AND cause=$retryCause AND NOT " + PrerequisiteOutstanding + ";";
    public const string ClosePageWaitNotice = "DELETE FROM issue WHERE pair_id=$pair AND issue_id=$issue AND path='' " +
        "AND kind=$kind AND cause=$retryCause AND EXISTS(SELECT 1 FROM pair WHERE id=$pair " +
        "AND execution_generation=$generation AND ($pageOnly OR (retry_attempts IS NULL AND retry_after_ticks IS NULL)));";
    public const string TransferLocalEvidenceGap = "UPDATE source_state SET repair_reason=$gapReason WHERE pair_id=$pair AND side=0 AND source_epoch=$epoch AND repair_revision=$revision AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND execution_generation=$generation) AND " + CurrentLocalWork + ";";
    internal const string LocalEvidenceTouchedFunction = "localevidencetouched";
    internal const string LocalPositionKind = "position";
    internal const string CreateLocalEvidenceTriggers = """
        CREATE TEMP TRIGGER local_evidence_insert AFTER INSERT ON main.change_work
        WHEN NEW.side=0 AND NEW.work_origin='Acquired' BEGIN SELECT localevidencetouched('evidence'); END;
        CREATE TEMP TRIGGER local_evidence_delete AFTER DELETE ON main.change_work
        WHEN OLD.side=0 AND OLD.work_origin='Acquired' BEGIN SELECT localevidencetouched('evidence'); END;
        CREATE TEMP TRIGGER local_evidence_update AFTER UPDATE OF pair_generation,source_epoch,side,work_origin ON main.change_work
        BEGIN SELECT localevidencetouched('evidence'); END;
        CREATE TEMP TRIGGER local_evidence_generation AFTER UPDATE OF execution_generation ON main.pair
        BEGIN SELECT localevidencetouched('evidence'); END;
        CREATE TEMP TRIGGER local_evidence_pair_delete AFTER DELETE ON main.pair
        BEGIN SELECT localevidencetouched('evidence'); END;
        CREATE TEMP TRIGGER local_evidence_epoch AFTER UPDATE OF source_epoch ON main.source_state
        BEGIN SELECT localevidencetouched('evidence'); END;
        CREATE TEMP TRIGGER local_position_insert AFTER INSERT ON main.journal_position
        BEGIN SELECT localevidencetouched('position'); END;
        CREATE TEMP TRIGGER local_position_update AFTER UPDATE ON main.journal_position
        BEGIN SELECT localevidencetouched('position'); END;
        CREATE TEMP TRIGGER local_position_delete AFTER DELETE ON main.journal_position
        BEGIN SELECT localevidencetouched('position'); END;
        """;
    public const string AnyLocalEvidence = "SELECT EXISTS(SELECT 1 FROM change_work INDEXED BY work_source_order WHERE pair_id=$pair AND pair_generation=$generation AND side=0 AND source_epoch=$epoch AND work_origin='Acquired');";
    public const string HasCurrentLocalWork = "SELECT EXISTS(SELECT 1 FROM change_work INDEXED BY work_identity WHERE pair_id=$pair AND pair_generation=$generation AND side=0 AND work_origin='Acquired');";
    public const string RetryNoticeProtected = "SELECT EXISTS(SELECT 1 FROM issue i WHERE i.pair_id=$pair AND i.issue_id=$issue AND i.path='' AND i.kind=$kind AND i.cause=$retryCause AND " + PrerequisiteOutstanding + ");";
    public const string RetireCoveredWork = "WITH retained_dirty AS MATERIALIZED (SELECT max(work_id) AS work_id FROM change_work WHERE pair_id=$pair AND work_origin='Acquired' AND side=0 AND $side=0 AND work_id<=$through AND pair_generation<=$generation AND json_extract(notice_json,'$.Value.LocalContentDirty')=1 AND path_key IN (SELECT " + KeyOf + "path) FROM issue WHERE pair_id=$pair AND kind=0) GROUP BY domain,identity,path_key) DELETE FROM change_work AS w WHERE w.pair_id=$pair" + NoRenameResponsibility + " AND w.work_origin='Acquired' AND w.work_id<=$through AND w.pair_generation<=$generation AND w.side=$side AND w.work_id NOT IN (SELECT work_id FROM retained_dirty) AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.work_id=w.work_id) AND EXISTS(SELECT 1 FROM pair p WHERE p.id=$pair AND p.execution_generation=$generation AND p.pending_json IS NULL) AND EXISTS(SELECT 1 FROM source_state WHERE pair_id=$pair AND side=$side AND source_epoch=$epoch) AND NOT EXISTS(SELECT 1 FROM source_state WHERE pair_id=$pair AND repair_reason IS NOT NULL) AND NOT EXISTS(SELECT 1 FROM folder_removal f WHERE f.pair_id=$pair AND " + FolderUnresolved + ");";
    public const string PairItemEpisodes = "SELECT i.issue_id FROM issue i INDEXED BY issue_item_retry_work JOIN change_work w ON w.work_id=i.item_retry_work_id WHERE i.pair_id=$pair AND i.item_retry_episode IS NOT NULL AND ($side IS NULL OR w.side=$side);";
    public const string ItemEpisodeByWork = "SELECT issue_id,item_retry_episode,item_retry_revision FROM issue WHERE item_retry_work_id=$work;";
    public const string ItemEpisodeBySubject = "SELECT i.issue_id,i.item_retry_episode,i.item_retry_revision,i.item_retry_work_id FROM issue_member m INDEXED BY issue_member_retry_subject JOIN issue i ON i.issue_id=m.issue_id WHERE m.pair_id=$pair AND m.side=$side AND m.path_key=$path AND m.identity IS $identity AND m.work_id IS NOT NULL AND i.item_retry_episode IS NOT NULL;";
    public const string ItemEpisodesAtPath = "SELECT i.item_retry_work_id,m.subject_json,i.issue_id,m.identity FROM issue_member m INDEXED BY issue_member_retry_subject JOIN issue i ON i.issue_id=m.issue_id WHERE m.pair_id=$pair AND m.side=$side AND m.path_key=$path AND m.work_id IS NOT NULL AND i.item_retry_episode IS NOT NULL;";
    public const string WorkDispatch = "SELECT work_origin,issue_work_key,dispatch_kind,pending_operation_id,pending_generation FROM change_work WHERE work_id=$work AND pair_id=$pair;";
    public const string EnsureIssueWork = "INSERT INTO change_work(work_id,work_origin,issue_work_key,dispatch_kind,pending_operation_id,pending_generation,pair_id,pair_generation,source_epoch,side,domain,path_key,old_path_key,identity,notice_json,before_json,acquisition_work_id,parent_anchor,obligation_key) VALUES($work,'Issue',$key,$dispatch,$pendingOperation,$pendingGeneration,$pair,$generation,$epoch,$side,$domain,$path,NULL,$identity,NULL,$before,NULL,NULL,NULL) ON CONFLICT(pair_id,issue_work_key) WHERE issue_work_key IS NOT NULL DO NOTHING;";
    public const string IssueWorkByKey = "SELECT work_id,work_origin,dispatch_kind,pending_operation_id,pending_generation,pair_generation,source_epoch,side,domain,path_key,identity,before_json FROM change_work WHERE pair_id=$pair AND issue_work_key=$key;";
    public const string BindLiveRetryWork = "UPDATE change_work SET dispatch_kind=$dispatch,pending_operation_id=$pendingOperation,pending_generation=$pendingGeneration WHERE work_id=$work AND pair_id=$pair AND pair_generation=$generation AND source_epoch=$epoch AND work_origin=$origin;";
    public const string ReadItemEpisode = "SELECT issue_id,pair_id,path,kind,cause,subject_json,item_retry_episode,item_retry_revision,item_retry_work_id FROM issue WHERE pair_id=$pair AND issue_id=$issue;";
    public const string StartItemEpisode = "UPDATE issue SET item_retry_episode=$episode,item_retry_revision=0,item_retry_work_id=$work WHERE pair_id=$pair AND issue_id=$issue AND kind=$failureKind AND localaccessretrycause(cause)=1 AND subject_json=$subject AND item_retry_episode IS NULL AND " + ItemWorkFence + ItemPendingFence + ";";
    public const string TryClaimItemRetry = "SELECT item_retry_work_id,item_retry_revision FROM issue WHERE pair_id=$pair AND issue_id=$issue AND kind=$failureKind AND localaccessretrycause(cause)=1 AND subject_json=$subject AND item_retry_episode=$episode AND item_retry_revision=$revision AND " + ItemWorkFence + " AND (w.parent_anchor IS NULL OR NOT EXISTS(SELECT 1 FROM change_work parent WHERE parent.work_id=w.parent_anchor))" + ItemPendingFence + ";";
    public const string CompleteFailedItemAttempt = "UPDATE issue SET item_retry_revision=$nextRevision WHERE pair_id=$pair AND issue_id=$issue AND kind=$failureKind AND localaccessretrycause(cause)=1 AND subject_json=$subject AND item_retry_episode=$episode AND item_retry_revision=$revision AND $nextRevision=item_retry_revision+1 AND item_retry_work_id=$work AND " + ItemWorkFence + ItemPendingFence + ";";
    public const string ClearItemEpisode = "UPDATE issue SET item_retry_episode=NULL,item_retry_revision=NULL,item_retry_work_id=NULL WHERE pair_id=$pair AND issue_id=$issue AND item_retry_episode=$episode AND item_retry_revision=$revision AND item_retry_work_id=$work AND subject_json=$subject;";
    public const string DeleteSettledIssueWork = "DELETE FROM change_work WHERE pair_id=$pair AND work_id=$work AND work_origin='Issue' AND issue_work_key=$key AND NOT EXISTS(SELECT 1 FROM issue WHERE item_retry_work_id=$work);";
    public const string ReadIssueRetryViews = "SELECT i.issue_id,i.cause,i.item_retry_episode,i.item_retry_revision,i.item_retry_work_id,w.work_origin,w.dispatch_kind,w.pending_operation_id,w.pending_generation,w.pair_generation,w.source_epoch,w.side,i.subject_json FROM issue i NOT INDEXED LEFT JOIN change_work w ON w.work_id=i.item_retry_work_id WHERE i.pair_id=$pair AND i.issue_id IN (SELECT value FROM json_each($ids));";
    public const string RebindIssueWork = "UPDATE change_work SET pair_generation=$generation,source_epoch=$epoch WHERE pair_id=$pair AND work_id=$work AND work_origin='Issue' AND issue_work_key=$key AND pair_generation=$oldGeneration AND source_epoch=$oldEpoch AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND execution_generation=$generation) AND EXISTS(SELECT 1 FROM source_state WHERE pair_id=$pair AND side=change_work.side AND source_epoch=$epoch);";
    public const string RefreshPendingRetryBinding = "UPDATE change_work SET pending_generation=$nextPendingGeneration WHERE pair_id=$pair AND work_id=$work AND dispatch_kind='LocalRecovery' AND pending_operation_id=$pendingOperation AND pending_generation=$pendingGeneration AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND execution_generation=$generation AND pending_json=$pendingJson AND coalesce(json_extract(pending_json,'$.Version'),0)=1 AND json_extract(pending_json,'$.OperationId')=$pendingOperation AND json_extract(pending_json,'$.Generation')=$nextPendingGeneration);";
    public const string CloseSupersededRetryNotice = "DELETE FROM issue WHERE pair_id=$pair AND issue_id=$issue AND path='' AND kind=$kind AND cause=$retryCause AND EXISTS(SELECT 1 FROM pair WHERE id=$pair AND execution_generation=$generation AND " + BothSourceFences + " AND retry_attempts=$expectedAttempts AND retry_after_ticks=$expectedRetryAfter);";
    public const string ReadFolderDisposition = "SELECT generation,phase,active,retirement_disposition FROM folder_removal WHERE pair_id=$pair AND operation_id=$operation;";
    public const string RetireFolderDisposition ="UPDATE folder_removal AS f SET generation=$nextGeneration,phase=$nextPhase,active=0,retirement_disposition=$disposition,cause=$cause WHERE f.pair_id=$pair AND f.operation_id=$operation AND f.generation=$generation AND $nextGeneration=f.generation+1 AND f.retirement_disposition IS NULL AND $nextPhase IN ('Unknown','Retained') AND $disposition IN ('Acknowledged','NoEffect') AND NOT EXISTS(SELECT 1 FROM folder_removal_item i WHERE i.pair_id=f.pair_id AND i.operation_id=f.operation_id AND (" + FolderCustody + " OR ($disposition='NoEffect' AND i.effect_state NOT IN ('None','Ready','NoEffect'))));";
    public const string ReclassifyItemRetry = "UPDATE issue SET cause=$nextCause,at_ticks=$at,dismissed=0,ignored=0 WHERE pair_id=$pair AND issue_id=$issue AND item_retry_episode=$episode AND item_retry_revision=$revision AND item_retry_work_id=$work AND subject_json=$subject AND cause=$expectedCause AND $nextCause<>$expectedCause AND localaccessretrycause(cause)=1 AND localaccessretrycause($nextCause)=1;";
    public const string ClassifiedInsert = "INSERT INTO scan_state.classified(policy_key,native_key,present,entry_path,entry_folder,entry_size,entry_mtime,entry_id,entry_rev,entry_hash,entry_skip,saved_local_key,saved_local_path,saved_dropbox_delta,saved_dropbox_path_delta,saved_folder,saved_local_id,saved_size,saved_mtime,saved_dropbox_id,saved_hash,verdict,follow_up) VALUES($policy_key,$native_key,$present,$entry_path,$entry_folder,$entry_size,$entry_mtime,$entry_id,$entry_rev,$entry_hash,$entry_skip,$saved_local_key,$saved_local_path,$saved_dropbox_delta,$saved_dropbox_path_delta,$saved_folder,$saved_local_id,$saved_size,$saved_mtime,$saved_dropbox_id,$saved_hash,$verdict,$follow_up);";
    // Membership is EXACT at one root key; caller walks ancestors using PathNames.
    public const string ClassifiedLocalRootForLocal = "SELECT EXISTS(SELECT 1 FROM scan_state.classified WHERE coalesce(saved_local_key,policy_key)=$key AND selected=1);";
    // Direct Local-overlap also needs an indexed strict-descendant probe.
    public const string ClassifiedLocalBelowForLocal = "SELECT EXISTS(SELECT 1 FROM scan_state.classified WHERE coalesce(saved_local_key,policy_key)>=$low AND coalesce(saved_local_key,policy_key)<$high AND selected=1);";
    public const string ClassifiedDropboxRootForLocal = "SELECT EXISTS(SELECT 1 FROM scan_state.classified WHERE coalesce(saved_dropbox_delta,saved_local_key)=$key AND selected=1);";

    // Parameter names of the statements above (literals rule): every statement binds through these.
    public const string ParamAfter = "$after";
    public const string ParamProgressOnly = "$progressOnly";
    public const string ParamAccount = "$account";
    public const string ParamFirst = "$first";
    public const string ParamAt = "$at";
    public const string ParamConnection = "$c";
    public const string ParamCause = "$cause";
    public const string ParamCompleted = "$completed";
    public const string ParamKeepRetry = "$keepRetry";
    public const string ParamDef = "$def";
    public const string ParamDropboxHash = "$dropboxHash";
    public const string ParamDropboxId = "$dropboxId";
    public const string ParamDropboxKey = "$dropboxKey";
    public const string ParamDropboxPath = "$dropboxPath";
    public const string ParamHash = "$hash";
    public const string ParamId = "$id";
    public const string ParamIsFolder = "$isFolder";
    public const string ParamTargetKey = "$k";
    public const string ParamJoinKey = "$key";
    public const string ParamKind = "$kind";
    public const string ParamKinds = "$kinds";
    public const string ParamPublicSourceKind = "$publicSourceKind";
    public const string ParamPublicTargetKind = "$publicTargetKind";
    public const string ParamPublicCauses = "$publicCauses";
    public const string ParamLevel = "$level";
    public const string ParamLimit = "$limit";
    public const string ParamLocalId = "$localId";
    public const string ParamLocalKey = "$localKey";
    public const string ParamLocalMtime = "$localMtime";
    public const string ParamLocalPath = "$localPath";
    public const string ParamPublishedJournal = "$publishedJournal";
    public const string ParamPublishedUsn = "$publishedUsn";
    public const string ParamLocalSize = "$localSize";
    public const string ParamMtime = "$mtime";
    public const string ParamOffset = "$offset";
    public const string ParamSearch = "$search";
    public const string ParamNewDropboxKey = "$newDropboxKey";
    public const string ParamNewDropboxPath = "$newDropboxPath";
    public const string ParamNewKey = "$newKey";
    public const string ParamNewLocalKey = "$newLocalKey";
    public const string ParamNewLocalPath = "$newLocalPath";
    public const string ParamNewOwnKey = "$newOwnKey";
    public const string ParamNewPath = "$newPath";
    public const string ParamOldDropboxKey = "$oldDropboxKey";
    public const string ParamOldDropboxPath = "$oldDropboxPath";
    public const string ParamOldLocalPath = "$oldLocalPath";
    public const string ParamOldKey = "$oldKey";
    public const string ParamOldLocalKey = "$oldLocalKey";
    public const string ParamOldOwnKey = "$oldOwnKey";
    public const string ParamOldPath = "$oldPath";
    public const string ParamOwnKey = "$ownKey";
    public const string ParamPair = "$p";
    public const string ParamPath = "$path";
    public const string ParamRev = "$rev";
    /// <summary>Bind name for the side number (0 local, 1 Dropbox).</summary>
    public const string ParamSide = "$side";
    public const string ParamSize = "$size";
    public const string ParamSkip = "$skip";
    public const string ParamSubject = "$subject";
    public const string ParamSummary = "$summary";
    public const string ParamAcquisition = "$acquisition";
    public const string ParamActive = "$active";
    public const string ParamAfterOperation = "$afterOperation";
    public const string ParamAfterPair = "$afterPair";
    public const string ParamAfterTicks = "$afterTicks";
    public const string ParamAllocation = "$allocation";
    public const string ParamAnchor = "$anchor";
    public const string ParamAttempts = "$attempts";
    public const string ParamBefore = "$before";
    public const string ParamBootstrap = "$bootstrap";
    public const string ParamCursor = "$cursor";
    public const string ParamDefinition = "$definition";
    public const string ParamDispatch = "$dispatch";
    public const string ParamDisposition = "$disposition";
    public const string ParamDomain = "$domain";
    public const string ParamDropboxEpoch = "$dropboxEpoch";
    public const string ParamDropboxRevision = "$dropboxRevision";
    public const string ParamEffectGeneration = "$effectGeneration";
    public const string ParamEpisode = "$episode";
    public const string ParamEpoch = "$epoch";
    public const string ParamExpected = "$expected";
    public const string ParamExpectedAt = "$expectedAt";
    public const string ParamExpectedDismissed = "$expectedDismissed";
    public const string ParamExpectedAttempts = "$expectedAttempts";
    public const string ParamExpectedCause = "$expectedCause";
    public const string ParamExpectedCursor = "$expectedCursor";
    public const string ParamExpectedIssue = "$expectedIssue";
    public const string ParamExpectedJournal = "$expectedJournal";
    public const string ParamExpectedNext = "$expectedNext";
    public const string ParamExpectedProvenance = "$expectedProvenance";
    public const string ParamExpectedRetryAfter = "$expectedRetryAfter";
    /// <summary>The Failure ordinal, bound from the enum so SQL never restates it.</summary>
    public const string ParamFailureKind = "$failureKind";
    public const string ParamGapReason = "$gapReason";
    public const string ParamGeneration = "$generation";
    public const string ParamHigh = "$high";
    public const string ParamKeys = "$keys";
    public const string ParamIdentity = "$identity";
    public const string ParamIds = "$ids";
    public const string ParamIndex = "$index";
    public const string ParamIssue = "$issue";
    public const string ParamJob = "$job";
    public const string ParamJournal = "$journal";
    public const string ParamLocalEpoch = "$localEpoch";
    public const string ParamLocalRevision = "$localRevision";
    public const string ParamLow = "$low";
    public const string ParamMember = "$member";
    public const string ParamNative = "$native";
    public const string ParamNegativeDepth = "$negativeDepth";
    public const string ParamNext = "$next";
    public const string ParamNextBefore = "$nextBefore";
    public const string ParamNextCause = "$nextCause";
    public const string ParamNextEffectGeneration = "$nextEffectGeneration";
    public const string ParamNextEpoch = "$nextEpoch";
    public const string ParamNextGeneration = "$nextGeneration";
    public const string ParamNextIssue = "$nextIssue";
    public const string ParamNextPendingGeneration = "$nextPendingGeneration";
    public const string ParamNextPhase = "$nextPhase";
    public const string ParamNextRevision = "$nextRevision";
    public const string ParamNextState = "$nextState";
    public const string ParamNormal = "$normal";
    public const string ParamNotice = "$notice";
    public const string ParamObligation = "$obligation";
    public const string ParamOldEpoch = "$oldEpoch";
    public const string ParamOldGeneration = "$oldGeneration";
    public const string ParamOperation = "$operation";
    public const string ParamOrdinal = "$ordinal";
    public const string ParamOrigin = "$origin";
    public const string ParamPageOnly = "$pageOnly";
    public const string ParamPairRow = "$pair";
    public const string ParamParent = "$parent";
    public const string ParamPendingGeneration = "$pendingGeneration";
    public const string ParamPendingJson = "$pendingJson";
    public const string ParamPendingOperation = "$pendingOperation";
    public const string ParamPhase = "$phase";
    public const string ParamPolicy = "$policy";
    public const string ParamProvenance = "$provenance";
    public const string ParamReason = "$reason";
    public const string ParamReceipt = "$receipt";
    public const string ParamResult = "$result";
    public const string ParamRemoteEpoch = "$remoteEpoch";
    public const string ParamRetryAfter = "$retryAfter";
    public const string ParamRetryCause = "$retryCause";
    public const string ParamRevision = "$revision";
    public const string ParamSelected = "$selected";
    public const string ParamStage = "$stage";
    public const string ParamState = "$state";
    public const string ParamSubmission = "$submission";
    public const string ParamTable = "$table";
    public const string ParamTake = "$take";
    public const string ParamThrough = "$through";
    public const string ParamVerdict = "$verdict";
    public const string ParamVolume = "$volume";
    public const string ParamWatermark = "$watermark";
    public const string ParamWork = "$work";
    public const string ParamDeletingSide = "$deleting_side";
    public const string ParamEffectGenerationColumn = "$effect_generation";
    public const string ParamEffectKind = "$effect_kind";
    public const string ParamEffectResultJson = "$effect_result_json";
    public const string ParamEffectState = "$effect_state";
    public const string ParamEntryFolder = "$entry_folder";
    public const string ParamEntryHash = "$entry_hash";
    public const string ParamEntryId = "$entry_id";
    public const string ParamEntryMtime = "$entry_mtime";
    public const string ParamEntryPath = "$entry_path";
    public const string ParamEntryRev = "$entry_rev";
    public const string ParamEntrySize = "$entry_size";
    public const string ParamEntrySkip = "$entry_skip";
    public const string ParamExpectedJson = "$expected_json";
    public const string ParamFollowUp = "$follow_up";
    public const string ParamFollowUpState = "$follow_up_state";
    public const string ParamNativeKey = "$native_key";
    public const string ParamOperationId = "$operation_id";
    public const string ParamPairId = "$pair_id";
    public const string ParamPolicyKey = "$policy_key";
    public const string ParamResultJson = "$result_json";
    public const string ParamRootContextJson = "$root_context_json";
    public const string ParamRootEntryJson = "$root_entry_json";
    public const string ParamSavedDropboxDelta = "$saved_dropbox_delta";
    public const string ParamSavedDropboxId = "$saved_dropbox_id";
    public const string ParamSavedDropboxPathDelta = "$saved_dropbox_path_delta";
    public const string ParamSavedFolder = "$saved_folder";
    public const string ParamSavedHash = "$saved_hash";
    public const string ParamSavedJson = "$saved_json";
    public const string ParamSavedLocalId = "$saved_local_id";
    public const string ParamSavedLocalKey = "$saved_local_key";
    public const string ParamSavedLocalPath = "$saved_local_path";
    public const string ParamSavedMtime = "$saved_mtime";
    public const string ParamSavedRootJson = "$saved_root_json";
    public const string ParamSavedSize = "$saved_size";
    public const string ParamSelectedBranch = "$selected_branch";
    public const string ParamSelectedJson = "$selected_json";
    public const string ParamSourceEvidenceJson = "$source_evidence_json";
    public const string ParamStageAllocationJson = "$stage_allocation_json";
    public const string ParamStageJson = "$stage_json";
    public const string ParamSubmissionId = "$submission_id";
    public const string ParamSubmissionMember = "$submission_member";

    // Column ordinals: the only or first column of a single-column SELECT.
    public const int FirstColumn = 0;

    // Column ordinals: CountWork. The queue snapshot returns the Local count first, the Dropbox count second.
    public const int QueueLocalCount = 0;
    public const int QueueDropboxCount = 1;

    // Column ordinals: SyncedColumns. The Dropbox key is the nullable delta, or the local key when inherited.
    public const int SyncedLocalKey = 0;
    public const int SyncedLocalPath = 1;
    public const int SyncedDropboxPath = 3;
    public const int SyncedIsFolder = 4;
    public const int SyncedLocalId = 5;
    public const int SyncedLocalSize = 6;
    public const int SyncedLocalModified = 7;
    public const int SyncedDropboxId = 8;
    public const int SyncedDropboxHash = 9;

    // Column ordinals: IssueColumns.
    public const int IssueId = 0;
    public const int IssueKind = 1;
    public const int IssueCause = 2;
    public const int IssuePath = 3;
    public const int IssueSubject = 4;
    public const int IssueAt = 5;
    public const int IssueDismissed = 6;
    public const int IssueIgnored = 7;
    public const int HeldMemberSavedJson = 8;
    public const string ParamIgnored = "$ignored";

    // Column ordinals: narrow issue reads.
    public const int HoldingKind = 0;
    public const int HoldingSubject = 1;
    public const int HoldingCause = 2;
    public const int HoldingId = 3;
    public const int HoldingPath = 4;
    public const int ClosurePath = 0;
    public const int ClosureKind = 1;
    public const int IssueCountKind = 0;
    public const int IssueCountValue = 1;

    // Column ordinals: ScanRows. The saved nine-field block starts at RowLocalPath.
    public const int RowSynced = 0;
    public const int RowLocalPath = 2;
    public const int RowLocalOwnKey = 11;
    public const int RowLocalEntryPath = 12;
    public const int RowDropboxOwnKey = 18;
    public const int RowDropboxEntryPath = 19;

    // Column ordinals: ScanRenames.
    public const int ScanSide = 0;
    public const int ScanPath = 1;
    public const int ScanId = 5;
    public const int RenameLocalKey = 9;
    public const int RenameLocalPath = 10;

    // Column ordinals: Pair fence.
    public const int PairFenceId = 0;
    public const int PairFenceExecutionGeneration = 1;
    public const int PairFenceFirstRepairDone = 2;
    public const int PairFenceDefJson = 3;

    // Column ordinals: source_state.
    public const int SourceEpoch = 0;
    public const int SourceProvenanceJson = 1;
    public const int SourceCursor = 2;
    public const int SourceRepairReason = 3;
    public const int SourceRepairRevision = 4;

    // Column ordinals: journal_position.
    public const int JournalId = 0;
    public const int JournalNextUsn = 1;

    // Column ordinals: change_work rows.
    public const int WorkId = 0;
    public const int WorkPairId = 1;
    public const int WorkPairGeneration = 2;
    public const int WorkSourceEpoch = 3;
    public const int WorkSide = 4;
    public const int WorkDomain = 5;
    public const int WorkPathKey = 6;
    public const int WorkOldPathKey = 7;
    public const int WorkIdentity = 8;
    public const int WorkNoticeJson = 9;
    public const int WorkBeforeJson = 10;
    public const int WorkAcquisitionWorkId = 11;
    public const int WorkParentAnchor = 12;
    public const int WorkObligationKey = 13;
    public const int WorkDiscoveryRequired = 14;

    // Column ordinals: DirtyIdentity.
    public const int DirtyWorkId = 0;
    public const int DirtyAcquisitionWorkId = 1;
    public const int DirtyNoticeJson = 2;

    // Column ordinals: LocalDirtyEvidence.
    public const int LocalDirtyNoticeJson = 0;
    public const int LocalDirtyAcquisitionWorkId = 1;

    // Column ordinals: ReadIssueMembers.
    public const int MemberId = 0;
    public const int MemberWorkId = 2;
    public const int MemberSubjectJson = 6;

    // Column ordinals: WorkIssue.
    public const int WorkIssueId = 1;
    public const int WorkIssueSubjectJson = 2;

    // Column ordinals: ValidateIssue.
    public const int ValidIssueKind = 1;
    public const int ValidIssueCause = 2;
    public const int ValidIssuePath = 3;
    public const int ValidIssueSubjectJson = 4;

    // Column ordinals: MemberlessSkips.
    public const int MemberlessIssue = 0;
    public const int MemberlessPair = 1;
    public const int MemberlessPath = 2;
    public const int MemberlessCause = 3;
    public const int MemberlessSubject = 4;
    // Column ordinals: SkipMember.
    public const int SkipMemberIdentity = 2;
    public const int SkipMemberSubjectJson = 3;
    public const int SkipMemberSkipCause = 4;

    // Column ordinals: folder_removal rows.
    public const int FolderOperationId = 1;
    public const int FolderGeneration = 2;
    public const int FolderPhase = 3;
    public const int FolderDeletingSide = 4;
    public const int FolderRootEntryJson = 5;
    public const int FolderSourceEvidenceJson = 6;
    public const int FolderSavedRootJson = 7;
    public const int FolderRootContextJson = 8;
    public const int FolderCursor = 9;
    public const int FolderActive = 10;
    public const int FolderCause = 11;

    // Column ordinals: folder_removal_item rows.
    public const int FolderItemOperationId = 1;
    public const int FolderItemOrdinal = 2;
    public const int FolderItemGeneration = 3;
    public const int FolderItemProvenance = 4;
    public const int FolderItemPath = 5;
    public const int FolderItemPolicyKey = 6;
    public const int FolderItemExpectedJson = 9;
    public const int FolderItemSavedJson = 10;
    public const int FolderItemVerdict = 11;
    public const int FolderItemFollowUp = 12;
    public const int FolderItemFollowUpState = 13;
    public const int FolderItemSelectedBranch = 14;
    public const int FolderItemEffectKind = 15;
    public const int FolderItemEffectState = 16;
    public const int FolderItemEffectGeneration = 17;
    public const int FolderItemSubmissionId = 18;
    public const int FolderItemSubmissionMember = 19;
    public const int FolderItemEffectResultJson = 20;
    public const int FolderItemPhase = 21;
    public const int FolderItemSelectedJson = 22;
    public const int FolderItemStageAllocationJson = 23;
    public const int FolderItemStageJson = 24;
    public const int FolderItemResultJson = 25;
    public const int FolderItemCause = 26;

    // Column ordinals: ReadyBoundary.
    public const int ReadyBoundaryNegativeDepth = 0;
    public const int ReadyBoundaryPolicyKey = 1;
    public const int ReadyBoundaryNativeKey = 2;
    public const int ReadyBoundaryPresent = 3;

    // Column ordinals: ReadyFirst and ReadyNext.
    public const int ReadyOperationId = 0;
    public const int ReadyOrdinal = 1;
    public const int ReadyEffectGeneration = 2;
    public const int ReadyEffectKind = 3;
    public const int ReadyExpectedJson = 4;
    public const int ReadyDeletingSide = 5;

    // Column ordinals: FolderSubmissionRoots.
    public const int SubmissionRootPath = 1;

    // Column ordinals: SubmissionRead and PendingFolderSubmissions.
    public const int SubmissionId = 0;
    public const int SubmissionPairId = 1;
    public const int SubmissionGeneration = 2;
    public const int SubmissionPhase = 3;
    public const int SubmissionJobId = 4;
    public const int SubmissionCause = 5;

    // Column ordinals: SubmissionEffects.
    public const int SubmissionEffectSubmissionMember = 5;
    public const int SubmissionEffectDeletingSide = 6;

    // Column ordinals: FrozenEvidence.
    public const int FrozenPolicyKey = 1;
    public const int FrozenVerdict = 22;
    public const int FrozenFollowUp = 23;
    public const int FrozenSelected = 24;

    // Column ordinals: ReadRetry.
    public const int RetryAttempts = 1;

    // Column ordinals: EarliestRetry.
    public const int EarliestRetryId = 0;
    public const int EarliestRetryPairId = 1;
    public const int EarliestRetryExecutionGeneration = 2;
    public const int EarliestRetryAttempts = 4;

    // Column ordinals: ItemEpisodeByWork.
    public const int EpisodeByWorkIssueId = 0;

    // Column ordinals: ItemEpisodesAtPath.
    public const int EpisodePathWork = 0;
    public const int EpisodePathSubject = 1;
    public const int EpisodePathIssue = 2;
    public const int EpisodePathIdentity = 3;

    // Column ordinals: ItemEpisodeBySubject.
    public const int EpisodeBySubjectIssueId = 0;
    public const int EpisodeBySubjectItemRetryWorkId = 3;

    // Column ordinals: WorkDispatch.
    public const int DispatchWorkOrigin = 0;
    public const int DispatchIssueWorkKey = 1;

    // Column ordinals: IssueWorkByKey.
    public const int IssueWorkId = 0;
    public const int IssueWorkDispatchKind = 2;
    public const int IssueWorkPendingOperationId = 3;
    public const int IssueWorkPendingGeneration = 4;
    public const int IssueWorkPairGeneration = 5;
    public const int IssueWorkSourceEpoch = 6;
    public const int IssueWorkSide = 7;
    public const int IssueWorkDomain = 8;
    public const int IssueWorkPathKey = 9;
    public const int IssueWorkIdentity = 10;

    // Column ordinals: ReadItemEpisode.
    public const int ItemEpisodeCause = 4;
    public const int ItemEpisodeSubjectJson = 5;
    public const int ItemEpisodeItemRetryEpisode = 6;


    // Column ordinals: ReadIssueRetryViews.
    public const int RetryViewIssueId = 0;
    public const int RetryViewItemRetryEpisode = 2;
    public const int RetryViewDispatchKind = 6;
    public const int RetryViewPendingOperationId = 7;
    public const int RetryViewPendingGeneration = 8;
    public const int RetryViewPairGeneration = 9;
    public const int RetryViewSourceEpoch = 10;
    public const int RetryViewSubjectJson = 12;

    // Column ordinals: ReadFolderDisposition.
    public const int DispositionRetirementDisposition = 3;
}
