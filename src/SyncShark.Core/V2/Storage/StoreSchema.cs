using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the v2 state database DDL, constants, pragmas and schema verification.</summary>
internal static class StoreSchema
{
    /// <summary>The schema version written to and required by <c>PRAGMA user_version</c>; any schema change bumps it.</summary>
    // Coordinated additive slots: v12 retry, v13 quarantine, v14 gap verification,
    // v15 listing/pair pause, v16 local walk, v17 explicit conflict choices, v18 removal review, v19 journal sizing, v20 run summaries and notice-family index.
    public const int Version = 20;

    /// <summary>The state database path relative to the app data root.</summary>
    public const string DatabaseRelativePath = @"v2\state.db";

    /// <summary>SQLITE_CORRUPT.</summary>
    public const int CorruptCode = 11;

    /// <summary>SQLITE_NOTADB.</summary>
    public const int NotADatabaseCode = 26;

    private const string JournalDdl = "CREATE TABLE IF NOT EXISTS journal_volume(serial INTEGER PRIMARY KEY, volume TEXT NOT NULL, state_json TEXT NOT NULL);";

    private const string Ddl = """
        -- *_key columns hold PathKey.Of: equal exactly when equal under .NET OrdinalIgnoreCase (owner decision 50).
        CREATE TABLE dir(dir_no INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, path_key TEXT NOT NULL, mapped_key TEXT);
        CREATE INDEX dir_key ON dir(path_key);
        CREATE INDEX dir_mapped_key ON dir(mapped_key) WHERE mapped_key IS NOT NULL;
        CREATE TABLE root(root_no INTEGER PRIMARY KEY, root_id TEXT NOT NULL UNIQUE, cursor TEXT);
        CREATE TABLE pair(pair_no INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL,
          first_sync_done INTEGER NOT NULL);
        CREATE TABLE generation(gen INTEGER PRIMARY KEY AUTOINCREMENT, root_no INTEGER NOT NULL, kind TEXT NOT NULL,
          started INTEGER NOT NULL, finished INTEGER, complete INTEGER);
        CREATE TABLE file_state(root_no INTEGER NOT NULL, dir_no INTEGER NOT NULL, name_key TEXT NOT NULL, name TEXT NOT NULL,
          kind INTEGER NOT NULL,
          object_id TEXT NOT NULL, version_id TEXT, hash BLOB, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
          flags INTEGER NOT NULL, identity_json TEXT, PRIMARY KEY(root_no, dir_no, name_key, name)) WITHOUT ROWID;
        CREATE INDEX fs_obj ON file_state(root_no, object_id);
        CREATE TABLE tombstone(root_no INTEGER NOT NULL, object_id TEXT NOT NULL, dir_no INTEGER NOT NULL,
          name TEXT NOT NULL, deleted_at INTEGER NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
        CREATE TABLE sync_point(pair_no INTEGER NOT NULL, object_key TEXT NOT NULL, gen INTEGER NOT NULL, kind INTEGER NOT NULL,
          hash BLOB, size INTEGER NOT NULL,
          a_dir INTEGER NOT NULL, a_name TEXT NOT NULL, a_name_key TEXT NOT NULL, a_object_id TEXT NOT NULL, a_version_id TEXT, a_mtime INTEGER NOT NULL,
          b_dir INTEGER, b_name TEXT, b_name_key TEXT, b_object_id TEXT NOT NULL, b_version_id TEXT, b_mtime INTEGER NOT NULL,
          PRIMARY KEY(pair_no, object_key)) WITHOUT ROWID;
        CREATE INDEX sp_a_obj ON sync_point(pair_no, a_object_id);
        CREATE INDEX sp_b_obj ON sync_point(pair_no, b_object_id);
        CREATE INDEX sp_a_dir ON sync_point(pair_no, a_dir, a_name_key);
        CREATE INDEX sp_b_dir ON sync_point(pair_no, b_dir, b_name_key) WHERE b_dir IS NOT NULL;
        -- A rowid table: an intent's JSON (~1 KB) overflows a WITHOUT ROWID page's local payload, one page per row.
        CREATE TABLE pending_effect(action_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_json TEXT NOT NULL,
          state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL);
        CREATE INDEX pe_unresolved ON pending_effect(pair_no, state, updated);
        CREATE TABLE recovery(recovery_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_id TEXT NOT NULL,
          side TEXT NOT NULL, path TEXT NOT NULL, location TEXT NOT NULL, version_id TEXT, hash TEXT NOT NULL,
          size INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX rc_pair ON recovery(pair_no, created);
        CREATE TABLE notice(notice_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, rule INTEGER NOT NULL, kind TEXT NOT NULL,
          path TEXT NOT NULL, detail TEXT NOT NULL, reason_key TEXT NOT NULL, state TEXT NOT NULL, choice TEXT,
          created INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX nt_open ON notice(state, pair_no, created);
        -- Positive and negative probe results are reusable only after validating their basis cursor.
        CREATE TABLE deletion_evidence(pair_no INTEGER NOT NULL, root_no INTEGER NOT NULL, path TEXT NOT NULL,
          path_key TEXT NOT NULL, deleted_at INTEGER, probed_at INTEGER NOT NULL, basis_cursor TEXT, content_hash TEXT, failure_json TEXT,
          PRIMARY KEY(pair_no, root_no, path_key)) WITHOUT ROWID;
        """;

