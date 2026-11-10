namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Records temporary item settlements for checks that assert them; other attempt reports are refused.</summary>
internal sealed class TemporaryAttempts : IItemAttempts
{
    internal int Registered;
    internal int Reported;
    internal Guid Attempt;
    internal IssueSubject? Subject;
    internal string? Path;
    internal TimeSpan? ProviderWait;
    internal long? ProviderWaitStarted;
    internal long SourceEpoch;
    internal Exception? Rejection;
    // The port's answer: an identical replay is not newly accepted, and a Repair registration has no deadline.
    internal (bool NewlyAccepted, DateTimeOffset? RetryAt) Result = (true, null);
    public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
        Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        Registered++;
        return execute(Guid.NewGuid(), ct);
    }
    public (bool NewlyAccepted, DateTimeOffset? RetryAt) ReportTemporary(Guid attemptId, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        TimeSpan? providerWait, long? providerWaitStarted)
    {
        if (Rejection is { } rejection) throw rejection;
        Reported++;
        Attempt = attemptId;
        Subject = subject;
        Path = path;
        ProviderWait = providerWait;
        ProviderWaitStarted = providerWaitStarted;
        SourceEpoch = sourceEpoch;
        return Result;
    }
    public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
        TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
        ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) =>
        throw new NotSupportedException();
    public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null) =>
        throw new NotSupportedException();
}
