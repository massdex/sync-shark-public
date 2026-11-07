using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Commits writes every <c>limit</c> operations; one <see cref="Step"/> is one atomic operation.</summary>
internal sealed class StoreBatch : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _limit;
    private SqliteTransaction _transaction;
    private int _pending;
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);

    public StoreBatch(SqliteConnection connection, int limit)
    {
        _connection = connection;
        _limit = limit;
        _transaction = connection.BeginTransaction();
    }

    /// <summary>The active transaction every statement in the batch must carry.</summary>
    public SqliteTransaction Transaction => _transaction;

    /// <summary>Ends one logical operation, committing once the batch size is reached.</summary>
    public void Step()
    {
        if (++_pending < _limit) return;
        _transaction.Commit();
        _transaction.Dispose();
        _transaction = _connection.BeginTransaction();
        _pending = 0;
    }

    /// <summary>Commits every operation written so far.</summary>
    public void Flush()
    {
        _transaction.Commit();
        _pending = 0;
    }

    /// <summary>Reuses prepared DML shapes across rows and transaction boundaries.</summary>
    internal int Execute(string sql, params (string Name, object? Value)[] arguments)
    {
        if (!_commands.TryGetValue(sql, out var command))
        {
            command = StoreSchema.Command(_connection, _transaction, sql);
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            command.Prepare();
            _commands.Add(sql, command);
        }
        command.Transaction = _transaction;
        foreach (var (name, value) in arguments) command.Parameters[name].Value = value ?? DBNull.Value;
        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var command in _commands.Values) command.Dispose();
        _transaction.Dispose();
    }
}
