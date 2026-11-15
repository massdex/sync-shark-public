using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitMoveOpportunityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var destination = "Destination";
        var local = FolderEntry(destination, row.Synced!.LocalId);
        fixture.Local.Seed(local, null);
        for (var index = 0; index < 2; index++)
        {
            var remote = RemoteEntry(PathNames.Child(source.Path, "File" + index), "remote-" + index);
            var savedLocal = LocalEntry(remote.Path, "local-" + index);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Local.Seed(savedLocal with { Path = PathNames.Child(destination, "File" + index) }, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(savedLocal, remote)] });
        }
        fixture.Dropbox.MoveEntryLimit = 1;
        var root = new FolderMove(source.Id, source.Id, null, source.Path, destination, null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Local, destination, local.Id), new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, destination, null, true) })]
        });
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), events,
            configure: live => live with { Local = context.Local, Dropbox = context.Dropbox }).SyncWorkAsync(default);
        var moved = await fixture.Dropbox.ReadOneAsync(destination, default);
        var saved = fixture.Inner.SyncedById(PairId, Side.Local, local.Id);
        Check.Require(moved is not null && moved.Id != source.Id && saved?.DropboxId == moved.Id &&
            saved.LocalPath == destination && saved.DropboxPath == destination &&
            fixture.Inner.Issues(PairId).Count == 0 && fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
            await fixture.Dropbox.ReadOneAsync(source.Path, default) is null,
            "one opportunity fills, atomically relabels, settles the ordinary rename and cleans the empty source");
        for (var index = 0; index < 2; index++)
            Check.Require(fixture.Dropbox.FindById("remote-" + index)?.Path == PathNames.Child(destination, "File" + index) &&
                fixture.Inner.SyncedById(PairId, Side.Dropbox, "remote-" + index)?.DropboxPath ==
                    PathNames.Child(destination, "File" + index),
                "ordinary rename settlement rebases each existing child binding without changing its identity");
    }

    private static async Task SplitUnboundParityAsync(bool boundChildren, bool crash)
    {
        async Task<SyncedEntry?[]> RunAsync(bool split)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var (context, row) = SplitCleanupContext(fixture);
            var source = row.Dropbox!;
            var local = FolderEntry("Destination", row.Synced!.LocalId);
            fixture.Local.Seed(local, null);
            fixture.Inner.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Dropbox, row.Synced.DropboxKey)] });
            for (var index = 0; index < 2; index++)
            {
                var remote = RemoteEntry(PathNames.Child(source.Path, "File" + index), "remote-" + index);
                var before = LocalEntry(remote.Path, "local-" + index);
                fixture.Dropbox.Seed(remote, [1]);
                fixture.Local.Seed(before with { Path = PathNames.Child(local.Path, "File" + index) }, [1]);
                if (boundChildren)
                    fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(before, remote)] });
            }
            if (split)
            {
                fixture.Dropbox.MoveEntryLimit = 1;
                var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                    RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
                await jobs.Actions.ExecuteAsync(new SyncAction(boundChildren ? ActionKind.MoveDropbox : ActionKind.MoveUnlinked,
                    row with { Local = local, Synced = null }, Target: local.Path, Side: boundChildren ? null : Side.Dropbox),
                    null, guard.Token);
                Check.Require(fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase == FolderMovePhase.Armed &&
                    fixture.Inner.MoveResponsibility(PairId, source.Id)?.Cause == TextKey.FailMoveInParts,
                    "the unbound action's oversized refusal arms custody without a lane-side relabel");
            }
            else
            {
                await fixture.Dropbox.MoveAsync(source, local.Path, guard.Token);
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                        new IssueSubject([new(Side.Local, local.Path, local.Id), new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                        { Move = new(source.Id, source.Path, local.Path, null, true) })]
                });
            }
            var relabels = 0;
            var interruption = new InvalidOperationException("synthetic pre-settlement interruption");
            if (split && crash) fixture.Hooks.BeforeCallWith = (_, args) =>
            {
                if (!args.OfType<StateChange>().Any(change => change.SplitRelabel is not null)) return;
                fixture.Hooks.BeforeCallWith = null;
                throw interruption;
            };
            fixture.Hooks.AfterCall = (_, args, _) =>
            {
                if (!args.OfType<StateChange>().Any(change => change.SplitRelabel is not null)) return;
                relabels++;
                Check.Require(fixture.Inner.SyncedById(PairId, Side.Dropbox, source.Id) is null &&
                    fixture.Inner.SyncedById(PairId, Side.Local, local.Id) is null,
                    "an unbound root relabel updates no root binding and leaves ordinary settlement to create it");
            };
            Task Opportunity() => LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), [],
                configure: live => live with { Local = context.Local, Dropbox = context.Dropbox })
                .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            if (split && crash)
            {
                var caught = await ThrowsAsync<InvalidOperationException>(Opportunity);
                Check.Require(ReferenceEquals(caught, interruption) && relabels == 0 &&
                    fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase == FolderMovePhase.Filled,
                    "a crash before the unbound settlement commit keeps Filled custody without relabelling");
                fixture.RestartStore();
            }
            await Opportunity();
            Check.Require(relabels == (split ? 1 : 0) && fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
                fixture.Inner.Issues(PairId).Count == 0,
                "unbound split settlement commits one relabel and releases its custody and held notice");
            var saved = fixture.Inner.SyncedById(PairId, Side.Local, local.Id);
            return new[] { saved is null ? null : saved with { DropboxId = "normalized-root-id" },
                fixture.Inner.SyncedById(PairId, Side.Local, "local-0"),
                fixture.Inner.SyncedById(PairId, Side.Local, "local-1") };
        }
        var ordinary = await RunAsync(false);
        var split = await RunAsync(true);
        Check.Require(ordinary.All(saved => saved is not null) && split.SequenceEqual(ordinary),
            "unlinked and unrecorded-root split settlement matches the completed ordinary move with its commit lost");
    }

    private static async Task SplitSendFenceAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var child = FolderEntry(PathNames.Child(source.Path, "Child"), "child-id");
        fixture.Dropbox.Seed(child, null);
        var root = new FolderMove(source.Id, source.Id, null, source.Path, "Destination", null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        var sends = 0;
        var sourceReads = 0;
        var side = new ProxySide(fixture.Dropbox)
        {
            BeforeRead = (path, _) =>
            {
                if (path == source.Path && ++sourceReads == 2 && mode == "source")
                    fixture.Dropbox.Seed(source with { Id = "replacement-id" }, null);
                return Task.CompletedTask;
            },
            SubmitMove = async (members, attempt, ct) =>
            {
                sends++;
                var result = await fixture.Dropbox.SubmitMoveBatchAsync(members, attempt, ct);
                if (mode == "destination")
                    fixture.Dropbox.Seed(FolderEntry(root.DestinationPath, "replacement-id"), null);
                else
                {
                    var pair = fixture.Inner.Pair(PairId)!;
                    fixture.Inner.SavePairDecision(pair, Generation, pair with { Enabled = false }, null, false, null);
                }
                return result;
            }
        };
        context = context with { Dropbox = IdentitySide.Truthful(side, fixture.Dropbox) };
        Exception? failure = null;
        try { await new FolderMoveRunner(context).FinishAsync([root], default); }
        catch (Exception error) { failure = error; }
        var retained = fixture.Inner.FolderMoves(PairId, source.Id).Single();
        if (mode == "generation")
            Check.Require(failure is LiveAdmissionStaleException && retained.Launch is { Count: 1 } &&
                retained.Launch[0].Id == child.Id && retained.JobId is null && sends == 1,
                "stale post-send writes preserve exact launch custody rather than clearing evidence");
        else
            Check.Require(failure is ItemFailedException { Cause: TextKey.FailChanged } &&
                sends == (mode == "source" ? 0 : 1) && (retained.Launch is null) == (mode == "source"),
                "fresh source and terminal destination identity checks fence effects and preserve uncertain launch custody");
        if (mode == "generation")
        {
            var current = fixture.Inner.ReadPairExecution(PairId)!.Value;
            var resumed = context with { Pair = current.Pair, ExpectedGeneration = current.Generation,
                Dropbox = IdentitySide.Truthful(fixture.Dropbox, fixture.Dropbox) };
            await new FolderMoveRunner(resumed).FinishAsync([retained], default);
            Check.Require(fixture.Dropbox.MoveBatchCalls == 1 &&
                fixture.Inner.FolderMoves(PairId, source.Id).Single() is { Phase: FolderMovePhase.Filled, Launch: null },
                "fresh-generation identity recovery resolves retained launch without resubmission");
        }
    }

    private static async Task SplitNestedOccupancyAsync(int count)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var destination = FolderEntry("Destination", "destination-id");
        fixture.Dropbox.Seed(destination, null);
        fixture.Dropbox.MoveEntryLimit = 1;
        fixture.Dropbox.AggregateMoveLimit = true;
        for (var index = 0; index <= count; index++)
        {
            var name = "Branch" + index.ToString("D2");
            var child = FolderEntry(PathNames.Child(source.Path, name), "branch-" + index);
            fixture.Dropbox.Seed(child, null);
            for (var file = 0; file < 2; file++)
                fixture.Dropbox.Seed(RemoteEntry(PathNames.Child(child.Path, "File" + file), $"file-{index}-{file}"), [1]);
            if (index < count)
                fixture.Dropbox.Seed(FolderEntry(PathNames.Child(destination.Path, name), "occupant-" + index), null);
        }
        var root = new FolderMove(source.Id, source.Id, null, source.Path, destination.Path, destination.Id,
            FolderMovePhase.Filling, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        ItemFailedException? failure = null;
        try { await new FolderMoveRunner(context).FinishAsync([root], default); }
        catch (ItemFailedException error) { failure = error; }
        Check.Require(failure?.Cause == TextKey.FailChanged && fixture.Dropbox.ExclusiveCreates == count + 1,
            "each occupied nested target is attempted once and the later independent sibling still fills");
        for (var index = 0; index < count; index++)
        {
            var name = "Branch" + index.ToString("D2");
            Check.Require(fixture.Dropbox.FindById("occupant-" + index)?.Path == PathNames.Child(destination.Path, name),
                "nested occupancy never adopts or replaces an existing destination identity");
            for (var file = 0; file < 2; file++)
                Check.Require(fixture.Dropbox.FindById($"file-{index}-{file}")?.Path ==
                    PathNames.Child(PathNames.Child(source.Path, name), "File" + file),
                    "every refused child's source content is preserved");
        }
        for (var file = 0; file < 2; file++)
            Check.Require(fixture.Dropbox.FindById($"file-{count}-{file}")?.Path ==
                PathNames.Child(PathNames.Child(destination.Path, "Branch" + count.ToString("D2")), "File" + file),
                "a later sibling completes despite earlier occupied children");
        Console.WriteLine($"split-occupancy children={count} exclusive-creates={fixture.Dropbox.ExclusiveCreates} " +
            $"retained-rows={fixture.Inner.FolderMoves(PairId, source.Id).Count}");
    }

    private static async Task SplitMixedReceiptsAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var root = new FolderMove(source.Id, source.Id, null, source.Path, "Destination", null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        var children = Enumerable.Range(0, mode == "known-before-unknown" ? 3 : mode == "throttle-refusal" ? 2 : 1)
            .Select(index => FolderEntry(PathNames.Child(source.Path, "Child" + index), "child-" + index)).ToArray();
        foreach (var child in children) fixture.Dropbox.Seed(child, null);
        var sends = 0;
        var moved = 0;
        var identities = 0;
        var side = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = async (members, attempt, ct) =>
            {
                sends++;
                Check.Require(sends <= (mode == "throttle-progress" ? 2 : 1), "a definite refusal ends the pass without replay");
                if (mode == "throttle-progress" && sends == 2)
                    return await fixture.Dropbox.SubmitMoveBatchAsync(members, attempt, ct);
                if (mode == "known-before-unknown")
                {
                    var receipt = await fixture.Dropbox.MoveAsync(children[1], members[1].To, ct);
                    return new FolderSubmitResult.Terminal([new FolderMemberResult.Unknown(0, TextKey.DropboxRefused),
                        new FolderMemberResult.Receipt(1, receipt),
                        new FolderMemberResult.KnownNoEffect(2, TextKey.FailDropboxNoPermission)]);
                }
                return new FolderSubmitResult.Terminal(mode == "throttle-progress"
                    ? [new FolderMemberResult.KnownNoEffect(0, TextKey.WaitTooManyWrites)]
                    : [new FolderMemberResult.KnownNoEffect(0, TextKey.WaitTooManyWrites),
                        new FolderMemberResult.KnownNoEffect(1, TextKey.FailDropboxNoPermission)]);
            }
        };
        context = context with
        {
            Dropbox = IdentitySide.Truthful(side, fixture.Dropbox, () =>
            {
                identities++;
                Check.Require(moved == 1, "known receipts are accounted before the first uncertain identity read");
            }),
            Events = value => { if (value.StageKey == TextKey.StageFolderMoveParts) moved++; }
        };
        ItemFailedException? failure = null;
        try { await new FolderMoveRunner(context).FinishAsync([root], default); }
        catch (ItemFailedException error) { failure = error; }
        var final = fixture.Inner.FolderMoves(PairId, source.Id).Single();
        Check.Require(final.Launch is null && final.JobId is null, "mixed certainty is durably accounted before clearing launch");
        if (mode == "throttle-progress")
            Check.Require(failure is null && sends == 2 && final.Phase == FolderMovePhase.Filled && moved == 1,
                "write throttling counts as progress and permits the next pass, not an interruption or refusal");
        else
            Check.Require(failure?.Cause == TextKey.FailDropboxNoPermission && sends == 1 &&
                final.Phase == FolderMovePhase.Filling && identities == (mode == "known-before-unknown" ? 1 : 0) &&
                fixture.Dropbox.FindById(children[^1].Id)?.Path == children[^1].Path,
                "a later definite permission refusal wins over throttling and uncertain source evidence");
    }

    private static async Task SplitMoveReceiptsAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var child = FolderEntry(PathNames.Child(source.Path, "Child"), "child-id");
        fixture.Dropbox.Seed(child, null);
        var target = "Destination";
        var childTarget = PathNames.Child(target, "Child");
        var root = new FolderMove(source.Id, source.Id, null, source.Path, target, null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        var submitted = 0;
        var identities = 0;
        var polls = 0;
        IReadOnlyList<(string From, string To)>? launch = null;
        var stopped = false;
        var side = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = async (members, attempt, ct) =>
            {
                submitted++;
                Check.Require(submitted == 1, "scripted receipt recovery never sends an unplanned second mutation");
                launch = members;
                var durable = fixture.Inner.FolderMoves(PairId, root.SourceId).Single();
                Check.Require(durable.Launch is { Count: 1 } && durable.Launch[0].Id == child.Id && durable.JobId is null,
                    "wire-order launch custody precedes the provider send");
                switch (mode)
                {
                    case "accepted": return new FolderSubmitResult.Accepted("move-job");
                    case "refused": return new FolderSubmitResult.KnownNoEffect(TextKey.DropboxRefused);
                    case "unknown-source": return new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
                    case "unknown-absent":
                        await fixture.Dropbox.DeleteAsync(child, null, ct);
                        return new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
                    case "foreign-conflict":
                        fixture.Dropbox.Seed(FolderEntry(childTarget, "foreign-id"), null);
                        return new FolderSubmitResult.Terminal([new FolderMemberResult.KnownNoEffect(0, TextKey.ClashNameTaken)]);
                    default:
                        await fixture.Dropbox.SubmitMoveBatchAsync(members, attempt, ct);
                        return mode == "own-conflict"
                            ? new FolderSubmitResult.Terminal([new FolderMemberResult.KnownNoEffect(0, TextKey.ClashNameTaken)])
                            : new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
                }
            },
            ReadMove = async (job, count, attempt, ct) =>
            {
                polls++;
                var durable = fixture.Inner.FolderMoves(PairId, root.SourceId).Single();
                Check.Require(job == "move-job" && count == 1 && durable.JobId == job && durable.Launch is { Count: 1 },
                    "accepted job and launch are durable before polling");
                if (!stopped) { stopped = true; throw new OperationCanceledException(); }
                var result = (FolderSubmitResult.Terminal)await fixture.Dropbox.SubmitMoveBatchAsync(launch!, attempt, ct);
                return new FolderJobResult.Terminal(result.Results);
            }
        };
        context = context with { Dropbox = IdentitySide.Truthful(side, fixture.Dropbox, () => identities++) };
        if (mode == "accepted")
        {
            try
            {
                await new FolderMoveRunner(context).FinishAsync([root], default);
                throw new Exception("poll interruption did not propagate");
            }
            catch (OperationCanceledException) { }
            root = fixture.Inner.FolderMoves(PairId, source.Id).Single();
            Check.Require(root.JobId == "move-job" && root.Launch is { Count: 1 },
                "interruption retains the accepted launch for receipt recovery, not resubmission");
        }
        ItemFailedException? failed = null;
        Entry? destination = null;
        try { destination = await new FolderMoveRunner(context).FinishAsync([root], default); }
        catch (ItemFailedException error) { failed = error; }
        var final = fixture.Inner.FolderMoves(PairId, source.Id).Single();
        Check.Require(submitted == 1 && final.Launch is null && final.JobId is null,
            "one send is accounted for and never replayed after an uncertain receipt");
        if (mode is "unknown-source" or "refused")
            Check.Require(failed?.Cause == TextKey.FailMoveInParts && final.Phase == FolderMovePhase.Filling &&
                fixture.Dropbox.FindById(child.Id)?.Path == child.Path,
                "no-effect without progress interrupts with custody, rather than spinning or replaying");
        else if (mode == "foreign-conflict")
            Check.Require(failed?.Cause == TextKey.FailChanged && fixture.Dropbox.FindById(child.Id)?.Path == child.Path &&
                fixture.Dropbox.FindById("foreign-id")?.Path == childTarget,
                "a foreign destination identity is not adopted as successful movement");
        else if (mode == "unknown-absent")
            Check.Require(destination?.Path == target && final.Phase == FolderMovePhase.Filled &&
                fixture.Inner.MoveResponsibility(PairId, child.Id) is { Cause: TextKey.DropboxRefused },
                "unresolved member custody is held before its launch is cleared");
        else
            Check.Require(failed is null && destination?.Path == target && final.Phase == FolderMovePhase.Filled &&
                fixture.Dropbox.FindById(child.Id)?.Path == childTarget,
                "own conflict and by-ID proof complete the same member movement");
        Check.Require(mode == "accepted" ? polls == 2 && identities == 0 :
            identities == (mode.StartsWith("unknown-", StringComparison.Ordinal) ? 1 : 0),
            "accepted jobs poll across interruption; only uncertain members require by-ID reads");
    }
}
