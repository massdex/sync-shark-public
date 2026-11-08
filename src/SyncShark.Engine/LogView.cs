namespace Massdex.SyncShark.Engine;

/// <summary>The outcome of the read-only stored-log view (D-1105-6): the human blocks were streamed to the caller's
/// output transport, or the reason the view is unavailable. Structured output carries the same canonical six-field
/// records, never formatted prose.</summary>
public sealed record LogView(LogViewStatus Status, IReadOnlyList<LogRecord>? Records = null,
    int MalformedRecords = 0, bool UnfinishedTrailingRecord = false);
