using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>One run-log line from the Syncer or Run (section 8.4).</summary>
public readonly record struct RunEvent(RunEventKind Kind, string What, string Path, string Detail, string Outcome)
{
    public TextKey? StageKey { get; init; }
    public bool Ended { get; init; }
    public long? Count { get; init; }
    public long? Total { get; init; }
    public bool InProgress { get; init; }

    public static RunEvent Stage(TextKey key, bool ended = false, long? count = null,
        bool inProgress = false, string path = "", long? total = null) =>
        new(RunEventKind.Stage, string.Empty, path, string.Empty, string.Empty)
        { StageKey = key, Ended = ended, Count = count ?? (ended ? null : 0L),
            InProgress = inProgress, Total = total };

    public object? RequestId { get; init; }
    public RequestResult? RequestResult { get; init; }

    public Exception? Error { get; init; }
    public TextKey? Cause { get; init; }
    public IssueKind? IssueKind { get; init; }

    public static RunEvent RetryChanged(string path) =>
        new(RunEventKind.Wait, nameof(TextKey.RetryChanged), path,
            Text.Get(TextKey.RetryChanged, (TextArg.Path, path)), string.Empty);

    public static RunEvent Failed(Exception error, TextKey? reason = null, string path = "") =>
        new(RunEventKind.Failed, string.Empty, path, string.Empty, string.Empty)
        { Error = error, Cause = reason };

    /// <summary>Packages a committed notice and its original failure without presentation.</summary>
    public static RunEvent Issue(IssueKind kind, TextKey cause, string path, Exception? error = null) =>
        new(RunEventKind.Issue, string.Empty, path, string.Empty, string.Empty)
        { IssueKind = kind, Cause = cause, Error = error };

    /// <summary>One effect event. The recurring kinds and sides use compile-time names; any other value keeps
    /// its ToString name, and a state-only effect has an empty outcome.</summary>
    public EffectKind? EffectKind { get; init; }
    public Side? Side { get; init; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RunEvent Effect(EffectKind kind, string path, string detail, Side? side = null) =>
        new(RunEventKind.Effect, string.Empty, path, detail, string.Empty) { EffectKind = kind, Side = side };
}
