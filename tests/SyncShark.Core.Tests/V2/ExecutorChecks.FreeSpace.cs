namespace Massdex.SyncShark.V2;

// Executor-level checks for the free-space admission path: a refusal is pending (never Failed or replayed), and the
// reservation is released on success, failure and cancellation.
internal static partial class ExecutorChecks
{
    private static async Task FreeSpace_RefusalIsPendingAndOthersContinue()
    {
        var journal = new List<string>();
        var store = new JournalStore(new InMemoryStateStore(), journal);
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var refused = a.Seed("blocked", "blocked-bytes", versionId: "v1");
        var allowed = a.Seed("ok", "ok-bytes", versionId: "v1");
        var record = new RecordingWriter(b)
        {
            FreeSpaceAdmissionFor = action => action.Path == "blocked"
                ? new FreeSpaceAdmission(null, new FreeSpaceRefusal(
                    "Not enough free space on C:: needs 1.0 MiB, free 1.0 GiB, reserve 512.0 MiB. " +
                    "Options: free space and retry; exclude this folder",
                    FreeSpaceRefusal.InsufficientReasonKey))
                : FreeSpaceAdmission.Granted,
        };
        var admission = Admission();
        var executor = new Executor(store, Settings(4), admission);
        var blocked = Act(ActionKind.CopyNew, Side.B, "blocked", source: refused, key: "k1");
        var allowedAction = Act(ActionKind.CopyNew, Side.B, "ok", source: allowed, key: "k2");

        var results = await Apply(executor, Plan(blocked, allowedAction), a, record);
        var blockedResult = results.Single(result => result.ActionId == blocked.ActionId);
        Require(blockedResult.Outcome == Outcome.Skipped,
            "an insufficient-free-space item is pending (Skipped), never Failed or Uncertain");
        Require(blockedResult.Reason is { } reason &&
            reason.StartsWith("Not enough free space on C:", StringComparison.Ordinal) &&
            reason.Contains("free space and retry", StringComparison.Ordinal) &&
            reason.Contains("exclude this folder", StringComparison.Ordinal),
            "the pending result carries the notice text and both options: " + blockedResult.Reason);
        Require(results.Single(result => result.ActionId == allowedAction.ActionId).Outcome == Outcome.Done,
            "other work continues while the refused item stays pending");
        Require(admission.Snapshot().ActiveFiles == 0,
            "a refused item releases the transfer admission it acquired first");
        Require(record.Puts == 1 && !record.Calls.Contains("Put:blocked"),
            "no effect is attempted for the refused item");
        Require(!journal.Contains("intent:" + blocked.ActionId) &&
            journal.Contains("commit:" + blocked.ActionId + "|" + Outcome.Skipped),
            "the refused item records no intent, so nothing can be replayed; only its pending result commits");
        Require(store.UnresolvedIntents("p1").Count == 0,
            "a pending free-space item leaves no unresolved intent");
        var notice = store.OpenNotices("p1").Single(item => item.Path == "blocked");
        Require(notice.ReasonKey == FreeSpaceRefusal.InsufficientReasonKey &&
            notice.Detail.StartsWith("Not enough free space on C:", StringComparison.Ordinal),
            "a stored notice names the volume and the amounts: " + notice.Detail);
        Require(store.OpenNotices("p1").All(item => item.Path != "ok"),
            "the admitted item raises no free-space notice");
        var unresolved = store.UnresolvedIntents("p1");
        Require(unresolved.Count == 0, "a low-space refusal leaves no intent for reconcile");
        var reconciled = await executor.ReconcileAsync(unresolved, 1, a, record, CancellationToken.None);
        Require(reconciled.Count == 0, "reconcile has no low-space refusal to replay");

        // The same pending item applies on a later run once space is available, and the stored notice then closes
        // through the run's ordinary closure (SyncRunSteps.CloseResolvedNotices): the reason does not await
        // acknowledgement and its path is evaluated and Done, so no separate executor close is needed.
        var retried = await Apply(executor, Plan(blocked), a, new RecordingWriter(b));
        Require(retried.Single().Outcome == Outcome.Done,
            "the same pending item applies on a later run once space is available");
        Require(await b.ObserveAsync("blocked", CancellationToken.None) is not null,
            "the retry writes the item");
        var retryNotice = store.OpenNotices("p1").Single(item => item.Path == "blocked");
        Require(!PlanNotices.AwaitsAcknowledgement(retryNotice),
            "a low-space notice is closable by a later clean sync");
        store.CloseNotice(retryNotice.NoticeId!, "resolved");
        Require(store.OpenNotices("p1").All(item => item.Path != "blocked"),
            "the low-space notice closes without touching unrelated notices");
    }

