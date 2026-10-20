using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Injected damage for the Open checks; the only raw access to a state database in the tests.</summary>
/// <remarks>
/// No store method can produce these states. Their reader is <see cref="SqliteStateStore.Open"/>: StoreSchema.Verify
/// reads user_version and the table/index set; the store's Load reads the root and pair tables. The delete fault
/// (a trigger, which Verify does not check) is read by the B-127 subtree crash check.
/// </remarks>
internal static class StorageDamage
{
    private const int UserVersionOffset = 60;
    private const byte Garbage = 0x5A;

    /// <summary>Overwrites the header's user_version with another schema version.</summary>
    public static void PokeUserVersion(string dbPath, int version)
    {
        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Seek(UserVersionOffset, SeekOrigin.Begin);
        stream.Write([(byte)(version >> 24), (byte)(version >> 16), (byte)(version >> 8), (byte)version]);
    }

    /// <summary>Adds a table the schema does not know.</summary>
    public static void AddTable(string dbPath, string table)
    {
        using var connection = OpenRaw(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE {table}(x);";
        command.ExecuteNonQuery();
    }

    /// <summary>Overwrites the root pages of a table and its indexes with garbage; the schema page (Verify) is intact.</summary>
    public static void CorruptTable(string dbPath, string table)
    {
        var pages = new List<long>();
        long pageSize;
        using (var connection = OpenRaw(dbPath))
        {
            using var lookup = connection.CreateCommand();
            lookup.CommandText = "SELECT rootpage FROM sqlite_master WHERE tbl_name=$t AND rootpage > 0;";
            lookup.Parameters.AddWithValue("$t", table);
            using (var reader = lookup.ExecuteReader())
                while (reader.Read()) pages.Add(reader.GetInt64(0));
            using var size = connection.CreateCommand();
            size.CommandText = "PRAGMA page_size;";
            pageSize = (long)size.ExecuteScalar()!;
        }
        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        foreach (var page in pages)
        {
            stream.Seek((page - 1) * pageSize, SeekOrigin.Begin);
            stream.Write(Enumerable.Repeat(Garbage, (int)pageSize).ToArray());
        }
    }

    /// <summary>Makes deleting the live row named <paramref name="name"/> fail, like a crash at that point (B-127).</summary>
    public static void FailDeleteOf(string dbPath, string name)
    {
        using var connection = OpenRaw(dbPath);
        using var command = connection.CreateCommand();
        // A trigger cannot take parameters; the name is a fixed test literal, quoted as SQL text.
        command.CommandText = "CREATE TRIGGER fail_delete BEFORE DELETE ON file_state WHEN old.name = '" +
            name.Replace("'", "''") + "' BEGIN SELECT RAISE(ABORT, 'simulated crash'); END;";
        command.ExecuteNonQuery();
    }

    /// <summary>Removes the fault <see cref="FailDeleteOf"/> injected.</summary>
    public static void ClearDeleteFault(string dbPath)
    {
        using var connection = OpenRaw(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_delete;";
        command.ExecuteNonQuery();
    }

    /// <summary>Models an existing v5 database from before the performance-only index.</summary>
    public static void RemoveFlaggedIndex(string dbPath)
    {
        using var connection = OpenRaw(dbPath);
        StoreSchema.Execute(connection, null, "DROP INDEX fs_flagged;");
    }

    public static (long Version, long RootPage, string Sql) FlaggedIndexState(string dbPath)
    {
        using var connection = OpenRaw(dbPath);
        return ((long)StoreSchema.Scalar(connection, null, "PRAGMA user_version;")!,
            (long)StoreSchema.Scalar(connection, null, "SELECT rootpage FROM sqlite_master WHERE name='fs_flagged';")!,
            (string)StoreSchema.Scalar(connection, null, "SELECT sql FROM sqlite_master WHERE name='fs_flagged';")!);
    }

    private static SqliteConnection OpenRaw(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
