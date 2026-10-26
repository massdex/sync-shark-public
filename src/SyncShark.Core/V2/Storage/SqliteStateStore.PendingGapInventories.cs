namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private void PublishGapInventory(Microsoft.Data.Sqlite.SqliteTransaction transaction, long root,
        string journalCursor, bool protect)
    {
        if (!protect)
            StoreSchema.Execute(_connection, transaction,
                "UPDATE local_gap SET discovery_done=1,worker_done=1,after_object=char(65535) WHERE root_no=$r;",
                ("$r", root));
        // Publish the pre-walk checkpoint with the obligation in the epoch's transaction;
        // later journal changes must remain available for ordinary replay.
        if (NtfsUsnCheckpoint.TryParseCursor(journalCursor, out _, out _))
            StoreSchema.Execute(_connection, transaction,
                "UPDATE local_gap SET inventory_pending=1 WHERE root_no=$r;" +
                "UPDATE root SET cursor=$c WHERE root_no=$r;",
                ("$r", root), ("$c", journalCursor));
    }

    // Root-sized restart work, not a walk through the per-file legacy verification queue.
    internal IReadOnlyList<string> PendingGapInventories() => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT r.root_id FROM local_gap g JOIN root r ON r.root_no=g.root_no WHERE g.inventory_pending=1 ORDER BY r.root_id;");
        using var reader = command.ExecuteReader();
        var roots = new List<string>();
        while (reader.Read()) roots.Add(reader.GetString(0));
        return roots;
    });
}
