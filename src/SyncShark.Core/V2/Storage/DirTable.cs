using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Interns folder paths to small integers and resolves self-or-under prefix ranges by path key.</summary>
/// <remarks>
/// Path keys (<see cref="RowCodec.Key"/>) give .NET ordinal-ignore-case matching, non-ASCII included (owner
/// decision 50). The table is global: different roots may intern "Docs" and "docs" as two rows with one key, so key
/// lookups return every match and the caller's query filters by root or pair.
/// </remarks>
internal sealed class DirTable
{
    /// <summary>The bounded key range for "at this prefix or under it" (the character after '/' is '0').</summary>
    private const string KeyRange = "path_key=$k OR (path_key >= $lo AND path_key < $hi)";

    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, long> _cache = new(StringComparer.Ordinal);

    public DirTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Returns the number for a folder path, inserting it once when absent.</summary>
    public long Intern(string dirPath, SqliteTransaction? transaction = null)
    {
        if (_cache.TryGetValue(dirPath, out var cached)) return cached;
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO dir(path, path_key, mapped_key) VALUES($p, $k, $m) ON CONFLICT(path) DO NOTHING;",
            ("$p", dirPath), ("$k", RowCodec.Key(dirPath)),
            ("$m", DropboxNameMapping.Map(dirPath) is var mapped && mapped != dirPath ? RowCodec.Key(mapped) : null));
        var dirNo = ReadNumber(dirPath, transaction)
            ?? throw new InvalidOperationException($"Folder '{dirPath}' vanished after insert.");
        _cache[dirPath] = dirNo;
        return dirNo;
    }

    /// <summary>Returns the number for a folder path spelled exactly so, or null when it is unknown.</summary>
    public long? Find(string dirPath, SqliteTransaction? transaction = null)
    {
        if (_cache.TryGetValue(dirPath, out var cached)) return cached;
        var dirNo = ReadNumber(dirPath, transaction);
        if (dirNo is not null) _cache[dirPath] = dirNo.Value;
        return dirNo;
    }

    /// <summary>Every folder at the prefix or under it, ignoring case, with its stored spelling; "" returns them all.</summary>
    public IReadOnlyList<(long DirNo, string Path)> SelfOrUnder(string prefix, SqliteTransaction? transaction = null)
    {
        using var command = prefix.Length == 0
            ? StoreSchema.Command(_connection, transaction, "SELECT dir_no, path FROM dir;")
            : StoreSchema.Command(_connection, transaction, $"SELECT dir_no, path FROM dir WHERE {KeyRange};");
        AddRangeArguments(command, prefix);
        var result = new List<(long, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    /// <summary>Fills the temporary tables with the folders and the items of these prefixes, by key.</summary>
    public void PopulatePrefixes(SqliteTransaction? transaction, IReadOnlyList<string> prefixes)
    {
        StoreSchema.Execute(_connection, transaction, "DELETE FROM tmp_dirs; DELETE FROM tmp_items;");
        foreach (var prefix in prefixes)
        {
            if (prefix.Length == 0)
            {
                StoreSchema.Execute(_connection, transaction, "INSERT OR IGNORE INTO tmp_dirs SELECT dir_no FROM dir;");
                continue;
            }
            using (var range = StoreSchema.Command(_connection, transaction,
                $"INSERT OR IGNORE INTO tmp_dirs(dir_no) SELECT dir_no FROM dir WHERE {KeyRange};"))
            {
                AddRangeArguments(range, prefix);
                range.ExecuteNonQuery();
            }
            var (parent, name) = RowCodec.Split(prefix);
            StoreSchema.Execute(_connection, transaction,
                "INSERT OR IGNORE INTO tmp_items(dir_no, name_key) SELECT dir_no, $n FROM dir WHERE path_key = $k;",
                ("$k", RowCodec.Key(parent)), ("$n", RowCodec.Key(name)));
        }
    }

    /// <summary>Drops the cached numbers after a prune removed folders.</summary>
    public void ClearCache() => _cache.Clear();

    private static void AddRangeArguments(SqliteCommand command, string prefix)
    {
        if (prefix.Length == 0) return;
        var key = RowCodec.Key(prefix);
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$lo", key + "/");
        command.Parameters.AddWithValue("$hi", key + "0");
    }

    private long? ReadNumber(string dirPath, SqliteTransaction? transaction) =>
        StoreSchema.Scalar(_connection, transaction, "SELECT dir_no FROM dir WHERE path=$p;", ("$p", dirPath))
            is long number
            ? number
            : null;
}
