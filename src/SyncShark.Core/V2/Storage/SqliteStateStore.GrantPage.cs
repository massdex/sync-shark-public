namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Called under the existing store gate. Batch optional evidence uses indexed exact path keys;
    // the read-only safety exclusion query retains its ordinary whole-root grant semantics.
    private IReadOnlyList<DefenderGrant> ReadDefenderGrantPage(string prefix, string after, string afterId,
        string? exactKeys)
    {
        var page = new List<DefenderGrant>();
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT grant_id,owner_object,source_object,local_object,path,stage_path,verified FROM defender_grant " +
            "WHERE path_key >= $prefix AND path_key < $end AND (path_key > $after OR (path_key=$after AND grant_id>$id)) " +
            (exactKeys is null ? "" : "AND path_key IN (SELECT value FROM json_each($exact)) ") +
            "AND verified<>2 ORDER BY path_key,grant_id LIMIT 512;");
        cmd.Parameters.AddWithValue("$prefix", prefix); cmd.Parameters.AddWithValue("$end", prefix + char.MaxValue);
        cmd.Parameters.AddWithValue("$after", after); cmd.Parameters.AddWithValue("$id", afterId);
        if (exactKeys is not null) cmd.Parameters.AddWithValue("$exact", exactKeys);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) page.Add(ReadGrant(reader));
        return page;
    }
}
