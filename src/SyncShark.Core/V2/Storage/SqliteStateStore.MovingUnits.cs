using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private bool _movingUnitIndex;
    private void EnsureMovingUnitIndex() => InStore(() =>
    {
        if (_movingUnitIndex) return;
        StoreSchema.Execute(_connection, null,
            "CREATE INDEX IF NOT EXISTS temp.removal_observation_object " +
            "ON removal_observation(session,side,json_extract(json,'$.ObjectId'));");
        _movingUnitIndex = true;
    });

    internal IReadOnlyList<ItemState> StagedIdentity(string session, Side side, string objectId) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT json FROM temp.removal_observation WHERE session=$s AND side=$side " +
            "AND json_extract(json,'$.ObjectId')=$id LIMIT $limit;");
        command.Parameters.AddWithValue("$s", session);
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$id", objectId);
        command.Parameters.AddWithValue("$limit", V2Settings.SettleCandidateCap + 1);
        var rows = new List<ItemState>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<ItemState>(reader.GetString(0))!);
        return (IReadOnlyList<ItemState>)rows;
    });

    internal IReadOnlyList<ItemState> StagedSubtreePage(string session, Side side, string folder,
        string afterKey, string afterPath) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT json FROM temp.removal_observation WHERE session=$s AND side=$side " +
            "AND path_key >= $folder AND path_key < $folder||'0' " +
            "AND (path_key=$folder OR path_key>$folder||'/') " +
            "AND (path_key,path)>($k,$p) ORDER BY path_key,path LIMIT $limit;");
        command.Parameters.AddWithValue("$s", session);
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$folder", PathKey.Of(folder));
        command.Parameters.AddWithValue("$k", afterKey);
        command.Parameters.AddWithValue("$p", afterPath);
        command.Parameters.AddWithValue("$limit", V2Settings.PlanningObservationBatchRows);
        var rows = new List<ItemState>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<ItemState>(reader.GetString(0))!);
        return (IReadOnlyList<ItemState>)rows;
    });

    internal int MovingUnitItems(string session, Side side, string rootId, IReadOnlyList<string> folders) => InStore(() =>
    {
        if (RootNo(rootId) is not { } root) return 0;
        return Convert.ToInt32(StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(DISTINCT object_id) FROM (" +
            "SELECT json_extract(o.json,'$.ObjectId') object_id FROM json_each($folders) j " +
            "JOIN temp.removal_observation o ON o.session=$s AND o.side=$side " +
            "AND o.path_key>=j.value AND o.path_key<j.value||'0' " +
            "AND (o.path_key=j.value OR o.path_key>j.value||'/') UNION ALL " +
            "SELECT f.object_id FROM json_each($folders) j CROSS JOIN dir d INDEXED BY dir_key CROSS JOIN file_state f " +
            "WHERE d.path_key>=j.value AND d.path_key<j.value||'0' " +
            "AND (d.path_key=j.value OR d.path_key>j.value||'/') AND f.root_no=$r AND f.dir_no=d.dir_no UNION ALL " +
            "SELECT f.object_id FROM json_each($parents) j CROSS JOIN dir d INDEXED BY dir_key CROSS JOIN file_state f " +
            "WHERE d.path_key=json_extract(j.value,'$.Parent') " +
            "AND f.root_no=$r AND f.dir_no=d.dir_no AND f.name_key=json_extract(j.value,'$.Name'));",
            ("$s", session), ("$side", side.ToString()), ("$r", root),
            ("$folders", JsonSerializer.Serialize(folders.Select(PathKey.Of))),
            ("$parents", JsonSerializer.Serialize(folders.Select(folder => new {
                Parent = PathKey.Of(folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : string.Empty),
                Name = PathKey.Of(folder[(folder.LastIndexOf('/') + 1)..]) })))));
    });

    internal void DeferMovingSubtrees(string session, IReadOnlyList<string> folders) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        foreach (var side in new[] { Side.A, Side.B })
            foreach (var folder in folders)
                StoreSchema.Execute(_connection, tx,
                    "UPDATE OR REPLACE temp.removal_observation SET session=$deferred WHERE session=$work " +
                    "AND side=$side AND path_key >= $folder AND path_key < $folder||'0' " +
                    "AND (path_key=$folder OR path_key>$folder||'/');",
                    ("$deferred", session + ":deferred"), ("$work", session + ":work"),
                    ("$side", side.ToString()), ("$folder", PathKey.Of(folder)));
        tx.Commit();
    });
}