    // Performance-only, additive index for the intent stream and the rowid horizon. It is replayed on every
    // compatible open: (pair_no) carries rowid as the implicit final key, so MAX(rowid) and the ordered rowid
    // keyset never degrade to a primary-key scan after concurrent state changes.
    private const string IntentOrderIndexDdl =
        "CREATE INDEX IF NOT EXISTS pe_intent_order ON pending_effect(pair_no);";

    // Performance-only, additive index; no persisted row format changes or version bump.
    private const string FlaggedIndexDdl =
        "CREATE INDEX IF NOT EXISTS fs_flagged ON file_state(root_no) WHERE flags != 0;" +
        "CREATE INDEX IF NOT EXISTS fs_refused ON file_state(root_no, kind) WHERE (flags & 64) != 0;" +
        "CREATE INDEX IF NOT EXISTS fs_quarantine ON file_state(root_no, kind) WHERE (flags & 128) != 0;";

    // Quarantine storage: entity-owned evidence survives notice pruning and removal of the original file row.
    // Integration assigns its schema slot after fscomplete; the DDL and repairs are presence-guarded.
    private const string QuarantineDdl = """
        CREATE TABLE IF NOT EXISTS defender_deferral(object_id TEXT PRIMARY KEY, attempts INTEGER NOT NULL,
          first_utc INTEGER NOT NULL, last_run TEXT NOT NULL, updated INTEGER NOT NULL, consent INTEGER NOT NULL DEFAULT 0, batch TEXT) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS defender_deferral_updated ON defender_deferral(updated,object_id);
        CREATE TABLE IF NOT EXISTS defender_grant(grant_id TEXT PRIMARY KEY, owner_object TEXT NOT NULL,
          source_object TEXT, local_object TEXT, path TEXT NOT NULL, path_key TEXT NOT NULL,
          stage_path TEXT NOT NULL, verified INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS defender_grant_path ON defender_grant(path_key);
        CREATE TABLE IF NOT EXISTS quarantine_upgrade(name TEXT PRIMARY KEY) WITHOUT ROWID;
        """;

    // Additive migrations preserve all existing rows.
    private const string DeletionEvidenceDdl = """
        CREATE TABLE IF NOT EXISTS deletion_evidence(pair_no INTEGER NOT NULL, root_no INTEGER NOT NULL,
          path TEXT NOT NULL, path_key TEXT NOT NULL, deleted_at INTEGER, probed_at INTEGER NOT NULL, basis_cursor TEXT,
          content_hash TEXT, PRIMARY KEY(pair_no, root_no, path_key)) WITHOUT ROWID;
        """;

