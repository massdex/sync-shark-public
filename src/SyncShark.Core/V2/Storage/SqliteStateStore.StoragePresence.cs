namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>Pair-local storage participation, including retained registration after the last policy is removed.
    /// Existing root/entity evidence is authoritative; schema availability is not a pair's opt-in flag.</summary>
    public bool PairUsesStorage(PairDef pair) => InStore(() =>
    {
        if (!IsStorageEnabled) return false; // Explicit projection must observe another process's opt-in.
        foreach (var root in new[] { pair.A, pair.B }.Where(root => root.Provider == SyncProviders.Local))
        {
            if (HasOwnedPointers(root.RootId) || HasStorageRootWork(root.RootId)) return true;
            if (Convert.ToInt64(StoreSchema.Scalar(_connection, null, """
                SELECT EXISTS(SELECT 1 FROM storage_policy WHERE root_id=$r)
                    OR EXISTS(SELECT 1 FROM storage_registration WHERE root_id=$r)
                    OR EXISTS(SELECT 1 FROM storage_item WHERE root_id=$r)
                    OR EXISTS(SELECT 1 FROM storage_settlement WHERE root_id=$r);
                    -- Retained settled receipts are evidence too; pending work belongs to HasStorageRootWork.
                """, ("$r", root.RootId))) != 0) return true;
        }
        return false;
    });
}
