namespace Massdex.SyncShark.Engine;

/// <summary>The outcome of the read-only stored-log view (D-1105-6): the human blocks were streamed to the caller's
/// output transport, or the reason the view is unavailable. Structured output carries the same canonical six-field
/// records, never formatted prose.</summary>
public sealed record LogView(LogViewStatus Status, IReadOnlyList<LogRecord>? Records = null,
    int MalformedRecords = 0, bool UnfinishedTrailingRecord = false);

/// <summary>One canonical run-log record: the six persisted fields, unescaped. This is the structured form of the
/// same file the human projection reads; it is never a formatted sentence.</summary>
public sealed record LogRecord(DateTimeOffset Timestamp, string Kind, string What, string Path, string Detail,
    string Outcome);

public enum LogViewStatus { Found, Invalid, NotFound, Unreadable }
