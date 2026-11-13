namespace Massdex.SyncShark.Engine;

/// <summary>Binds item reporting and completion to a registered operation and its actual opportunity.</summary>
public interface IItemAttempts
{
    Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected,
        IssueSubject subject, Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct);
    /// <summary>Reports one committed failure; settledIssue announces only its first report, while committedIssue
    /// retains responsibility on every report, including an identical replay, outside the attempt gate.</summary>
    ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
        IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode,
        Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null);
    /// <summary>Commits a registered completion once; committedIssue retains responsibility, while settledIssue
    /// receives announcement dispositions after Store commit and attempt bookkeeping, outside the attempt gate.</summary>
    void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change, PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null);
    /// <summary>Commits a case-only respelling inside the registered wait (row 43a) without completing the attempt: the
    /// episode and its notice stay, and the content attempt follows in the same opportunity.</summary>
    void CommitRename(Guid attemptId, ItemRetryCandidate expected, StateChange change, WorkSelection? selection) =>
        throw new NotSupportedException();
    /// <summary>Retains a temporarily failed, proven-no-effect turn and accounts its retry at most once per
    /// registration. NewlyAccepted is false only for an identical replay; RetryAt is null inside a Repair registration.</summary>
    (bool NewlyAccepted, DateTimeOffset? RetryAt) ReportTemporary(Guid attemptId, long sourceEpoch, string path,
        IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        TimeSpan? providerWait, long? providerWaitStarted) => throw new NotSupportedException();
    /// <summary>True while live work must skip this side and key: a pending item wait that recorded it, or a running
    /// Repair's unaccounted temporary turn on it.</summary>
    bool HeldForRetry(Side side, string key) => false;
}
