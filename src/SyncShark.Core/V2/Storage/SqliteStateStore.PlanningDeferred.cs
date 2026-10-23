using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Entity-owned work, not a path hold: queued work must not protect itself forever.
    // Pair is a consumer of the entity's work; path is only its last observed spelling.
    internal sealed record DeferredPlanning(long Root, string ObjectId, string Path, long Version,
        IReadOnlyList<string> Paths, bool AllAliases, string RootId = "", string Reason = "Waiting for content verification",
        long AliasDir = -1, string AliasKey = "", string AliasName = "", bool AliasUnsettled = false,
        long NextAliasDir = -1, string NextAliasKey = "", string NextAliasName = "");

    // Pair-leading index supports both counting and ordered entity keyset seeks.
    internal const string DeferredPlanningDisplaySql = """
        SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason
        FROM planning_deferred d
        JOIN root r ON r.root_no=d.root_no WHERE d.pair_no=$p
          AND (d.root_no,d.object_id)>($root,$id) AND d.version<$version
        ORDER BY d.root_no,d.object_id LIMIT 256;
        """;

    // Run intake: oldest queued work first, served by planning_deferred_pair_version so the read is
    // one bounded indexed page, never a sort and never the whole pair.
    internal const string DeferredPlanningIntakeSql = """
        SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason,
          d.alias_dir,d.alias_key,d.alias_name,d.alias_unsettled
        FROM planning_deferred d INDEXED BY planning_deferred_pair_version
        JOIN root r ON r.root_no=d.root_no
        WHERE d.pair_no=$p AND d.version<$before
        ORDER BY d.version,d.root_no,d.object_id LIMIT $limit;
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
                WritePlanningDirty(tx, pair.Value, System.Text.Json.JsonSerializer.Serialize(rows),
                    "Waiting for content verification");
                tx.Commit();
            });
    }

    // Inventory publication and its planning handoff share the caller's transaction. Every pair
    // consuming this root gets the entity's work; aliases are resolved at bounded intake time.
    internal void MarkPlanningDirty(SqliteTransaction tx, long root,
        IReadOnlyList<(string ObjectId, string Path)> items)
    {
        if (items.Count == 0) return;
        var rows = System.Text.Json.JsonSerializer.Serialize(items.Select(item =>
            new { root, id = item.ObjectId, path = item.Path }));
        foreach (var pair in RootSides(root, tx).Select(binding => binding.PairNo).Distinct())
            WritePlanningDirty(tx, pair, rows, "Waiting for planning after inventory observation");
    }

    private void WritePlanningDirty(SqliteTransaction tx, long pair, string rows, string reason) =>
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,reason)
            SELECT json_extract(value,'$.root'),json_extract(value,'$.id'),$p,
              json_extract(value,'$.path'),$version,$reason FROM json_each($rows) WHERE 1
            ON CONFLICT(root_no,object_id,pair_no) DO UPDATE SET
              path=excluded.path,reason=excluded.reason,version=MAX(planning_deferred.version+1,excluded.version),
              alias_dir=-1,alias_key='',alias_name='',alias_unsettled=0;
            """, ("$p", pair), ("$version", DateTime.UtcNow.Ticks), ("$reason", reason), ("$rows", rows));

    // Read-only Show remaining projection. It does not admit aliases or acknowledge work.
    internal IReadOnlyList<DeferredPlanning> DeferredPlanningDisplayPage(string pairId, long afterRoot = -1, string afterId = "", long beforeVersion = long.MaxValue) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return (IReadOnlyList<DeferredPlanning>)Array.Empty<DeferredPlanning>();
        var page = new List<DeferredPlanning>();
        using var tx = _connection.BeginTransaction();
        using (var command = StoreSchema.Command(_connection, tx, DeferredPlanningDisplaySql))
        {
            command.Parameters.AddWithValue("$p", pair.Value);
            command.Parameters.AddWithValue("$root", afterRoot);
            command.Parameters.AddWithValue("$id", afterId);
            command.Parameters.AddWithValue("$version", beforeVersion);
            using var reader = command.ExecuteReader();
            while (reader.Read()) page.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), [], true, reader.GetString(4), reader.GetString(5)));
        }
        tx.Commit();
        return page;
    });

    // Hard run bounds: one oldest-first page, never the whole queue.
    private const int DeferredIntakeEntityCap = 256;
    private const int DeferredIntakePathCap = 1024;

    /// <summary>
    /// One run's bounded intake: oldest first through the pair/version/root/id index, 256 entities
    /// and 1024 alias paths at most. Admission rotates the admitted versions behind every unadmitted
    /// eligible row (below the run cutoff) in the same transaction, so held work cannot starve later
    /// entities across runs or restarts; the returned rotated version matches the existing exact
    /// acknowledgement. Read-only callers pass <c>rotate: false</c> and write nothing.
    /// </summary>
    internal IReadOnlyList<DeferredPlanning> AdmitDeferredPlanning(string pairId, long beforeVersion,
        bool rotate = true) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return (IReadOnlyList<DeferredPlanning>)Array.Empty<DeferredPlanning>();
        var rows = new List<DeferredPlanning>();
        using var tx = _connection.BeginTransaction();
        using (var command = StoreSchema.Command(_connection, tx, DeferredPlanningIntakeSql))
        {
            command.Parameters.AddWithValue("$p", pair.Value);
            command.Parameters.AddWithValue("$before", beforeVersion);
            command.Parameters.AddWithValue("$limit", DeferredIntakeEntityCap);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), [], true, reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9) != 0));
        }
        var totalPaths = 0;
        var admitted = new List<DeferredPlanning>(rows.Count);
        foreach (var row in rows)
        {
            var remaining = DeferredIntakePathCap - totalPaths;
            if (remaining <= 0) break;
            var materialized = MaterializeAliases(tx, row, remaining);
            totalPaths += materialized.Paths.Count;
            admitted.Add(materialized);
        }
        if (rotate && admitted.Count > 0)
        {
            // FIFO rotation: admitted versions move above the unadmitted eligible queue, keeping
            // their admission order. Checked arithmetic keeps versions monotonic; an impossible
            // overflow throws before any update instead of lowering a selected version.
            var start = checked(beforeVersion + 1);
            var rotated = admitted.Select((row, index) => new
            {
                root = row.Root, id = row.ObjectId, version = row.Version, rotated = checked(start + index)
            }).ToArray();
            StoreSchema.Execute(_connection, tx, """
                UPDATE planning_deferred AS d SET version=j.rotated
                FROM (SELECT json_extract(value,'$.root') root,json_extract(value,'$.id') id,
                        json_extract(value,'$.version') version,json_extract(value,'$.rotated') rotated
                      FROM json_each($rows)) j
                WHERE d.pair_no=$p AND d.root_no=j.root AND d.object_id=j.id AND d.version=j.version;
                """, ("$p", pair.Value), ("$rows", System.Text.Json.JsonSerializer.Serialize(rotated)));
            for (var index = 0; index < admitted.Count; index++)
                admitted[index] = admitted[index] with { Version = rotated[index].rotated };
        }
        tx.Commit();
        return admitted;
    });

    /// <summary>
    /// The entity's own queued path plus its current aliases, bounded by the remaining run budget.
    /// <c>AllAliases</c> is false whenever the budget cut the list, so the existing acknowledgement
    /// rule keeps partially materialized work queued.
    /// </summary>
    private DeferredPlanning MaterializeAliases(SqliteTransaction tx, DeferredPlanning row, int remaining)
    {
        var paths = new List<string>();
        if (row.AliasDir < 0) paths.Add(row.Path);
        var capacity = remaining - paths.Count;
        var nextDir = row.AliasDir;
        var nextKey = row.AliasKey;
        var nextName = row.AliasName;
        var all = true;
        using var command = StoreSchema.Command(_connection, tx,
            "SELECT d.path,f.name,f.dir_no,f.name_key FROM file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.object_id=$o AND (f.dir_no,f.name_key,f.name)>($dir,$key,$name) " +
            "ORDER BY f.dir_no,f.name_key,f.name LIMIT $limit;");
        command.Parameters.AddWithValue("$r", row.Root);
        command.Parameters.AddWithValue("$o", row.ObjectId);
        command.Parameters.AddWithValue("$dir", row.AliasDir);
        command.Parameters.AddWithValue("$key", row.AliasKey);
        command.Parameters.AddWithValue("$name", row.AliasName);
        command.Parameters.AddWithValue("$limit", capacity + 1);
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (capacity-- == 0) { all = false; break; }
                paths.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)));
                nextDir = reader.GetInt64(2);
                nextKey = reader.GetString(3);
                nextName = reader.GetString(1);
            }
        return row with { Paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), AllAliases = all,
            NextAliasDir = nextDir, NextAliasKey = nextKey, NextAliasName = nextName };
    }

    internal void AcknowledgeDeferredPlanning(string pairId, IReadOnlyList<DeferredPlanning> page,
        Func<string, bool> settled, Func<string, bool>? emptyPath = null) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return;
        // Settlement may read current inventory/baselines through the store. Evaluate under
        // its gate before opening the write transaction; newer work is still version-guarded.
        // An empty-path retirement is structural: it may only retire work for an entity that has
        // no current row on its root, because the alias list was captured before this run's scan.
        var rows = page.Select(row => new { Row = row, Settled = row.Paths.All(settled) ||
            (emptyPath is not null && row.Paths.All(emptyPath) && _reads.ByObject(row.Root, row.ObjectId) is null) }).ToArray();
        using var tx = _connection.BeginTransaction();
        foreach (var entry in rows)
        {
            var row = entry.Row;
            if (row.AllAliases && entry.Settled && !row.AliasUnsettled)
                StoreSchema.Execute(_connection, tx,
                    "DELETE FROM planning_deferred WHERE pair_no=$p AND root_no=$r AND object_id=$o AND version=$v;",
                    ("$p", pair.Value), ("$r", row.Root), ("$o", row.ObjectId), ("$v", row.Version));
            else
                // Finish every bounded alias slice even if one alias is held. Keep a cycle's
                // unresolved bit; only a complete successful cycle may retire the entity.
                // An interrupted run never calls this acknowledgement, so its slice replays.
                StoreSchema.Execute(_connection, tx, """
                    UPDATE planning_deferred SET alias_dir=$dir,alias_key=$key,alias_name=$name,alias_unsettled=$held
                    WHERE pair_no=$p AND root_no=$r AND object_id=$o AND version=$v;
                    """, ("$p", pair.Value), ("$r", row.Root), ("$o", row.ObjectId), ("$v", row.Version),
                    ("$dir", row.AllAliases ? -1 : row.NextAliasDir), ("$key", row.AllAliases ? "" : row.NextAliasKey),
                    ("$name", row.AllAliases ? "" : row.NextAliasName),
                    ("$held", !row.AllAliases && (row.AliasUnsettled || !entry.Settled) ? 1 : 0));
        }
        tx.Commit();
    });

    internal int CountDeferredPlanning(string pairId) => InStore(() => (int)Math.Min(int.MaxValue,
        Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM planning_deferred WHERE pair_no=$p;", ("$p", PairNo(pairId))))));
}
