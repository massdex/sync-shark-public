using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string RehashAcknowledgementAliasesSql = AcknowledgeRehashIdentitySql;

    private string[] ReadRehashAcknowledgementPage(string rootId, long through, string after,
        IReadOnlyCollection<string>? objectIds) => InStore(() =>
    {
        if (RootNo(rootId) is not { } root) return [];
        using var command = StoreSchema.Command(_connection, null, objectIds is null
            ? "SELECT object_id FROM local_rehash WHERE root_no=$r AND object_id>$after AND last_attempt<=$through ORDER BY object_id LIMIT $limit;"
            : "SELECT q.object_id FROM json_each($ids) j CROSS JOIN local_rehash q " +
                "WHERE q.root_no=$r AND q.object_id=j.value AND q.object_id>$after AND q.last_attempt<=$through ORDER BY q.object_id LIMIT $limit;");
        command.Parameters.AddWithValue("$r", root);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$through", through);
        command.Parameters.AddWithValue("$limit", _settings.StoreRehashPageRows);
        if (objectIds is not null) command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(objectIds.Distinct()));
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.ToArray();
    });

    internal IReadOnlyList<DeferredPlanning> DeferredPlanningFor(string pairId, string rootId,
        IReadOnlyCollection<string> objectIds)
    {
        var result = new List<DeferredPlanning>();
        var remaining = DeferredIntakePathCap;
        foreach (var ids in objectIds.Distinct().Chunk(ReconciliationPageRows)) result.AddRange(InStore(() =>
        {
            if (PairNo(pairId) is not { } pair || RootNo(rootId) is not { } root) return [];
            using var tx = _connection.BeginTransaction();
            using var command = StoreSchema.Command(_connection, tx,
                "SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason,d.alias_dir,d.alias_key,d.alias_name,d.alias_unsettled " +
                "FROM json_each($ids) j CROSS JOIN planning_deferred d JOIN root r ON r.root_no=d.root_no " +
                "WHERE d.root_no=$r AND d.object_id=j.value AND d.pair_no=$p;");
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
            command.Parameters.AddWithValue("$r", root);
            command.Parameters.AddWithValue("$p", pair);
            var rows = new List<DeferredPlanning>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), [], false, reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
                    reader.GetString(7), reader.GetString(8), reader.GetInt64(9) != 0));
            var materialized = rows.Select(row =>
            {
                if (remaining <= 0) return row;
                var admitted = MaterializeAliases(tx, row, remaining);
                remaining -= admitted.Paths.Count;
                return admitted;
            }).ToArray();
            tx.Commit();
            return materialized;
        }));
        return result;
    }

    internal bool HasPlanningBacklog(string pairId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM planning_deferred d INDEXED BY planning_deferred_pair WHERE d.pair_no=$p AND d.reason<>$user " +
            "AND choice_key(d.path) NOT IN (SELECT value FROM json_each($accounted)) " +
            "AND NOT EXISTS(SELECT 1 FROM sync_retry r WHERE r.pair_no=d.pair_no AND r.path_key=choice_key(d.path) AND r.retry_after>$now) LIMIT 1;",
            ("$p", PairNo(pairId)), ("$user", UserChangeReason), ("$now", NowMs()),
            ("$accounted", JsonSerializer.Serialize(PlanNotices.AccountedPaths(UnresolvedNotices(pairId)).Select(PathKey.Of)))) is not null);

    internal IReadOnlyList<DeferredPlanning> PlanningBacklogPage(string pairId, long afterRoot = -1,
        string afterId = "") => InStore(() =>
    {
        if (PairNo(pairId) is not { } pair) return [];
        using var command = StoreSchema.Command(_connection, null,
            "SELECT d.root_no,d.object_id,d.path,r.root_id,d.reason FROM planning_deferred d INDEXED BY planning_deferred_pair " +
            "JOIN root r ON r.root_no=d.root_no WHERE d.pair_no=$p AND d.reason<>$user AND (d.root_no,d.object_id)>($root,$id) " +
            "ORDER BY d.root_no,d.object_id LIMIT $limit;");
        command.Parameters.AddWithValue("$p", pair);
        command.Parameters.AddWithValue("$user", UserChangeReason);
        command.Parameters.AddWithValue("$root", afterRoot);
        command.Parameters.AddWithValue("$id", afterId);
        command.Parameters.AddWithValue("$limit", ReconciliationPageRows);
        var rows = new List<DeferredPlanning>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            0, [], false, reader.GetString(3), reader.GetString(4)));
        return (IReadOnlyList<DeferredPlanning>)rows;
    });
}
