namespace Massdex.SyncShark.Engine;

/// <summary>One stage, effect, notice, wait or provider-request observation for the run log.</summary>
public readonly record struct RunEvent(RunEventKind Kind, string What, string Path, string Detail, string Outcome)
{
    public long? Count { get; init; }
    public long? Total { get; init; }
    public bool Ended { get; init; }
    public Side? Side { get; init; }
    public ActionKind? Action { get; init; }
    public NoticeKind? Notice { get; init; }
    public TextKey? Cause { get; init; }
    public Exception? Error { get; init; }
    /// <summary>The provider's refusal as safe facts only: the route, the HTTP status and Dropbox's error tag.</summary>
    public string? Provider { get; init; }
}
