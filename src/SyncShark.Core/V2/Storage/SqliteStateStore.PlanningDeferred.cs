namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Entity-owned work, not a path hold: queued work must not protect itself forever.
    // Pair is a consumer of the entity's work; path is only its last observed spelling.
    internal sealed record DeferredPlanning(long Root, string ObjectId, string Path, long Version,
        IReadOnlyList<string> Paths, bool AllAliases);

    internal void DeferPlanning(string pairId, IEnumerable<(string Root, ItemState Item)> items)
    {
        foreach (var batch in items.Chunk(256))
            InStore(() =>
            {
                var pair = PairNo(pairId);
                if (pair is null) return;
                using var tx = _connection.BeginTransaction();
                var rows = batch.Select(row => new { root = RootNo(row.Root), id = row.Item.ObjectId, path = row.Item.Path })
                    .Where(row => row.root is not null).ToArray();
                StoreSchema.Execute(_connection, tx, """
                    INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,last_attempt)
                    SELECT json_extract(value,'$.root'),json_extract(value,'$.id'),$p,
                      json_extract(value,'$.path'),1,0 FROM json_each($rows) WHERE 1
                    ON CONFLICT(root_no,object_id,pair_no) DO UPDATE SET
                      path=excluded.path,version=planning_deferred.version+1;
                    """, ("$p", pair.Value), ("$rows", System.Text.Json.JsonSerializer.Serialize(rows)));
                tx.Commit();
            });
    }

    // A fair page. Current aliases follow the entity through a move; the old path also
    // returns so a removed/moved baseline is reconsidered. No root-sized managed queue.
    internal IReadOnlyList<DeferredPlanning> DeferredPlanningPage(string pairId) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return (IReadOnlyList<DeferredPlanning>)Array.Empty<DeferredPlanning>();
        var page = new List<DeferredPlanning>();
        using var tx = _connection.BeginTransaction();
        using (var command = StoreSchema.Command(_connection, tx,
            "SELECT root_no,object_id,path,version FROM planning_deferred WHERE pair_no=$p " +
            "ORDER BY last_attempt,root_no,object_id LIMIT 256;"))
        {
            command.Parameters.AddWithValue("$p", pair.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read()) page.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), [], true));
        }
        var totalPaths = 0;
        var admitted = new List<DeferredPlanning>();
        foreach (var row in page)
        {
            if (totalPaths >= 1024) break;
            var paths = new List<string> { row.Path };
            using var command = StoreSchema.Command(_connection, tx,
                "SELECT d.path,f.name FROM file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no=f.dir_no " +
                "WHERE f.root_no=$r AND f.object_id=$o LIMIT $limit;");
            command.Parameters.AddWithValue("$r", row.Root);
            command.Parameters.AddWithValue("$o", row.ObjectId);
            command.Parameters.AddWithValue("$limit", 1025 - totalPaths);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) paths.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)));
            var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var all = paths.Count < 1026 - totalPaths && unique.Length <= 1024 - totalPaths;
            var bounded = unique.Take(1024 - totalPaths).ToArray();
            totalPaths += bounded.Length;
            admitted.Add(row with { Paths = bounded, AllAliases = all });
            StoreSchema.Execute(_connection, tx,
                "UPDATE planning_deferred SET last_attempt=$now WHERE root_no=$r AND object_id=$o AND pair_no=$p;",
                ("$now", DateTime.UtcNow.Ticks), ("$r", row.Root), ("$o", row.ObjectId), ("$p", pair.Value));
        }
        tx.Commit();
        return admitted;
    });

    internal void AcknowledgeDeferredPlanning(string pairId, IReadOnlyList<DeferredPlanning> page,
        Func<string, bool> settled) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return;
        using var tx = _connection.BeginTransaction();
        foreach (var row in page)
            if (row.AllAliases && row.Paths.All(settled))
                StoreSchema.Execute(_connection, tx,
                    "DELETE FROM planning_deferred WHERE root_no=$r AND object_id=$o AND pair_no=$p AND version=$v;",
                    ("$r", row.Root), ("$o", row.ObjectId), ("$p", pair.Value), ("$v", row.Version));
        tx.Commit();
    });

    internal int CountDeferredPlanning(string pairId) => InStore(() => (int)Math.Min(int.MaxValue,
        Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM planning_deferred WHERE pair_no=$p;", ("$p", PairNo(pairId))))));
}
