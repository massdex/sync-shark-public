namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal bool InvalidateReconciliationPage(string rootId, string epoch, Func<ItemState, bool> eligible) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return false;
        using var tx = _connection.BeginTransaction();
        string after;
        bool legacy;
        using (var command = StoreSchema.Command(_connection, tx,
            "SELECT after_object,EXISTS(SELECT 1 FROM local_gap_item WHERE root_no=$r) FROM local_gap " +
            "WHERE root_no=$r AND epoch=$e AND trust_usn=0 AND discovery_done=0;"))
        {
            command.Parameters.AddWithValue("$r", root.Value); command.Parameters.AddWithValue("$e", epoch);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;
            after = reader.GetString(0); legacy = reader.GetInt64(1) != 0;
        }
        var ids = new List<string>();
        using (var command = StoreSchema.Command(_connection, tx,
            "SELECT DISTINCT f.object_id FROM file_state f WHERE f.root_no=$r AND f.kind=0 AND f.object_id>$after " +
            (legacy ? "AND EXISTS(SELECT 1 FROM local_gap_item g WHERE g.root_no=f.root_no AND g.object_id=f.object_id) " : "") +
            "ORDER BY f.object_id LIMIT $n;"))
        {
            command.Parameters.AddWithValue("$r", root.Value); command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$n", V2Settings.PlanningObservationBatchRows);
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        foreach (var id in ids)
            if (_reads.ByObject(root.Value, id) is { } item && eligible(item) && !item.Flags.HasFlag(ItemFlags.NeedsRehash))
                MarkRehash(root.Value, item, MessageCatalog.Default.Template("reconcile-journal-proof"), tx);
        var done = ids.Count < V2Settings.PlanningObservationBatchRows;
        StoreSchema.Execute(_connection, tx,
            "UPDATE local_gap SET after_object=$after,discovery_done=$done WHERE root_no=$r AND epoch=$e;",
            ("$after", ids.Count == 0 ? after : ids[^1]), ("$done", done ? 1 : 0), ("$r", root.Value), ("$e", epoch));
        tx.Commit();
        return !done;
    });
}
