namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // Exact fresh-store DDL from final25 (916397cb), not a new schema with its version lowered.
    // All fixture rows added by the integration check are synthetic.
    private const string LegacyV12Schema = """
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
        CREATE TABLE IF NOT EXISTS local_rehash(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          last_attempt INTEGER NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS rehash_retry ON local_rehash(root_no, last_attempt, object_id);
        CREATE TABLE IF NOT EXISTS sync_retry(pair_no INTEGER NOT NULL, path_key TEXT NOT NULL,
          json TEXT NOT NULL, retry_after INTEGER NOT NULL, PRIMARY KEY(pair_no,path_key));
        CREATE INDEX IF NOT EXISTS sync_retry_due ON sync_retry(pair_no,retry_after,path_key);
        CREATE INDEX IF NOT EXISTS fs_flagged ON file_state(root_no) WHERE flags != 0;CREATE INDEX IF NOT EXISTS fs_refused ON file_state(root_no, kind) WHERE (flags & 64) != 0;
        CREATE TABLE IF NOT EXISTS prepare_header(pair_id TEXT PRIMARY KEY, generation TEXT NOT NULL UNIQUE,
          metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS prepare_row(generation TEXT NOT NULL, kind TEXT NOT NULL, ordinal INTEGER NOT NULL,
          lookup_key TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(generation, kind, ordinal));
        CREATE INDEX IF NOT EXISTS prepare_lookup ON prepare_row(generation, kind, lookup_key, ordinal);
        CREATE TABLE IF NOT EXISTS prepare_cursor(generation TEXT NOT NULL, root_id TEXT NOT NULL, cursor TEXT,
          PRIMARY KEY(generation, root_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS deletion_classification(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, basis_cursor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS deletion_classification_path(root_no INTEGER NOT NULL,
          path_key TEXT NOT NULL, path TEXT NOT NULL, exact INTEGER NOT NULL,
          PRIMARY KEY(root_no,path_key)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS observation_checkpoint(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, scope_json TEXT NOT NULL, cursor TEXT NOT NULL);
        PRAGMA user_version=12;
        """;
}
