using Microsoft.Data.Sqlite;

// Frozen final26.2 (044d87e9) v21 schema, not the Files On-Demand implementation under test.
// Kept as SQL so the fixture does not depend on a binary, native APIs or live state.
// The "V20" name is historical: v20 (84f0bc18) legitimately migrates to v21 on open, so a
// v20 baseline cannot assert that a no-command open leaves every sqlite_master definition
// unchanged. This is the exact schema a final26.2 store has before Files On-Demand is enabled.
internal static class FilesOnDemandV20Fixture
{
    internal static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO root(root_no,root_id,cursor) VALUES(1,'fixture-root','fixture-cursor'); " +
            "INSERT INTO dir(dir_no,path,path_key) VALUES(1,'Folder','FOLDER'); " +
            "INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags) VALUES(1,1,'FILE','file',0,'fixture-item',17,0,0); " +
            // A real final26.2 store has already completed the one-time grant backfill.
            "INSERT INTO quarantine_upgrade VALUES('grant-backfill');";
        command.ExecuteNonQuery();
    }

    private const string Schema = """
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
        CREATE INDEX pe_intent_order ON pending_effect(pair_no);
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
        CREATE TABLE IF NOT EXISTS defender_deferral(object_id TEXT PRIMARY KEY, attempts INTEGER NOT NULL,
          first_utc INTEGER NOT NULL, last_run TEXT NOT NULL, updated INTEGER NOT NULL, consent INTEGER NOT NULL DEFAULT 0, batch TEXT) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS defender_deferral_updated ON defender_deferral(updated,object_id);
        CREATE TABLE IF NOT EXISTS defender_grant(grant_id TEXT PRIMARY KEY, owner_object TEXT NOT NULL,
          source_object TEXT, local_object TEXT, path TEXT NOT NULL, path_key TEXT NOT NULL,
          stage_path TEXT NOT NULL, verified INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS defender_grant_path ON defender_grant(path_key);
        CREATE TABLE IF NOT EXISTS quarantine_upgrade(name TEXT PRIMARY KEY) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS removal_review(entity_key TEXT PRIMARY KEY, pair_id TEXT NOT NULL,
          generation TEXT NOT NULL UNIQUE, metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS prepare_header(pair_id TEXT PRIMARY KEY, generation TEXT NOT NULL UNIQUE,
          metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS prepare_row(generation TEXT NOT NULL, kind TEXT NOT NULL, ordinal INTEGER NOT NULL,
          lookup_key TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(generation, kind, ordinal));
        CREATE INDEX IF NOT EXISTS prepare_lookup ON prepare_row(generation, kind, lookup_key, ordinal);
        CREATE TABLE IF NOT EXISTS prepare_cursor(generation TEXT NOT NULL, root_id TEXT NOT NULL, cursor TEXT,
          PRIMARY KEY(generation, root_id)) WITHOUT ROWID;
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
        CREATE TABLE IF NOT EXISTS deletion_classification(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, basis_cursor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS deletion_classification_path(root_no INTEGER NOT NULL,
          path_key TEXT NOT NULL, path TEXT NOT NULL, exact INTEGER NOT NULL,
          PRIMARY KEY(root_no,path_key)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS observation_checkpoint(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, scope_json TEXT NOT NULL, cursor TEXT NOT NULL);
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
        CREATE INDEX IF NOT EXISTS nt_groupable_delete ON notice(pair_no,reason_key,notice_id) WHERE state='open';
        CREATE INDEX IF NOT EXISTS pe_uncertain_delete ON pending_effect(pair_no,action_id)
          WHERE state='uncertain' AND json_extract(action_json,'$.Kind')='Delete';
        CREATE TABLE IF NOT EXISTS listing_checkpoint(root_id TEXT PRIMARY KEY, binding TEXT NOT NULL, mode TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS listing_page(root_id TEXT NOT NULL, request_key TEXT NOT NULL,
          cursor TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(root_id,request_key));
        CREATE TABLE IF NOT EXISTS pair_pause(pair_id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS local_walk(root_id TEXT PRIMARY KEY, binding TEXT NOT NULL, cursor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS local_walk_page(root_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          payload TEXT NOT NULL, PRIMARY KEY(root_id,ordinal));
        CREATE TABLE IF NOT EXISTS local_rehash(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          last_attempt INTEGER NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS rehash_retry ON local_rehash(root_no, last_attempt, object_id);
        CREATE TABLE IF NOT EXISTS local_gap(root_no INTEGER PRIMARY KEY, epoch TEXT NOT NULL,
          journal_cursor TEXT NOT NULL, boundary INTEGER NOT NULL, trust_usn INTEGER NOT NULL,
          after_object TEXT NOT NULL DEFAULT '', discovery_done INTEGER NOT NULL DEFAULT 0,
          next_result INTEGER NOT NULL DEFAULT 0, worker_after TEXT NOT NULL DEFAULT '', worker_done INTEGER NOT NULL DEFAULT 0, reason TEXT NOT NULL DEFAULT 'journal history discontinuity');
        CREATE TABLE IF NOT EXISTS local_gap_item(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          epoch TEXT NOT NULL, expected_json TEXT NOT NULL, hash TEXT, proof_json TEXT,
          result_seq INTEGER NOT NULL DEFAULT 0, last_attempt INTEGER NOT NULL DEFAULT 0, metadata_ready INTEGER NOT NULL DEFAULT 0, metadata_stamp TEXT, failure_count INTEGER NOT NULL DEFAULT 0, failure_reason TEXT, retry_allowed INTEGER NOT NULL DEFAULT 1, defer_count INTEGER NOT NULL DEFAULT 0, defer_reason TEXT, discovery_reason TEXT,
          PRIMARY KEY(root_no,object_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS run_summary(seq INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL UNIQUE,
          pair_id TEXT NOT NULL, started INTEGER NOT NULL, completed INTEGER NOT NULL, outcome TEXT NOT NULL,
          done INTEGER NOT NULL, uploaded INTEGER NOT NULL, downloaded INTEGER NOT NULL, deleted INTEGER NOT NULL,
          moved INTEGER NOT NULL, kept_both INTEGER NOT NULL, held INTEGER NOT NULL, failed INTEGER NOT NULL,
          uncertain INTEGER NOT NULL, skipped INTEGER NOT NULL, deferred INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS run_summary_pair ON run_summary(pair_id, seq);
        CREATE TABLE IF NOT EXISTS journal_volume(serial INTEGER PRIMARY KEY, volume TEXT NOT NULL, state_json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS fs_flagged ON file_state(root_no) WHERE flags != 0;CREATE INDEX IF NOT EXISTS fs_refused ON file_state(root_no, kind) WHERE (flags & 64) != 0;CREATE INDEX IF NOT EXISTS fs_quarantine ON file_state(root_no, kind) WHERE (flags & 128) != 0;
        CREATE INDEX IF NOT EXISTS gap_ready ON local_gap_item(root_no,epoch,last_attempt,object_id) WHERE hash IS NULL AND retry_allowed=1 AND metadata_ready=0;
        CREATE INDEX IF NOT EXISTS gap_metadata ON local_gap_item(root_no,epoch,object_id) WHERE metadata_ready=1;
        CREATE INDEX IF NOT EXISTS gap_waiting ON local_gap_item(root_no,object_id) WHERE hash IS NULL AND retry_allowed=0;
        CREATE INDEX IF NOT EXISTS gap_failed ON local_gap_item(root_no,object_id) WHERE failure_count>=3;
        CREATE INDEX IF NOT EXISTS gap_results ON local_gap_item(root_no,result_seq,object_id) WHERE hash IS NOT NULL;
        CREATE INDEX IF NOT EXISTS defender_deferral_batch ON defender_deferral(batch,object_id);
        CREATE INDEX IF NOT EXISTS nt_pair_reason ON notice(pair_no,reason_key);
        CREATE TABLE IF NOT EXISTS notice_legacy_fold(pair_no INTEGER NOT NULL, reason_key TEXT NOT NULL,
          PRIMARY KEY(pair_no,reason_key)) WITHOUT ROWID;
        PRAGMA user_version=21;
        """;
}
