namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>The real in-use lifecycle over the store, for checks that assert the association.</summary>
internal sealed class StoreItemAttempts(IStateStore store, string pairId, long generation) : IItemAttempts
{
    public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
        Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);
    public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
        TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
        ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) =>
        store.RecordInUseFailure(pairId, generation, sourceEpoch, path, subject, cause, selection, dispatch,
            expectedPending, expectedEpisode, attemptId, (issue, isNew) =>
            {
                committedIssue?.Invoke(issue);
                settledIssue?.Invoke(issue, isNew);
            });
    public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null) =>
        store.CompleteItemAttempt(expected, attemptId, change, completedPending, selection, committedIssue, settledIssue);
    public void CommitRename(Guid attemptId, ItemRetryCandidate expected, StateChange change, WorkSelection? selection) =>
        store.CommitItemRename(expected, attemptId, change, selection);
    // E-4: the store's temporary retention, as EngineHost.ReportTemporaryFailure records it; no retry schedule here.
    public (bool NewlyAccepted, DateTimeOffset? RetryAt, long? Wait) ReportTemporary(Guid attemptId, long sourceEpoch, string path,
        IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        TimeSpan? providerWait, long? providerWaitStarted)
    {
        (Side Side, string Path, string? Id)? successor = null;
        if (selection is null)
        {
            var side = subject.Incoming ?? subject.Items.First(item => item.Path == path).Side;
            successor = (side, path, subject.Items.FirstOrDefault(item => item.Side == side && item.Path == path)?.Id);
        }
        Reported++;
        store.RecordTemporaryItem(pairId, generation, sourceEpoch, selection, successor, null, null);
        return (true, null, null);
    }
    internal int Reported { get; private set; }
}
