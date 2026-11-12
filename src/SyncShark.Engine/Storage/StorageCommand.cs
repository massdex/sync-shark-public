using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Builds and binds one SQLite command; callers own execution and disposal.</summary>
internal static class StorageCommand
{
    /// <summary>B-204: a store failure that means the whole store is unusable, not one item's rows: SQLite BUSY, LOCKED,
    /// NOMEM, READONLY, IOERR, CORRUPT, FULL (also as <see cref="InsufficientSpaceException"/>), CANTOPEN, MISUSE or
    /// NOTADB. Any other store exception during one selection is that item's failure.</summary>
    public static bool StoreUnusable(Exception error) => error is InsufficientSpaceException ||
        error is SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED or
            SQLitePCL.raw.SQLITE_NOMEM or SQLitePCL.raw.SQLITE_READONLY or SQLitePCL.raw.SQLITE_IOERR or
            SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_FULL or SQLitePCL.raw.SQLITE_CANTOPEN or
            SQLitePCL.raw.SQLITE_MISUSE or SQLitePCL.raw.SQLITE_NOTADB };

    /// <summary>Creates a bound command with one argument, without a params array; disposes if setup or binding fails.</summary>
    public static SqliteCommand Create(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        string name, object? value)
    {
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>Creates a bound command, disposing it if setup or binding fails.</summary>
    public static SqliteCommand Create(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}