    // Rowid payload table avoids one overflow page per moderately sized JSON row.
    private const string RemovalReviewDdl = """
        CREATE TABLE IF NOT EXISTS removal_review(entity_key TEXT PRIMARY KEY, pair_id TEXT NOT NULL,
          generation TEXT NOT NULL UNIQUE, metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
        """;

    private const string PrepareDdl = """
        CREATE TABLE IF NOT EXISTS prepare_header(pair_id TEXT PRIMARY KEY, generation TEXT NOT NULL UNIQUE,
          metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS prepare_row(generation TEXT NOT NULL, kind TEXT NOT NULL, ordinal INTEGER NOT NULL,
          lookup_key TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(generation, kind, ordinal));
        CREATE INDEX IF NOT EXISTS prepare_lookup ON prepare_row(generation, kind, lookup_key, ordinal);
        CREATE TABLE IF NOT EXISTS prepare_cursor(generation TEXT NOT NULL, root_id TEXT NOT NULL, cursor TEXT,
          PRIMARY KEY(generation, root_id)) WITHOUT ROWID;
        """;

    // Presence-guarded frozen contents of a folder an explicit conflict choice removes (decisions 87/88). Filled by one
    // INSERT ... SELECT and read in bounded pages, so a large folder never sits in application memory.
    private const string ChoiceVictimDdl = """
        CREATE TABLE IF NOT EXISTS choice_merge(notice_id TEXT PRIMARY KEY, content BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS choice_restore_receipt(pair_no INTEGER NOT NULL, action_id TEXT NOT NULL,
          item_json TEXT NOT NULL, PRIMARY KEY(pair_no, action_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS choice_victim(ord INTEGER PRIMARY KEY, op_id TEXT NOT NULL, pair_no INTEGER NOT NULL,
          state INTEGER NOT NULL, depth INTEGER NOT NULL, dir_path TEXT NOT NULL, name TEXT NOT NULL, kind INTEGER NOT NULL,
          object_id TEXT NOT NULL, version_id TEXT, hash BLOB, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
          flags INTEGER NOT NULL, identity_json TEXT);
        CREATE INDEX IF NOT EXISTS cv_op ON choice_victim(op_id, state, depth, ord);
        CREATE INDEX IF NOT EXISTS cv_item ON choice_victim(op_id, dir_path, name);
        CREATE INDEX IF NOT EXISTS cv_scan ON choice_victim(op_id, state, ord);
        """;

    private const string ClassificationDdl = """
        CREATE TABLE IF NOT EXISTS deletion_classification(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, basis_cursor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS deletion_classification_path(root_no INTEGER NOT NULL,
          path_key TEXT NOT NULL, path TEXT NOT NULL, exact INTEGER NOT NULL,
          PRIMARY KEY(root_no,path_key)) WITHOUT ROWID;
        """;

    private const string ObservationDdl = """
        CREATE TABLE IF NOT EXISTS observation_checkpoint(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, scope_json TEXT NOT NULL, cursor TEXT NOT NULL);
        """;

    private const string RetryDdl = """
        CREATE TABLE IF NOT EXISTS sync_retry(pair_no INTEGER NOT NULL, path_key TEXT NOT NULL,
          json TEXT NOT NULL, retry_after INTEGER NOT NULL, PRIMARY KEY(pair_no,path_key));
        CREATE INDEX IF NOT EXISTS sync_retry_due ON sync_retry(pair_no,retry_after,path_key);
        """;

    // Presence-guarded on every open; integration assigns v15.
    private const string ListingDdl = """
        CREATE TABLE IF NOT EXISTS listing_checkpoint(root_id TEXT PRIMARY KEY, binding TEXT NOT NULL, mode TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS listing_page(root_id TEXT NOT NULL, request_key TEXT NOT NULL,
          cursor TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(root_id,request_key));
        CREATE TABLE IF NOT EXISTS pair_pause(pair_id TEXT PRIMARY KEY);
        """;

