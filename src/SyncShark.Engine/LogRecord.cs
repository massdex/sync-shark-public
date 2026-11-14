namespace Massdex.SyncShark.Engine;

/// <summary>One canonical run-log record: the six persisted fields, unescaped. This is the structured form of the
/// same file the human projection reads; it is never a formatted sentence.</summary>
public sealed record LogRecord(DateTimeOffset Timestamp, string Kind, string What, string Path, string Detail,
    string Outcome);
