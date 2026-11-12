using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for TaskSet (`--engine-checks repair`).</summary>
public static class TaskSetChecks
{
    public static void Run()
    {
        WhenAllAwaitsEveryMember();
        WhenAllRethrowsAMemberFailure();
        CompletedMembersAreReleased().GetAwaiter().GetResult();
        FirstFaultCancelsTheRestBeforeTheJoin();
        JoinRethrowsTheFirstFaultNotTheFirstInserted(false);
        JoinRethrowsTheFirstFaultNotTheFirstInserted(true);
        CancelAndJoinCancelsAndAwaitsEveryMember();
        CancelAndJoinRethrowsTheFirstOtherFailureAfterAllFinish();
        ThrowingCancellationCallbackKeepsJoining();
        RecordedMemberFaultOutranksACallbackFailure();
        DisposedCancellationSourceKeepsJoining();
        MemberFaultAfterACallbackFailureWins();
        ManyFinishedMembersCostNoMoreThanOneRowEach().GetAwaiter().GetResult();
        AddReportsWhetherWorkWasTracked();
        TotalCeilingStopsProductionWhileTheOrdinaryAllowanceAdmits().GetAwaiter().GetResult();
        TotalCeilingWakeKeepsTheFirstFault().GetAwaiter().GetResult();
        PromotionReleasesOnlyTheOrdinaryShareAndTheTailStaysCounted().GetAwaiter().GetResult();
        PromotionDuringSynchronousStartIsAppliedOnce().GetAwaiter().GetResult();
        SynchronousSuccessAndLatePromotionLeaveNoShare();
        StartFailureBecomesTheFirstJoinedFault();
        PromotedRowKeepsALaterTailFault();
        CapacityWaitsAtTheLimitAndReusesItsSignal().GetAwaiter().GetResult();
        CapacityRechecksAfterShrink().GetAwaiter().GetResult();
        CapacityRechecksAfterGrowth().GetAwaiter().GetResult();
        CancelledMemberFreesCapacity().GetAwaiter().GetResult();
        CapacityHonoursOnlyCallerCancellation().GetAwaiter().GetResult();
        FaultReleasesCapacityWithoutReplacingItsDiagnosis().GetAwaiter().GetResult();
        CompletionDuringCapacityCheckIsNotLost().GetAwaiter().GetResult();
        CancellationDuringCapacityCheckIsNotLost(false).GetAwaiter().GetResult();
        CancellationDuringCapacityCheckIsNotLost(true).GetAwaiter().GetResult();
    }