    private static async Task FreeSpace_ReservationIsReleasedOnEveryExit()
    {
        await FreeSpace_CancelledAdmissionRecordsNothing();
        var (store, a, b, _) = Rig();
        var executor = new Executor(store, Settings(4), Admission());

        var success = new TrackingReservation();
        var successSource = a.Seed("success", "success-bytes", versionId: "v1");
        var successRecord = new RecordingWriter(b)
        {
            FreeSpaceAdmissionFor = _ => new FreeSpaceAdmission(success, null),
        };
        var successResults = await Apply(executor,
            Plan(Act(ActionKind.CopyNew, Side.B, "success", source: successSource)), a, successRecord);
        Require(successResults.Single().Outcome == Outcome.Done && success.Disposed == 1,
            "a successful effect releases its free-space reservation");

        b.FailNextWrites = 1;
        var failure = new TrackingReservation();
        var failureSource = a.Seed("failure", "failure-bytes", versionId: "v1");
        var failureRecord = new RecordingWriter(b)
        {
            FreeSpaceAdmissionFor = _ => new FreeSpaceAdmission(failure, null),
        };
        var failureResults = await Apply(executor,
            Plan(Act(ActionKind.CopyNew, Side.B, "failure", source: failureSource)), a, failureRecord);
        Require(failureResults.Single().Outcome == Outcome.Failed && failure.Disposed == 1,
            "a failed effect releases its free-space reservation");

        var cancelled = new TrackingReservation();
        var heldSource = a.Seed("held", "held-bytes", versionId: "v1");
        var heldRecord = new RecordingWriter(b)
        {
            HoldPutsExcept = "other",
            FreeSpaceAdmissionFor = _ => new FreeSpaceAdmission(cancelled, null),
        };
        using var stop = new CancellationTokenSource();
        var steps = executor.ApplyAsync(Plan(Act(ActionKind.CopyNew, Side.B, "held", source: heldSource)),
            a, heldRecord, stop.Token).GetAsyncEnumerator();
        var next = steps.MoveNextAsync();
        await heldRecord.PutHeld.WaitAsync(HeldEffectTimeout);
        stop.Cancel();
        try
        {
            await next;
        }
        catch (OperationCanceledException)
        {
            // A cancelled run surfaces the cancellation to the caller.
        }
        await steps.DisposeAsync();
        Require(cancelled.Disposed == 1,
            "a cancelled effect releases its free-space reservation before the run drains");
    }

    private static async Task FreeSpace_CancelledAdmissionRecordsNothing()
    {
        var (store, a, b, _) = Rig();
        var admission = Admission();
        var executor = new Executor(store, Settings(4), admission);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var record = new RecordingWriter(b)
        {
            FreeSpaceAdmissionAsyncFor = async (_, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FreeSpaceAdmission.Granted;
            },
        };
        var source = a.Seed("waiting-space", "bytes", versionId: "v1");
        using var stop = new CancellationTokenSource();
        await using var steps = executor.ApplyAsync(
            Plan(Act(ActionKind.CopyNew, Side.B, source.Path, source: source)), a, record, stop.Token)
            .GetAsyncEnumerator();
        var next = steps.MoveNextAsync();
        await entered.Task.WaitAsync(HeldEffectTimeout);
        stop.Cancel();
        try { await next; }
        catch (OperationCanceledException) { }
        Require(store.UnresolvedIntents("p1").Count == 0 && store.OpenNotices("p1").Count == 0 && record.Puts == 0,
            "cancelling the async free-space wait records no intent or refusal and attempts no effect");
        Require(admission.Snapshot().ActiveFiles == 0,
            "cancelling the async free-space wait releases its transfer lease");
    }

    private sealed class TrackingReservation : IDisposable
    {
        private int _disposed;
        internal int Disposed => Volatile.Read(ref _disposed);

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}
