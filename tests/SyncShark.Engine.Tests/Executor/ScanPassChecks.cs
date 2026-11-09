using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for one scan pass (`ScanPass`), entering through the Syncer's own ports.</summary>
public static class ScanPassChecks
{
    private static readonly EngineSettings Settings = ShippedSettings() with
    {
        ConflictLabelFormat = "{name} ({side} {time}){ext}",
        ConflictLabelTimeFormat = "yyyyMMddHHmmss", ConflictLabelLocalName = "Local",
        ConflictLabelDropboxName = "Dropbox", CaseRenameSuffix = ".case", DefaultIgnoreRules = ""
    };
    private static readonly AsyncLocal<string?> FixtureRoot = new();

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await StaleListedAdoptionAsync();
        await RenameAdmissionOrderAsync();
        await RenameConcurrencyAsync();
        await CapacityAsync();
        await UploadAsync();
        await HeldFailureNoPermitsAsync();
        await UnchangedRowNoPermitsAsync();
        foreach (var held in new[] { false, true }) await RenameQuietTotalAsync(held);
        await ComparisonBeforePolicyAsync();
        await SiblingFaultAsync(false);
        await SiblingFaultAsync(true);
        await HeldSurvivorCancelAsync(false);
        await HeldSurvivorCancelAsync(true);
        await HeldSurvivorWakeAsync();
        await HeldFollowUpWakeCostAsync();
        await UnobservableSurvivorAsync();
        await RepeatedResolutionAsync();
        foreach (var refusal in new[] { "identity", "no-effect", "saved-only" })
            await RefusedBranchReplansAsync(refusal);
        await ListingCountAsync();
        await FirstRunFinalizerFailureAsync();
        await BatchExclusionProbeAsync();
        await BatchAncestorAdmissionAsync();
        await FirstRunBothSideBarriersAsync();
    }

    public static void RunRepairHeld() => RepairHeldSurvivorsAsync().GetAwaiter().GetResult();

    // R6-6: under persistent Local debt and with no new event, each released survivor that would write Local is
    // refused on its own, the later operation's survivor is still reached, and the work wake returns normally so
    // AutoSync goes on to schedule its folder poll; clearing the debt lets the next wake restore both (3.8).
    private static Task RepairHeldSurvivorsAsync() => WithFolder("repair-held-survivors", async folder =>
    {
        using var rig = NewRig(Pair());
        var pairId = rig.Pair.PairId;
        string[] roots = ["docs", "more"];
        var folders = new List<Entry>();
        foreach (var name in roots)
        {
            folders.Add(await rig.Local.EnsureFolderAsync(name, default));
            await PutAsync(rig.Local, name + "/kept.txt", [1]);
            await PutAsync(rig.Local, name + "/removed.txt", [2]);
        }
        using (var log = Log(rig, folder, "seed")) await RunRepairAsync(rig, log, Settings);
        var raised = new List<(IssueKind, TextKey, string, IssueSubject)>();
        foreach (var name in roots)
        {
            var kept = name + "/kept.txt";
            await rig.Dropbox.DeleteAsync((await rig.Dropbox.ReadOneAsync(kept, default))!, null, default);
            var edited = await PutAsync(rig.Dropbox, kept, [3, 3]);
            raised.Add((IssueKind.Failure, TextKey.FailDropboxNoPermission, kept,
                new IssueSubject([new IssueItem(Side.Dropbox, kept, edited.Id)], null)));
        }
        foreach (var root in folders) await rig.Local.DeleteAsync(root, null, default);
        rig.Store.Commit(pairId, StateChange.Empty with { Raise = raised });
        using (var log = Log(rig, folder, "held")) await RunRepairAsync(rig, log, Settings);
        var operations = rig.Store.ActiveFolderRemovals(pairId, default).Select(operation => operation.OperationId).ToArray();
        int Pending() => operations.Count(id => rig.Store.PendingFolderFollowUps(pairId, id, -1, 1).Any());
        Check.Require(operations.Length == 2 && Pending() == 2, "R6-6 fixture: two operations, each with a held survivor");
        rig.Store.Commit(pairId, StateChange.Empty with
        {
            Close = roots.Select(name => (name + "/kept.txt", IssueClosure.AfterEffect)).ToArray()
        });
        void Owe(SourceObligation? obligation)
        {
            var source = rig.Store.ReadSource(pairId, Side.Local)!.Value;
            rig.Store.AdvanceSource(pairId, Side.Local, rig.Store.ReadPairExecution(pairId)!.Value.Generation,
                source.Epoch, source.Revision, source.Provenance, source.Cursor, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, obligation);
        }
        async Task<int> Restored()
        {
            var count = 0;
            foreach (var name in roots)
                if (await rig.Local.ReadOneAsync(name + "/kept.txt", default) is not null) count++;
            return count;
        }
        Owe(SourceObligation.NormalRepair);
        var counted = new RefusalReads(rig.Store);
        Exception? error = null;
        using (var log = Log(rig, folder, "refused"))
        {
            try
            {
                await RunOperationAsync(rig with { Store = counted }, log, Settings,
                    jobs => jobs.Syncer.SyncWorkAsync(default));
            }
            catch (Exception caught) { error = caught; }
        }
        var refusedPending = Pending();
        var refusedRestored = await Restored();
        Check.Require(error is null && counted.Reads >= 2 && refusedPending == 2 && refusedRestored == 0,
            "R6-6 each released survivor is refused on its own and the wake returns normally: error=" +
            error?.GetType().Name + "; refusalReads=" + counted.Reads + "; pending=" + refusedPending +
            "; restored=" + refusedRestored);
        Owe(null);
        using (var log = Log(rig, folder, "cleared"))
            await RunOperationAsync(rig, log, Settings, jobs => jobs.Syncer.SyncWorkAsync(default));
        var clearedPending = Pending();
        var clearedRestored = await Restored();
        Check.Require(clearedPending == 0 && clearedRestored == 2,
            "R6-6 once the debt clears, the next wake restores both retained survivors: pending=" + clearedPending +
            "; restored=" + clearedRestored);
    });

    private sealed class RefusalReads(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal int Reads;
        public override LiveRefusal ReadLiveRefusal(string pairId)
        {
            Reads++;
            return base.ReadLiveRefusal(pairId);
        }
    }

    public static void FreshIssuesSurviveReconciliation() => FreshIssuesAsync().GetAwaiter().GetResult();

    private static Task FreshIssuesAsync() => WithFolder("fresh-issues", async folder =>
    {
        using var rig = NewRig(Pair());
        var store = rig.Store;
        var pairId = rig.Pair.PairId;
        void Raise(string path, string id, bool retained) => store.Commit(pairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, path, new IssueSubject(
                [new IssueItem(Side.Local, path, id)], null, FirstRunDeletion: retained
                    ? IssueSubject.FirstRunDeletionKind.Item : IssueSubject.FirstRunDeletionKind.None))]
        });
        Raise("Old", "old", false);
        Raise("Updated", "old", true);
        var token = store.BeginScan(pairId, store.ReadPairExecution(pairId)!.Value.Generation, false);
        await store.OpenScan(pairId, Side.Local, ScanTestSettings.BatchRows).CompleteAsync();
        await store.OpenScan(pairId, Side.Dropbox, ScanTestSettings.BatchRows).CompleteAsync();
        // A live batch publishes these failures after Repair has completed its empty listing.
        Raise("Updated", "new", true);
        Raise("New", "new", true);
        using var log = Log(rig, folder, "reconcile");
        var context = Context(rig, store, log, rig.Local, rig.Dropbox, Settings, null, null);
        var syncer = new Syncer(context, new SyncPorts(new LeaseBoundary(),
            RefusingExecutionBoundary.Instance, new LeaseBoundary(), LocalSide.HashSlots));
        await syncer.SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
        Check.Require(store.Issues(pairId).Select(issue => issue.Path).Order().SequenceEqual(new[] { "New", "Updated" }) &&
            store.RetainedFirstRun(pairId).Count == 2,
            "S-G2 actual reconciliation closes covered old evidence but preserves newer failures and first-run subjects");
        await store.EndScanAsync(pairId, token, Settings.StoragePageSize);
    });

    private static Task FirstRunBothSideBarriersAsync() => WithFolder("both-side-barrier", async directory =>
    {
        const string child = "Parent/G/Child";
        foreach (var batch in new[] { false, true })
        foreach (var subjectSide in new Side?[] { Side.Local, Side.Dropbox, null })
        foreach (var excluded in new[] { false, true })
        {
            var pair = Pair() with
            {
                Exclusions = excluded ? [new Exclusion(subjectSide ?? Side.Local, child, true)] : []
            };
            using var rig = NewRig(pair);
            var roots = new List<(Entry Local, Entry Remote)>();
            foreach (var path in new[] { "Parent/G", "Parent/H" })
            {
                var local = await rig.Local.EnsureFolderAsync(path, default);
                var remote = await rig.Dropbox.EnsureFolderAsync(path, default);
                roots.Add((local, remote));
                rig.Store.Commit(pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                        new IssueSubject([new IssueItem(Side.Local, path, local.Id)], null,
                            FirstRunDeletion: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))]
                });
            }
            using var log = Log(rig, directory, Guid.NewGuid().ToString("N"));
            var injected = false;
            var context = Context(rig, rig.Store, log, rig.Local, rig.Dropbox, Settings, null, null) with
            {
                BatchScope = batch ? "Parent" : null,
                Events = item =>
                {
                    if (injected || item.StageKey != TextKey.StageFolderPlans) return;
                    injected = true;
                    rig.Store.Commit(pair.PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.ReadLocalFailed, child,
                            new IssueSubject(subjectSide is { } side ? [new IssueItem(side, child, "child-id")] : [], null))]
                    });
                }
            };
            var token = rig.Store.BeginScan(pair.PairId, context.ExpectedGeneration, false, batch ? "Parent" : null);
            foreach (var side in Enum.GetValues<Side>())
            {
                var sink = rig.Store.OpenScan(pair.PairId, side, ScanTestSettings.BatchRows);
                foreach (var entries in roots)
                {
                    var entry = side == Side.Local ? entries.Local : entries.Remote;
                    await sink.AddAsync(new Listed(entry.Path, entry), default);
                }
                await sink.CompleteAsync();
            }
            await new Syncer(context, new SyncPorts(new LeaseBoundary(), RefusingExecutionBoundary.Instance,
                new LeaseBoundary(), 0)).SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
            var issues = rig.Store.Issues(pair.PairId);
            Check.Require(injected && issues.Any(issue => issue.Path == "Parent/G" && issue.Kind == IssueKind.Failure)
                == (!excluded || subjectSide is null),
                "both-side and item-less issues hold ancestor settlement; subject exclusion uses its actual side");
            Check.Require(issues.All(issue => issue.Path != "Parent/H" || issue.Kind != IssueKind.Failure),
                "an issue in one subtree never holds an independent sibling");
        }
    });

    private static Task BatchAncestorAdmissionAsync() => WithFolder("batch-ancestor", async directory =>
    {
        foreach (var later in new[] { false, true })
        {
            using var rig = NewRig(Pair());
            var store = rig.Store;
            var pair = rig.Pair.PairId;
            var generation = store.ReadPairExecution(pair)!.Value.Generation;
            foreach (var side in Enum.GetValues<Side>())
            {
                var source = store.ReadSource(pair, side)!.Value;
                store.AdvanceSource(pair, side, generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance,
                    source.Cursor, null);
            }
            var parent = await rig.Local.EnsureFolderAsync("Parent", default);
            var child = await PutAsync(rig.Local, "Parent/Child");
            var domain = rig.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            long Acquire(Entry entry)
            {
                var source = store.ReadSource(pair, Side.Local)!.Value;
                var position = store.ReadJournal(domain)!;
                return store.AcquireJournalPage(domain, position, position with { NextUsn = position.NextUsn + 1 },
                    [(pair, generation, source.Epoch, source.Revision, false)],
                    [(pair, new ChangeNotice(Side.Local, ChangeKind.Changed, entry.Path, null, entry.Id, entry, false, false))])!.Value;
            }
            if (!later) Acquire(parent);
            var through = Acquire(child);
            var selection = new WorkSelection(pair, generation, store.ReadSource(pair, Side.Local)!.Value.Epoch,
                Side.Local, domain, child.Id, PathNames.Key(child.Path), through, through);
            if (later) Acquire(parent);
            var token = store.BeginScan(pair, generation, false, "Parent");
            var sink = store.OpenScan(pair, Side.Local, ScanTestSettings.BatchRows);
            await sink.AddAsync(new Listed(child.Path, child), default);
            await sink.CompleteAsync();
            await store.OpenScan(pair, Side.Dropbox, ScanTestSettings.BatchRows).CompleteAsync();
            using var log = Log(rig, directory, Guid.NewGuid().ToString("N"));
            var local = new CountingSide(rig.Local);
            var remote = new CountingSide(rig.Dropbox);
            var context = Context(rig, store, log, local, remote, Settings, null, null) with { BatchScope = "Parent" };
            LiveAdmissionStaleException? refusal = null;
            try
            {
                await new Syncer(context, new SyncPorts(new LeaseBoundary(), RefusingExecutionBoundary.Instance,
                    new LeaseBoundary(), 0)).SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default, selection);
            }
            catch (LiveAdmissionStaleException error) { refusal = error; }
            Check.Require((refusal is not null) == later,
                "a batch leaves an older pending ancestor's child but throws Stale for a later owner");
            Check.Require(local.Reads + remote.Reads + local.Work + remote.Work == 0 &&
                store.SyncedByKey(pair, Side.Local, PathNames.Key(child.Path)) is null &&
                store.Issues(pair).Count == 0 && store.ReadWork(selection).Any(),
                "a batch leaves the child unobserved, unsucceeded and undeferred, with its acquired work still owed");
            await store.EndScanAsync(pair, token, Settings.StoragePageSize);
        }
    });

    private static Task BatchExclusionProbeAsync() => WithFolder("batch-exclusion", async directory =>
    {
        const string root = "Parent/Root";
        foreach (var side in Enum.GetValues<Side>())
        foreach (var location in new[] { "above", "below", "outside" })
        foreach (var state in new[] { "present", "absent", "keep", "failed" })
        {
            var path = location == "above" ? "Parent" : location == "below" ? root + "/Excluded" : "Elsewhere";
            var exclusion = new Exclusion(side, path, state == "keep");
            using var rig = NewRig(Pair() with { Exclusions = [exclusion] });
            var localRoot = await rig.Local.EnsureFolderAsync(root, default);
            var remoteRoot = await rig.Dropbox.EnsureFolderAsync(root, default);
            if (state != "absent") await (side == Side.Local ? rig.Local : rig.Dropbox).EnsureFolderAsync(path, default);
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, root,
                    new IssueSubject([new IssueItem(Side.Local, root, localRoot.Id)], null,
                        FirstRunDeletion: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))]
            });
            var fault = new ItemFailedException(path, Text.ReadFailedOn(side), null);
            var reads = 0;
            var source = new ProxySide(side == Side.Local ? rig.Local : rig.Dropbox)
            {
                OnRead = value => { if (value == path) reads++; },
                ReadFault = value => value == path && state == "failed" ? fault : null
            };
            var local = new CountingSide(side == Side.Local ? source : rig.Local);
            var remote = new CountingSide(side == Side.Dropbox ? source : rig.Dropbox);
            using var log = Log(rig, directory, Guid.NewGuid().ToString("N"));
            var events = new List<RunEvent>();
            var context = Context(rig, rig.Store, log, local, remote, Settings, null, null) with
            {
                BatchScope = root, Events = item => events.Add(item)
            };
            var token = rig.Store.BeginScan(rig.Pair.PairId, context.ExpectedGeneration, false, root);
            var localSink = rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
            await localSink.AddAsync(new Listed(root, localRoot), default);
            await localSink.CompleteAsync();
            var remoteSink = rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            await remoteSink.AddAsync(new Listed(root, remoteRoot), default);
            await remoteSink.CompleteAsync();
            await new Syncer(context, new SyncPorts(new LeaseBoundary(), RefusingExecutionBoundary.Instance,
                new LeaseBoundary(), 0)).SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
            var probed = location != "outside" && state != "keep";
            Check.Require(reads == (probed ? 1 : 0) &&
                events.Count(item => ReferenceEquals(item.Error, fault)) == (probed && state == "failed" ? 1 : 0),
                "batch probes only overlapping non-Keep targets once, and diagnoses a failed read");
            Check.Require(local.Work + remote.Work + local.Hashes + remote.Hashes == 0,
                "batch exclusion probes never copy, hash, rename or remove their target");
            if (location == "below")
                Check.Require(rig.Store.Issues(rig.Pair.PairId).Any(issue =>
                    issue.Path == root && issue.Kind == IssueKind.Failure) == (state is "present" or "failed"),
                    "present or unreadable descendants hold first-run settlement; absent and Keep targets do not");
        }
    });

    private static Task FirstRunFinalizerFailureAsync() => WithFolder("finalizer-failure", async directory =>
    {
        using var rig = NewRig(Pair());
        foreach (var path in new[] { "Parent", "Parent/G", "Parent/H" })
        {
            var local = await rig.Local.EnsureFolderAsync(path, default);
            await rig.Dropbox.EnsureFolderAsync(path, default);
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                    new IssueSubject([new IssueItem(Side.Local, path, local.Id)], null,
                        FirstRunDeletion: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))]
            });
        }
        var finalizing = false;
        var reads = 0;
        var remote = new ProxySide(rig.Dropbox)
        {
            ReadFault = path => finalizing && path == "Parent/G"
                ? new ItemFailedException(path, TextKey.FailDropboxNoPermission, null) : null,
            OnRead = path => { if (finalizing && path == "Parent/G") reads++; }
        };
        using var log = Log(rig, directory, "run");
        var token = rig.Store.BeginScan(rig.Pair.PairId, rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation, false);
        await ListAsync(rig.Local, rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows), [], [], false, default);
        await ListAsync(remote, rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows), [], [], false, default);
        var context = Context(rig, rig.Store, log, rig.Local, remote, Settings, null, null) with
        {
            Events = item =>
            {
                log.Event(item);
                if (item.StageKey == TextKey.StageFolderPlans) finalizing = true;
            }
        };
        await new Syncer(context, new SyncPorts(new LeaseBoundary(), RefusingExecutionBoundary.Instance,
            new LeaseBoundary(), LocalSide.HashSlots)).SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
        var issues = rig.Store.Issues(rig.Pair.PairId);
        Check.Require(reads > 0 && issues.Any(issue => issue.Path == "Parent/G" &&
            issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailDropboxNoPermission),
            "A finalizer observation failure remains on its own folder.");
        Check.Require(issues.Any(issue => issue.Path == "Parent" && issue.Kind == IssueKind.Failure &&
            issue.Subject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None) &&
            issues.All(issue => issue.Path != "Parent/H" || issue.Kind != IssueKind.Failure) &&
            issues.Any(issue => issue.Path == "Parent/H" && issue.Cause == TextKey.InfoDropboxHistoryUnchecked),
            "The failed folder holds its parent while the independent sibling finishes and closes its failure.");
    });

    private static Task StaleListedAdoptionAsync() => WithFolder("stale-listed-adoption", async folder =>
    {
        using var rig = NewRig(Pair());
        var local = await PutAsync(rig.Local, "Item");
        var remote = await PutAsync(rig.Dropbox, "Item");
        var saved = SyncedEntries.From(local, remote) with { LocalPath = "item" };
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var replacement = saved with { LocalSize = saved.LocalSize + 1 };
        var injected = false;
        var reads = 0;
        var store = new HookStore(rig.Store)
        {
            DisposeInner = false,
            BeforeCommitAsync = change =>
            {
                if (injected || change.Renames.Count == 0) return;
                injected = true;
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [replacement] });
            }
        };
        var remoteSide = new ProxySide(rig.Dropbox) { OnRead = _ => Interlocked.Increment(ref reads) };
        rig.Store.BeginScan(rig.Pair.PairId, rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation, false);
        await ListAsync(rig.Local, store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows), [], [], false, default);
        await ListAsync(remoteSide, store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows), [], [], false, default);
        var group = store.ScanRenames(rig.Pair.PairId).ToArray();
        using var log = Log(rig, folder, "stale");
        var context = Context(rig, store, log, rig.Local, remoteSide, Settings, null, null);
        var items = SyncJobs.Create(context, new SyncPorts(new LeaseBoundary(), RefusingExecutionBoundary.Instance,
            new LeaseBoundary(), 0)).Items;
        var decision = await items.RenameAsync(group, local, default, completedScan: true);
        Check.Require(injected && reads > 0 && decision.Kind == RenameKind.Adopt,
            "Stale queued adoption reobserves through ordinary execution in the same pass.");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, saved.LocalKey) ==
            (replacement with { LocalPath = local.Path }),
            "Rejected stale adoption never overwrites the replacement content history.");
    });

    private static Task RenameAdmissionOrderAsync() => WithFolder("rename-admission-order", async folder =>
    {
        foreach (var duplicateDestination in new[] { false, true })
        {
            using var original = NewRig(Pair());
            var limiter = new Massdex.SyncShark.Engine.Dropbox.AdaptiveLimiter(1, 1, TimeProvider.System,
                new FakeNetworkSignal(), _ => { }, new FailureRetryOwner.BackoffSettings(1, 1.6, 0, 120));
            var rig = original with { Admission = limiter };
            var local = await PutAsync(rig.Local, "A");
            var remote = await PutAsync(rig.Dropbox, "A");
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            await rig.Local.MoveAsync(local, "B", default);
            if (duplicateDestination)
            {
                await rig.Dropbox.MoveAsync(remote, "B", default);
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(
                    (await rig.Local.ReadOneAsync("B", default))!, (await rig.Dropbox.ReadOneAsync("B", default))!)] });
            }
            rig.Store.BeginScan(rig.Pair.PairId, rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation, false);
            await ListAsync(rig.Local, rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows), [], [], false, default);
            await ListAsync(rig.Dropbox, rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows), [], [], false, default);
            var group = rig.Store.ScanRenames(rig.Pair.PairId).ToArray();
            using var log = Log(rig, folder, "order");
            using var cancel = new CancellationTokenSource();
            var context = Context(rig, rig.Store, log, rig.Local, rig.Dropbox, Settings, null, null);
            var syncer = SyncJobs.Create(context, new SyncPorts(new LeaseBoundary(), RefusingExecutionBoundary.Instance,
                new LeaseBoundary(), 0));
            await using var permit = await limiter.AdmitAsync(false, default);
            var run = syncer.Items.RenameAsync(group, group[0].Moved, cancel.Token, completedScan: true);
            try
            {
                Check.Require(!run.IsCompleted, "Fresh fallback waits for the occupied provider permit.");
                var contender = PathLocks.Shared.EnterAsync(LockKeys.ForPaths(rig.Pair,
                    syncer.Items.Renamer().Footprint(group, group[0].Moved)).ToArray(), cancel.Token);
                Check.Require(contender.IsCompletedSuccessfully,
                    "Fallback releases all path locks before waiting for provider admission.");
                await using var held = await contender;
            }
            finally
            {
                cancel.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    });

    private static Task RenameConcurrencyAsync() => WithFolder("rename-concurrency", async folder =>
    {
        foreach (var overlap in new[] { false, true })
        {
            using var rig = NewRig(Pair());
            foreach (var name in new[] { "A", "B" })
            {
                var local = await PutAsync(rig.Local, name);
                var remote = await PutAsync(rig.Dropbox, name);
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            }
            var localA = (await rig.Local.ReadOneAsync("A", default))!;
            var localB = (await rig.Local.ReadOneAsync("B", default))!;
            await rig.Local.MoveAsync(localB, overlap ? "C" : "Y", default);
            await rig.Local.MoveAsync(localA, overlap ? "B" : "X", default);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = false;
            var reads = 0;
            var remoteSide = new ProxySide(rig.Dropbox)
            {
                BeforeRead = async (path, ct) =>
                {
                    if (path == "A")
                    {
                        first.TrySetResult();
                        await release.Task.WaitAsync(ct);
                    }
                    if (path == "B")
                    {
                        Interlocked.Increment(ref reads);
                        Check.Require(!overlap || released, "Overlapping rename observations never overtake the earlier group.");
                        second.TrySetResult();
                    }
                }
            };
            using var log = Log(rig, folder, overlap ? "overlap" : "independent");
            using var cancel = new CancellationTokenSource();
            var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
            var token = rig.Store.BeginScan(rig.Pair.PairId, generation, false);
            await ListAsync(rig.Local, rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows), [], [], false, default);
            await ListAsync(remoteSide, rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows), [], [], false, default);
            var context = Context(rig, rig.Store, log, rig.Local, remoteSide, Settings, null, null);
            var syncer = new Syncer(context, new SyncPorts(new LeaseBoundary(),
                RefusingExecutionBoundary.Instance, new LeaseBoundary(), 0));
            var run = syncer.SyncScannedAsync(token, new ListedSkips([], [], [], []), null, cancel.Token);
            try
            {
                Check.Require(first.Task.IsCompleted && second.Task.IsCompleted == !overlap,
                    "The synchronous producer starts independent groups but parks before an overlapping observation.");
                released = true;
                release.TrySetResult();
                await run;
                Check.Require(overlap || reads > 0, "Independent rename groups overlap their fresh observations.");
            }
            finally
            {
                released = true;
                release.TrySetResult();
                cancel.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    });

    private static Task RefusedBranchReplansAsync(string refusal) => WithFolder("refused-branch-" + refusal, async folder =>
    {
        using var rig = NewRig(Pair());
        const string victim = "docs/victim.txt";
        const string survivor = "docs/survivor.txt";
        await rig.Local.EnsureFolderAsync("docs", default);
        await PutAsync(rig.Local, victim, [1]);
        await PutAsync(rig.Local, survivor, [2]);
        using (var log = Log(rig, folder, "seed")) await RunRepairAsync(rig, log, Settings);
        var remoteRoot = (await rig.Dropbox.ReadOneAsync("docs", default))!;
        await rig.Dropbox.DeleteAsync(remoteRoot, null, default);
        var changedSurvivor = (await rig.Local.ReadOneAsync(survivor, default))!;
        ((FakeSide)rig.Local).Seed(changedSurvivor with { Size = 2 }, [2, 2]);
        var frozenVictim = (await rig.Local.ReadOneAsync(victim, default))!;
        if (refusal == "saved-only") await rig.Local.DeleteAsync(frozenVictim, null, default);
        var store = new AfterFreezeStore(rig.Store, () =>
            ((FakeSide)rig.Local).Seed(frozenVictim with
            {
                Id = refusal == "identity" ? frozenVictim.Id + "-changed" : frozenVictim.Id, Size = 3
            }, [1, 1, 1]));
        using (var log = Log(rig, folder, "refused"))
            await RunRepairAsync(rig, log, Settings, store: store);
        Check.Require(store.OperationId is not null, "the changed branch was frozen before the final observation");
        var operation = rig.Store.FolderRemoval(rig.Pair.PairId, store.OperationId!);
        var member = rig.Store.FolderRemovalItems(rig.Pair.PairId, operation.OperationId, -1, int.MaxValue)
            .Single(item => item.Path == victim);
        Check.Require(refusal == "no-effect" ? member.EffectState == FolderRemovalItem.EffectStatus.NoEffect :
            member.EffectState == FolderRemovalItem.EffectStatus.Ready &&
            (refusal != "saved-only" || member.EffectKind == FolderEffectKind.StateOnly),
            "the fixture exercises its exact refused branch category: " + refusal);
        Check.Require(!operation.Active && operation.Phase != FolderRemovalPhase.Complete &&
            await rig.Local.ReadOneAsync(victim, default) is { Size: 3 } &&
            await rig.Local.ReadOneAsync(survivor, default) is { Size: 2 },
            "a refused unsent branch releases only the old claim and keeps all surviving bytes");
        using (var log = Log(rig, folder, "replan")) await RunRepairAsync(rig, log, Settings);
        Check.Require(await rig.Dropbox.ReadOneAsync(victim, default) is { Size: 3 } &&
            await rig.Dropbox.ReadOneAsync(survivor, default) is { Size: 2 },
            "ordinary Repair freshly plans and syncs both abandoned survivors without a recovery choice");
    });

    private sealed class AfterFreezeStore(IStateStore inner, Action changed) : ForwardingStateStore(inner)
    {
        public string? OperationId { get; private set; }
        public override string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct)
        {
            var id = base.BeginFolderRemoval(pairId, plan, pageSize, ct);
            if (OperationId is null)
            {
                OperationId = id;
                changed();
            }
            return id;
        }
    }

    // S23-R1: one survivor's failed observation is that item's Failure; the next survivor still runs, and the run lives.
    private static Task UnobservableSurvivorAsync() => WithFolder("unobservable-survivor", async folder =>
    {
        using var rig = NewRig(Pair());
        var pairId = rig.Pair.PairId;
        var root = await rig.Local.EnsureFolderAsync("docs", default);
        string[] survivors = ["docs/a.txt", "docs/z.txt"];
        foreach (var path in survivors) await PutAsync(rig.Local, path, [1]);
        await PutAsync(rig.Local, "docs/removed.txt", [2]);
        using (var log = Log(rig, folder, "seed")) await RunRepairAsync(rig, log, Settings);
        var raised = new List<(IssueKind, TextKey, string, IssueSubject)>();
        foreach (var path in survivors)
        {
            await rig.Dropbox.DeleteAsync((await rig.Dropbox.ReadOneAsync(path, default))!, null, default);
            var edited = await PutAsync(rig.Dropbox, path, [3, 3]);
            raised.Add((IssueKind.Failure, TextKey.FailDropboxNoPermission, path,
                new IssueSubject([new IssueItem(Side.Dropbox, path, edited.Id)], null)));
        }
        await rig.Local.DeleteAsync(root, null, default);
        rig.Store.Commit(pairId, StateChange.Empty with { Raise = raised });
        using (var log = Log(rig, folder, "held")) await RunRepairAsync(rig, log, Settings);
        var operation = rig.Store.ActiveFolderRemovals(pairId, default).Single().OperationId;
        rig.Store.Commit(pairId, StateChange.Empty with
        {
            Close = survivors.Select(path => (path, IssueClosure.AfterEffect)).ToArray()
        });

        ((FakeSide)rig.Local).FailNext(survivors[0], new ItemFailedException(survivors[0], TextKey.ReadLocalFailed, null));
        var outcomes = new List<SyncOutcome>();
        using (var log = Log(rig, folder, "finish"))
            await RunOperationAsync(rig, log, Settings, jobs =>
                jobs.Folders.FinishFolderRemovalAsync(operation, null, default, (_, outcome) => outcomes.Add(outcome)));
        var pending = rig.Store.PendingFolderFollowUps(pairId, operation, -1, Settings.StoragePageSize).ToArray();
        Check.Require(pending.Length == 1 && pending[0].Path == survivors[0] &&
            await rig.Local.ReadOneAsync(survivors[1], default) is not null &&
            outcomes.Count(outcome => outcome.Raised == IssueKind.Failure) == 1 &&
            rig.Store.Issues(pairId).Single(issue => issue.Kind == IssueKind.Failure).Path == survivors[0],
            "the failed survivor keeps its follow-up and its own Failure; the independent survivor completes");
    });

    // S23-R2: one root keeps one operation, whichever route reaches it again.
    private static Task RepeatedResolutionAsync() => WithFolder("repeated-resolution", async folder =>
    {
        const string kept = "docs/kept.txt";
        using var rig = NewRig(Pair());
        var pairId = rig.Pair.PairId;
        var local = await rig.Local.EnsureFolderAsync("docs", default);
        await PutAsync(rig.Local, kept, [1]);
        await PutAsync(rig.Local, "docs/removed.txt", [2]);
        using (var log = Log(rig, folder, "seed")) await RunRepairAsync(rig, log, Settings);
        await rig.Dropbox.DeleteAsync((await rig.Dropbox.ReadOneAsync(kept, default))!, null, default);
        var edited = await PutAsync(rig.Dropbox, kept, [3, 3]);
        var remote = (await rig.Dropbox.ReadOneAsync("docs", default))!;
        rig.Store.Commit(pairId, StateChange.Empty with
        {
            Raise =
            [
                (IssueKind.Failure, TextKey.FailDropboxNoPermission, kept,
                    new IssueSubject([new IssueItem(Side.Dropbox, kept, edited.Id)], null)),
                (IssueKind.NameClash, TextKey.ClashNameTaken, "docs",
                    new IssueSubject([new IssueItem(Side.Local, "docs", local.Id),
                        new IssueItem(Side.Dropbox, "docs", remote.Id)], Side.Local))
            ]
        });
        var issue = rig.Store.Issues(pairId).Single(candidate => candidate.Kind == IssueKind.NameClash);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var log = Log(rig, folder, "resolve-" + attempt);
            await RunOperationAsync(rig, log, Settings, jobs =>
                jobs.Syncer.ResolveAsync(issue, ResolutionChoice.ReplaceExisting, null, null, default));
            Check.Require(rig.Store.ActiveFolderRemovals(pairId, default).Count() == 1 &&
                rig.Store.IssueById(pairId, issue.Id) is not null,
                "a repeated selected resolution keeps the root's one unfinished operation: attempt " + attempt);
        }
    });

    private static Task HeldFollowUpWakeCostAsync() => WithFolder("held-wake-cost", async folder =>
    {
        using var rig = NewRig(Pair());
        var local = new CountingSide(rig.Local);
        var remote = new CountingSide(rig.Dropbox);
        var counted = rig with { Local = local, Dropbox = remote };
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Store)!;
        async Task<int> Probe(string name)
        {
            var statements = 0;
            var readOnly = true;
            using var log = Log(rig, folder, name);
            await RunOperationAsync(counted, log, Settings, async jobs =>
            {
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
                {
                    readOnly &= sql.utf8_to_string().TrimStart().StartsWith("SELECT", StringComparison.Ordinal);
                    statements++;
                }), null);
                try { await jobs.Folders.ContinueReleasedFollowUpsAsync(default); }
                finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            });
            Check.Require(readOnly, "idle folder continuation only reads stored state");
            return statements;
        }
        Check.Require(await Probe("empty") == 2, "no active folder operation costs two indexed reads");
        const int heldCount = 1000;
        var root = new Entry("docs", true, 0, DateTimeOffset.UnixEpoch, "root", "rev", "", null);
        using (var plan = rig.Store.OpenFolderPlan(rig.Pair.PairId,
            Row.ForOneSide(Side.Dropbox, "docs", root, null, [], null,
                RowContext.Neutral), Side.Dropbox))
        {
            async IAsyncEnumerable<Listed> Children()
            {
                await Task.CompletedTask;
                for (var index = 0; index < heldCount; index++)
                {
                    var path = "docs/child-" + index;
                    yield return new Listed(path, new Entry(path, false, 1, DateTimeOffset.UnixEpoch,
                        "id-" + index, "rev", "", null));
                }
            }
            await plan.CaptureAsync(Children(), default);
            foreach (var child in plan.Children(default))
                plan.MarkClassified(child.Key, child.NativeKey,
                    new FolderChild(child.Entry, child.Synced, FolderVerdict.Survives), true, default);
            plan.PrepareBranches(default);
            foreach (var child in plan.RemovedChildren(default)) plan.SelectBranch(child.Id, default);
            plan.Complete(false, default);
            rig.Store.BeginFolderRemoval(rig.Pair.PairId, plan, Settings.StoragePageSize, default);
        }
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, root.Path,
                new IssueSubject([new IssueItem(Side.Dropbox, root.Path, root.Id)], null))]
        });
        rig.Store.HeldCovers(rig.Pair.PairId, PathNames.Key(root.Path), null);
        var pages = (heldCount + Settings.StoragePageSize - 1) / Settings.StoragePageSize + 1;
        var actual = await Probe("held");
        Check.Require(actual == 3 + 2 * pages,
            "held survivor wake costs active reads plus pair and pending reads per page, no per-survivor SQL: " + actual);
        Check.Require(local.Reads + local.Listings + local.Opens + local.Hashes +
            remote.Reads + remote.Listings + remote.Opens + remote.Hashes == 0,
            "one thousand held survivors cause zero provider calls");
    });

    private static Task HeldSurvivorWakeAsync() => WithFolder("held-survivor-wake", async folder =>
    {
        using var rig = NewRig(Pair());
        const string kept = "docs/kept.txt";
        var root = await rig.Local.EnsureFolderAsync("docs", default);
        await PutAsync(rig.Local, kept, [1]);
        using (var log = Log(rig, folder, "seed")) await RunRepairAsync(rig, log, Settings);
        await rig.Dropbox.DeleteAsync((await rig.Dropbox.ReadOneAsync(kept, default))!, null, default);
        var edited = await PutAsync(rig.Dropbox, kept, [3, 3]);
        await rig.Local.DeleteAsync(root, null, default);
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, kept,
                new IssueSubject([new IssueItem(Side.Dropbox, kept, edited.Id)], null))]
        });
        using (var log = Log(rig, folder, "held")) await RunRepairAsync(rig, log, Settings);
        var operation = rig.Store.ActiveFolderRemovals(rig.Pair.PairId, default).Single().OperationId;
        Check.Require(rig.Store.PendingFolderFollowUps(rig.Pair.PairId, operation, -1, 1).Any(),
            "Repair leaves a durable held survivor even without an ordinary selection");
        var local = new CountingSide(rig.Local);
        var remote = new CountingSide(rig.Dropbox);
        var counted = rig with { Local = local, Dropbox = remote };
        using (var log = Log(rig, folder, "still-held"))
            await RunOperationAsync(counted, log, Settings, jobs => jobs.Syncer.SyncWorkAsync(default));
        Check.Require(local.Reads + local.Listings + local.Opens + local.Hashes +
            remote.Reads + remote.Listings + remote.Opens + remote.Hashes == 0,
            "an ordinary wake with only a held survivor has no provider observation");
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Close = [(kept, IssueClosure.AfterEffect)] });
        using (var log = Log(rig, folder, "released-wake"))
            await RunOperationAsync(counted, log, Settings, jobs => jobs.Syncer.SyncWorkAsync(default));
        Check.Require(!rig.Store.PendingFolderFollowUps(rig.Pair.PairId, operation, -1, 1).Any() &&
            !rig.Store.FolderRemoval(rig.Pair.PairId, operation).Active &&
            await rig.Local.ReadOneAsync(kept, default) is { } restored &&
            await rig.Local.ContentHashAsync(restored, default) == await rig.Dropbox.ContentHashAsync(edited, default),
            "CLOSE plus an ordinary work wake completes the released survivor without another Repair");
    });

    // A26: cancellation on either side of CompleteFolderFollowUp keeps the delete active and loses nothing; a fresh
    // composition over the durable rows finishes the rest with no replayed effect.
    private static Task HeldSurvivorCancelAsync(bool afterComplete) => WithFolder("held-survivor-cancel-" + afterComplete, async folder =>
    {
        const string kept = "docs/kept.txt";
        var database = Path.Combine(FixtureRoot.Value!, Guid.NewGuid().ToString("N") + ".db");
        var rig = NewRig(Pair(), database);
        try
        {
        var pairId = rig.Pair.PairId;
        var root = await rig.Local.EnsureFolderAsync("docs", default);
        await PutAsync(rig.Local, kept, [1]);
        await PutAsync(rig.Local, "docs/removed.txt", [2]);
        using (var log = Log(rig, folder, "seed")) await RunRepairAsync(rig, log, Settings);
        await rig.Dropbox.DeleteAsync((await rig.Dropbox.ReadOneAsync(kept, default))!, null, default);
        var edited = await PutAsync(rig.Dropbox, kept, [3, 3]);
        await rig.Local.DeleteAsync(root, null, default);
        rig.Store.Commit(pairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, kept,
                new IssueSubject([new IssueItem(Side.Dropbox, kept, edited.Id)], null))]
        });
        using (var log = Log(rig, folder, "held")) await RunRepairAsync(rig, log, Settings);
        var operation = rig.Store.ActiveFolderRemovals(pairId, default).Single().OperationId;
        Check.Require(await rig.Local.ReadOneAsync(kept, default) is null &&
            rig.Store.PendingFolderFollowUps(pairId, operation, -1, 1).Any(), "the held survivor waits, untouched");

        rig.Store.Commit(pairId, StateChange.Empty with { Close = [(kept, IssueClosure.AfterEffect)] });
        using var cancel = new CancellationTokenSource();
        var store = new CancellingFollowUps(rig.Store, cancel, afterComplete);
        var cancelled = false;
        using (var log = Log(rig, folder, "cancelled"))
        {
            try { await RunRepairAsync(rig, log, Settings, store: store, ct: cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Check.Require(cancelled && rig.Store.FolderRemoval(pairId, operation).Active &&
            rig.Store.PendingFolderFollowUps(pairId, operation, -1, 1).Any() != afterComplete,
            "cancellation keeps the delete active, and only a completed follow-up leaves pending work");

        // Restart: the database closes and reopens, and the next run is a new composition over its durable rows.
        rig.Store.Dispose();
        rig = rig with { Store = StateStore.Open(database) };
        Check.Require(rig.Store.ActiveFolderRemovals(pairId, default).Single().OperationId == operation &&
            rig.Store.PendingFolderFollowUps(pairId, operation, -1, 1).Any() != afterComplete &&
            rig.Store.SyncedByKey(pairId, Side.Dropbox, PathNames.DropboxKey("docs")) is not null,
            "the reopened database holds the same operation, its pending follow-up and the root binding");

        using (var log = Log(rig, folder, "next"))
        {
            var next = await RunRepairAsync(rig, log, Settings);
            Check.Require(!next.Effects.ContainsKey(nameof(EffectKind.Downloaded)) &&
                !rig.Store.PendingFolderFollowUps(pairId, operation, -1, 1).Any() &&
                !rig.Store.FolderRemoval(pairId, operation).Active &&
                !rig.Store.ActiveFolderRemovals(pairId, default).Any() &&
                (await rig.Local.ReadOneAsync(kept, default)) is { } restored &&
                await rig.Local.ContentHashAsync(restored, default) == await rig.Dropbox.ContentHashAsync(edited, default),
                "the next run completes the follow-up without repeating the restore");
        }
        }
        finally { rig.Dispose(); }
    });

    private sealed class CancellingFollowUps(IStateStore inner, CancellationTokenSource cancel, bool afterComplete)
        : ForwardingStateStore(inner)
    {
        public override void CompleteFolderFollowUp(string pairId, string operationId, long ordinal,
            long expectedGeneration)
        {
            if (afterComplete) base.CompleteFolderFollowUp(pairId, operationId, ordinal, expectedGeneration);
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
        }
    }

    private static Task SiblingFaultAsync(bool incomplete) => WithFolder("sibling-fault-" + incomplete, async folder =>
    {
        using var rig = NewRig(Pair());
        await PutAsync(rig.Local, "a.txt");
        await PutAsync(rig.Local, "b.txt");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = false;
        Exception fault = incomplete ? new InventoryIncompleteException("late", null)
            : new InvalidOperationException("sibling provider fault");
        var remote = new ProxySide(rig.Dropbox)
        {
            BlockPublish = async (path, ct) =>
            {
                if (path == "b.txt")
                {
                    await started.Task.WaitAsync(ct);
                    throw fault;
                }
                started.TrySetResult();
                using var registration = ct.Register(() =>
                {
                    if (!incomplete) throw new IOException("cancellation callback fault");
                });
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { joined = true; }
            }
        };
        using var log = Log(rig, folder, "run");
        Exception? thrown = null;
        try { await RunRepairAsync(rig, log, Settings, dropbox: remote); }
        catch (Exception error) { thrown = error; }
        Check.Require(joined && ReferenceEquals(thrown, fault),
            "substantive sibling failure wins over cancellation and a throwing cancellation callback after joining");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null && !rig.Store.FirstRepairDone(rig.Pair.PairId),
            "a joined member fault saves no completed scan summary");
        using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Check.Require(!reader.ReadToEnd().Split(Environment.NewLine).Any(line =>
            line.Split(RunLog.FieldSeparator).Contains(RunLog.CompleteKind)),
            "a late failure does not claim completion");
        Check.Require(await rig.Dropbox.ReadOneAsync("a.txt", CancellationToken.None) is null,
            "the blocked publication is cancelled and joined before the pass returns");
    });

    private static async Task ComparisonBeforePolicyAsync()
    {
        foreach (var unreliable in new[] { false, true })
        {
            await WithFolder("comparison-before-policy-" + unreliable, async folder =>
            {
                using var rig = NewRig(Pair());
                var item = await PutAsync(rig.Local, "item", [1]);
                await PutAsync(rig.Dropbox, "item", [2]);
                ((FakeSide)rig.Local).Seed(item with
                {
                    Modified = unreliable ? new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        : item.Modified.AddSeconds(1)
                }, [1]);
                var order = new List<string>();
                var local = new ProxySide(rig.Local)
                {
                    Hash = async (entry, ct) =>
                    {
                        order.Add("hash");
                        return await rig.Local.ContentHashAsync(entry, ct);
                    }
                };
                var remote = new ProxySide(rig.Dropbox)
                {
                    BlockPublish = (_, _) =>
                    {
                        order.Add("publish");
                        return Task.CompletedTask;
                    }
                };
                using var log = Log(rig, folder, "run");
                await RunScanAsync(rig, log, local, remote);
                Check.Require(order.Count >= 2 && order[0] == "hash" && order[1] == "publish",
                    "equal-size different content is compared before the policy-selected publication");
                if (unreliable)
                    Check.Require(rig.Store.Issues(rig.Pair.PairId).Count(issue =>
                        issue.Cause == TextKey.InfoKeptBoth) == 1,
                        "unreliable differing content keeps both only after comparison");
                else
                    Check.Require(order.SequenceEqual(new[] { "hash", "publish" }) &&
                        log.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 1,
                        "reliable Local-newer content compares exactly once before one upload");
            });
        }
    }

    // A214: one Repair lists each side once (root listings) on a new tree. Whether a pending echo is retired is
    // proved through the production selection in scenario 43; this row cannot show it without an acquired echo.
    private static Task ListingCountAsync() => WithFolder("listing-count", async folder =>
    {
        using var rig = NewRig(Pair());
        var local = new CountingSide(rig.Local);
        var dropbox = new CountingSide(rig.Dropbox);
        await local.EnsureFolderAsync("docs", default);
        await PutAsync(local, "docs/a.txt", [1]);
        await PutAsync(local, "new.txt", [2]);
        using var log = Log(rig, folder, "once");
        var summary = await RunRepairAsync(rig, log, Settings, local: local, dropbox: dropbox);
        Check.Require(summary.Completed, "A214: a new tree completes in one Repair");
        var localRoots = local.Listings - local.SubtreeListings - local.SiblingListings;
        var dropboxRoots = dropbox.Listings - dropbox.SubtreeListings - dropbox.SiblingListings;
        Check.Require(localRoots == 1 && dropboxRoots == 1,
            $"A214: one root listing per side (local={localRoots}, dropbox={dropboxRoots})");
    });

    private static async Task CapacityAsync()
    {
        foreach (var work in new[] { "admission", "cancel-admission", "publish", "hash", "record" })
        {
            await WithFolder("capacity-" + work, async folder =>
            {
                using var rig = NewRig(Pair());
                ((FakeAdmission)rig.Admission).Level = 1;
                var queued = work is "admission" or "cancel-admission";
                ((FakeAdmission)rig.Admission).Hold = queued;
                using var cancellation = new CancellationTokenSource();
                var capacity = work == "publish"
                    ? checked(2 * ((FakeAdmission)rig.Admission).WorkCapacity + LocalSide.HashSlots)
                    : Math.Min(rig.Admission.Level, ((FakeAdmission)rig.Admission).WorkCapacity) + LocalSide.HashSlots;
                for (var index = 0; index < capacity + 1; index++)
                {
                    var path = "item-" + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                    await PutAsync(rig.Local, path);
                    if (work is "hash" or "record")
                    {
                        var remote = await PutAsync(rig.Dropbox, path);
                        if (work == "hash")
                            ((FakeSide)rig.Dropbox).Seed(remote with { Modified = remote.Modified.AddSeconds(1) }, [1]);
                    }
                }
                var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var waiting = 0;
                var released = false;
                async Task WaitAsync(CancellationToken ct)
                {
                    if (Interlocked.Increment(ref waiting) == capacity) full.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                var local = new ProxySide(rig.Local)
                {
                    Hash = async (entry, ct) =>
                    {
                        if (work == "hash") await WaitAsync(ct);
                        return await rig.Local.ContentHashAsync(entry, ct);
                    }
                };
                var dropbox = new ProxySide(rig.Dropbox)
                {
                    BlockPublish = (_, ct) => work == "publish" ? WaitAsync(ct) : Task.CompletedTask
                };
                var ceiling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var store = new HookStore(rig.Store)
                {
                    OnRow = count =>
                    {
                        Check.Require(released || count <= capacity,
                            "no row is decoded beyond admission plus hash capacity");
                        if (count >= capacity) ceiling.TrySetResult();
                    },
                    Record = async (pairId, change) =>
                    {
                        if (work == "record") await WaitAsync(CancellationToken.None);
                        await rig.Store.CommitRecordAsync(pairId, change, null);
                    }
                };
                using var log = Log(rig, folder, "run");
                var run = RunScanAsync(rig, log, local, dropbox, store, cancellation.Token);
                // Awaited completion, never a wall-clock bound: a loaded machine only waits longer, while an early
                // end or fault still fails through run.IsCompleted before the signal arrives.
                async Task RequireReachedAsync(Task reached, string what)
                {
                    await Task.WhenAny(reached, run);
                    Check.Require(reached.IsCompletedSuccessfully && !run.IsCompleted, what);
                }
                try
                {
                    if (queued)
                    {
                        await RequireReachedAsync(
                            ((FakeAdmission)rig.Admission).WaitBlockedAsync(capacity),
                            "queued permits fill capacity");
                        Check.Require(((FakeAdmission)rig.Admission).Blocked == capacity && ((FakeAdmission)rig.Admission).Admitted == 0 &&
                            ((FakeAdmission)rig.Admission).Completed == 0,
                            "queued permits fill capacity without granting admission or completing effects");
                    }
                    else if (work == "publish")
                    {
                        // An upload releases its ordinary share, so live rows fill to the total ceiling; the exact
                        // blocked-publish count is not a stable observable, but the producer's stop at capacity is.
                        await RequireReachedAsync(ceiling.Task, "live rows fill the total ceiling");
                    }
                    else
                    {
                        await RequireReachedAsync(full.Task, "blocked work reaches the ordinary capacity");
                    }
                    Check.Require(store.Rows == capacity && !run.IsCompleted,
                        "queued admission, publication, hash and Record tasks each consume production capacity");
                    if (work == "cancel-admission")
                    {
                        cancellation.Cancel();
                        var stopped = false;
                        try { await run; }
                        catch (OperationCanceledException) { stopped = true; }
                        Check.Require(stopped && ((FakeAdmission)rig.Admission).Blocked == 0 && ((FakeAdmission)rig.Admission).Admitted == 0 &&
                            ((FakeAdmission)rig.Admission).Completed == 0 && log.Effects.Count == 0 && store.Rows == capacity,
                            "cancellation joins every blocked admission without effects or more rows");
                        ((FakeAdmission)rig.Admission).Hold = false;
                        Check.Require(((FakeAdmission)rig.Admission).Blocked == 0 && ((FakeAdmission)rig.Admission).Admitted == 0 &&
                            ((FakeAdmission)rig.Admission).Completed == 0 && log.Effects.Count == 0,
                            "releasing the hold after cancellation grants no late admission or effect");
                        return;
                    }
                }
                finally
                {
                    released = true;
                    ((FakeAdmission)rig.Admission).Hold = false;
                    release.TrySetResult();
                    try { await run; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
                Check.Require(store.Rows == capacity + 1,
                    "completion wakes the producer and drains every remaining row");
                if (work == "admission")
                    Check.Require(((FakeAdmission)rig.Admission).Blocked == 0 && ((FakeAdmission)rig.Admission).StageAcquired == capacity + 1 &&
                        ((FakeAdmission)rig.Admission).StageReports == capacity + 1 && ((FakeAdmission)rig.Admission).WorkReleased == capacity + 1,
                        "releasing queued permits uploads every row exactly once and leaves no waiter");
            });
        }
    }

    // ITEM-SYNC upload accounting: one pure upload uses one work/staging lifetime and no nested ordinary admission.
    private static Task UploadAsync() => WithFolder("upload", async folder =>
    {
        using var rig = NewRig(Pair());
        await PutAsync(rig.Local, "a.txt", [1, 2, 3]);
        using var log = Log(rig, folder, "run-1");
        await RunScanAsync(rig, log);

        Check.Require(await rig.Dropbox.ReadOneAsync("a.txt", CancellationToken.None) is not null,
            "a local-only file is uploaded by the row pass");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("a.txt")) is not null,
            "the upload records the link");
        Check.Require(log.Effects.TryGetValue(EffectKind.Uploaded.ToString(), out var uploaded) && uploaded == 1,
            "the run log counts the upload");
        Check.Require(((FakeAdmission)rig.Admission).StageAcquired == 1 && ((FakeAdmission)rig.Admission).StageReports == 1 &&
            ((FakeAdmission)rig.Admission).StageReleased == 1 && ((FakeAdmission)rig.Admission).WorkReleased == 1 && ((FakeAdmission)rig.Admission).Admitted == 0,
            "pure upload uses one work/staging lifetime and no nested ordinary admission");
    });

    // ROW-PASS holds: a stored held Failure adds no per-item provider read, hash, publish or permit.
    private static Task HeldFailureNoPermitsAsync() => WithFolder("held-failure-no-permits", async folder =>
    {
        using var rig = NewRig(Pair());
        var item = await PutAsync(rig.Local, "item");
        var reads = 0;
        var attempts = 0;
        var local = new ProxySide(rig.Local) { OnRead = _ => Interlocked.Increment(ref reads) };
        var dropbox = new ProxySide(rig.Dropbox)
        {
            OnRead = _ => Interlocked.Increment(ref reads),
            BlockPublish = (_, _) => { attempts++; return Task.CompletedTask; }
        };
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, item.Path,
                new IssueSubject([new IssueItem(Side.Local, item.Path, item.Id)], null))]
        });
        using var log = Log(rig, folder, "run");
        await RunScanAsync(rig, log, local, dropbox);
        Check.Require(attempts == 0 && reads == 0 && local.Hashes + dropbox.Hashes == 0 &&
            ((FakeAdmission)rig.Admission).Admitted == 0 && log.Effects.Count == 0,
            "a held Failure adds no per-item provider reads, hashes, transfers or permits");
    });

    // ROW-PASS permits: an unchanged synchronous row pass acquires no admission of any kind.
    private static Task UnchangedRowNoPermitsAsync() => WithFolder("unchanged-no-permits", async folder =>
    {
        using var rig = NewRig(Pair());
        const int items = 20;
        for (var index = 0; index < items; index++)
        {
            await PutAsync(rig.Local, "same-" + index);
            await PutAsync(rig.Dropbox, "same-" + index);
        }
        using (var first = Log(rig, folder, "first")) await RunCompletedScanAsync(rig, first);
        var before = ((FakeAdmission)rig.Admission).Admitted;
        var messages = new List<string>();
        using var unchanged = RunLog.Open(folder, rig.Pair.PairId, "unchanged", messages.Add, Settings.LogBufferCharacters);
        await RunScanAsync(rig, unchanged, store: new QuietRowsStore(rig.Store, unchanged));
        Check.Require(((FakeAdmission)rig.Admission).Admitted == before,
            "a synchronous unchanged row pass acquires no permits");
        Check.Require(unchanged.Effects.Count == 0 && messages.Any(message =>
            message.Contains("progress; count 20 of") && message.Contains(PathNames.Key("same-"))),
            "real unchanged row work supplies completed counts and subjects to quiet progress without effects");
        unchanged.Dispose();
        Check.Require(File.ReadLines(unchanged.Path).Count(line => line.Contains(nameof(TextKey.StageRows))) == items + 2,
            "row stage writes only begin, one snapshot per explicit quiet signal, and end");
    });

    private static Task RenameQuietTotalAsync(bool held) => WithFolder("rename-quiet-total-" + held, async folder =>
    {
        using var rig = NewRig(Pair());
        foreach (var path in new[] { "a.txt", "b.txt" })
        {
            await PutAsync(rig.Local, path);
            await PutAsync(rig.Dropbox, path);
        }
        using (var seed = Log(rig, folder, "seed")) await RunCompletedScanAsync(rig, seed);
        foreach (var path in new[] { "a.txt", "b.txt" })
            await rig.Local.MoveAsync((await rig.Local.ReadOneAsync(path, default))!, "moved-" + path, default);
        if (held)
        {
            var item = (await rig.Local.ReadOneAsync("moved-a.txt", default))!;
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, item.Path,
                    new IssueSubject([new IssueItem(Side.Local, item.Path, item.Id)], null))]
            });
        }
        var messages = new List<string>();
        using var log = RunLog.Open(folder, rig.Pair.PairId, "renames", messages.Add, Settings.LogBufferCharacters);
        var quietSignals = 0;
        var remote = new ProxySide(rig.Dropbox)
        {
            // Hold the owner at its provider boundary while publishing the quiet snapshot.
            OnRead = _ => { quietSignals++; log.ReportProgress(); }
        };
        await RunScanAsync(rig, log, dropbox: remote);
        Check.Require(messages.Any(message => message.Contains("progress; count 1 of 2;") &&
            message.Contains("elapsed") && message.Contains(".TXT")),
            "a rename blocked at its provider boundary reports completed groups, exact total, current path and elapsed time");
        if (!held)
            Check.Require(messages.Any(message => message.Contains("progress; count 0 of 2;") &&
                message.Contains("A.TXT")),
                "the first rename publishes its path and total before its first provider read completes");
        log.Dispose();
        Check.Require(File.ReadLines(log.Path).Count(line => line.Contains(nameof(TextKey.StageRenames))) == quietSignals + 2,
            "rename stage writes only begin, one snapshot per provider-boundary quiet signal, and end");
        Check.Require(messages.Any(message => message.Contains(Text.Get(TextKey.StageRenames) + ": 2 groups  Complete")),
            "rename completion counts both processed groups, including a held group, without changing the total");
        if (held)
            Check.Require(rig.Store.HeldCovers(rig.Pair.PairId, PathNames.Key("moved-a.txt"), null) &&
                await rig.Dropbox.ReadOneAsync("a.txt", default) is not null &&
                await rig.Dropbox.ReadOneAsync("moved-a.txt", default) is null,
                "the counted held group keeps its notice and performs no counterpart rename");
    });

    private sealed class QuietRowsStore(IStateStore inner, RunLog log) : ForwardingStateStore(inner)
    {
        public override IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
        {
            foreach (var row in base.ScanRows(pairId, scanId, context))
            {
                yield return row;
                log.ReportProgress();
            }
        }
    }

    /// <summary>Runs one scan pass over the rig's current fake sides, seeding the scan through the store.</summary>
    internal static async Task RunScanAsync(Rig rig, RunLog log, ISide? local = null,
        ISide? dropbox = null, IStateStore? store = null, CancellationToken ct = default,
        IReadOnlySet<long>? retryIssues = null, bool includeDeleted = false) =>
        await RunScannedCoreAsync(rig, log, null, local, dropbox, store, ct, retryIssues, includeDeleted);

    /// <summary>Runs one scan pass and records its authoritative completion, making later scans non-first-run.</summary>
    internal static async Task RunCompletedScanAsync(Rig rig, RunLog log, ISide? local = null,
        ISide? dropbox = null, IStateStore? store = null, CancellationToken ct = default,
        IReadOnlySet<long>? retryIssues = null, bool includeDeleted = false)
    {
        var token = await RunScannedCoreAsync(rig, log, null, local, dropbox, store, ct, retryIssues, includeDeleted);
        CompleteScan(rig, store ?? rig.Store, token, log);
        Check.Require((store ?? rig.Store).FirstRepairDone(rig.Pair.PairId),
            "the completed scan records first-repair done for later passes");
    }

    /// <summary>Runs one scan pass at the module's ports and records its completion, mirroring one Repair.</summary>
    internal static async Task<RepairSummary> RunRepairAsync(Rig rig, RunLog log, EngineSettings settings,
        ISide? local = null, ISide? dropbox = null, IStateStore? store = null, CancellationToken ct = default,
        IReadOnlySet<long>? retryIssues = null,
        Func<string, bool, CancellationToken, Task<DeletionEvidence>>? readEvidence = null,
        IItemAttempts? itemAttempts = null, Func<ExecutorContext, CancellationToken, Task>? before = null,
        bool includeDeleted = false)
    {
        var target = store ?? rig.Store;
        var domain = rig.Pair.Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (target.ReadJournal(domain) is null) SourceFixture.AcquireJournal(target, rig.Pair);
        if (before is not null)
            await before(Context(rig, target, log, local ?? rig.Local, dropbox ?? rig.Dropbox, settings,
                readEvidence, itemAttempts), ct);
        var token = await RunScannedCoreAsync(rig, log, settings, local, dropbox, target, ct,
            retryIssues, includeDeleted, readEvidence);
        var scan = target.ReadScan(rig.Pair.PairId, token);
        var now = DateTimeOffset.UtcNow;
        var summary = new RepairSummary(log.RunId, now, now, true, scan.FirstRun, null, false,
            log.Effects, log.IssuesByKind, log.Path, log.HasFailed);
        CompleteScan(rig, target, token, log);
        return summary;
    }

    internal static Task RunOperationAsync(Rig rig, RunLog log, EngineSettings settings,
        Func<(Syncer Syncer, FolderSync Folders, ExclusionRunner Exclusions), Task> operation)
    {
        var context = Context(rig, rig.Store, log, rig.Local, rig.Dropbox, settings, null, null);
        var ports = new SyncPorts(new LeaseBoundary(),
            RefusingExecutionBoundary.Instance, new LeaseBoundary(), LocalSide.HashSlots);
        var jobs = SyncJobs.Create(context, ports);
        return operation((new Syncer(context, ports), jobs.Items.Folders, jobs.Exclusions));
    }

    internal static void CompleteScan(Rig rig, IStateStore store, Guid token, RunLog log)
    {
        var pairId = rig.Pair.PairId;
        var scan = store.ReadScan(pairId, token);
        var generation = scan.Generation;
        var domain = rig.Pair.Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var position = store.ReadJournal(domain) ?? throw new InvalidOperationException("the seeded journal is absent");
        var cursor = store.ReadSource(pairId, Side.Dropbox)!.Value.Cursor;
        var now = DateTimeOffset.UtcNow;
        var summary = new RepairSummary(log.RunId, now, now, true, scan.FirstRun, null, false,
            log.Effects, log.IssuesByKind, log.Path, log.HasFailed);
        store.FinishRepair(pairId, summary, generation, token,
            new LocalCatchUp(pairId, generation, scan.LocalEpoch,
                new NtfsUsnCheckpoint(position.JournalId, 0, (long)position.NextUsn, 0), scan.ThroughWorkId),
            new DropboxCatchUp(pairId, generation, scan.DropboxEpoch, cursor!, scan.ThroughWorkId), out _);
    }

    internal static async Task<Guid> RunScannedCoreAsync(Rig rig, RunLog log,
        EngineSettings? settings, ISide? local, ISide? dropbox, IStateStore? store, CancellationToken ct,
        IReadOnlySet<long>? retryIssues, bool includeDeleted,
        Func<string, bool, CancellationToken, Task<DeletionEvidence>>? readEvidence = null,
        IItemAttempts? itemAttempts = null)
    {
        var target = store ?? rig.Store;
        var pairId = rig.Pair.PairId;
        var generation = target.ReadPairExecution(pairId)!.Value.Generation;
        var token = target.BeginScan(pairId, generation, includeDeleted);
        var localSink = target.OpenScan(pairId, Side.Local, ScanTestSettings.BatchRows);
        IScanSink remoteSink;
        try { remoteSink = target.OpenScan(pairId, Side.Dropbox, ScanTestSettings.BatchRows); }
        catch (Exception)
        {
            try { await localSink.AbortAsync(); } catch (Exception) { }
            throw;
        }
        var localSkipped = new List<(string Path, TextKey Cause, IssueSubject Subject)>();
        var remoteSkipped = new List<(string Path, TextKey Cause, IssueSubject Subject)>();
        var localFolders = new List<(Side Side, string Key)>();
        var remoteFolders = new List<(Side Side, string Key)>();
        try
        {
            await ListAsync(local ?? rig.Local, localSink, localSkipped, localFolders, false, ct);
            await ListAsync(dropbox ?? rig.Dropbox, remoteSink, remoteSkipped, remoteFolders, includeDeleted, ct);
        }
        catch
        {
            try { await Task.WhenAll(localSink.AbortAsync(), remoteSink.AbortAsync()); } catch (Exception) { }
            throw;
        }
        var context = Context(rig, target, log, local ?? rig.Local, dropbox ?? rig.Dropbox,
            settings ?? Settings, readEvidence, itemAttempts);
        var syncer = new Syncer(context, new SyncPorts(new LeaseBoundary(),
            RefusingExecutionBoundary.Instance, new LeaseBoundary(), LocalSide.HashSlots));
        await syncer.SyncScannedAsync(token,
            new ListedSkips(localSkipped, remoteSkipped, localFolders, remoteFolders), retryIssues, ct);
        return token;
    }

    private static async Task ListAsync(ISide side, IScanSink sink,
        List<(string Path, TextKey Cause, IssueSubject Subject)> skipped, List<(Side Side, string Key)> folders,
        bool includeDeleted, CancellationToken ct)
    {
        await foreach (var item in side.ReadAllAsync(string.Empty,
            new ListOptions(true, false, IncludeDeleted: includeDeleted), ct))
        {
            await sink.AddAsync(item, ct);
            if (item.Entry is { Skip: { } skip } entry)
            {
                skipped.Add((item.Path, Enum.Parse<TextKey>(skip),
                    new IssueSubject([new IssueItem(side.Side, item.Path, entry.Id)], null)));
                if (entry.IsFolder) folders.Add((side.Side, PathNames.KeyFor(side.Side, item.Path)));
            }
        }
        await sink.CompleteAsync();
    }

    private static ExecutorContext Context(Rig rig, IStateStore store, RunLog log, ISide local, ISide dropbox,
        EngineSettings settings, Func<string, bool, CancellationToken, Task<DeletionEvidence>>? readEvidence,
        IItemAttempts? itemAttempts) =>
        new(Local: local, Dropbox: dropbox, Recovery: rig.Recovery, Store: store, Pair: rig.Pair,
            Scope: rig.Scope, Settings: settings, Runtime: PairRuntimes.For(rig.Pair.PairId),
            Admission: rig.Admission, Priority: false, Events: log.Event,
            ReserveDownloadAsync: (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoopDisposable.Instance),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: readEvidence ?? ((path, _, _) =>
                Task.FromResult<DeletionEvidence>(new DeletionEvidence.NoRetainedHistory(path))),
            BeginDownloadAttempt: operation => new RequestAttempt(operation, AttemptOwner.Transfer),
            CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(false),
            UploadAdmissionIdentity: ("account-a", "upload", rig.Pair.PairId),
            ItemAttempts: itemAttempts ?? new FakeItemAttempts(), Opportunity: new object(),
            ExpectedGeneration: store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation)
        {
            ScanId = ScanFixture.TokenOrNone(store, rig.Pair.PairId),
            CaptureFolderCursorAsync = _ => Task.FromResult("fixture-cursor"),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };

    private static async IAsyncEnumerable<Listed> NoFolderChangesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static Rig NewRig(PairConfig pair, string? database = null)
    {
        var store = StateStore.Open(database ?? Path.Combine(FixtureRoot.Value!, Guid.NewGuid().ToString("N") + ".db"));
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        return new Rig(pair, store, new FakeSide(Side.Local), new FakeSide(Side.Dropbox),
            new FakeRecovery(), new FakeAdmission { Level = 3 }, new PathScope(pair, [pair]));
    }

    private static PairConfig Pair()
    {
        const string id = "pair-a";
        var root = Path.Combine(FixtureRoot.Value ?? throw new InvalidOperationException(),
            "roots", "ScanFixture");
        Directory.CreateDirectory(root);
        return new PairConfig(id, id, root, "/ScanFixture", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null,
            new RootBinding(root, "/ScanFixture", "connection-a", WindowsDirectoryListing.ReadIdentity(root),
                new DropboxRootIdentity("account-a", "namespace-a", "namespace-a", "folder-a")));
    }

    private static async Task<Entry> PutAsync(ISide side, string path, byte[]? bytes = null)
    {
        var source = new Entry(path, false, bytes?.Length ?? 1,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), path, "", "", null);
        using var content = new MemoryStream(bytes ?? [1]);
        await using var stage = await side.StageAsync(path, content, source, CancellationToken.None);
        var result = await side.PublishAsync(stage, null, null, CancellationToken.None);
        Check.Require(result.Outcome == RemovalOutcome.Published && result.Final is not null, nameof(PutAsync));
        return result.Final!;
    }

    private static RunLog Log(Rig rig, string folder, string runId) =>
        RunLog.Open(folder, rig.Pair.PairId, runId, _ => { }, Settings.LogBufferCharacters);

    private static EngineSettings ShippedSettings()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null);
    }

    private static async Task WithFolder(string name, Func<string, Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-scanpass-" + name + "-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = FixtureRoot.Value;
        FixtureRoot.Value = folder;
        try { await body(folder); }
        finally
        {
            FixtureRoot.Value = previous;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal sealed record Rig(PairConfig Pair, IStateStore Store, ISide Local, ISide Dropbox,
        ILocalRecovery Recovery, IAdmission Admission, PathScope Scope) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => default;
    }

    private sealed class FakeItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) => throw new NotSupportedException();
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) { }
    }

    /// <summary>Admits recovery callbacks without adding production scope; a failed effect stays the product's own.</summary>
    private sealed class LeaseBoundary : IRecoveryExecutionBoundary, ISelectedExecutionBoundary
    {
        public void RequireCurrent() { }
        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            execute(new Lease(), ct);
        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            execute(new Lease(), ct);
        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            execute(new Lease(), ct);
        public Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) => execute(new Lease(), ct);
        public Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(new Lease(), ct);

        private sealed class Lease : IAdmittedExecution
        {
            public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys) { }
            public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) { }
            public void Revalidate() { }
            public ValueTask ReleaseAdmissionAsync() => default;
            public ValueTask DisposeAsync() => default;
        }
    }
}
