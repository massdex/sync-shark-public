namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The durable store and the disposable tables attached during Repair.</summary>
public static class Schema
{
    /// <summary>Version installed only after the schema transaction succeeds.</summary>
    public const int Version = 17;
    /// <summary>Connection configuration for the durable store.</summary>
    public const string Pragmas = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
    /// <summary>Creates the durable tables, indexes and root-change trigger on a fresh image only; never run on an existing store.</summary>
    // The name_count CHECK uses DeletedNamesCodec.DeletedBlockRows, fixed by the v16 disk format.
    public const string Create = """
        CREATE TABLE pair(id INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL, pending_json TEXT,
          last_repair_json TEXT, first_repair_done INTEGER NOT NULL DEFAULT 0,
          deleted_history_cursor TEXT CHECK(deleted_history_cursor IS NULL OR length(deleted_history_cursor)>0),
          deleted_history_owed INTEGER NOT NULL DEFAULT 1 CHECK(deleted_history_owed IN (0,1))) STRICT;
        CREATE TABLE synced_entry(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          local_path TEXT NOT NULL, dropbox_key_delta TEXT, dropbox_path_delta TEXT,
          dropbox_key TEXT GENERATED ALWAYS AS (coalesce(dropbox_key_delta, pathkey(local_path))) VIRTUAL,
          dropbox_path TEXT GENERATED ALWAYS AS (coalesce(dropbox_path_delta, local_path)) VIRTUAL,
          is_folder INTEGER NOT NULL, local_id ANY NOT NULL, local_size INTEGER NOT NULL, local_mtime INTEGER NOT NULL,
          dropbox_id TEXT NOT NULL, dropbox_hash ANY NOT NULL,
          local_published_journal INTEGER, local_published_usn INTEGER) STRICT;
        CREATE UNIQUE INDEX synced_local_key ON synced_entry(pair_id, pathkey(local_path));
        CREATE UNIQUE INDEX synced_dropbox_key ON synced_entry(pair_id, dropbox_key_delta) WHERE dropbox_key_delta IS NOT NULL;
        CREATE INDEX synced_local_id ON synced_entry(pair_id, local_id);
        CREATE INDEX synced_dropbox_id ON synced_entry(pair_id, dropbox_id);
        CREATE TABLE issue(issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
         at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
         ignored INTEGER NOT NULL DEFAULT 0 CHECK(ignored IN (0,1)),
         UNIQUE(pair_id,path,kind,cause)) STRICT;
        CREATE INDEX issue_path_key ON issue(pair_id, pathkey(path), kind);
        CREATE TABLE journal_position(
         volume_key TEXT PRIMARY KEY NOT NULL CHECK(length(volume_key)>0),
         journal_id INTEGER NOT NULL, next_usn INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE source_state(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         repair_reason TEXT CHECK(repair_reason IS NULL OR length(repair_reason)>0),
         repair_revision INTEGER NOT NULL CHECK(repair_revision>=0),
         PRIMARY KEY(pair_id,side),
         CHECK(side=1 OR cursor IS NULL)

        ) STRICT;
        CREATE TABLE discovery_deleted_block(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          last_key TEXT NOT NULL, name_count INTEGER NOT NULL CHECK(name_count BETWEEN 1 AND 5000),
          names BLOB NOT NULL, PRIMARY KEY(pair_id,last_key)) STRICT;
        CREATE TABLE discovery_deleted_delta(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, spent INTEGER NOT NULL DEFAULT 0 CHECK(spent IN (0,1)), PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json ON pair
        WHEN json_extract(new.def_json,'$.LocalRoot') IS NOT json_extract(old.def_json,'$.LocalRoot') OR
             json_extract(new.def_json,'$.DropboxRoot') IS NOT json_extract(old.def_json,'$.DropboxRoot') OR
             json_extract(new.def_json,'$.ConnectionId') IS NOT json_extract(old.def_json,'$.ConnectionId') OR
             json_extract(new.def_json,'$.Roots') IS NOT json_extract(old.def_json,'$.Roots')
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id AND spent=0;
        END;
        CREATE TABLE accepted_job(
          pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          route TEXT NOT NULL, job_id TEXT NOT NULL,
          launch TEXT NOT NULL CHECK(json_valid(launch) AND json_type(launch)='array'),
          PRIMARY KEY(pair_id,route,job_id)) STRICT;
        CREATE TABLE issue_member(
         member_id INTEGER PRIMARY KEY,
         issue_id INTEGER REFERENCES issue(issue_id) ON DELETE SET NULL,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)), path_key TEXT NOT NULL,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         subject_json TEXT NOT NULL CHECK(json_valid(subject_json) AND coalesce(json_extract(subject_json,'$.Version'),0)=1),
         skip_cause TEXT
        ) STRICT;
        CREATE INDEX issue_member_issue ON issue_member(issue_id,side,path_key);
        CREATE INDEX issue_member_scope ON issue_member(pair_id,side,path_key,issue_id);
        CREATE UNIQUE INDEX issue_member_skip ON issue_member(pair_id,side,path_key) WHERE skip_cause IS NOT NULL;
        """;
    /// <summary>Creates only the listing and deleted-name buffers on the attached scan store.</summary>
    public const string CreateScan = """
        CREATE TABLE IF NOT EXISTS scan_state.scan(pair_id INTEGER NOT NULL, scan_id BLOB NOT NULL, side INTEGER NOT NULL,
          own_key TEXT GENERATED ALWAYS AS (pathkey(path)) VIRTUAL, key_delta TEXT,
          key TEXT GENERATED ALWAYS AS (coalesce(key_delta, own_key)) VIRTUAL, path TEXT NOT NULL, is_folder INTEGER NOT NULL, size INTEGER NOT NULL,
          mtime INTEGER NOT NULL, id ANY NOT NULL, rev TEXT NOT NULL, hash ANY NOT NULL, skip TEXT,
          shared INTEGER NOT NULL DEFAULT 0) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS scan_state.scan_own ON scan(pair_id, scan_id, side, pathkey(path));
        CREATE INDEX IF NOT EXISTS scan_state.scan_key ON scan(pair_id, scan_id, side, key_delta) WHERE side=1 AND key_delta IS NOT NULL;
        CREATE TABLE IF NOT EXISTS scan_state.deleted_name(scan_id BLOB NOT NULL,path_key TEXT NOT NULL,PRIMARY KEY(scan_id,path_key)) STRICT, WITHOUT ROWID;
        """;
}
