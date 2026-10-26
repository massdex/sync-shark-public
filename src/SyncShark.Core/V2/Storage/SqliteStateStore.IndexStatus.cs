namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>Informational root-owned verification debt. Never participates in sync outcome.</summary>
    public long IndexingRemaining(string rootId) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return 0L;
        // Count entities once across the undiscovered frontier, staged gap work, and ordinary
        // rehash debt. This is a status/report query, not a per-page admission query.
        return Convert.ToInt64(StoreSchema.Scalar(_connection, null, """
            SELECT COUNT(*) FROM (
                SELECT g.object_id FROM local_gap_item g WHERE g.root_no=$r
                UNION
                SELECT f.object_id FROM file_state f INDEXED BY fs_obj
                JOIN local_gap g ON g.root_no=f.root_no
                WHERE f.root_no=$r AND g.discovery_done=0 AND f.object_id>g.after_object AND f.kind=0
                UNION
                SELECT q.object_id FROM local_rehash q WHERE q.root_no=$r AND EXISTS(
                    SELECT 1 FROM file_state f INDEXED BY fs_obj WHERE f.root_no=q.root_no AND f.object_id=q.object_id
                    AND (f.hash IS NULL OR (f.flags & 256)!=0))
            );
            """, ("$r", root.Value)));
    });
}
