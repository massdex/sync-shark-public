namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static System.Collections.Concurrent.ConcurrentQueue<string> TraceRehashProofs(string folder, SqliteStateStore store, Fixture fx, IReadOnlySet<string>? ids = null)
    {
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        store.RehashOrderForTest = (phase, root, id, order) =>
        {
            if (ids is null || ids.Contains(id)) trace.Enqueue($"phase={phase} root={root} object={id} order={order}");
        };
        fx.A.ContentProofCompletedForTest = (path, hash) =>
        {
            var item = store.Live(fx.Pair.A.RootId, [path]).Single();
            if (ids is not null && !ids.Contains(item.ObjectId)) return;
            trace.Enqueue($"proof path={path} returned={hash} blob={Convert.ToHexString(RowCodec.HashBlob(hash)!)} " +
                $"proofOrder={store.RehashProofOrder(fx.Pair.A.RootId, item.ObjectId, hash)} " +
                ProofAliasDiagnostics(folder, fx.Pair.A.RootId, item.ObjectId, hash));
        };
        return trace;
    }

    private static string ProofAliasDiagnostics(string folder, string root, string id, string hash)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        db.Open();
        using var command = StoreSchema.Command(db, null,
            "SELECT d.path,f.name,hex(f.hash),f.flags,q.last_attempt,(f.hash IS NOT $h) FROM root r " +
            "LEFT JOIN file_state f ON f.root_no=r.root_no AND f.object_id=$o LEFT JOIN dir d ON d.dir_no=f.dir_no " +
            "LEFT JOIN local_rehash q ON q.root_no=r.root_no AND q.object_id=$o WHERE r.root_id=$r;");
        command.Parameters.AddWithValue("$r", root); command.Parameters.AddWithValue("$o", id);
        command.Parameters.AddWithValue("$h", RowCodec.HashBlob(hash)!);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add($"alias={reader.GetValue(0)}/{reader.GetValue(1)} storedBlob={reader.GetValue(2)} " +
            $"flags={reader.GetValue(3)} order={reader.GetValue(4)} hashMismatch={reader.GetValue(5)} exists={!reader.IsDBNull(1)}");
        return string.Join(";", rows);
    }

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
