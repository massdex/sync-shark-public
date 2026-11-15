using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunSplitMoves() => RunSplitMovesAsync().GetAwaiter().GetResult();

    private static async Task RunSplitMovesAsync()
    {
        var failures = new List<Exception>();
        foreach (var mode in new[] { "ordinary", "flat", "nested", "mount", "reserved", "case", "occupied", "replacement" })
        {
            try { await SplitMoveAsync(mode); }
            catch (Exception error) { failures.Add(new Exception(mode, error)); }
        }
        foreach (var aggregate in new[] { false, true })
        foreach (var (shape, size) in new[] { ("flat", 2500), ("flat", 5000), ("fanout", 4), ("fanout", 16),
            ("deep", 3), ("deep", 6), ("deep", 4), ("deep", 16) })
        {
            try { await SplitMoveCostAsync(shape, size, aggregate); }
            catch (Exception error) { failures.Add(new Exception($"{shape}/{size}/aggregate={aggregate}", error)); }
        }
        foreach (var mode in new[] { "accepted", "refused", "unknown-source", "unknown-destination", "unknown-absent",
            "own-conflict", "foreign-conflict" })
        {
            try { await SplitMoveReceiptsAsync(mode); }
            catch (Exception error) { failures.Add(new Exception(mode, error)); }
        }
        foreach (var mode in new[] { "throttle-progress", "throttle-refusal", "known-before-unknown" })
        {
            try { await SplitMixedReceiptsAsync(mode); }
            catch (Exception error) { failures.Add(new Exception(mode, error)); }
        }
        foreach (var mode in new[] { "skipped", "advanced", "walk-skipped", "walk-advanced", "occupied" })
        {
            try { await SplitSettlementProgressAsync(mode); }
            catch (Exception error) { failures.Add(new Exception(mode, error)); }
        }
        foreach (var mode in new[] { "source", "destination", "generation" })
        {
            try { await SplitSendFenceAsync(mode); }
            catch (Exception error) { failures.Add(new Exception("send fence " + mode, error)); }
        }
        foreach (var count in new[] { 4, 16 })
        {
            try { await SplitNestedOccupancyAsync(count); }
            catch (Exception error) { failures.Add(new Exception("nested occupancy " + count, error)); }
        }
        foreach (var mode in new[] { "cancel", "callback", "secondary" })
        {
            try { await SplitFailureCompositionAsync(mode); }
            catch (Exception error) { failures.Add(new Exception("split failure composition " + mode, error)); }
        }
        foreach (var (boundChildren, crash) in new[] { (false, false), (false, true), (true, false) })
        {
            try { await SplitUnboundParityAsync(boundChildren, crash); }
            catch (Exception error) { failures.Add(new Exception($"unbound parity children={boundChildren} crash={crash}", error)); }
        }
        foreach (var resumed in new[] { false, true })
        {
            try { await SplitPollingBesideLanesAsync(resumed); }
            catch (Exception error) { failures.Add(new Exception($"split polling beside lanes resumed={resumed}", error)); }
        }
        foreach (var (lateRoot, nonAdvancing) in new[] { (false, false), (true, false), (true, true) })
        {
            try { await SplitSerialRootsAsync(lateRoot, nonAdvancing); }
            catch (Exception error) { failures.Add(new Exception($"serial split roots late={lateRoot} nonadvancing={nonAdvancing}", error)); }
        }
        try { await SplitReleasesHeldWorkAsync(); }
        catch (Exception error) { failures.Add(new Exception("split settlement releases held work", error)); }
        try { await SplitEndedAcrossPassesAsync(); }
        catch (Exception error) { failures.Add(new Exception("ended root across progress passes", error)); }
        try { await SplitBlockedReadinessAsync(); }
        catch (Exception error) { failures.Add(new Exception("blocked root readiness", error)); }
        try { await SplitReleasedReadinessAsync(); }
        catch (Exception error) { failures.Add(new Exception("released root readiness", error)); }
        try { await SplitHeldAncestorOpportunityAsync(); }
        catch (Exception error) { failures.Add(new Exception("held ancestor opportunity", error)); }
        try { await SplitMoveOpportunityAsync(); }
        catch (Exception error) { failures.Add(new Exception("whole opportunity", error)); }
        try { await SplitPartialOpportunitiesAsync(); }
        catch (Exception error) { failures.Add(new Exception("partial cleanup opportunities", error)); }
        try { await SplitPartialOpportunitiesAsync(terminal: true); }
        catch (Exception error) { failures.Add(new Exception("partial then terminal cleanup opportunities", error)); }
        try { await SplitCleanupReachabilityAsync(); }
        catch (Exception error) { failures.Add(new Exception("cleanup reachability", error)); }
        try { await SplitListingReservedMountAsync(); }
        catch (Exception error) { failures.Add(new Exception("real listing reserved mount before arm", error)); }
        foreach (var filling in new[] { false, true })
        {
            try { await SplitListingMalformedAsync(filling); }
            catch (Exception error) { failures.Add(new Exception($"real malformed listing filling={filling}", error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task SplitSettlementProgressAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var root = new FolderMove(source.Id, source.Id, null, source.Path, "Destination", null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, root.DestinationPath, null, true) })]
        });
        var issue = fixture.Inner.MoveResponsibility(PairId, source.Id)!;
        var skipped = mode.EndsWith("skipped", StringComparison.Ordinal);
        var occupied = mode == "occupied";
        if (occupied) fixture.Dropbox.Seed(FolderEntry(root.DestinationPath, "occupant-id"), null);
        else if (!skipped) fixture.Dropbox.Seed(source with { Id = "replacement-id" }, null);
        var counters = new StoreCounters(fixture.Inner);
        context = context with { Store = counters };
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var claim = PairRuntimes.FolderMoveClaim(source.Id);
        if (skipped) Check.Require(PairRuntimes.TryClaimFolderJob(claim), "the fixture owns the split claim");
        try
        {
            if (mode.StartsWith("walk-", StringComparison.Ordinal))
            {
                var ended = new HashSet<string>(StringComparer.Ordinal);
                var advanced = await new FolderMoveContinuation(context, jobs.Items.Renames, jobs.Items.Folders)
                    .RunAsync(ended, default);
                Check.Require(advanced == !skipped && ended.Contains(source.Id) == !skipped,
                    "claim contention skips without ending; a persisted phase transition advances the walk");
            }
            else
            {
                var result = await jobs.Items.Renames.SettleMoveAsync(issue, () => fixture.Inner.Commit(PairId,
                    StateChange.Empty with { Close = [(issue.Path, [issue.Kind])] }), default);
                Check.Require(result.Skipped == skipped && result.Advanced == !skipped &&
                    (skipped ? result.Outcome is null && !result.Kept : occupied ? !result.Kept : result.Kept),
                    "the producer reports contention, phase progress and root retirement independently of settlement");
            }
            Check.Require(counters.MemberCalls.GetValueOrDefault(nameof(IStateStore.FolderMoves)) == 1,
                "settlement loads custody once and the continuation never reloads it to infer progress");
            Check.Require(occupied ? fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 :
                fixture.Inner.FolderMoves(PairId, source.Id).Single().Phase ==
                    (skipped ? FolderMovePhase.Armed : FolderMovePhase.Filling),
                "reported progress corresponds to persisted custody, including progress before refusal");
        }
        finally { if (skipped) PairRuntimes.ReleaseFolderJob(claim); }
    }

    private static async Task SplitMoveCostAsync(string shape, int size, bool aggregate)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        fixture.Dropbox.MoveEntryLimit = 100;
        fixture.Dropbox.AggregateMoveLimit = aggregate;
        var parent = source.Path;
        var folders = 1;
        var files = 0;
        void Files(string path, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var name = PathNames.Child(path, "File" + index);
                fixture.Dropbox.Seed(RemoteEntry(name, "file-" + files++), [1]);
            }
        }
        if (shape == "flat") Files(parent, size);
        else if (shape == "fanout")
        {
            for (var index = 0; index < size; index++)
            {
                var child = FolderEntry(PathNames.Child(parent, "Branch" + index), "folder-" + folders++);
                fixture.Dropbox.Seed(child, null);
                Files(child.Path, 101);
            }
        }
        else
        {
            for (var index = 0; index < size; index++)
            {
                parent = PathNames.Child(parent, "Child");
                fixture.Dropbox.Seed(FolderEntry(parent, "folder-" + folders++), null);
            }
            Files(parent, 1001);
        }
        var counters = new StoreCounters(fixture.Inner);
        context = context with { Store = counters };
        try
        {
            await new FolderMoveRunner(context).MoveAsync(Side.Dropbox, source, "Destination", default);
            throw new Exception("oversized move did not arm");
        }
        catch (ItemFailedException error) when (error.Cause == TextKey.FailMoveInParts) { }
        var rows = counters.FolderMoves(PairId, source.Id);
        await new FolderMoveRunner(context).FinishAsync(rows, default);
        var launches = shape switch
        {
            "flat" => aggregate ? size == 2500 ? 77 : 155 : (size + 999) / 1000,
            "fanout" => (aggregate ? 5 : 3) * size - 1,
            _ => size + (aggregate ? 32 : 2)
        };
        var writes = counters.MemberCalls.GetValueOrDefault(nameof(IStateStore.SetFolderMove));
        Check.Require(fixture.Dropbox.MoveBatchCalls == launches && fixture.Dropbox.ExclusiveCreates == folders &&
            writes == 3 * folders + 2 * launches &&
            counters.MemberCalls.GetValueOrDefault(nameof(IStateStore.FolderMoves)) == 2 &&
            counters.MemberRows.GetValueOrDefault(nameof(IStateStore.FolderMoves)) == 1 &&
            fixture.Inner.FolderMoves(PairId, source.Id).Count == folders &&
            fixture.Inner.FolderMoves(PairId, source.Id).All(move => move.Phase == FolderMovePhase.Filled),
            $"split counts {shape}/{size}/aggregate={aggregate}: launches={fixture.Dropbox.MoveBatchCalls}/{launches}; " +
            $"creates={fixture.Dropbox.ExclusiveCreates}/{folders}; writes={writes}/{3 * folders + 2 * launches}");
        Console.WriteLine($"split-cost shape={shape} size={size} aggregate={aggregate} folders={folders} files={files} " +
            $"launches={launches} creates={fixture.Dropbox.ExclusiveCreates} custody-writes={writes} loaded-rows=1");
    }

    private static async Task SplitCleanupReachabilityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var root = row.Dropbox!;
        var branch = FolderEntry(PathNames.Child(root.Path, "Branch"), "branch-id");
        var child = FolderEntry(PathNames.Child(branch.Path, "Child"), "child-id");
        var sibling = FolderEntry(PathNames.Child(root.Path, "Sibling"), "sibling-id");
        fixture.Dropbox.Seed(branch, null);
        fixture.Dropbox.Seed(child, null);
        fixture.Dropbox.Seed(sibling, null);
        var rootMove = new FolderMove(root.Id, root.Id, null, root.Path, "Destination", "destination-id",
            FolderMovePhase.Relabelled, null, null, 0);
        foreach (var move in new[] { rootMove,
            new FolderMove(branch.Id, root.Id, root.Id, branch.Path, "Destination/Branch", "destination-branch-id",
                FolderMovePhase.Relabelled, null, null, 0),
            new FolderMove(child.Id, root.Id, branch.Id, child.Path, "Destination/Branch/Child", "destination-child-id",
                FolderMovePhase.Relabelled, null, null, 0),
            new FolderMove(sibling.Id, root.Id, root.Id, sibling.Path, "Destination/Sibling", "destination-sibling-id",
                FolderMovePhase.Relabelled, null, null, 0) })
            fixture.Inner.SetFolderMove(PairId, Generation, null, move);
        // Model a covered-listing ancestor wait independently of source work under the cleanup paths.
        var store = new SplitAncestorWaitStore(fixture.Inner, (WorkSelection)context.Opportunity, PathNames.Key(branch.Path));
        context = context with { Store = store };
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        jobs.Ancestors.CoverListedWork(0);
        var waiting = await jobs.Items.Folders.PrepareCheckedAsync(new FolderRemovalRequest(
            Row.ForOneSide(Side.Dropbox, PathNames.Key(child.Path), child, null, [], null, RowContext.Neutral),
            Side.Dropbox, FolderRemovalRoute.SplitCleanup)
        { ExpectedRootId = child.Id, Live = true, Accepts = FolderRemovalRequest.EmptyPlan }, default);
        Check.Require(waiting.Check == FolderRemovalCheck.Waiting, "the child fixture really reaches checked-removal Waiting");
        var refused = await jobs.Items.Folders.PrepareCheckedAsync(new FolderRemovalRequest(row,
            Side.Dropbox, FolderRemovalRoute.SplitCleanup)
        { ExpectedRootId = root.Id, Live = true, Accepts = FolderRemovalRequest.EmptyPlan }, default);
        Check.Require(refused.Check == FolderRemovalCheck.NotAccepted, "the root's nonempty plan really is NotAccepted");
        var first = await new FolderMoveCleanup(context, jobs.Items.Folders).RunAsync(root.Id, () => { }, default);
        Check.Require(!first.Ended && fixture.Inner.FolderMoveRoots(PairId, default).Single().SourceId == root.Id &&
            fixture.Inner.FolderMoves(PairId, root.Id).Any(move => move.SourceId == child.Id) &&
            fixture.Inner.FolderMoves(PairId, root.Id).All(move => move.SourceId != sibling.Id),
            "a waiting descendant keeps its root reachable despite root NotAccepted");
        store.Waiting = false;
        await new FolderMoveCleanup(context, jobs.Items.Folders).RunAsync(root.Id, () => { }, default);
        Check.Require(fixture.Inner.FolderMoves(PairId, root.Id).Count == 0 &&
            !fixture.Inner.FolderMoveRoots(PairId, default).Any(),
            "after the child retires, the next walk also retires the root without unreachable custody");
    }

    private sealed class SplitAncestorWaitStore(IStateStore inner, WorkSelection blocker, string ancestor)
        : ForwardingStateStore(inner)
    {
        internal bool Waiting { get; set; } = true;
        public override IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
            IReadOnlyList<string> ancestorKeys, long? coveredThrough = null) =>
            Waiting && side == Side.Dropbox && ancestorKeys.Contains(ancestor) ? [blocker] : [];
    }

    private static async Task SplitMoveAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var target = mode == "case" ? "parent" : "Destination";
        fixture.Dropbox.MoveEntryLimit = mode == "ordinary" ? null : 1;
        var parent = mode == "nested" ? PathNames.Child(source.Path, "Nested") : source.Path;
        if (mode == "nested") fixture.Dropbox.Seed(FolderEntry(parent, "nested-id"), null);
        var first = FolderEntry(PathNames.Child(parent, "First"), "first-id");
        var second = FolderEntry(PathNames.Child(parent, "Second"), "second-id");
        if (mode == "mount") second = second with { SharedRoot = true };
        if (mode == "reserved") second = second with { Skip = nameof(TextKey.SkipReservedName) };
        fixture.Dropbox.Seed(first, null);
        fixture.Dropbox.Seed(second, null);
        if (mode == "occupied") fixture.Dropbox.Seed(FolderEntry(target, "occupant-id"), null);
        ItemFailedException? held = null;
        Entry? moved = null;
        try { moved = await new FolderMoveRunner(context).MoveAsync(Side.Dropbox, source, target, default); }
        catch (ItemFailedException error) { held = error; }
        catch (PathOccupiedException) when (mode == "occupied") { }
        var rows = fixture.Inner.FolderMoves(PairId, source.Id);
        if (mode is "ordinary" or "mount" or "reserved" or "case" or "occupied")
        {
            Check.Require(rows.Count == 0, "ordinary success and pre-arm refusals leave no split custody");
            Check.Require(mode switch
            {
                "ordinary" => moved?.Id == source.Id && moved.Path == target,
                "mount" or "reserved" => held is { Cause: TextKey.FailMoveSharedFolder, Move: null },
                "case" => held is { Cause: TextKey.FailDropboxTooManyFiles, Move: null },
                _ => held is null && fixture.Dropbox.FindById("occupant-id")?.Path == target
            }, "the lane preserves ordinary success, mount proof, case-only refusal and destination occupancy");
            return;
        }
        Check.Require(held is { Cause: TextKey.FailMoveInParts, Disposition: FailureDisposition.UnknownEffect } &&
            held.Move == new IssueSubject.UnknownMove(source.Id, source.Path, target, null, true) &&
            rows.Single() is { Phase: FolderMovePhase.Armed, DestinationId: null, Launch: null, JobId: null } &&
            await fixture.Dropbox.ReadOneAsync(target, default) is null,
            "the lane arms and hands off without creating or filling the destination");
        if (mode == "replacement")
        {
            fixture.Dropbox.Seed(source with { Id = "replacement-id" }, null);
            try
            {
                await new FolderMoveRunner(context).FinishAsync(rows, default);
                throw new Exception("a replacement source was accepted");
            }
            catch (ItemFailedException error)
            {
                Check.Require(error.Cause == TextKey.FailChanged && error.Move?.ExpectedId == source.Id &&
                    fixture.Dropbox.FindById(first.Id)?.Path == first.Path,
                    "a changed source identity refuses before listing or launching its children");
            }
            return;
        }
        moved = await new FolderMoveRunner(context).FinishAsync(rows, default);
        var filled = fixture.Inner.FolderMoves(PairId, source.Id);
        Check.Require(moved.Path == target && moved.Id != source.Id &&
            filled.Count == (mode == "nested" ? 2 : 1) && filled.All(move => move.Phase == FolderMovePhase.Filled &&
                move.Launch is null && move.JobId is null) &&
            fixture.Dropbox.FindById(first.Id)?.Path == PathNames.Child(mode == "nested" ? PathNames.Child(target, "Nested") : target, "First") &&
            fixture.Dropbox.FindById(second.Id)?.Path == PathNames.Child(mode == "nested" ? PathNames.Child(target, "Nested") : target, "Second") &&
            fixture.Dropbox.FindById(source.Id)?.Path == source.Path,
            "the fill preserves member identities, finishes nested custody, and leaves source cleanup to its owner");
    }
}
