using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for RenameRunner (`--engine-checks syncer`), written by RenameRunner's writer.</summary>
public static class RenameRunnerChecks
{
    private const int DefaultLogBufferCharacters = 262144;
    private static readonly FailureRetryOwner.BackoffSettings AdoptedBackoff = new(1, 1.6, 0, 120);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await LocksAsync();
        await AdmissionAsync();
        await VacatedFileRequiresLiveCoverageAsync();
        DependencySuspension();
        await ListedChronologyAsync();
        await ListedRootChronologyAsync();
        await PendingTargetAncestorAsync();
        await KeySetsAsync();
        await ItemAttemptAuthorityAsync();
        await AdmittedEffectAsync();
        await AdmittedRebaseAsync();
        await AdmittedStaleAsync();
        await AdmittedCancellationAsync();
        await CommittedIssueAsync();
        await IgnoredNameAsync();
        await UpgradeRenameAsync();
        await HoldingSnapshotAsync();
        await MappedHeldFootprintAsync();
        await HeldRenameEndpointsAsync(false);
        await HeldRenameEndpointsAsync(true);
        await UnreadableAsync();
        await SavedUnreadableAsync();
        Measured();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            await FollowAsync(side);
            await OccupiedAsync(side);
            await MappedParentAsync(side);
            foreach (var folder in new[] { false, true })
            {
                await SplitAsync(side, folder, false);
                if (!folder) await SplitAsync(side, folder, true);
                await LeaveAsync(side, folder);
            }
        }
        await LocalHashAsync();
        await BothMovedAsync();
        await HoldsAsync();
        await CarriesPairAsync();
        await CaseCompetitionAsync();
        await FinishCaseAsync();
        await FinishCaseOccupiedAsync();
        await NoneAsync();
        await RebasedChildrenAsync();
        await DropboxFolderCaseAsync();
        await SavedSpellingDriftAsync();
        var freshnessFailures = new List<string>();
        foreach (var check in new Func<Task>[] { StaleRenameReversalsAsync, FreshRenameEvidenceAsync,
            RenameReacquireBoundAsync, RefreshedHintFootprintAsync })
        {
            try { await check(); }
            catch (Exception error) { freshnessFailures.Add(check.Method.Name + ": " + error.Message); }
        }
        Check.Require(freshnessFailures.Count == 0, string.Join(Environment.NewLine, freshnessFailures));
        await ListedAdoptionFallbackAsync();
        await RecordedRenameDestinationAsync();
        await DuplicateFolderDestinationAsync();
        await SavedCollisionSidesAsync();
        await MultipleDestinationOwnersAsync();
        await SelectedDestinationIdentityAsync();
        await FailuresAsync();
    }

    private static async Task ListedRootChronologyAsync()
    {
        foreach (var real in new[] { false, true })
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var f = new Fixture(folder: true, realStore: real);
            var child = f.SeedChild("Old/Child");
            await f.ScanAsync();
            var token = f.Context.ScanId;
            await f.Store.CommitAsync(f.Pair.PairId, StateChange.Empty with { Record = [f.Saved], ListedScanId = token });
            Check.Require(f.Store.CanUseListedEvidence(f.Pair.PairId, token, child.LocalKey, child.DropboxKey, false),
                "Same-kind same-scan folder Record does not invalidate untouched child evidence.");
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { ForgetSubtrees = [(side, f.Saved.KeyOn(side))] });
            Check.Require(!f.Store.CanUseListedEvidence(f.Pair.PairId, token, child.LocalKey, child.DropboxKey, false) &&
                f.Store.CanUseListedEvidence(f.Pair.PairId, token, PathNames.Key("OldSibling"), PathNames.DropboxKey("OldSibling"), true),
                "A forgotten root invalidates children, not adjacent-prefix siblings, on both sides.");
            f.Store.ForgetAllSynced(f.Pair.PairId);
            Check.Require(!f.Store.CanUseListedEvidence(f.Pair.PairId, token, "UNRELATED", "UNRELATED", false),
                "Whole-history retirement invalidates every listed endpoint.");
        }
    }

    private static async Task ListedChronologyAsync()
    {
        foreach (var real in new[] { false, true })
        {
            using var f = new Fixture(realStore: real);
            var historicalLocal = f.LocalOld with { Modified = Fixture.Time.AddSeconds(1) };
            f.Local.Seed(historicalLocal, Fixture.Content);
            await f.ScanAsync();
            f.Local.Seed(f.LocalOld, Fixture.Content);
            var token = f.Context.ScanId;
            bool Eligible(SyncedEntry saved) => f.Store.CanUseListedEvidence(f.Pair.PairId, token,
                saved.LocalKey, saved.DropboxKey, saved.IsFolder, saved);
            Check.Require(Eligible(f.Saved), "Untouched whole-scan evidence is eligible in both stores.");
            await f.Store.CommitAsync(f.Pair.PairId, StateChange.Empty with
            { Record = [f.Saved], ListedScanId = token });
            Check.Require(Eligible(f.Saved), "Same-scan listed Record adds no endpoint marks.");
            var after = f.Saved with { LocalPath = "OLD", LocalKey = PathNames.Key("OLD"),
                DropboxPath = "OLD", DropboxKey = PathNames.DropboxKey("OLD") };
            await f.Store.CommitAsync(f.Pair.PairId, StateChange.Empty with
            { Renames = [new RenameApplied(f.Saved, after, null, null, null)] });
            Check.Require(!Eligible(f.Saved) && !Eligible(after),
                "A queued external file Adopt invalidates both old and new listed endpoints.");
            var refused = false;
            try { await f.Store.CommitAsync(f.Pair.PairId, StateChange.Empty with { Record = [f.Saved], ListedScanId = token }); }
            catch (ChangedUnderneathException) { refused = true; }
            Check.Require(refused && f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == after,
                "Final queued Record guard refuses old listing without overwriting the adopted binding.");
            var oldDropboxOnly = f.Saved with { LocalPath = "Untouched", LocalKey = PathNames.Key("Untouched") };
            Check.Require(f.Store.CanUseListedEvidence(f.Pair.PairId, token, oldDropboxOnly.LocalKey,
                    PathNames.DropboxKey("Untouched"), false),
                "The Dropbox-only queue guard fixture has an untouched Local endpoint.");
            refused = false;
            try { await f.Store.CommitAsync(f.Pair.PairId, StateChange.Empty with
                { Record = [oldDropboxOnly], ListedScanId = token }); }
            catch (ChangedUnderneathException) { refused = true; }
            Check.Require(refused && f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == after &&
                    f.Store.SyncedByKey(f.Pair.PairId, Side.Local, oldDropboxOnly.LocalKey) is null,
                "Final queued Record guard checks the old Dropbox key even when its Local key is untouched.");
            await f.Local.MoveAsync(f.LocalOld, "OLD", default);
            await f.Dropbox.MoveAsync(f.DropboxOld, "OLD", default);
            var reads = 0;
            var context = f.Context with
            {
                Local = new ProxySide(f.Local) { OnRead = _ => reads++ },
                Dropbox = new ProxySide(f.Dropbox) { OnRead = _ => reads++ }
            };
            var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0));
            var historical = f.Store.ScanRows(f.Pair.PairId, token, _ => RowContext.Neutral)
                .Single(row => row.Synced?.LocalId == f.Saved.LocalId);
            Check.Require(historical.Local == historicalLocal && historical.Synced == after,
                "Production scan join retains historical observation beside the current adopted binding.");
            historical = historical with { LocalHash = f.Saved.DropboxContentHash };
            await jobs.Items.RunRowAttemptAsync(historical, default, listedScanId: token);
            Check.Require(reads >= 2 && f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == after &&
                await f.Local.ReadOneAsync("OLD", default) is not null && await f.Dropbox.ReadOneAsync("OLD", default) is not null,
                "Old-path listed Record reobserves after queued Adopt and preserves the adopted paths: real=" + real +
                "; reads=" + reads + "; saved=" + f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId));
        }
    }

    private static void DependencySuspension()
    {
        foreach (var realStore in new[] { false, true })
        foreach (var prerequisiteFirst in new[] { false, true })
        {
            using var f = new Fixture(realStore: realStore);
            var source = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
            var notices = new[] { "Dependent", "Prerequisite" };
            if (prerequisiteFirst) Array.Reverse(notices);
            var through = f.Store.AcquireJournalPage("1", new JournalPosition(1, 1), new JournalPosition(1, 2),
                [(f.Pair.PairId, Fixture.Generation, source.Epoch, source.Revision, false)],
                notices.Select(path => (f.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed,
                    path, null, path, null, false, false))))!.Value;
            WorkSelection Select(string path) => new(f.Pair.PairId, Fixture.Generation, source.Epoch, Side.Local,
                "1", path, PathNames.Key(path), through - 1 + Array.IndexOf(notices, path), through);
            var dependent = Select("Dependent");
            var prerequisite = Select("Prerequisite");
            f.Store.BeginSelection(dependent);
            f.Store.EndSelection(dependent, prerequisite);
            Check.Require(!f.Store.BeginSelection(dependent) && f.Store.ReadWork(dependent).Any(),
                "An attempted anchor refuses without changing its acquired rows or performing effects.");
            f.Store.BeginSelection(prerequisite);
            Check.Require(f.Store.CompleteWork(prerequisite, null), "The prerequisite settles in this pass.");
            f.Store.EndSelection(prerequisite);
            f.Store.BeginSelection(dependent);
            Check.Require(!f.Store.BeginSelection(dependent) && f.Store.ReadWork(dependent).Any(),
                "An attempted anchor refuses without changing its acquired rows or performing effects.");
            Check.Require(f.Store.ReadWork(dependent).Any(), "Suspension preserves dependent responsibility.");
            f.Store.EndSelection(dependent);
            f.Store.EndWorkPass(f.Pair.PairId);
            f.Store.BeginSelection(dependent);
            var nextSource = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
            var next = f.Store.AcquireJournalPage("1", new JournalPosition(1, 2), new JournalPosition(1, 3),
                [(f.Pair.PairId, Fixture.Generation, nextSource.Epoch, nextSource.Revision, false)],
                [(f.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, "Unfinished", null,
                    "unfinished", null, false, false))])!.Value;
            var unfinished = new WorkSelection(f.Pair.PairId, Fixture.Generation, nextSource.Epoch, Side.Local,
                "1", "unfinished", PathNames.Key("Unfinished"), next, next);
            f.Store.EndSelection(dependent, unfinished);
            Check.Require(!f.Store.BeginSelection(dependent) && f.Store.ReadWork(dependent).Any(),
                "An attempted anchor refuses without changing its acquired rows or performing effects.");
            f.Store.EndWorkPass(f.Pair.PairId);
            f.Store.BeginSelection(dependent);
            Check.Require(f.Store.ReadWork(dependent).Any() && f.Store.ReadWork(unfinished).Any(),
                "EndWorkPass drops an unconsumed suspension without losing either acquired responsibility.");
            f.Store.EndSelection(dependent);
        }

    }

    private static async Task VacatedFileRequiresLiveCoverageAsync()
    {
        using var f = new Fixture();
        var incoming = await f.MoveAsync(Side.Local, "New");
        var selection = new WorkSelection(f.Pair.PairId, Fixture.Generation, 0, Side.Local,
            "domain", incoming.Moved.Id, PathNames.Key("New"), 1, 1);
        var items = SyncJobs.Create(f.Context with { Live = true }, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items;
        var refused = false;
        try { await items.RemoveVacatedFileAsync(incoming, f.Saved, selection, default); }
        catch (NotSupportedException) { refused = true; }
        Check.Require(refused && f.Admission.Admitted == 0 && f.Events.Count == 0 &&
            await f.Dropbox.ReadOneAsync("Old", default) == f.DropboxOld,
            "A live Vacate file requests source coverage before ordinary admission, observation or removal.");
        using var folder = new Fixture(folder: true);
        var folderIncoming = await folder.MoveAsync(Side.Local, "New");
        var source = folder.Store.ReadSource(folder.Pair.PairId, Side.Local)!.Value;
        var acquired = folder.Store.AcquireJournalPage("1", new JournalPosition(1, 1), new JournalPosition(1, 2),
            [(folder.Pair.PairId, Fixture.Generation, source.Epoch, source.Revision, false)],
            [(folder.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Renamed, "New", "Old", folderIncoming.Moved.Id,
                folderIncoming.Moved, false, false))]);
        var folderSelection = new WorkSelection(folder.Pair.PairId, Fixture.Generation, source.Epoch, Side.Local,
            "1", folderIncoming.Moved.Id, PathNames.Key("New"), acquired!.Value, acquired.Value);
        var folderItems = SyncJobs.Create(folder.Context with { Live = true }, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items;
        refused = false;
        try
        {
            await folderItems.Folders.RemoveVacatedAsync(Row.ForOneSide(Side.Dropbox, folder.Saved.LocalKey,
                folder.DropboxOld, folder.Saved, [], null, RowContext.Neutral), folderIncoming, folderSelection, default);
        }
        catch (NotSupportedException) { refused = true; }
        Check.Require(refused && folder.Admission.Admitted == 0 && folder.Events.Count == 0 &&
            !folder.Store.ActiveFolderRemovals(folder.Pair.PairId, default).Any(),
            "A live Vacate folder requests source coverage before freezing or submitting a removal.");
    }

    private static async Task PendingTargetAncestorAsync()
    {
        using var fixture = new Fixture(realStore: true);
        await fixture.Local.EnsureFolderAsync("Target", default);
        var moved = await fixture.MoveAsync(Side.Local, "Target/New");
        var store = fixture.Store;
        var pair = fixture.Pair;
        var source = store.ReadSource(pair.PairId, Side.Dropbox)!.Value;
        var root = pair.Roots!.Dropbox;
        var provenance = new SourceProvenance(root.AccountId, root.RootNamespaceId, root.FolderId,
            pair.DropboxRoot, true, true, true, true, Fixture.Generation, source.Epoch);
        store.AdvanceSource(pair.PairId, Side.Dropbox, Fixture.Generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision, provenance, "before", source.Obligation);
        source = store.ReadSource(pair.PairId, Side.Dropbox)!.Value;
        store.AcquireDropboxPage(pair.PairId, Fixture.Generation, source.Epoch, source.Revision, provenance, "before",
            [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Target", null, "parent-id", null, false, true)], "after");
        var refused = false;
        try { await fixture.RunAsync([moved]); }
        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged)
        { refused = true; }
        Check.Require(refused && await fixture.Dropbox.ReadOneAsync("Target", default) is null &&
            await fixture.Dropbox.ReadOneAsync("Target/New", default) is null &&
            await fixture.Dropbox.ReadOneAsync("Old", default) == fixture.DropboxOld,
            "CL03: pending destination ancestor refuses before folder creation or rename effect");
    }

    private static async Task LocksAsync()
    {
        foreach (var movedSide in new[] { Side.Local, Side.Dropbox })
        {
            using var store = new InMemoryStateStore();
            var pair = new PairConfig(nameof(RenameRunnerChecks), nameof(RenameRunnerChecks),
                Path.GetTempPath(), "/Pair", "Account", [], [], FirstRunPolicy.NewestWins, true, null);
            var settings = new EngineSettings("", "", "", "", ".case",
                RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", DefaultLogBufferCharacters, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes };
            var local = new FakeSide(Side.Local);
            var dropbox = new FakeSide(Side.Dropbox);
            var events = new List<RunEvent>();
            var context = new ExecutorContext(local, dropbox, new FakeRecovery(), store, pair,
                new PathScope(pair, [pair]), settings, new ReplaceTurn(), new FakeAdmission(), false, events.Add,
                ReserveDownloadAsync: RefuseReserveDownload, Time: TimeProvider.System,
                ReadDeletionEvidenceAsync: RefuseDeletionEvidence,
                BeginDownloadAttempt: RefuseBeginDownloadAttempt,
                CompleteDownloadAttemptAsync: RefuseCompleteDownloadAttempt,
                UploadAdmissionIdentity: ("", "", ""), ItemAttempts: new RefusingItemAttempts(),
                Opportunity: new object(), ExpectedGeneration: Fixture.Generation);
            store.SavePair(pair);
            var saved = new SyncedEntry(PathNames.Key("Old"), "Old", PathNames.DropboxKey("Old"), "Old",
                false, "local-id", 1, DateTimeOffset.UnixEpoch.UtcTicks, "dropbox-id", "hash");
            store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
            var moved = new Entry("New", false, saved.LocalSize, DateTimeOffset.UnixEpoch,
                movedSide == Side.Local ? saved.LocalId : saved.DropboxId, "revision", "hash", null);
            var localById = movedSide == Side.Local ? moved : moved with { Path = "OLD", Id = saved.LocalId };
            var syncer = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items;

            foreach (var (side, path) in new[]
            {
                (Side.Local, "Old"), (Side.Dropbox, "Old"),
                (Side.Local, "New"), (Side.Dropbox, "New")
            })
            {
                var key = side == Side.Local ? PathLocks.LocalKey(pair, path) : PathLocks.DropboxKey(pair, path);
                await using var held = await PathLocks.Shared.EnterAsync([key], CancellationToken.None);
                using var cancellation = new CancellationTokenSource();
                var admitted = ((FakeAdmission)context.Admission).Admitted;
                var pending = syncer.RenameAsync([new RenameCandidate(movedSide, moved, saved)], localById,
                    cancellation.Token);
                Check.Require(((FakeAdmission)context.Admission).Admitted == admitted + 1 &&
                    ((FakeAdmission)context.Admission).Completed == 0,
                    "Rename admission precedes every conflicting path lock without a completion callback.");
                Check.Require(!pending.IsCompleted, $"Rename must wait for {side} {path} before reading or writing.");
                cancellation.Cancel();
                var cancelled = false;
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                Check.Require(cancelled, "Cancellation while waiting for rename locks must propagate.");
                Check.Require(store.SyncedById(pair.PairId, movedSide, moved.Id) == saved,
                    "A cancelled rename must leave the saved binding unchanged.");
                Check.Require(events.Count == 0 && store.Issues(pair.PairId).Count == 0,
                    "A cancelled rename must not emit events or create issues.");
            }
        }

    }

    private static async Task AdmissionAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(side, "New");
            var admission = new AdaptiveLimiter(1, 1, TimeProvider.System, new FakeNetworkSignal(), _ => { }, AdoptedBackoff);
            var context = f.Context with { Admission = admission };
            await using (await admission.AdmitAsync(false, default))
            {
                using var cancellation = new CancellationTokenSource();
                var pending = f.RunAsync([moved], context: context, ct: cancellation.Token);
                Check.Require(!pending.IsCompleted, "A rename waits for outer admission before observing either provider.");
                using var lockCancellation = new CancellationTokenSource();
                var lockTask = PathLocks.Shared.EnterAsync([
                    PathLocks.LocalKey(f.Pair, "Old"), PathLocks.DropboxKey(f.Pair, "Old"),
                    PathLocks.LocalKey(f.Pair, "New"), PathLocks.DropboxKey(f.Pair, "New")], lockCancellation.Token);
                var locksFree = lockTask.IsCompletedSuccessfully;
                if (!locksFree) lockCancellation.Cancel();
                try { await using var hold = await lockTask; }
                catch (OperationCanceledException) { }
                cancellation.Cancel();
                var cancelled = false;
                try { await pending; }
                catch (OperationCanceledException) { cancelled = true; }
                Check.Require(locksFree && cancelled && f.Events.Count == 0 &&
                    f.Store.Issues(f.Pair.PairId).Count == 0 &&
                    f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) == f.Saved,
                    "Queued cancellation leaves every path lock free, emits nothing and preserves the binding.");
            }
            using var completedCancellation = new CancellationTokenSource();
            var completed = f.RunAsync([moved], context: context, ct: completedCancellation.Token);
            var synchronous = completed.IsCompleted;
            if (!synchronous) completedCancellation.Cancel();
            RenameDecision? result = null;
            try { result = await completed; }
            catch (OperationCanceledException) when (!synchronous) { }
            Check.Require(synchronous && result?.Kind == RenameKind.Follow,
                "A level-one rename completes without a nested admission deadlock after queued cancellation.");
        }

        using var blocked = new Fixture();
        var candidate = await blocked.MoveAsync(Side.Local, "New");
        var limiter = new AdaptiveLimiter(1, 1, TimeProvider.System, new FakeNetworkSignal(), _ => { }, AdoptedBackoff);
        await using var occupied = await limiter.AdmitAsync(false, default);
        blocked.Local.FailNext("Old", new InvalidOperationException("Observation preceded admission"));
        blocked.Dropbox.FailNext("Old", new InvalidOperationException("Observation preceded admission"));
        using var queuedCancellation = new CancellationTokenSource();
        var queued = blocked.RunAsync([candidate], context: blocked.Context with { Admission = limiter },
            ct: queuedCancellation.Token);
        Check.Require(!queued.IsCompleted, "A blocked permit prevents both local and Dropbox observations.");
        queuedCancellation.Cancel();
        var wasCancelled = false;
        try { await queued; }
        catch (OperationCanceledException) { wasCancelled = true; }
        Check.Require(wasCancelled && blocked.Events.Count == 0,
            "Cancellation before observation never consumes provider failures or reports an effect.");
    }

    private static async Task HoldingSnapshotAsync()
    {
        foreach (var (mapped, path) in new[]
        {
            (false, "Old"), (false, "New"),
            (true, "a:b/Old"), (true, "a:b/New"), (true, "a:b")
        })
        {
            using var f = new Fixture(mappedParent: mapped);
            var moved = await f.MoveAsync(Side.Dropbox, mapped ? "a:b/New" : "New");
            var key = PathNames.KeyFor(Side.Dropbox, path);
            f.Local.Seed(f.LocalOld with { Modified = Fixture.Time.AddSeconds(1) }, null);
            var result = await f.RunAsync([moved], runHolds: [moved with
            {
                Moved = moved.Moved with { Path = path },
                Synced = moved.Synced with { LocalKey = key, DropboxKey = key }
            }]);
            Check.Require(result.Kind == RenameKind.Hold && f.Events.Count == 0 &&
                f.Store.Issues(f.Pair.PairId).Count == 0 && f.Admission.Admitted == 1 && f.Admission.Completed == 0 &&
                f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved,
                "A run-local exact or ancestor hold protects mapped paths without a store issue or hash.");
        }

        foreach (var (cause, held) in new[]
        {
            (TextKey.FailDropboxNoPermission, true), (TextKey.FailDropboxNameRejected, true),
            (TextKey.DropboxRefused, false), (TextKey.FailChanged, false),
            (TextKey.FailDropboxNoSpace, false), (TextKey.FailLocalNoSpace, false), (TextKey.ReadLocalFailed, false)
        })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Dropbox, "New");
            f.Raise(IssueKind.Failure, cause, "Old", Side.Local);
            if (held) f.Local.Seed(f.LocalOld with { Modified = Fixture.Time.AddSeconds(1) }, null);
            var result = await f.RunAsync([moved]);
            Check.Require(result.Kind == (held ? RenameKind.Hold : RenameKind.Follow) &&
                f.Store.Issues(f.Pair.PairId).Count == (held ? 1 : 0) &&
                f.Events.Count == (held ? 0 : 1) && f.Admission.Admitted == 1 && f.Admission.Completed == 0,
                "Definite refusal holds before hashing; temporary failures retry and close after a successful rename.");
        }

        foreach (var cause in new[] { TextKey.ClashMappedNames, TextKey.ClashMappedDiffers,
            TextKey.FailDropboxNoPermission, TextKey.FailDropboxNameRejected })
        {
            using var f = new Fixture(mappedParent: true);
            var moved = await f.MoveAsync(Side.Dropbox, "a:b/New");
            var failure = cause is TextKey.FailDropboxNoPermission or TextKey.FailDropboxNameRejected;
            f.Raise(failure ? IssueKind.Failure : IssueKind.NameClash, cause, f.DropboxOld.Path, Side.Dropbox);
            var result = await f.RunAsync([moved],
                retry: f.Store.Issues(f.Pair.PairId).Select(issue => issue.Id).ToHashSet());
            Check.Require(result.Kind == RenameKind.Follow && f.Store.Issues(f.Pair.PairId).Count == (failure ? 0 : 1),
                "Rename consumes the supplied retry/exception snapshot; its existing closure removes only Failures.");
        }

        using var current = new Fixture();
        var fresh = await current.MoveAsync(Side.Dropbox, "New");
        var heldResult = await current.RunAsync([fresh], runHolds: [fresh with
        {
            Synced = fresh.Synced with { LocalKey = PathNames.Key("New"), DropboxKey = PathNames.Key("New") }
        }]);
        Check.Require(heldResult.Kind == RenameKind.Hold && current.Events.Count == 0 &&
            current.Store.SyncedById(current.Pair.PairId, Side.Local, current.LocalOld.Id) == current.Saved,
            "A current-run hold remains effective even when the retry kind snapshot has no corresponding issue.");
    }

    private static async Task UnreadableAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var (mapped, protectedPath) in new[]
        {
            (false, "Old"), (false, "New"), (false, "Old/Unreadable"), (false, "New/Unreadable"),
            (true, "a_b/Old"), (true, "a_b/New"), (true, "a_b/Old/Unreadable"),
            (true, "a_b/New/Unreadable"), (true, "a_b")
        })
        {
            using var f = new Fixture(folder: true, mappedParent: mapped, realStore: true);
            var target = mapped ? (side == Side.Local ? "a_b/New" : "a:b/New") : "New";
            var moved = await f.MoveAsync(side, target);
            await f.ScanAsync();
            UnreadableMember.Record(f.Store, f.Pair.PairId, protectedPath);
            f.Local.FailNext(f.LocalOld.Path, new InvalidOperationException("Unreadable footprint was observed"));
            f.Dropbox.FailNext(f.DropboxOld.Path, new InvalidOperationException("Unreadable footprint was observed"));
            var result = await f.RunAsync([moved]);
            Check.Require(result.Kind == RenameKind.Hold && f.Store.Issues(f.Pair.PairId).Single(Unskipped) is
                { Kind: IssueKind.Failure, Cause: TextKey.ReadLocalFailed } &&
                f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) == f.Saved &&
                f.ScanEntry(side, moved.Moved.Id)?.Path == moved.Moved.Path &&
                f.ScanEntry(Other(side), Other(side) == Side.Local ? f.LocalOld.Id : f.DropboxOld.Id)?.Path ==
                    (Other(side) == Side.Local ? f.LocalOld.Path : f.DropboxOld.Path),
                "Unreadable old, new, ancestor and descendant local footprints fail before reads, effects or rebasing.");
            Check.Require(f.Events.Single().Kind == RunEventKind.Issue && f.Admission.Completed == 0,
                "An unreadable rename raises one temporary Failure, never an effect or action completion.");
        }

        using (var f = new Fixture(folder: true))
        {
            var moved = await f.MoveAsync(Side.Dropbox, "New");
            var localById = (await f.MoveAsync(Side.Local, "Elsewhere")).Moved;
            UnreadableMember.Record(f.Store, f.Pair.PairId, localById.Path);
            f.Local.FailNext("Old", new InvalidOperationException("Unreadable footprint was observed"));
            f.Dropbox.FailNext("Old", new InvalidOperationException("Unreadable footprint was observed"));
            var result = await SyncJobs.Create(f.Context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items.RenameAsync([moved], localById, default);
            Check.Require(result.Kind == RenameKind.Hold &&
                f.Store.Issues(f.Pair.PairId).Single(Unskipped) is { Kind: IssueKind.Failure, Cause: TextKey.ReadLocalFailed } &&
                f.Store.SyncedById(f.Pair.PairId, Side.Local, localById.Id) == f.Saved &&
                f.Events.Single().Kind == RunEventKind.Issue && f.Admission.Completed == 0,
                "An unreadable local identity path outside the candidate paths fails before observation or effects.");
        }

        using var independent = new Fixture(realStore: true);
        var candidate = await independent.MoveAsync(Side.Dropbox, "New");
        UnreadableMember.Record(independent.Store, independent.Pair.PairId, "Old");
        Check.Require((await independent.RunAsync([candidate])).Kind == RenameKind.Hold &&
            independent.Store.SyncedById(independent.Pair.PairId, Side.Local, independent.LocalOld.Id) == independent.Saved &&
            independent.Store.Issues(independent.Pair.PairId).Single(Unskipped).Cause == TextKey.ReadLocalFailed,
            "The unreadable attempt leaves a temporary Failure and preserves its saved binding.");
        UnreadableMember.Release(independent.Store, independent.Pair.PairId, "Old");
        UnreadableMember.Record(independent.Store, independent.Pair.PairId, "OldSibling");
        UnreadableMember.Record(independent.Store, independent.Pair.PairId, "NewSibling");
        Check.Require((await independent.RunAsync([candidate])).Kind == RenameKind.Follow &&
            !independent.Store.Issues(independent.Pair.PairId).Any(Unskipped),
            "A later readable attempt closes the Failure; adjacent unreadable prefixes do not block it.");
    }

    private static bool Unskipped(Issue issue) => issue.Kind != IssueKind.Skipped;

    private static async Task SavedUnreadableAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var leave in new[] { false, true })
        {
            using var f = new Fixture(folder: true, realStore: true);
            var child = SyncedEntries.From(
                f.LocalOld with { Path = "Unreadable/Child", IsFolder = false, Id = "saved-local-child" },
                f.DropboxOld with { Path = "Old/Child", IsFolder = false, Id = "saved-dropbox-child" });
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [child] });
            var moved = await f.MoveAsync(side, "New");
            await f.ScanAsync();
            var pair = leave ? f.Pair with { Exclusions = [new Exclusion(side, "New", true)] } : f.Pair;
            var context = f.Context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
            UnreadableMember.Record(f.Store, f.Pair.PairId, "Unreadable");
            f.Local.FailNext("Old", new InvalidOperationException("Saved protected footprint was observed"));
            f.Dropbox.FailNext("Old", new InvalidOperationException("Saved protected footprint was observed"));
            var result = await f.RunAsync([moved], context: context);
            Check.Require(result.Kind == RenameKind.Hold &&
                f.Store.Issues(f.Pair.PairId).Single(Unskipped) is { Kind: IssueKind.Failure, Cause: TextKey.ReadLocalFailed } &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, child.LocalKey) == child &&
                f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved &&
                f.ScanEntry(side, moved.Moved.Id)?.Path == "New" && f.Events.Single().Kind == RunEventKind.Issue &&
                f.Admission.Completed == 0,
                "A saved-only cross-linked counterpart blocks Follow and Leave before any read, forget or rebase.");
        }
    }

    private static async Task FollowAsync(Side side)
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(side, "New");
        moved = moved with { Moved = moved.Moved with { Modified = Fixture.Time.AddMinutes(1), ContentHash = "edited" } };
        f.For(side).Seed(moved.Moved, Fixture.Content);
        await f.ScanAsync();
        f.Raise(IssueKind.Failure, TextKey.FailChanged, "Old", side);
        var result = await f.RunAsync([moved]);
        var saved = f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id);
        Check.Require(result.Kind == RenameKind.Follow && saved?.LocalPath == "New" && saved.DropboxPath == "New",
            "Follow moves the counterpart and links both new paths.");
        Check.Require(await f.For(Other(side)).ReadOneAsync("Old", default) is null &&
            await f.For(Other(side)).ReadOneAsync("New", default) is not null, "Follow moves, never copies or deletes the incoming item.");
        f.CheckStamps(saved!);
        Check.Require(f.ScanEntry(Other(side), Other(side) == Side.Local ? f.LocalOld.Id : f.DropboxOld.Id)?.Path == "New",
            "Follow rewrites the counterpart scan row.");
        Check.Require(f.Store.Issues(f.Pair.PairId).Count == 0, "A successful Follow closes prior Failures.");
        var effect = f.Events.Single();
        Check.Require(effect.Kind == RunEventKind.Effect && effect.Path == "New" && effect.Detail == "Old" &&
            Massdex.SyncShark.Engine.Run.RunLog.Present(effect).Outcome == Other(side).ToString(), "Follow emits the committed counterpart move.");
        Check.Require(f.Admission.Admitted == 1 && f.Admission.Completed == (side == Side.Local ? 1 : 0),
            "Every rename admits once; only a Dropbox effect completes an action.");
    }

    private static async Task LocalHashAsync()
    {
        foreach (var (idChanged, timeChanged, sizeChanged, edited, expected) in new[]
        {
            (false, false, false, false, RenameKind.Follow),
            (true, false, false, false, RenameKind.Follow),
            (false, true, false, false, RenameKind.Follow),
            (true, true, false, true, RenameKind.Split),
            (false, true, true, false, RenameKind.Split)
        })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Dropbox, "New");
            var local = f.LocalOld with
            {
                Id = idChanged ? "replacement-local" : f.LocalOld.Id,
                Modified = timeChanged ? Fixture.Time.AddSeconds(1) : Fixture.Time,
                Size = sizeChanged ? Fixture.Content.Length + 1 : Fixture.Content.Length
            };
            // Missing bytes prove unchanged metadata and changed size do not request a hash.
            f.Local.Seed(local, sizeChanged || !idChanged && !timeChanged ? null : edited ? [9, 8, 7] : Fixture.Content);
            var decision = await f.RunAsync([moved]);
            Check.Require(decision.Kind == expected, "Touched content follows; changed content or size splits.");
            if (expected == RenameKind.Follow)
            {
                var saved = f.Store.SyncedById(f.Pair.PairId, Side.Dropbox, moved.Moved.Id)!;
                f.CheckStamps(saved);
                Check.Require(saved.LocalId == local.Id, "Follow records the current counterpart identity without refreshing stamps.");
            }
            else
                Check.Require(f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.InfoMovedEdited,
                    "A changed counterpart produces InfoMovedEdited.");
        }
    }

    private static async Task SplitAsync(Side side, bool folder, bool edited)
    {
        using var f = new Fixture(folder);
        var child = folder ? f.SeedChild("Old/Child") : null;
        var moved = await f.MoveAsync(side, "New");
        var counterpart = side == Side.Local ? f.DropboxOld : f.LocalOld;
        if (edited)
            f.For(Other(side)).Seed(counterpart with { Size = counterpart.Size + 1, ContentHash = "edited" }, Fixture.Content);
        else
            await f.For(Other(side)).DeleteAsync(counterpart, null, default);
        await f.ScanAsync();
        var result = await f.RunAsync([moved]);
        Check.Require(result.Kind == RenameKind.Split && f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) is null,
            "Split forgets the item's mapping.");
        if (child is not null)
            Check.Require(f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId) == child,
                "Splitting a folder forgets only the folder, not descendants.");
        Check.Require(f.ScanEntry(side, moved.Moved.Id)?.Path == "New" &&
            await f.For(side).ReadOneAsync("New", default) == moved.Moved, "Split leaves scan rows and moved content untouched.");
        var issue = f.Store.Issues(f.Pair.PairId).Single();
        Check.Require(issue.Kind == IssueKind.Info && issue.Cause == (edited ? TextKey.InfoMovedEdited : TextKey.InfoMovedDeleted),
            "Split explains an edited or absent counterpart.");
        Check.Require(issue.Subject.Incoming == side && issue.Subject.Items.Count == (edited ? 2 : 1),
            "Split's subject names the moved candidate and, when present, the observed counterpart.");
        Check.Require(f.Events.All(e => e.Kind == RunEventKind.Issue) && f.Admission.Admitted == 1 &&
            f.Admission.Completed == 0, "Split admits its observations but has no file effect or action completion.");
    }

    private static async Task BothMovedAsync()
    {
        foreach (var sameKey in new[] { true, false })
        {
            using var f = new Fixture(true);
            var child = f.SeedChild("Old/Child");
            var local = await f.MoveAsync(Side.Local, "New");
            var dropbox = await f.MoveAsync(Side.Dropbox, sameKey ? "New" : "Elsewhere");
            await f.ScanAsync();
            var result = await f.RunAsync([local, dropbox]);
            Check.Require(result.Kind == (sameKey ? RenameKind.Adopt : RenameKind.Split),
                "Both moves adopt one new key or split different new keys.");
            var saved = f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id);
            if (sameKey)
            {
                Check.Require(saved?.LocalPath == "New" && saved.DropboxPath == "New", "Adopt relinks both moved entries.");
                f.CheckStamps(saved!);
                var rebased = f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId)!;
                Check.Require(rebased.LocalPath == "New/Child" && rebased.DropboxPath == "New/Child",
                    "Folder Adopt rebases saved descendants on both sides.");
                Check.Require(f.Events.Count == 0, "Adopt has no file effect.");
            }
            else
            {
                Check.Require(saved is null && f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId) == child,
                    "Both-moved Split forgets only the folder.");
                Check.Require(f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.InfoMovedTwice,
                    "Different new keys raise InfoMovedTwice.");
            }
            Check.Require(f.ScanEntry(Side.Local, child.LocalId)?.Path == "New/Child" &&
                f.ScanEntry(Side.Dropbox, child.DropboxId)?.Path == (sameKey ? "New/Child" : "Elsewhere/Child"),
                "Adopt and Split leave both scan listings unchanged.");
        }
    }

    private static async Task LeaveAsync(Side side, bool folder)
    {
        using var f = new Fixture(folder);
        var child = folder ? f.SeedChild("Old/Child") : null;
        var moved = await f.MoveAsync(side, "New");
        var pair = f.Pair with { Exclusions = [new Exclusion(side, "New", true)] };
        var context = f.Context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        f.Raise(IssueKind.Failure, TextKey.FailChanged, "Old", side);
        await f.ScanAsync();
        var result = await f.RunAsync([moved], context: context);
        Check.Require(result.Kind == RenameKind.Leave && f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) == f.Saved &&
            (child is null || f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId) == child),
            "Leave preserves the mapping and subtree until ordinary old-path conflict decisions settle.");
        Check.Require(await f.For(side).ReadOneAsync("New", default) is not null &&
            await f.For(Other(side)).ReadOneAsync("Old", default) is not null &&
            f.ScanEntry(Other(side), side == Side.Local ? f.DropboxOld.Id : f.LocalOld.Id)?.Path == "Old",
            "Leave changes no files or scan rows.");
        Check.Require(f.Events.Count == 0 && f.Store.Issues(f.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
            "Leave does not claim settlement before the ordinary old-path action.");
    }

    private static async Task HoldsAsync()
    {
        foreach (var atNew in new[] { false, true })
        foreach (var skipped in new[] { false, true })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Dropbox, "New");
            if (skipped && atNew)
            {
                moved = moved with { Moved = moved.Moved with { Skip = "skipped" } };
                f.Dropbox.Seed(moved.Moved, Fixture.Content);
            }
            else if (skipped)
                f.Local.Seed(f.LocalOld with { Skip = "skipped", Modified = Fixture.Time.AddSeconds(1) }, null);
            else
                f.Raise(IssueKind.NameClash, TextKey.ClashNameTaken, atNew ? "New" : "Old", atNew ? Side.Dropbox : Side.Local);
            var result = await f.RunAsync([moved]);
            Check.Require(result.Kind == RenameKind.Hold && f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved,
                "An old or new skipped/held path holds without changing the link.");
            Check.Require(f.Events.Count == 0 && f.Admission.Admitted == 1 && f.Admission.Completed == 0 &&
                await f.Local.ReadOneAsync("New", default) is null, "Hold admits once but has no effect or completion.");
        }
    }

    private static async Task CarriesPairAsync()
    {
        foreach (var nestedSide in new[] { Side.Local, Side.Dropbox })
        {
            using var f = new Fixture(true);
            var moved = await f.MoveAsync(Side.Local, "New");
            var nested = f.Pair with
            {
                PairId = "Nested",
                LocalRoot = nestedSide == Side.Local ? Path.Combine(f.Pair.LocalRoot, "New", "Inner") : Path.Combine(Path.GetTempPath(), "OtherRoot"),
                DropboxRoot = nestedSide == Side.Dropbox ? f.Pair.DropboxRoot + "/Old/Inner" : "/OtherRoot"
            };
            var context = f.Context with { Scope = new PathScope(f.Pair, [f.Pair, nested]) };
            var result = await f.RunAsync([moved], context: context);
            Check.Require(result.Kind == RenameKind.Notice && f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.FailCarriesOtherPair &&
                f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved,
                "A folder carrying another pair on either side is refused without changing its link.");
            Check.Require(await f.Dropbox.ReadOneAsync("Old", default) is not null &&
                f.Admission.Admitted == 1 && f.Admission.Completed == 0,
                "A refused folder rename never moves the protected counterpart.");
        }
    }

    private static async Task CaseCompetitionAsync()
    {
        foreach (var mappedParent in new[] { false, true })
        foreach (var (localSeconds, dropboxSeconds, runSeconds, folder, expected, winner) in new[]
        {
            (2, 1, 3, false, RenameKind.Follow, Side.Local),
            (1, 2, 3, false, RenameKind.Follow, Side.Dropbox),
            (1, 1, 3, false, RenameKind.Notice, Side.Local),
            (1, 2, 1, false, RenameKind.Notice, Side.Local),
            (2, 1, 3, true, RenameKind.Notice, Side.Local)
        })
        {
            using var f = new Fixture(folder, mappedParent);
            var local = await f.MoveAsync(Side.Local, mappedParent ? "a_b/OLD" : "OLD");
            var dropbox = await f.MoveAsync(Side.Dropbox, mappedParent ? "a:b/old" : "old");
            local = local with { Moved = local.Moved with { Modified = Fixture.Time.AddSeconds(localSeconds) } };
            dropbox = dropbox with { Moved = dropbox.Moved with { Modified = Fixture.Time.AddSeconds(dropboxSeconds) } };
            f.Local.Seed(local.Moved, Fixture.Content);
            f.Dropbox.Seed(dropbox.Moved, Fixture.Content);
            await f.ScanAsync();
            var decision = await f.RunAsync([local, dropbox], Fixture.Time.AddSeconds(runSeconds));
            Check.Require(decision.Kind == expected, "Competing case moves use supplied RunStart and whole-second times; folders tie.");
            if (expected == RenameKind.Follow)
            {
                var path = winner == Side.Local ? "OLD" : "old";
                var localPath = mappedParent ? "a_b/" + path : path;
                var dropboxPath = mappedParent ? "a:b/" + path : path;
                var saved = f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id)!;
                Check.Require(saved.LocalPath == localPath && saved.DropboxPath == dropboxPath && decision.MovedSide == winner,
                    "The newer case spelling wins on both sides without changing mapped parents.");
                var written = Other(winner);
                var target = written == Side.Local ? localPath : dropboxPath;
                var counterpart = written == Side.Local ? local.Moved : dropbox.Moved;
                Check.Require(decision.Target == target &&
                    (await f.For(written).ReadOneAsync(target, default)) is { } actual &&
                    actual.Path == target && actual.Id == counterpart.Id &&
                    f.ScanEntry(written, counterpart.Id)?.Path == target &&
                    f.Events.Single().Path == target,
                    "The returned case-competition target equals the executed, scanned and logged mapped destination.");
                if (mappedParent)
                    Check.Require(await f.Dropbox.ReadOneAsync("a_b", default) is null,
                        "Case competition never creates a Dropbox folder with the local mapped spelling.");
                f.CheckStamps(saved);
            }
            else
                Check.Require(f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.TieCaseBoth &&
                    f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved,
                    "A case tie raises a notice and leaves the link alone.");
        }
    }

    private static async Task FinishCaseAsync()
    {
        foreach (var localPath in new string?[] { "OLD", null, "Elsewhere" })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Dropbox, "OLD" + f.Context.Settings.CaseRenameSuffix);
            Entry? localById = localPath is null ? null : (await f.MoveAsync(Side.Local, localPath)).Moved;
            // Recovery must not hash local metadata or adopt an intermediate spelling.
            if (localById is not null) f.Local.Seed(localById with { Modified = Fixture.Time.AddSeconds(1) }, null);
            await f.ScanAsync();
            var decision = await SyncJobs.Create(f.Context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items.RenameAsync([moved], localById, default);
            var target = localPath == "OLD" ? "OLD" : "Old";
            var saved = f.Store.SyncedById(f.Pair.PairId, Side.Dropbox, f.DropboxOld.Id)!;
            Check.Require(decision.Kind == RenameKind.FinishCaseRename && saved.DropboxPath == target && saved.LocalPath == "Old",
                "FinishCaseRename uses current local case or the saved spelling, and updates only Dropbox.");
            f.CheckStamps(saved);
            Check.Require(await f.Dropbox.ReadOneAsync(moved.Moved.Path, default) is null &&
                f.ScanEntry(Side.Dropbox, f.DropboxOld.Id)?.Path == target,
                "The case marker is removed from files and the scan, never propagated.");
            Check.Require(f.Events.Single().Detail == moved.Moved.Path && f.Admission.Admitted == 1 && f.Admission.Completed == 1,
                "Case recovery logs the intermediate-to-target move and completes one permit.");
        }
    }

    private static async Task FinishCaseOccupiedAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Dropbox, "OLD" + f.Context.Settings.CaseRenameSuffix);
        var occupant = f.DropboxOld with { Id = "occupant" };
        f.Dropbox.Seed(occupant, Fixture.Content);
        await f.ScanAsync();
        var decision = await f.RunAsync([moved]);
        var issue = f.Store.Issues(f.Pair.PairId).Single();
        Check.Require(decision.Kind == RenameKind.Hold && issue.Kind == IssueKind.Failure &&
            issue.Cause == TextKey.FailChanged && issue.Path == moved.Moved.Path,
            "An occupied case-recovery target is an item Failure and Hold, not a run-stopping exception");
        Check.Require(await f.Dropbox.ReadOneAsync(occupant.Path, default) == occupant &&
            await f.Dropbox.ReadOneAsync(moved.Moved.Path, default) == moved.Moved &&
            f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved &&
            f.ScanEntry(Side.Dropbox, moved.Moved.Id) == moved.Moved,
            "Failed case recovery preserves the occupant, intermediate entry, scan and saved mapping");
        Check.Require(f.Events.Single().Kind == RunEventKind.Issue &&
            f.Admission.Admitted == 1 && f.Admission.Completed == 1,
            "Failed case recovery completes its permit without reporting a move");
    }

    private static async Task NoneAsync()
    {
        foreach (var vanished in new[] { true, false })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Local, "New");
            f.Store.Commit(f.Pair.PairId, vanished
                ? StateChange.Empty with { ForgetSelf = [(Side.Local, f.Saved.LocalKey)] }
                : StateChange.Empty with { Record = [f.Saved with { LocalPath = "New", LocalKey = PathNames.Key("New") }] });
            f.Local.FailNext(vanished ? "Old" : "New", new InvalidOperationException("No reads expected"));
            var decision = await f.RunAsync([moved]);
            Check.Require(decision.Kind == RenameKind.None && f.Events.Count == 0 && f.Store.Issues(f.Pair.PairId).Count == 0 &&
                f.Admission.Admitted == 1 && f.Admission.Completed == 0,
                "A vanished or already-rebased link admits once but returns None before observation or mutation.");
        }
    }

    private static async Task OccupiedAsync(Side side)
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(side, "New");
        var occupant = (side == Side.Local ? f.DropboxOld : f.LocalOld) with { Path = "New", Id = "occupant" };
        f.For(Other(side)).Seed(occupant, [9, 8, 7]);
        var decision = await f.RunAsync([moved]);
        var issue = f.Store.Issues(f.Pair.PairId).Single();
        Check.Require(decision.Kind == RenameKind.Hold && issue.Kind == IssueKind.NameClash && issue.Cause == TextKey.ClashNameTaken &&
            issue.Subject.Incoming == side && issue.Subject.Items.Contains(new IssueItem(Other(side), "New", occupant.Id)),
            "A taken Follow target holds a NameClash naming the moved side and occupant.");
        Check.Require(issue.Subject.Items.Count == 3 && issue.Subject.Items[2] == new IssueItem(Other(side), "New", occupant.Id),
            "The occupant is appended once, after the moved candidate and the saved-path observation.");
        Check.Require(f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) == f.Saved &&
            await f.For(Other(side)).ReadOneAsync("New", default) == occupant &&
            await f.For(Other(side)).ReadOneAsync("Old", default) is not null,
            "A refused rename preserves the link, counterpart and occupant.");
        Check.Require(f.Events.Single().Kind == RunEventKind.Issue && f.Admission.Admitted == 1 &&
            f.Admission.Completed == (side == Side.Local ? 1 : 0),
            "A refused move emits no effect and completes its admission.");
    }

    private static async Task MappedHeldFootprintAsync()
    {
        using var f = new Fixture();
        var localParent = await f.Local.EnsureFolderAsync("Mapped_Parent", default);
        var remoteParent = await f.Dropbox.EnsureFolderAsync("Mapped:Parent", default);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(localParent, remoteParent)] });
        var moved = await f.MoveAsync(Side.Local, "Mapped_Parent/New");
        var jobs = SyncJobs.Create(f.Context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
            RefusingExecutionBoundary.Instance, 0));
        var rows = jobs.Items.Rows;
        rows.HoldRenamePaths([(Side.Local, f.Saved.LocalPath)]);
        Check.Require((await jobs.Items.RenameAsync([moved], moved.Moved, default)).Kind == RenameKind.Hold,
            "F10: the admitted rename chooses Hold");
        Check.Require(rows.Covers(PathNames.DropboxKey("Mapped:Parent/New")),
            "F10: a held admitted rename retains its mapped destination footprint too");
    }

    private static async Task HeldRenameEndpointsAsync(bool folder)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var f = new Fixture(folder, mappedParent: true);
            var localParent = await f.Local.EnsureFolderAsync("Mapped_Parent", default);
            var remoteParent = await f.Dropbox.EnsureFolderAsync("Mapped:Parent", default);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with
            { Record = [SyncedEntries.From(localParent, remoteParent)] });
            var moved = await f.MoveAsync(side, side == Side.Local ? "Mapped_Parent/New" : "Mapped:Parent/New");
            var jobs = SyncJobs.Create(f.Context, new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            var rows = jobs.Items.Rows;
            rows.HoldRenamePaths([(Side.Local, f.Saved.LocalPath)]);
            Check.Require((await jobs.Items.RenameAsync([moved], moved.Moved, default)).Kind == RenameKind.Hold,
                "the renamed item is held before effects");
            foreach (var key in new[] { PathNames.Key(f.Saved.LocalPath),
                PathNames.DropboxKey(f.Saved.DropboxPath), PathNames.Key("Mapped_Parent/New"),
                PathNames.DropboxKey("Mapped:Parent/New") })
            {
                Check.Require(rows.Covers(key), "a held rename protects every saved, moved and mapped endpoint");
                if (folder)
                    Check.Require(rows.Covers(key + "/Child/Leaf"),
                        "a held folder rename protects descendants at every endpoint");
                Check.Require(!rows.Covers(key + "Sibling"), "segment siblings remain free");
            }
            foreach (var path in new[] { "a_b", "a_b/Sibling", "a_b/Other/Leaf",
                "Mapped_Parent", "Mapped_Parent/Sibling", "Mapped_Parent/Other/Leaf" })
                Check.Require(!rows.Covers(PathNames.Key(path)),
                    "holding an item does not hold its parent or unrelated rows beneath that parent");
        }
    }

    private static async Task MappedParentAsync(Side side)
    {
        using var f = new Fixture();
        var localParent = await f.Local.EnsureFolderAsync("Mapped_Parent", default);
        var dropboxParent = await f.Dropbox.EnsureFolderAsync("Mapped:Parent", default);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(localParent, dropboxParent)] });
        var moved = await f.MoveAsync(side, side == Side.Local ? "Mapped_Parent/New" : "Mapped:Parent/New");
        var decision = await f.RunAsync([moved]);
        var saved = f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id)!;
        Check.Require(decision.Kind == RenameKind.Follow && saved.LocalPath == "Mapped_Parent/New" &&
            saved.DropboxPath == "Mapped:Parent/New", "Follow uses Parents.Destination rather than copying the source parent spelling.");
        Check.Require(decision.Target == (side == Side.Local ? saved.DropboxPath : saved.LocalPath),
            "The planner's returned target is the mapped destination actually executed");
    }

    private static async Task RebasedChildrenAsync()
    {
        using var f = new Fixture(true);
        var carried = f.SeedChild("Old/Carried");
        var independent = f.SeedChild("Old/Independent");
        var parent = await f.MoveAsync(Side.Local, "New");
        var independentlyMoved = await f.Local.MoveAsync((await f.Local.ReadOneAsync("New/Independent", default))!, "Elsewhere", default);
        var staleChild = new RenameCandidate(Side.Local, independentlyMoved, independent);
        var carriedEntry = (await f.Local.ReadOneAsync("New/Carried", default))!;
        await f.ScanAsync();
        Check.Require((await f.RunAsync([parent])).Kind == RenameKind.Follow, "A folder rename follows first.");
        Check.Require((await f.RunAsync([new RenameCandidate(Side.Local, carriedEntry, carried)])).Kind == RenameKind.None,
            "A carried child candidate is already handled by parent rebasing.");
        await using (var held = await PathLocks.Shared.EnterAsync([PathLocks.DropboxKey(f.Pair, "New/Independent")], default))
        {
            using var cancellation = new CancellationTokenSource();
            var pending = f.RunAsync([staleChild], ct: cancellation.Token);
            Check.Require(!pending.IsCompleted, "A stale child group must acquire its rebased counterpart path before observing it.");
            cancellation.Cancel();
            var cancelled = false;
            try { await pending; }
            catch (OperationCanceledException) { cancelled = true; }
            Check.Require(cancelled && f.Store.SyncedById(f.Pair.PairId, Side.Local, independent.LocalId)?.DropboxPath == "New/Independent",
                "Cancellation during refreshed locking leaves the child mapping unchanged.");
        }
        var admittedBefore = f.Admission.Admitted;
        Check.Require((await f.RunAsync([staleChild])).Kind == RenameKind.Follow &&
            f.Admission.Admitted == admittedBefore + 1,
            "An independent child move refreshes its rebased record within one admission, not split as deleted.");
        var child = f.Store.SyncedById(f.Pair.PairId, Side.Local, independent.LocalId)!;
        Check.Require(child.LocalPath == "Elsewhere" && child.DropboxPath == "Elsewhere" &&
            f.ScanEntry(Side.Dropbox, independent.DropboxId)?.Path == "Elsewhere",
            "Independent child Follow moves the rebased counterpart and its scan row.");

        using var partial = new Fixture();
        var local = await partial.MoveAsync(Side.Local, "ParentMove");
        var dropbox = await partial.MoveAsync(Side.Dropbox, "IndependentMove");
        partial.Store.Commit(partial.Pair.PairId, StateChange.Empty with { Renames = [new RenameApplied(partial.Saved, partial.Saved with
        {
            LocalPath = "ParentMove", LocalKey = PathNames.Key("ParentMove"),
            DropboxPath = "ParentMove", DropboxKey = PathNames.DropboxKey("ParentMove")
        }, null, null, null)] });
        var result = await partial.RunAsync([local, dropbox]);
        Check.Require(result.Kind == RenameKind.Follow && result.MovedSide == Side.Dropbox &&
            partial.Store.SyncedById(partial.Pair.PairId, Side.Local, partial.LocalOld.Id)?.LocalPath == "IndependentMove",
            "Refreshing a group drops only its already-accounted-for candidate.");
    }

    private static async Task RefreshedHintFootprintAsync()
    {
        foreach (var mode in new[] { "ordinary", "admitted", "repeated" })
        {
            using var f = new Fixture(mappedParent: true);
            var moved = await f.MoveAsync(Side.Dropbox, "a:b/New");
            if (mode == "repeated") moved = moved with { Synced = moved.Synced with
                { DropboxPath = "Earlier", DropboxKey = PathNames.DropboxKey("Earlier") } };
            var hint = f.LocalOld with { Path = "HistoricalHint" };
            var reads = 0;
            var context = f.Context with { Local = new ProxySide(f.Local)
                { OnRead = path => { if (path == f.Saved.LocalPath) reads++; } } };
            if (mode == "admitted")
            {
                var lease = await AdmitAsync(f, [moved], hint, Fixture.Generation);
                LiveAdmissionStaleException? refusal = null;
                try { await RunAdmittedAsync(f, lease, Fixture.Generation, [moved], hint, context: context); }
                catch (LiveAdmissionStaleException error) { refusal = error; }
                finally { await lease.DisposeAsync(); }
                Check.Require(refusal?.Reason == LiveAdmissionStaleReason.FootprintChanged && reads == 1 &&
                    f.Events.Count == 0 && f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == f.Saved,
                    "A refreshed saved Local hint requires its mapped Dropbox footprint before admitted effects.");
            }
            else
            {
                var items = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                    RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items;
                var result = await items.RenameAsync([moved], hint, default);
                Check.Require(mode == "repeated"
                    ? result.Kind == RenameKind.Hold && reads == 1 &&
                        f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.FailChanged &&
                        f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == f.Saved
                    : result.Kind == RenameKind.Follow && reads == 2 &&
                        f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId)?.LocalPath == "a_b/New",
                    "A refreshed hint reacquires once; widening after binding reacquisition fails without an effect.");
            }
        }
    }

    private static async Task RenameReacquireBoundAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Local, "New");
        var store = new ChangingRenameStore(f.Store, f.Saved);
        RenameDecision? decision = null;
        try { decision = await f.RunAsync([moved], context: f.Context with { Store = store }); }
        catch (InvalidOperationException) when (store.Reads > 2) { }
        Check.Require(decision?.Kind == RenameKind.Hold && store.Reads == 2 && f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.FailChanged &&
            f.Events.All(item => item.Kind == RunEventKind.Issue),
            "A repeatedly widening rename binding ends after the existing diagnostic retry allowance without provider effects.");
    }

    private static async Task FreshRenameEvidenceAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var failure in new[] { "absent", "replacement", "unreadable" })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(side, "New");
            await f.For(side).MoveAsync(moved.Moved, "Third", default);
            if (failure == "replacement") f.For(side).Seed(moved.Moved with { Id = "replacement" }, Fixture.Content);
            if (failure == "unreadable") f.For(side).FailNext("New", new ItemFailedException("New", TextKey.ReadLocalFailed, null));
            Check.Require((await f.RunAsync([moved])).Kind == RenameKind.Hold &&
                f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) == f.Saved &&
                f.Store.Issues(f.Pair.PairId).Single().Kind == IssueKind.Failure &&
                f.Events.All(item => item.Kind == RunEventKind.Issue),
                "Unknown initiating identity and read failures preserve history and produce an explicit Failure without effects.");
        }
        using var recovery = new Fixture();
        var marker = await recovery.MoveAsync(Side.Dropbox, "OLD" + recovery.Context.Settings.CaseRenameSuffix);
        var hint = (await recovery.MoveAsync(Side.Local, "OLD")).Moved;
        await recovery.Local.MoveAsync(hint, "Old", default);
        var items = SyncJobs.Create(recovery.Context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items;
        Check.Require((await items.RenameAsync([marker], hint, default)).Kind == RenameKind.FinishCaseRename &&
            recovery.Store.SyncedById(recovery.Pair.PairId, Side.Local, hint.Id)?.DropboxPath == "Old",
            "A stale recovery hint cannot override the current saved-endpoint observation.");
    }

    private static async Task StaleRenameReversalsAsync()
    {
        var failures = new List<string>();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var scenario in new[] { "file-case", "folder-case", "move-out" })
        {
            try
            {
                using var f = new Fixture(folder: scenario == "folder-case", realStore: true);
                var child = scenario == "folder-case" ? f.SeedChild("Old/Child") : null;
                var local = f.LocalOld;
                var dropbox = f.DropboxOld;
                var before = f.Saved;
                if (scenario == "move-out")
                {
                    await f.Local.EnsureFolderAsync("Parent", default);
                    await f.Dropbox.EnsureFolderAsync("Parent", default);
                    local = await f.Local.MoveAsync(local, "Parent/Old", default);
                    dropbox = await f.Dropbox.MoveAsync(dropbox, "Parent/Old", default);
                    before = SyncedEntries.Relink(before, local, dropbox);
                    f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Renames = [new RenameApplied(f.Saved, before, null, null, null)] });
                }
                var target = scenario == "move-out" ? "Old" : "OLD";
                await f.ScanAsync();
                var historical = side == Side.Local ? local : dropbox;
                var moved = new RenameCandidate(side, await f.For(side).MoveAsync(historical, target, default), before);
                Check.Require((await f.RunAsync([moved])).Kind == RenameKind.Follow,
                    "The current rename completes before stale Repair work is executed.");
                var saved = f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id)!;
                var stale = new RenameCandidate(side, historical, saved);
                var effects = f.Events.Count;
                Check.Require((await f.RunAsync([stale])).Kind == RenameKind.None && f.Events.Count == effects &&
                    f.Store.SyncedById(f.Pair.PairId, side, moved.Moved.Id) == saved &&
                    (await f.Local.ReadOneAsync(target, default))?.Path == target &&
                    (await f.Dropbox.ReadOneAsync(target, default))?.Path == target,
                    "Stale Repair must not reverse a completed file case, folder case, or move-out rename.");
                if (child is not null)
                {
                    var rebased = f.Store.SyncedById(f.Pair.PairId, side, child.IdOn(side))!;
                    var oldChild = historical with { Path = "Old/Child", IsFolder = false, Id = child.IdOn(side) };
                    var reads = new List<string>();
                    var context = f.Context with
                    {
                        Local = new ProxySide(f.Local) { OnRead = path => reads.Add(path) },
                        Dropbox = new ProxySide(f.Dropbox) { OnRead = path => reads.Add(path) }
                    };
                    Check.Require((await f.RunAsync([new RenameCandidate(side, oldChild, rebased)], context: context)).Kind == RenameKind.None &&
                        f.Events.Count == effects && f.Store.SyncedById(f.Pair.PairId, side, child.IdOn(side)) == rebased &&
                        !reads.Contains("Old/Child"),
                        "Case-equivalent children use saved-parent spelling, never the historical requested parent.");
                }
            }
            catch (Exception error) { failures.Add($"{side}/{scenario}: {error.Message}"); }
        }
        Check.Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static async Task SavedSpellingDriftAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var folder in new[] { false, true })
        foreach (var completedScan in new[] { false, true })
        {
            using var f = new Fixture(folder, realStore: true);
            var drifted = side == Side.Local ? f.Saved with { LocalPath = "old" }
                : f.Saved with { DropboxPath = "old" };
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [drifted] });
            var child = folder ? f.SeedChild("Old/Child") : null;
            await f.ScanAsync();
            var candidates = f.Store.ScanRenames(f.Pair.PairId);
            Check.Require(candidates.Count == 1 && candidates[0].Side == side,
                "Exact scan spelling still presents drift for identity-checked adoption.");
            var reads = 0;
            var context = f.Context with
            {
                Local = new ProxySide(f.Local) { OnRead = _ => reads++ },
                Dropbox = new ProxySide(f.Dropbox) { OnRead = _ => reads++ }
            };
            var syncer = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items;
            var decision = await syncer.RenameAsync(candidates, f.LocalOld, default, completedScan);
            Check.Require(reads == (completedScan ? 0 : 2),
                "Only this Repair's identity-matched adoption reuses listing evidence without provider reads.");
            var saved = f.Store.SyncedById(f.Pair.PairId, side, drifted.IdOn(side))!;
            Check.Require(decision.Kind == RenameKind.Adopt && saved == f.Saved && f.Events.Count == 0 &&
                f.Store.Issues(f.Pair.PairId).Count == 0 && f.Admission.Completed == 0,
                "Saved case drift refreshes only the binding, with no provider move, clash or completion.");
            f.CheckStamps(saved);
            Check.Require(!syncer.Rows.Covers(PathNames.Key("Old/Child")) &&
                (child is null || f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId) == child),
                "Adopted ancestor drift leaves descendants unchanged and does not create a run-local Tie hold.");
            Check.Require(f.Store.ScanRenames(f.Pair.PairId).Count == 0,
                "Refreshed spelling does not return as rename debt in the next scan query.");
        }
    }

    private static async Task ListedAdoptionFallbackAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var scenario in new[] { "adopt", "identity", "effect" })
        {
            using var f = new Fixture(realStore: true);
            var drifted = side == Side.Local ? f.Saved with { LocalPath = "old" }
                : f.Saved with { DropboxPath = "old" };
            if (scenario == "effect") await f.MoveAsync(side, "New");
            else f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [drifted] });
            var other = Other(side);
            var counterpart = other == Side.Local ? f.LocalOld : f.DropboxOld;
            if (scenario == "identity") f.For(other).Seed(counterpart with { Id = "replacement" }, Fixture.Content);
            await f.ScanAsync();
            var candidates = f.Store.ScanRenames(f.Pair.PairId);
            if (scenario == "identity") f.For(other).Seed(counterpart, Fixture.Content);
            var reads = 0;
            var context = f.Context with
            {
                ScanId = ScanFixture.Token(f.Store, f.Pair.PairId),
                Local = new ProxySide(f.Local) { OnRead = _ => reads++ },
                Dropbox = new ProxySide(f.Dropbox) { OnRead = _ => reads++ }
            };
            var items = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items;
            var decision = await items.RenameAsync(candidates, null, default, completedScan: true);
            Check.Require(scenario == "adopt" ? reads == 0 && decision.Kind == RenameKind.Adopt : reads >= 2,
                "Repair adoption skips reads only with matching listed identities; effects and mismatches reobserve both sides.");
            Check.Require(decision.Kind == (scenario == "effect" ? RenameKind.Follow : RenameKind.Adopt),
                "Fresh fallback preserves the ordinary rename decision.");
        }
    }

    private static async Task RecordedRenameDestinationAsync()
    {
        foreach (var different in new[] { false, true })
        foreach (var destinationFirst in new[] { false, true })
        foreach (var completedScan in new[] { false, true })
        foreach (var realStore in new[] { false, true })
        foreach (var liveOld in new[] { "absent", "hardlink", "local-replacement", "cloud-replacement" })
        {
            using var f = new Fixture(realStore: realStore);
            await f.Local.EnsureFolderAsync("Archive", default);
            await f.Dropbox.EnsureFolderAsync("Archive", default);
            var local = await f.MoveAsync(Side.Local, "Archive/Item");
            var dropbox = await f.MoveAsync(Side.Dropbox, "Archive/Item");
            var destination = SyncedEntries.Relink(f.Saved, local.Moved, dropbox.Moved);
            if (different) destination = destination with { LocalId = "other-local", DropboxId = "other-dropbox" };
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [destination] });
            if (destinationFirst)
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [f.Saved] });
            if (liveOld == "hardlink") f.Local.Seed(f.LocalOld, Fixture.Content);
            if (liveOld == "local-replacement") f.Local.Seed(f.LocalOld with { Id = "replacement-local" }, Fixture.Content);
            if (liveOld == "cloud-replacement") f.Dropbox.Seed(f.DropboxOld with { Id = "replacement-cloud" }, Fixture.Content);
            await f.ScanAsync();
            IReadOnlyList<RenameCandidate> candidates = f.Store.ScanRenames(f.Pair.PairId)
                .Where(candidate => candidate.Synced == f.Saved).ToArray();
            Check.Require(candidates.Count == 2, "Repair discovers both moved endpoints of the stale saved row.");
            void RequireAdmission(string _) => Check.Require(f.Admission.Admitted > 0,
                "Duplicate-destination observation and retirement require provider admission before every read.");
            var context = f.Context with
            {
                Local = new ProxySide(f.Local) { OnRead = RequireAdmission },
                Dropbox = new ProxySide(f.Dropbox) { OnRead = RequireAdmission }
            };
            var decision = await f.RunAsync(candidates, context: context, completedScan: completedScan);
            Check.Require(f.Store.SyncedByKey(f.Pair.PairId, Side.Local, destination.LocalKey) == destination,
                "An occupied rename destination retains its saved identities and content baseline.");
            if (liveOld == "hardlink")
                Check.Require(decision.Kind == RenameKind.Leave &&
                    f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) == f.Saved &&
                    f.Store.Issues(f.Pair.PairId).Count == 0,
                    "A surviving alias leaves both bindings for ordinary path processing without a rename clash.");
            else if (different || liveOld != "absent")
                Check.Require(decision.Kind == RenameKind.Hold &&
                    f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) == f.Saved &&
                    f.Store.Issues(f.Pair.PairId).Single() is { Kind: IssueKind.NameClash, Cause: TextKey.ClashNameTaken },
                    "A different saved destination produces NameClash without deleting either binding or throwing.");
            else
                Check.Require(decision.Kind == RenameKind.Adopt &&
                    f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) is null &&
                    f.Store.ScanRenames(f.Pair.PairId).Count == 0 && f.Store.Issues(f.Pair.PairId).Count == 0,
                    "Repair self-heals the pre-existing duplicate by retaining the destination and retiring the source.");
            Check.Require(f.Events.All(item => item.Kind == RunEventKind.Issue),
                "Settling saved rename collisions performs no provider move.");
        }
    }

    private static async Task DuplicateFolderDestinationAsync()
    {
        foreach (var realStore in new[] { false, true })
        foreach (var differentChild in new[] { false, true })
        foreach (var liveOld in new Side?[] { null, Side.Local, Side.Dropbox })
        {
            using var f = new Fixture(folder: true, realStore: realStore);
            var child = f.SeedChild("Old/Child");
            var local = await f.MoveAsync(Side.Local, "New");
            var remote = await f.MoveAsync(Side.Dropbox, "New");
            var destination = SyncedEntries.Relink(f.Saved, local.Moved, remote.Moved);
            var destinationChild = SyncedEntries.Relink(child,
                (await f.Local.ReadOneAsync("New/Child", default))!,
                (await f.Dropbox.ReadOneAsync("New/Child", default))!);
            if (differentChild) destinationChild = destinationChild with { LocalId = "other-local", DropboxId = "other-cloud" };
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [destination, destinationChild] });
            PathOccupiedException? clash = null;
            try
            {
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                { Renames = [new RenameApplied(f.Saved, destination, null, null, null)] });
            }
            catch (PathOccupiedException error) { clash = error; }
            Check.Require(clash is { CollidingSide: Side.Local, Path: "New" },
                "An occupied folder root refuses before any descendant bulk rebase, with a typed clash.");
            foreach (var row in new[] { f.Saved, child, destination, destinationChild })
                Check.Require(f.Store.SyncedByKey(f.Pair.PairId, Side.Local, row.LocalKey) == row,
                    "Folder refusal preserves every root and child binding, including different destination children.");
            if (liveOld is { } side) f.For(side).Seed(side == Side.Local ? f.LocalOld : f.DropboxOld, null);
            var decision = await f.RunAsync([local, remote]);
            Check.Require(decision.Kind == (liveOld is null ? RenameKind.Adopt : RenameKind.Hold) &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) == (liveOld is null ? null : f.Saved),
                "Folder Adopt retires an exact absent old root, but preserves a root with either live old endpoint.");
            foreach (var row in new[] { child, destination, destinationChild })
                Check.Require(f.Store.SyncedByKey(f.Pair.PairId, Side.Local, row.LocalKey) == row,
                    "Root-only retirement never rebases or deletes any child or replaces the destination baseline.");
            Check.Require(liveOld is null ? f.Store.Issues(f.Pair.PairId).Count == 0 :
                f.Store.Issues(f.Pair.PairId).All(issue => issue.Kind == IssueKind.NameClash),
                "Successful root retirement closes its old clash; a live endpoint retains the controlled clash.");
            if (liveOld is { } retainedSide)
            {
                await f.For(retainedSide).DeleteAsync(retainedSide == Side.Local ? f.LocalOld : f.DropboxOld, null, default);
                await f.ScanAsync();
                var issue = f.Store.Issues(f.Pair.PairId).Single();
                Check.Require(IssuePolicy.Obsolete(issue,
                    (side, path) => f.Store.ScanEntry(f.Pair.PairId, side, PathNames.KeyFor(side, path)),
                    new PrefixSet(), new PrefixSet(), new PrefixSet(), new PrefixSet(), f.Context.Scope, false),
                    "After the owner removes the live old endpoint, Repair's existing reconciliation obsoletes its clash.");
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Close = [(issue.Path, new[] { issue.Kind })] });
                Check.Require((await f.RunAsync([local, remote])).Kind == RenameKind.Adopt &&
                    f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) is null,
                    "After normal clash reconciliation, the now-absent source root retires without a cleanup queue.");
            }
            Check.Require((await f.RunAsync([local, remote])).Kind == RenameKind.None,
                "A retired folder root cannot recur as an unresolved rename on the next Repair.");
        }
    }

    private static async Task MultipleDestinationOwnersAsync()
    {
        foreach (var realStore in new[] { false, true })
        foreach (var sameIdentity in new[] { false, true })
        foreach (var folder in new[] { false, true })
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var f = new Fixture(folder: folder, realStore: realStore);
            var child = folder ? f.SeedChild("Old/Child") : null;
            var moved = await f.MoveAsync(side, "New");
            var localOwner = SyncedEntries.From(f.LocalOld with { Path = "New", Id = "other-local" },
                f.DropboxOld with { Path = "ElsewhereCloud", Id = "other-cloud" });
            var cloudOwner = SyncedEntries.From(f.LocalOld with
                { Path = "ElsewhereLocal", Id = sameIdentity ? localOwner.LocalId : "second-local" },
                f.DropboxOld with { Path = "New", Id = "second-cloud" });
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [localOwner, cloudOwner] });
            var result = await f.RunAsync([moved]);
            Check.Require(result.Kind == RenameKind.Hold &&
                await f.For(Other(side)).ReadOneAsync("Old", default) == (side == Side.Local ? f.DropboxOld : f.LocalOld) &&
                await f.For(Other(side)).ReadOneAsync("New", default) is null &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) == f.Saved &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, localOwner.LocalKey) == localOwner &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, cloudOwner.LocalKey) == cloudOwner &&
                f.Store.Issues(f.Pair.PairId).Single() is { Kind: IssueKind.NameClash, Cause: TextKey.ClashNameTaken } &&
                f.Events.All(item => item.Kind != RunEventKind.Effect),
                "Multiple saved destination owners refuse before a provider move, even when the physical target is empty.");
            if (child is not null)
                Check.Require(f.Store.SyncedByKey(f.Pair.PairId, Side.Local, child.LocalKey) == child &&
                    await f.For(Other(side)).ReadOneAsync("Old/Child", default) is { } retained &&
                    retained.Id == child.IdOn(Other(side)) &&
                    await f.For(Other(side)).ReadOneAsync("New/Child", default) is null,
                    "A blocked folder move preserves its descendant binding and counterpart path.");
        }
    }

    private static async Task SelectedDestinationIdentityAsync()
    {
        foreach (var realStore in new[] { false, true })
        foreach (var (identity, outside) in new[]
        {
            (DestinationIdentity.Here, false), (DestinationIdentity.Moving, false),
            (DestinationIdentity.Gone, false), (DestinationIdentity.Gone, true)
        })
        {
            using var f = new Fixture(realStore: realStore);
            var moved = await f.MoveAsync(Side.Local, "New");
            var ownerLocal = f.LocalOld with { Path = "New", Id = "owner-local" };
            var ownerDropbox = f.DropboxOld with { Path = "New", Id = "owner-cloud" };
            var owner = SyncedEntries.From(ownerLocal, ownerDropbox);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [owner] });
            f.Dropbox.Seed(ownerDropbox, Fixture.Content);
            var source = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
            var acquired = f.Store.AcquireJournalPage("1", new JournalPosition(1, 1), new JournalPosition(1, 2),
                [(f.Pair.PairId, Fixture.Generation, source.Epoch, source.Revision, false)],
                [(f.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Renamed, "New", "Old", moved.Moved.Id,
                    moved.Moved, false, false))])!.Value;
            var selection = new WorkSelection(f.Pair.PairId, Fixture.Generation, source.Epoch, Side.Local,
                "1", moved.Moved.Id, PathNames.Key("New"), acquired, acquired);
            Check.Require(f.Store.BeginSelection(selection), "The focused rename owns acquired work.");
            var reads = 0;
            var local = new IdentitySide(f.Local, (id, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Check.Require(id == owner.LocalId, "Identity lookup targets the single destination owner, not the incoming item.");
                reads++;
                return Task.FromResult<(IdentityLocation Location, bool InScope)?>(identity == DestinationIdentity.Gone && !outside
                    ? null : (new IdentityLocation("1", "native", ownerLocal with
                        { Path = identity == DestinationIdentity.Here ? "New" : "Elsewhere" }), !outside));
            });
            var context = f.Context with { Local = local };
            var items = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items;
            await using var lease = await AdmitAsync(f, [moved], moved.Moved, Fixture.Generation);
            var result = await items.RenameAdmittedAsync(lease, selection, context.Opportunity, [moved], moved.Moved, default);
            Check.Require(reads == 1 && (identity == DestinationIdentity.Here
                ? result.Kind == RenameKind.Hold && f.Store.Issues(f.Pair.PairId).Single().Kind == IssueKind.NameClash
                : result.Kind == (identity == DestinationIdentity.Gone ? RenameKind.Vacate : RenameKind.Hold) &&
                    result.Destination is { } destination && destination.Identity == identity && destination.Binding == owner),
                "Selected single-owner rename classifies Here, Moving and Gone through its public execution path.");
            Check.Require(await f.Dropbox.ReadOneAsync("Old", default) == f.DropboxOld &&
                await f.Dropbox.ReadOneAsync("New", default) == ownerDropbox &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) == f.Saved &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, owner.LocalKey) == owner &&
                f.Events.All(item => item.Kind != RunEventKind.Effect),
                "Classification and an occupied Here target preserve both bindings and provider entries without effects.");
            f.Store.EndSelection(selection);
        }
    }

    private static async Task SavedCollisionSidesAsync()
    {
        foreach (var realStore in new[] { false, true })
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var f = new Fixture(realStore: realStore);
            var after = SyncedEntries.Relink(f.Saved, f.LocalOld with { Path = "NewLocal" },
                f.DropboxOld with { Path = "NewCloud" });
            var occupant = SyncedEntries.From(f.LocalOld with
                { Path = side == Side.Local ? "NewLocal" : "OtherLocal", Id = "other-local" },
                f.DropboxOld with { Path = side == Side.Dropbox ? "NewCloud" : "OtherCloud", Id = "other-cloud" });
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [occupant] });
            PathOccupiedException? clash = null;
            try
            {
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                { Renames = [new RenameApplied(f.Saved, after, null, null, null)] });
            }
            catch (PathOccupiedException error) { clash = error; }
            Check.Require(clash?.CollidingSide == side && clash.Path == after.PathOn(side) &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, f.Saved.LocalKey) == f.Saved &&
                f.Store.SyncedByKey(f.Pair.PairId, Side.Local, occupant.LocalKey) == occupant,
                "Each separately occupied key reports its actual provider/path without changing either saved record.");

            using var adopt = new Fixture(realStore: realStore);
            var local = await adopt.MoveAsync(Side.Local, "New");
            var remote = await adopt.MoveAsync(Side.Dropbox, "NEW");
            var taken = SyncedEntries.From(adopt.LocalOld with
                { Path = side == Side.Local ? "New" : "OtherLocal", Id = "other-local" },
                adopt.DropboxOld with { Path = side == Side.Dropbox ? "NEW" : "OtherCloud", Id = "other-cloud" });
            adopt.Store.Commit(adopt.Pair.PairId, StateChange.Empty with { Record = [taken] });
            var unrelated = PathNames.Other(side);
            var unrelatedReads = 0;
            var unrelatedView = new ProxySide(adopt.For(unrelated))
            {
                ReadFault = path => PathNames.Same(path, "New") && ++unrelatedReads > 1
                    ? new ItemFailedException("New", TextKey.DropboxRefused, null) : null
            };
            var context = unrelated == Side.Local ? adopt.Context with { Local = unrelatedView }
                : adopt.Context with { Dropbox = unrelatedView };
            var decision = await adopt.RunAsync([local, remote], context: context);
            Check.Require(decision.Kind == RenameKind.Hold && unrelatedReads == 1 && adopt.Store.Issues(adopt.Pair.PairId).Single() is
                { Kind: IssueKind.NameClash, Cause: TextKey.ClashNameTaken },
                "Adopt probes the actual collision provider, not the unrelated endpoint that fails on read.");

            using var actionCase = new Fixture(realStore: realStore);
            var movedPath = side == Side.Local ? "NewLocal" : "NewCloud";
            var target = side == Side.Local ? "NewCloud" : "NewLocal";
            var moved = await actionCase.MoveAsync(side, movedPath);
            var actionOccupant = SyncedEntries.From(actionCase.LocalOld with
                { Path = side == Side.Local ? movedPath : "OtherLocal", Id = "occupied-local" },
                actionCase.DropboxOld with { Path = side == Side.Dropbox ? movedPath : "OtherCloud", Id = "occupied-cloud" });
            actionCase.Store.Commit(actionCase.Pair.PairId, StateChange.Empty with { Record = [actionOccupant] });
            actionCase.For(PathNames.Other(side)).FailNext(movedPath,
                new ItemFailedException(movedPath, TextKey.DropboxRefused, null));
            var transfers = new Transfers(actionCase.Context);
            var executor = new ActionExecutor(actionCase.Context, transfers, new Parents(actionCase.Context),
                new KeepBothRunner(actionCase.Context, transfers));
            var row = new Row(PathNames.Key(movedPath), side == Side.Local ? moved.Moved : actionCase.LocalOld,
                side == Side.Dropbox ? moved.Moved : actionCase.DropboxOld, actionCase.Saved, [], null, RowContext.Neutral);
            var action = new Massdex.SyncShark.Engine.Planner.SyncAction(side == Side.Local ? ActionKind.MoveDropbox : ActionKind.MoveLocal, row, Target: target);
            await using (await PathLocks.Shared.EnterAsync(LockKeys.For(action, actionCase.Pair, null), default))
                await executor.ExecuteAsync(action, null, default);
            var actionIssue = actionCase.Store.Issues(actionCase.Pair.PairId).Single();
            Check.Require(actionIssue is { Kind: IssueKind.NameClash, Cause: TextKey.ClashNameTaken } &&
                actionIssue.Subject.Incoming == side &&
                actionIssue.Subject.Items.Any(item => item.Side == side && item.Path == movedPath && item.Id == moved.Moved.Id),
                "MoveLocal/MoveDropbox probe the colliding provider while retaining the action's incoming side.");
        }
    }

    private static async Task DropboxFolderCaseAsync()
    {
        using var f = new Fixture(true);
        var child = f.SeedChild("Old/f.txt");
        await f.Dropbox.MoveAsync(f.DropboxOld, "old", default);
        await f.ScanAsync();
        var candidates = f.Store.ScanRenames(f.Pair.PairId);
        Check.Require(candidates.Count == 2 && candidates[0].Side == Side.Dropbox && candidates[0].Moved.Path == "old" &&
            candidates[1].Side == Side.Dropbox && candidates[1].Moved.Path == "old/f.txt",
            "A Dropbox folder case rename lists the folder and its carried child as candidates.");
        var decision = await f.RunAsync([candidates[0]]);
        var folder = f.Store.SyncedById(f.Pair.PairId, Side.Dropbox, f.DropboxOld.Id)!;
        var rebased = f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId)!;
        Check.Require(decision.Kind == RenameKind.Follow && decision.MovedSide == Side.Dropbox &&
            folder.LocalPath == "old" && folder.DropboxPath == "old" &&
            rebased.LocalPath == "old/f.txt" && rebased.DropboxPath == "old/f.txt",
            "The Dropbox folder case rename follows to one local folder move and rebases the child on both sides.");
        Check.Require(f.Events.Single() is { Kind: RunEventKind.Effect, Path: "old", Detail: "Old", Side: Side.Local } &&
            await f.Local.ReadOneAsync("old", default) is { IsFolder: true, Path: "old" } &&
            await f.Local.ReadOneAsync("Old/f.txt", default) is { Path: "old/f.txt" },
            "Exactly one local folder move carries the child, with no separate child effect.");
        var carried = await f.RunAsync([candidates[1]]);
        Check.Require(carried.Kind == RenameKind.None && f.Events.Count == 1 &&
            f.Store.SyncedById(f.Pair.PairId, Side.Local, child.LocalId) == rebased,
            "The carried child candidate is already handled by the folder move.");
    }

    private static async Task FailuresAsync()
    {
        foreach (var read in new[] { false, true })
        foreach (var changed in new[] { false, true })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Local, "New");
            Exception error = changed ? new ChangedUnderneathException("Old") : new ItemFailedException("Old", TextKey.DropboxRefused, null);
            f.Dropbox.FailNext(read ? "Old" : "New", error);
            var decision = await f.RunAsync([moved]);
            var issue = f.Store.Issues(f.Pair.PairId).Single();
            Check.Require(decision.Kind == RenameKind.Hold && issue.Kind == IssueKind.Failure &&
                issue.Cause == (changed ? TextKey.FailChanged : TextKey.DropboxRefused) &&
                f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved,
                "Read and move item failures keep their cause and hold without forgetting the link.");
            Check.Require(await f.Dropbox.ReadOneAsync("Old", default) is not null &&
                f.Events.All(e => e.Kind == RunEventKind.Issue) && f.Admission.Admitted == 1 &&
                f.Admission.Completed == (read ? 0 : 1),
                "A failed rename keeps the counterpart and releases admission without an effect.");
        }
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Dropbox, "New");
            f.Local.Seed(f.LocalOld with { Modified = Fixture.Time.AddSeconds(1) }, null);
            var decision = await f.RunAsync([moved]);
            Check.Require(decision.Kind == RenameKind.Hold && f.Store.Issues(f.Pair.PairId).Single().Cause == TextKey.FailChanged &&
                f.Store.SyncedById(f.Pair.PairId, Side.Local, f.LocalOld.Id) == f.Saved &&
                f.Admission.Admitted == 1 && f.Admission.Completed == 0,
                "A failed local hash holds the rename without changing its mapping or beginning a move.");
        }
        foreach (var cancelled in new[] { false, true })
        {
            using var f = new Fixture();
            var moved = await f.MoveAsync(Side.Local, "New");
            Exception error = cancelled ? new OperationCanceledException() : new InvalidOperationException("Unexpected failure");
            f.Dropbox.FailNext("New", error);
            Exception? caught = null;
            try { await f.RunAsync([moved]); }
            catch (Exception actual) { caught = actual; }
            Check.Require(ReferenceEquals(caught, error) && f.Events.Count == 0 && f.Store.Issues(f.Pair.PairId).Count == 0 &&
                f.Admission.Admitted == 1 && f.Admission.Completed == 1,
                "Cancellation and unexpected errors propagate without invented issues.");
        }
    }

    private static void KeySets()
    {
        var pair = new PairConfig(nameof(RenameRunnerChecks), nameof(RenameRunnerChecks),
            Path.GetTempPath(), "/Pair", "Account", [], [], FirstRunPolicy.NewestWins, true, null);
        var keys = LockKeys.ForPaths(pair,
        [
            (Side.Local, "Old"), (Side.Dropbox, "Old"),
            (Side.Local, "OLD"), (Side.Dropbox, "Old"),
            (Side.Local, "New"), (Side.Dropbox, "New")
        ]);
        Check.Require(keys.Count == 4 &&
            new[]
            {
                PathLocks.LocalKey(pair, "Old"), PathLocks.DropboxKey(pair, "Old"),
                PathLocks.LocalKey(pair, "New"), PathLocks.DropboxKey(pair, "New")
            }.All(key => keys.Contains(key, PathLocks.KeyComparer)),
            "ForPaths folds case variants and a repeated direct footprint into the exact distinct key set.");
    }

    private static async Task KeySetsAsync()
    {
        KeySets();
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Local, "New");
            var lease = await AdmitAsync(f, [moved], moved.Moved, Fixture.Generation);
            RenameDecision result;
            try { result = await RunAdmittedAsync(f, lease, Fixture.Generation, [moved], moved.Moved); }
            finally { await lease.DisposeAsync(); }
            Check.Require(result.Kind == RenameKind.Follow && lease.RequireCalls == 1 &&
                ExactKeys(f.Pair, lease.RequiredKeys[0], "Old", "New"),
                "An unchanged admitted rename derives the exact all-distinct key set once and reuses it.");
        }
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Local, "OLD");
            var lease = await AdmitAsync(f, [moved], moved.Moved, Fixture.Generation);
            RenameDecision result;
            try { result = await RunAdmittedAsync(f, lease, Fixture.Generation, [moved], moved.Moved); }
            finally { await lease.DisposeAsync(); }
            Check.Require(result.Kind == RenameKind.Follow && lease.RequireCalls == 1 &&
                ExactKeys(f.Pair, lease.RequiredKeys[0], "Old"),
                "A case-variant admitted rename derives one key per side and reuses that list.");
        }
    }

    private static async Task ItemAttemptAuthorityAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Local, "New");
        Check.Require((await f.RunAsync([moved])).Kind == RenameKind.Follow && f.ItemAttempts.Uses == 0,
            "An ordinary rename success never consults the bound item-attempt authority.");
        using var failed = new Fixture();
        var failure = await failed.MoveAsync(Side.Local, "New");
        failed.Dropbox.FailNext("New", new ItemFailedException("New", TextKey.DropboxRefused, null));
        Check.Require((await failed.RunAsync([failure])).Kind == RenameKind.Hold && failed.ItemAttempts.Uses == 0,
            "A handled rename failure raises its ordinary issue without a second item-attempt route.");
    }

    private static async Task AdmittedEffectAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Local, "New");
        const long captured = Fixture.Generation + 6;
        f.AdvanceGeneration(captured);
        var lease = await AdmitAsync(f, [moved], moved.Moved, captured);
        using var cancellation = new CancellationTokenSource();
        RenameDecision? result = null;
        var synchronous = false;
        try
        {
            var pending = RunAdmittedAsync(f, lease, captured, [moved], moved.Moved, ct: cancellation.Token);
            synchronous = pending.IsCompleted;
            if (!synchronous) cancellation.Cancel();
            try { result = await pending; }
            catch (OperationCanceledException) { }
            Check.Require(synchronous && result?.Kind == RenameKind.Follow,
                "A borrowed lease reaches its one rename effect without a nested admission or path hold.");
            Check.Require(f.Admission.Admitted == 1 && f.Admission.Completed == 1 && lease.RequireCalls == 1 &&
                lease.DisposeCount == 0,
                "The outer permit is the only admission; the Dropbox effect completes it and the runner keeps the lease.");
            Check.Require(f.Store.SyncedById(f.Pair.PairId, Side.Local, moved.Moved.Id) is
                { LocalPath: "New", DropboxPath: "New" },
                "The admitted rename links both new paths from the captured generation, not the context's.");
        }
        finally { await lease.DisposeAsync(); }
        Check.Require(lease.DisposeCount == 1, "The caller releases the borrowed lease exactly once.");
    }

    private static async Task AdmittedRebaseAsync()
    {
        using (var inside = new Fixture())
        {
            var local = await inside.MoveAsync(Side.Local, "ParentMove");
            var dropbox = await inside.MoveAsync(Side.Dropbox, "IndependentMove");
            RebaseSaved(inside, "ParentMove", "ParentMove");
            var lease = await AdmitAsync(inside, [local, dropbox], local.Moved, Fixture.Generation);
            RenameDecision result;
            try { result = await RunAdmittedAsync(inside, lease, Fixture.Generation, [local, dropbox], local.Moved); }
            finally { await lease.DisposeAsync(); }
            var saved = inside.Store.SyncedById(inside.Pair.PairId, Side.Local, local.Synced.LocalId);
            Check.Require(result.Kind == RenameKind.Follow && result.MovedSide == Side.Dropbox &&
                lease.RequireCalls == 2 && saved is
                { LocalPath: "IndependentMove", DropboxPath: "IndependentMove" },
                "A rebase inside the admitted footprint revalidates the new keys and executes without widening them.");
        }
        using (var outside = new Fixture())
        {
            var local = await outside.MoveAsync(Side.Local, "ParentMove");
            var dropbox = await outside.MoveAsync(Side.Dropbox, "IndependentMove");
            RebaseSaved(outside, "ParentMove", "Other");
            var lease = await AdmitAsync(outside, [local, dropbox], local.Moved, Fixture.Generation);
            LiveAdmissionStaleException? caught = null;
            try { await RunAdmittedAsync(outside, lease, Fixture.Generation, [local, dropbox], local.Moved); }
            catch (LiveAdmissionStaleException error) { caught = error; }
            finally { await lease.DisposeAsync(); }
            var saved = outside.Store.SyncedById(outside.Pair.PairId, Side.Local, local.Synced.LocalId);
            var counterpart = await outside.Dropbox.ReadOneAsync("IndependentMove", default);
            Check.Require(caught?.Reason == LiveAdmissionStaleReason.FootprintChanged &&
                outside.Events.Count == 0 && outside.Store.Issues(outside.Pair.PairId).Count == 0 &&
                saved is { DropboxPath: "Other" } && counterpart is not null &&
                outside.Admission.Completed == 0 && lease.DisposeCount == 1,
                "A rebase outside the borrowed footprint refuses before an effect, leaves no issue and releases once.");
        }
    }

    private static async Task AdmittedStaleAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Local, "New");
        const long captured = Fixture.Generation + 3;
        var lease = await AdmitAsync(f, [moved], moved.Moved, captured + 1);
        LiveAdmissionStaleException? stale = null;
        try { await RunAdmittedAsync(f, lease, captured, [moved], moved.Moved); }
        catch (LiveAdmissionStaleException error) { stale = error; }
        finally { await lease.DisposeAsync(); }
        Check.Require(stale?.Reason == LiveAdmissionStaleReason.SelectionChanged && f.Events.Count == 0 &&
            f.Store.Issues(f.Pair.PairId).Count == 0 &&
            f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == f.Saved &&
            f.Admission.Completed == 0 && lease.DisposeCount == 1,
            "A stale captured generation refuses before an effect and leaves the binding unchanged.");
        const string otherPair = "OtherPair";
        var foreign = await AdmitAsync(f, [moved], moved.Moved, captured, pairId: otherPair);
        LiveAdmissionStaleException? foreignStale = null;
        try { await RunAdmittedAsync(f, foreign, captured, [moved], moved.Moved); }
        catch (LiveAdmissionStaleException error) { foreignStale = error; }
        finally { await foreign.DisposeAsync(); }
        Check.Require(foreignStale?.Reason == LiveAdmissionStaleReason.SelectionChanged && f.Events.Count == 0 &&
            f.Store.SyncedById(f.Pair.PairId, Side.Local, f.Saved.LocalId) == f.Saved,
            "A lease for another pair lends this endpoint no authority and refuses before an effect.");
    }

    private static async Task AdmittedCancellationAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Dropbox, "New");
        var lease = await AdmitAsync(f, [moved], null, Fixture.Generation);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Exception? caught = null;
        try { await RunAdmittedAsync(f, lease, Fixture.Generation, [moved], null, ct: cancelled.Token); }
        catch (OperationCanceledException error) { caught = error; }
        finally { await lease.DisposeAsync(); }
        var counterpart = await f.Local.ReadOneAsync("Old", default);
        Check.Require(caught is not null && f.Events.Count == 0 && f.Store.Issues(f.Pair.PairId).Count == 0 &&
            counterpart is not null && f.Store.SyncedById(f.Pair.PairId, Side.Dropbox, f.Saved.DropboxId) == f.Saved &&
            f.Admission.Completed == 0 && lease.DisposeCount == 1,
            "Cancellation at observation performs no acknowledgement or effect and releases the lease once.");
    }

    private static async Task CommittedIssueAsync()
    {
        using (var f = new Fixture(true))
        {
            var moved = await f.MoveAsync(Side.Local, "New");
            var nested = f.Pair with
            {
                PairId = "Nested",
                LocalRoot = Path.Combine(f.Pair.LocalRoot, "New", "Inner"),
                DropboxRoot = "/OtherRoot"
            };
            var context = f.Context with { Scope = new PathScope(f.Pair, [f.Pair, nested]) };
            var (result, delivered) = await AdmittedIssuesAsync(f, [moved], moved.Moved, context);
            var stored = f.Store.Issues(f.Pair.PairId).Single();
            Check.Require(result.Kind == RenameKind.Notice && delivered.Count == 1 &&
                delivered[0].Id == stored.Id && delivered[0].Kind == IssueKind.Failure &&
                delivered[0].Cause == TextKey.FailCarriesOtherPair,
                "The notice path forwards the committed Issue, not a reconstructed one.");
        }
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Local, "New");
            await f.Dropbox.DeleteAsync(f.DropboxOld, null, default);
            var (result, delivered) = await AdmittedIssuesAsync(f, [moved], moved.Moved);
            var stored = f.Store.Issues(f.Pair.PairId).Single();
            Check.Require(result.Kind == RenameKind.Split && delivered.Count == 1 && delivered[0].Id == stored.Id &&
                delivered[0].Kind == IssueKind.Info && delivered[0].Cause == TextKey.InfoMovedDeleted,
                "The Split path forwards its committed Info issue once.");
        }
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Local, "New");
            f.Dropbox.FailNext("New", new ItemFailedException("New", TextKey.DropboxRefused, null));
            var (result, delivered) = await AdmittedIssuesAsync(f, [moved], moved.Moved);
            var stored = f.Store.Issues(f.Pair.PairId).Single();
            Check.Require(result.Kind == RenameKind.Hold && delivered.Count == 1 && delivered[0].Id == stored.Id &&
                delivered[0].Kind == IssueKind.Failure && delivered[0].Cause == TextKey.DropboxRefused,
                "The Failure path forwards the classified committed issue once.");
        }
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Local, "New");
            var occupant = f.DropboxOld with { Path = "New", Id = "occupant" };
            f.Dropbox.Seed(occupant, [9, 8, 7]);
            var (result, delivered) = await AdmittedIssuesAsync(f, [moved], moved.Moved);
            var stored = f.Store.Issues(f.Pair.PairId).Single();
            Check.Require(result.Kind == RenameKind.Hold && delivered.Count == 1 && delivered[0].Id == stored.Id &&
                delivered[0].Kind == IssueKind.NameClash && delivered[0].Cause == TextKey.ClashNameTaken &&
                delivered[0].Path == "New" && delivered[0].Subject.Incoming == Side.Local &&
                delivered[0].Subject.Items.Contains(new IssueItem(Side.Dropbox, "New", occupant.Id)),
                "The occupied destination forwards the occupant-inclusive committed issue at the actual target.");
        }
        using (var f = new Fixture())
        {
            var moved = await f.MoveAsync(Side.Local, "New");
            var (result, delivered) = await AdmittedIssuesAsync(f, [moved], moved.Moved);
            Check.Require(result.Kind == RenameKind.Follow && delivered.Count == 0 &&
                f.Store.Issues(f.Pair.PairId).Count == 0,
                "A committed success invokes no unfinished-issue callback.");
        }
    }

    private static async Task IgnoredNameAsync()
    {
        using var f = new Fixture();
        var moved = await f.MoveAsync(Side.Local, "New");
        var pair = f.Pair with { Exclusions = [new Exclusion(Side.Local, "Old", true)] };
        var context = f.Context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        var result = await f.RunAsync([moved], context: context);
        var saved = f.Store.SyncedById(f.Pair.PairId, Side.Local, moved.Moved.Id);
        Check.Require(result.Kind == RenameKind.Follow && saved is { LocalPath: "New", DropboxPath: "New" } &&
            f.Events.Single().Kind == RunEventKind.Effect && f.Store.Issues(f.Pair.PairId).Count == 0,
            "A move out of an ignored name follows into scope through the production PathScope.");
    }

    private static async Task UpgradeRenameAsync()
    {
        const string savedLocal = "A_B";
        const string savedDropbox = "A:B";
        const string observedDropbox = "AB";
        using var f = new Fixture();
        var localSaved = f.LocalOld with { Path = savedLocal, Id = "local-upgrade" };
        var dropboxSaved = f.DropboxOld with { Path = savedDropbox, Id = "dropbox-upgrade" };
        var saved = SyncedEntries.From(localSaved, dropboxSaved);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
        {
            ForgetSelf = [(Side.Local, f.Saved.LocalKey)],
            Record = [saved]
        });
        f.Local.Seed(localSaved, Fixture.Content);
        var observed = dropboxSaved with { Path = observedDropbox };
        f.Dropbox.Seed(observed, Fixture.Content);
        var decision = await f.RunAsync([new RenameCandidate(Side.Dropbox, observed, saved)]);
        var upgraded = f.Store.SyncedById(f.Pair.PairId, Side.Dropbox, dropboxSaved.Id);
        var local = await f.Local.ReadOneAsync(observedDropbox, default);
        var dropbox = await f.Dropbox.ReadOneAsync(observedDropbox, default);
        Check.Require(decision.Kind == RenameKind.Follow && decision.MovedSide == Side.Dropbox &&
            decision.Target == observedDropbox && upgraded is
            {
                LocalPath: observedDropbox, DropboxPath: observedDropbox,
                LocalId: "local-upgrade", DropboxId: "dropbox-upgrade"
            } && upgraded.LocalKey == PathNames.Key(observedDropbox) &&
            upgraded.DropboxKey == PathNames.DropboxKey(observedDropbox),
            "The upgrade regression follows the current observed spelling and persists the new valid binding key.");
        Check.Require(f.Store.Issues(f.Pair.PairId).Count == 0 && f.Events.Single() is
            { Kind: RunEventKind.Effect, Path: observedDropbox, Detail: savedLocal, Side: Side.Local } &&
            await f.Local.ReadOneAsync(savedLocal, default) is null && local is { Id: "local-upgrade" } &&
            dropbox == observed,
            "One Follow moves the PC copy, with no delete, SkipReservedName, collision refusal or mapper revival.");
    }

    private static bool ExactKeys(PairConfig pair, IReadOnlyList<string> required, params string[] paths)
    {
        var expected = paths.SelectMany(path => new[]
            { PathLocks.LocalKey(pair, path), PathLocks.DropboxKey(pair, path) }).ToArray();
        return required.Count == expected.Length && expected.All(key => required.Contains(key, PathLocks.KeyComparer));
    }

    private static void RebaseSaved(Fixture f, string local, string dropbox) =>
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
        {
            Renames = [new RenameApplied(f.Saved, f.Saved with
            {
                LocalPath = local, LocalKey = PathNames.Key(local),
                DropboxPath = dropbox, DropboxKey = PathNames.DropboxKey(dropbox)
            }, null, null, null)]
        });

    private static async Task<CountingExecution> AdmitAsync(Fixture f, IReadOnlyList<RenameCandidate> group,
        Entry? localById, long generation, string? pairId = null, CancellationToken ct = default)
    {
        var keys = LockKeys.ForPaths(f.Pair, RenameFootprint(f, group, localById));
        var request = new ExecutionRequest(pairId ?? f.Pair.PairId, generation, "account", f.Admission, false,
            keys.Select(key => new PathLockScope(key, false)).ToArray());
        return new CountingExecution(await AdmittedExecution.AcquireAsync([request], static () => true, ct));
    }

    private static IEnumerable<(Side Side, string Path)> RenameFootprint(Fixture f,
        IReadOnlyList<RenameCandidate> group, Entry? localById)
    {
        var parents = new Parents(f.Context);
        foreach (var candidate in group)
        {
            yield return (Side.Local, candidate.Synced.LocalPath);
            yield return (Side.Dropbox, candidate.Synced.DropboxPath);
            yield return (candidate.Side, candidate.Moved.Path);
            var other = PathNames.Other(candidate.Side);
            yield return (other, parents.Destination(other, candidate.Moved.Path));
        }
        if (localById is not null)
        {
            yield return (Side.Local, localById.Path);
            yield return (Side.Dropbox, localById.Path);
        }
    }

    private static Task<RenameDecision> RunAdmittedAsync(Fixture f, IAdmittedExecution lease, long generation,
        IReadOnlyList<RenameCandidate> group, Entry? localById, Action<Issue>? retainUnfinished = null,
        ExecutorContext? context = null, CancellationToken ct = default) =>
        SyncJobs.Create((context ?? f.Context) with { ExpectedGeneration = generation },
            new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items.RenameAdmittedAsync(lease, null,
            (context ?? f.Context).Opportunity, group, localById, ct, retainUnfinished);

    private static async Task<(RenameDecision Result, List<Issue> Delivered)> AdmittedIssuesAsync(
        Fixture f, IReadOnlyList<RenameCandidate> group, Entry? localById, ExecutorContext? context = null)
    {
        var lease = await AdmitAsync(f, group, localById, Fixture.Generation);
        var delivered = new List<Issue>();
        try
        {
            var result = await RunAdmittedAsync(f, lease, Fixture.Generation, group, localById, delivered.Add, context);
            return (result, delivered);
        }
        finally { await lease.DisposeAsync(); }
    }

    private static Side Other(Side side) => side == Side.Local ? Side.Dropbox : Side.Local;

    private static void Measured()
    {
        var saved = new SyncedEntry(PathNames.Key("Old"), "Old", PathNames.DropboxKey("Old"), "Old",
            false, "local-id", 1, DateTimeOffset.UnixEpoch.UtcTicks, "dropbox-id", "hash");
        var follow = new RenameDecision(RenameKind.Follow, Side.Local, "New");
        var items = new[] { new IssueItem(Side.Local, "Old", "local-id") };

        Check.Require(AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(follow.WrittenSide());
        }) == AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(follow.Kind == RenameKind.FinishCaseRename ? Side.Dropbox
                    : follow.Kind == RenameKind.Follow && follow.MovedSide is { } moved ? PathNames.Other(moved) : null);
        }), "RenameDecision.WrittenSide allocates no more than its inline expression.");

        Check.Require(AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(saved.PathOn(i % 2 == 0 ? Side.Local : Side.Dropbox));
        }) == AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(i % 2 == 0 ? saved.LocalPath : saved.DropboxPath);
        }), "SyncedEntry.PathOn allocates no more than its inline field selection.");

        Check.Require(AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(EffectKinds.Moved(i % 2 == 0 ? Side.Local : Side.Dropbox));
        }) == AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(i % 2 == 0 ? EffectKind.MovedLocal : EffectKind.MovedDropbox);
        }), "EffectKinds.Moved allocates no more than its inline expression.");

        Check.Require(AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(IssueSubject.FromDistinct(items, Side.Local));
        }) == AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(new IssueSubject(items.Distinct().ToArray(), Side.Local));
        }), "IssueSubject.FromDistinct allocates exactly as its inline distinct construction.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(Side? value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(EffectKind value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(IssueSubject value)
    {
    }

    private static ValueTask<IAsyncDisposable> RefuseReserveDownload(string account, string route, long length,
        CancellationToken ct) => throw new InvalidOperationException("Rename never reserves a download.");

    private static Task<DeletionEvidence> RefuseDeletionEvidence(string path, bool isFolder, CancellationToken ct) =>
        throw new InvalidOperationException("Rename never reads deletion evidence.");

    private static RequestAttempt RefuseBeginDownloadAttempt(object operation) =>
        throw new InvalidOperationException("Rename never begins a download attempt.");

    private static Task<bool> RefuseCompleteDownloadAttempt(RequestAttempt attempt, CancellationToken ct) =>
        throw new InvalidOperationException("Rename never completes a download attempt.");

    private sealed class IdentitySide(ISide inner,
        Func<string, CancellationToken, Task<(IdentityLocation Location, bool InScope)?>> identity) : ISide, IIdentitySide
    {
        public Side Side => inner.Side;
        public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct) => identity(id, ct);
        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            inner.ReadAllAsync(under, options, ct);
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) => inner.OpenReadAsync(entry, attempt, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) => inner.StageAsync(path, content, source, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.PublishAsync(staged, expected, selection, ct);
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) => inner.DeleteAsync(expected, selection, ct);
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) => inner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    private sealed class ChangingRenameStore(IStateStore inner, SyncedEntry saved) : ForwardingStateStore(inner)
    {
        public int Reads { get; private set; }

        public override SyncedEntry? SyncedByKey(string pairId, Side side, string key)
        {
            Reads++;
            if (Reads > 2) throw new InvalidOperationException("Rename admission retry did not terminate.");
            var path = "Changed" + Reads;
            return saved with { DropboxPath = path, DropboxKey = PathNames.DropboxKey(path) };
        }
    }

    private sealed class CountingExecution(IAdmittedExecution inner) : IAdmittedExecution
    {
        private int _disposes;

        public int RequireCalls { get; private set; }
        public List<IReadOnlyList<string>> RequiredKeys { get; } = [];
        public int DisposeCount => Volatile.Read(ref _disposes);

        public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys)
        {
            RequireCalls++;
            RequiredKeys.Add(requiredLockKeys);
            inner.Require(pairId, generation, requiredLockKeys);
        }

        public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) =>
            inner.RequireScopes(pairId, generation, scopes);

        public void Revalidate() => inner.Revalidate();

        public ValueTask ReleaseAdmissionAsync() => inner.ReleaseAdmissionAsync();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposes);
            return inner.DisposeAsync();
        }
    }

    private sealed class RefusingItemAttempts : IItemAttempts
    {
        private int _uses;

        public int Uses => Volatile.Read(ref _uses);

        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) => Refuse<ItemRetryCandidate>();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) => Refuse<object>();

        private T Refuse<T>()
        {
            Interlocked.Increment(ref _uses);
            throw new InvalidOperationException("Rename does not hold an item-attempt authority.");
        }
    }

    private sealed class RecoveryBoundary : IRecoveryExecutionBoundary
    {
        public void RequireCurrent() => throw new NotSupportedException(nameof(RequireCurrent));

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new NotSupportedException(nameof(ExecuteAsync));

        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new NotSupportedException(nameof(ExecuteAsync));

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new NotSupportedException(nameof(ExecuteAsync));

        public Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) =>
            throw new NotSupportedException(nameof(ExecuteAsync));
    }

    private sealed class Fixture : IDisposable
    {
        public const long Generation = 0;
        public static readonly byte[] Content = [1, 2, 3];
        public static readonly DateTimeOffset Time = DateTimeOffset.UnixEpoch.AddDays(1);
        public static readonly DateTimeOffset RunStart = Time.AddDays(1);
        private readonly string? _storeFolder;
        public readonly IStateStore Store;
        public readonly FakeSide Local = new(Side.Local);
        public readonly FakeSide Dropbox = new(Side.Dropbox);
        public readonly FakeAdmission Admission = new();
        public readonly FakeTimeProvider Clock = new();
        public readonly RefusingItemAttempts ItemAttempts = new();
        public readonly List<RunEvent> Events = [];
        public readonly PairConfig Pair;
        public ExecutorContext Context { get; private set; }
        public readonly Entry LocalOld;
        public readonly Entry DropboxOld;
        public readonly SyncedEntry Saved;

        public Fixture(bool folder = false, bool mappedParent = false, bool realStore = false)
        {
            if (realStore)
            {
                _storeFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Store = StateStore.Open(Path.Combine(_storeFolder, "rename.db"));
            }
            else Store = new InMemoryStateStore();
            Pair = SourceFixture.Bound(new PairConfig(nameof(RenameRunnerChecks), nameof(RenameRunnerChecks),
                Path.Combine(Path.GetTempPath(), nameof(RenameRunnerChecks)), "/Pair", "Account", [], [], FirstRunPolicy.NewestWins, true, null));
            var settings = new EngineSettings("", "", "", "", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", DefaultLogBufferCharacters, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes };
            Context = new ExecutorContext(Local, Dropbox, new FakeRecovery(), Store, Pair, new PathScope(Pair, [Pair]),
                settings, new ReplaceTurn(), Admission, false, Events.Add,
                ReserveDownloadAsync: RefuseReserveDownload, Time: Clock,
                ReadDeletionEvidenceAsync: RefuseDeletionEvidence,
                BeginDownloadAttempt: RefuseBeginDownloadAttempt,
                CompleteDownloadAttemptAsync: RefuseCompleteDownloadAttempt,
                UploadAdmissionIdentity: ("", "", ""), ItemAttempts: ItemAttempts,
                Opportunity: new object(), ExpectedGeneration: Generation);
            Clock.Advance(RunStart - DateTimeOffset.UnixEpoch);
            LocalOld = new Entry(mappedParent ? "a_b/Old" : "Old", folder, Content.Length, Time, "local-id", "", "", null);
            DropboxOld = new Entry(mappedParent ? "a:b/Old" : "Old", folder, Content.Length, Time, "dropbox-id", "revision", DropboxContentHash.Compute(Content), null);
            Local.Seed(LocalOld, folder ? null : Content);
            Dropbox.Seed(DropboxOld, folder ? null : Content);
            Saved = SyncedEntries.From(LocalOld, DropboxOld);
            Store.SavePair(Pair);
            SourceFixture.AcquireJournal(Store, Pair);
            Store.Commit(Pair.PairId, StateChange.Empty with { Record = [Saved] });
            if (mappedParent)
            {
                var localParent = LocalOld with { Path = "a_b", IsFolder = true, Id = "local-parent" };
                var dropboxParent = DropboxOld with { Path = "a:b", IsFolder = true, Id = "dropbox-parent" };
                Local.Seed(localParent, null);
                Dropbox.Seed(dropboxParent, null);
                Store.Commit(Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(localParent, dropboxParent)] });
            }
        }

        public FakeSide For(Side side) => side == Side.Local ? Local : Dropbox;

        public Entry? ScanEntry(Side side, string id) =>
            Store.ScanRows(Pair.PairId, _ => null!)
                .Select(row => side == Side.Local ? row.Local : row.Dropbox)
                .FirstOrDefault(entry => entry?.Id == id);

        public async Task<RenameCandidate> MoveAsync(Side side, string path) => new(side,
            await For(side).MoveAsync(side == Side.Local ? LocalOld : DropboxOld, path, default), Saved);

        public Task<RenameDecision> RunAsync(IReadOnlyList<RenameCandidate> group, DateTimeOffset? runAt = null,
            ExecutorContext? context = null, IReadOnlyList<RenameCandidate>? runHolds = null,
            IReadOnlySet<long>? retry = null,
            CancellationToken ct = default, bool completedScan = false)
        {
            Clock.Advance((runAt ?? RunStart) - Clock.GetUtcNow());
            var execution = context ?? Context;
            var syncer = SyncJobs.Create(execution, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items;
            var rows = retry is null ? syncer.Rows : syncer.ScanContexts(false, retry, null, null);
            if (runHolds is not null) rows.HoldRename(runHolds);
            return syncer.RenameAsync(group, group.FirstOrDefault(c => c.Side == Side.Local)?.Moved,
                ct, completedScan);
        }

        public SyncedEntry SeedChild(string path)
        {
            var local = LocalOld with { Path = path, IsFolder = false, Id = "local-" + path };
            var dropbox = DropboxOld with { Path = path, IsFolder = false, Id = "dropbox-" + path };
            Local.Seed(local, Content);
            Dropbox.Seed(dropbox, Content);
            var saved = SyncedEntries.From(local, dropbox);
            Store.Commit(Pair.PairId, StateChange.Empty with { Record = [saved] });
            return saved;
        }

        /// <summary>Moves the store past the context's generation, as a captured selection can be.</summary>
        public void AdvanceGeneration(long to)
        {
            for (var generation = Generation; generation < to; generation++)
                Store.SavePairDecision(Pair, generation, Pair, null, false, null);
        }

        public async Task ScanAsync()
        {
            var generation = Store.ReadPairExecution(Pair.PairId)!.Value.Generation;
            Context = Context with { ScanId = Store.BeginScan(Pair.PairId, generation, false) };
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var sink = Store.OpenScan(Pair.PairId, side, ScanTestSettings.BatchRows);
                await foreach (var item in For(side).ReadAllAsync("", new ListOptions(true, true), default))
                    await sink.AddAsync(item, default);
                await sink.CompleteAsync();
            }
        }

        public void Raise(IssueKind kind, TextKey cause, string path, Side side) => Store.Commit(Pair.PairId,
            StateChange.Empty with { Raise = [(kind, cause, path, new IssueSubject([new IssueItem(side, path, "issue-id")], null))] });

        public void CheckStamps(SyncedEntry saved) => Check.Require(saved.LocalSize == Saved.LocalSize &&
            saved.LocalModifiedTicks == Saved.LocalModifiedTicks && saved.DropboxContentHash == Saved.DropboxContentHash,
            "Rename decisions preserve all unreconciled content stamps.");

        public void Dispose()
        {
            Store.Dispose();
            if (_storeFolder is not null) Directory.Delete(_storeFolder, recursive: true);
        }
    }
}
