using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>All tasks a run started (section 8.3).</summary>
internal sealed class TaskSet(CancellationTokenSource cancel)
{
    private readonly HashSet<Member> _members = [];
    private int _live;
    private int _ordinaryLive;
    private Exception? _firstFailure;
    private TaskCompletionSource? _changed;

    /// <summary>Tracks a member and cancels siblings on a fault; returns false only for an already-successful task.</summary>
    public bool Add(Task task)
    {
        // A finished member never needs joining, so it costs no lock, continuation or row task.
        if (task.IsCompletedSuccessfully) return false;
        var member = new Member(task) { Attached = true };
        lock (_members)
        {
            _members.Add(member);
            _live++;
            _ordinaryLive++;
        }
        task.ContinueWith(static (_, state) =>
        {
            var (set, member) = ((TaskSet, Member))state!;
            set.Finished(member);
        }, (this, member), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    // One member lifetime: the member is live and ordinary before start, its task is attached under the _members lock, and a promotion wake is published only after attachment and before final completion.
    /// <summary>
    /// Starts one tracked row whose callback releases its ordinary share once the work and stage permits are held;
    /// returns false only when the started task has already finished successfully.
    /// </summary>
    public bool Add(Func<Action, Task> start)
    {
        var member = new Member();
        lock (_members)
        {
            // The row is live and ordinary before start, so a synchronous promotion cannot escape accounting.
            _live++;
            _ordinaryLive++;
        }

        Task task;
        try
        {
            task = start(() => Promote(member));
        }
        catch (Exception failure)
        {
            // A start that throws before returning a Task is a final fault, never a successful promotion.
            var faulted = Task.FromException(failure);
            lock (_members)
            {
                member.RowTask = faulted;
                member.Attached = true;
                _members.Add(member);
            }
            Finished(member);
            return true;
        }

        lock (_members)
        {
            member.RowTask = task;
            member.Attached = true;
            _members.Add(member);
        }
        task.ContinueWith(static (_, state) =>
        {
            var (set, member) = ((TaskSet, Member))state!;
            set.Finished(member);
        }, (this, member), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        lock (_members)
        {
            // A promotion during start becomes visible only after its returned task is attached, and never
            // before a final fault/success on that task releases capacity through Finished.
            if (member.PromotionPending && !member.Completed && !task.IsCompleted) Wake();
        }
        return !task.IsCompletedSuccessfully;
    }

    /// <summary>
    /// Waits for capacity; wakes when a member finishes, promotes or faults, or when the caller's token is cancelled.
    /// </summary>
    public async Task WaitForCapacityAsync(Func<int> capacity, CancellationToken ct, int? totalCapacity = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var limit = capacity();
            Task changed;
            lock (_members)
            {
                // Sibling cancellation must reach the join without replacing its fault with cancellation.
                if (cancel.IsCancellationRequested) return;
                // Both the ordinary allowance and the total live-row ceiling must admit another row.
                if (_live == 0 || (_ordinaryLive < limit && (totalCapacity is not { } total || _live < total)))
                    return;
                _changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Joins members, preserving the producer's primary failure and reporting a distinct member failure.</summary>
    public async ValueTask<Exception?> JoinAsync(Exception? failure, bool cancelSiblings, Action<Exception> report,
        bool replaceCancellation = true, bool ignoreJoinedFailure = false)
    {
        try
        {
            if (cancelSiblings) await CancelAndJoinAsync().ConfigureAwait(false);
            else await WhenAllAsync().ConfigureAwait(false);
        }
        catch (Exception joined)
        {
            // PairScan can already have returned its primary incomplete diagnosis to Repair.
            if (!ignoreJoinedFailure)
            {
                if (failure is null || replaceCancellation && failure is OperationCanceledException) failure = joined;
                else if (!ReferenceEquals(failure, joined)) report(joined);
            }
        }
        return failure;
    }

    /// <summary>Joins the currently tracked members.</summary>
    public async Task WhenAllAsync()
    {
        Task all;
        lock (_members)
        {
            var tasks = new Task[_members.Count];
            var index = 0;
            foreach (var member in _members) tasks[index++] = member.RowTask!;
            all = Task.WhenAll(tasks);
        }
        try
        {
            await all.ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref _firstFailure) is { } first)
        {
            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <summary>Joins every member and rethrows the first faulting member's own exception.</summary>
    public async Task CancelAndJoinAsync()
    {
        // A cancellation callback fault must not skip the join.
        var failure = TryCancel();

        Member[] pending;
        lock (_members) pending = [.. _members];
        foreach (var member in pending)
        {
            try
            {
                await member.RowTask!.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException) failure ??= exception;
            }
        }

        // Prefer the first recorded fault, not whichever member the join awaited first.
        failure = _firstFailure ?? failure;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void Finished(Member member)
    {
        var task = member.RowTask!;
        if (task.Exception?.InnerException is { } failure)
        {
            // Record the first fault before cancelling siblings or waking the producer.
            Interlocked.CompareExchange(ref _firstFailure, failure, null);
            _ = TryCancel();
        }

        lock (_members)
        {
            if (member.Completed) return;
            member.Completed = true;
            _live--;
            if (member.Ordinary)
            {
                member.Ordinary = false;
                _ordinaryLive--;
            }
            // Faulted and cancelled members stay joinable, but never consume capacity.
            if (task.IsCompletedSuccessfully) _members.Remove(member);
            Wake();
        }
    }

    private void Promote(Member member)
    {
        lock (_members)
        {
            // Exactly one promotion releases the ordinary share; final completion may already have released it.
            if (!member.Ordinary) return;
            member.Ordinary = false;
            _ordinaryLive--;
            // The task is attached before an early promotion wake becomes visible.
            if (member.Attached) Wake();
            else member.PromotionPending = true;
        }
    }

    /// <summary>Wakes a waiting producer; the caller holds the member lock.</summary>
    private void Wake()
    {
        _changed?.SetResult();
        _changed = null;
    }

    /// <summary>Cancels once; returns the callback's exception rather than letting it skip the join.</summary>
    private Exception? TryCancel()
    {
        try
        {
            cancel.Cancel();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>One tracked row: its task and its once-only capacity releases.</summary>
    private sealed class Member(Task? task = null)
    {
        public Task? RowTask { get; set; } = task;
        public bool Attached;
        public bool Ordinary = true;
        public bool PromotionPending;
        public bool Completed;
    }
}
