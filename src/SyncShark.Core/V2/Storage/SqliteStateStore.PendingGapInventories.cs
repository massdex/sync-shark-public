namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
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
