using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private const int ReconciliationPageRows = V2Settings.PlanningObservationBatchRows;

    internal void StageReconcile(string session, Side side, IReadOnlyList<ItemState> rows)
    {
        foreach (var page in rows.Chunk(ReconciliationPageRows)) StageRemovalItems(session, side, page);
    }

    internal IReadOnlyList<ItemState> ReconcilePage(string session, Side side, string afterKey,
        string afterPath, int limit) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT json FROM temp.removal_observation WHERE session=$s AND side=$side " +
            "AND (path_key,path)>($k,$p) ORDER BY path_key,path LIMIT $limit;");
        command.Parameters.AddWithValue("$s", session);
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$k", afterKey);
        command.Parameters.AddWithValue("$p", afterPath);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, ReconciliationPageRows));
        var rows = new List<ItemState>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<ItemState>(reader.GetString(0))!);
        return (IReadOnlyList<ItemState>)rows;
    });

    internal void RetireReconcile(string session, Side side, IReadOnlyList<ItemState> rows)
    {
        foreach (var page in rows.Chunk(ReconciliationPageRows)) InStore(() =>
        {
            using var tx = _connection.BeginTransaction();
            foreach (var item in page)
                StoreSchema.Execute(_connection, tx,
                    "DELETE FROM temp.removal_observation WHERE session=$s AND side=$side AND path_key=$k AND path=$p;",
                    ("$s", session), ("$side", side.ToString()), ("$k", PathKey.Of(item.Path)), ("$p", item.Path));
            tx.Commit();
        });
    }

    internal IReadOnlySet<string> StagedAmong(string session, Side side, IReadOnlyList<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in paths.Chunk(ReconciliationPageRows)) result.UnionWith(InStore(() =>
        {
            using var command = StoreSchema.Command(_connection, null,
                "SELECT o.path FROM json_each($keys) j CROSS JOIN temp.removal_observation o " +
                "WHERE o.session=$s AND o.side=$side AND o.path_key=j.value;");
            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(page.Select(PathKey.Of)));
            command.Parameters.AddWithValue("$s", session);
            command.Parameters.AddWithValue("$side", side.ToString());
            var found = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        }));
        return result;
    }

    internal bool StagedUnder(string session, Side side, string folder) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM temp.removal_observation WHERE session=$s AND side=$side " +
            "AND path_key>$k||'/' AND path_key<$k||'0' LIMIT 1;",
            ("$s", session), ("$side", side.ToString()), ("$k", PathKey.Of(folder))) is not null);

    internal void RestageReconciliationFolders(string session) => InStore(() =>
    {
        // The main queue is empty at a pass boundary. Keep side identities while moving the deferred queue.
        StoreSchema.Execute(_connection, null,
            "UPDATE temp.removal_observation SET session=$work WHERE session=$deferred;",
            ("$work", session + ":work"), ("$deferred", session + ":deferred"));
    });

    internal long DeferredReconciliationFolders(string session) => InStore(() => Convert.ToInt64(
        StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM temp.removal_observation WHERE session=$s;", ("$s", session + ":deferred"))));

    // This page selects candidates only. The targeted reread owns absence authority.
    internal IReadOnlyList<(FileStateReader.Entry Row, bool Seen)> MissingPage(string session, Side side,
        string rootId, string scope, long afterDir, string afterKey, string afterName) => InStore(() =>
    {
        if (RootNo(rootId) is not { } rootNo) return [];
        using var command = StoreSchema.Command(_connection, null,
            "SELECT f.dir_no,d.path,f.name,f.kind,f.object_id,f.version_id,f.hash,f.size,f.mtime,f.flags,f.identity_json,f.name_key," +
            "EXISTS(SELECT 1 FROM temp.removal_observation o WHERE o.session=$s AND o.side=$side " +
            "AND o.path_key=CASE WHEN d.path='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END) " +
            "FROM file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND (f.dir_no,f.name_key,f.name)>($dir,$key,$name) " +
            "AND ($scope='' OR d.path_key=$scope OR (d.path_key>$scope||'/' AND d.path_key<$scope||'0') " +
            "OR (CASE WHEN d.path='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END)=$scope) " +
            "ORDER BY f.dir_no,f.name_key,f.name LIMIT $limit;");
        command.Parameters.AddWithValue("$s", session);
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$dir", afterDir);
        command.Parameters.AddWithValue("$key", afterKey);
        command.Parameters.AddWithValue("$name", afterName);
        command.Parameters.AddWithValue("$scope", PathKey.Of(scope));
        command.Parameters.AddWithValue("$limit", ReconciliationPageRows);
        var rows = new List<(FileStateReader.Entry Row, bool Seen)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add((FileStateReader.Decode(reader), reader.GetInt64(12) != 0));
        return (IReadOnlyList<(FileStateReader.Entry Row, bool Seen)>)rows;
    });
}
