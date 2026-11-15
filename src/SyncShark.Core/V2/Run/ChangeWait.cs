namespace Massdex.SyncShark.V2;

/// <summary>Waits for the first of several watch signals, a Kick or a timeout, then cancels and joins the rest.</summary>
internal static class ChangeWait
{
    /// <summary>
    /// Every losing watch wait is cancelled and awaited before this returns, so the next wait on a watch never overlaps
    /// (IChangeWatch: one outstanding wait) and a signal that lost the race stays pending (cancellation never consumes).
    /// </summary>
    internal static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    internal static async Task<Wake> AnyAsync(IReadOnlyList<IChangeWatch> watches, Task kick, TimeSpan? timeout,
        TimeProvider time, CancellationToken stop)
    {
        using var losers = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var waits = watches.Select(watch => Begin(watch, losers.Token)).ToArray();
        var timer = timeout is { } delay
            ? Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, time, losers.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, losers.Token);
        await Task.WhenAny(waits.Append(timer).Append(kick)).ConfigureAwait(false);
        // A wait that ended cancelled before we cancelled it was cancelled by something else (a provider timeout):
        // that is a dead watch, not a stop.
        var dead = waits.FirstOrDefault(wait => wait.IsFaulted || (wait.IsCanceled && !losers.IsCancellationRequested));
        losers.Cancel();
        try { await JoinAsync(waits.Append(timer)).WaitAsync(DrainLimit, time).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Never start another wait on a non-cooperative watch. Retire the entire set;
            // PairLoop rebuilds fresh watches or falls back to polling. Late faults are observed by JoinAsync.
            foreach (var watch in watches)
                try { watch.Dispose(); } catch (Exception error) { RunOutput.Observe(error); }
            stop.ThrowIfCancellationRequested();
            return new Wake(WakeKind.Fault, new TimeoutException("Change watching did not stop within its drain deadline; the watches were retired."));
        }
        stop.ThrowIfCancellationRequested();
        dead ??= waits.FirstOrDefault(wait => wait.IsFaulted);
        if (dead is not null) return new Wake(WakeKind.Fault, Unwrap(dead));
        if (kick.IsCompleted) return new Wake(WakeKind.Kick);
        return waits.Any(wait => wait.IsCompletedSuccessfully) ? new Wake(WakeKind.Signal) : new Wake(WakeKind.Timeout);
    }

    // A watch that throws instead of returning a task (even a cancellation) is a dead watch like a faulted wait, and
    // must not skip the cancel-and-join of the waits already started.
    private static Task Begin(IChangeWatch watch, CancellationToken ct)
    {
        try
        {
            return watch.ChangedAsync(ct);
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    // Only the task's AggregateException is unwrapped: the watch's own exception (e.g. EndpointAccessException)
    // must survive, not be replaced by its inner provider error.
    private static Exception Unwrap(Task dead) => dead.IsFaulted
        ? dead.Exception!.InnerExceptions[0]
        : new OperationCanceledException("A change watch was cancelled by its provider.");

    // Outcomes are read from each task's status afterwards; awaiting through a continuation keeps a cancelled or
    // faulted loser from throwing here.
    private static Task JoinAsync(IEnumerable<Task> tasks) =>
        Task.WhenAll(tasks).ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
