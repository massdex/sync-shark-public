using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Root bindings and folder path identify the entity, not a pair-wide prepare slot.
    internal static string RemovalKey(PairDef pair, string path) =>
        JsonSerializer.Serialize(new { pair.PairId, pair.A, pair.B, Path = PathKey.Of(path) });

    internal PrepareHeader? ReadRemovalReview(PairDef pair, string path) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT generation,metadata_json,digest FROM removal_review WHERE entity_key=$k;");
        command.Parameters.AddWithValue("$k", RemovalKey(pair, path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new PrepareHeader(pair.PairId, reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    });

    internal void ClearRemovalReview(string generation)
    {
        InStore(() => StoreSchema.Execute(_connection, null,
            "DELETE FROM removal_review WHERE generation=$g;", ("$g", generation)));
        TrySweepPrepareGenerations();
    }

    internal long ProtectedRemovalFolders(string generation) => InStore(() =>
        (long)StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM prepare_row f WHERE f.generation=$g AND f.kind LIKE 'removal-folders-%' " +
            "AND EXISTS(SELECT 1 FROM prepare_row p WHERE p.generation=f.generation " +
            "AND p.kind='removal-protected-parents' AND p.lookup_key=f.lookup_key);", ("$g", generation))!);

    internal void ConsumeRemovalReview(string generation) => InStore(() => StoreSchema.Execute(_connection, null,
        "UPDATE removal_review SET digest='consumed' WHERE generation=$g;", ("$g", generation)));

    internal void SetRemovalMetadata(string generation, string json) => InStore(() => StoreSchema.Execute(_connection, null,
        "UPDATE removal_review SET metadata_json=$j WHERE generation=$g;", ("$j", json), ("$g", generation)));

    internal void StageRemovalItems(string session, Side side, IReadOnlyList<ItemState> rows) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        foreach (var item in rows)
            StoreSchema.Execute(_connection, tx,
                "INSERT OR REPLACE INTO removal_observation VALUES($s,$side,$k,$p,$j);",
                ("$s", session), ("$side", side.ToString()), ("$k", PathKey.Of(item.Path)),
                ("$p", item.Path), ("$j", JsonSerializer.Serialize(item)));
        tx.Commit();
    });

    internal IEnumerable<ItemState> RemovalObservation(string session, Side side)
    {
        string key = "", path = "";
        while (true)
        {
            var page = InStore(() =>
            {
                using var command = StoreSchema.Command(_connection, null,
                    "SELECT path_key,path,json FROM removal_observation WHERE session=$s AND side=$side " +
                    "AND (path_key,path)>($k,$p) ORDER BY path_key,path LIMIT 256;");
                command.Parameters.AddWithValue("$s", session);
                command.Parameters.AddWithValue("$side", side.ToString());
                command.Parameters.AddWithValue("$k", key);
                command.Parameters.AddWithValue("$p", path);
                var rows = new List<(string Key, string Path, ItemState Item)>();
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize<ItemState>(reader.GetString(2))!));
                return rows;
            });
            if (page.Count == 0) yield break;
            foreach (var row in page) yield return row.Item;
            key = page[^1].Key; path = page[^1].Path;
        }
    }

    internal void ClearRemovalObservation(string session) => InStore(() => StoreSchema.Execute(_connection, null,
        "DELETE FROM removal_observation WHERE session=$s;", ("$s", session)));
}
