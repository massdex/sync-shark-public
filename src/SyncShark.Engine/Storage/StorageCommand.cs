using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Builds and binds one SQLite command; callers own execution and disposal.</summary>
internal static class StorageCommand
{
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
