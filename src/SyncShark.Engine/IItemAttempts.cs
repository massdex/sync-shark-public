namespace Massdex.SyncShark.Engine;

/// <summary>Binds item reporting and completion to a registered operation and its actual opportunity.</summary>
public interface IItemAttempts
{
    Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected,
        IssueSubject subject, Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct);
    ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
        IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode);
    void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change, PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null);
    /// <summary>Retains a temporarily failed, proven-no-effect turn and accounts its retry at most once per
    /// registration. NewlyAccepted is false only for an identical replay; RetryAt is null inside a Repair registration.</summary>
    (bool NewlyAccepted, DateTimeOffset? RetryAt) ReportTemporary(Guid attemptId, long sourceEpoch, string path,
        IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        TimeSpan? providerWait, long? providerWaitStarted) => throw new NotSupportedException();
}
