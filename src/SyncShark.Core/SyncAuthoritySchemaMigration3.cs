namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration3
{
    public const int PreviousVersion = 2;

    public const string Sql = """
        DROP TRIGGER IF EXISTS observation_no_update;
        CREATE TRIGGER IF NOT EXISTS observation_no_update
        BEFORE UPDATE ON observation
        FOR EACH ROW WHEN NOT (
            OLD.complete_generation=0 AND NEW.complete_generation=1
            AND OLD.observation_id=NEW.observation_id
            AND OLD.scan_generation_id IS NEW.scan_generation_id
            AND OLD.root_binding_id=NEW.root_binding_id
            AND OLD.scan_generation=NEW.scan_generation
            AND OLD.object_version_id IS NEW.object_version_id
            AND OLD.membership_id IS NEW.membership_id
            AND OLD.evidence_json=NEW.evidence_json
            AND OLD.observed_utc=NEW.observed_utc
            AND EXISTS(SELECT 1 FROM scan_generation g
                       WHERE g.scan_generation_id=NEW.scan_generation_id AND g.status='complete')
        )
        BEGIN
            SELECT RAISE(ABORT, 'observation evidence is append-only');
        END;

        CREATE TABLE IF NOT EXISTS upload_session_checkpoint (
            allocation_id TEXT PRIMARY KEY REFERENCES resource_allocation(allocation_id) ON DELETE RESTRICT,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            session_id TEXT NOT NULL CHECK(length(session_id) > 0),
            confirmed_offset_bytes INTEGER NOT NULL CHECK(confirmed_offset_bytes >= 0),
            source_object_version_id TEXT NOT NULL REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            source_size_bytes INTEGER NOT NULL CHECK(source_size_bytes > 0),
            source_content_hash TEXT NOT NULL CHECK(length(source_content_hash) = 64),
            status TEXT NOT NULL CHECK(status IN ('active','completed','abandoned')),
            transition_lease_id TEXT NOT NULL REFERENCES lease(lease_id) ON DELETE RESTRICT,
            transition_fencing_token INTEGER NOT NULL CHECK(transition_fencing_token > 0),
            updated_utc TEXT NOT NULL,
            CHECK(confirmed_offset_bytes <= source_size_bytes)
        ) STRICT;

        CREATE TRIGGER IF NOT EXISTS upload_session_checkpoint_allocation_guard
        BEFORE INSERT ON upload_session_checkpoint
        FOR EACH ROW WHEN NOT EXISTS (
            SELECT 1 FROM resource_allocation a
            WHERE a.allocation_id=NEW.allocation_id
              AND a.operation_id=NEW.operation_id
              AND a.resource_kind='upload_session'
        )
        BEGIN
            SELECT RAISE(ABORT, 'upload-session checkpoint lacks its preauthorized allocation');
        END;

        CREATE TRIGGER IF NOT EXISTS upload_session_checkpoint_identity_immutable
        BEFORE UPDATE OF allocation_id,operation_id,source_object_version_id,source_size_bytes,source_content_hash
        ON upload_session_checkpoint
        BEGIN
            SELECT RAISE(ABORT, 'upload-session checkpoint source authority is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS upload_session_checkpoint_requires_current_fence_insert
        BEFORE INSERT ON upload_session_checkpoint
        FOR EACH ROW WHEN NOT EXISTS (
            SELECT 1 FROM lease l JOIN operation o ON o.operation_id=NEW.operation_id
            WHERE l.lease_id=NEW.transition_lease_id
              AND l.scope_kind='operation' AND l.scope_id=NEW.operation_id
              AND l.status='active' AND l.fencing_token=NEW.transition_fencing_token
              AND julianday(l.expires_utc)>julianday('now')
              AND o.current_lease_id=l.lease_id
              AND o.current_fencing_token=l.fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'upload-session checkpoint does not hold the current fenced operation lease');
        END;

        CREATE TRIGGER IF NOT EXISTS upload_session_checkpoint_requires_current_fence_update
        BEFORE UPDATE ON upload_session_checkpoint
        FOR EACH ROW WHEN NOT EXISTS (
            SELECT 1 FROM lease l JOIN operation o ON o.operation_id=NEW.operation_id
            WHERE l.lease_id=NEW.transition_lease_id
              AND l.scope_kind='operation' AND l.scope_id=NEW.operation_id
              AND l.status='active' AND l.fencing_token=NEW.transition_fencing_token
              AND julianday(l.expires_utc)>julianday('now')
              AND o.current_lease_id=l.lease_id
              AND o.current_fencing_token=l.fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'upload-session checkpoint update does not hold the current fenced operation lease');
        END;

        CREATE TABLE IF NOT EXISTS sync_path_index (
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            relative_path_key TEXT COLLATE NOCASE NOT NULL,
            object_kind TEXT NOT NULL CHECK(object_kind IN ('file','directory')),
            local_present INTEGER NOT NULL CHECK(local_present IN (0,1)),
            dropbox_present INTEGER NOT NULL CHECK(dropbox_present IN (0,1)),
            online_only INTEGER NOT NULL CHECK(online_only IN (0,1)),
            local_stable_identity TEXT,
            dropbox_stable_identity TEXT,
            included INTEGER NOT NULL CHECK(included IN (0,1)),
            local_scan_generation_id TEXT REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            dropbox_scan_generation_id TEXT REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            subtree_folder_count INTEGER NOT NULL DEFAULT 1 CHECK(subtree_folder_count >= 1),
            subtree_included_folder_count INTEGER NOT NULL DEFAULT 1 CHECK(subtree_included_folder_count >= 0),
            subtree_local_items INTEGER NOT NULL DEFAULT 0 CHECK(subtree_local_items >= 0),
            subtree_online_only_items INTEGER NOT NULL DEFAULT 0 CHECK(subtree_online_only_items >= 0),
            subtree_mixed_items INTEGER NOT NULL DEFAULT 0 CHECK(subtree_mixed_items >= 0),
            subtree_excluded_items INTEGER NOT NULL DEFAULT 0 CHECK(subtree_excluded_items >= 0),
            updated_utc TEXT NOT NULL,
            PRIMARY KEY(pair_id, relative_path_key)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS sync_path_index_folder_page
            ON sync_path_index(pair_id, object_kind, relative_path_key);
        CREATE INDEX IF NOT EXISTS sync_path_index_presence
            ON sync_path_index(pair_id, object_kind, local_present, dropbox_present, online_only, included);
        """;
}
