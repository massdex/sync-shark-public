using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitArrivalsCompositionAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
            source.Path, "Destination", null, FolderMovePhase.Armed, null, null, 0));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var passes = 0;
        var walks = 0;
        var carriedAt = -1;
        var remote = new ProxySide(fixture.Dropbox)
        {
            BeforeRead = async (path, ct) =>
            {
                if (path != source.Path) return;
                blocked.TrySetResult();
                try { await release.Task.WaitAsync(ct); }
                finally { exited = true; }
                throw new ItemFailedException(path, TextKey.FailMoveInParts, null);
            }
        };
        void SeedArrival(int index)
        {
            var entry = RemoteEntry("Arrival" + index, "arrival-" + index);
            fixture.Dropbox.Seed(entry, [1]);
            SeedRemote(fixture, entry);
        }
        SeedArrival(0);
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            BeforeStage = async (_, ct) =>
            {
                await blocked.Task.WaitAsync(ct);
                await Task.Yield();
            }
        };
        fixture.Hooks.AfterCall = (method, _, _) =>
        {
            if (method == nameof(IStateStore.FolderMoveRoots) && ++walks == 2) carriedAt = passes;
            if (method != nameof(IStateStore.EndWorkPass)) return;
            passes++;
            SeedArrival(passes);
            if (passes == 3) thirdPass.TrySetResult();
        };
        var run = LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with { Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox) })
            .SyncWorkAsync(guard.Token);
        try
        {
            await thirdPass.Task.WaitAsync(guard.Token);
            Check.Require(!run.IsCompleted && !exited, "sustained arrivals cannot yield while the split walk is outstanding");
            release.TrySetResult();
            var result = await run.WaitAsync(guard.Token);
            Check.Require(result.Revisit && !result.Drained && exited && carriedAt >= 3 && passes == carriedAt + 1 &&
                fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase == FolderMovePhase.Filling,
                $"split advancement suppresses its completion pass; the next arrivals-only pass yields after joining: passes={passes}, carried={carriedAt}");
        }
        finally
        {
            release.TrySetResult();
            guard.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private static async Task SplitStaleCloseCompositionAsync(bool opportunity, bool beforeReread = false)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        fixture.Local.Seed(FolderEntry("Destination", row.Synced!.LocalId), null);
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
            source.Path, "Destination", null, FolderMovePhase.Armed, null, null, 0));
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Local, "Destination", row.Synced.LocalId),
                    new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, "Destination", null, true) })]
        });
        var issue = fixture.Inner.MoveResponsibility(PairId, source.Id)!;
        var events = new List<RunEvent>();
        context = context with { Events = events.Add };
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var automaticClosed = false;
        var raced = false;
        var closeAttempts = 0;
        var followUps = false;
        var requested = new UnknownMoveSettlement(context, jobs.Items);
        requested.Request(issue);
        Task<bool>? automaticRun = null;
        fixture.Hooks.BeforeCallWith = (method, _) =>
        {
            if (beforeReread && !raced && method.Name == nameof(IStateStore.MoveResponsibility) &&
                fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase == FolderMovePhase.Relabelled)
            {
                raced = true;
                // The queued pre-relabel id rereads its updated owner, closes it, then waits for the split's locks.
                automaticRun = requested.SettleRequestedAsync(guard.Token);
                Check.Require(fixture.Inner.IssueById(PairId, issue.Id) is null && !automaticRun.IsCompleted,
                    "automatic exact closure precedes the continuation's owner reread and binding rebase");
            }
            if (method.Name == nameof(IStateStore.CloseUnknownMove)) closeAttempts++;
            if (automaticClosed && method.Name == nameof(IStateStore.ActiveFolderRemovals) &&
                Environment.StackTrace.Contains("ContinueReleasedFollowUpsAsync", StringComparison.Ordinal)) followUps = true;
        };
        var remote = new IdentitySide(fixture.Dropbox, async (id, ct) =>
        {
            var observed = await ((IIdentitySide)context.Dropbox).ReadByIdAsync(id, ct);
            if (!raced && fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase == FolderMovePhase.Relabelled)
            {
                raced = true;
                // The continuation captured its current owner; the automatic route wins its exact close now.
                var current = fixture.Inner.MoveResponsibility(PairId, id)!;
                var automatic = await new UnknownMoveSettlement(context, jobs.Items).SettleAsync(current, true, ct);
                automaticClosed = automatic.Resolved && fixture.Inner.IssueById(PairId, current.Id) is null;
                Check.Require(automaticClosed, "automatic settlement wins the close after relabel");
            }
            return observed;
        });
        if (opportunity)
        {
            var result = await LiveSyncer(fixture, (WorkSelection)context.Opportunity,
                new Boundary(_ => new Lease()), events, configure: live => live with
                { Local = context.Local, Dropbox = remote }).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            Check.Require(result.Drained && followUps,
                "a stale continuation joins without aborting the opportunity and released follow-ups still run");
        }
        else
        {
            var racedJobs = SyncJobs.Create(context with { Dropbox = remote }, new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            var result = await racedJobs.Items.Renames.SettleMoveAsync(issue, guard.Token).WaitAsync(guard.Token);
            Check.Require(result.Outcome?.None == NoneReason.Held && result.Kept && !result.Skipped && result.Advanced,
                "a refused exact close is held without losing the continuation's relabel advancement");
        }
        if (beforeReread)
        {
            Check.Require(automaticRun is not null, "the pre-reread settlement race executed");
            automaticClosed = await automaticRun!.WaitAsync(guard.Token);
        }
        Check.Require(automaticClosed && closeAttempts == (beforeReread ? 1 : 2) && !events.Any(item => item.Kind == RunEventKind.Failed) &&
            fixture.Inner.Issues(PairId).Count == 0 &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("Destination"))?.DropboxPath == "Destination",
            $"the losing continuation neither restores the settled owner nor faults its walk: closes={closeAttempts}");
    }

    private static async Task SplitSettlementCompositionAsync(bool automatic)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        fixture.Local.Seed(FolderEntry("Destination", row.Synced!.LocalId), null);
        var root = new FolderMove(source.Id, source.Id, null, source.Path, "Destination", null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Local, root.DestinationPath, row.Synced!.LocalId),
                    new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, root.DestinationPath, null, true) })]
        });
        var issue = fixture.Inner.MoveResponsibility(PairId, source.Id)!;
        var counters = new StoreCounters(fixture.Inner);
        Issue? closed = null;
        var custodyReads = 0;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.FolderMoves)) custodyReads++;
            if (method.Name == nameof(IStateStore.CloseUnknownMove)) closed = args.OfType<Issue>().Single();
        };
        context = context with { Store = automatic ? counters : fixture.Store };
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        if (automatic)
        {
            var result = await new UnknownMoveSettlement(context, jobs.Items).SettleAsync(issue, true, default);
            Check.Require(!result.Resolved && result.Outcome.None == NoneReason.Held &&
                fixture.Inner.FolderMoves(PairId, source.Id).Single() == root &&
                await fixture.Dropbox.ReadOneAsync(root.DestinationPath, default) is null &&
                fixture.Inner.MoveResponsibility(PairId, source.Id)?.Id == issue.Id &&
                fixture.Dropbox.ExclusiveCreates == 0 && fixture.Dropbox.MoveBatchCalls == 0 &&
                counters.MemberCalls.GetValueOrDefault(nameof(IStateStore.SetFolderMove)) == 0 &&
                counters.MemberCalls.GetValueOrDefault(nameof(IStateStore.FolderMoves)) == 1,
                "automatic settlement detects split custody once and leaves filling to its continuation lane");
        }
        else
        {
            var result = await LiveSyncer(fixture, (WorkSelection)context.Opportunity,
                new Boundary(_ => new Lease()), [], configure: live => live with
                { Local = context.Local, Dropbox = context.Dropbox })
                .ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
            // One indexed custody read per Retry attempt, independent of the split's children or pages.
            Check.Require(custodyReads == 1, $"Retry loads custody exactly once: {custodyReads}");
            var destination = await fixture.Dropbox.ReadOneAsync(root.DestinationPath, default);
            Check.Require(result.Resolved && destination is not null &&
                fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase == FolderMovePhase.Relabelled &&
                closed is not null && closed.Subject.Move?.ExpectedId == destination.Id &&
                fixture.Inner.IssueById(PairId, closed.Id) is null &&
                fixture.Inner.SyncedById(PairId, Side.Dropbox, destination.Id)?.DropboxPath == root.DestinationPath,
                "Retry fills and relabels a split move, then exactly closes its current responsibility: " +
                $"resolved={result.Resolved}, outcome={result.Outcome}, destination={destination}, closed={closed}, " +
                $"custody={string.Join(";", fixture.Inner.FolderMoves(PairId, source.Id))}, " +
                $"issues={string.Join(";", fixture.Inner.Issues(PairId))}, " +
                $"binding={(destination is null ? null : fixture.Inner.SyncedById(PairId, Side.Dropbox, destination.Id))}");
        }
    }
}
