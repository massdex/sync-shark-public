using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunLocalSplit() => LocalSplitCasesAsync().GetAwaiter().GetResult();
    public static void RunNoEffectClosure() => NoEffectClosureCasesAsync().GetAwaiter().GetResult();

    private static async Task NoEffectClosureCasesAsync()
    {
        var failures = new List<Exception>();
        foreach (var shape in new[] { "plain-fault", "plain-receipt", "fault-new-evidence", "fault-other-cause" })
        {
            try { await LocalSplitAsync(shape, 2, 8); }
            catch (Exception error) { failures.Add(new InvalidOperationException(shape, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

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
        var childFault = shape.StartsWith("fault", StringComparison.Ordinal);
        var pureDelete = shape is "deleted" or "remote-delete" or "root-fault" or "send-fault" or "plain-fault" or "plain-receipt";
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
        var fault = childFault || shape is "root-fault" or "send-fault" or "plain-fault" or "plain-receipt";
        var faultCause = shape == "fault-other-cause" ? TextKey.FailChanged : TextKey.ReadLocalFailed;
        var submissions = 0;
        var effects = new List<FolderRemovalEffect>();
        var batches = 0;
        var batchPublications = 0;
        var followUps = 0;
        var retirements = new List<FolderRetirementDisposition>();
        var closures = new List<(bool Closed, long Through, int Reads, bool Held)>();
        long injectedWatermark = 0;
        var successfulClosures = 0;
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.CloseNoEffectFailure) && result is true)
                successfulClosures++;
            if (method == nameof(IStateStore.BeginFolderRemoval))
                followUps += fixture.Inner.PendingFolderFollowUps(PairId, (string)result!, -1, int.MaxValue).Count();
            if (method == nameof(IStateStore.RetireFolderRemoval))
                retirements.Add((FolderRetirementDisposition)args[3]!);
            if (shape == "fault-new-evidence" && method == nameof(IStateStore.CloseNoEffectFailure))
                closures.Add(((bool)result!, (long)args[4]!, documented,
                    fixture.Inner.IssueById(PairId, ((Issue)args[3]!).Id)?.Cause == TextKey.ReadLocalFailed &&
                    remoteCalls.Moves == 0 && effects.Count == 0));
        };
        var faultId = childFault ? members.Single(value => value.LocalPath == oldPath + "/Loose").LocalId : root.LocalId;
        Task<(IdentityLocation Location, bool InScope)?> Identity(string id, bool strict)
        {
            if (strict) documented++; else plain++;
            if (strict && id == root.LocalId) rootReads++;
            if (fault && id == faultId && (shape != "send-fault" || rootReads > 1))
            {
                // Plain absence models the native answer that is insufficient to authorize deletion.
                if (strict || shape is "plain-fault" or "plain-receipt") throw new ItemFailedException(id, faultCause, null);
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
        if (shape is "plain-fault" or "plain-receipt")
        {
            // Existing unreadable-member protection yields NoEffect without moved-out identity classification.
            foreach (var member in members.Where(value => PathNames.Parent(value.LocalPath) == oldPath))
                UnreadableMember.Record(fixture.Inner, PairId, member.LocalPath);
            Removed();
            await Runner().SyncWorkAsync(default);
            var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == oldPath);
            Check.Require(issue.Cause == TextKey.ReadLocalFailed && effects.Count == 0, "Plain folder read failure is retained");
            fault = false;
            if (shape == "plain-receipt")
            {
                var protectedMembers = members.Where(value => PathNames.Parent(value.LocalPath) == oldPath).ToArray();
                foreach (var member in protectedMembers)
                    UnreadableMember.Release(fixture.Inner, PairId, member.LocalPath);
                var providerSnapshot = members.Prepend(root)
                    .Select(value => fixture.Dropbox.FindById(value.DropboxId)!).ToArray();
                var receiptContext = fixture.Context(selected!) with
                {
                    Store = store, Live = false,
                    CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
                    ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
                };
                var remote = fixture.Dropbox.FindById(root.DropboxId)!;
                var receiptRow = Row.ForOneSide(Side.Dropbox, root.LocalKey, remote, root, [], null, RowContext.Neutral);
                string receiptOperation;
                using (var plan = await new FolderPlanBuilder(receiptContext).BuildAsync(receiptRow,
                    Side.Dropbox, new HashSet<string>(), false, default))
                {
                    Check.Require(plan.DeleteWhole && !plan.Survivors(default).Any(),
                        "B-257 C2: the stranded operation owns a whole recursive receipt, not a NoEffect plan.");
                    receiptOperation = store.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Inferred,
                        fixture.Settings.StoragePageSize, default);
                }
                var ready = store.ReadyFolderEffects(PairId, receiptOperation, -1, fixture.Settings.StoragePageSize).ToArray();
                Check.Require(ready.Length == 1 && ready[0].Expected == remote,
                    "B-257 C2: exactly one recursive root effect is frozen.");
                var submission = store.FolderSubmission(store.ArmFolderSubmission(PairId, ready));
                var accepted = submission with
                {
                    Generation = submission.Generation + 1,
                    Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = "receipt-job"
                };
                store.AdvanceFolderSubmission(submission, accepted, []);
                store.AdvanceFolderSubmission(accepted, accepted with
                {
                    Generation = accepted.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Terminal
                }, [new FolderMemberResult.Receipt(0, remote)]);
                var operation = store.FolderRemoval(PairId, receiptOperation);
                store.AdvanceFolderRemoval(PairId, receiptOperation, operation.Generation, operation.Phase, operation with
                {
                    Generation = operation.Generation + 1, Phase = FolderRemovalPhase.Submitted, Cursor = Cursor
                });
                await fixture.Dropbox.DeleteAsync(remote, null, default);
                Check.Require(store.IssueById(PairId, issue.Id) is not null && successfulClosures == 0,
                    "B-257 C2: the work-owned read failure remains beside the recorded receipt.");
                var noSelection = receiptContext with { Store = new ReceiptContinuationStore(store) };
                await new Syncer(noSelection, new SyncPorts(new RecoveryBoundary(),
                    RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).SyncWorkAsync(default);
                Check.Require(store.FolderRemoval(PairId, receiptOperation) is
                        { Phase: FolderRemovalPhase.Complete, Active: false } &&
                    store.IssueById(PairId, issue.Id) is not null && successfulClosures == 0 && submissions == 0,
                    "B-257 C2: ordinary receipt completion removes custody but does not close the work failure.");
                foreach (var entry in providerSnapshot)
                    fixture.Dropbox.Seed(entry, entry.IsFolder ? null : [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = members.Prepend(root).ToArray() });
                foreach (var member in protectedMembers)
                    UnreadableMember.Record(fixture.Inner, PairId, member.LocalPath);
                Check.Require(store.IssueById(PairId, issue.Id) is not null,
                    "B-257 C2: fresh provider evidence still requires the real work-owned closure.");
            }
            await Runner().SyncWorkAsync(default);
            Check.Require(retirements.Contains(FolderRetirementDisposition.NoEffect) && effects.Count == 0 &&
                fixture.Inner.IssueById(PairId, issue.Id) is null, "Fresh plain NoEffect closes its exact read failure");
            if (shape == "plain-receipt")
            {
                Check.Require(successfulClosures == 1,
                    "B-257 C2: the later fresh same-root NoEffect plan closes the exact failure once.");
                await Runner().SyncWorkAsync(default);
                Check.Require(successfulClosures == 1 && fixture.Inner.IssueById(PairId, issue.Id) is null &&
                    submissions == 0,
                    "B-257 C2: a later opportunity neither closes again nor sends a folder effect.");
            }
            return Counts();
        }
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
                Check.Require(submissions == 0 && effects.Count == 0 && remoteCalls.Deletes == 0 &&
                    retirements.Contains(FolderRetirementDisposition.NoEffect) &&
                    members.Append(root).All(value => fixture.Dropbox.FindById(value.DropboxId)?.Path == value.DropboxPath &&
                        fixture.Inner.SyncedByKey(PairId, Side.Local, value.LocalKey) == value),
                    "Uncertain send-root identity retires unsent NoEffect without deleting or forgetting");
                return Counts();
            }
        }
        if (!pureDelete && shape is not ("reference" or "historic"))
        {
            Renames();
            await Runner().SyncWorkAsync(default);
            if (shape is "fault-new-evidence" or "fault-other-cause")
            {
                var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == oldPath);
                fault = false;
                var injected = false;
                if (shape == "fault-new-evidence")
                {
                    fixture.Hooks.BeforeCallWith = (method, args) =>
                    {
                        if (method.Name != nameof(IStateStore.CloseNoEffectFailure)) return;
                        fixture.Hooks.BeforeCallWith = null;
                        Notice(new ChangeNotice(Side.Local, ChangeKind.Changed, oldPath + "/Loose", null,
                            members.Single(value => value.LocalPath == oldPath + "/Loose").LocalId, null, false, false));
                        injectedWatermark = fixture.Inner.ReadWorkWatermark();
                        Check.Require(injectedWatermark > (long)args[4]!,
                            "The injected descendant evidence is newer than this closure's read proof");
                        injected = true;
                    };
                }
                await Runner().SyncWorkAsync(default);
                Check.Require(shape != "fault-new-evidence" || injected, "The closure boundary received newer evidence");
                if (shape == "fault-new-evidence")
                    Check.Require(retirements.Contains(FolderRetirementDisposition.NoEffect) && effects.Count == 0 &&
                        closures.Count > 0 && !closures[0].Closed && closures[0].Held && closures.Any(value => value.Closed) &&
                        closures.Where(value => value.Closed).All(value => value.Through >= injectedWatermark &&
                            value.Reads > closures[0].Reads),
                        "Newer evidence holds the injected attempt; later closure requires fresh covering reads");
                else
                    Check.Require(retirements.Contains(FolderRetirementDisposition.NoEffect) && effects.Count == 0 &&
                        remoteCalls.Moves == 0 && fixture.Inner.IssueById(PairId, issue.Id)?.Cause == faultCause,
                        "NoEffect keeps the exact issue after later evidence or an unrelated failure cause: " + shape);
                return Counts();
            }
            if (shape == "fault")
            {
                Check.Require(remoteCalls.Moves == 0 && effects.Count == 0 &&
                    fixture.Inner.Issues(PairId).Any(issue => issue.Path == oldPath && issue.Cause == TextKey.ReadLocalFailed),
                    "The failed parent fences child renames; no move or removal escapes");
                fault = false;
                var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == oldPath);
                var beforeRetry = documented;
                Check.Require(await Identity(faultId, true) is { InScope: true }, "The injected Local-read fault is cleared");
                var retry = await Runner(live: false).ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
                Console.WriteLine($"LOCAL-SPLIT Retry {children}/{background}: resolved={retry.Resolved}, outcome={retry.Outcome}, " +
                    $"documented={documented - beforeRetry - 1}, batches={batches}, " +
                    $"issues={string.Join(',', fixture.Inner.Issues(PairId).Select(value => value.Cause))}, retirements={string.Join(',', retirements)}");
                Check.Require(!retry.Resolved && effects.Count == 0 && submissions == 0 &&
                    fixture.Inner.Issues(PairId).Any(value => value.Id == issue.Id && value.Cause == TextKey.ReadLocalFailed),
                    "Retry leaves the existing folder Failure open without deleting");
                await Runner().SyncWorkAsync(default);
                Console.WriteLine($"LOCAL-SPLIT recovery {children}/{background}: moves={remoteCalls.Moves}, " +
                    $"issues={string.Join(',', fixture.Inner.Issues(PairId).Select(value => value.Id + ":" + value.Cause))}, " +
                    $"retirements={string.Join(',', retirements)}");
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
        if (expectedEffects != 0)
        {
            var target = shape == "deleted-child" ? oldPath + "/Child-0"
                : shape == "excluded-child" ? oldPath + "/Loose" : oldPath;
            Check.Require(effects.Single().Expected?.Path == target, "Only the intended absent branch is removed: " + shape);
        }
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
        {
            Check.Require(followUps == 0 && retirements.Contains(FolderRetirementDisposition.NoEffect) &&
                fixture.Dropbox.FindById(root.DropboxId) is not null &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, root.LocalKey) == root,
                "The populated survivor plan retires NoEffect, without follow-ups, and keeps the old root binding");
            await foreach (var child in fixture.Dropbox.ReadAllAsync(oldPath, new ListOptions(true, false), default))
                Check.Require(child.Entry is null, "The retained old remote root is empty");
        }
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
            Console.WriteLine($"LOCAL-SPLIT members {shape}/{children}/{background}: " +
                string.Join(",", store.MemberCalls.OrderBy(value => value.Key).Select(value => value.Key + "=" + value.Value)));
            // Successful-case CI observations at both sizes; these also pin the rename reference across sizes.
            // Commands measures this wrapper, not SQL instructions; Hooks includes the evidence owner's store calls.
            var pinned = (shape, children) switch
            {
                ("reference", 2) => "0,0,5,6,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,80,3,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,83",
                ("reference", 8) => "0,0,5,6,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,80,3,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,83",
                ("stall", 2) => "1,2,21,25,0,0,0,0,0,0,0,0,0,1,0,0,0,3,0,10,0,0,0,1,9,9,434,11,1,10,11,0,1,2,0,0,0,0,0,0,0,0,0,0,0,11,2,0,0,41,449",
                ("stall", 8) => "1,2,51,61,0,0,0,0,0,0,0,0,0,1,0,0,0,9,0,34,0,0,0,1,33,33,1082,29,1,34,35,0,1,2,0,0,0,0,0,0,0,0,0,0,0,35,2,0,0,137,1103",
                ("batch", 2) => "1,2,21,25,0,0,0,0,0,0,0,0,0,1,0,0,0,3,0,10,0,0,0,1,9,9,445,11,1,10,11,0,1,2,0,0,0,0,0,0,0,0,0,0,0,11,2,0,0,41,462",
                ("batch", 8) => "1,2,51,61,0,0,0,0,0,0,0,0,0,1,0,0,0,9,0,34,0,0,0,1,33,33,1093,29,1,34,35,0,1,2,0,0,0,0,0,0,0,0,0,0,0,35,2,0,0,137,1116",
                ("historic", 2) => "1,3,22,26,0,0,0,0,0,0,0,0,0,1,0,0,0,3,0,2,0,1,1,1,9,0,418,11,1,10,11,0,1,3,0,0,0,0,0,0,0,0,0,0,0,11,2,0,0,3,433",
                ("historic", 8) => "1,3,52,62,0,0,0,0,0,0,0,0,0,1,0,0,0,9,0,2,0,1,1,1,33,0,946,29,1,34,35,0,1,3,0,0,0,0,0,0,0,0,0,0,0,35,2,0,0,3,967",
                ("deleted-child", 2) => "1,3,17,21,0,0,0,0,0,0,0,0,0,1,0,0,0,2,0,11,0,1,1,1,5,12,398,8,1,6,7,0,1,3,0,0,0,0,0,0,0,0,0,0,0,7,2,0,0,54,412",
                ("deleted-child", 8) => "1,3,47,57,0,0,0,0,0,0,0,0,0,1,0,0,0,8,0,35,0,1,1,1,29,36,1094,26,1,30,31,0,1,3,0,0,0,0,0,0,0,0,0,0,0,31,2,0,0,150,1114",
                ("deleted", 2) => "0,2,4,4,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,11,0,1,1,0,0,18,160,0,0,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,39,164",
                ("deleted", 8) => "0,2,4,4,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,35,0,1,1,0,0,66,328,0,0,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,135,332",
                ("remote-delete", 2) => "2,0,4,4,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,2,0,0,0,18,0,161,1,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,39,173",
                ("remote-delete", 8) => "2,0,4,4,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,2,0,0,0,66,0,329,1,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,135,359",
                ("excluded-child", 2) => "1,2,17,21,0,0,0,0,0,0,0,0,0,1,0,0,0,2,0,11,0,1,1,1,8,9,392,8,1,9,10,0,1,2,0,0,0,0,0,0,0,0,0,0,0,10,2,0,0,46,404",
                ("excluded-child", 8) => "1,2,47,57,0,0,0,0,0,0,0,0,0,1,0,0,0,8,0,35,0,1,1,1,32,33,1064,26,1,33,34,0,1,2,0,0,0,0,0,0,0,0,0,0,0,34,2,0,0,142,1082",
                ("fresh", 2) => "1,2,22,26,0,0,1,0,0,1,0,1,0,1,0,0,0,3,0,10,0,0,0,1,10,9,463,12,1,11,12,0,1,2,0,0,1,0,1,0,0,0,0,0,0,12,2,0,0,41,480",
                ("fresh", 8) => "1,2,52,62,0,0,1,0,0,1,0,1,0,1,0,0,0,9,0,34,0,0,0,1,34,33,1111,30,1,35,36,0,1,2,0,0,1,0,1,0,0,0,0,0,0,36,2,0,0,137,1134",
                ("root-fault", 2) => "0,0,3,3,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,38,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,41",
                ("root-fault", 8) => "0,0,3,3,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,38,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,41",
                ("chosen", 2) => "0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,9,3,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,29,3",
                ("chosen", 8) => "0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,33,3,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,101,3",
                ("excluded", 2) => "0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,9,3,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,29,3",
                ("excluded", 8) => "0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,33,3,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,101,3",
                _ => null
            };
            if (pinned is not null)
                Check.Require(string.Join(",", values) == pinned, "Absolute Local split work budget: " + shape);
            return values;
        }
    }

    private sealed class ReceiptContinuationStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        public override WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope) => null;
    }
}
