namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal Action<string, long, string, long>? RehashOrderForTest { get; set; }

    private void ReportRehashOrderForTest(string phase, long root, Microsoft.Data.Sqlite.SqliteTransaction? tx,
        string? objectId = null)
    {
        if (RehashOrderForTest is not { } report) return;
        using var command = StoreSchema.Command(_connection, tx,
            "SELECT object_id,last_attempt FROM local_rehash WHERE root_no=$r AND ($o IS NULL OR object_id=$o);");
        command.Parameters.AddWithValue("$r", root);
        command.Parameters.AddWithValue("$o", (object?)objectId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read()) report(phase, root, reader.GetString(0), reader.GetInt64(1));
    }
}
