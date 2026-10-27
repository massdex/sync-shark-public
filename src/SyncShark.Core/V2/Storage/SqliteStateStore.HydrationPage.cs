namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private IReadOnlyList<(long Dir, string Name, string Path, string Key)> ReadHydrationInventoryPage(
        long number, long afterDir, string afterKey, string afterName)
    {
        PrefixHydrationPageForTest?.Invoke();
        var result = new List<(long Dir, string Name, string Path, string Key)>();
        using var command = StoreSchema.Command(_connection, null,
            "SELECT f.dir_no,f.name,CASE WHEN d.path='' THEN f.name ELSE d.path||'/'||f.name END,f.name_key FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND (f.dir_no,f.name_key,f.name)>($d,$k,$n) ORDER BY f.dir_no,f.name_key,f.name LIMIT 256;");
        command.Parameters.AddWithValue("$r", number);
        command.Parameters.AddWithValue("$d", afterDir);
        command.Parameters.AddWithValue("$k", afterKey);
        command.Parameters.AddWithValue("$n", afterName);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }
}
