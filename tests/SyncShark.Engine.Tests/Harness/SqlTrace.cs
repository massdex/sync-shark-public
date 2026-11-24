using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>
/// Attaches the SQLite statement trace and a rows-changed measure to each open connection a phase uses (the writer,
/// plus the Store's separate streaming and ingestion connections), so the counts are read from the connections the
/// phase actually uses and never from a second connection to the same image. Rows are read with the native
/// total_changes counter from a baseline captured at <see cref="Attach"/>, so reading them adds no statement and each
/// connection's count starts at Attach rather than at connection open.
/// </summary>
internal sealed class SqlTrace
{
    private readonly List<(SqliteConnection Connection, int Baseline)> _observed = new();
    private int _statements;
    private SQLitePCL.strdelegate_trace? _trace;

    private SqlTrace() { }

    /// <summary>Start observing each connection the phase uses; read the counters after the phase.</summary>
    internal static SqlTrace Attach(params SqliteConnection[] connections)
    {
        var trace = new SqlTrace();
        // Statements on separate connections run on separate threads; count with an interlocked increment so none is lost.
        SQLitePCL.strdelegate_trace handler = (_, text) => { if (!string.IsNullOrEmpty(text)) Interlocked.Increment(ref trace._statements); };
        trace._trace = handler;
        foreach (var db in connections)
        {
            trace._observed.Add((db, SQLitePCL.raw.sqlite3_total_changes(db.Handle!)));
            SQLitePCL.raw.sqlite3_trace(db.Handle!, handler, null);
        }
        return trace;
    }

    /// <summary>Statements observed on the observed connections since <see cref="Attach"/>.</summary>
    internal int Statements => Volatile.Read(ref _statements);

    /// <summary>Rows changed on the observed connections since <see cref="Attach"/>; read natively, so no statement is emitted.</summary>
    internal int TotalChanges =>
        _observed.Sum(o => SQLitePCL.raw.sqlite3_total_changes(o.Connection.Handle!) - o.Baseline);

    /// <summary>Stop observing; every observed connection's trace callback is detached.</summary>
    internal void Detach()
    {
        if (_trace is null) return;
        foreach (var o in _observed) SQLitePCL.raw.sqlite3_trace(o.Connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
        _trace = null;
    }
}