    private static void AddReportsWhetherWorkWasTracked()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        Check.Require(!set.Add(Task.CompletedTask), "synchronous success does not enter the tracked path");
        var member = new TaskCompletionSource();
        Check.Require(set.Add(member.Task), "an incomplete member enters the tracked path");
        member.SetResult();
        Check.Require(!set.Add(member.Task), "a previously asynchronous success now takes the synchronous path");
        Check.Require(set.Add(Task.FromCanceled(new CancellationToken(true))),
            "an already-cancelled member is tracked for joining");
        var failure = new ArgumentException("already failed");
        Check.Require(set.Add(Task.FromException(failure)), "an already-faulted member is observed and tracked");
        Check.Require(cancel.IsCancellationRequested, "an already-faulted member cancels the run");
        Check.Require(set.WaitForCapacityAsync(() => 1, CancellationToken.None).IsCompletedSuccessfully,
            "a cancelled run does not block production");
        Check.Require(ReferenceEquals(Catch(set.CancelAndJoinAsync()), failure),
            "joining preserves the already-faulted member's original exception");
    }

    private static async Task CapacityWaitsAtTheLimitAndReusesItsSignal()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        Check.Require(set.WaitForCapacityAsync(() => 1, CancellationToken.None).IsCompletedSuccessfully,
            "an empty set has capacity");
        var survivor = new TaskCompletionSource();
        set.Add(survivor.Task);
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None).IsCompletedSuccessfully,
            "an incomplete member below the limit does not block production");

        // Two successive full sets exercise replacement of the completion signal, not just its first wake.
        for (var round = 0; round < 2; round++)
        {
            var member = new TaskCompletionSource();
            set.Add(member.Task);
            var reads = 0;
            var capacity = set.WaitForCapacityAsync(() =>
            {
                Interlocked.Increment(ref reads);
                return 2;
            }, CancellationToken.None);
            Check.Require(!capacity.IsCompleted, "production waits when incomplete work equals capacity");
            member.SetResult();
            await capacity;
            Check.Require(Volatile.Read(ref reads) == 2,
                "each full set reads capacity once before waiting and once after its completion wake");
            Check.Require(!survivor.Task.IsCompleted && !cancel.IsCancellationRequested,
                "one completion frees capacity without cancelling or joining the remaining work");
        }

        var join = set.WhenAllAsync();
        Check.Require(!join.IsCompleted, "a capacity wake does not release the remaining member from the join");
        survivor.SetResult();
        await join;
    }

    private static async Task CapacityRechecksAfterShrink()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var members = new[] { new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource() };
        foreach (var member in members) set.Add(member.Task);
        var level = members.Length;
        var reread = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capacity = set.WaitForCapacityAsync(() =>
        {
            var current = Volatile.Read(ref level);
            if (current < members.Length) reread.TrySetResult();
            return current;
        }, CancellationToken.None);
        Check.Require(!capacity.IsCompleted, "the initial full level blocks production");
        Volatile.Write(ref level, 1);
        members[0].SetResult();
        await reread.Task;
        Check.Require(!capacity.IsCompleted && !cancel.IsCancellationRequested,
            "a lower level stops production without cancelling sound work");
        members[1].SetResult();
        Check.Require(!capacity.IsCompleted, "one incomplete member still fills the shrunken capacity");
        members[2].SetResult();
        await capacity;
        Check.Require(capacity.IsCompletedSuccessfully && !cancel.IsCancellationRequested,
            "falling below the shrunken capacity releases production without cancelling the run");
        await set.WhenAllAsync();
    }

    private static async Task CapacityRechecksAfterGrowth()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var third = new TaskCompletionSource();
        set.Add(first.Task);
        set.Add(second.Task);
        set.Add(third.Task);
        var level = 1;
        var capacity = set.WaitForCapacityAsync(() => Volatile.Read(ref level), CancellationToken.None);
        Check.Require(!capacity.IsCompleted, "work above the original level blocks production");
        Volatile.Write(ref level, 3);
        first.SetResult();
        await capacity;
        Check.Require(!second.Task.IsCompleted && !third.Task.IsCompleted && !cancel.IsCancellationRequested,
            "completion rechecks a grown level without waiting for the old level to drain");
        second.SetResult();
        third.SetResult();
        await set.WhenAllAsync();
    }

    private static async Task CancelledMemberFreesCapacity()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);
        var capacity = set.WaitForCapacityAsync(() => 1, CancellationToken.None);
        Check.Require(!capacity.IsCompleted, "a pending member fills capacity before it is cancelled");
        member.SetCanceled();
        await capacity;
        Check.Require(!cancel.IsCancellationRequested, "a cancelled-only member does not fault the run");
        Check.Require(set.Add(Task.FromCanceled(new CancellationToken(true))),
            "an already-cancelled member is still joinable");
        Check.Require(set.WaitForCapacityAsync(() => 1, CancellationToken.None).IsCompletedSuccessfully,
            "cancelled-only members kept for joining occupy no capacity");
        Check.Require(Catch(set.WhenAllAsync()) is OperationCanceledException,
            "freeing cancelled capacity does not hide cancellation from WhenAllAsync");
        await set.CancelAndJoinAsync();
    }

    private static async Task CapacityHonoursOnlyCallerCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);
        var capacity = set.WaitForCapacityAsync(() => 1, caller.Token);
        Check.Require(!capacity.IsCompleted, "caller cancellation begins with a capacity-blocked producer");
        caller.Cancel();
        Check.Require(Catch(capacity) is OperationCanceledException error && error.CancellationToken == caller.Token,
            "the capacity wait reports the caller's cancellation token");
        Check.Require(!cancel.IsCancellationRequested && !member.Task.IsCompleted,
            "cancelling a capacity wait neither cancels nor finishes tracked work");
        Check.Require(Catch(set.WaitForCapacityAsync(() => 2, caller.Token)) is OperationCanceledException,
            "an already-cancelled caller is honoured even when capacity is available");

        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "the cancelled run still joins its unfinished member");
        member.SetResult();
        await join;
    }

    private static async Task FaultReleasesCapacityWithoutReplacingItsDiagnosis()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Token.Register(() => throw new InvalidOperationException("callback failed"));
        var set = new TaskSet(cancel);
        var survivor = new TaskCompletionSource();
        var member = new TaskCompletionSource();
        set.Add(survivor.Task);
        set.Add(member.Task);
        var capacity = set.WaitForCapacityAsync(() => 1, CancellationToken.None);
        Check.Require(!capacity.IsCompleted, "the fault begins while production has no capacity");
        var failure = new ArgumentException("member failed");
        member.SetException(failure);
        await capacity;
        Check.Require(cancel.IsCancellationRequested && !survivor.Task.IsCompleted,
            "a member fault wakes production even while another member fills capacity");
        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "neither the member nor callback fault skips the outstanding join");
        survivor.SetCanceled();
        Check.Require(ReferenceEquals(Catch(join), failure),
            "a member fault is rethrown after its sibling's cancellation");
    }

    private static async Task CompletionDuringCapacityCheckIsNotLost()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);
        // Inline completion makes callback re-entry deterministic without sleeps or hooks.
        await set.WaitForCapacityAsync(() =>
        {
            member.TrySetResult();
            return 1;
        }, CancellationToken.None);
        Check.Require(member.Task.IsCompletedSuccessfully,
            "completion during the capacity check releases the waiter");
        await set.WhenAllAsync();
    }

    private static async Task CancellationDuringCapacityCheckIsNotLost(bool cancelCaller)
    {
        using var cancel = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);
        var capacity = set.WaitForCapacityAsync(() =>
        {
            (cancelCaller ? caller : cancel).Cancel();
            return 1;
        }, caller.Token);
        if (cancelCaller)
            Check.Require(Catch(capacity) is OperationCanceledException error && error.CancellationToken == caller.Token,
                "caller cancellation during the capacity check is not lost before subscribing");
        else
            await capacity;
        Check.Require((cancelCaller ? caller : cancel).IsCancellationRequested && !member.Task.IsCompleted,
            "cancellation racing the capacity check is observed without completing a tracked member");
        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "the join still waits after cancellation races the capacity check");
        member.SetResult();
        await join;
    }

    private static void ThrowingCancellationCallbackKeepsJoining()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Token.Register(() => throw new InvalidOperationException("callback failed"));
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);

        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "a throwing cancellation callback does not skip the join");
        member.SetResult();

        var failure = Catch(join);
        Check.Require(failure is AggregateException { InnerException: InvalidOperationException },
            "the join still surfaces the cancellation callback failure after joining");
    }

    private static void RecordedMemberFaultOutranksACallbackFailure()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Token.Register(() => throw new InvalidOperationException("callback failed"));
        var set = new TaskSet(cancel);
        set.Add(Task.FromException(new ArgumentException("member failed")));
        Check.Require(cancel.IsCancellationRequested, "the faulting member cancels the set before the join");

        var failure = Catch(set.CancelAndJoinAsync());
        Check.Require(failure is ArgumentException && failure.Message == "member failed",
            "a recorded member fault outranks a cancellation callback failure");
    }

    private static void DisposedCancellationSourceKeepsJoining()
    {
        var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);
        cancel.Dispose();

        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "a disposed cancellation source does not skip the join");
        member.SetResult();

        var failure = Catch(join);
        Check.Require(failure is ObjectDisposedException,
            "the disposed source's error stays observable after joining");
    }

    private static void MemberFaultAfterACallbackFailureWins()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Token.Register(() => throw new InvalidOperationException("callback failed"));
        var set = new TaskSet(cancel);
        var member = new TaskCompletionSource();
        set.Add(member.Task);

        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "the callback failure does not skip the join");
        member.SetException(new ArgumentException("member failed"));

        var failure = Catch(join);
        Check.Require(failure is ArgumentException && failure.Message == "member failed",
            "a member fault recorded after a callback failure keeps the final precedence");
    }

    private static async Task ManyFinishedMembersCostNoMoreThanOneRowEach()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        for (var i = 0; i < 100_000; i++) set.Add(Task.FromResult(i));

        var late = new TaskCompletionSource[10_000];
        for (var i = 0; i < late.Length; i++)
        {
            late[i] = new TaskCompletionSource();
            set.Add(late[i].Task);
        }

        // The finished members left no counted shares behind, so only the later members fill the total ceiling.
        Check.Require(set.WaitForCapacityAsync(() => late.Length + 1, CancellationToken.None, late.Length + 1)
                .IsCompletedSuccessfully,
            "100,000 finished members leave no counted work");
        var full = set.WaitForCapacityAsync(() => late.Length + 1, CancellationToken.None, late.Length);
        Check.Require(!full.IsCompleted, "only the members that finish later fill the total ceiling");
        var all = set.WhenAllAsync();
        Check.Require(!all.IsCompleted, "the run still waits for the members that finish later");
        late[0].SetResult();
        await full;
        for (var i = 1; i < late.Length; i++) late[i].SetResult();
        await all;
        Check.Require(all.IsCompletedSuccessfully, "a large mixed set completes");
    }

    private static async Task CompletedMembersAreReleased()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var pending = new TaskCompletionSource();
        set.Add(pending.Task);
        set.Add(Task.CompletedTask);
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None, totalCapacity: 2).IsCompletedSuccessfully,
            "an already-completed member leaves its ordinary and total shares free");

        var survivor = new TaskCompletionSource();
        set.Add(survivor.Task);
        var full = set.WaitForCapacityAsync(() => 2, CancellationToken.None);
        Check.Require(!full.IsCompleted, "two pending members fill the ordinary allowance");
        pending.SetResult();
        await full;
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None, totalCapacity: 2).IsCompletedSuccessfully,
            "a finished member releases both its ordinary and total shares");
        survivor.SetResult();
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None, totalCapacity: 2).IsCompletedSuccessfully,
            "the last member's completion empties the set");

        // A faulted member stays, so releasing the finished ones does not hide its failure.
        set.Add(Task.FromException(new InvalidOperationException("kept")));
        Check.Require(cancel.IsCancellationRequested, "a faulted member still cancels the run");
        Check.Require(Catch(set.WhenAllAsync()) is InvalidOperationException,
            "WhenAllAsync still surfaces a faulted member after the finished ones were released");
    }

    private static void WhenAllAwaitsEveryMember()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        set.Add(first.Task);
        set.Add(second.Task);

        var all = set.WhenAllAsync();
        Check.Require(!all.IsCompleted, "WhenAllAsync waits for the first member");
        first.SetResult();
        Check.Require(!all.IsCompleted, "WhenAllAsync waits for the last member");
        second.SetResult();
        all.GetAwaiter().GetResult();
        Check.Require(all.IsCompletedSuccessfully, "WhenAllAsync completes when every member completes");

        using var emptyCancel = new CancellationTokenSource();
        Check.Require(new TaskSet(emptyCancel).WhenAllAsync().IsCompletedSuccessfully,
            "WhenAllAsync of no members is already complete");
    }

    private static void WhenAllRethrowsAMemberFailure()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        set.Add(Task.CompletedTask);
        set.Add(Task.FromException(new InvalidOperationException("member failed")));

        var failure = Catch(set.WhenAllAsync());
        Check.Require(failure is InvalidOperationException, "WhenAllAsync surfaces a member's own failure");
    }

    private static void FirstFaultCancelsTheRestBeforeTheJoin()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var pending = Task.Delay(Timeout.InfiniteTimeSpan, cancel.Token);
        set.Add(pending);
        set.Add(Task.FromException(new ArgumentException("first")));

        Check.Require(cancel.IsCancellationRequested, "the first faulting member cancels the linked source at once");
        set.Add(Task.FromException(new InvalidOperationException("later")));

        var failure = Catch(set.CancelAndJoinAsync());
        Check.Require(failure is ArgumentException && failure.Message == "first",
            "the join rethrows the first faulting member's original exception");
        Check.Require(pending.IsCanceled, "the join awaits the members the first fault cancelled");
    }

    private static void JoinRethrowsTheFirstFaultNotTheFirstInserted(bool cancelBeforeJoin)
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        set.Add(first.Task);
        set.Add(second.Task);

        var join = cancelBeforeJoin ? set.CancelAndJoinAsync() : set.WhenAllAsync();
        Check.Require(!join.IsCompleted, "the join is still waiting when both members are pending");
        var firstFailure = new ArgumentException("faulted first");
        second.SetException(firstFailure);
        Check.Require(!join.IsCompleted, "the first fault does not skip the remaining member's join");
        first.SetException(new InvalidOperationException("faulted second"));

        Check.Require(ReferenceEquals(Catch(join), firstFailure),
            "the join rethrows the member that faulted first, not the first inserted");
    }

    private static void CancelAndJoinCancelsAndAwaitsEveryMember()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var done = false;
        set.Add(Task.Run(() => done = true));
        var member = Task.Delay(Timeout.InfiniteTimeSpan, cancel.Token);
        set.Add(member);

        var join = set.CancelAndJoinAsync();
        Check.Require(cancel.IsCancellationRequested, "CancelAndJoinAsync cancels the set's token");
        join.GetAwaiter().GetResult();
        Check.Require(done, "CancelAndJoinAsync awaits every finished member");
        Check.Require(member.IsCanceled, "CancelAndJoinAsync awaits the cancelled member");
    }

    private static void CancelAndJoinRethrowsTheFirstOtherFailureAfterAllFinish()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        set.Add(Task.Delay(Timeout.InfiniteTimeSpan, cancel.Token));
        set.Add(Task.FromException(new InvalidOperationException("first failure")));
        set.Add(Task.FromException(new ArgumentException("second failure")));
        var gate = new TaskCompletionSource();
        set.Add(gate.Task);

        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "CancelAndJoinAsync surfaces no failure before every member finishes");

        gate.SetResult();
        var failure = Catch(join);
        Check.Require(failure is InvalidOperationException, "CancelAndJoinAsync rethrows the first other failure");
        Check.Require(failure!.Message == "first failure", "CancelAndJoinAsync rethrows it unchanged");
    }

    private static async Task TotalCeilingStopsProductionWhileTheOrdinaryAllowanceAdmits()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        set.Add(first.Task);
        set.Add(second.Task);
        Check.Require(set.WaitForCapacityAsync(() => 3, CancellationToken.None).IsCompletedSuccessfully,
            "two incomplete members stay below the ordinary allowance");
        var total = set.WaitForCapacityAsync(() => 3, CancellationToken.None, totalCapacity: 2);
        Check.Require(!total.IsCompleted, "the total ceiling stops production while the ordinary allowance admits");
        first.SetResult();
        await total;
        Check.Require(!second.Task.IsCompleted && !cancel.IsCancellationRequested,
            "one final completion reopens the total gate without cancelling or finishing the other member");
        second.SetResult();
        await set.WhenAllAsync();
    }

    private static async Task TotalCeilingWakeKeepsTheFirstFault()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var pending = new TaskCompletionSource();
        var faulting = new TaskCompletionSource();
        set.Add(pending.Task);
        set.Add(faulting.Task);
        var blocked = set.WaitForCapacityAsync(() => 3, CancellationToken.None, totalCapacity: 2);
        Check.Require(!blocked.IsCompleted, "the total ceiling holds production before the fault");
        var failure = new ArgumentException("member failed");
        faulting.SetException(failure);
        await blocked;
        Check.Require(cancel.IsCancellationRequested,
            "the member's fault cancels the run before the total-gate waiter wakes");
        pending.SetResult();
        Check.Require(ReferenceEquals(Catch(set.CancelAndJoinAsync()), failure),
            "the first member fault survives the total-gate wake");
    }

    private static async Task PromotionReleasesOnlyTheOrdinaryShareAndTheTailStaysCounted()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        Action? release = null;
        var tail = new TaskCompletionSource();
        var survivor = new TaskCompletionSource();
        Check.Require(set.Add(starter =>
        {
            release = starter;
            return tail.Task;
        }), "an incomplete started row enters the tracked path");
        set.Add(survivor.Task);
        Check.Require(release is not null, "the start callback receives its once-only promotion action");
        var full = set.WaitForCapacityAsync(() => 2, CancellationToken.None);
        Check.Require(!full.IsCompleted, "two unpromoted rows fill the ordinary allowance");
        release!();
        await full;
        release!();
        var notTwice = set.WaitForCapacityAsync(() => 1, CancellationToken.None);
        var parkedTail = set.WaitForCapacityAsync(() => 3, CancellationToken.None, totalCapacity: 2);
        Check.Require(!notTwice.IsCompleted, "a repeated promotion does not release a second ordinary share");
        Check.Require(!parkedTail.IsCompleted, "the promoted row's parked tail still counts toward the total ceiling");
        var all = set.WhenAllAsync();
        Check.Require(!all.IsCompleted, "promotion neither completes nor releases the row from the join");
        tail.SetResult();
        var afterTailRelease = set.WaitForCapacityAsync(() => 1, CancellationToken.None);
        Check.Require(!afterTailRelease.IsCompleted,
            "a promoted row's final completion releases no second ordinary share");
        survivor.SetResult();
        await all;
        await notTwice;
        await parkedTail;
        await afterTailRelease;
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None, totalCapacity: 2).IsCompletedSuccessfully,
            "the set empties when the survivor finishes");
    }

    private static async Task PromotionDuringSynchronousStartIsAppliedOnce()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var tail = new TaskCompletionSource();
        Check.Require(set.Add(starter =>
        {
            starter();
            return tail.Task;
        }), "a row that promotes during its own start is still tracked");
        Check.Require(set.WaitForCapacityAsync(() => 1, CancellationToken.None).IsCompletedSuccessfully,
            "the remembered promotion is applied after the returned task is attached");
        var survivor = new TaskCompletionSource();
        set.Add(survivor.Task);
        var notTwice = set.WaitForCapacityAsync(() => 1, CancellationToken.None);
        var parkedTail = set.WaitForCapacityAsync(() => 3, CancellationToken.None, totalCapacity: 2);
        Check.Require(!notTwice.IsCompleted, "the remembered promotion releases the ordinary share exactly once");
        Check.Require(!parkedTail.IsCompleted, "the promoted row still counts toward the total ceiling");
        var all = set.WhenAllAsync();
        Check.Require(!all.IsCompleted, "a row that promoted during start still joins its returned task");
        tail.SetResult();
        survivor.SetResult();
        await all;
        await notTwice;
        await parkedTail;
    }

    private static void SynchronousSuccessAndLatePromotionLeaveNoShare()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var survivor = new TaskCompletionSource();
        set.Add(survivor.Task);
        Action? release = null;
        Check.Require(!set.Add(starter =>
        {
            release = starter;
            starter();
            return Task.CompletedTask;
        }), "a promoted synchronous success still takes the synchronous path");
        Check.Require(release is not null, "the start ran before the synchronous result was observed");
        release!();
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None, totalCapacity: 2).IsCompletedSuccessfully,
            "a synchronously completed promoted row leaves neither an ordinary nor a total share");
        survivor.SetResult();
        set.WhenAllAsync().GetAwaiter().GetResult();
        Check.Require(set.WaitForCapacityAsync(() => 2, CancellationToken.None, totalCapacity: 2).IsCompletedSuccessfully,
            "the survivor's completion empties the set");
    }

    private static void StartFailureBecomesTheFirstJoinedFault()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        var failure = new InvalidOperationException("start failed");
        Check.Require(set.Add(starter =>
        {
            starter();
            throw failure;
        }), "a starter that throws after a remembered promotion is tracked");
        Check.Require(cancel.IsCancellationRequested,
            "the starter's failure cancels the run through the final-fault path");
        Check.Require(ReferenceEquals(Catch(set.CancelAndJoinAsync()), failure),
            "the starter's original exception survives the join");
    }

    private static void PromotedRowKeepsALaterTailFault()
    {
        using var cancel = new CancellationTokenSource();
        var set = new TaskSet(cancel);
        Action? release = null;
        var tail = new TaskCompletionSource();
        set.Add(starter =>
        {
            release = starter;
            return tail.Task;
        });
        release!();
        var join = set.CancelAndJoinAsync();
        Check.Require(!join.IsCompleted, "a promoted row is still joined to its tail");
        var failure = new ArgumentException("tail failed");
        tail.SetException(failure);
        Check.Require(ReferenceEquals(Catch(join), failure),
            "promotion does not relabel a later tail failure as success");
    }

    private static Exception? Catch(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
