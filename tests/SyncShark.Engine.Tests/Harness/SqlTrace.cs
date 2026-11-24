using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>Counts statements and changed rows on the actual connections used by a settled phase.</summary>
internal sealed class SqlTrace
{
    private readonly Dictionary<SqliteConnection, int> _live = new();
    private readonly SQLitePCL.strdelegate_trace _trace;
    private int _closedRows;
    private int _statements;
    private bool _detached;

    private SqlTrace() => _trace = (_, text) =>
    {
        if (!string.IsNullOrEmpty(text)) Interlocked.Increment(ref _statements);
    };

    internal static SqlTrace Attach(params SqliteConnection[] connections)
    {
        var trace = new SqlTrace();
        foreach (var db in connections) trace.Observe(db, true);
        return trace;
    }

    internal void Observe(SqliteConnection db, bool opening)
    {
        lock (_live)
        {
            if (_detached) return;
            if (opening)
            {
                _live.Add(db, SQLitePCL.raw.sqlite3_total_changes(db.Handle!));
                SQLitePCL.raw.sqlite3_trace(db.Handle!, _trace, null);
            }
            else if (_live.Remove(db, out var baseline))
            {
                _closedRows += SQLitePCL.raw.sqlite3_total_changes(db.Handle!) - baseline;
                SQLitePCL.raw.sqlite3_trace(db.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            }
        }
    }

    internal int Statements => Volatile.Read(ref _statements);

    /// <summary>Sample after the phase's writers settle; the native counters issue no SQL.</summary>
    internal int TotalChanges
    {
        get { lock (_live) return _closedRows + _live.Sum(o => SQLitePCL.raw.sqlite3_total_changes(o.Key.Handle!) - o.Value); }
    }

    internal void Detach()
    {
        lock (_live)
        {
            foreach (var db in _live.Keys.ToArray()) Observe(db, false);
            _detached = true;
        }
    }
}
