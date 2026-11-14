using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static partial class EngineHostChecks
{
    // Row 43a: the host commits a respelling only for its own attempt, token and an unsettled attempt, and leaves that
    // attempt uncompleted so the content attempt on the same wait is admitted in the same opportunity. A real no-wait
    // Local-origin rename's subject names its saved Dropbox counterpart: a concurrent attempt on that item waits for it
    // and is admitted after it, never refused for good.
    private static async Task ItemRenameAuthorityAsync(string root)
    {
        using var instance = Lock(root, "item-rename");
        const string PairId = "item-rename-pair";
        const string Path = "held.txt";
        var inner = new InMemoryStateStore();
        inner.SavePair(Paired(PairId));
        var store = new RenameRecordingStore(inner);
        await using var host = Compose(instance.StateAuthority, store);
        var subject = new IssueSubject([new IssueItem(Side.Local, Path, "local-1"),
            new IssueItem(Side.Dropbox, Path, "dropbox-1")], null);
        static bool Stale(Action write)
        {
            try { write(); return false; }
            catch (LiveAdmissionStaleException) { return true; }
        }
        static async Task<bool> StaleAsync(Func<Task> attempt)
        {
            try { await attempt().ConfigureAwait(false); return false; }
            catch (LiveAdmissionStaleException) { return true; }
        }
        var respelling = StateChange.Empty;
        await host.DispatchAsync(PairId, (registration, pair, generation, token) =>
            host.ExecuteItemAttemptAsync(registration, registration, null, subject, (attemptId, ct) =>
            {
                var source = inner.ReadSource(PairId, Side.Local) ?? throw new InvalidOperationException();
                var reported = host.ReportItemFailure(registration, attemptId, source.Epoch, Path, subject,
                    TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                Check.Require(Stale(() => host.CommitItemRename(registration, attemptId, reported, respelling, null)),
                    "43a: a reported attempt commits no respelling");
                return Task.FromResult(true);
            }, token), CancellationToken.None).ConfigureAwait(false);
        var candidate = inner.ReadItemRetryForSubject(PairId, inner.ReadPairExecution(PairId)!.Value.Generation, Path,
            subject) ?? throw new InvalidOperationException();
        var continued = await host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
        {
            await host.ExecuteItemAttemptAsync(registration, registration, candidate, subject, (attemptId, ct) =>
            {
                Check.Require(Stale(() => host.CommitItemRename(registration, Guid.NewGuid(), candidate, respelling, null)),
                    "43a: an unregistered attempt commits no respelling");
                Check.Require(Stale(() => host.CommitItemRename(registration, attemptId, candidate with
                        { Token = candidate.Token with { Revision = candidate.Token.Revision + 1 } }, respelling, null)),
                    "43a: a respelling commits only under the attempt's own token");
                host.CommitItemRename(registration, attemptId, candidate, respelling, null);
                return Task.FromResult(true);
            }, token).ConfigureAwait(false);
            Check.Require(store.Renames == 1, "43a: the registered attempt's respelling reaches the store once");
            // The same wait's content attempt follows in this opportunity, then settles as an ordinary continuing failure.
            return await host.ExecuteItemAttemptAsync(registration, registration, candidate, subject, (attemptId, ct) =>
            {
                host.CompleteItemAttempt(registration, attemptId, candidate, StateChange.Empty with
                    { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, Path, subject)] }, null, null);
                Check.Require(Stale(() => host.CommitItemRename(registration, attemptId, candidate, respelling, null)),
                    "43a: a completed attempt commits no respelling");
                return Task.FromResult(true);
            }, token).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
        Check.Require(continued && store.Renames == 1 && inner.Issues(PairId, [IssueKind.Failure]).Count == 1,
            "43a: after the respelling the content attempt on the same wait is admitted in the same opportunity");

        // A real Local-origin no-wait rename through RenameRunner: while it runs, an attempt on its saved Dropbox
        // counterpart is refused, and that attempt is admitted once the rename ends.
        await Massdex.SyncShark.Engine.Tests.Executor.RenameRunnerChecks.WithLocalFollowAsync(
            async (renameStore, renamePair, saved, follow) =>
        {
            using var renameInstance = Lock(root, "item-rename-follow");
            await using var renameHost = Compose(renameInstance.StateAuthority, renameStore);
            var counterpart = new IssueSubject([new IssueItem(Side.Dropbox, saved.DropboxPath, saved.DropboxId)], null);
            bool? held = null;
            var admitted = await renameHost.DispatchAsync(renamePair, async (registration, pair, generation, token) =>
            {
                var port = new CompetingItemAttempts(renameHost.ItemAttemptsFor(registration), async ct => held ??=
                    await StaleAsync(() => renameHost.ExecuteItemAttemptAsync(registration, registration, null, counterpart,
                        (_, _) => Task.FromResult(true), ct)).ConfigureAwait(false));
                Check.Require(await follow(port, registration).ConfigureAwait(false) == RenameKind.Follow,
                    "43a: the Local-origin rename follows");
                return await renameHost.ExecuteItemAttemptAsync(registration, registration, null, counterpart,
                    (_, _) => Task.FromResult(true), token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
            Check.Require(held == true, "43a: a running Local-origin rename holds its saved Dropbox counterpart");
            Check.Require(admitted, "43a: the counterpart's own attempt is admitted once the rename attempt ends");
        }).ConfigureAwait(false);
    }

    // Runs one competing attempt inside the first attempt the wrapped authority admits, then forwards everything.
    private sealed class CompetingItemAttempts(IItemAttempts inner, Func<CancellationToken, Task> compete) : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            inner.ExecuteAsync(opportunity, expected, subject, async (attempt, token) =>
            {
                await compete(token).ConfigureAwait(false);
                return await execute(attempt, token).ConfigureAwait(false);
            }, ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) =>
            inner.ReportFailure(attemptId, sourceEpoch, path, subject, cause, selection, dispatch, expectedPending,
                expectedEpisode, settledIssue, committedIssue);
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change, PendingEffect? completedPending,
            WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) =>
            inner.Complete(attemptId, expected, change, completedPending, selection, committedIssue, settledIssue);
        public void CommitRename(Guid attemptId, ItemRetryCandidate expected, StateChange change, WorkSelection? selection) =>
            inner.CommitRename(attemptId, expected, change, selection);
        public (bool NewlyAccepted, DateTimeOffset? RetryAt, long? Wait) ReportTemporary(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, TimeSpan? providerWait,
            long? providerWaitStarted) => inner.ReportTemporary(attemptId, sourceEpoch, path, subject, cause, selection,
                dispatch, providerWait, providerWaitStarted);
        public bool RetryPending(long wait) => inner.RetryPending(wait);
        public bool HeldForRetry(Side side, string key) => inner.HeldForRetry(side, key);
    }

    private sealed class RenameRecordingStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal int Renames { get; private set; }

        public override void CommitItemRename(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
            WorkSelection? selection) => Renames++;
    }
}
