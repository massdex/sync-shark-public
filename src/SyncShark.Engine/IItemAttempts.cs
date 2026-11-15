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
    /// <summary>Retains a temporarily failed, proven-no-effect turn. It accounts once per registration while the live
    /// item wait is pending, and once per due wait after that. NewlyAccepted is false only for an identical replay.
    /// RetryAt is the committed or joined wait's deadline, for display; Wait is its identity, answered by RetryPending.
    /// Both are null inside a Repair registration; Wait is null when the turn holds no key.</summary>
    (bool NewlyAccepted, DateTimeOffset? RetryAt, long? Wait) ReportTemporary(Guid attemptId, long sourceEpoch, string path,
        IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        TimeSpan? providerWait, long? providerWaitStarted) => throw new NotSupportedException();
    /// <summary>True while live work must skip this side and key: a pending item wait that recorded it, or a running
    /// Repair's unaccounted temporary turn on it.</summary>
    bool HeldForRetry(Side side, string key) => false;
    /// <summary>True while a reported item wait holds its work: Monitor answers for the live wait; a wait replaced at
    /// its due turn is due; every other replaced wait holds. A port without a wait owner holds for the run.</summary>
    bool RetryPending(long wait) => true;
}
