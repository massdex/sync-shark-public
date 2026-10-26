namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static string RehashDiagnostics(string folder, PairDef pair)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        db.Open();
        using var command = StoreSchema.Command(db, null,
            "SELECT r.root_id,q.object_id,d.path,f.name,f.flags,f.object_id,q.last_attempt FROM local_rehash q " +
            "JOIN root r ON r.root_no=q.root_no LEFT JOIN file_state f ON f.root_no=q.root_no AND f.object_id=q.object_id " +
            "LEFT JOIN dir d ON d.dir_no=f.dir_no;");
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"side={(reader.GetString(0) == pair.A.RootId ? Side.A : Side.B)} object={reader.GetString(1)} " +
                $"path={reader.GetValue(2)}/{reader.GetValue(3)} flags={reader.GetValue(4)} in-file-state={!reader.IsDBNull(5)} order={reader.GetInt64(6)}");
        return string.Join("; ", rows);
    }
}
