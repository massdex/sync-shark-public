using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static void SeedSplitHold(Fixture fixture, Entry source, Entry local) =>
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Local, local.Path, local.Id), new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, local.Path, null, true) })]
        });

    private static async Task SplitPollingBesideLanesAsync(bool resumed)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var source = row.Dropbox!;
        var destination = FolderEntry("Destination", row.Synced!.LocalId);
        fixture.Local.Seed(destination, null);
        var children = Enumerable.Range(0, 2).Select(index =>
            RemoteEntry(PathNames.Child(source.Path, "File" + index), "remote-" + index)).ToArray();
        for (var index = 0; index < children.Length; index++)
        {
            var before = LocalEntry(children[index].Path, "local-" + index);
            fixture.Dropbox.Seed(children[index], [1]);
            fixture.Local.Seed(before with { Path = PathNames.Child(destination.Path, "File" + index) }, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(before, children[index])] });
        }
        var upload = LocalEntry("LargeUpload", "upload-id");
        if (resumed)
        {
            fixture.Dropbox.Seed(destination with { Id = "destination-id" }, null);
            fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
                source.Path, destination.Path, "destination-id", FolderMovePhase.Filling,
                children.Select(child => (child.Id, PathNames.Name(child.Path))).ToArray(), "move-job", 0));
            SeedSplitHold(fixture, source, destination);
            fixture.Local.Seed(upload, [1]);
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, upload.Path, null, upload.Id, upload, false, false), page: 2);
        }
        else
        {
            fixture.Dropbox.MoveEntryLimit = 1;
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, destination.Path, source.Path,
                destination.Id, destination, false, false), page: 2);
            Check.Require(!fixture.Inner.FolderMoveRoots(PairId, default).Any(),
                "the lane-started fixture has no split custody at opportunity start");
        }
        var firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independent = RemoteEntry("Independent", "independent-id");
        var independentCommitted = false;
        var uploadActive = false;
        var selectedWhileUploadActive = false;
        var order = new List<string>();
        var polls = 0;
        var rootAttempts = 0;
        var clock = (FakeTimeProvider)context.Time;
        var remote = new ProxySide(fixture.Dropbox)
        {
            BeforeStage = async (path, ct) =>
            {
                if (path != upload.Path) return;
                uploadActive = true;
                uploadStarted.TrySetResult();
                await uploadReleased.Task.WaitAsync(ct);
            },
            SubmitMove = (_, _, _) => Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted("move-job")),
            ReadMove = async (_, _, _, ct) =>
            {
                Check.Require(++polls < 50, "unrelated work commits before the bounded poll gate is exhausted");
                if (polls == 1)
                {
                    if (resumed) await uploadStarted.Task.WaitAsync(ct);
                    firstPoll.TrySetResult();
                    return new FolderJobResult.InProgress();
                }
                await Task.Yield();
                if (!independentCommitted) return new FolderJobResult.InProgress();
                order.Add("terminal");
                var receipts = new List<FolderMemberResult>();
                for (var index = 0; index < children.Length; index++)
                {
                    var moved = await fixture.Dropbox.MoveAsync(children[index],
                        PathNames.Child(destination.Path, PathNames.Name(children[index].Path)), ct);
                    receipts.Add(new FolderMemberResult.Receipt(index, moved));
                }
                return new FolderJobResult.Terminal(receipts);
            }
        };
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.FolderMoves) && args.OfType<string>().Contains(source.Id) &&
                fixture.Inner.FolderMoves(PairId, source.Id).Any(move => move.Phase < FolderMovePhase.Relabelled))
                rootAttempts++;
        };
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.SelectWork) && result is WorkSelection selected && selected.Key == PathNames.KeyFor(selected.Side, independent.Path))
                selectedWhileUploadActive |= uploadActive;
            foreach (var change in args.OfType<StateChange>())
            foreach (var saved in change.Record)
            {
                if (saved.LocalPath == upload.Path)
                {
                    uploadActive = false;
                    order.Add("upload");
                    uploadCommitted.TrySetResult();
                }
                if (saved.DropboxPath == independent.Path && !independentCommitted)
                {
                    independentCommitted = true;
                    order.Add("independent");
                }
            }
        };
        var run = LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with
            {
                Local = context.Local, Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox), Time = clock,
                Settings = live.Settings with { FolderDeleteMinimumPollSeconds = 1 }
            }).SyncWorkAsync(guard.Token);
        try
        {
            await firstPoll.Task.WaitAsync(guard.Token);
            while (!clock.HasArmedTimer)
            {
                guard.Token.ThrowIfCancellationRequested();
                if (run.IsCompleted) await run;
                await Task.Yield();
            }
            fixture.Dropbox.Seed(independent, [1]);
            SeedRemote(fixture, independent);
            if (resumed)
            {
                uploadReleased.TrySetResult();
                await uploadCommitted.Task.WaitAsync(guard.Token);
            }
            while (!run.IsCompleted)
            {
                guard.Token.ThrowIfCancellationRequested();
                await Task.Yield();
                if (clock.NextDueIn is { } due) clock.Advance(due);
            }
            await run;
        }
        finally
        {
            uploadReleased.TrySetResult();
            if (!run.IsCompleted) guard.Cancel();
            try { await run; } catch { }
        }
        Check.Require(independentCommitted && !selectedWhileUploadActive && polls is >= 2 and < 50 && rootAttempts == 1 &&
            order.SequenceEqual(resumed ? new[] { "upload", "independent", "terminal" } : new[] { "independent", "terminal" }) &&
            fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 && fixture.Inner.Issues(PairId).Count == 0,
            "the split polls beside ordinary passes, which commit unrelated work before terminal settlement: " +
            $"resumed={resumed}, polls={polls}, attempts={rootAttempts}, order={string.Join(',', order)}");
    }

    private static async Task SplitSerialRootsAsync(bool lateRoot, bool nonAdvancing)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var first = row.Dropbox!;
        var second = FolderEntry("Second", lateRoot ? "a-before" : "z-after");
        var firstLocal = FolderEntry("Destination", row.Synced!.LocalId);
        var secondLocal = FolderEntry("SecondDestination", "second-local");
        fixture.Local.Seed(firstLocal, null);
        fixture.Local.Seed(secondLocal, null);
        fixture.Dropbox.Seed(second, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(secondLocal with { Path = second.Path }, second)]
        });
        foreach (var (source, local) in new[] { (first, firstLocal), (second, secondLocal) })
        {
            for (var index = 0; index < 2; index++)
            {
                var child = RemoteEntry(PathNames.Child(source.Path, "File" + index), source.Id + "-" + index);
                var before = LocalEntry(child.Path, "local-" + child.Id);
                fixture.Dropbox.Seed(child, [1]);
                fixture.Local.Seed(before with { Path = PathNames.Child(local.Path, "File" + index) }, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(before, child)] });
            }
        }
        fixture.Dropbox.MoveEntryLimit = 1;
        if (nonAdvancing) fixture.Dropbox.Seed(firstLocal with { Id = "destination-id" }, null);
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(first.Id, first.Id, null,
            first.Path, firstLocal.Path, nonAdvancing ? "destination-id" : null,
            nonAdvancing ? FolderMovePhase.Filling : FolderMovePhase.Armed,
            nonAdvancing ? new[] { (first.Id + "-0", "File0"), (first.Id + "-1", "File1") } : null,
            nonAdvancing ? "first-job" : null, 0));
        SeedSplitHold(fixture, first, firstLocal);
        if (!lateRoot)
        {
            fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(second.Id, second.Id, null,
                second.Path, secondLocal.Path, null, FolderMovePhase.Armed, null, null, 0));
            SeedSplitHold(fixture, second, secondLocal);
        }
        else
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, secondLocal.Path, second.Path,
                secondLocal.Id, secondLocal, false, false), page: 2);
        var independent = RemoteEntry("Independent", "independent-id");
        fixture.Dropbox.Seed(independent, [1]);
        SeedRemote(fixture, independent);
        var independentCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPolling = false;
        var secondFilled = false;
        var firstEnded = false;
        var pollsActive = 0;
        var maxPolls = 0;
        var firstPolls = 0;
        var secondPolls = 0;
        var walksBefore = fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots));
        var secondWalk = 0;
        var remote = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = (members, _, _) =>
            {
                var isFirst = members[0].From == first.Id + "-0";
                Check.Require(isFirst || firstEnded, "the second root cannot submit before the first root's pending job ends");
                return Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted(isFirst ? "first-job" : "second-job"));
            },
            ReadMove = async (job, _, _, ct) =>
            {
                pollsActive++;
                maxPolls = Math.Max(maxPolls, pollsActive);
                var isFirst = job == "first-job";
                try
                {
                    if (isFirst)
                    {
                        firstPolls++;
                        firstPolling = true;
                        if (lateRoot) await secondArmed.Task.WaitAsync(ct);
                        await independentCommitted.Task.WaitAsync(ct);
                        if (nonAdvancing) throw new ItemFailedException(first.Path, TextKey.DropboxRefused, null);
                    }
                    else secondPolls++;
                    var source = isFirst ? first : second;
                    var target = isFirst ? firstLocal : secondLocal;
                    var receipts = new List<FolderMemberResult>();
                    for (var index = 0; index < 2; index++)
                    {
                        var child = fixture.Dropbox.FindById(source.Id + "-" + index)!;
                        var moved = await fixture.Dropbox.MoveAsync(child, PathNames.Child(target.Path, "File" + index), ct);
                        receipts.Add(new FolderMemberResult.Receipt(index, moved));
                    }
                    return new FolderJobResult.Terminal(receipts);
                }
                finally { if (isFirst) firstEnded = true; pollsActive--; }
            }
        };
        fixture.Hooks.AfterCall = (method, args, _) =>
        {
            if (method == nameof(IStateStore.SetFolderMove) && args.LastOrDefault() is FolderMove move && move.SourceId == second.Id)
            {
                if (move.Phase == FolderMovePhase.Armed)
                {
                    Check.Require(firstPolling, "the earlier-sorting root is armed while the existing walk is busy");
                    secondArmed.TrySetResult();
                }
                if (move.Phase == FolderMovePhase.Filling && !secondFilled)
                {
                    secondFilled = true;
                    secondWalk = fixture.Hooks.Count(nameof(IStateStore.FolderMoveRoots)) - walksBefore;
                }
            }
            if (args.OfType<StateChange>().Any(change => change.Record.Any(saved => saved.DropboxPath == independent.Path)))
                independentCommitted.TrySetResult();
        };
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with { Local = context.Local, Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox) })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(maxPolls == 1 && firstPolls == 1 && secondPolls == 1 && independentCommitted.Task.IsCompletedSuccessfully &&
            secondFilled && (!lateRoot || secondWalk >= 2) && fixture.Inner.FolderMoves(PairId, second.Id).Count == 0 &&
            (nonAdvancing
                ? fixture.Inner.FolderMoves(PairId, first.Id).Single() is { Phase: FolderMovePhase.Filling, JobId: "first-job" }
                : fixture.Inner.FolderMoves(PairId, first.Id).Count == 0),
            "one fill slot runs roots serially and revisits a newly armed earlier root even when the first did not advance: " +
            $"late={lateRoot}, nonadvancing={nonAdvancing}, max={maxPolls}, polls={firstPolls}/{secondPolls}, secondWalk={secondWalk}");
    }

    private static async Task SplitReleasesHeldWorkAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var source = row.Dropbox!;
        var destination = FolderEntry("Destination", row.Synced!.LocalId);
        fixture.Local.Seed(destination, null);
        var child = RemoteEntry(PathNames.Child(source.Path, "File"), "remote-child");
        var before = LocalEntry(child.Path, "local-child");
        fixture.Dropbox.Seed(child, [1]);
        fixture.Local.Seed(before with { Path = PathNames.Child(destination.Path, "File") }, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(before, child)] });
        fixture.Inner.SetFolderMove(PairId, Generation, null, new FolderMove(source.Id, source.Id, null,
            source.Path, destination.Path, null, FolderMovePhase.Armed, null, null, 0));
        SeedSplitHold(fixture, source, destination);
        var lane = RemoteEntry("HeldLane", "lane-id");
        fixture.Dropbox.Seed(lane, [1]);
        SeedRemote(fixture, lane);
        var released = RemoteEntry(PathNames.Child(destination.Path, "Released"), "released-id");
        var laneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injectedDuringPass = false;
        var selectedBeforeSettlement = false;
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            BeforeStage = async (path, ct) =>
            {
                if (path != lane.Path) return;
                laneStarted.TrySetResult();
                await rootSettled.Task.WaitAsync(ct);
                throw new ItemFailedException(lane.Path, TextKey.FailChanged, null);
            }
        };
        var remote = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = (_, _, _) => Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted("move-job")),
            ReadMove = async (_, _, _, ct) =>
            {
                await laneStarted.Task.WaitAsync(ct);
                var moved = await fixture.Dropbox.MoveAsync(child, PathNames.Child(destination.Path, "File"), ct);
                return new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, moved)]);
            }
        };
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.SelectWork) && result is WorkSelection selected && selected.Key == PathNames.KeyFor(selected.Side, released.Path))
                selectedBeforeSettlement |= !rootSettled.Task.IsCompleted;
            foreach (var change in args.OfType<StateChange>())
            {
                if (change.SplitRelabel is not null)
                {
                    injectedDuringPass = laneStarted.Task.IsCompleted && !rootSettled.Task.IsCompleted &&
                        fixture.Inner.MoveResponsibility(PairId,
                            fixture.Inner.FolderMoves(PairId, source.Id).Single().DestinationId!) is not null;
                    fixture.Dropbox.Seed(released, [1]);
                    SeedRemote(fixture, released);
                }
                if (change.Record.Any(saved => saved.LocalId == destination.Id && saved.LocalPath == destination.Path))
                    rootSettled.TrySetResult();
            }
        };
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
            configure: live => live with
            {
                Local = IdentitySide.Truthful(fixture.LocalView, fixture.Local),
                Dropbox = IdentitySide.Truthful(remote, fixture.Dropbox)
            }).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(injectedDuringPass && !selectedBeforeSettlement && rootSettled.Task.IsCompletedSuccessfully &&
            fixture.Inner.SyncedById(PairId, Side.Dropbox, released.Id) is not null &&
            fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
            fixture.Inner.Issues(PairId).All(issue => issue.Path == lane.Path && issue.Cause == TextKey.FailChanged),
            "a settling walk releases held work in the same opportunity despite the active lane making no progress");
    }
}
