namespace Massdex.SyncShark.Engine;

/// <summary>One entry on a pair's issue list (D28, section 2.4).</summary>
public sealed record Issue(long Id, IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject,
    DateTimeOffset At, bool Dismissed);