    // v16: independently resumable local walks.
    private const string LocalWalkDdl = """
        CREATE TABLE IF NOT EXISTS local_walk(root_id TEXT PRIMARY KEY, binding TEXT NOT NULL, cursor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS local_walk_page(root_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          payload TEXT NOT NULL, PRIMARY KEY(root_id,ordinal));
        """;

    // One row per (pair, reason) whose pre-fa93b15f path-bearing notice ids have already been folded. This is
    // additive, presence-guarded feature storage: no version bump and no existing row changes.
    private const string LegacyFoldDdl = """
        CREATE TABLE IF NOT EXISTS notice_legacy_fold(pair_no INTEGER NOT NULL, reason_key TEXT NOT NULL,
          PRIMARY KEY(pair_no,reason_key)) WITHOUT ROWID;
        """;

    private const string RehashDdl = """
        CREATE TABLE IF NOT EXISTS local_rehash(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          last_attempt INTEGER NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS rehash_retry ON local_rehash(root_no, last_attempt, object_id);
        """;

    private const string GapDdl = """
        CREATE TABLE IF NOT EXISTS local_gap(root_no INTEGER PRIMARY KEY, epoch TEXT NOT NULL,
          journal_cursor TEXT NOT NULL, boundary INTEGER NOT NULL, trust_usn INTEGER NOT NULL,
          after_object TEXT NOT NULL DEFAULT '', discovery_done INTEGER NOT NULL DEFAULT 0,
          next_result INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS local_gap_item(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          epoch TEXT NOT NULL, expected_json TEXT NOT NULL, hash TEXT, proof_json TEXT,
          result_seq INTEGER NOT NULL DEFAULT 0, last_attempt INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(root_no,object_id)) WITHOUT ROWID;
        """;

