namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Entity-owned work, not a path hold: queued work must not protect itself forever.
    // Pair is a consumer of the entity's work; path is only its last observed spelling.
    internal sealed record DeferredPlanning(long Root, string ObjectId, string Path, long Version,
        IReadOnlyList<string> Paths, bool AllAliases, string RootId = "", string Reason = "Waiting for content verification");

    // Unary + keeps the consumer filter exact but excludes it as an index-search term.
    // The entity keyset then seeks naturally on the PK, without relying on an autoindex name
    // or sorting the whole pair through the historical due index on every page.
    internal const string DeferredPlanningPageSql = """
        SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason
        FROM planning_deferred d
        JOIN root r ON r.root_no=d.root_no WHERE +d.pair_no=$p
          AND (d.root_no,d.object_id)>($root,$id) AND d.version<$version
        ORDER BY d.root_no,d.object_id LIMIT 256;
        """;

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
                    INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,last_attempt,reason)
                    SELECT json_extract(value,'$.root'),json_extract(value,'$.id'),$p,
                      json_extract(value,'$.path'),$version,0,'Waiting for content verification' FROM json_each($rows) WHERE 1
                    ON CONFLICT(root_no,object_id,pair_no) DO UPDATE SET
                      path=excluded.path,reason=excluded.reason,version=MAX(planning_deferred.version+1,excluded.version);
                    """, ("$p", pair.Value), ("$version", DateTime.UtcNow.Ticks), ("$rows", System.Text.Json.JsonSerializer.Serialize(rows)));
                tx.Commit();
            });
    }

    // A fair page. Current aliases follow the entity through a move; the old path also
    // returns so a removed/moved baseline is reconsidered. No root-sized managed queue.
    internal IEnumerable<IReadOnlyList<DeferredPlanning>> DeferredPlanningPages(string pairId, long beforeVersion = long.MaxValue)
    {
        long root = -1;
        var id = "";
        while (true)
        {
            var page = DeferredPlanningPage(pairId, root, id, beforeVersion);
            if (page.Count == 0) yield break;
            yield return page;
            root = page[^1].Root;
            id = page[^1].ObjectId;
        }
    }

    internal IReadOnlyList<DeferredPlanning> DeferredPlanningPage(string pairId, long afterRoot = -1, string afterId = "", long beforeVersion = long.MaxValue) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return (IReadOnlyList<DeferredPlanning>)Array.Empty<DeferredPlanning>();
        var page = new List<DeferredPlanning>();
        using var tx = _connection.BeginTransaction();
        using (var command = StoreSchema.Command(_connection, tx, DeferredPlanningPageSql))
        {
            command.Parameters.AddWithValue("$p", pair.Value);
            command.Parameters.AddWithValue("$root", afterRoot);
            command.Parameters.AddWithValue("$id", afterId);
            command.Parameters.AddWithValue("$version", beforeVersion);
            using var reader = command.ExecuteReader();
            while (reader.Read()) page.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), [], true, reader.GetString(4), reader.GetString(5)));
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
            // Reads do not write history or reschedule work; keyset paging visits every entity.
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
        var rows = page.Where(row => row.AllAliases && row.Paths.All(settled))
            .Select(row => new { root = row.Root, id = row.ObjectId, version = row.Version }).ToArray();
        StoreSchema.Execute(_connection, tx, """
            DELETE FROM planning_deferred WHERE pair_no=$p AND (root_no,object_id,version) IN (
              SELECT json_extract(value,'$.root'),json_extract(value,'$.id'),json_extract(value,'$.version')
              FROM json_each($rows));
            """, ("$p", pair.Value), ("$rows", System.Text.Json.JsonSerializer.Serialize(rows)));
        tx.Commit();
    });

    internal int CountDeferredPlanning(string pairId) => InStore(() => (int)Math.Min(int.MaxValue,
        Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM planning_deferred WHERE pair_no=$p;", ("$p", PairNo(pairId))))));
}
