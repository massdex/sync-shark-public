using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Bounded keyset traversal shared by live and staged inventories.</summary>
internal static class SnapshotPager
{
    internal static IEnumerable<FileStateReader.Entry> Read(SqliteConnection connection,
        Func<SqliteTransaction?> transaction, string directorySql, string pageSql,
        Func<SqliteDataReader, FileStateReader.Entry> decode, long? root = null)
    {
        long directory = -1;
        var firstPageSql = pageSql.Replace(")>(", ")>=(", StringComparison.Ordinal);
        while (StoreSchema.Scalar(connection, transaction(), directorySql,
            ("$r", root), ("$d", directory)) is long next)
        {
            directory = next;
            var key = "";
            var name = "";
            var first = true;
            while (true)
            {
                var page = new List<FileStateReader.Entry>(256);
                using (var command = StoreSchema.Command(connection, transaction(), first ? firstPageSql : pageSql))
                {
                    command.Parameters.AddWithValue("$r", root is null ? DBNull.Value : root.Value);
                    command.Parameters.AddWithValue("$d", directory);
                    command.Parameters.AddWithValue("$k", key);
                    command.Parameters.AddWithValue("$n", name);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) page.Add(decode(reader) with { DirNo = directory });
                }
                if (page.Count == 0) break;
                first = false;
                key = page[^1].NameKey;
                name = page[^1].Name;
                foreach (var entry in page) yield return entry;
            }
        }
    }
}
