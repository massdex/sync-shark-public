namespace Massdex.SyncShark.V2;

// Presence-guarded additive schema: merge-safe with independently arriving schema slots.
internal static class FilesOnDemandSchema
{
    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS storage_policy(root_id TEXT NOT NULL, object_id TEXT NOT NULL,
          mode TEXT NOT NULL CHECK(mode IN ('offline','online-only')), revision INTEGER NOT NULL,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS storage_item(root_id TEXT NOT NULL, object_id TEXT NOT NULL,
          remote_binding TEXT NOT NULL, remote_json TEXT NOT NULL, availability TEXT NOT NULL,
          pinned INTEGER NOT NULL, in_sync INTEGER NOT NULL,
          PRIMARY KEY(root_id,object_id,remote_binding)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS storage_settlement(job_id INTEGER PRIMARY KEY AUTOINCREMENT,
          root_id TEXT NOT NULL, object_id TEXT NOT NULL, remote_binding TEXT NOT NULL,
          remote_json TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending',
          UNIQUE(root_id,object_id,remote_binding));
        CREATE INDEX IF NOT EXISTS storage_pending ON storage_settlement(state,job_id);
        """;
}

public sealed record StorageFolderPolicy(string ObjectId, string Path, string Mode, long Revision);
public sealed record StorageSettlement(long JobId, string RootId, string ObjectId, string RemoteBinding, PilotRemoteEntry Remote);
public sealed record StorageObserved(string Availability, bool Pinned, bool InSync);
public sealed record StorageFolderSummary(string ObjectId, string Path, long Items, long Hydrated, long Dehydrated)
{
    public long Unknown => Items - Hydrated - Dehydrated;
}
