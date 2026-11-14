namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Records temporary item settlements for checks that assert them; other attempt reports are refused.</summary>
internal sealed class TemporaryAttempts : IItemAttempts
{
    private readonly object _gate = new();
    private readonly Dictionary<(Side Side, string Key), long> _waits = [];
    internal int Registered;
    internal int Reported;
    internal Guid Attempt;
    internal IssueSubject? Subject;
    internal string? Path;
    internal TimeSpan? ProviderWait;
    internal long? ProviderWaitStarted;
    internal long SourceEpoch;
    internal Exception? Rejection;
    // An identical replay is not newly accepted; a Repair registration has no deadline or wait.
    internal (bool NewlyAccepted, DateTimeOffset? RetryAt, long? Wait) Result = (true, null, null);
    internal Func<int, string, long?>? WaitFor;
    internal Func<long, bool> Pending = _ => true;
    internal Func<object, bool, Task>? Executing;
    internal List<string> Order { get; } = [];
    internal HashSet<(Side Side, string Key)> Held { get; } = [];
    public bool HeldForRetry(Side side, string key)
    {
        lock (_gate) return Held.Contains((side, key)) || _waits.TryGetValue((side, key), out var wait) && Pending(wait);
    }
    public bool RetryPending(long wait) => Pending(wait);
    public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
        Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        Interlocked.Increment(ref Registered);
        return Executing is { } executing ? OrderedAsync(executing) : execute(Guid.NewGuid(), ct);

        async Task<T> OrderedAsync(Func<object, bool, Task> ordered)
        {
            await ordered(opportunity, false);
            var result = await execute(Guid.NewGuid(), ct);
            await ordered(opportunity, true);
            lock (_gate) Order.Add("executed:" + (opportunity as WorkSelection)?.Key);
            return result;
        }
    }
    public (bool NewlyAccepted, DateTimeOffset? RetryAt, long? Wait) ReportTemporary(Guid attemptId, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        TimeSpan? providerWait, long? providerWaitStarted)
    {
        lock (_gate)
        {
            if (Rejection is { } rejection) throw rejection;
            Reported++;
            Attempt = attemptId;
            Subject = subject;
            Path = path;
            ProviderWait = providerWait;
            ProviderWaitStarted = providerWaitStarted;
            SourceEpoch = sourceEpoch;
            var wait = WaitFor?.Invoke(Reported, path) ?? Result.Wait;
            if (wait is { } held)
            {
                var side = selection?.Side ?? subject.Incoming ?? subject.Items.First(item => item.Path == path).Side;
                _waits[(side, selection?.Key ?? PathNames.KeyFor(side, path))] = held;
            }
            Order.Add("report:" + path);
            return (Result.NewlyAccepted, Result.RetryAt, wait);
        }
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
