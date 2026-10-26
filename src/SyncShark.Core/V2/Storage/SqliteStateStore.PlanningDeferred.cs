using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Entity-owned work, not a path hold: queued work must not protect itself forever.
    // Pair is a consumer of the entity's work; path is only its last observed spelling.
    internal sealed record DeferredPlanning(long Root, string ObjectId, string Path, long Version,
        IReadOnlyList<string> Paths, bool AllAliases, string RootId = "", string Reason = VerificationWaitReason,
        long AliasDir = -1, string AliasKey = "", string AliasName = "", bool AliasUnsettled = false,
        long NextAliasDir = -1, string NextAliasKey = "", string NextAliasName = "");

    internal const string ObservationPlanningReason = "Waiting for planning after inventory observation";
    internal const int SettleCandidateCap = V2Settings.SettleCandidateCap;

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

    // One identity/version seek per admitted entity. A JSON UPDATE FROM lets SQLite choose
    // the pair/version index as its outer loop and re-scan the JSON page for every queued row.
    internal const string DeferredPlanningRotateSql = """
        UPDATE planning_deferred SET version=$next
        WHERE root_no=$root AND object_id=$id AND pair_no=$pair AND version=$version;
        """;

    internal void DeferPlanning(string pairId, IEnumerable<(string Root, ItemState Item)> items, bool urgent = false)
    {
        foreach (var batch in items.Chunk(DeferredIntakeEntityCap))
            InStore(() =>
            {
                var pair = PairNo(pairId);
                if (pair is null) return;
                using var tx = _connection.BeginTransaction();
                var rows = batch.Select(row => new { root = RootNo(row.Root), id = row.Item.ObjectId, path = row.Item.Path })
                    .Where(row => row.root is not null).ToArray();
                WritePlanningDirty(tx, pair.Value, System.Text.Json.JsonSerializer.Serialize(rows),
                    urgent ? UserChangeReason : VerificationWaitReason);
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
            WritePlanningDirty(tx, pair, rows, ObservationPlanningReason);
    }

    private void WritePlanningDirty(SqliteTransaction tx, long pair, string rows, string reason) =>
        StoreSchema.Execute(_connection, tx, """
            INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,reason)
            SELECT json_extract(value,'$.root'),json_extract(value,'$.id'),$p,
              json_extract(value,'$.path'),$version,$reason FROM json_each($rows) WHERE 1
            ON CONFLICT(root_no,object_id,pair_no) DO UPDATE SET
              path=excluded.path,reason=CASE WHEN planning_deferred.reason=$user THEN planning_deferred.reason ELSE excluded.reason END,version=MAX(planning_deferred.version+1,excluded.version),
              alias_dir=-1,alias_key='',alias_name='',alias_unsettled=0;
            """, ("$user", UserChangeReason), ("$p", pair), ("$version", DateTime.UtcNow.Ticks), ("$reason", reason), ("$rows", rows));

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
    internal const int DeferredIntakeEntityCap = 2048;
    private const int DeferredIntakePathCap = 8192;

    /// <summary>
    /// One run's bounded intake: oldest first through the pair/version/root/id index, 2048 entities
    /// and 8192 alias paths at most. Admission rotates the admitted versions behind every unadmitted
    /// eligible row (below the run cutoff) in the same transaction, so held work cannot starve later
    /// entities across runs or restarts; the returned rotated version matches the existing exact
    /// acknowledgement. Read-only callers pass <c>rotate: false</c> and write nothing.
    /// </summary>
    internal IReadOnlyList<DeferredPlanning> AdmitDeferredPlanning(string pairId, long beforeVersion,
        bool rotate = true) => InStore(() =>
    {
        var pair = PairNo(pairId);
        if (pair is null) return (IReadOnlyList<DeferredPlanning>)Array.Empty<DeferredPlanning>();
        using var tx = _connection.BeginTransaction();
        var admitted = ReadPlanningIntake(tx, pair.Value, beforeVersion);
        if (rotate && admitted.Count > 0)
        {
            // FIFO rotation: admitted versions move above the unadmitted eligible queue, keeping
            // their admission order. Checked arithmetic keeps versions monotonic; an impossible
            // overflow throws before any update instead of lowering a selected version.
            var start = checked(beforeVersion + 1);
            using var rotateRow = StoreSchema.Command(_connection, tx, DeferredPlanningRotateSql);
            var next = rotateRow.Parameters.AddWithValue("$next", 0L);
            var root = rotateRow.Parameters.AddWithValue("$root", 0L);
            var id = rotateRow.Parameters.AddWithValue("$id", "");
            rotateRow.Parameters.AddWithValue("$pair", pair.Value);
            var version = rotateRow.Parameters.AddWithValue("$version", 0L);
            rotateRow.Prepare();
            for (var index = 0; index < admitted.Count; index++)
            {
                var row = admitted[index];
                var rotated = checked(start + index);
                next.Value = rotated; root.Value = row.Root; id.Value = row.ObjectId; version.Value = row.Version;
                rotateRow.ExecuteNonQuery();
                admitted[index] = row with { Version = rotated };
            }
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
        Func<string, bool> settled, Func<string, bool>? emptyPath = null,
        Action<DeferredPlanning>? acknowledged = null, Action<DeferredPlanning, string>? blocked = null) => InStore(() =>
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
        var retired = new List<DeferredPlanning>();
        var blockers = new List<(DeferredPlanning Row, string Reason)>();
        foreach (var entry in rows)
        {
            var row = entry.Row;
            if (row.AllAliases && entry.Settled && !row.AliasUnsettled)
            {
                if (AcknowledgePlanningVersion(tx, pair.Value, row)) retired.Add(row);
                else blockers.Add((row, "newer planning version"));
            }
            else
            {
                blockers.Add((row, !row.AllAliases ? "alias slice incomplete" :
                    row.AliasUnsettled ? "prior alias slice unsettled" : "path not settled by this plan"));
                AdvancePlanningAliasSlice(tx, pair.Value, row, entry.Settled);
            }
        }
        tx.Commit();
        foreach (var row in retired) acknowledged?.Invoke(row);
        foreach (var blocker in blockers) blocked?.Invoke(blocker.Row, blocker.Reason);
    });

    // Interrupted runs replay their slice; only a complete successful alias cycle retires the entity.
    private void AdvancePlanningAliasSlice(SqliteTransaction tx, long pair, DeferredPlanning row, bool settled)
    {
        StoreSchema.Execute(_connection, tx, """
            UPDATE planning_deferred SET alias_dir=$dir,alias_key=$key,alias_name=$name,alias_unsettled=$held
            WHERE pair_no=$p AND root_no=$r AND object_id=$o AND version=$v;
            """, ("$p", pair), ("$r", row.Root), ("$o", row.ObjectId), ("$v", row.Version),
            ("$dir", row.AllAliases ? -1 : row.NextAliasDir), ("$key", row.AllAliases ? "" : row.NextAliasKey),
            ("$name", row.AllAliases ? "" : row.NextAliasName),
            ("$held", !row.AllAliases && (row.AliasUnsettled || !settled) ? 1 : 0));
    }

    private bool AcknowledgePlanningVersion(SqliteTransaction tx, long pair, DeferredPlanning row)
    {
        using var delete = StoreSchema.Command(_connection, tx,
            "DELETE FROM planning_deferred WHERE pair_no=$p AND root_no=$r AND object_id=$o AND version=$v;");
        delete.Parameters.AddWithValue("$p", pair);
        delete.Parameters.AddWithValue("$r", row.Root);
        delete.Parameters.AddWithValue("$o", row.ObjectId);
        delete.Parameters.AddWithValue("$v", row.Version);
        return delete.ExecuteNonQuery() != 0;
    }

    internal int CountDeferredPlanning(string pairId, bool verificationOnly = false) => InStore(() => (int)Math.Min(int.MaxValue,
        Convert.ToInt64(StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM planning_deferred WHERE pair_no=$p" +
            (verificationOnly ? " AND reason<>$observation" : "") + ";",
            ("$p", PairNo(pairId)), ("$observation", ObservationPlanningReason)))));
}
