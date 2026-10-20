namespace Massdex.SyncShark.V2;

// Presence-guarded additive schema: merge-safe with independently arriving schema slots.
internal static class FilesOnDemandSchema
{
    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS storage_policy(root_id TEXT NOT NULL, object_id TEXT NOT NULL,
          mode TEXT NOT NULL CHECK(mode IN ('offline','online-only')), revision INTEGER NOT NULL,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS storage_online_policy ON storage_policy(root_id,object_id) WHERE mode='online-only';
        CREATE TABLE IF NOT EXISTS storage_registration(root_id TEXT PRIMARY KEY,path TEXT NOT NULL,registration_id TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS storage_path_intent(root_id TEXT NOT NULL,path_key TEXT NOT NULL,path TEXT NOT NULL,
          root_object TEXT NOT NULL,mode TEXT NOT NULL,next_attempt INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(root_id,path_key)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS storage_intent_due ON storage_path_intent(next_attempt,root_id,path_key);
        CREATE TABLE IF NOT EXISTS storage_policy_sweep(root_id TEXT PRIMARY KEY,after_pair INTEGER NOT NULL DEFAULT 0,
          after_key TEXT NOT NULL DEFAULT '',revision INTEGER NOT NULL DEFAULT 1) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS storage_item(root_id TEXT NOT NULL, object_id TEXT NOT NULL,
          remote_binding TEXT NOT NULL, remote_json TEXT NOT NULL, availability TEXT NOT NULL,
          pinned INTEGER NOT NULL, in_sync INTEGER NOT NULL,
          PRIMARY KEY(root_id,object_id,remote_binding)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS storage_remote_object ON storage_item(remote_binding,json_extract(remote_json,'$.Id'));
        CREATE TABLE IF NOT EXISTS storage_settlement(job_id INTEGER PRIMARY KEY AUTOINCREMENT,
          root_id TEXT NOT NULL, object_id TEXT NOT NULL, remote_binding TEXT NOT NULL,
          remote_json TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending',
          UNIQUE(root_id,object_id,remote_binding));
        CREATE INDEX IF NOT EXISTS storage_pending ON storage_settlement(state,job_id);
        CREATE TABLE IF NOT EXISTS storage_refresh(job_id INTEGER PRIMARY KEY AUTOINCREMENT,
          root_id TEXT NOT NULL,object_id TEXT NOT NULL,remote_binding TEXT NOT NULL,due INTEGER NOT NULL,
          UNIQUE(root_id,object_id,remote_binding));
        CREATE INDEX IF NOT EXISTS storage_refresh_due ON storage_refresh(due,root_id,object_id,remote_binding);
        CREATE TABLE IF NOT EXISTS storage_cleanup(root_id TEXT NOT NULL,object_id TEXT NOT NULL,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        -- A move is implemented as delete+insert. Defer identity cleanup until publication completes.
        CREATE TRIGGER IF NOT EXISTS storage_deleted AFTER DELETE ON file_state
        WHEN EXISTS(SELECT 1 FROM storage_policy WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
          OR EXISTS(SELECT 1 FROM storage_item WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
          OR EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
        BEGIN
          INSERT OR IGNORE INTO storage_cleanup SELECT root_id,old.object_id FROM root WHERE root_no=old.root_no;
        END;
        CREATE TRIGGER IF NOT EXISTS storage_replaced AFTER UPDATE OF object_id ON file_state WHEN old.object_id<>new.object_id
        BEGIN
          INSERT OR IGNORE INTO storage_cleanup SELECT root_id,old.object_id FROM root WHERE root_no=old.root_no
          AND (EXISTS(SELECT 1 FROM storage_policy WHERE root_id=root.root_id AND object_id=old.object_id)
            OR EXISTS(SELECT 1 FROM storage_item WHERE root_id=root.root_id AND object_id=old.object_id)
            OR EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=root.root_id AND object_id=old.object_id));
        END;
        CREATE TRIGGER IF NOT EXISTS storage_remote_insert AFTER INSERT ON file_state
        BEGIN
          INSERT INTO storage_refresh(root_id,object_id,remote_binding,due)
          SELECT s.root_id,s.object_id,s.remote_binding,0 FROM storage_item s INDEXED BY storage_remote_object
          WHERE s.remote_binding=(SELECT root_id FROM root WHERE root_no=new.root_no)
            AND json_extract(s.remote_json,'$.Id')=new.object_id AND s.availability='OnlineOnly'
            AND (json_extract(s.remote_json,'$.Revision') IS NOT new.version_id)
          ON CONFLICT(root_id,object_id,remote_binding) DO UPDATE SET due=0,job_id=excluded.job_id;
        END;
        CREATE TRIGGER IF NOT EXISTS storage_remote_update AFTER UPDATE OF version_id ON file_state
        WHEN old.version_id IS NOT new.version_id
        BEGIN
          INSERT INTO storage_refresh(root_id,object_id,remote_binding,due)
          SELECT s.root_id,s.object_id,s.remote_binding,0 FROM storage_item s INDEXED BY storage_remote_object
          WHERE s.remote_binding=(SELECT root_id FROM root WHERE root_no=new.root_no)
            AND json_extract(s.remote_json,'$.Id')=new.object_id AND s.availability='OnlineOnly'
          ON CONFLICT(root_id,object_id,remote_binding) DO UPDATE SET due=0,job_id=excluded.job_id;
        END;
        """;
}

public sealed record StorageSettlement(long JobId, string RootId, string ObjectId, string RemoteBinding, PilotRemoteEntry Remote, long RefreshDue = 0);
public sealed record StorageObserved(string Availability, bool Pinned, bool InSync);
public sealed record StorageFolderSummary(string ObjectId, string Path, long Items, long Hydrated, long Dehydrated)
{
    public long Unknown => Items - Hydrated - Dehydrated;
}
