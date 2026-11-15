using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunLocalSplit() => LocalSplitCasesAsync().GetAwaiter().GetResult();

    // Two siblings expose partial deletion; quadrupling them separates descendant work from constant work.
    // Doubling unrelated bindings independently detects pair-wide work.
    private static async Task LocalSplitCasesAsync()
    {
        var failures = new List<Exception>();
        foreach (var shape in new[] { "reference", "stall", "batch", "historic", "deleted-child", "deleted",
                     "remote-delete", "excluded-child", "fresh", "fault", "root-fault", "send-fault", "chosen", "excluded" })
        foreach (var children in new[] { 2, 8 })
        {
            long[]? previous = null;
            foreach (var background in new[] { 8, 16 })
            {
                try
                {
                    var counts = await LocalSplitAsync(shape, children, background);
                    if (previous is not null)
                        Check.Require(counts.SequenceEqual(previous), "Local split counts are independent of unrelated bindings: " + shape);
                    previous = counts;
                }
                catch (Exception error)
                {
                    failures.Add(new InvalidOperationException($"Local split {shape}/{children}/{background}", error));
                }
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task<long[]> LocalSplitAsync(string shape, int children, int background)
    {
        const string oldPath = "Parent/Old";
        const string newPath = "Parent/New";
        var attempt = Guid.NewGuid();
        using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        fixture.Attempts.Settles = (fixture.Inner, attempt);
        var saved = new List<SyncedEntry>();
        void Bind(string path, bool folder)
        {
            var local = folder ? FolderEntry(path, "local-" + path) : LocalEntry(path, "local-" + path);
            var remote = folder ? FolderEntry(path, "remote-" + path) : RemoteEntry(path, "remote-" + path);
            fixture.Local.Seed(local, folder ? null : [1]);
            fixture.Dropbox.Seed(remote, folder ? null : [1]);
            saved.Add(SyncedEntries.From(local, remote));
        }
        Bind("Parent", true);
        Bind(oldPath, true);
        for (var child = 0; child < children; child++)
        {
            var path = oldPath + "/Child-" + child;
            Bind(path, true);
            for (var file = 0; file < 3; file++) Bind(path + "/File-" + file, false);
        }
        Bind(oldPath + "/Loose", false);
        for (var other = 0; other < background; other++) Bind("Outside/Item-" + other, false);
        if (shape == "reference") Bind(newPath, true);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = saved });
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        await CompleteRepairAsync(fixture);
        RepairDone(fixture);
        ClearObligations(fixture);
        var root = saved.Single(value => value.LocalPath == oldPath);
        var members = saved.Where(value => value.LocalPath.StartsWith(oldPath + "/", StringComparison.Ordinal)).ToArray();
        var moved = new List<(Entry Entry, string OldPath)>();
        var pureDelete = shape is "deleted" or "remote-delete" or "root-fault" or "send-fault";
        if (!pureDelete)
        {
            await fixture.Local.EnsureFolderAsync(newPath, default);
            foreach (var member in members.Where(value => PathNames.Parent(value.LocalPath) == oldPath))
            {
                var entry = fixture.Local.FindById(member.LocalId)!;
                if (shape == "reference" && entry.Path != oldPath + "/Child-0") continue;
                if (shape == "deleted-child" && entry.Path == oldPath + "/Child-0")
                {
                    await fixture.Local.DeleteAsync(entry, null, default);
                    continue;
                }
                var target = shape == "excluded-child" && !entry.IsFolder
                    ? "Parent/Excluded/Loose" : newPath + "/" + PathNames.Name(entry.Path);
                moved.Add((await fixture.Local.MoveAsync(entry, target, default), entry.Path));
            }
            if (shape == "fresh") fixture.Local.Seed(LocalEntry(newPath + "/Fresh", "fresh-local"), [1]);
        }
        if (shape != "reference")
        {
            var source = shape == "remote-delete" ? fixture.Dropbox : fixture.Local;
            await source.DeleteAsync(source.FindById(shape == "remote-delete" ? root.DropboxId : root.LocalId)!, null, default);
        }

        var localCalls = new CountingSide(fixture.Local);
        var remoteCalls = new CountingSide(fixture.Dropbox);
        var store = new StoreCounters(fixture.Store);
        var plain = 0;
        var documented = 0;
        var remoteIdentity = 0;
        var rootReads = 0;
        var fault = shape is "fault" or "root-fault" or "send-fault";
        var submissions = 0;
        var effects = new List<FolderRemovalEffect>();
        var batches = 0;
        var batchPublications = 0;
        var followUps = 0;
        var retirements = new List<FolderRetirementDisposition>();
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.BeginFolderRemoval))
                followUps += fixture.Inner.PendingFolderFollowUps(PairId, (string)result!, -1, int.MaxValue).Count();
            if (method == nameof(IStateStore.RetireFolderRemoval))
                retirements.Add((FolderRetirementDisposition)args[3]!);
        };
        var faultId = shape == "fault" ? members.Single(value => value.LocalPath == oldPath + "/Loose").LocalId : root.LocalId;
        Task<(IdentityLocation Location, bool InScope)?> Identity(string id, bool strict)
        {
            if (strict) documented++; else plain++;
            if (strict && id == root.LocalId) rootReads++;
            if (fault && id == faultId && (shape != "send-fault" || rootReads > 1))
            {
                // Plain absence models the native answer that is insufficient to authorize deletion.
                if (strict) throw new ItemFailedException(id, TextKey.ReadLocalFailed, null);
                return Task.FromResult<(IdentityLocation Location, bool InScope)?>(null);
            }
            var found = fixture.Local.FindById(id);
            return Task.FromResult<(IdentityLocation Location, bool InScope)?>(found is null ? null :
                (new IdentityLocation(VolumeKey, found.Path, found),
                    shape != "excluded-child" || found.Path != "Parent/Excluded/Loose"));
        }
        fixture.LocalView = new IdentitySide(localCalls, (id, _) => Identity(id, false), (id, _) => Identity(id, true));
        fixture.DropboxView = new HookedSide(IdentitySide.Truthful(remoteCalls, fixture.Dropbox, () => remoteIdentity++),
            submit: (submission, membersToRemove, request, ct) =>
            {
                submissions++;
                effects.AddRange(membersToRemove);
                return fixture.Dropbox.SubmitFolderRemovalAsync(submission, membersToRemove, request, ct);
            });
        long page = 1;
        WorkSelection? selected = null;
        WorkSelection Notice(ChangeNotice notice)
        {
            if (notice.Side == Side.Dropbox)
                return selected = SeedRemote(fixture, notice.Observed ?? FolderEntry(notice.Path, notice.Id!), notice);
            return selected = Seed(fixture, notice, ++page);
        }
        Syncer Runner(bool live = true)
        {
            var boundary = new Boundary(_ => new Lease());
            boundary.Capture = (work, _, _) => Task.FromResult(Coverage(work));
            var context = fixture.Context(selected!) with
            {
                Store = store,
                Live = live,
                CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
                ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
            };
            var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
            return new Syncer(context, ports with
            {
                RunBatchAsync = async (work, side, scope, retries, settles, ct) =>
                {
                    batches++;
                    var before = remoteCalls.Publishes;
                    await SyncJobs.RunBatchAsync(context, ports, work, scope, ct, side, retries, settles);
                    batchPublications += remoteCalls.Publishes - before;
                }
            });
        }
        void Renames()
        {
            foreach (var item in moved.Where(value => !value.Entry.Path.StartsWith("Parent/Excluded/", StringComparison.Ordinal)))
                Notice(new ChangeNotice(Side.Local, ChangeKind.Renamed, item.Entry.Path, item.OldPath,
                    item.Entry.Id, item.Entry, false, false));
        }
        void Removed() => Notice(new ChangeNotice(shape == "remote-delete" ? Side.Dropbox : Side.Local,
            ChangeKind.Deleted, oldPath, null, shape == "remote-delete" ? root.DropboxId : root.LocalId, null, false, true));
        if (shape is "chosen" or "excluded")
        {
            selected = new WorkSelection(PairId, Generation, 1, Side.Local, VolumeKey, root.LocalId, root.LocalKey, 0, 0);
            var context = fixture.Context(selected) with { Store = store };
            var remote = fixture.Dropbox.FindById(root.DropboxId)!;
            var row = Row.ForOneSide(Side.Dropbox, root.LocalKey, remote, root, [], null, RowContext.Neutral);
            using var plan = await new FolderPlanBuilder(context).BuildAsync(row, Side.Dropbox, new HashSet<string>(),
                true, null, shape == "chosen" ? FolderRemovalAuthority.Chosen : FolderRemovalAuthority.Excluded, default);
            Check.Require(plan.DeleteWhole && !plan.Survivors(default).Any() && documented == 0 && plain == 0,
                "Explicit removal authority does not probe or preserve moved children: " + shape);
            return Counts();
        }
        if (!pureDelete && shape != "reference")
        {
            var destination = fixture.Local.FindById((await fixture.Local.ReadOneAsync(newPath, default))!.Id)!;
            Notice(new ChangeNotice(Side.Local, ChangeKind.Changed, newPath, null, destination.Id, destination, false, false));
            if (shape is "batch" or "fresh") await Runner().SyncWorkAsync(default);
        }
        if (shape is "reference" or "historic")
        {
            Renames();
            await Runner().SyncWorkAsync(default);
        }
        if (shape != "reference")
        {
            Removed();
            var before = documented;
            var plainBefore = plain;
            await Runner().SyncWorkAsync(default);
            Console.WriteLine($"LOCAL-SPLIT removal {shape}/{children}/{background}: documented={documented - before}, " +
                $"plain={plain - plainBefore}, submissions={submissions}, effects={effects.Count}, moves={remoteCalls.Moves}, followups={followUps}");
            var expected = shape switch
            {
                "historic" => 2,
                "remote-delete" => 0,
                "root-fault" => 1,
                // The held send is rechecked by the drain's continuation, still without a submission.
                "send-fault" => 4 * children + 4,
                "deleted" or "deleted-child" or "excluded-child" => 4 * children + 3,
                _ => 4 * children + 2
            };
            Check.Require(documented - before == expected && plain == plainBefore,
                $"Removal uses only documented identities: {shape}, observed={documented - before}, expected={expected}, plain={plain}");
            if (shape is "fault" or "root-fault")
            {
                Check.Require(submissions == 0 && effects.Count == 0 && remoteCalls.Moves == 0 &&
                    fixture.Inner.Issues(PairId).Any(issue => issue.Path == oldPath && issue.Cause == TextKey.ReadLocalFailed) &&
                    !fixture.Inner.ActiveFolderRemovals(PairId, default).Any() &&
                    members.All(value => fixture.Inner.SyncedByKey(PairId, Side.Local, value.LocalKey) == value),
                    "Uncertain documented identity holds the populated plan and every binding");
            }
            if (shape == "send-fault")
            {
                Check.Require(submissions == 0 && effects.Count == 0 &&
                    fixture.Inner.ActiveFolderRemovals(PairId, default).Any(),
                    "Uncertain send-root identity retains the ready operation without sending");
                return Counts();
            }
        }
        if (!pureDelete && shape is not ("reference" or "historic"))
        {
            Renames();
            await Runner().SyncWorkAsync(default);
            if (shape == "fault")
            {
                Check.Require(remoteCalls.Moves == 0 && effects.Count == 0 &&
                    fixture.Inner.Issues(PairId).Any(issue => issue.Path == oldPath && issue.Cause == TextKey.ReadLocalFailed),
                    "The failed parent fences child renames; no move or removal escapes");
                fault = false;
                var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == oldPath);
                var retry = await Runner(live: false).ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
                Console.WriteLine($"LOCAL-SPLIT Retry {children}/{background}: resolved={retry.Resolved}, outcome={retry.Outcome}, " +
                    $"issues={string.Join(',', fixture.Inner.Issues(PairId).Select(value => value.Cause))}, retirements={string.Join(',', retirements)}");
                Check.Require(retry.Resolved, "Retry clears the failed populated removal: " + retry.Outcome);
                await Runner().SyncWorkAsync(default);
            }
        }
        if (shape == "root-fault") return Counts();
        var expectedMoves = shape switch
        {
            "reference" => 1,
            "deleted" or "remote-delete" => 0,
            "deleted-child" or "excluded-child" => children,
            _ => children + 1
        };
        var expectedEffects = shape is "historic" or "deleted" or "deleted-child" or "excluded-child" ? 1 : 0;
        Check.Require(remoteCalls.Moves == expectedMoves && remoteCalls.Publishes == (shape == "fresh" ? 1 : 0) &&
            effects.Count == expectedEffects && submissions == expectedEffects && remoteCalls.Deletes == 0,
            $"Split effects {shape}: moves={remoteCalls.Moves}/{expectedMoves}, publishes={remoteCalls.Publishes}, " +
            $"effects={effects.Count}/{expectedEffects}, submissions={submissions}, deletes={remoteCalls.Deletes}");
        // Fresh needs one Local stream open for its upload; the mirror delete needs one Local removal.
        Check.Require(localCalls.Work == (shape is "remote-delete" or "fresh" ? 1 : 0),
            $"No unintended Local work: {shape}, work={localCalls.Work}, opens={localCalls.Opens}, deletes={localCalls.Deletes}");
        Check.Require(remoteCalls.Ensures == (pureDelete || shape == "reference" ? 0 : 1), "Only the new root is created: " + shape);
        if (shape is "batch" or "fresh")
            Check.Require(batches == 1 && batchPublications == (shape == "fresh" ? 1 : 0),
                "Add-only batch skips bound moved arrivals but admits Fresh");
        if (shape == "remote-delete") Check.Require(remoteIdentity == 2, "Mirror delete probes only its plan and send roots");
        Check.Require(fixture.Inner.Issues(PairId).Count == 0 && !fixture.Inner.ActiveFolderRemovals(PairId, default).Any(),
            "The completed opportunity leaves no failure or active removal: " + shape);
        if (shape is "stall" or "batch" or "fresh" or "fault")
            Check.Require(followUps == 0 && retirements.Contains(FolderRetirementDisposition.NoEffect) &&
                fixture.Dropbox.FindById(root.DropboxId) is not null &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, root.LocalKey) == root,
                "The populated survivor plan retires NoEffect, without follow-ups, and keeps the old root binding");
        foreach (var member in members)
        {
            var current = fixture.Local.FindById(member.LocalId);
            if (current is null || current.Path.StartsWith("Parent/Excluded/", StringComparison.Ordinal)) continue;
            var remote = fixture.Dropbox.FindById(member.DropboxId);
            var binding = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(current.Path));
            Check.Require(remote?.Path == current.Path && binding?.LocalId == member.LocalId && binding.DropboxId == member.DropboxId,
                "Every moved descendant keeps its identities and follows to its final path: " + member.LocalPath);
        }
        return Counts();

        long[] Counts()
        {
            var values = new long[] { localCalls.Listings, remoteCalls.Listings, localCalls.Reads, remoteCalls.Reads,
                localCalls.Hashes, remoteCalls.Hashes, localCalls.Opens, remoteCalls.Opens, localCalls.Stages, remoteCalls.Stages,
                localCalls.Publishes, remoteCalls.Publishes, localCalls.Ensures, remoteCalls.Ensures, localCalls.Deletes,
                remoteCalls.Deletes, localCalls.Moves, remoteCalls.Moves, plain, documented, remoteIdentity, submissions,
                effects.Count, batches, localCalls.Yielded, remoteCalls.Yielded, store.Commands, store.CommitCalls,
                store.ReadConnections, store.RowsRead, store.ScanAccepted, followUps,
                localCalls.SubtreeListings, remoteCalls.SubtreeListings, localCalls.SiblingListings, remoteCalls.SiblingListings,
                localCalls.Disposals, remoteCalls.Disposals, localCalls.BytesRead, remoteCalls.BytesRead,
                localCalls.HashBytes, remoteCalls.HashBytes, store.RecordCommitCalls, store.RecordCommittedMembers,
                store.RecordCommitFailures, store.ScanAppliedOnCompletion, store.ScanCompleted, store.ScanAborted,
                store.ScanFailedCompletions, store.DerivedPlanDataStatements, fixture.Hooks.Total };
            Console.WriteLine($"LOCAL-SPLIT {shape} children={children} background={background}: " + string.Join(",", values));
            return values;
        }
    }
}
