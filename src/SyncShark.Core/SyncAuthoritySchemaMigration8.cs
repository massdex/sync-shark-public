namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration8
{
    public const int PreviousVersion = 7;

    // Additive and narrow: exactly three new STRICT tables, two read indexes and one partial unique
    // index. No retained table, column, trigger, constraint or row is created, dropped or rewritten
    // by this migration, so every existing row keeps its identity, its values and its foreign keys
    // byte for byte and the migration is safe to run inside the existing versioned transaction with
    // its existing rollback backup.
    //
    // Rewind state moves out of the bounded product-state aggregate into this existing authority
    // schema. rewind_session holds only bounded header/current-preview identity fields, so header
    // growth is independent of the number of reviewed paths. rewind_preview_item holds the exact
    // original reviewed preview semantics once per admitted preview. rewind_path_progress holds one
    // latest bounded progress row per reviewed item, so one checkpoint is one indexed row update
    // rather than a rewrite of the whole reviewed path set.
    //
    // relative_path_key carries the existing ordinal-ignore-case/PilotPath identity computed in
    // managed code. SQLite NOCASE is ASCII-only, so it is never used as the path identity here.
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS rewind_session (
            session_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL,
            connection_id TEXT NOT NULL,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            pair_name TEXT NOT NULL,
            dropbox_root TEXT NOT NULL,
            local_checkpoint_scan_id TEXT NOT NULL,
            dropbox_checkpoint_scan_id TEXT NOT NULL,
            dropbox_checkpoint_cursor TEXT NOT NULL,
            checkpoint_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('awaiting_dropbox','preview_ready','applying','applied_paused','resumed','blocked','partially_applied','apply_stopped','effect_unknown','apply_blocked')),
            local_preview_scan_id TEXT,
            dropbox_preview_scan_id TEXT,
            preview_generation INTEGER NOT NULL DEFAULT 0 CHECK(preview_generation >= 0),
            operation_group_id TEXT,
            detail TEXT,
            CHECK((local_preview_scan_id IS NULL) = (dropbox_preview_scan_id IS NULL))
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS one_active_rewind_session_per_pair
            ON rewind_session(pair_id) WHERE status<>'resumed';
        CREATE INDEX IF NOT EXISTS rewind_session_updated_utc ON rewind_session(updated_utc DESC);

        CREATE TABLE IF NOT EXISTS rewind_preview_item (
            session_id TEXT NOT NULL REFERENCES rewind_session(session_id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            relative_path_key TEXT NOT NULL,
            group_label TEXT NOT NULL,
            proposed_effect TEXT NOT NULL,
            dropbox_changed INTEGER NOT NULL CHECK(dropbox_changed IN (0, 1)),
            local_changed INTEGER NOT NULL CHECK(local_changed IN (0, 1)),
            reason TEXT NOT NULL,
            PRIMARY KEY(session_id, relative_path_key)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS rewind_preview_item_session ON rewind_preview_item(session_id);

        CREATE TABLE IF NOT EXISTS rewind_path_progress (
            session_id TEXT NOT NULL REFERENCES rewind_session(session_id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            relative_path_key TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('pending','planned','completed','conflict','blocked','stopped','effect_unknown')),
            operation_id TEXT,
            detail TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY(session_id, relative_path_key)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS rewind_path_progress_session ON rewind_path_progress(session_id);
        """;
}
