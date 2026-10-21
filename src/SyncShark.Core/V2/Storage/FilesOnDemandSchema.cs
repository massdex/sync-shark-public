namespace Massdex.SyncShark.V2;

// Presence-guarded additive schema: merge-safe with independently arriving schema slots.
internal static class FilesOnDemandSchema
{
    internal static bool IsEnabled(Microsoft.Data.Sqlite.SqliteConnection connection) =>
        StoreSchema.Scalar(connection, null, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='storage_policy';") is not null;

    internal static readonly string[] Names = [
        "storage_policy", "storage_item", "storage_settlement", "storage_pending", "storage_online_policy",
        "storage_path_intent", "storage_intent_due", "storage_policy_sweep", "storage_remote_object",
        "storage_refresh", "storage_refresh_due", "storage_cleanup", "storage_registration", "fs_owned",
        "storage_subtree_sweep", "storage_root_backoff", "storage_pending_root", "storage_refresh_root",
        "storage_hydration", "storage_hydration_root", "storage_hydration_due", "fs_nonpointer",
        "storage_pending_requested", "storage_hydration_requested", "storage_refresh_requested", "fs_pointer_verify"
    ];

    internal static void Ensure(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var legacyPeriodic = IsEnabled(connection) && StoreSchema.Scalar(connection, null, "SELECT 1 FROM sqlite_master WHERE name='storage_root_backoff';") is null;
        using var tx = connection.BeginTransaction();
        // Pre-1024 opt-in builds defined both pointer indexes with OwnedPointer=512. CREATE INDEX IF NOT EXISTS
        // cannot replace those definitions, so such a store needs its rows moved and both indexes rebuilt.
        // Only this index evidence proves 512 meant OwnedPointer here: in a current store 512 is RequiresExport,
        // which must never be migrated. Detect before the Ddl replay, merge within the same transaction.
        var legacyOwned = StoreSchema.Scalar(connection, tx,
            "SELECT 1 FROM sqlite_master WHERE name IN ('fs_owned','fs_nonpointer') AND instr(sql,'512')>0 LIMIT 1;") is not null;
        StoreSchema.Execute(connection, tx, Ddl);
        if (legacyOwned)
            StoreSchema.Execute(connection, tx,
                "UPDATE file_state SET flags=(flags & ~512)|1024 WHERE (flags & 512)!=0;" +
                "DROP INDEX IF EXISTS fs_owned; DROP INDEX IF EXISTS fs_nonpointer;" +
                "CREATE INDEX fs_owned ON file_state(root_no) WHERE (flags & 1024) != 0;" +
                "CREATE INDEX fs_nonpointer ON file_state(root_no) WHERE (flags & ~1024) != 0;");
        // Additive lazy migration: existing opted-in stores gain item-scoped retry scheduling without a schema version.
        if (StoreSchema.Scalar(connection, tx, "SELECT 1 FROM pragma_table_info('storage_settlement') WHERE name='due';") is null)
            StoreSchema.Execute(connection, tx, "ALTER TABLE storage_settlement ADD COLUMN due INTEGER NOT NULL DEFAULT 0;");
        if (StoreSchema.Scalar(connection, tx, "SELECT 1 FROM pragma_table_info('storage_settlement') WHERE name='kind';") is null)
            StoreSchema.Execute(connection, tx, "ALTER TABLE storage_settlement ADD COLUMN kind TEXT;");
        // Must follow the due-column migration for older opted-in stores.
        StoreSchema.Execute(connection, tx, RequestedIndexesDdl);
        if (legacyPeriodic) StoreSchema.Execute(connection, tx, "DELETE FROM storage_refresh WHERE due>0;");
        // Preview builds changed the shared index. Restore its final26 predicate only on opted-in stores.
        // Both the 512-era and the 1024-era preview predicates must be repaired; a current store has flags != 0.
        if (StoreSchema.Scalar(connection, tx, "SELECT sql FROM sqlite_master WHERE name='fs_flagged';") is string sql &&
            (sql.Contains("~512", StringComparison.Ordinal) || sql.Contains("~1024", StringComparison.Ordinal)))
            StoreSchema.Execute(connection, tx, "DROP INDEX fs_flagged; CREATE INDEX fs_flagged ON file_state(root_no) WHERE flags != 0;");
        // Resume legacy whole-root sweeps once; new commands create entity-scoped sweeps.
        StoreSchema.Execute(connection, tx, "INSERT OR IGNORE INTO storage_subtree_sweep(root_id,object_id) SELECT root_id,'' FROM storage_policy_sweep; DELETE FROM storage_policy_sweep;");
        tx.Commit();
    }

    // Literal predicate is required for SQLite partial-index matching; keep in sync with StorageDue.UserRequested.
    internal const string RequestedIndexesDdl = """
        CREATE INDEX IF NOT EXISTS storage_pending_requested ON storage_settlement(root_id,job_id) WHERE due=-1 AND state='pending';
        CREATE INDEX IF NOT EXISTS storage_hydration_requested ON storage_hydration(root_id,job_id) WHERE due=-1;
        CREATE INDEX IF NOT EXISTS storage_refresh_requested ON storage_refresh(root_id,object_id,remote_binding) WHERE due=-1;
        """;

    internal const string Ddl = """
        CREATE INDEX IF NOT EXISTS fs_nonpointer ON file_state(root_no) WHERE (flags & ~1024) != 0;
        CREATE INDEX IF NOT EXISTS fs_owned ON file_state(root_no) WHERE (flags & 1024) != 0;
        -- Owned pointers that also carry content-verification debt; the literal predicate matches the queries.
        CREATE INDEX IF NOT EXISTS fs_pointer_verify ON file_state(root_no) WHERE (flags & 1280) = 1280;
        CREATE TABLE IF NOT EXISTS storage_root_backoff(root_id TEXT PRIMARY KEY,due INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS storage_subtree_sweep(root_id TEXT NOT NULL,object_id TEXT NOT NULL,
          after_dir_key TEXT NOT NULL DEFAULT '',after_dir INTEGER NOT NULL DEFAULT -1,
          after_name_key TEXT NOT NULL DEFAULT '',after_name TEXT NOT NULL DEFAULT '',scope_key TEXT NOT NULL DEFAULT '',revision INTEGER NOT NULL DEFAULT 1,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS storage_hydration(job_id INTEGER PRIMARY KEY AUTOINCREMENT,
          root_id TEXT NOT NULL,object_id TEXT NOT NULL,due INTEGER NOT NULL DEFAULT 0,UNIQUE(root_id,object_id));
        CREATE INDEX IF NOT EXISTS storage_hydration_root ON storage_hydration(root_id,job_id,due);
        CREATE INDEX IF NOT EXISTS storage_hydration_due ON storage_hydration(due,root_id,job_id);
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
          due INTEGER NOT NULL DEFAULT 0,
          UNIQUE(root_id,object_id,remote_binding));
        CREATE INDEX IF NOT EXISTS storage_pending_root ON storage_settlement(root_id,state,job_id);
        CREATE INDEX IF NOT EXISTS storage_pending ON storage_settlement(state,job_id);
        CREATE TABLE IF NOT EXISTS storage_refresh(job_id INTEGER PRIMARY KEY AUTOINCREMENT,
          root_id TEXT NOT NULL,object_id TEXT NOT NULL,remote_binding TEXT NOT NULL,due INTEGER NOT NULL,
          UNIQUE(root_id,object_id,remote_binding));
        CREATE INDEX IF NOT EXISTS storage_refresh_root ON storage_refresh(root_id,due,object_id,remote_binding);
        CREATE INDEX IF NOT EXISTS storage_refresh_due ON storage_refresh(due,root_id,object_id,remote_binding);
        CREATE TABLE IF NOT EXISTS storage_cleanup(root_id TEXT NOT NULL,object_id TEXT NOT NULL,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        -- A move is implemented as delete+insert. Defer identity cleanup until publication completes.
        CREATE TRIGGER IF NOT EXISTS storage_deleted AFTER DELETE ON file_state
        WHEN EXISTS(SELECT 1 FROM storage_policy WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
          OR EXISTS(SELECT 1 FROM storage_item WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
          OR EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
          OR EXISTS(SELECT 1 FROM storage_hydration WHERE root_id=(SELECT root_id FROM root WHERE root_no=old.root_no) AND object_id=old.object_id)
        BEGIN
          INSERT OR IGNORE INTO storage_cleanup SELECT root_id,old.object_id FROM root WHERE root_no=old.root_no;
        END;
        CREATE TRIGGER IF NOT EXISTS storage_replaced AFTER UPDATE OF object_id ON file_state WHEN old.object_id<>new.object_id
        BEGIN
          INSERT OR IGNORE INTO storage_cleanup SELECT root_id,old.object_id FROM root WHERE root_no=old.root_no
          AND (EXISTS(SELECT 1 FROM storage_policy WHERE root_id=root.root_id AND object_id=old.object_id)
            OR EXISTS(SELECT 1 FROM storage_item WHERE root_id=root.root_id AND object_id=old.object_id)
            OR EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=root.root_id AND object_id=old.object_id)
            OR EXISTS(SELECT 1 FROM storage_hydration WHERE root_id=root.root_id AND object_id=old.object_id));
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

public sealed record StorageSettlement(long JobId, string RootId, string ObjectId, string RemoteBinding, PilotRemoteEntry Remote, long RefreshDue = 0, string? Kind = null)
{
    public const string AcknowledgeMove = "acknowledge-move";
}
public sealed record StorageObserved(string Availability, bool Pinned, bool InSync);
public sealed record StorageFolderSummary(string ObjectId, string Path, long Items, long Hydrated, long Dehydrated)
{
    public long Unknown => Items - Hydrated - Dehydrated;
}

public sealed record StorageHydrationJob(long JobId, string RootId, string ObjectId);
public sealed record StoragePathIntent(string Path, string Mode);
