using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the v2 state database DDL, constants, pragmas and schema verification.</summary>
internal static class StoreSchema
{
    /// <summary>The schema version written to and required by <c>PRAGMA user_version</c>; any schema change bumps it.</summary>
    // v22 adds entity-owned deferred planning work.
    // Coordinated additive slots: v12 retry, v13 quarantine, v14 gap verification,
    // v15 listing/pair pause, v16 local walk, v17 explicit conflict choices, v18 removal review,
    // v19 journal sizing, v20 run summaries and notice-family index, v21 staged gap metadata/protection frontier.
    // v23 records terminal notice closure separately from its original creation.
    // v24 monotonic intents; v25 indexing retries; v26 durable alias continuation.
    public const int Version = 27;
    // v27 separates proof evidence revision from retry scheduling.

    /// <summary>The state database path relative to the app data root.</summary>
    public const string DatabaseRelativePath = @"v2\state.db";

    /// <summary>SQLITE_CORRUPT.</summary>
    public const int CorruptCode = 11;

    /// <summary>SQLITE_NOTADB.</summary>
    public const int NotADatabaseCode = 26;

    private const string NoticeTerminalIndexDdl =
        "CREATE INDEX IF NOT EXISTS nt_terminal ON notice(closed_at,notice_id) WHERE state='closed' AND restore_hold=0;";

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
        CREATE TABLE pending_effect(intent_no INTEGER PRIMARY KEY AUTOINCREMENT, action_id TEXT NOT NULL UNIQUE, pair_no INTEGER NOT NULL, action_json TEXT NOT NULL,
          state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL);
        CREATE INDEX pe_unresolved ON pending_effect(pair_no, state, updated);
        CREATE TABLE recovery(recovery_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_id TEXT NOT NULL,
          side TEXT NOT NULL, path TEXT NOT NULL, path_key TEXT, location TEXT NOT NULL, version_id TEXT, hash TEXT NOT NULL,
          size INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX rc_pair ON recovery(pair_no, created);
        CREATE TABLE notice(notice_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, rule INTEGER NOT NULL, kind TEXT NOT NULL,
          path TEXT NOT NULL, path_key TEXT, detail TEXT NOT NULL, reason_key TEXT NOT NULL, state TEXT NOT NULL, choice TEXT,
          created INTEGER NOT NULL, closed_at INTEGER, restore_hold INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
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

    // Performance-only, additive index for pair-scoped Recovery keyset pages: recovery_id is the page key and
    // pair_no scopes it, so ORDER BY recovery_id reads the index in order instead of sorting the pair's rows.
    private const string RecoveryOrderIndexDdl =
        "CREATE INDEX IF NOT EXISTS rc_page ON recovery(pair_no, recovery_id);";

    // Indexed exact-path lookup for the bulk winner (finding 1), plus a partial index that makes the reopen repair a
    // probe when an older binary inserted rows without the key.
    private const string RecoveryPathIndexDdl =
        "CREATE INDEX IF NOT EXISTS rc_path ON recovery(pair_no, path_key, created DESC, recovery_id DESC);" +
        "CREATE INDEX IF NOT EXISTS rc_path_missing ON recovery(recovery_id) WHERE path_key IS NULL;";

    // Performance-only, additive index for bounded restore-notice discovery: only the two restore reasons are in the
    // partial index, so a page never walks unrelated open notices.
    private static readonly string RestoreNoticePageIndexDdl =
        "CREATE INDEX IF NOT EXISTS nt_restore_page ON notice(pair_no, notice_id) " +
        "WHERE " + NoticeTable.ActionableSql("state") + " AND (reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready');";

    // Performance-only, additive index for the per-path pending-restore protection predicate. It reads the stored
    // case-insensitive path key, so each planned path costs its bounded ancestor lookups instead of a pair-wide scan.
    private static readonly string RestoreNoticePathIndexDdl =
        "CREATE INDEX IF NOT EXISTS nt_restore_path ON notice(pair_no, path_key) " +
        "WHERE " + NoticeTable.ActionableSql("state") + " AND reason_key='recovery-restore-pending';";

    // Ready notices are retained closed as history; approved-plan replay must still see their path. No state predicate.
    private const string RestoreNoticeReadyPathIndexDdl =
        "CREATE INDEX IF NOT EXISTS nt_restore_path_ready ON notice(pair_no, path_key) " +
        "WHERE reason_key='recovery-restore-ready';";

    // A pending restore holds a path only while it carries a frozen intent (bytes may have been written). This
    // partial index serves both the one-per-run EXISTS gate and the bounded held-path snapshot.
    private static readonly string RestoreNoticeHoldPathIndexDdl =
        "CREATE INDEX IF NOT EXISTS nt_restore_hold_path ON notice(pair_no, path_key) " +
        "WHERE " + NoticeTable.ActionableSql("state") + " AND reason_key='recovery-restore-pending' AND restore_hold=1;";

    // The reopen repair pages only restore notices (including closed ready history), never the notice table.
    private const string RestoreNoticeReasonPageIndexDdl =
        "CREATE INDEX IF NOT EXISTS nt_restore_reason_page ON notice(notice_id) " +
        "WHERE reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready';";

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

    private static readonly string RetryDdl = $"""
        CREATE TABLE IF NOT EXISTS sync_retry(pair_no INTEGER NOT NULL, path_key TEXT NOT NULL,
          json TEXT NOT NULL, retry_after INTEGER NOT NULL, PRIMARY KEY(pair_no,path_key));
        CREATE INDEX IF NOT EXISTS sync_retry_due ON sync_retry(pair_no,retry_after,path_key);
        CREATE INDEX IF NOT EXISTS sync_retry_kind ON sync_retry(pair_no,json_extract(json,'$.Kind'),path_key);
        CREATE INDEX IF NOT EXISTS sync_retry_delete_blocked ON sync_retry(pair_no,path_key)
          WHERE json_extract(json,'$.DeleteBlockedDetail') IS NOT NULL;
        CREATE INDEX IF NOT EXISTS sync_retry_delete_review ON sync_retry(pair_no,path_key)
          WHERE json_extract(json,'$.MissingDeleteIdentityReview')=1;
        CREATE INDEX IF NOT EXISTS sync_retry_review_path ON sync_retry(pair_no,json_extract(json,'$.Path'))
          WHERE json_extract(json,'$.MissingDeleteIdentityReview')=1;
        CREATE INDEX IF NOT EXISTS nt_groupable_delete ON notice(pair_no,reason_key,notice_id) WHERE {NoticeTable.ActionableSql("state")} ;
        CREATE INDEX IF NOT EXISTS pe_uncertain_delete ON pending_effect(pair_no,action_id)
          WHERE state='uncertain' AND json_extract(action_json,'$.Kind')='Delete';
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

    private const string PlanningDeferredDdl = """
        CREATE TABLE IF NOT EXISTS planning_deferred(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          pair_no INTEGER NOT NULL, path TEXT NOT NULL, version INTEGER NOT NULL,
          reason TEXT NOT NULL DEFAULT 'Waiting for content verification',
          PRIMARY KEY(root_no,object_id,pair_no)) WITHOUT ROWID;
        DROP INDEX IF EXISTS planning_deferred_due;
        CREATE INDEX IF NOT EXISTS planning_deferred_pair ON planning_deferred(pair_no,root_no,object_id);
        CREATE INDEX IF NOT EXISTS planning_deferred_pair_version ON planning_deferred(pair_no,version,root_no,object_id);
        """;

    // v24: the independent index's wake detector asks MAX(version) for one root on every host pass.
    // Without this index that is a whole-root scan of the deferred set; with it SQLite walks the
    // rightmost (root_no,version) entry, so a million-row set stays bounded.
    private const string PlanningDeferredRootVersionDdl =
        "CREATE INDEX IF NOT EXISTS planning_deferred_root_version ON planning_deferred(root_no,version);";

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
            // v21 separates worker progress from published protection. The small root
            // progress rows are seeded from the legacy frontier only when columns are added.
            ("local_gap", "inventory_pending", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap", "worker_after", "TEXT NOT NULL DEFAULT ''"),
            ("local_gap", "worker_done", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "metadata_ready", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "metadata_stamp", "TEXT"),
            ("local_gap", "next_result", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap", "reason", "TEXT NOT NULL DEFAULT 'journal history discontinuity'"),
            ("local_gap_item", "proof_json", "TEXT"),
            ("local_gap_item", "result_seq", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "last_attempt", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "failure_count", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "failure_reason", "TEXT"),
            ("local_gap_item", "retry_allowed", "INTEGER NOT NULL DEFAULT 1"),
            ("local_gap_item", "defer_count", "INTEGER NOT NULL DEFAULT 0"),
            ("local_gap_item", "defer_reason", "TEXT"),
            ("local_gap_item", "discovery_reason", "TEXT"),
            ("local_gap_item", "retry_after", "INTEGER NOT NULL DEFAULT 0"),
            ("local_rehash", "failure_count", "INTEGER NOT NULL DEFAULT 0"),
            ("local_rehash", "retry_after", "INTEGER NOT NULL DEFAULT 0"),
            ("root", "index_failure_count", "INTEGER NOT NULL DEFAULT 0"),
            ("root", "index_retry_after", "INTEGER NOT NULL DEFAULT 0"),
            ("root", "index_failure_reason", "TEXT")
        })
            if (Convert.ToInt64(Scalar(connection, transaction,
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name;", ("$name", column))) == 0)
            {
                Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
                if (table == "local_gap" && column == "worker_after")
                    Execute(connection, transaction, "UPDATE local_gap SET worker_after=after_object;");
                if (table == "local_gap" && column == "worker_done")
                    Execute(connection, transaction, "UPDATE local_gap SET worker_done=discovery_done;");
            }
        // Replace only the old ready predicate; the entity work and legacy gap_retry remain intact.
        if (Scalar(connection, transaction, "SELECT sql FROM sqlite_master WHERE name='gap_ready';") is string ready &&
            !ready.Contains("metadata_ready=0", StringComparison.Ordinal))
            Execute(connection, transaction, "DROP INDEX gap_ready;");
        Execute(connection, transaction,
            "CREATE INDEX IF NOT EXISTS gap_ready ON local_gap_item(root_no,epoch,last_attempt,object_id) WHERE hash IS NULL AND retry_allowed=1 AND metadata_ready=0;" +
            "CREATE INDEX IF NOT EXISTS gap_metadata ON local_gap_item(root_no,epoch,object_id) WHERE metadata_ready=1;" +
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

    /// <summary>Adds and fills the notice path key once; later opens only verify the marker.</summary>
    private static void EnsureNoticePathKeys(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('notice') WHERE name='path_key';") is long columns && columns == 0)
            Execute(connection, null, "ALTER TABLE notice ADD COLUMN path_key TEXT;");
        BackfillNoticePathKeys(connection);
    }

    // Bounded pages so a million notices never materialize at once. The marker makes this a one-time pass.
    private static void BackfillNoticePathKeys(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT 1 FROM quarantine_upgrade WHERE name='notice-path-key-backfill';") is not null) return;
        var after = "";
        while (true)
        {
            var page = new List<(string Id, string Path)>();
            using (var command = Command(connection, null,
                "SELECT notice_id,path FROM notice WHERE notice_id>$after ORDER BY notice_id LIMIT 512;"))
            {
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) page.Add((reader.GetString(0), reader.GetString(1)));
            }
            if (page.Count == 0)
            {
                Execute(connection, null, "INSERT OR IGNORE INTO quarantine_upgrade VALUES('notice-path-key-backfill');");
                return;
            }
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var (id, path) in page)
                    Execute(connection, transaction, "UPDATE notice SET path_key=$key WHERE notice_id=$id AND path_key IS NULL;",
                        ("$key", PathKey.Of(path)), ("$id", id));
                transaction.Commit();
            }
            after = page[^1].Id;
        }
    }

    /// <summary>Adds the indexed Recovery path key once; older binaries insert rows without it (NULL).</summary>
    private static void EnsureRecoveryPathKeys(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('recovery') WHERE name='path_key';") is long columns && columns == 0)
            Execute(connection, null, "ALTER TABLE recovery ADD COLUMN path_key TEXT;");
    }

    /// <summary>
    /// Fills keys an older binary left NULL after a rollback. Recovery rows are immutable, so only NULL needs repair;
    /// the partial index makes a healthy database one empty probe, and pages of 512 bound memory.
    /// </summary>
    private static void RepairRecoveryPathKeys(SqliteConnection connection)
    {
        var after = "";
        while (true)
        {
            var page = new List<(string Id, string Path)>();
            using (var command = Command(connection, null,
                "SELECT recovery_id,path FROM recovery WHERE path_key IS NULL AND recovery_id>$after " +
                "ORDER BY recovery_id LIMIT 512;"))
            {
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) page.Add((reader.GetString(0), reader.GetString(1)));
            }
            if (page.Count == 0) return;
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var (id, path) in page)
                    Execute(connection, transaction, "UPDATE recovery SET path_key=$key WHERE recovery_id=$id AND path_key IS NULL;",
                        ("$key", PathKey.Of(path)), ("$id", id));
                transaction.Commit();
            }
            after = page[^1].Id;
        }
    }

    /// <summary>Adds the frozen-intent hold marker once; older binaries simply leave it at its zero default.</summary>
    private static void EnsureNoticeRestoreHold(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('notice') WHERE name='restore_hold';") is long columns && columns == 0)
            Execute(connection, null, "ALTER TABLE notice ADD COLUMN restore_hold INTEGER NOT NULL DEFAULT 0;");
    }

    /// <summary>
    /// Recomputes a restore notice's indexed path key and hold marker from its durable row on every open. A rolled-back
    /// binary writes <c>path_key</c> as NULL (it does not know the column), or updates <c>path</c> while leaving the old
    /// key, and its INSERT leaves <c>restore_hold</c> at zero. Without this repair a restored file could lose its
    /// protection. Pages of 512 through the restore-only partial index: a million unrelated notices are never read and
    /// no state is mutated.
    /// </summary>
    private static void ReconcileRestoreNotices(SqliteConnection connection)
    {
        var after = "";
        while (true)
        {
            var page = new List<(string Id, string Path, string Reason, string Detail)>();
            using (var command = Command(connection, null,
                "SELECT notice_id,path,reason_key,detail FROM notice WHERE notice_id>$after " +
                "AND (reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready') " +
                "ORDER BY notice_id LIMIT 512;"))
            {
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    page.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            if (page.Count == 0) return;
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var (id, path, reason, detail) in page)
                {
                    var stored = NoticeDetailCodec.Decode(new Notice("", 0, NoticeKind.Info, path, detail, reason));
                    var hold = reason == RecoveryRestoreHold.PendingReason &&
                        RecoveryRestoreHold.HasFrozenIntent(stored.ConflictOperation) ? 1 : 0;
                    Execute(connection, transaction,
                        "UPDATE notice SET path_key=$key, restore_hold=$hold WHERE notice_id=$id " +
                        "AND (path_key IS NOT $key OR restore_hold<>$hold);",
                        ("$key", PathKey.Of(path)), ("$hold", hold), ("$id", id));
                }
                transaction.Commit();
            }
            after = page[^1].Id;
        }
    }

    /// <summary>Adds schema objects without removing rows; mapped directory keys backfill in short transactions.</summary>
    private static void Migrate(SqliteConnection connection, long fromVersion, TimeProvider clock)
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
        EnsureFeatureSchema(connection, fromVersion, clock);
    }

    // The same idempotent steps run on upgrade and every compatible open. Each committed
    // version includes all preceding feature objects; interrupted backfills resume before stamping.
    private static void EnsureFeatureSchema(SqliteConnection connection, long fromVersion, TimeProvider clock)
    {
        if (FilesOnDemandSchema.IsEnabled(connection)) FilesOnDemandSchema.Ensure(connection);
        Execute(connection, null, QuarantineDdl);
        EnsureDefenderColumns(connection, null);
        Execute(connection, null, FlaggedIndexDdl);
        Execute(connection, null, IntentOrderIndexDdl);
        Execute(connection, null, RecoveryOrderIndexDdl);
        EnsureRecoveryPathKeys(connection);
        Execute(connection, null, RecoveryPathIndexDdl);
        RepairRecoveryPathKeys(connection);
        EnsureNoticePathKeys(connection);
        EnsureNoticeRestoreHold(connection);
        Execute(connection, null, RestoreNoticePageIndexDdl);
        Execute(connection, null, RestoreNoticePathIndexDdl);
        Execute(connection, null, RestoreNoticeReadyPathIndexDdl);
        Execute(connection, null, RestoreNoticeHoldPathIndexDdl);
        Execute(connection, null, RestoreNoticeReasonPageIndexDdl);
        ReconcileRestoreNotices(connection);
        BackfillDefenderGrants(connection);
        Step(13, null);
        Step(14, tx => EnsureGapSchema(connection, tx));
        Step(15, tx => Execute(connection, tx, ListingDdl));
        Step(16, tx => Execute(connection, tx, LocalWalkDdl));
        Step(17, tx => Execute(connection, tx, ChoiceVictimDdl));
        Step(18, tx => Execute(connection, tx, RemovalReviewDdl));
        Step(19, tx => Execute(connection, tx, JournalDdl));
        Step(20, tx => MigrateRunSummary(connection, tx));
        Step(21, tx => EnsureGapSchema(connection, tx));
        Step(22, tx =>
        {
            EnsureGapSchema(connection, tx);
            Execute(connection, tx, PlanningDeferredDdl);
            // Only unshipped v22 stores contain this unused scheduling column.
            if (Convert.ToInt64(Scalar(connection, tx,
                "SELECT COUNT(*) FROM pragma_table_info('planning_deferred') WHERE name='last_attempt';")) != 0)
                Execute(connection, tx, "ALTER TABLE planning_deferred DROP COLUMN last_attempt;");
            if (Convert.ToInt64(Scalar(connection, tx,
                "SELECT COUNT(*) FROM pragma_table_info('planning_deferred') WHERE name='reason';")) == 0)
                Execute(connection, tx, "ALTER TABLE planning_deferred ADD COLUMN reason TEXT NOT NULL DEFAULT 'Waiting for content verification';");
        });

        Step(23, tx =>
        {
            // Notice semantics migrate once, not on every compatible (possibly read-only archive)
            // open. A current-version database is validated without replaying data mutations.
            var missingClosureColumn = Convert.ToInt64(Scalar(connection, tx,
                "SELECT COUNT(*) FROM pragma_table_info('notice') WHERE name='closed_at';")) == 0;
            // Interrupted preview stores can carry a newer stamp with the old additive shape.
            // Repair that missing shape once; complete current stores still perform no data writes.
            if (fromVersion >= 23 && !missingClosureColumn) return;
            if (missingClosureColumn)
            {
                Execute(connection, tx, "ALTER TABLE notice ADD COLUMN closed_at INTEGER;");
                // Legacy rows have no closure timestamp. Creation is not a safe proxy for closure:
                // conservatively retain a full window from migration rather than prune a recent resolution.
                Execute(connection, tx, "UPDATE notice SET closed_at=$now WHERE state='closed';",
                    ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
            }
            // Legacy Ignore sometimes meant permanent exclusion. Preserve that retained user policy:
            // those rows are terminal exclusions, not unresolved work resurrected by this upgrade.
            connection.CreateFunction<string, string, bool>("notice_path_excluded", (json, path) =>
            {
                var pair = System.Text.Json.JsonSerializer.Deserialize<PairDef>(json, RowCodec.Json);
                return pair?.Excluded.Any(prefix => PairPath.IsSelfOrUnder(path, prefix)) == true;
            }, isDeterministic: true);
            Execute(connection, tx,
                "UPDATE notice SET state='closed',choice='exclude',closed_at=COALESCE(closed_at,$now) " +
                "WHERE " + NoticeTable.IgnoredSql("choice") + " AND EXISTS (SELECT 1 FROM pair p " +
                "WHERE p.pair_no=notice.pair_no AND notice_path_excluded(p.def_json,notice.path));",
                ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
            Execute(connection, tx, "UPDATE notice SET state='open',closed_at=NULL WHERE state='closed' AND " +
                NoticeTable.IgnoredSql("choice") + ";");
            Execute(connection, tx, NoticeTerminalIndexDdl);
            // Earlier versions indexed only visible/open rows. Authority queries now include aggregated
            // rows, so replace each old partial index once; never rebuild an already-upgraded index.
            foreach (var index in new[] { "nt_restore_page", "nt_restore_path", "nt_restore_hold_path", "nt_groupable_delete" })
                if (Scalar(connection, tx, "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;",
                    ("$name", index)) is string sql && sql.Contains("state='open'", StringComparison.Ordinal))
                    Execute(connection, tx, "DROP INDEX " + index + ";");
            Execute(connection, tx, RestoreNoticePageIndexDdl + RestoreNoticePathIndexDdl + RestoreNoticeHoldPathIndexDdl + RetryDdl);
        });

        Step(24, tx => EnsureIntentInsertionOrder(connection, tx));
        // Runs on every compatible open as well, so an interrupted create resumes before the stamp.
        Step(25, tx =>
        {
            EnsureGapSchema(connection, tx);
            Execute(connection, tx, PlanningDeferredRootVersionDdl);
        });

        Step(26, tx => EnsureDeferredAliases(connection, tx));

        // Integration: this step follows the existing v23/v24/v25/v26 steps.
        Step(27, tx => EnsureRehashEvidenceRevision(connection, tx));

        void Step(int version, Action<SqliteTransaction>? create)
        {
            using var tx = connection.BeginTransaction();
            create?.Invoke(tx);
            if (fromVersion < version) Execute(connection, tx, $"PRAGMA user_version={version};");
            tx.Commit();
        }
    }

    private static void EnsureDeferredAliases(SqliteConnection connection, SqliteTransaction tx)
    {
        foreach (var (name, type) in new[] { ("alias_dir", "INTEGER NOT NULL DEFAULT -1"),
            ("alias_key", "TEXT NOT NULL DEFAULT ''"), ("alias_name", "TEXT NOT NULL DEFAULT ''"),
            ("alias_unsettled", "INTEGER NOT NULL DEFAULT 0") })
            if (Convert.ToInt64(Scalar(connection, tx,
                "SELECT COUNT(*) FROM pragma_table_info('planning_deferred') WHERE name=$name;", ("$name", name))) == 0)
                Execute(connection, tx, $"ALTER TABLE planning_deferred ADD COLUMN {name} {type};");
    }

    private static void EnsureIntentInsertionOrder(SqliteConnection connection, SqliteTransaction tx)
    {
        if (Convert.ToInt64(Scalar(connection, tx,
            "SELECT COUNT(*) FROM pragma_table_info('pending_effect') WHERE name='intent_no';")) != 0) return;
        // Capture every explicit index, including optional feature indexes. The table and its
        // rows, rowids, indexes and version stamp change in one transaction or not at all.
        var indexes = new List<string>();
        using (var command = Command(connection, tx,
            "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name='pending_effect' AND sql IS NOT NULL;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) indexes.Add(reader.GetString(0));
        Execute(connection, tx, """
            CREATE TABLE pending_effect_v24(intent_no INTEGER PRIMARY KEY AUTOINCREMENT,
              action_id TEXT NOT NULL UNIQUE, pair_no INTEGER NOT NULL, action_json TEXT NOT NULL,
              state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL);
            INSERT INTO pending_effect_v24(intent_no,action_id,pair_no,action_json,state,attempts,updated)
              SELECT rowid,action_id,pair_no,action_json,state,attempts,updated FROM pending_effect;
            DROP TABLE pending_effect;
            ALTER TABLE pending_effect_v24 RENAME TO pending_effect;
            """);
        foreach (var sql in indexes) Execute(connection, tx, sql);
    }

    private static void EnsureRehashEvidenceRevision(SqliteConnection connection, SqliteTransaction tx)
    {
        if (Convert.ToInt64(Scalar(connection, tx,
            "SELECT COUNT(*) FROM pragma_table_info('local_rehash') WHERE name='evidence_revision';")) != 0) return;
        Execute(connection, tx, "ALTER TABLE local_rehash ADD COLUMN evidence_revision INTEGER NOT NULL DEFAULT 0;");
        // Preserve every existing obligation's captured token; selection order is no longer evidence.
        Execute(connection, tx, "UPDATE local_rehash SET evidence_revision=last_attempt;");
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
        "planning_deferred", "planning_deferred_pair", "planning_deferred_pair_version", "planning_deferred_root_version", "journal_volume", "dir", "root", "pair", "generation", "file_state", "tombstone", "sync_point", "pending_effect", "sync_retry", "sync_retry_due", "sync_retry_kind", "sync_retry_delete_blocked", "sync_retry_delete_review", "sync_retry_review_path", "nt_groupable_delete", "pe_uncertain_delete", "recovery",
        "notice", "notice_legacy_fold", "local_gap", "local_gap_item", "gap_ready", "gap_failed", "gap_waiting", "gap_results", "gap_metadata", "local_rehash", "rehash_retry", "deletion_evidence", "dir_key", "dir_mapped_key", "fs_obj", "fs_flagged", "fs_refused", "sp_a_obj", "sp_b_obj", "sp_a_dir", "sp_b_dir",

        "removal_review", "pe_unresolved", "pe_intent_order", "rc_pair", "rc_page", "rc_path", "rc_path_missing", "nt_open", "nt_terminal", "nt_restore_page", "nt_restore_path", "nt_restore_path_ready",
        "nt_restore_hold_path", "nt_restore_reason_page", "prepare_header", "prepare_row", "prepare_lookup", "prepare_cursor",
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
        Execute(connection, transaction, NoticeTerminalIndexDdl);
        Execute(connection, transaction, JournalDdl);
        Execute(connection, transaction, RehashDdl);
        EnsureRehashEvidenceRevision(connection, transaction);
        Execute(connection, transaction, PlanningDeferredDdl);
        EnsureDeferredAliases(connection, transaction);
        Execute(connection, transaction, PlanningDeferredRootVersionDdl);
        Execute(connection, transaction, RetryDdl);
        EnsureGapSchema(connection, transaction);
        MigrateRunSummary(connection, transaction);
        Execute(connection, transaction, LegacyFoldDdl);
        Execute(connection, transaction, FlaggedIndexDdl);
        Execute(connection, transaction, IntentOrderIndexDdl);
        Execute(connection, transaction, RecoveryOrderIndexDdl);
        Execute(connection, transaction, RecoveryPathIndexDdl);
        Execute(connection, transaction, RestoreNoticePageIndexDdl);
        Execute(connection, transaction, RestoreNoticePathIndexDdl);
        Execute(connection, transaction, RestoreNoticeReadyPathIndexDdl);
        Execute(connection, transaction, RestoreNoticeHoldPathIndexDdl);
        Execute(connection, transaction, RestoreNoticeReasonPageIndexDdl);
        Execute(connection, transaction, QuarantineDdl);
        Execute(connection, transaction, "INSERT INTO quarantine_upgrade VALUES('grant-backfill');");
        Execute(connection, transaction, "INSERT INTO quarantine_upgrade VALUES('notice-path-key-backfill');");
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
    public static void Verify(SqliteConnection connection) => Verify(connection, TimeProvider.System);

    public static void Verify(SqliteConnection connection, TimeProvider clock)
    {
        if (Scalar(connection, null, "PRAGMA user_version;") is not long version)
            throw new StateDamagedException("v2 state schema version is missing or not numeric.");
        // Preserve v3/v4 evidence upgrades and add v6 prepare storage in place.
        // All migrations preserve existing rows and indexes; pre-v3 stores need a fresh database.
        if (version is >= 3 and < Version)
        {
            Migrate(connection, version, clock);
            version = Version;
        }
        if (version is >= 1 and < Version) throw new StateNeedsFreshDatabaseException(version, Version);
        if (version != Version)
            throw new StateDamagedException($"v2 state schema version {version}, expected {Version}.");
        Execute(connection, null, RetryDdl);
        Execute(connection, null, ClassificationDdl);
        Execute(connection, null, ObservationDdl);
        EnsureFeatureSchema(connection, version, clock);
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
        if (!names.SetEquals(FilesOnDemandSchema.IsEnabled(connection)
            ? ExpectedNames.Concat(FilesOnDemandSchema.Names) : ExpectedNames))
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
