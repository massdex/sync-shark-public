using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>One-row-at-a-time reads. Comparison finishes before publication, so no live cursor crosses a write.</summary>
internal static class SnapshotRows
{
    internal static IEnumerable<FileStateReader.Entry> Read(SqliteConnection connection,
        SqliteTransaction? transaction, string sql, Func<SqliteDataReader, FileStateReader.Entry> decode,
        long? root = null)
    {
        using var command = StoreSchema.Command(connection, transaction, sql);
        if (root is not null) command.Parameters.AddWithValue("$r", root.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return decode(reader);
    }
}
