namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static string ReconciliationCompletionDiagnostics(string folder, PairDef pair)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        db.Open();
        var rows = new List<string>();
        using (var command = StoreSchema.Command(db, null,
            "SELECT r.root_id,g.inventory_pending,g.after_object,g.discovery_done,g.worker_after,g.worker_done," +
            "(SELECT COUNT(*) FROM local_gap_item i WHERE i.root_no=g.root_no) FROM local_gap g JOIN root r ON r.root_no=g.root_no;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add($"side={(reader.GetString(0) == pair.A.RootId ? Side.A : Side.B)} " +
                $"pending={reader.GetValue(1)} after={System.Text.Json.JsonSerializer.Serialize(reader.GetString(2))} " +
                $"discoveryDone={reader.GetValue(3)} workerAfter={System.Text.Json.JsonSerializer.Serialize(reader.GetString(4))} " +
                $"workerDone={reader.GetValue(5)} gapItems={reader.GetValue(6)}");
        using (var command = StoreSchema.Command(db, null,
            "SELECT r.root_id,d.object_id,d.path,d.version,d.reason FROM planning_deferred d JOIN root r ON r.root_no=d.root_no ORDER BY d.root_no,d.object_id LIMIT 100;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add($"deferred side={(reader.GetString(0) == pair.A.RootId ? Side.A : Side.B)} " +
                $"object={reader.GetValue(1)} path={reader.GetValue(2)} version={reader.GetValue(3)} reason={reader.GetValue(4)}");
        return string.Join(" | ", rows);
    }
}
