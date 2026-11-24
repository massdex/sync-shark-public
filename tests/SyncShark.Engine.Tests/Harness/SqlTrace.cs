using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>
/// Attaches the SQLite statement trace and a row-change measure to one open connection, so a phase's SQL-statement and
/// rows-changed counts are read from the connection the phase actually uses, not from a second connection to the same
/// image. This is the statement/row observer the budget checks need on their fixture connections.
/// </summary>
internal sealed class SqlTrace
{
    private readonly SqliteConnection _db;
    private int _statements;
    private SQLitePCL.strdelegate_trace? _trace;

    private SqlTrace(SqliteConnection db) => _db = db;

    /// <summary>Start observing the connection this phase uses; the returned counter is read after the phase.</summary>
    internal static SqlTrace Attach(SqliteConnection db)
    {
        var trace = new SqlTrace(db);
        SQLitePCL.strdelegate_trace handler = (_, text) => { if (!string.IsNullOrEmpty(text)) trace._statements++; };
        trace._trace = handler;
        SQLitePCL.raw.sqlite3_trace(db.Handle!, handler, null);
        return trace;
    }

    /// <summary>Statements observed on this connection since <see cref="Attach"/>.</summary>
    internal int Statements => _statements;

    /// <summary>Rows changed on this connection so far (INSERT/UPDATE/DELETE), read over the same connection.</summary>
    internal int TotalChanges()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT total_changes()";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Stop observing; the trace callback is detached so later statements on the connection are not counted.</summary>
    internal void Detach()
    {
        if (_trace is not null) SQLitePCL.raw.sqlite3_trace(_db.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
        _trace = null;
    }
}
