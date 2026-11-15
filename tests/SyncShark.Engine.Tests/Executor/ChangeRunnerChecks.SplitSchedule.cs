using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitFailureCompositionAsync(string mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        fixture.Local.Seed(FolderEntry("Destination", row.Synced!.LocalId), null);
        fixture.Dropbox.Seed(FolderEntry(PathNames.Child(source.Path, "Child"), "child-id"), null);
        var root = new FolderMove(source.Id, source.Id, null, source.Path, "Destination", null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        var incoming = RemoteEntry("Failing", "failing-id");
        fixture.Dropbox.Seed(incoming, [1]);
        SeedRemote(fixture, incoming);
        var primary = new InvalidOperationException("synthetic lane failure");
        var secondary = new InvalidOperationException("synthetic walk failure");
        var callback = new InvalidOperationException("synthetic cancellation callback failure");
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var walkFaultObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        CancellationTokenRegistration registration = default;
        var events = new System.Collections.Concurrent.ConcurrentQueue<RunEvent>();
        var remote = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = (_, _, _) => Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted("move-job")),
            ReadMove = async (_, _, _, ct) =>
            {
                // The walk's TaskSet cancels its token only after recording an actual member fault.
                registration = ct.Register(() =>
                {
                    if (mode == "callback") throw callback;
                    walkFaultObserved.TrySetResult();
                });
                polling.TrySetResult();
                try
                {
                    await laneStarted.Task.WaitAsync(ct);
                    if (mode == "secondary") throw secondary;
                    await release.Task.WaitAsync(ct);
                    throw new InvalidOperationException("the pending poll was not cancelled");
                }
                finally { exited = true; }
            }
        };
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            BeforeStage = async (path, ct) =>
            {
                if (path != incoming.Path) return;
                await polling.Task.WaitAsync(ct);
                laneStarted.TrySetResult();
                if (mode == "secondary") await walkFaultObserved.Task.WaitAsync(ct);
                throw primary;
            }
        };
        var run = LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with
            {
                Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox), Events = events.Enqueue
            }).SyncWorkAsync(guard.Token);
        Exception? caught = null;
        var endedAtReturn = false;
        try { await run.WaitAsync(guard.Token); endedAtReturn = exited; }
        catch (Exception error) { caught = error; endedAtReturn = exited; }
        finally
        {
            release.TrySetResult();
            try { await run; } catch { }
            registration.Dispose();
        }
        Check.Require(ReferenceEquals(caught, primary) && endedAtReturn,
            "a lane's primary failure survives the split walk join, which completes before return");
        Check.Require(fixture.Inner.FolderMoves(PairId, source.Id).Single() is { JobId: "move-job", Launch.Count: 1 },
            "the failed opportunity preserves accepted split job custody");
        var failures = events.Where(value => value.Kind == RunEventKind.Failed).Select(value => value.Error).ToArray();
        if (mode == "callback")
            Check.Require(failures.Length == 1 && failures[0] is AggregateException aggregate &&
                aggregate.InnerExceptions.Count == 1 && ReferenceEquals(aggregate.InnerExceptions[0], callback),
                "a throwing cancellation callback is reported once as its aggregate without skipping the walk join");
        else if (mode == "secondary")
            Check.Require(failures.Length == 1 && ReferenceEquals(failures[0], secondary),
                "an already-faulted walk is reported once without replacing the lane's primary failure");
        else Check.Require(failures.Length == 0, "expected split-walk cancellation is not a secondary failure event");
    }

    private static async Task SplitHeldAncestorOpportunityAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var ancestor = row.Dropbox!;
        var child = FolderEntry(PathNames.Child(ancestor.Path, "Child"), "child-id");
        fixture.Dropbox.Seed(child, null);
        SeedRemote(fixture, ancestor);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, ancestor.Path,
                new IssueSubject([new(Side.Dropbox, ancestor.Path, ancestor.Id)], Side.Dropbox))]
        });
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(child.Id, child.Id, null,
            child.Path, "Destination", "destination-id", FolderMovePhase.Relabelled, null, null, 0));
        var reads = 0;
        var remote = new ProxySide(fixture.Dropbox)
        {
            BeforeRead = async (path, _) =>
            {
                if (path != child.Path) return;
                reads++;
                await Task.Yield();
            }
        };
        var walks = fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots));
        var readiness = fixture.Hooks.Count(nameof(IStateStore.HasPendingUnder));
        var freezes = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), events,
            configure: live => live with { Local = context.Local, Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox) })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(reads == 1 && fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots)) - walks == 1 &&
            fixture.Hooks.Count(nameof(IStateStore.HasPendingUnder)) - readiness == 1 &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == freezes &&
            fixture.Inner.FolderMoves(PairId, child.Id).Count == 1 &&
            fixture.Inner.Issues(PairId).Single().Path == ancestor.Path &&
            events.All(value => value.Kind != RunEventKind.Failed),
            "one asynchronous cleanup read waits behind held ancestor work without a second walk, failure or custody loss: " +
            $"reads={reads}, walks={fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots)) - walks}, " +
            $"readiness={fixture.Hooks.Count(nameof(IStateStore.HasPendingUnder)) - readiness}, " +
            $"freezes={fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - freezes}, " +
            $"rows={fixture.Inner.FolderMoves(PairId, child.Id).Count}, " +
            $"issues={string.Join(',', fixture.Inner.Issues(PairId).Select(issue => issue.Path + '/' + issue.Cause))}, " +
            $"failures={events.Count(value => value.Kind == RunEventKind.Failed)}");
    }

    private static async Task SplitEndedAcrossPassesAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var source = row.Dropbox!;
        fixture.Dropbox.Seed(FolderEntry("Destination", "destination-id"), null);
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
            source.Path, "Destination", "destination-id", FolderMovePhase.Filling, null, null, 0));
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, "Destination", null, true) })]
        });
        var attempts = 0;
        var remote = IdentitySide.Truthful(new ProxySide(fixture.Dropbox)
        {
            ReadFault = path =>
            {
                if (path != source.Path) return null;
                attempts++;
                return new ItemFailedException(source.Path, TextKey.FailMoveInParts, null);
            }
        }, fixture.Dropbox);
        void SeedIndependent(int index)
        {
            var entry = RemoteEntry("Independent" + index, "independent-" + index);
            fixture.Dropbox.Seed(entry, [1]);
            SeedRemote(fixture, entry);
        }
        SeedIndependent(0);
        var retired = 0;
        var passes = 0;
        var walks = fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots));
        fixture.Hooks.AfterCall = (method, _, result) =>
        {
            if (method != nameof(IStateStore.EndWorkPass)) return;
            passes++;
            if (result is not true) return;
            retired++;
            if (retired < 3) SeedIndependent(retired);
        };
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with { Local = context.Local, Dropbox = remote })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var walked = fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots)) - walks;
        Check.Require(attempts == 1 && retired == 3 && passes == 4 && walked >= 2 && walked <= 1 + retired &&
            fixture.Inner.FolderMoves(PairId, source.Id).Count == 1 &&
            fixture.Inner.MoveResponsibility(PairId, source.Id)?.Cause == TextKey.FailMoveInParts,
            "three progress passes restart walks but an ended root is attempted once and retains its hold: " +
            $"attempts={attempts}, retired={retired}, passes={passes}, walks={walked}");
    }

    private static async Task SplitBlockedReadinessAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var source = row.Dropbox!;
        var scripted = new ScriptedJobSide(fixture.Dropbox) { Accepting = true };
        var yields = 0;
        var blockedReads = 0;
        var blocked = FolderEntry("Blocked", "z-root");
        var remote = new ProxySide(scripted)
        {
            BeforeRead = async (path, _) =>
            {
                if (path == blocked.Path) blockedReads++;
                if (path != source.Path) return;
                yields++;
                await Task.Yield();
            }
        };
        context = context with { Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox) };
        fixture.Dropbox.Seed(blocked, null);
        var pending = RemoteEntry(PathNames.Child(blocked.Path, "Held"), "held-id");
        fixture.Dropbox.Seed(pending, [1]);
        SeedRemote(fixture, pending);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, pending.Path,
                new IssueSubject([new(Side.Dropbox, pending.Path, pending.Id)], Side.Dropbox))]
        });
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with { Local = context.Local, Dropbox = context.Dropbox })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(fixture.Inner.HasPendingUnder(PairId, blocked.Path),
            "the held descendant remains a readiness blocker after unrelated setup work drains");
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        for (var index = 0; index < 3; index++)
            scripted.Script.Enqueue(new ItemFailedException(source.Path, TextKey.DropboxRefused, null));
        var partial = await jobs.Items.Folders.RemoveCheckedAsync(new FolderRemovalRequest(row, Side.Dropbox, FolderRemovalRoute.SplitCleanup)
            { ExpectedRootId = source.Id, Live = true, Accepts = FolderRemovalRequest.EmptyPlan }, guard.Token);
        Check.Require(partial.Removal?.Status == FolderRemovalStatus.Partial && scripted.Observations == 3,
            "the earlier root really reaches all scripted refusal observations before the measured opportunity");
        foreach (var root in new[] { source, blocked })
            fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(root.Id, root.Id, null,
                root.Path, "Destination/" + root.Id, "destination-" + root.Id, FolderMovePhase.Relabelled, null, null, 0));
        scripted.Script.Clear();
        for (var index = 0; index < 3; index++)
            scripted.Script.Enqueue(new ItemFailedException(source.Path, TextKey.DropboxRefused, null));
        var observations = scripted.Observations;
        var walks = fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots));
        var readiness = 0;
        yields = 0;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.HasPendingUnder) && args.OfType<string>().Contains(blocked.Path))
                readiness++;
        };
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with { Local = context.Local, Dropbox = context.Dropbox })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(yields > 0 && readiness == 1 && blockedReads == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots)) - walks == 1 &&
            scripted.Observations - observations == 3 && scripted.Script.Count == 0 &&
            fixture.Inner.FolderMoveRoots(PairId, default).Count() == 2,
            "an asynchronous nonadvancing earlier root does not restart a blocked root's readiness check");
    }

    private static async Task SplitReleasedReadinessAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var source = row.Dropbox!;
        var remote = RemoteEntry(PathNames.Child(source.Path, "Deleted"), "remote-deleted");
        var local = LocalEntry(remote.Path, "local-deleted");
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var changed = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, local.Path, null, local.Id, null, false, false), page: 2);
        Check.Require(changed.AnchorWorkId > ((WorkSelection)context.Opportunity).AnchorWorkId,
            "the second journal page commits genuinely later descendant work");
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
            source.Path, "Destination", "destination-id", FolderMovePhase.Relabelled, null, null, 0));
        var readiness = fixture.Hooks.Count(nameof(IStateStore.HasPendingUnder));
        var freezes = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with { Local = context.Local, Dropbox = context.Dropbox })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.HasPendingUnder)) - readiness == 2 &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - freezes == 1 &&
            fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
            fixture.Dropbox.FindById(source.Id) is null && fixture.Dropbox.FindById(remote.Id) is null,
            "lane progress clears descendant work and permits one cleanup after exactly two readiness checks");
    }
}