    private static void EnsureGapSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, GapDdl);
        // Presence guards also recover an interrupted additive migration before its version stamp.
        foreach (var (table, column, definition) in new[]
        {
            ("local_gap", "next_result", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap", "reason", "TEXT NOT NULL DEFAULT 'journal history discontinuity'"),
            ("local_gap_item", "proof_json", "TEXT"),
            ("local_gap_item", "result_seq", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "last_attempt", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "failure_count", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "failure_reason", "TEXT"),
            ("local_gap_item", "retry_allowed", "INTEGER NOT NULL DEFAULT 1"),
            ("local_gap_item", "defer_count", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "defer_reason", "TEXT")
        })
            if (Convert.ToInt64(Scalar(connection, transaction,
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name;", ("$name", column))) == 0)
                Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        // Replace only the old ready predicate; the entity work and legacy gap_retry remain intact.
        if (Scalar(connection, transaction, "SELECT sql FROM sqlite_master WHERE name='gap_ready';") is string ready &&
            !ready.Contains("retry_allowed=1", StringComparison.Ordinal))
            Execute(connection, transaction, "DROP INDEX gap_ready;");
        Execute(connection, transaction,
            "CREATE INDEX IF NOT EXISTS gap_ready ON local_gap_item(root_no,epoch,last_attempt,object_id) WHERE hash IS NULL AND retry_allowed=1;" +
            "CREATE INDEX IF NOT EXISTS gap_waiting ON local_gap_item(root_no,object_id) WHERE hash IS NULL AND retry_allowed=0;" +
            "CREATE INDEX IF NOT EXISTS gap_failed ON local_gap_item(root_no,object_id) WHERE failure_count>=3;" +
            "CREATE INDEX IF NOT EXISTS gap_results ON local_gap_item(root_no,result_seq,object_id) WHERE hash IS NOT NULL;");
    }

    // one bounded summary per finished run. AUTOINCREMENT keeps the paging cursor unique and
    // never reused after pruning, so two runs sharing a start millisecond still page exactly.
    private const string RunSummaryDdl = """
        CREATE TABLE IF NOT EXISTS run_summary(seq INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL UNIQUE,
          pair_id TEXT NOT NULL, started INTEGER NOT NULL, completed INTEGER NOT NULL, outcome TEXT NOT NULL,
          done INTEGER NOT NULL, uploaded INTEGER NOT NULL, downloaded INTEGER NOT NULL, deleted INTEGER NOT NULL,
          moved INTEGER NOT NULL, kept_both INTEGER NOT NULL, held INTEGER NOT NULL, failed INTEGER NOT NULL,
          uncertain INTEGER NOT NULL, skipped INTEGER NOT NULL, deferred INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS run_summary_pair ON run_summary(pair_id, seq);
        """;

    private const string TemporaryDdl = """
        CREATE TEMP TABLE IF NOT EXISTS removal_observation(session TEXT NOT NULL, side TEXT NOT NULL,
          path_key TEXT NOT NULL, path TEXT NOT NULL, json TEXT NOT NULL,
          PRIMARY KEY(session,side,path_key,path)) WITHOUT ROWID;
        CREATE TEMP TABLE IF NOT EXISTS tmp_dirs(dir_no INTEGER PRIMARY KEY);
        CREATE TEMP TABLE IF NOT EXISTS tmp_items(dir_no INTEGER NOT NULL, name_key TEXT NOT NULL,
          PRIMARY KEY(dir_no, name_key)) WITHOUT ROWID;
        """;

    private static void EnsureDefenderColumns(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if ((long)Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info('defender_deferral') WHERE name='consent';")! == 0)
            Execute(connection, transaction, "ALTER TABLE defender_deferral ADD COLUMN consent INTEGER NOT NULL DEFAULT 0;");
        if ((long)Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info('defender_deferral') WHERE name='batch';")! == 0)
            Execute(connection, transaction, "ALTER TABLE defender_deferral ADD COLUMN batch TEXT;");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS defender_deferral_batch ON defender_deferral(batch,object_id);");
    }

    private static void BackfillDefenderGrants(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT 1 FROM quarantine_upgrade WHERE name='grant-backfill';") is not null) return;
        var after = "";
        while (true)
        {
            var page = new List<(string Id, string Detail)>();
            using (var cmd = Command(connection, null, "SELECT notice_id,detail FROM notice WHERE notice_id>$after AND reason_key='antivirus-quarantine' ORDER BY notice_id LIMIT 512;"))
            {
                cmd.Parameters.AddWithValue("$after", after);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) page.Add((reader.GetString(0), reader.GetString(1)));
            }
            if (page.Count == 0)
            {
                Execute(connection, null, "INSERT OR IGNORE INTO quarantine_upgrade VALUES('grant-backfill');");
                return;
            }
            using var tx = connection.BeginTransaction();
            foreach (var row in page)
            {
                var q = NoticeDetailCodec.Decode(new Notice("", 30, NoticeKind.Info, "", row.Detail, QuarantineFailure.NoticeKey)).Quarantine;
                if (q?.GrantedExclusionPath is not { } path || q.GrantedStagePath is not { } stage) continue;
                // Legacy payloads lack authoritative post-publication identity. Preserve the entity's watch,
                // but do not silently authorize a replacement merely because its path matches.
                Execute(connection, tx, "INSERT OR IGNORE INTO defender_grant VALUES($id,$owner,NULL,NULL,$path,$key,$stage,1);",
                    ("$id", "legacy-" + row.Id), ("$owner", q.Source?.ObjectId ?? q.ExpectedTarget?.ObjectId ?? row.Id),
                    ("$path", path), ("$key", RowCodec.Key(path)), ("$stage", stage));
            }
            tx.Commit();
            after = page[^1].Id;
        }
    }

    /// <summary>Adds schema objects without removing rows; mapped directory keys backfill in short transactions.</summary>
    private static void Migrate(SqliteConnection connection, long fromVersion)
    {
        using (var transaction = connection.BeginTransaction())
        {
            if (fromVersion < 4) Execute(connection, transaction, DeletionEvidenceDdl);
            if (fromVersion < 5 && Scalar(connection, transaction,
                "SELECT COUNT(*) FROM pragma_table_info('deletion_evidence') WHERE name='failure_json';") is long columns && columns == 0)
                Execute(connection, transaction, "ALTER TABLE deletion_evidence ADD COLUMN failure_json TEXT;");
            Execute(connection, transaction, PrepareDdl);
            transaction.Commit();
        }
        // v7 is restartable: column first, short backfill batches, version stamp only after completion.
        if (fromVersion < 7) MigrateMappedDirectories(connection);
        if (fromVersion < 8) Execute(connection, null, FlaggedIndexDdl);
        if (fromVersion < 9) Execute(connection, null, ClassificationDdl);
        if (fromVersion < 10) Execute(connection, null, ObservationDdl);
        Execute(connection, null, RehashDdl);
        Execute(connection, null, RetryDdl);
        Execute(connection, null, LegacyFoldDdl);
        EnsureFeatureSchema(connection, fromVersion);
    }

    // The same idempotent steps run on upgrade and every compatible open. Each committed
    // version includes all preceding feature objects; interrupted backfills resume before stamping.
    private static void EnsureFeatureSchema(SqliteConnection connection, long fromVersion)
    {
        Execute(connection, null, QuarantineDdl);
        EnsureDefenderColumns(connection, null);
        Execute(connection, null, FlaggedIndexDdl);
        Execute(connection, null, IntentOrderIndexDdl);
        BackfillDefenderGrants(connection);
        Step(13, null);
        Step(14, tx => EnsureGapSchema(connection, tx));
        Step(15, tx => Execute(connection, tx, ListingDdl));
        Step(16, tx => Execute(connection, tx, LocalWalkDdl));
        Step(17, tx => Execute(connection, tx, ChoiceVictimDdl));
        Step(18, tx => Execute(connection, tx, RemovalReviewDdl));
        Step(19, tx => Execute(connection, tx, JournalDdl));
        Step(20, tx => MigrateRunSummary(connection, tx));

        void Step(int version, Action<SqliteTransaction>? create)
        {
            using var tx = connection.BeginTransaction();
            create?.Invoke(tx);
            if (fromVersion < version) Execute(connection, tx, $"PRAGMA user_version={version};");
            tx.Commit();
        }
    }

    // Presence-guarded additive step, also run after the version chain on every compatible startup.
    // At final26 integration this composes AFTER v12..v17/removal_review without changing their stamps.
    private static void MigrateRunSummary(SqliteConnection connection, SqliteTransaction? transaction) =>
        Execute(connection, transaction, RunSummaryDdl +
            "CREATE INDEX IF NOT EXISTS nt_pair_reason ON notice(pair_no,reason_key);");

    private static void MigrateMappedDirectories(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('dir') WHERE name='mapped_key';") is long n && n == 0)
            Execute(connection, null, "ALTER TABLE dir ADD COLUMN mapped_key TEXT;");
        long after = -1;
        while (true)
        {
            var batch = new List<(long Id, string Path)>();
            using (var command = Command(connection, null, "SELECT dir_no,path FROM dir WHERE dir_no>$after ORDER BY dir_no LIMIT 256;"))
            {
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            if (batch.Count == 0) break;
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var (id, path) in batch)
                    if (DropboxNameMapping.Map(path) is var mapped && mapped != path)
                        Execute(connection, transaction, "UPDATE dir SET mapped_key=$key WHERE dir_no=$id;",
                            ("$key", RowCodec.Key(mapped)), ("$id", id));
                transaction.Commit();
            }
            after = batch[^1].Id;
        }
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS dir_mapped_key ON dir(mapped_key) WHERE mapped_key IS NOT NULL;");
    }

    private static readonly string[] ExpectedNames =
    [
        "journal_volume", "dir", "root", "pair", "generation", "file_state", "tombstone", "sync_point", "pending_effect", "sync_retry", "sync_retry_due", "recovery",
        "notice", "notice_legacy_fold", "local_gap", "local_gap_item", "gap_ready", "gap_failed", "gap_waiting", "gap_results", "local_rehash", "rehash_retry", "deletion_evidence", "dir_key", "dir_mapped_key", "fs_obj", "fs_flagged", "fs_refused", "sp_a_obj", "sp_b_obj", "sp_a_dir", "sp_b_dir",

        "removal_review", "pe_unresolved", "pe_intent_order", "rc_pair", "nt_open", "prepare_header", "prepare_row", "prepare_lookup", "prepare_cursor",
        "quarantine_upgrade", "defender_deferral_batch", "fs_quarantine", "defender_deferral", "defender_deferral_updated", "defender_grant", "defender_grant_path",
        "choice_victim", "cv_op", "cv_item", "cv_scan", "choice_restore_receipt", "choice_merge",
        "run_summary", "run_summary_pair", "nt_pair_reason",
        "deletion_classification", "deletion_classification_path", "observation_checkpoint",
        "listing_checkpoint", "listing_page", "pair_pause", "local_walk", "local_walk_page",
    ];

    /// <summary>A command bound to the connection and optional transaction (Microsoft.Data.Sqlite requires it).</summary>
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Runs a statement with the given parameters.</summary>
    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>The first column of the first row, or null.</summary>
    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    /// <summary>How many rows the last statement on this transaction changed.</summary>
    public static long Changes(SqliteConnection connection, SqliteTransaction? transaction) =>
        (long)Scalar(connection, transaction, "SELECT changes();")!;

    /// <summary>The EXPLAIN QUERY PLAN detail lines of a statement, each prefixed with a label (parameters bound null).</summary>
    public static IReadOnlyList<string> QueryPlan(SqliteConnection connection, string label, string sql,
        params string[] parameterNames)
    {
        using var command = Command(connection, null, "EXPLAIN QUERY PLAN " + sql);
        foreach (var name in parameterNames) command.Parameters.AddWithValue(name, DBNull.Value);
        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) lines.Add(label + ": " + reader.GetString(3));
        return lines;
    }

    /// <summary>Whether a SQLite failure means the state database is unusable (CORRUPT or NOTADB).</summary>
    public static bool IsDamaged(SqliteException exception) =>
        exception.SqliteErrorCode is CorruptCode or NotADatabaseCode;

    /// <summary>Applies the store's pragmas and requires WAL journaling; a non-WAL database is damaged.</summary>
    public static void Pragmas(SqliteConnection connection, V2Settings settings)
    {
        var journal = Scalar(connection, null, "PRAGMA journal_mode=WAL;");
        if (!string.Equals(journal as string, "wal", StringComparison.OrdinalIgnoreCase))
            throw new StateDamagedException($"v2 state journal_mode is '{journal}', expected wal.");
        // FULL: a committed intent survives power loss or an OS crash, so an effect never runs without one on disk
        // (WAL + NORMAL may lose the last commits). Group commit (B-124) amortises the extra syncs.
        Execute(connection, null,
            settings.StoreDurableCommits ? "PRAGMA synchronous=FULL;" : "PRAGMA synchronous=NORMAL;");
        // Narrowing/large subtree work sets must spill to disk rather than retain O(inventory) native
        // memory. Set this once at open: changing it later would discard the store's temp tables.
        Execute(connection, null, "PRAGMA temp_store=FILE;");
        Execute(connection, null, "PRAGMA foreign_keys=OFF;");
        Execute(connection, null, $"PRAGMA cache_size=-{settings.StoreCacheMb * 1024};");
        Execute(connection, null, $"PRAGMA busy_timeout={(long)settings.StoreBusyTimeout.TotalMilliseconds};");
    }

    /// <summary>Creates the fresh schema and stamps the version in one transaction.</summary>
    public static void Create(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, Ddl);
        Execute(connection, transaction, JournalDdl);
        Execute(connection, transaction, RehashDdl);
        Execute(connection, transaction, RetryDdl);
        EnsureGapSchema(connection, transaction);
        MigrateRunSummary(connection, transaction);
        Execute(connection, transaction, LegacyFoldDdl);
        Execute(connection, transaction, FlaggedIndexDdl);
        Execute(connection, transaction, IntentOrderIndexDdl);
        Execute(connection, transaction, QuarantineDdl);
        Execute(connection, transaction, "INSERT INTO quarantine_upgrade VALUES('grant-backfill');");
        EnsureDefenderColumns(connection, transaction);
        Execute(connection, transaction, PrepareDdl);
        Execute(connection, transaction, ChoiceVictimDdl);
        Execute(connection, transaction, RemovalReviewDdl);
        Execute(connection, transaction, ClassificationDdl);
        Execute(connection, transaction, ObservationDdl);
        Execute(connection, transaction, ListingDdl);
        Execute(connection, transaction, LocalWalkDdl);
        Execute(connection, transaction, $"PRAGMA user_version={Version};");
        transaction.Commit();
    }

    /// <summary>Creates the session's temporary working tables.</summary>
    public static void CreateTemporary(SqliteConnection connection) => Execute(connection, null, TemporaryDdl);

    /// <summary>Checks the version and the exact set of tables and indexes; a mismatch is damage.</summary>
    public static void Verify(SqliteConnection connection)
    {
        if (Scalar(connection, null, "PRAGMA user_version;") is not long version)
            throw new StateDamagedException("v2 state schema version is missing or not numeric.");
        // Preserve v3/v4 evidence upgrades and add v6 prepare storage in place.
        // All migrations preserve existing rows and indexes; pre-v3 stores need a fresh database.
        if (version is >= 3 and < Version)
        {
            Migrate(connection, version);
            version = Version;
        }
        if (version is >= 1 and < Version) throw new StateNeedsFreshDatabaseException(version, Version);
        if (version != Version)
            throw new StateDamagedException($"v2 state schema version {version}, expected {Version}.");
        Execute(connection, null, RetryDdl);
        Execute(connection, null, ClassificationDdl);
        Execute(connection, null, ObservationDdl);
        EnsureFeatureSchema(connection, version);
        Execute(connection, null, LegacyFoldDdl);
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command(connection, null,
            "SELECT name FROM sqlite_master WHERE type IN ('table','index') AND name NOT LIKE 'sqlite_%';"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        // Unknown or missing non-additive objects still indicate damage.
        names.Remove("gap_retry"); // Legacy unused index is tolerated, never dropped or newly created.
        if (!names.SetEquals(ExpectedNames))
            throw new StateDamagedException($"v2 state schema mismatch: {{{string.Join(", ", names.Order())}}}.");
        // Preview consent metadata is additive, including a store already stamped at the current version.
        EnsureDefenderColumns(connection, null);
        Execute(connection, null, FlaggedIndexDdl);
        // Unreleased v4 previews may have created the cache before cursor binding was added.
        // Legacy rows keep a null basis and are discarded before any reuse.
        if (Scalar(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('deletion_evidence') WHERE name='basis_cursor';") is long columns && columns == 0)
            Execute(connection, null, "ALTER TABLE deletion_evidence ADD COLUMN basis_cursor TEXT;");
        // Decision 69 adds the last revision's content hash as nullable evidence; an older v4 preview
        // gains the column in place, and its existing rows keep an unknown hash (never skip Recovery).
        if (Scalar(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('deletion_evidence') WHERE name='content_hash';") is long hashColumns && hashColumns == 0)
            Execute(connection, null, "ALTER TABLE deletion_evidence ADD COLUMN content_hash TEXT;");
    }
}
