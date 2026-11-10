using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for Syncer (`--engine-checks syncer`).</summary>
public static class SyncerChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static void RunResolvedFolderExclusion() => ResolvedFolderChildAsync(held: false).GetAwaiter().GetResult();

    public static void RunResolvedFolderHold() => ResolvedFolderChildAsync(held: true).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await BeforeProofReadBudgetAsync();
        var concurrencyFailures = new List<Exception>();
        foreach (var check in new Func<Task>[] { ChangeRunnerChecks.ConcurrentUploadCapacityAsync,
            ChangeRunnerChecks.ConcurrentCoverageCapacityAsync, ChangeRunnerChecks.ConcurrentSelectionIsolationAsync,
            ChangeRunnerChecks.ConcurrentLaneFailureAsync, ChangeRunnerChecks.ConcurrentRefusalRedispatchAsync,
            ChangeRunnerChecks.SustainedArrivalRoundsAsync, ChangeRunnerChecks.ConcurrentMissingParentAsync,
            ChangeRunnerChecks.AttemptedAncestorSkipsAsync, ChangeRunnerChecks.AttemptedSkipReplaysAsync,
            ChangeRunnerChecks.DuplicateSelectionKeepsClaimAsync, ChangeRunnerChecks.AttemptedAncestorProgressAsync,
            ChangeRunnerChecks.SelectionRefusalReasonsAsync })
        {
            try { await check(); }
            catch (Exception error) { concurrencyFailures.Add(new InvalidOperationException(check.Method.Name, error)); }
        }
        if (concurrencyFailures.Count != 0) throw new AggregateException(concurrencyFailures);
        await SkippedAdmittedFollowUpAsync();
        await ReservedFolderAsync();
        await NameSkipTargetedNoticeAsync();
        await NameSkipSavedBindingAsync();
        await NameSkipNoticeLifecycleAsync();
        await NameSkipZeroSiblingAsync();
        await AdmittedGenerationAsync();
        await RenameWorkflowAsync();
        await EditedContentAfterSpellingAdoptionAsync();
        await RecordRelocatedBindingAsync();
        await RenameInUseAssociatesLocalAsync();
        await TieKeepBothAsync();
        await FirstRunFolderAsync();
        await FailedReplacementCompletesRowAsync();
        await UntouchedRowsAsync();
        await DropboxRootBatchAsync();
        await OwnFolderLiveBatchCasesAsync();
        await DirectRowsAsync();
        await ReappearedVersionsAsync();
        await StaleDeferralAsync();
        await StaleTieAsync();
        await UploadFreshnessAsync();
        await FirstRunContentAsync();
        await SpentRestoreAsync();
        await RemovedThenRestoredAsync();
        await CopiesAsync();
        await DisappearedCopyParentAsync();
        await RemovalReadFreshnessAsync();
        await ResolutionFollowUpsAsync();
        await FollowUpReadFailureAsync();
        await FollowUpSessionHoldsAsync();
        await SavedDestinationsAsync();
        await RecreatedAsync();
        await HashFreshnessAsync();
        await DirtyArrivalBeforeEffectAsync();
        await OldAppPatternThreeAsync();
        await NewlyLinkedAsync();
        await FreeIncomingAsync();
        await ReturningOriginalsAsync();
        await MappedReobserveAsync();
        await ReobserveFailureAsync();
        await SingleLegKeepBothAsync();
        await LocksAsync();
        await LabelRaceAsync();
        await SkippedDropboxLabelRaceAsync();
        await QueuedAdmissionAsync();
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await PlannedReplacementInLiveContextAsync(folder);
            await FolderPlansAsync(folder);
            await RestoredFolderAsync(folder);
            await CrossLinkedRestoredAsync(folder);
            await UnreadableFoldersAsync(folder);
            await CounterpartCollisionAsync(folder);
            await CounterpartSelectedBranchAsync(folder);
            await FailedSurvivorAsync(folder);
            await DeferredSurvivorAsync(folder);
            await RefusedSurvivorAsync(folder);
            Check.Require(!Directory.EnumerateFiles(folder, "*" + StateSchema.PlanFileSuffix + "*" + StateSchema.DatabaseExtension).Any(),
                "Every completed or failed folder operation disposes its real scratch plan.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        await PendingAsync();
        await LearnedLocalContentAsync();
        await ReadmittedOutcomeCasesAsync();
        await LocalProofLifetimeAsync();
        await HashFailureAsync();
        await HashSourceReappearsAsync();
        await ReadFailuresAsync();
        await AdmittedPreludeFailuresAsync();
        await FolderPreparationReadFailuresAsync();
        await CancellationAsync();
        CostProbe();
    }

    internal static async Task OwnFolderActionCasesAsync()
    {
        foreach (var target in Enum.GetValues<Side>())
        foreach (var kind in new[] { ActionKind.Upload, ActionKind.Download, ActionKind.EnsureLocalFolder,
            ActionKind.EnsureDropboxFolder, ActionKind.DeleteLocal, ActionKind.DeleteDropbox, ActionKind.MoveLocal,
            ActionKind.MoveDropbox, ActionKind.MoveUnlinked, ActionKind.ReplaceKind, ActionKind.KeepBoth,
            ActionKind.Record, ActionKind.ForgetOnly })
        foreach (var bound in new[] { false, true })
        foreach (var occupied in new[] { false, true })
        {
            if (kind is (ActionKind.Upload or ActionKind.EnsureDropboxFolder or ActionKind.DeleteDropbox or ActionKind.MoveDropbox)
                && target != Side.Dropbox || kind is (ActionKind.Download or ActionKind.EnsureLocalFolder or ActionKind.DeleteLocal or ActionKind.MoveLocal)
                && target != Side.Local) continue;
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var localParent = await context.Local.EnsureFolderAsync("Parent", default);
            var remoteParent = await context.Dropbox.EnsureFolderAsync("Parent", default);
            Save(context, localParent, remoteParent);
            var generation = store.ReadPairExecution(context.Pair.PairId)!.Value.Generation;
            var source = store.ReadSource(context.Pair.PairId, Side.Local)!.Value;
            var position = store.ReadJournal("1");
            store.AcquireJournalPage("1", position, position!,
                [(context.Pair.PairId, generation, source.Epoch, source.Revision, false)],
                [(context.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, "Parent", null,
                    localParent.Id, localParent, false, false))]);
            var folder = kind is ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder;
            var origin = PathNames.Other(target);
            var incoming = folder ? await context.For(origin).EnsureFolderAsync("Parent/Child", default)
                : await PutAsync(context.For(origin), "Parent/Child");
            // A populated written side is the overwrite/move/state variant: today's code, never the create exception.
            var occupiedTarget = occupied
                ? folder ? await context.For(target).EnsureFolderAsync("Parent/Child", default)
                    : await PutAsync(context.For(target), "Parent/Child")
                : null;
            var saved = bound ? new SyncedEntry(PathNames.Key(incoming.Path), incoming.Path,
                PathNames.DropboxKey(incoming.Path), incoming.Path, folder, incoming.Id, incoming.Size,
                incoming.Modified.UtcTicks, incoming.Id, incoming.ContentHash) : null;
            if (saved is not null) store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var row = Row(
                target == Side.Local ? occupiedTarget ?? (origin == Side.Local ? incoming : null) : origin == Side.Local ? incoming : null,
                target == Side.Dropbox ? occupiedTarget ?? (origin == Side.Dropbox ? incoming : null) : origin == Side.Dropbox ? incoming : null,
                saved);
            row = row with { Context = row.Context with { Destination = incoming.Path } };
            var action = new SyncAction(kind, row, Target: kind is ActionKind.Record or ActionKind.ForgetOnly or ActionKind.KeepBoth
                ? null : incoming.Path, Side: target);
            var jobs = NewJobs(context);
            jobs.Ancestors.CoverListedWork(0);
            var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, incoming.Path), (Side.Dropbox, incoming.Path)]);
            var labels = kind == ActionKind.KeepBoth ? new KeepBothLabels("Parent/RemoteCopy", false, "Parent/LocalCopy", false) : null;
            var result = await jobs.Items.Admission.ExecuteLockedAsync(action, labels, keys, keys, default, null, null);
            var qualifies = action.CreatesNewItem();
            Check.Require((result.Outcome?.Raised != IssueKind.Failure) == qualifies,
                $"A233/A234/A81: {kind}, target={target}, bound={bound}, occupied={occupied} preserves action containment");
            if (!qualifies)
            {
                Check.Require(result.Outcome?.Raised == IssueKind.Failure &&
                    store.Issues(context.Pair.PairId).Single(issue => issue.Path == incoming.Path).Cause == TextKey.FailChanged,
                    $"A233/A81: {kind}, occupied={occupied} is refused before effects");
                var after = await context.For(target).ReadOneAsync(incoming.Path, default);
                Check.Require(occupiedTarget is null ? after is null : after == occupiedTarget,
                    $"A233: {kind}, occupied={occupied} left the target unchanged");
            }
            Check.Require(store.PendingAncestors(context.Pair.PairId, generation, Side.Local,
                [PathNames.Key("Parent")]).Any(), "A237: matching notices remain pending");
        }
    }

    // B-104 T3: in a live batch, a later matching notice at the folder passes only a create-intent row; every other
    // kind refuses at the ancestor fence (AncestorFence.AncestorsClear), and without a batch every kind refuses as today.
    private static async Task OwnFolderLiveBatchCasesAsync()
    {
        foreach (var batch in new[] { true, false })
        foreach (var target in Enum.GetValues<Side>())
        foreach (var kind in new[] { ActionKind.Upload, ActionKind.Download, ActionKind.EnsureLocalFolder,
            ActionKind.EnsureDropboxFolder, ActionKind.DeleteLocal, ActionKind.DeleteDropbox, ActionKind.MoveLocal,
            ActionKind.MoveDropbox, ActionKind.MoveUnlinked, ActionKind.ReplaceKind, ActionKind.KeepBoth,
            ActionKind.Record, ActionKind.ForgetOnly })
        foreach (var bound in new[] { false, true })
        foreach (var occupied in new[] { false, true })
        {
            if (kind is (ActionKind.Upload or ActionKind.EnsureDropboxFolder or ActionKind.DeleteDropbox or ActionKind.MoveDropbox)
                && target != Side.Dropbox || kind is (ActionKind.Download or ActionKind.EnsureLocalFolder or ActionKind.DeleteLocal or ActionKind.MoveLocal)
                && target != Side.Local) continue;
            using var store = new InMemoryStateStore();
            var context = Context(store);
            if (batch) context = context with { BatchScope = "Parent" };
            var localParent = await context.Local.EnsureFolderAsync("Parent", default);
            var remoteParent = await context.Dropbox.EnsureFolderAsync("Parent", default);
            Save(context, localParent, remoteParent);
            var generation = store.ReadPairExecution(context.Pair.PairId)!.Value.Generation;
            var source = store.ReadSource(context.Pair.PairId, Side.Local)!.Value;
            var position = store.ReadJournal("1");
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, "Parent", null, localParent.Id, localParent, false, false);
            // The selection's own notice, then a later restating one after its pass watermark.
            var later = store.AcquireJournalPage("1", position, position!,
                [(context.Pair.PairId, generation, source.Epoch, source.Revision, false)],
                [(context.Pair.PairId, notice), (context.Pair.PairId, notice)])!.Value;
            var own = new WorkSelection(context.Pair.PairId, generation, source.Epoch, Side.Local, "1", localParent.Id,
                PathNames.KeyFor(Side.Local, "Parent"), later - 1, later - 1);
            var folder = kind is ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder;
            var origin = PathNames.Other(target);
            var incoming = folder ? await context.For(origin).EnsureFolderAsync("Parent/Child", default)
                : await PutAsync(context.For(origin), "Parent/Child");
            var occupiedTarget = occupied
                ? folder ? await context.For(target).EnsureFolderAsync("Parent/Child", default)
                    : await PutAsync(context.For(target), "Parent/Child")
                : null;
            var saved = bound ? new SyncedEntry(PathNames.Key(incoming.Path), incoming.Path,
                PathNames.DropboxKey(incoming.Path), incoming.Path, folder, incoming.Id, incoming.Size,
                incoming.Modified.UtcTicks, incoming.Id, incoming.ContentHash) : null;
            if (saved is not null) store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var row = Row(
                target == Side.Local ? occupiedTarget ?? (origin == Side.Local ? incoming : null) : origin == Side.Local ? incoming : null,
                target == Side.Dropbox ? occupiedTarget ?? (origin == Side.Dropbox ? incoming : null) : origin == Side.Dropbox ? incoming : null,
                saved);
            row = row with { Context = row.Context with { Destination = incoming.Path } };
            var action = new SyncAction(kind, row, Target: kind is ActionKind.Record or ActionKind.ForgetOnly or ActionKind.KeepBoth
                ? null : incoming.Path, Side: target);
            var jobs = NewJobs(context);
            jobs.Ancestors.RunSelection(own);
            var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, incoming.Path), (Side.Dropbox, incoming.Path)]);
            var labels = kind == ActionKind.KeepBoth ? new KeepBothLabels("Parent/RemoteCopy", false, "Parent/LocalCopy", false) : null;
            LiveAdmissionStaleException? refused = null;
            SyncOutcome? outcome = null;
            try { outcome = (await jobs.Items.Admission.ExecuteLockedAsync(action, labels, keys, keys, default, null, null)).Outcome; }
            catch (LiveAdmissionStaleException error) { refused = error; }
            var qualifies = batch && action.CreatesNewItem();
            var after = await context.For(target).ReadOneAsync(incoming.Path, default);
            Check.Require(qualifies
                    ? refused is null && outcome?.Raised != IssueKind.Failure && after is not null
                    : refused is not null && (occupiedTarget is null ? after is null : after == occupiedTarget),
                $"T3: {kind}, batch={batch}, target={target}, bound={bound}, occupied={occupied}: only a live batch's create " +
                $"passes a later matching notice; refused={refused is not null} raised={outcome?.Raised} written={after is not null}");
        }
    }

    // A223: under a matching echo a changed own read refuses without reobserving; without later work it reobserves.
    internal static async Task OwnFolderRestatedFreshnessAsync()
    {
        foreach (var target in Enum.GetValues<Side>())
        foreach (var withNotice in new[] { true, false })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var localParent = await context.Local.EnsureFolderAsync("Parent", default);
            var remoteParent = await context.Dropbox.EnsureFolderAsync("Parent", default);
            Save(context, localParent, remoteParent);
            if (withNotice)
            {
                var generation = store.ReadPairExecution(context.Pair.PairId)!.Value.Generation;
                var source = store.ReadSource(context.Pair.PairId, Side.Local)!.Value;
                var position = store.ReadJournal("1");
                store.AcquireJournalPage("1", position, position!,
                    [(context.Pair.PairId, generation, source.Epoch, source.Revision, false)],
                    [(context.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, "Parent", null,
                        localParent.Id, localParent, false, false))]);
            }
            var origin = PathNames.Other(target);
            var incoming = await PutAsync(context.For(origin), "Parent/Child");
            // The row's own source entry differs from the fresh read, so the row is not fresh.
            var stale = incoming with { Size = incoming.Size + 1, Modified = incoming.Modified.AddMinutes(1) };
            var row = Row(origin == Side.Local ? stale : null, origin == Side.Dropbox ? stale : null, null);
            row = row with { Context = row.Context with { Destination = incoming.Path } };
            var action = new SyncAction(SyncAction.CopyOn(false, target), row, Target: incoming.Path);
            var jobs = NewJobs(context);
            jobs.Ancestors.CoverListedWork(0);
            var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, incoming.Path), (Side.Dropbox, incoming.Path)]);
            var result = await jobs.Items.Admission.ExecuteLockedAsync(action, null, keys, keys, default, null, null);
            if (withNotice)
                Check.Require(result.Reobserve == false && result.Outcome?.Raised == IssueKind.Failure,
                    "A223: a changed own read under a matching echo refuses without reobserving");
            else
                Check.Require(result.Reobserve && result.Outcome is null,
                    "A223: the same changed row with no later ancestor work reobserves as today");
        }
    }

    private static async Task ReservedFolderAsync()    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var pair = context.Pair with { LocalRoot = root };
        var scope = new PathScope(pair, [pair]);
        var events = new List<RunEvent>();
        context = context with { Pair = pair, Scope = scope, Events = events.Add };
        var remote = (FakeSide)context.Dropbox;
        var folder = new Entry("CON", true, 0, default, "folder-id", "", "", TextKey.SkipReservedName.ToString());
        var child = folder with { Path = "CON/Child", Id = "child-id", IsFolder = false, Skip = null };
        remote.Seed(folder, null);
        remote.Seed(child, [1]);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, folder.Path, folder.Id)], null);
        var scan = store.BeginScan(pair.PairId, 0, false);
        store.ReplaceSkipped(pair.PairId, scan, [(folder.Path, TextKey.SkipReservedName, subject)]);
        store.Commit(pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.LocalNameInvalid, folder.Path, subject)]
        });
        var row = Row(null, folder, null) with { Context = Facts() with { Skipped = true } };
        Check.Require(Decide.For(row, pair.FirstRun, CompareAt, localContentDirty: false) is { Kind: ActionKind.None, None: NoneReason.Skipped },
            "A reserved Dropbox folder is already decided Skipped before execution.");
        var jobs = NewJobs(context);
        foreach (var pass in new[] { 0, 1 })
        {
            var outcome = await jobs.Items.RunRowAttemptAsync(row, default);
            Check.Require(outcome == new SyncOutcome(ActionKind.None, NoneReason.Skipped, null, null, false),
                $"Reserved folder pass {pass} must not become a local name Failure.");
            Check.Require(store.Issues(pair.PairId) is { Count: 2 } issues &&
                issues[0].Kind == IssueKind.Skipped && issues[0].Path == folder.Path &&
                issues[1].Kind == IssueKind.Failure && issues[1].Path == folder.Path &&
                issues[1].Cause == TextKey.LocalNameInvalid && issues[1].Subject == subject,
                "A skipped folder preserves its existing Failure and its one Skipped notice.");
        }
        Check.Require(events.Count == 0 && ((FakeAdmission)context.Admission).Admitted == 0 &&
            await context.Local.ReadOneAsync(folder.Path, default) is null &&
            await remote.ReadOneAsync(child.Path, default) == child,
            "Skipped folders leave their children untouched, create nothing locally and emit no extra notice.");
    }

    // NAME-TARGETED-NOTICE: a direct invalid Dropbox target is skipped with the existing notice at once,
    // deduplicated on repetition, without an invalid Local open or any transfer/create/delete/record.
    private static async Task NameSkipTargetedNoticeAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var localInner = (FakeSide)context.Local;
        var remoteInner = (FakeSide)context.Dropbox;
        var skipped = new Entry("CON", false, 1, DateTimeOffset.UnixEpoch, "dropbox-id", "", "",
            TextKey.SkipReservedName.ToString());
        remoteInner.Seed(skipped, [1]);
        var valid = await PutAsync(remoteInner, "valid");
        var local = new CountingSide(localInner);
        var remote = new CountingSide(remoteInner);
        context = context with { Local = local, Dropbox = remote };
        var jobs = NewJobs(context);
        var outcome = await jobs.Items.SyncPathAsync(Side.Dropbox, skipped.Path, default);
        Check.Require(outcome.Kind == ActionKind.None && outcome.None == NoneReason.Skipped && outcome.Raised is null,
            "A targeted invalid Dropbox name is skipped without a failure.");
        var issue = store.Issues(context.Pair.PairId).FirstOrDefault();
        Check.Require(issue is { Kind: IssueKind.Skipped, Cause: TextKey.SkipReservedName } &&
            issue.Path == skipped.Path,
            "Targeted observation raises the existing Skipped/SkipReservedName notice immediately.");
        await jobs.Items.SyncPathAsync(Side.Dropbox, skipped.Path, default);
        Check.Require(store.Issues(context.Pair.PairId).Count == 1,
            "A repeated targeted skip deduplicates to one notice.");
        Check.Require(local.Reads == 0 && local.ContentReads == 0 && local.Work == 0 && remote.Work == 0 &&
            local.Stages == 0 && local.Publishes == 0 && local.Deletes == 0 && local.Moves == 0 &&
            remote.Stages == 0 && remote.Publishes == 0 && remote.Deletes == 0 && remote.Moves == 0,
            "A skipped target never opens an invalid Local name and performs no transfer, create, delete or record.");
        var next = await jobs.Items.SyncPathAsync(Side.Dropbox, valid.Path, default);
        Check.Require(next.Kind is ActionKind.Download or ActionKind.Record,
            "An unrelated valid item still progresses beside the skipped target.");
    }

    // NAME-PERSISTED-BINDING + restart: a saved mapped Local copy must survive a raw Dropbox name change and
    // re-observation; the raw key is never inferred as absence and the protected bytes are never deleted.
    private static async Task NameSkipSavedBindingAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var localInner = (FakeSide)context.Local;
        var remoteInner = (FakeSide)context.Dropbox;
        var localEntry = await PutAsync(localInner, "a_b");
        var remoteEntry = new Entry("a:b", false, 1, DateTimeOffset.UnixEpoch, "dropbox-id", "", "",
            TextKey.SkipReservedName.ToString());
        remoteInner.Seed(remoteEntry, [1]);
        var saved = Save(context, localEntry, remoteEntry);
        var local = new CountingSide(localInner);
        var remote = new CountingSide(remoteInner);
        context = context with { Local = local, Dropbox = remote };
        var row = Row(localEntry, remoteEntry, saved) with { Context = Facts() with { Skipped = true } };
        var jobs = NewJobs(context);
        foreach (var pass in new[] { 0, 1 })
        {
            var outcome = await jobs.Items.RunRowAttemptAsync(row, default);
            // EX-02 (F6, A29): the Repair row pass delivers no skip notice; only a targeted path maintains it.
            Check.Require(outcome.Raised is null &&
                store.Issues(context.Pair.PairId).Count(issue => issue.Kind == IssueKind.Skipped) == 0,
                $"Saved mapped binding pass {pass} raises no per-row Skipped notice and no failure.");
            Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
                await context.Local.ReadOneAsync(localEntry.Path, default) == localEntry,
                "The saved mapped Local bytes and binding survive; a raw key change is never inferred as absence.");
            Check.Require(local.Deletes == 0 && local.Moves == 0 && local.Publishes == 0,
                "A skipped mapped target never deletes, moves or rewrites the protected Local copy.");
        }
    }

    // NAME-TARGETED-NOTICE lifecycle: dismissal grants no effect permission; unrelated skipped notices survive
    // while a valid item continues to sync.
    private static async Task NameSkipNoticeLifecycleAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var localInner = (FakeSide)context.Local;
        var remoteInner = (FakeSide)context.Dropbox;
        var skipped = new Entry("CON", false, 1, DateTimeOffset.UnixEpoch, "dropbox-id", "", "",
            TextKey.SkipReservedName.ToString());
        remoteInner.Seed(skipped, [1]);
        var valid = await PutAsync(remoteInner, "valid");
        var local = new CountingSide(localInner);
        var remote = new CountingSide(remoteInner);
        context = context with { Local = local, Dropbox = remote };
        var jobs = NewJobs(context);
        await jobs.Items.SyncPathAsync(Side.Dropbox, skipped.Path, default);
        var issue = store.Issues(context.Pair.PairId).FirstOrDefault(candidate => candidate.Kind == IssueKind.Skipped);
        Check.Require(issue is not null, "The skipped target raises its notice before dismissal is tested.");
        if (issue is null) return;
        store.DismissIssue(context.Pair.PairId, issue.Id);
        var afterDismiss = await jobs.Items.SyncPathAsync(Side.Dropbox, skipped.Path, default);
        Check.Require(afterDismiss.Effect is null && afterDismiss.Kind == ActionKind.None &&
            local.Work == 0 && remote.Publishes == 0,
            "Dismissal is not permission to sync an invalid name.");
        var next = await jobs.Items.SyncPathAsync(Side.Dropbox, valid.Path, default);
        Check.Require(next.Kind is ActionKind.Download or ActionKind.Record &&
            store.Issues(context.Pair.PairId).Count(candidate => candidate.Kind == IssueKind.Skipped) <= 1,
            "A valid item still syncs and unrelated skipped notice state is not multiplied.");
    }

    // HI-01: a survivor that becomes skipped runs inside the recovery boundary's outer admission. The
    // skipped-notice path must validate that lease, never re-enter PathLocks (not reentrant) on itself.
    private static async Task SkippedAdmittedFollowUpAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var remote = (FakeSide)context.Dropbox;
        var skipped = new Entry("CON", false, 1, DateTimeOffset.UnixEpoch, "dropbox-id", "", "",
            TextKey.SkipReservedName.ToString());
        remote.Seed(skipped, [1]);
        // The recovery boundary holds both root subtrees, as production does for a folder operation, plus the
        // exact survivor keys so the required-key check does not depend on tree-key spelling.
        var paths = Massdex.SyncShark.Engine.Row.Paths(Row(null, skipped, null));
        IReadOnlyList<PathLockScope> scopes =
        [
            new(PathLocks.LocalKey(context.Pair, ""), true),
            new(PathLocks.DropboxKey(context.Pair, ""), true),
            new(PathLocks.LocalKey(context.Pair, paths.Local), false),
            new(PathLocks.DropboxKey(context.Pair, paths.Dropbox), false)
        ];
        var boundary = new RealPathLocksBoundary(scopes);
        var jobs = SyncJobs.Create(context,
            new SyncPorts(boundary, RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        using var cancellation = new CancellationTokenSource();
        var outcomes = new List<SyncOutcome>();
        var running = boundary.Run(async (execution, token) =>
        {
            await jobs.Items.Observation.SyncFollowUpAsync(Side.Dropbox, skipped.Path, token, execution,
                completed: (_, outcome) => outcomes.Add(outcome));
            return true;
        }, cancellation.Token);
        // Every fake operation completes inline; an incomplete task is a lock wait, not machine slowness.
        var completedInline = running.IsCompleted;
        if (!completedInline)
        {
            cancellation.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
        }
        Check.Require(completedInline, "A skipped survivor must not wait on its already-held footprint.");
        await running;
        Check.Require(!cancellation.IsCancellationRequested &&
            outcomes is [{ Kind: ActionKind.None, None: NoneReason.Skipped, Raised: null, Effect: null }] &&
            store.Issues(context.Pair.PairId) is [{ Kind: IssueKind.Skipped, Path: "CON" }],
            "A skipped admitted follow-up completes without waiting on its own lock, one exact notice, no effect.");
    }

    // NAME-ZERO-SIBLING: one involved invalid item at a flat root of N entries performs zero Observer
    // ReadAll/sibling visits, including the retry.
    private static async Task NameSkipZeroSiblingAsync()
    {
        const int siblings = 32;
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var inner = (FakeSide)context.Dropbox;
        inner.Seed(new Entry("CON", false, 1, DateTimeOffset.UnixEpoch, "id-0", "", "",
            TextKey.SkipReservedName.ToString()), [1]);
        for (var index = 1; index <= siblings; index++)
            await PutAsync(inner, "sibling-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var counts = new CountingSide(inner);
        context = context with { Dropbox = counts };
        var jobs = NewJobs(context);
        foreach (var pass in new[] { 0, 1 })
        {
            var outcome = await jobs.Items.SyncPathAsync(Side.Dropbox, "CON", default);
            Check.Require(outcome.None == NoneReason.Skipped,
                $"Zero-sibling pass {pass} still skips the invalid target.");
        }
        Check.Require(counts.Listings == 0 && counts.SiblingListings == 0,
            "A targeted invalid item at a flat root never lists siblings, including the retry.");
    }

    // F107 admitted generation seam: the captured request generation reaches the boundary unchanged; a Store
    // advanced since the request refuses through the lease with zero effect or completion.
    private static async Task AdmittedGenerationAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var row = Row(await PutAsync(context.Local, "item"), null, null);
        var lease = new StaleLease(current: 1);
        var jobs = NewJobs(context);
        var refused = await RefusedAsync(() => jobs.Items.Execution.RunAdmittedAsync(row, default, execution: lease));
        Check.Require(refused && lease.Seen == 0 && ((FakeAdmission)context.Admission).Completed == 0 &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key("item")) is null,
            "The row route forwards the captured generation, and a stale lease refuses before any effect or record.");

        var pathLease = new StaleLease(current: 1);
        var pathRefused = await RefusedAsync(() => jobs.Items.Observation.SyncFollowUpAsync(
            Side.Local, "item", default, pathLease));
        Check.Require(pathRefused && pathLease.Seen == 0 && ((FakeAdmission)context.Admission).Completed == 0,
            "The path route forwards the same captured generation and refuses a stale lease with no effect.");

        var moved = row.Local! with { Path = "ITEM", Id = "same-id" };
        var saved = SyncedEntries.From(row.Local!, row.Local!);
        var renameLease = new StaleLease(current: 1);
        var renameRefused = await RefusedAsync(() => jobs.Items.Renames.RenameAdmittedAsync(renameLease, null, new object(),
            [new RenameCandidate(Side.Local, moved, saved)], null, default));
        Check.Require(renameRefused || renameLease.Seen is -1 or 0,
            "The rename route never substitutes a refreshed Store generation for the captured one.");
        Check.Require(((FakeAdmission)context.Admission).Completed == 0,
            "No stale admitted route completes an effect.");
    }

    private static async Task RecordRelocatedBindingAsync()
    {
        foreach (var realStore in new[] { false, true })
        foreach (var upload in new[] { false, true })
        foreach (var protectedOld in new[] { false, true })
        foreach (var liveOld in new Side?[] { null, Side.Local, Side.Dropbox })
        {
            var root = Directory.CreateTempSubdirectory(nameof(RecordRelocatedBindingAsync)).FullName;
            try
            {
                using IStateStore store = realStore ? StateStore.Open(Path.Combine(root, "state.db")) : new InMemoryStateStore();
                var context = Context(store);
                var local = await PutAsync(context.Local, "Old");
                var remote = await PutAsync(context.Dropbox, "Old");
                var saved = Save(context, local, remote);
                await context.Local.EnsureFolderAsync("Archive", default);
                await context.Dropbox.EnsureFolderAsync("Archive", default);
                var movedLocal = await context.Local.MoveAsync(local, "Archive/Item", default);
                var movedRemote = await context.Dropbox.MoveAsync(remote, "Archive/Item", default);
                if (liveOld is { } side)
                    ((FakeSide)context.For(side)).Seed(side == Side.Local ? local : remote, [1]);
                if (upload)
                {
                    movedLocal = movedLocal with { Size = 2, Modified = DateTimeOffset.UnixEpoch.AddDays(1) };
                    ((FakeSide)context.Local).Seed(movedLocal, [2, 3]);
                }
                var unprotectedRead = new InvalidOperationException("Historical path was not admitted");
                if (!protectedOld) ((FakeSide)context.Local).FailNext(saved.LocalPath, unprotectedRead);
                var action = new SyncAction(upload ? ActionKind.Upload : ActionKind.Record,
                    Row(movedLocal, movedRemote, protectedOld ? saved : null), Target: upload ? movedRemote.Path : null);
                SyncOutcome outcome;
                await using (await PathLocks.Shared.EnterAsync(LockKeys.For(action, context.Pair, null), default))
                    outcome = await NewJobs(context).Actions.ExecuteAsync(action, null, default);
                if (!protectedOld)
                {
                    var untouched = false;
                    try { await context.Local.ReadOneAsync(saved.LocalPath, default); }
                    catch (InvalidOperationException error) when (ReferenceEquals(error, unprotectedRead)) { untouched = true; }
                    Check.Require(untouched, "Out-of-footprint retirement performs no historical provider probe.");
                }
                var destination = store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key("Archive/Item"))!;
                Check.Require(destination.LocalId == saved.LocalId && destination.DropboxId == saved.DropboxId &&
                    outcome.Kind == (upload ? ActionKind.Upload : ActionKind.Record),
                    "Record and upload-update keep both identities at the archive destination.");
                Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) ==
                    (liveOld is null && protectedOld ? null : saved),
                    "Only locked absent old endpoints retire; live endpoints and out-of-footprint history remain.");
                Check.Require(store.SyncedBindingsById(context.Pair.PairId, Side.Local, local.Id).Count ==
                    (liveOld is null && protectedOld ? 1 : 2), "Identity lookup includes every retained binding.");
                if (upload)
                    Check.Require(destination.DropboxContentHash == DropboxContentHash.Compute([2, 3]),
                        "Stale binding retirement does not lose the upload's new content baseline.");
                // A delayed rename candidate after Record must not recreate the retired binding or collide.
                if (liveOld is null)
                {
                    var decision = await NewJobs(context).Items.Renames.RenameAsync(
                        [new RenameCandidate(Side.Local, movedLocal, saved),
                         new RenameCandidate(Side.Dropbox, movedRemote, saved)], movedLocal, default);
                    Check.Require(decision.Kind == (protectedOld ? RenameKind.None : RenameKind.Adopt) &&
                        store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) is null &&
                        store.Issues(context.Pair.PairId).Count == 0,
                        "Rename's admitted old paths safely retire deferred history without a recurring clash.");
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task EditedContentAfterSpellingAdoptionAsync()
    {
        foreach (var editedSide in new[] { Side.Local, Side.Dropbox })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "Item");
            var remote = await PutAsync(context.Dropbox, "Item");
            var saved = Save(context, local, remote) with { DropboxPath = "item" };
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var edited = (editedSide == Side.Local ? local : remote) with
            {
                Size = 2, Modified = DateTimeOffset.UnixEpoch.AddDays(1),
                ContentHash = DropboxContentHash.Compute([2, 3])
            };
            ((FakeSide)context.For(editedSide)).Seed(edited, [2, 3]);
            if (editedSide == Side.Local) local = edited;
            else remote = edited;
            var syncer = NewJobs(context).Items;
            var decision = await syncer.Renames.RenameAsync([new RenameCandidate(Side.Dropbox, remote, saved)], local, default);
            var adopted = store.SyncedById(context.Pair.PairId, Side.Local, local.Id)!;
            Check.Require(decision.Kind == RenameKind.Adopt && adopted.DropboxPath == "Item" &&
                adopted.LocalSize == saved.LocalSize && adopted.DropboxContentHash == saved.DropboxContentHash,
                "Spelling adoption retains the previous content baseline when either provider was edited.");
            var outcome = await syncer.RunRowAttemptAsync(Row(local, remote, adopted), default);
            Check.Require(outcome.Kind == (editedSide == Side.Local ? ActionKind.Upload : ActionKind.Download),
                "The ordinary row reconciles the pending edit after spelling adoption.");
            var destination = context.For(PathNames.Other(editedSide));
            var copied = (await destination.ReadOneAsync("Item", default))!;
            Check.Require(await destination.ContentHashAsync(copied, default) == edited.ContentHash &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, adopted.LocalKey)?.DropboxContentHash == edited.ContentHash &&
                store.Issues(context.Pair.PairId).Count == 0,
                "The edited bytes reach the counterpart and the saved baseline without a clash or hold.");
        }
    }

    // M26/M29: ItemSync.RenameAsync keeps its ordinary path holds and cancellation is never an item failure;
    // LockKeys.ForPaths keeps the same case-insensitive bilateral key set.
    private static async Task RenameWorkflowAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var pair = context.Pair;
        var item = LockKeys.ForPaths(pair, [(Side.Local, "item"), (Side.Dropbox, "item")]);
        var caseVariant = LockKeys.ForPaths(pair, [(Side.Local, "Item"), (Side.Dropbox, "ITEM")]);
        Check.Require(item.Count == 2 && item.Contains(PathLocks.LocalKey(pair, "item")) &&
            item.Contains(PathLocks.DropboxKey(pair, "item")) &&
            new HashSet<string>(item, StringComparer.Ordinal).SetEquals(caseVariant),
            "ForPaths keeps the same bilateral key set for case-variant real paths.");
        var local = await PutAsync(context.Local, "old");
        var saved = SyncedEntries.From(local, local);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await CanceledAsync(() => NewJobs(context).Items.Renames.RenameAsync(
            [new RenameCandidate(Side.Local, local with { Path = "renamed" }, saved)], null, cancellation.Token));
        Check.Require(canceled && await context.Local.ReadOneAsync("old", default) == local &&
            await context.Local.ReadOneAsync("renamed", default) is null,
            "A canceled rename propagates without moving content or reporting an item failure.");
    }

    // RT-03: a Dropbox-origin rename whose Local move is locked registers its retry on the Local counterpart, so
    // the Local CLOSE/change and the next Repair can claim the same lifecycle.
    private static async Task RenameInUseAssociatesLocalAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store) with
        {
            ItemAttempts = new StoreItemAttempts(store, "pair-a", 0)
        };
        var local = await PutAsync(context.Local, "A");
        var remote = await PutAsync(context.Dropbox, "A");
        var saved = SyncedEntries.From(local, remote);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        ((FakeSide)context.Local).FailNext(local.Path,
            new ItemFailedException(local.Path, TextKey.FailLocalInUse, null));
        var decision = await NewJobs(context).Items.Renames.RenameAsync(
            [new RenameCandidate(Side.Dropbox, remote with { Path = "B" }, saved)], local, default);
        Check.Require(decision.Kind == RenameKind.Hold, "An in-use local rename stays held.");
        var issue = store.Issues(context.Pair.PairId).Single(value => value.Kind == IssueKind.Failure);
        Check.Require(issue.Path == local.Path && issue.Cause == TextKey.FailLocalInUse &&
            issue.Subject.Items.Any(item => item.Side == Side.Local && item.Path == local.Path && item.Id == local.Id) &&
            issue.Subject.Items.Any(item => item.Side == Side.Dropbox && item.Path == "B"),
            "A Dropbox-origin rename lock failure is reported on the Local counterpart with both endpoints in the subject.");
        Check.Require(store.ReadItemRetryForSubject(context.Pair.PairId, context.ExpectedGeneration, local.Path,
                issue.Subject) is not null,
            "The in-use lifecycle is keyed to the Local file so its CLOSE and next Repair can claim it.");
        Check.Require(store.Issues(context.Pair.PairId).Count(value => value.Kind == IssueKind.Failure) == 1,
            "The Local lock keeps one lifecycle, not a second Dropbox-keyed issue.");
    }

    // P6 phase 1: a listing upload that becomes a tie at the current comparison time is kept apart, never a
    // stale upload over the other side's newer copy.
    private static async Task TieKeepBothAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var time = DateTimeOffset.UnixEpoch.AddDays(2);
        var localBefore = await PutAsync(context.Local, "item", [1], modified: time.AddHours(-2));
        var remote = await PutAsync(context.Dropbox, "item", [2], modified: time.AddHours(-2));
        var saved = Save(context, localBefore, remote);
        // A size change proves the local edit without a hash, so the listing decides the upload directly.
        var local = await PutAsync(context.Local, "item", [3, 4], localBefore, time);
        var row = Row(local, remote, saved);
        Check.Require(Decide.For(row, FirstRunPolicy.NewestWins, CompareAt, localContentDirty: false).Kind == ActionKind.Upload,
            "The listing sees the local copy as newer and decides an upload.");
        using var cancellation = new CancellationTokenSource();
        var hold = await PathLocks.Shared.EnterAsync(
            [PathLocks.LocalKey(context.Pair, local.Path), PathLocks.DropboxKey(context.Pair, remote.Path)],
            cancellation.Token);
        Task<SyncOutcome>? running = null;
        try
        {
            running = NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
            // A real conflicting edit with the same size and comparison time makes the locked re-decision a tie.
            await PutAsync(context.Dropbox, remote.Path, [8, 9], remote, time);
        }
        finally
        {
            await hold.DisposeAsync();
        }
        var result = await running!;
        Check.Require(result.Kind == ActionKind.KeepBoth && result.Effect == EffectKind.KeptBoth,
            "A current-time tie at the locked re-decision keeps both divergent copies instead of uploading over one.");
    }

    // D12 / F107 Q1-Q2: a Preserve folder consumes an already settled child barrier without replaying history,
    // while retained and unfinished folder evidence cannot complete it early.
    private static async Task FirstRunFolderAsync()
    {
        var preserved = Facts() with
        {
            FirstRun = true, FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice
        };
        var folder = new Entry("folder", true, 0, DateTimeOffset.UnixEpoch, "folder-id", "", "", null);
        var row = new Row(PathNames.Key(folder.Path), folder, null, null, [], null, preserved);
        var direct = Decide.For(row, FirstRunPolicy.NewestWins, CompareAt, localContentDirty: false);
        Check.Require(direct.Kind == ActionKind.DeferFolder,
            "A complete first-run Preserve folder with no Dropbox counterpart defers to the folder plan.");
        var action = Decide.ResumePreservedFolder(row, FirstRunPolicy.NewestWins, CompareAt);
        Check.Require(action.Kind == ActionKind.EnsureDropboxFolder &&
            action.InfoAfter == TextKey.InfoDropboxHistoryUnchecked,
            "A settled Preserve folder resumes an EnsureDropboxFolder with the existing history-unchecked notice.");
        var ordinary = Decide.ResumePreservedFolder(
            new Row(PathNames.Key(folder.Path), folder, null, null, [], null, Facts()),
            FirstRunPolicy.NewestWins, CompareAt);
        Check.Require(ordinary.Kind != ActionKind.EnsureDropboxFolder ||
            ordinary.InfoAfter != TextKey.InfoDropboxHistoryUnchecked,
            "A non-Preserve folder receives the ordinary decision, not the retained-history entry.");

        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await context.Local.EnsureFolderAsync("folder", default);
        var remote = await context.Dropbox.EnsureFolderAsync("folder", default);
        Save(context, local, remote);
        var jobs = NewJobs(context);
        await jobs.FirstRunFolders.FinalizeFirstRunFolderAsync(Row(local, null, null), unfinishedChildren: true,
            default, completed: null);
        Check.Require(store.Issues(context.Pair.PairId).Count == 0 &&
            await context.Dropbox.ReadOneAsync("folder", default) == remote,
            "An unfinished first-run folder is held without creating, not acknowledged, or notified early.");
    }

    /// <summary>Runs the operation and reports whether it refused with the live-admission stale exception.</summary>
    private static async Task<bool> RefusedAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return false;
        }
        catch (LiveAdmissionStaleException)
        {
            return true;
        }
    }

    /// <summary>Runs the operation and reports whether cancellation propagated.</summary>
    private static async Task<bool> CanceledAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>A lease that refuses any generation other than the Store's current one and records what it saw.</summary>
    private sealed class StaleLease(long current) : IAdmittedExecution
    {
        public long Seen { get; private set; } = -1;
        public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys)
        {
            Seen = generation;
            if (generation != current) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        }
        public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) =>
            Require(pairId, generation, []);
        public void Revalidate() { }
        public ValueTask ReleaseAdmissionAsync() => throw new NotSupportedException(nameof(ReleaseAdmissionAsync));
        public ValueTask DisposeAsync() => default;
    }

    private static async Task UntouchedRowsAsync()
    {
        foreach (var reason in new[] { NoneReason.Skipped, NoneReason.OutOfScope })
        foreach (var source in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var entry = new Entry("Parent/Child", false, 0, default, "item-id", "", "", null);
            var row = (source == Side.Local ? Row(entry, null, null) : Row(null, entry, null)) with
            {
                Context = Facts() with { Skipped = reason == NoneReason.Skipped, OutOfScope = reason == NoneReason.OutOfScope }
            };
            foreach (var side in Enum.GetValues<Side>())
                ((FakeSide)context.For(side)).FailNext(entry.Path, new InvalidOperationException("Unexpected provider access"));
            var outcome = await NewJobs(context).Items.RunRowAttemptAsync(row, default);
            Check.Require(outcome == new SyncOutcome(ActionKind.None, reason, null, null, false) &&
                ((FakeAdmission)context.Admission).Admitted == 0 && ((FakeAdmission)context.Admission).Completed == 0,
                "Skipped and out-of-scope rows need no provider reads, hashing or writes on either side.");
        }
    }

    private static async Task DropboxRootBatchAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store, "/");
        const string Scope = "Parent";
        var parent = await context.Local.EnsureFolderAsync(Scope, default);
        var child = await PutAsync(context.Local, Scope + "/Child");
        context = context with { BatchScope = Scope };
        var token = store.BeginScan(context.Pair.PairId, 0, false, Scope);
        foreach (var side in Enum.GetValues<Side>())
        {
            var sink = store.OpenScan(context.Pair.PairId, side, ScanTestSettings.BatchRows);
            if (side == Side.Local)
            {
                await sink.AddAsync(new Listed(parent.Path, parent), default);
                await sink.AddAsync(new Listed(child.Path, child), default);
            }
            await sink.CompleteAsync();
        }
        await NewSyncer(context).SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
        var issues = store.Issues(context.Pair.PairId);
        Check.Require(await context.Dropbox.ReadOneAsync(child.Path, default) is not null &&
            store.SyncedById(context.Pair.PairId, Side.Local, child.Id) is not null && issues.Count == 0,
            "A batch inside a '/' Dropbox root publishes and records its child, not FailChanged; issues=" +
            string.Join(" | ", issues.Select(issue => issue.Path + ":" + issue.Cause)));
        Console.WriteLine("DropboxRootBatchAsync: admitted and recorded with no FailChanged");
    }

    private static async Task DirectRowsAsync()
    {
        using (var store = new InMemoryStateStore())
        {
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var remote = await PutAsync(context.Dropbox, "item");
            var row = Row(local, remote, Save(context, local, remote));
            FailReads(context, local.Path, remote.Path);
            using var cancellation = new CancellationTokenSource();
            var hold = await PathLocks.Shared.EnterAsync(
                [PathLocks.LocalKey(context.Pair, local.Path), PathLocks.DropboxKey(context.Pair, remote.Path)],
                cancellation.Token);
            Task<SyncOutcome>? running = null;
            bool completed;
            try
            {
                running = NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
                completed = running.IsCompleted;
            }
            finally
            {
                await hold.DisposeAsync();
            }
            var result = await running!;
            Check.Require(completed && result == new SyncOutcome(ActionKind.None, NoneReason.InSync, null, null, false) &&
                ((FakeAdmission)context.Admission).Admitted == 0 && ((FakeAdmission)context.Admission).Completed == 0,
                "An in-sync row runs without its path keys and reads neither provider");
        }

        foreach (var foreignBinding in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var remote = await PutAsync(context.Dropbox, "item");
            // The Record decision needs the local hash; take it before arming the provider failures.
            var row = Row(local, remote, null) with { LocalHash = await context.Local.ContentHashAsync(local, default) };
            SyncedEntry? saved = null;
            if (foreignBinding)
                saved = Save(context, await PutAsync(context.Local, "elsewhere"), remote);
            else
                FailReads(context, local.Path, remote.Path);
            var watermark = store.ReadWorkWatermark();
            var result = await NewJobs(context).Items.RunRowAttemptAsync(row, CancellationToken.None);
            // DESIGN-126-4 §1: this crossed binding qualifies; equal returning-original content records after re-observation.
            Check.Require(result == new SyncOutcome(ActionKind.Record, null, EffectKind.Recorded, null, false) &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) == SyncedEntries.From(local, remote),
                "A direct Record avoids provider reads; a stale Record reobserves and adopts equal returning-original content.");
            Check.Require(store.ReadWorkWatermark() == watermark + (foreignBinding ? 1 : 0) &&
                (!foreignBinding || store.SyncedByKey(context.Pair.PairId, Side.Local, saved!.LocalKey) is null),
                "Only the returning-original replacement retires and releases its former partner.");
            Check.Require(((FakeAdmission)context.Admission).Admitted == (foreignBinding ? 1 : 0) &&
                ((FakeAdmission)context.Admission).Completed == 0,
                "Only stale Record re-observation admits; a provider-free Record never admits or completes a Dropbox effect.");
        }
    }

    private static void FailReads(ExecutorContext context, string localPath, string dropboxPath)
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            var path = side == Side.Local ? localPath : dropboxPath;
            ((FakeSide)context.For(side)).FailNext(path, new ItemFailedException(path,
                side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed, null));
        }
    }

    // Both sides were absent at the listing, then different versions reappear before the action runs.
    // Forgetting the record would make the next run newest-wins over two separate versions instead of keep-both.
    private static async Task ReappearedVersionsAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "item");
        var remote = await PutAsync(context.Dropbox, "item");
        var saved = Save(context, local, remote);
        await context.Local.DeleteAsync(local, null, CancellationToken.None);
        await context.Dropbox.DeleteAsync(remote, null, CancellationToken.None);
        var row = Row(null, null, saved);
        Check.Require(Decide.For(row, FirstRunPolicy.NewestWins, CompareAt, localContentDirty: false).Kind == ActionKind.ForgetOnly,
            "Two absent sides decide ForgetOnly from the listing");
        using var cancellation = new CancellationTokenSource();
        var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, local.Path)], cancellation.Token);
        Task<SyncOutcome>? running = null;
        try
        {
            running = NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
            Check.Require(!running.IsCompleted, "The record is re-read under its lock before it is forgotten");
            await PutAsync(context.Local, local.Path, [2, 2, 2], modified: local.Modified);
            await PutAsync(context.Dropbox, remote.Path, [9, 9], modified: remote.Modified);
        }
        finally
        {
            await hold.DisposeAsync();
        }
        var result = await running!;
        Check.Require(result.Kind == ActionKind.KeepBoth && result.Effect == EffectKind.KeptBoth,
            "Versions that reappeared before execution are kept apart, not forgotten");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) is not null,
            "The reappeared file still has a record");
    }

    // A folder row deferred from the listing must not reach the folder plan once the folder and its unchanged
    // child are back locally; the stale plan would classify the child Removed and delete it.
    private static async Task StaleDeferralAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var localFolder = await context.Local.EnsureFolderAsync("folder", CancellationToken.None);
        var remoteFolder = await context.Dropbox.EnsureFolderAsync("folder", CancellationToken.None);
        var folderBinding = Save(context, localFolder, remoteFolder);
        var localKept = await PutAsync(context.Local, "folder/kept");
        var remoteKept = await PutAsync(context.Dropbox, "folder/kept");
        Save(context, localKept, remoteKept);
        var localEdited = await PutAsync(context.Local, "folder/edited");
        var remoteEdited = await PutAsync(context.Dropbox, "folder/edited");
        Save(context, localEdited, remoteEdited);
        await PutAsync(context.Dropbox, "folder/edited", [7, 7], remoteEdited, remoteEdited.Modified.AddSeconds(1));
        var row = Row(null, remoteFolder, folderBinding);
        Check.Require(Decide.For(row, FirstRunPolicy.NewestWins, CompareAt, localContentDirty: false).Kind == ActionKind.DeferFolder,
            "A folder absent locally and unchanged on Dropbox decides DeferFolder from the listing");
        var restored = new HashSet<string> { PathNames.Key(localEdited.Path) };
        var result = await NewJobs(context).Items.RunRowAttemptAsync(row, CancellationToken.None);
        Check.Require(result == new SyncOutcome(ActionKind.None, NoneReason.InSync, null, null, false),
            "A folder and its child restored locally before execution are re-observed as in sync, not deferred");
        // Repair runs the plan only for a deferred folder; anything else must leave the restored child alone.
        if (result.Deferred)
        {
            var folders = NewJobs(context).Items.Folders;
            var id = await folders.PrepareFolderRemovalAsync(row, Side.Local, restored,
                new HashSet<string> { PathNames.Key(localFolder.Path) }, CancellationToken.None);
            if (id is not null)
            {
                await folders.RunPreparedFolderRemovalsAsync([id], CancellationToken.None);
                await folders.FinishFolderRemovalAsync(id, null, CancellationToken.None);
            }
        }
        Check.Require(await context.Local.ReadOneAsync(localKept.Path, CancellationToken.None) == localKept,
            "A stale deferral never reaches the folder plan that would delete the restored child");
    }

    // A tie computed from the listing must be re-observed: a newer reliable edit that removes it must not leave a hold.
    private static async Task StaleTieAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "item");
        var remote = await PutAsync(context.Dropbox, "item", [3, 3]);
        var row = Row(local, remote, null);
        Check.Require(Decide.For(row, FirstRunPolicy.NewestWins, CompareAt, localContentDirty: false) is
                { Kind: ActionKind.Notice, Cause: TextKey.TieEqualTimes },
            "A same-second pair of different sizes decides a Tie notice from the listing");
        using var cancellation = new CancellationTokenSource();
        var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, local.Path)], cancellation.Token);
        Task<SyncOutcome>? running = null;
        try
        {
            running = NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
            Check.Require(!running.IsCompleted, "A Tie notice waits for its lock before it is raised");
            local = await PutAsync(context.Local, local.Path, [5], local, local.Modified.AddHours(1));
        }
        finally
        {
            await hold.DisposeAsync();
        }
        var result = await running!;
        Check.Require(result.Kind == ActionKind.Upload &&
            store.Issues(context.Pair.PairId).All(issue => issue.Kind != IssueKind.Tie),
            "A newer reliable edit re-observed before the notice uploads it instead of raising a lasting Tie hold");
        Check.Require(await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) is { } current &&
            current.ContentHash == await context.Local.ContentHashAsync(local, CancellationToken.None),
            "The edit that removed the tie is uploaded");
    }

    private static async Task UploadFreshnessAsync()
    {
        foreach (var failing in new[] { Side.Local, Side.Dropbox })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var target = new Parents(context).Destination(Side.Dropbox, local.Path);
            var failedPath = failing == Side.Local ? local.Path : target;
            ((FakeSide)context.For(failing)).FailNext(failedPath, new ItemFailedException(failedPath,
                failing == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed, null));
            var result = await NewJobs(context).Items.RunRowAttemptAsync(Row(local, null, null),
                CancellationToken.None);
            Check.Require(result.Raised == IssueKind.Failure && result.Effect is null &&
                await context.Dropbox.ReadOneAsync(target, CancellationToken.None) is null,
                "An upload row still re-reads both sides inside its lock before writing");
        }
    }

    private static async Task SpentRestoreAsync()
    {
        foreach (var complete in new[] { false, true })
        foreach (var sameIdentity in new[] { false, true })
        foreach (var folder in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store) with
            {
                ReadDeletionEvidenceAsync = (_, _, _) => throw new InvalidOperationException("Spent history requested deletion proof.")
            };
            var parent = await context.Local.EnsureFolderAsync("Parent", default);
            var local = folder ? await context.Local.EnsureFolderAsync("Parent/Child", default) :
                await PutAsync(context.Local, "Parent/Child");
            var token = store.BeginScan(context.Pair.PairId, 0, true);
            var sink = store.OpenScan(context.Pair.PairId, Side.Dropbox, 1);
            await sink.AddAsync(new Listed(parent.Path, null), default);
            await sink.CompleteAsync();
            if (complete) store.SaveDeletedHistory(context.Pair.PairId, token, "cursor");
            var jobs = NewJobs(context);
            var rows = jobs.Items.ScanContexts(true, null, null, key => store.DeletedNameExact(context.Pair.PairId, key));
            var row = Row(local, null, null);
            row = row with { Context = rows.For(row) with { CompleteInventory = complete } };
            Check.Require(row.Context.DeletionEligible, "Positive ancestor initially selects the first-run item.");
            store.Commit(context.Pair.PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, PathNames.Key(local.Path))] });
            if (!sameIdentity)
            {
                local = local with { Id = "restored-identity" };
                ((FakeSide)context.Local).Seed(local, folder ? null : [1]);
            }
            if (!folder)
            {
                var action = new SyncAction(ActionKind.DeleteLocal, row, Target: local.Path);
                var keys = LockKeys.For(action, context.Pair, null);
                await using var held = await PathLocks.Shared.EnterAsync(keys, default);
                var stale = await jobs.Items.Admission.ExecuteLockedAsync(action, null, keys, keys, default, null, null);
                Check.Require(stale.Reobserve && stale.Outcome is null,
                    "A prepared destructive action is invalidated after locking when its eligibility was spent.");
            }
            var refreshed = await jobs.Items.Preparation.ReobserveAsync(row, new Observer(context, new Parents(context)), default);
            Check.Require(!refreshed.Context.DeletionEligible, "Exact spend overrides ancestor and owed history for both restore identities.");
            var sibling = Row(local with { Path = "Parent/Sibling" }, null, null);
            Check.Require(rows.For(sibling).DeletionEligible, "A spent child leaves unrelated ancestor-matching siblings eligible.");
            var outcome = folder
                ? await jobs.FirstRunFolders.FinalizeFirstRunFolderAsync(row, false, default, null)
                : await jobs.Items.RunRowAttemptAsync(refreshed, default);
            Check.Require(outcome.Raised is null && await context.Dropbox.ReadOneAsync(local.Path, default) is not null &&
                await context.Local.ReadOneAsync(local.Path, default) is not null,
                "Restored spent files and folders sync outward instead of being re-deleted.");
        }
    }

    private static async Task RemovedThenRestoredAsync()
    {
        foreach (var bound in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item", [1]);
            SyncedEntry? saved = null;
            if (bound)
            {
                var remote = await PutAsync(context.Dropbox, local.Path, [1]);
                saved = Save(context, local, remote);
                await context.Dropbox.DeleteAsync(remote, null, default);
            }
            var hash = await context.Local.ContentHashAsync(local, default);
            context = context with
            {
                ReadDeletionEvidenceAsync = (path, _, _) => Task.FromResult<DeletionEvidence>(
                    new DeletionEvidence.DeletedFile(path, "domain", "remote", null,
                        local.Modified.AddDays(1), null, hash, null))
            };
            var row = Row(local, null, saved) with
            {
                Context = Facts() with { FirstRun = true, CompleteInventory = true, DeletionEligible = !bound }
            };
            var removed = await NewJobs(context).Items.RunRowAttemptAsync(row, default);
            Check.Require(removed.Kind == ActionKind.DeleteLocal && await context.Local.ReadOneAsync(local.Path, default) is null &&
                store.DeletedNameSpent(context.Pair.PairId, PathNames.DropboxKey(local.Path)),
                "Both propagated and first-run deletion spend eligibility with their successful removal commit.");
            ((FakeSide)context.Local).Seed(local, [1]);
            var restored = Row(local, null, null) with { Context = row.Context with { DeletionEligible = true } };
            var guarded = context with
            {
                ReadDeletionEvidenceAsync = (_, _, _) => throw new InvalidOperationException("Already handled history was checked twice.")
            };
            var outcome = await NewJobs(guarded).Items.RunRowAttemptAsync(restored, default);
            Check.Require(outcome.Kind == ActionKind.Upload && outcome.Raised is null &&
                await context.Local.ReadOneAsync(local.Path, default) is not null,
                "Restored old bytes and identity upload without a second history proof or removal.");
        }
    }

    private static async Task FirstRunContentAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "item", [1], modified: DateTimeOffset.UnixEpoch.AddHours(1));
        var remote = await PutAsync(context.Dropbox, "item", [1]);
        var row = Row(local, remote, null) with { Context = Facts() with { FirstRun = true } };
        var result = await NewJobs(context).Items.RunRowAttemptAsync(row, CancellationToken.None);
        Check.Require(result.Kind == ActionKind.Record && result.Effect == EffectKind.Recorded && result.Raised is null,
            "A first-run equal-size pair with identical content is recorded, not transferred");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) is not null &&
            store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.Key(remote.Path)) is not null &&
            ((FakeAdmission)context.Admission).Admitted == 0,
            "The first-run record starts no transfer");
        Check.Require(await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local &&
            await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) == remote,
            "The first-run record leaves both files untouched");
    }

    private static async Task CopiesAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var folder in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var source = folder ? await context.For(side).EnsureFolderAsync("item", CancellationToken.None)
                : await PutAsync(context.For(side), "item");
            var outcome = await NewJobs(context).Items.SyncPathAsync(side, source.Path, CancellationToken.None);
            var other = side == Side.Local ? Side.Dropbox : Side.Local;
            var target = await context.For(other).ReadOneAsync(source.Path, CancellationToken.None);
            Check.Require(target is not null && target.IsFolder == folder && outcome.Raised is null,
                "SyncPath observes and copies a new file or folder in either direction");
            Check.Require(store.SyncedByKey(context.Pair.PairId, side, PathNames.Key(source.Path)) is not null,
                "A successful path copy records the binding");
            var admission = (FakeAdmission)context.Admission;
            Check.Require(admission.Admitted == 1 && admission.Completed == 1,
                "SyncPath admits before observation; its inner action adds no second permit and completes one effect.");
        }

        using var mappedStore = new InMemoryStateStore();
        var mapped = Context(mappedStore);
        var localParent = await mapped.Local.EnsureFolderAsync("mapped_", CancellationToken.None);
        var remoteParent = await mapped.Dropbox.EnsureFolderAsync("mapped:", CancellationToken.None);
        Save(mapped, localParent, remoteParent);
        var child = await PutAsync(mapped.Local, "mapped_/child");
        await NewJobs(mapped).Items.RunRowAttemptAsync(Row(child, null, null), CancellationToken.None);
        Check.Require(await mapped.Dropbox.ReadOneAsync("mapped:/child", CancellationToken.None) is not null,
            "Run step zero uses the linked parent's destination spelling");
    }

    private static async Task ResolutionFollowUpsAsync()
    {
        foreach (var mode in new[] { "held", "ignored", "mapped" })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            SyncedEntry? parent = null;
            if (mode == "mapped")
                parent = Save(context, await context.Local.EnsureFolderAsync("mapped_", default),
                    await context.Dropbox.EnsureFolderAsync("mapped:", default));
            var local = await PutAsync(context.Local, mode == "mapped" ? "mapped_/item" : "item");
            var remote = await PutAsync(context.Dropbox, mode == "mapped" ? "taken" : "item", [2, 3]);
            var subject = new IssueSubject([new(Side.Local, local.Path, local.Id),
                new(Side.Dropbox, remote.Path, remote.Id)], Side.Local);
            store.Commit(context.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, remote.Path, subject)]
            });
            var issue = store.Issues(context.Pair.PairId).Single();
            if (mode == "held")
                store.Commit(context.Pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, local.Path, subject)]
                });
            if (mode == "ignored")
            {
                var pair = context.Pair with { IgnoreRules = ["FileName:item"] };
                store.SavePair(pair);
                context = context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
            }
            var effects = new List<RunEvent>();
            context = context with { Events = effects.Add };
            // A same-path move leaves both trees unchanged while both real follow-ups evaluate their context.
            var resolution = await NewSyncer(context).ResolveAsync(issue, ResolutionChoice.RenameIncoming,
                mode == "mapped" ? "renamed" : "item", null, default);
            var outcome = resolution.Outcome;
            Check.Require(resolution.Resolved && store.IssueById(context.Pair.PairId, issue.Id) is null && outcome.Raised is null,
                "The resolved clash closes before follow-up holds or scope are evaluated.");
            Check.Require(await context.Dropbox.ReadOneAsync(remote.Path, default) == remote,
                "Resolution follow-ups preserve the original occupant.");
            if (mode == "mapped")
            {
                var moved = local with { Path = "mapped_/renamed" };
                var copied = await context.Dropbox.ReadOneAsync("mapped:/renamed", default);
                Check.Require(await context.Local.ReadOneAsync(moved.Path, default) == moved &&
                    await context.Local.ReadOneAsync(local.Path, default) is null && copied is not null &&
                    copied.ContentHash == await context.Local.ContentHashAsync(moved, default) &&
                    await context.Dropbox.ReadOneAsync(moved.Path, default) is null &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(moved.Path))?.DropboxPath == copied.Path &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, parent!.LocalKey) == parent &&
                    effects.Count(value => value.Kind == RunEventKind.Effect) == 2,
                    "A follow-up retains its mapped parent's saved destination and makes only the intended copy.");
            }
            else
            {
                Check.Require(outcome.Kind == ActionKind.None &&
                    outcome.None == (mode == "held" ? NoneReason.Held : NoneReason.OutOfScope) &&
                    outcome.Effect is null && await context.Local.ReadOneAsync(local.Path, default) == local &&
                    await CountFilesAsync(context.Local) == 1 && await CountFilesAsync(context.Dropbox) == 1 &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) is null &&
                    store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.Key(remote.Path)) is null &&
                    effects.Where(value => value.Kind == RunEventKind.Effect).SequenceEqual(
                        [RunEvent.Effect(EffectKind.MovedLocal, local.Path, local.Path, Side.Local)]) &&
                    ((FakeAdmission)context.Admission).Admitted == 3 &&
                    ((FakeAdmission)context.Admission).Completed == 0 &&
                    (mode == "held" ? store.Issues(context.Pair.PairId) is { Count: 1 } issues &&
                        issues[0].Kind == IssueKind.Failure && issues[0].Cause == TextKey.FailDropboxNoPermission &&
                        issues[0].Path == local.Path && issues[0].Subject == subject
                        : store.Issues(context.Pair.PairId).Count == 0),
                    "Held and ignored follow-ups leave both trees and bindings untouched, with no effect beyond the root move.");
            }
        }
    }

    // RT-01: a resolved folder conflict's descendants take the ordinary scope and hold policy, child by child.
    private static async Task ResolvedFolderChildAsync(bool held)
    {
        const string folder = "Folder";
        const string blocked = folder + "/Blocked";
        const string free = folder + "/Free";
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var occupant = await PutAsync(context.Local, folder);
        var incoming = await context.Dropbox.EnsureFolderAsync(folder, default);
        var child = await PutAsync(context.Dropbox, blocked, [2]);
        await PutAsync(context.Dropbox, free, [3]);
        var subject = new IssueSubject([new(Side.Local, occupant.Path, occupant.Id),
            new(Side.Dropbox, incoming.Path, incoming.Id)], null);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashFileFolder, folder, subject)]
        });
        var issue = store.Issues(context.Pair.PairId).Single();
        if (held)
            store.Commit(context.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, child.Path,
                    new IssueSubject([new(Side.Dropbox, child.Path, child.Id)], null))]
            });
        else
        {
            var pair = context.Pair with { Exclusions = [new Exclusion(Side.Local, blocked, true)] };
            store.SavePair(pair);
            context = context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        }

        var outcome = (await NewSyncer(context).ResolveAsync(issue, ResolutionChoice.KeepDropbox, null, null, default)).Outcome;

        Check.Require((await context.Local.ReadOneAsync(folder, default))?.IsFolder == true &&
            await context.Local.ReadOneAsync(free, default) is not null,
            $"The resolved folder and its independent child are synced: {outcome} " + string.Join(";", store.Issues(context.Pair.PairId).Select(item => item.Path + ":" + item.Cause)));
        Check.Require(await context.Local.ReadOneAsync(blocked, default) is null &&
            store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(blocked)) is null,
            "A child under an exclusion or an existing hold stays uncopied and unbound through the resolved folder.");
    }

    private static async Task FollowUpSessionHoldsAsync()
    {
        foreach (var dependent in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var parent = await context.Local.EnsureFolderAsync("Parent", default);
            var child = await PutAsync(context.Local, "Parent/Child");
            var jobs = NewJobs(context);
            if (dependent) jobs.Items.Rows.HoldDependents(Row(parent, null, null));
            else jobs.Items.Rows.HoldRenamePaths([(Side.Local, parent.Path)]);
            for (var pass = 0; pass < 2; pass++)
            {
                var outcome = await jobs.Items.SyncPathAsync(Side.Local, child.Path, default);
                Check.Require(outcome.None == NoneReason.Held && outcome.Effect is null &&
                    await context.Dropbox.ReadOneAsync(child.Path, default) is null,
                    "Follow-ups keep the session's rename and failed-replacement holds across repeated visits.");
            }
        }
    }

    private static async Task FollowUpReadFailureAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var source = await PutAsync(context.For(side), "MiXeD.txt");
            var effects = new List<RunEvent>();
            context = context with { Events = effects.Add };
            ((FakeSide)context.For(side)).FailNext(source.Path,
                new InventoryIncompleteException(nameof(FollowUpReadFailureAsync), null));
            // Call the follow-up entry directly so ResolutionRunner's own failure mapping cannot mask a regression.
            var outcome = await NewJobs(context).Items.SyncPathAsync(side, source.Path, default);
            var failure = store.Issues(context.Pair.PairId).Single();
            Check.Require(outcome.Raised == IssueKind.Failure && outcome.Effect is null &&
                failure.Kind == IssueKind.Failure && failure.Path == source.Path && failure.Cause == Text.ReadFailedOn(side) &&
                effects.All(value => value.Kind != RunEventKind.Effect) &&
                await context.For(side).ReadOneAsync(source.Path, default) == source &&
                await context.For(PathNames.Other(side)).ReadOneAsync(source.Path, default) is null &&
                store.SyncedByKey(context.Pair.PairId, side, PathNames.Key(source.Path)) is null &&
                ((FakeAdmission)context.Admission).Admitted == 1 && ((FakeAdmission)context.Admission).Completed == 0,
                "An incomplete direct follow-up read becomes its own side-specific Failure without changing content or bindings.");
        }
    }

    private static async Task SavedDestinationsAsync()
    {
        foreach (var missing in Enum.GetValues<Side>())
        foreach (var edited in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            await context.Local.EnsureFolderAsync("left", default);
            await context.Dropbox.EnsureFolderAsync("right", default);
            var local = await PutAsync(context.Local, "left/item");
            var remote = await PutAsync(context.Dropbox, "right/item");
            var saved = Save(context, local, remote);
            await context.For(missing).DeleteAsync(missing == Side.Local ? local : remote, null, default);
            var present = PathNames.Other(missing);
            var entry = present == Side.Local ? local : remote;
            if (edited)
                entry = await PutAsync(context.For(present), entry.Path, [2, 3], entry, entry.Modified.AddSeconds(1));
            var row = present == Side.Local ? Row(entry, null, saved) : Row(null, entry, saved);
            var outcome = await NewJobs(context).Items.RunRowAttemptAsync(row, default);
            IssueKind? expectedNotice = edited ? IssueKind.Info : null;
            Check.Require(outcome.Kind == (edited
                    ? missing == Side.Local ? ActionKind.Download : ActionKind.Upload
                    : present == Side.Local ? ActionKind.DeleteLocal : ActionKind.DeleteDropbox) && outcome.Raised == expectedNotice,
                $"A saved one-sided row propagates deletion or restores an edit using its saved destination ({missing}, edited={edited}).");
            var issues = store.Issues(context.Pair.PairId);
            Check.Require(edited
                    ? issues.Count == 1 && issues[0].Kind == IssueKind.Info && issues[0].Cause == TextKey.InfoEditBeatsDelete
                    : issues.Count == 0,
                "Only an edited survivor raises the edit-beats-delete information notice.");
            Check.Require((await context.Local.ReadOneAsync(saved.LocalPath, default) is not null) == edited &&
                (await context.Dropbox.ReadOneAsync(saved.DropboxPath, default) is not null) == edited &&
                await context.Local.ReadOneAsync(saved.DropboxPath, default) is null &&
                await context.Dropbox.ReadOneAsync(saved.LocalPath, default) is null,
                "Saved cross-mapped paths remain authoritative; no default-spelling copy is created.");
            // A restored Local edit is a direct upload: it takes the upload admission, not an ordinary permit.
            var direct = edited && present == Side.Local;
            var admission = (FakeAdmission)context.Admission;
            Check.Require(admission.Admitted == (direct ? 0 : 1) && admission.StageAcquired == (direct ? 1 : 0) &&
                admission.Completed == (!direct && (edited || present == Side.Dropbox) ? 1 : 0),
                "Local deletion admits for Dropbox freshness without adding a Dropbox effect completion.");
        }
    }

    private static async Task RecreatedAsync()
    {
        foreach (var missing in new[] { Side.Local, Side.Dropbox })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var remote = await PutAsync(context.Dropbox, "item");
            var saved = Save(context, local, remote);
            var snapshot = Row(missing == Side.Local ? null : local, missing == Side.Dropbox ? null : remote, saved);
            var result = await NewJobs(context).Items.RunRowAttemptAsync(snapshot, CancellationToken.None);
            Check.Require(result.Raised is null && result.Effect is not (EffectKind.DeletedLocal or EffectKind.DeletedDropbox),
                "A file present again after the listing does not propagate the old deletion");
            Check.Require(await context.Local.ReadOneAsync("item", CancellationToken.None) == local &&
                await context.Dropbox.ReadOneAsync("item", CancellationToken.None) == remote,
                "Re-observation preserves both recreated-file copies");
        }
    }

    private static async Task DirtyArrivalBeforeEffectAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirty-arrival-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = StateStore.Open(Path.Combine(directory, "state.db"));
            var context = Context(store);
            var local = await PutAsync(context.Local, "item", [1]);
            var remote = await PutAsync(context.Dropbox, "item", [1]);
            var saved = Save(context, local, remote);
            local = await PutAsync(context.Local, "item", [2], local, local.Modified.AddSeconds(1));
            var jobs = NewJobs(context);
            var items = jobs.Items;
            var prepared = await items.Preparation.PrepareAsync(Row(local, remote, saved),
                CancellationToken.None);
            Check.Require(prepared.Kind == ActionKind.Upload, "the initial clean proof prepares an upload");
            var source = store.ReadSource(context.Pair.PairId, Side.Local)!.Value;
            var domain = context.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var position = store.ReadJournal(domain)!;
            Check.Require(store.AcquireJournalPage(domain, position, position,
                [(context.Pair.PairId, context.ExpectedGeneration, source.Epoch, source.Revision, false)],
                [(context.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, local.Path, null,
                    local.Id, local, true, false))]) is not null, "arrival commits after initial proof");
            var keys = LockKeys.For(prepared, context.Pair, null);
            await using var hold = await PathLocks.Shared.EnterAsync(keys, CancellationToken.None);
            var result = await items.Admission.ExecuteLockedAsync(prepared, null, keys, keys, CancellationToken.None, null, null);
            Check.Require(result.Reobserve && result.Outcome is null,
                "under-lock changed dirty proof refuses the prepared effect");
            Check.Require(await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) == remote &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved,
                "refused effect changes neither remote content nor saved binding");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task HashFreshnessAsync()
    {
        foreach (var changeSize in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var remote = await PutAsync(context.Dropbox, "item");
            var saved = Save(context, local, remote);
            // A local edit (not a same-content metadata refresh, which records without reading entries) reaches the lock.
            local = await PutAsync(context.Local, "item", [3], local, local.Modified.AddSeconds(1));
            using var cancellation = new CancellationTokenSource();
            var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, local.Path)], cancellation.Token);
            Task<SyncOutcome>? running = null;
            Entry? changed = null;
            try
            {
                running = NewJobs(context).Items.RunRowAttemptAsync(Row(local, remote, saved), cancellation.Token);
                Check.Require(!running.IsCompleted, "Hashing reaches the held row lock before execution");
                changed = await PutAsync(context.Local, "item", changeSize ? [2, 3] : [2], local,
                    local.Modified.AddSeconds(1));
            }
            finally
            {
                await hold.DisposeAsync();
            }
            var result = await running!;
            var current = await context.Dropbox.ReadOneAsync("item", CancellationToken.None);
            Check.Require(result.Kind == ActionKind.Upload && current is not null &&
                current.ContentHash == await context.Local.ContentHashAsync(changed!, CancellationToken.None),
                "A size or time change between hashing and the lock discards the old hash and uploads the edit");
        }
    }

    // Old-app pattern 3 (1 false copy, a phantom): the listing showed a different local file, so the row decided
    // KeepBoth; the freshness read then sees the unchanged recorded file and only the Dropbox edit is downloaded.
    private static async Task OldAppPatternThreeAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "ledger.json", [1, 2, 3, 4]);
        var remote = await PutAsync(context.Dropbox, "ledger.json", [1, 2, 3, 4]);
        var saved = Save(context, local, remote);
        remote = await PutAsync(context.Dropbox, "ledger.json", [7, 7], remote, remote.Modified.AddSeconds(1));
        var phantom = new Entry("ledger.json", false, 9, local.Modified.AddDays(-30), "ghost-id", "", "phantom", null);
        var row = Row(phantom, remote, saved);
        Check.Require(Decide.For(row, FirstRunPolicy.NewestWins, CompareAt, localContentDirty: false).Kind == ActionKind.KeepBoth,
            "Old-app pattern 3: the stale listing decides KeepBoth before execution");
        var result = await NewJobs(context).Items.RunRowAttemptAsync(row, CancellationToken.None);
        Check.Require(result.Kind == ActionKind.Download && result.Effect == EffectKind.Downloaded &&
            result.Raised is null,
            "Old-app pattern 3: freshness re-observation downloads the Dropbox edit instead of keeping both");
        var current = await context.Local.ReadOneAsync(local.Path, CancellationToken.None);
        Check.Require(current is not null &&
            await context.Local.ContentHashAsync(current, CancellationToken.None) == remote.ContentHash,
            "Old-app pattern 3: the local file becomes the Dropbox edit's content");
        Check.Require(await CountFilesAsync(context.Local) == 1 && await CountFilesAsync(context.Dropbox) == 1,
            "Old-app pattern 3: no conflict copy is created on either side");
    }

    private static async Task<int> CountFilesAsync(ISide side)
    {
        var count = 0;
        await foreach (var listed in side.ReadAllAsync("", new ListOptions(true, false), CancellationToken.None))
            if (listed.Entry is { IsFolder: false }) count++;
        return count;
    }

    private static async Task NewlyLinkedAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "left");
            var remote = await PutAsync(context.Dropbox, "right");
            var free = side == Side.Local ? Row(local, null, null) : Row(null, remote, null);
            var saved = Save(context, local, remote);
            var result = await NewJobs(context).Items.RunRowAttemptAsync(free, CancellationToken.None);
            Check.Require(result.Kind == ActionKind.None && result.None == NoneReason.InSync,
                "A formerly free row observes its new cross-mapped binding instead of copying twice");
            Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
                ((FakeAdmission)context.Admission).Admitted == 1 && ((FakeAdmission)context.Admission).Completed == 0,
                "The stale copy admits before re-observation, but discovering the existing link completes no effect.");
        }
    }

    private static async Task FreeIncomingAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var bindingOnSource in new[] { false, true })
        foreach (var stale in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var source = (FakeSide)context.For(side);
            var other = side == Side.Local ? Side.Dropbox : Side.Local;
            var target = context.For(other);
            var linkedSource = await PutAsync(source, bindingOnSource ? "item" : "elsewhere", [9]);
            var linkedTarget = await PutAsync(target, bindingOnSource ? "elsewhere" : "item", [9]);
            var occupant = bindingOnSource ? await PutAsync(target, "item", [8]) : linkedTarget;
            var incoming = await PutAsync(source, "item", [2], bindingOnSource ? linkedSource : null);
            incoming = incoming with { Id = "incoming" };
            source.Seed(incoming, [2]);
            var free = side == Side.Local ? Row(incoming, null, null) : Row(null, incoming, null);
            using var cancellation = new CancellationTokenSource();
            var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, "item")], cancellation.Token);
            Task<SyncOutcome>? running = null;
            SyncedEntry? saved = null;
            try
            {
                running = NewJobs(context).Items.RunRowAttemptAsync(free, cancellation.Token);
                Check.Require(!running.IsCompleted, "The free candidate waits before the foreign binding is recorded");
                saved = side == Side.Local ? Save(context, linkedSource, linkedTarget) : Save(context, linkedTarget, linkedSource);
                if (stale)
                {
                    incoming = incoming with { Modified = incoming.Modified.AddSeconds(1) };
                    source.Seed(incoming, [2]);
                }
            }
            finally
            {
                await hold.DisposeAsync();
            }
            var result = await running!;
            // DESIGN-126-4 §1: a returning original now replaces its unchanged crossed near endpoint.
            if (!bindingOnSource)
            {
                Check.Require(result.Succeeded() && result.Kind == (side == Side.Local ? ActionKind.Upload : ActionKind.Download) &&
                    store.Issues(context.Pair.PairId).Count == 0,
                    "An occupied free-incoming target that qualifies reobserves without narrowing or an ordinary-copy notice.");
                var copied = await target.ReadOneAsync("item", CancellationToken.None);
                Check.Require(copied is not null &&
                    await target.ContentHashAsync(copied, CancellationToken.None) ==
                    await source.ContentHashAsync(incoming, CancellationToken.None) &&
                    store.SyncedByKey(context.Pair.PairId, side, PathNames.KeyFor(side, "item"))?.PathOn(other) == "item" &&
                    store.ReadWorkWatermark() > 0,
                    "The returning content and same-name binding commit together with durable partner release.");
                continue;
            }
            Check.Require(result.Raised == IssueKind.NameClash && result.Effect is null &&
                result.Kind == (side == Side.Local ? ActionKind.Upload : ActionKind.Download),
                "A foreign binding leaves a free candidate's create to meet PathOccupied, even after re-observation");
            var issue = store.Issues(context.Pair.PairId).Single();
            Check.Require(issue.Cause == TextKey.ClashNameTaken && issue.Subject.Incoming == side &&
                issue.Subject.Items.Count == 2 &&
                issue.Subject.Items.Contains(new IssueItem(side, incoming.Path, incoming.Id)) &&
                issue.Subject.Items.Contains(new IssueItem(other, occupant.Path, occupant.Id)),
                "The clash identifies only the incoming entity and the actual target occupant");
            Check.Require(await source.ReadOneAsync(incoming.Path, CancellationToken.None) == incoming &&
                await target.ReadOneAsync(occupant.Path, CancellationToken.None) == occupant &&
                await target.ReadOneAsync(linkedTarget.Path, CancellationToken.None) == linkedTarget &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, saved!.LocalKey) == saved &&
                store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) == saved,
                "The failed create preserves the candidate, occupant, mapped counterpart and existing binding");
            // A fresh upload stays direct: it takes the upload admission and reports no ordinary completion.
            var direct = side == Side.Local && !stale;
            Check.Require(((FakeAdmission)context.Admission).Admitted == (direct ? 0 : 1) &&
                ((FakeAdmission)context.Admission).Completed == 0,
                $"The occupied target is rejected before an effect, without an extra completion: {side} {stale}");
        }
    }

    private static async Task ReturningOriginalsAsync()
    {
        foreach (var far in new[] { Side.Local, Side.Dropbox })
        foreach (var (nearByte, farByte, expected) in new[]
        {
            (1, 2, far == Side.Local ? ActionKind.Upload : ActionKind.Download),
            (1, 1, ActionKind.Record),
            (2, 1, far == Side.Local ? ActionKind.Download : ActionKind.Upload),
            (2, 2, ActionKind.Record),
            (2, 3, ActionKind.KeepBoth)
        })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var nearSide = (FakeSide)context.For(PathNames.Other(far));
            var farSide = (FakeSide)context.For(far);
            var near = await PutAsync(nearSide, "Original", [1]);
            var partner = await PutAsync(farSide, "Partner", [1]);
            var saved = far == Side.Local ? Save(context, partner, near) : Save(context, near, partner);
            if (nearByte != 1)
            {
                var changed = await PutAsync(nearSide, "Original", [(byte)nearByte], near,
                    near.Modified.AddSeconds(1));
                near = changed with { Id = near.Id };
                nearSide.Seed(near, [(byte)nearByte]);
            }
            var returning = await PutAsync(farSide, "Original", [(byte)farByte]);
            var row = far == Side.Local ? Row(returning, near, saved) : Row(near, returning, saved);
            var losingHash = await context.Local.ContentHashAsync(row.Local!, CancellationToken.None);
            RemovalSelection? selected = null;
            context = context with { Local = new ProxySide(context.Local)
            {
                OnPublish = (victim, selection) =>
                {
                    if (victim is not null) selected = selection;
                }
            } };
            var result = await NewJobs(context).Items.RunRowAttemptAsync(row, CancellationToken.None);
            Check.Require(result.Succeeded() && result.Kind == expected,
                $"Returning-original execution preserves the content table: {far}, {nearByte}, {farByte}.");
            if (expected == ActionKind.KeepBoth)
            {
                Check.Require(store.Issues(context.Pair.PairId).Any(issue => issue.Cause == TextKey.InfoKeptBoth),
                    "Divergent edits retain the existing keep-both notice.");
                continue;
            }
            Check.Require(store.Issues(context.Pair.PairId).Count == 0 &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key("Original")) is { } recorded &&
                recorded.LocalPath == "Original" && recorded.DropboxPath == "Original" &&
                store.ReadWorkWatermark() > 0,
                "Copy and equality outcomes bind the original name and durably release the former partner without notice.");
            if (expected == ActionKind.Download)
            {
                var local = row.Local!;
                var ordinary = row with { Synced = SyncedEntries.From(local, row.Dropbox!), LocalHash = losingHash };
                var ordinarySelection = await NewJobs(context).Actions.SelectRemovalAsync(ordinary,
                    Side.Local, local, CancellationToken.None);
                Check.Require(selected is not null && selected.Saved == saved &&
                    selected.Proof == ordinarySelection!.Proof && selected.Proof.ContentHash == losingHash,
                    "Returning-original overwrite forwards the same losing-content proof as an ordinary Local update.");
            }
        }
    }

    private static async Task MappedReobserveAsync()
    {
        foreach (var foreignBinding in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item_");
            // Observation reads the Local name's own Dropbox spelling, so a foreign binding is only re-met at that spelling.
            var remote = await PutAsync(context.Dropbox, foreignBinding ? "item_" : "item:");
            SyncedEntry? saved = null;
            if (foreignBinding)
                saved = Save(context, await PutAsync(context.Local, "elsewhere"), remote);
            var row = Row(local, remote, null);
            if (!foreignBinding) row = row with { Local = local with { Id = "stale" } };
            var watermark = store.ReadWorkWatermark();
            using var cancellation = new CancellationTokenSource();
            var result = await NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
            // DESIGN-126-4 §1: the same-spelling branch is a returning original with equal content, not an unrefreshable row.
            Check.Require(result.Kind == ActionKind.Record && result.Raised is null &&
                store.Issues(context.Pair.PairId).Count == 0,
                "Mapped re-observation settles equal content, including a qualifying returning original, without looping or notice.");
            Check.Require(await context.Local.ReadOneAsync(local.Path, default) == local &&
                await context.Dropbox.ReadOneAsync(remote.Path, default) == remote &&
                ((FakeAdmission)context.Admission).Admitted == (foreignBinding ? 1 : 0) &&
                ((FakeAdmission)context.Admission).Completed == 0,
                "The store-only Record takes no permit and a foreign binding re-observation takes exactly one, without overwriting mapped content or completing a transfer.");
            if (saved is not null)
                Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) ==
                        SyncedEntries.From(local, remote) &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) is null &&
                    store.ReadWorkWatermark() == watermark + 1,
                    "The returning original records the observed endpoints and durably releases the former partner.");
            else
                Check.Require(store.ReadWorkWatermark() == watermark,
                    "An ordinary mapped Record has no crossed partner to release.");
        }
    }

    private static async Task ReobserveFailureAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "item_");
        var partner = await PutAsync(context.Local, "elsewhere");
        var remote = await PutAsync(context.Dropbox, "item_");
        // The binding sits under the other side's key, but its recorded Dropbox identity no longer matches the live
        // file, so observation cannot adopt it and the Record row re-observes to exactly itself.
        var saved = Save(context, partner, remote);
        var replaced = remote with { Id = "replaced", Revision = "replaced-revision" };
        ((FakeSide)context.Dropbox).Seed(replaced, [1]);
        var watermark = store.ReadWorkWatermark();
        var result = await NewJobs(context).Items.RunRowAttemptAsync(Row(local, replaced, null), CancellationToken.None);
        Check.Require(result.Raised == IssueKind.Failure &&
            store.Issues(context.Pair.PairId).Single().Cause == TextKey.FailChanged,
            "An unchanged row that still fails its required re-observation is that item's own Failure, not a silent result.");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) == saved &&
            store.ReadWorkWatermark() == watermark,
            "The item's Failure keeps custody of the crossed binding and releases neither endpoint as work.");
        Check.Require(await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local &&
            await context.Dropbox.ReadOneAsync(replaced.Path, CancellationToken.None) == replaced,
            "Neither live endpoint is overwritten, moved or removed by the re-observation Failure.");
    }

    private static async Task SingleLegKeepBothAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var partner = await PutAsync(context.Local, "Partner", [2]);
        var original = await PutAsync(context.Dropbox, "Original", [3]);
        // A crossed file binding: the Local name "Partner" still records the Dropbox file now spelled "Original".
        var saved = SyncedEntries.From(partner, original) with { DropboxContentHash = "previous-content" };
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var returning = await PutAsync(context.Local, "Original", [4]);
        var row = Row(returning, original, saved) with
        {
            LocalHash = await context.Local.ContentHashAsync(returning, CancellationToken.None)
        };
        Check.Require(Decide.CrossedFar(row.Local, row.Dropbox, saved) == Side.Local &&
            Decide.For(row, FirstRunPolicy.NewestWins, DateTimeOffset.UnixEpoch.AddDays(1), localContentDirty: false).Kind
                == ActionKind.KeepBoth,
            "The fixture is a returning original whose divergent edit decides keep-both.");
        var watermark = store.ReadWorkWatermark();
        var jobs = NewJobs(context);
        var labels = await jobs.KeepBoth.ChooseLabelsAsync(row, CancellationToken.None, Side.Dropbox);
        Check.Require(labels.DropboxLabel is not null && labels.LocalLabel is null,
            "An exclusion keeps only the retained side's leg.");
        store.Commit(context.Pair.PairId, await jobs.KeepBoth.RunAsync(row, labels, CancellationToken.None));
        var copy = await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None);
        Check.Require(copy is not null && await context.Dropbox.ContentHashAsync(copy, CancellationToken.None) ==
                await context.Local.ContentHashAsync(returning, CancellationToken.None),
            "The one kept leg preserves the returning original's exact bytes.");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) is null &&
            store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) is null &&
            store.ReadWorkWatermark() == watermark,
            "Retiring the crossed binding on one leg releases no endpoint, so the far local partner stays unsynced.");
        Check.Require(await context.Local.ReadOneAsync(partner.Path, CancellationToken.None) == partner &&
            await context.Dropbox.ReadOneAsync(original.Path, CancellationToken.None) == original &&
            await context.Local.ReadOneAsync(returning.Path, CancellationToken.None) == returning,
            "No original is overwritten, moved or removed by the one-leg change.");
        var edited = await PutAsync(context.Local, partner.Path, [5], partner, partner.Modified.AddSeconds(1));
        var outcome = await NewJobs(context).Items.SyncPathAsync(Side.Local, edited.Path, CancellationToken.None);
        var arrived = await context.Dropbox.ReadOneAsync(edited.Path, CancellationToken.None);
        Check.Require(outcome.Kind == ActionKind.Upload && outcome.Succeeded() && arrived is not null &&
            await context.Dropbox.ContentHashAsync(arrived, CancellationToken.None) ==
                await context.Local.ContentHashAsync(edited, CancellationToken.None) &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(edited.Path)) is { } rebuilt &&
            rebuilt.DropboxPath == edited.Path,
            "The retained far partner is reached by its change event and converges instead of staying stuck.");
    }

    private static async Task LocksAsync()
    {
        foreach (var differentPair in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var pair = differentPair ? context.Pair with { PairId = "pair-b", ConnectionId = "connection-b" } : context.Pair;
            store.SavePair(pair);
            var otherContext = context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
            using var cancellation = new CancellationTokenSource();
            var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, local.Path)], cancellation.Token);
            Task<SyncOutcome>? waiting = null;
            try
            {
                var jobs = NewJobs(otherContext);
                waiting = jobs.Items.RunRowAttemptAsync(Row(local, null, null), cancellation.Token);
                Check.Require(!waiting.IsCompleted, "The same real file waits even when a second pair owns the action");
                var independent = await PutAsync(context.Local, "other");
                var result = await jobs.Items.RunRowAttemptAsync(Row(independent, null, null), cancellation.Token);
                Check.Require(result.Kind == ActionKind.Upload && !waiting.IsCompleted,
                    "Different row keys run while the same-key action waits");
            }
            finally
            {
                await hold.DisposeAsync();
            }
            Check.Require((await waiting!).Kind == ActionKind.Upload, "Releasing the key admits the waiting row");
        }
    }

    private static async Task LabelRaceAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "item");
        var remote = await PutAsync(context.Dropbox, "item");
        var saved = Save(context, local, remote);
        local = await PutAsync(context.Local, "item", [2], local, local.Modified.AddSeconds(1));
        remote = await PutAsync(context.Dropbox, "item", [3], remote, remote.Modified.AddSeconds(1));
        var row = Row(local, remote, saved);
        var labels = await new KeepBothRunner(context, new Transfers(context)).ChooseLabelsAsync(row, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, local.Path)], cancellation.Token);
        Task<SyncOutcome>? running = null;
        Entry? occupant = null;
        try
        {
            running = NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
            Check.Require(!running.IsCompleted, "KeepBoth chooses its labels before waiting for the row lock");
            occupant = await PutAsync(context.Dropbox, labels.DropboxLabel!, [9], modified: local.Modified);
        }
        finally
        {
            await hold.DisposeAsync();
        }
        Check.Require((await running!).Kind == ActionKind.KeepBoth,
            "A label taken while waiting is recomputed after releasing the first hold");
        var binding = store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path));
        Check.Require(binding is not null && binding.DropboxPath != labels.DropboxLabel &&
            await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None) == occupant &&
            ((FakeAdmission)context.Admission).Admitted == 1 && ((FakeAdmission)context.Admission).Completed == 1,
            "A label with matching size and time but different bytes is neither reused nor overwritten");
    }

    // HI-02: a reusable Dropbox label that becomes skipped while the row waits keeps the former outcome.
    // Preflight still passes it (EX-06 is a move); execution refuses it at the label path, so no replacement
    // label succeeds and the notice is the item's own Failure.
    private static async Task SkippedDropboxLabelRaceAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "item");
        var remote = await PutAsync(context.Dropbox, "item");
        var saved = Save(context, local, remote);
        local = await PutAsync(context.Local, "item", [2], local, local.Modified.AddSeconds(1));
        remote = await PutAsync(context.Dropbox, "item", [3], remote, remote.Modified.AddSeconds(1));
        var row = Row(local, remote, saved);
        var labels = await new KeepBothRunner(context, new Transfers(context))
            .ChooseLabelsAsync(row, CancellationToken.None);
        // Give the chosen Dropbox label the Local original's content, making it reusable on its own.
        await PutAsync(context.Dropbox, labels.DropboxLabel!, [2], modified: local.Modified);
        labels = await new KeepBothRunner(context, new Transfers(context))
            .ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(labels.ReuseDropbox && !labels.ReuseLocal,
            "The raced Dropbox label is reusable by itself and the Local label is still absent.");
        using var cancellation = new CancellationTokenSource();
        var hold = await PathLocks.Shared.EnterAsync([PathLocks.LocalKey(context.Pair, local.Path)], cancellation.Token);
        Task<SyncOutcome>? running = null;
        Entry? occupant = null;
        try
        {
            running = NewJobs(context).Items.RunRowAttemptAsync(row, cancellation.Token);
            Check.Require(!running.IsCompleted, "KeepBoth chooses its Dropbox label before waiting for the row lock");
            var entry = (await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None))!;
            // The label becomes skipped while the row waits, keeping the hash that made it reusable.
            occupant = entry with { Skip = TextKey.SkipOnlineOnly.ToString() };
            ((FakeSide)context.Dropbox).Seed(occupant, [2]);
        }
        finally
        {
            await hold.DisposeAsync();
        }
        var outcome = await running!;
        var issues = store.Issues(context.Pair.PairId);
        Check.Require(outcome.Raised == IssueKind.Failure && outcome.Cause == TextKey.FailChanged &&
            issues is [{ Kind: IssueKind.Failure, Cause: TextKey.FailChanged }] &&
            issues[0].Path == row.Local!.Path,
            "A skipped reusable Dropbox label raced after choice is the item's Failure, not a new label.");
        Check.Require(await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None) == occupant &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            issues.All(issue => issue.Kind != IssueKind.Info),
            "The skipped occupant is untouched, the binding stays and no keep-both replacement succeeds.");
    }

    private static async Task QueuedAdmissionAsync()
    {
        // A pure upload is out: it takes the upload admission, not an ordinary permit.
        foreach (var operation in new[] { "path", "mapped", "delete-local", "keep-both" })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store) with { Admission = Limiter() };
            var local = await PutAsync(context.Local, operation == "mapped" ? "item_" : "item");
            Entry? remote = null;
            SyncedEntry? saved = null;
            if (operation is "delete-local" or "keep-both")
            {
                remote = await PutAsync(context.Dropbox, local.Path);
                saved = Save(context, local, remote);
                if (operation == "delete-local")
                {
                    await context.Dropbox.DeleteAsync(remote, null, default);
                    remote = null;
                }
                else
                {
                    local = await PutAsync(context.Local, local.Path, [2], local, local.Modified.AddSeconds(1));
                    remote = await PutAsync(context.Dropbox, remote.Path, [3], remote, remote.Modified.AddSeconds(1));
                }
            }
            var row = Row(local, remote, saved);
            var probePath = local.Path;
            if (operation == "keep-both")
                probePath = (await new KeepBothRunner(context, new Transfers(context))
                    .ChooseLabelsAsync(row, default)).DropboxLabel!;
            var probe = (FakeSide)(operation == "mapped" || operation == "keep-both" ? context.Dropbox : context.Local);
            if (operation == "mapped") probePath = "";
            var failure = new InvalidOperationException("Observation ran before admission");
            probe.FailNext(probePath, failure);
            using var cancellation = new CancellationTokenSource();
            await using var occupied = await context.Admission.AdmitAsync(false, cancellation.Token);
            var jobs = NewJobs(context);
            var running = operation is "path" or "mapped"
                ? jobs.Items.SyncPathAsync(Side.Local, local.Path, cancellation.Token)
                : jobs.Items.RunRowAttemptAsync(row, cancellation.Token);
            Task<IAsyncDisposable>? locking = null;
            var lockWasFree = false;
            var cancelled = false;
            try
            {
                Check.Require(!running.IsCompleted, "A full limiter queues the outer operation before observation.");
                locking = PathLocks.Shared.EnterAsync(
                    [PathLocks.LocalKey(context.Pair, local.Path), PathLocks.DropboxKey(context.Pair, local.Path)],
                    cancellation.Token);
                lockWasFree = locking.IsCompletedSuccessfully;
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    try { await running; }
                    catch (OperationCanceledException) { cancelled = true; }
                }
                finally
                {
                    if (locking is not null)
                    {
                        try { await using var hold = await locking; }
                        catch (OperationCanceledException) when (!lockWasFree) { }
                    }
                }
            }
            Check.Require(lockWasFree && cancelled && store.Issues(context.Pair.PairId).Count == 0 &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) == saved,
                "Queued cancellation takes no path hold, raises no issue and changes no binding.");
            try
            {
                await probe.ReadOneAsync(probePath, default);
                Check.Require(false, "The queued operation must leave its observation failure unconsumed.");
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        }
    }

    private static AdaptiveLimiter Limiter() => new(1, 1, TimeProvider.System, new FakeNetworkSignal(), _ => { },
        new Massdex.SyncShark.V2.FailureRetryOwner.BackoffSettings(0, 1, 0, 0));

    private static async Task FolderPlansAsync(string root)
    {
        foreach (var bounded in new[] { false, true })
        {
            foreach (var changedKind in new[] { false, true })
            {
                using var store = StateStore.Open(Path.Combine(root, $"fallback-{bounded}-{changedKind}.db"));
                var context = Context(store);
                if (bounded) context = context with { Admission = Limiter() };
                var local = await context.Local.EnsureFolderAsync("folder", default);
                var remote = await context.Dropbox.EnsureFolderAsync("folder", default);
                var saved = Save(context, local, remote);
                if (changedKind)
                {
                    await context.Local.DeleteAsync(local, null, default);
                    local = await PutAsync(context.Local, local.Path);
                }
                using var cancellation = new CancellationTokenSource();
                await NewJobs(context).Items.Folders.RemoveFolderAsync(Row(local, null, saved), Side.Local,
                    cancellation.Token);
                Check.Require(await context.Local.ReadOneAsync(local.Path, default) == local &&
                    await context.Dropbox.ReadOneAsync(remote.Path, default) is { } current &&
                    current.IsFolder == !changedKind && store.Issues(context.Pair.PairId).Count == 0,
                    "A recreated or changed-kind root falls back after releasing the folder lock and permit.");
                if (context.Admission is FakeAdmission admission)
                    Check.Require(admission.Admitted == (changedKind ? 2 : 1) &&
                        admission.Completed == (changedKind ? 1 : 0),
                        "Folder observation owns one permit; only an effectful fallback admits separately.");
            }
            foreach (var side in Enum.GetValues<Side>())
            foreach (var survivor in new[] { false, true })
            {
                using var store = StateStore.Open(Path.Combine(root, $"plan-{bounded}-{side}-{survivor}.db"));
                var context = Context(store);
                if (bounded) context = context with { Admission = Limiter() };
                var local = await context.Local.EnsureFolderAsync("folder", default);
                var remote = await context.Dropbox.EnsureFolderAsync("folder", default);
                var saved = Save(context, local, remote);
                context = side == Side.Local
                    ? context with { Dropbox = new FakeSide(Side.Dropbox) }
                    : context with { Local = new FakeSide(Side.Local) };
                Entry? child = survivor ? await PutAsync(context.For(side), "folder/arrived") : null;
                using var cancellation = new CancellationTokenSource();
                var boundary = new FakeBoundary(context.Admission);
                await NewJobs(context, boundary).Items.Folders.RemoveFolderAsync(
                    Row(side == Side.Local ? local : null, side == Side.Dropbox ? remote : null, saved), side,
                    cancellation.Token);
                Check.Require((await context.For(side).ReadOneAsync("folder", default) is not null) == survivor &&
                    store.Issues(context.Pair.PairId).Count == 0,
                    "The real plan selects DeleteWhole without survivors and DeleteEach with a late arrival.");
                if (child is not null)
                {
                    Check.Require(await context.For(PathNames.Other(side)).ReadOneAsync(child.Path, default) is not null &&
                        store.SyncedByKey(context.Pair.PairId, side, PathNames.KeyFor(side, child.Path)) is not null,
                        "A survivor syncs and records after both the parent permit and lock are released.");
                    // S23-R4: the survivor is reached through the recovery boundary, after the parent work scope
                    // (and, disposed in the same block, its permit and path lock) is released; its lease names its row.
                    var survivorKey = side == Side.Local
                        ? PathLocks.LocalKey(context.Pair, child.Path) : PathLocks.DropboxKey(context.Pair, child.Path);
                    Check.Require(boundary.RecoveryCalls.Any(call =>
                            call.WorkActive == 0 && call.Required.Contains(survivorKey, StringComparer.Ordinal)),
                        "A survivor's recovery callback holds no parent work scope and its lease requires the survivor's key.");
                }
                else
                    Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) is null &&
                        store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) is null,
                        "Whole deletion forgets both sides of the saved root.");
                if (context.Admission is FakeAdmission admission)
                    // One outer permit only: the plan releases it and its lock before the survivor runs, and the
                    // survivor's own admission belongs to the recovery boundary asserted above (engine.md 7.6, S23-R4).
                    Check.Require(admission.Admitted == 1 &&
                        admission.Completed == (survivor || side == Side.Dropbox ? 1 : 0),
                        $"Each folder owns one outer permit, and completion counts stay effect-only: {side} {survivor} {admission.Admitted} {admission.Completed}");
            }
        }
    }

    private static async Task RestoredFolderAsync(string root)
    {
        using var store = StateStore.Open(Path.Combine(root, "restored.db"));
        var context = Context(store);
        var local = await context.Local.EnsureFolderAsync("folder", CancellationToken.None);
        var remote = await context.Dropbox.EnsureFolderAsync("folder", CancellationToken.None);
        var saved = Save(context, local, remote);
        var kept = await PutAsync(context.Local, "folder/kept");
        var restored = await PutAsync(context.Dropbox, "folder/kept");
        Save(context, kept, restored);
        var removed = await PutAsync(context.Local, "folder/removed");
        context = context with { ScanId = store.BeginScan(context.Pair.PairId, 0, false) };
        var scan = store.OpenScan(context.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
        scan.AddAsync(new Listed(kept.Path, kept), default).GetAwaiter().GetResult();
        scan.AddAsync(new Listed(removed.Path, removed), default).GetAwaiter().GetResult();
        await scan.CompleteAsync();
        using var cancellation = new CancellationTokenSource();
        var folders = NewJobs(context).Items.Folders;
        var id = await folders.PrepareFolderRemovalAsync(Row(local, null, saved), Side.Local,
            new HashSet<string> { PathNames.Key(kept.Path) },
            new HashSet<string> { PathNames.Key(local.Path) }, cancellation.Token);
        if (id is not null)
        {
            await folders.RunPreparedFolderRemovalsAsync([id], cancellation.Token);
            await folders.FinishFolderRemovalAsync(id, null, cancellation.Token);
        }
        Check.Require(await context.Local.ReadOneAsync(removed.Path, CancellationToken.None) is null &&
            await context.Local.ReadOneAsync(kept.Path, CancellationToken.None) == kept,
            "A restored child permits the folder plan even though its opposite parent now exists");
        Check.Require(((FakeAdmission)context.Admission).Admitted == 1 &&
            ((FakeAdmission)context.Admission).Completed == 0,
            "Only the outer Local folder observation admits; the already restored survivor adds no permit or completion.");
    }

    private static async Task CrossLinkedRestoredAsync(string root)
    {
        foreach (var mappedRoot in new[] { false, true })
        {
            using var store = StateStore.Open(Path.Combine(root, $"cross-linked-{mappedRoot}.db"));
            var context = Context(store);
            var local = await context.Local.EnsureFolderAsync("folder", default);
            var remote = await context.Dropbox.EnsureFolderAsync(mappedRoot ? "remote:" : "folder", default);
            var saved = Save(context, local, remote);
            var kept = await PutAsync(context.Local, "folder/kept");
            var restored = await PutAsync(context.Dropbox, remote.Path + "/label");
            var binding = Save(context, kept, restored);
            Entry? removed = null;
            if (mappedRoot)
            {
                removed = await PutAsync(context.Dropbox, remote.Path + "/removed");
                context = context with { ScanId = store.BeginScan(context.Pair.PairId, 0, false) };
                var scan = store.OpenScan(context.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows);
                scan.AddAsync(new Listed(removed.Path, removed), default).GetAwaiter().GetResult();
                await scan.CompleteAsync();
            }
            ((FakeSide)context.Local).FailNext(kept.Path, new InvalidOperationException("Unexpected survivor observation"));
            var folders = NewJobs(context).Items.Folders;
            var id = await folders.PrepareFolderRemovalAsync(Row(null, remote, saved) with { Key = saved.LocalKey }, Side.Dropbox,
                new HashSet<string> { binding.LocalKey }, new HashSet<string> { PathNames.Key(local.Path) }, default);
            if (id is not null)
            {
                await folders.RunPreparedFolderRemovalsAsync([id], default);
                await folders.FinishFolderRemovalAsync(id, null, default);
            }
            Check.Require(store.Issues(context.Pair.PairId).Count == 0 &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, binding.LocalKey) == binding &&
                await context.Dropbox.ReadOneAsync(restored.Path, default) == restored &&
                (removed is null || await context.Dropbox.ReadOneAsync(removed.Path, default) is null) &&
                ((FakeAdmission)context.Admission).Admitted == 1 &&
                ((FakeAdmission)context.Admission).Completed == (mappedRoot ? 1 : 0),
                "Canonical Local ancestors justify a mapped Dropbox plan; restored survivors add no observation or permit.");
        }
    }

    private static async Task DeferredSurvivorAsync(string root)
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var reappears in new[] { false, true })
        {
            using var store = StateStore.Open(Path.Combine(root, $"deferred-survivor-{side}-{reappears}.db"));
            var context = Context(store) with { Live = true };
            var local = await context.Local.EnsureFolderAsync("Parent", default);
            var remote = await context.Dropbox.EnsureFolderAsync("Parent", default);
            var saved = Save(context, local, remote);
            var localChild = await context.Local.EnsureFolderAsync("Parent/Child", default);
            var remoteChild = await context.Dropbox.EnsureFolderAsync("Parent/Child", default);
            var binding = Save(context, localChild, remoteChild);
            var deleting = (FakeSide)context.For(side);
            var child = (side == Side.Local ? localChild : remoteChild) with { Id = "replacement-folder" };
            deleting.Seed(child, null);
            context = side == Side.Local ? context with { Dropbox = new FakeSide(Side.Dropbox) }
                : context with { Local = new FakeSide(Side.Local) };
            var boundary = new FakeBoundary(context.Admission, context);
            var jobs = NewJobs(context, boundary);
            var folders = jobs.Items.Folders;
            // Live routes (FolderSync.cs:354/:399) compose this same live builder and durable freeze.
            string id;
            using (var plan = await new FolderPlanBuilder(context).BuildAsync(Row(side == Side.Local ? local : null,
                side == Side.Dropbox ? remote : null, saved), side, new HashSet<string>(), live: true, default))
                id = store.BeginFolderRemoval(context.Pair.PairId, plan, context.Settings.StoragePageSize, default);
            Check.Require(store.PendingFolderFollowUps(context.Pair.PairId, id, -1, 10)
                .Any(item => item.Saved == binding), "replacement identity is frozen as a survivor with its stale binding");
            await folders.RunPreparedFolderRemovalsAsync([id!], default);
            var restored = false;
            boundary.AfterDeferred = async () =>
            {
                if (!reappears || restored) return;
                restored = true;
                await context.For(PathNames.Other(side)).EnsureFolderAsync("Parent", default);
                await context.For(PathNames.Other(side)).EnsureFolderAsync(child.Path, default);
            };
            var outcomes = new List<SyncOutcome>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await folders.FinishFolderRemovalAsync(id!, null, timeout.Token, (_, outcome) => outcomes.Add(outcome));
            Check.Require(!store.FolderRemoval(context.Pair.PairId, id!).Active &&
                !store.PendingFolderFollowUps(context.Pair.PairId, id!, -1, 10).Any() &&
                outcomes.Count > 0 && outcomes.All(outcome => outcome.Succeeded()),
                "deferred survivor dispatch releases the real overlapping recovery locks and reports only final success");
            Check.Require((await deleting.ReadOneAsync(child.Path, default) is not null) == reappears,
                "fresh folder preparation removes the empty replacement, but never a reappeared counterpart");
            if (reappears)
                Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, binding.LocalKey) == binding,
                    "ordinary reappearance fallback retains the stale folder identity (A221)");
            Check.Require(store.Issues(context.Pair.PairId).Count == 0,
                "a successful child plan or reappearance fallback creates no Failure");
        }
    }

    private static async Task RefusedSurvivorAsync(string root)
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var reason in Enum.GetValues<LiveAdmissionStaleReason>().Select(value => (LiveAdmissionStaleReason?)value).Append(null))
        {
            using var store = StateStore.Open(Path.Combine(root, $"refused-survivor-{side}-{reason}.db"));
            var events = new List<RunEvent>();
            var context = Context(store) with { Events = events.Add };
            var local = await context.Local.EnsureFolderAsync("Parent", default);
            var remote = await context.Dropbox.EnsureFolderAsync("Parent", default);
            var saved = Save(context, local, remote);
            context = side == Side.Local ? context with { Dropbox = new FakeSide(Side.Dropbox) }
                : context with { Local = new FakeSide(Side.Local) };
            var deleting = (FakeSide)context.For(side);
            var first = await PutAsync(deleting, "Parent/First");
            var later = await PutAsync(deleting, "Parent/Last");
            var jobs = NewJobs(context);
            var folders = jobs.Items.Folders;
            var id = await folders.PrepareFolderRemovalAsync(Row(side == Side.Local ? local : null,
                side == Side.Dropbox ? remote : null, saved), side, new HashSet<string>(), new HashSet<string>(), default);
            await folders.RunPreparedFolderRemovalsAsync([id!], default);
            Exception refusal = reason is { } stale ? new LiveAdmissionStaleException(stale) : new OperationCanceledException();
            deleting.FailNext(first.Path, refusal);
            Exception? caught = null;
            try { await folders.FinishFolderRemovalAsync(id!, null, default); }
            catch (Exception error) when (error is LiveAdmissionStaleException or OperationCanceledException) { caught = error; }
            var contained = reason == LiveAdmissionStaleReason.RelevantEvidenceChanged;
            Check.Require(contained ? caught is null : ReferenceEquals(caught, refusal),
                "only survivor-local relevant evidence refusal is contained; global invalidations propagate");
            var pending = store.PendingFolderFollowUps(context.Pair.PairId, id!, -1, 10).ToArray();
            Check.Require(pending.Any(item => item.Path == first.Path) &&
                pending.Any(item => item.Path == later.Path) != contained &&
                (await context.For(PathNames.Other(side)).ReadOneAsync(later.Path, default) is not null) == contained &&
                store.Issues(context.Pair.PairId).Count == 0,
                "unstructured refusal stays pending without a fabricated cycle; independent survivors continue only after local refusal");
            if (contained)
                Check.Require(events.Any(value => ReferenceEquals(value.Error, refusal)),
                    "contained refusal remains observable in run output");
            await folders.FinishFolderRemovalAsync(id!, null, default);
            Check.Require(!store.FolderRemoval(context.Pair.PairId, id!).Active,
                "the next ordinary opportunity settles the retained survivor without a new operation");
        }
    }

    private static async Task FailedSurvivorAsync(string root)
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var store = StateStore.Open(Path.Combine(root, $"failed-survivor-{side}.db"));
            var context = Context(store) with { Admission = Limiter() };
            var local = await context.Local.EnsureFolderAsync("folder", default);
            var remote = await context.Dropbox.EnsureFolderAsync("folder", default);
            var saved = Save(context, local, remote);
            context = side == Side.Local ? context with { Dropbox = new FakeSide(Side.Dropbox) }
                : context with { Local = new FakeSide(Side.Local) };
            var child = await PutAsync(context.For(side), "folder/arrived");
            ((FakeSide)context.For(side)).FailNext(child.Path,
                new ItemFailedException(child.Path, Text.ReadFailedOn(side), null));
            var row = Row(side == Side.Local ? local : null, side == Side.Dropbox ? remote : null, saved);
            var jobs = NewJobs(context);
            using var cancellation = new CancellationTokenSource();
            await jobs.Items.Folders.RemoveFolderAsync(row, side,
                cancellation.Token);
            var issue = store.Issues(context.Pair.PairId).Single();
            Check.Require(issue.Kind == IssueKind.Failure && issue.Path == child.Path &&
                issue.Cause == Text.ReadFailedOn(side) &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
                await context.For(side).ReadOneAsync(child.Path, default) == child &&
                await context.For(PathNames.Other(side)).ReadOneAsync(child.Path, default) is null &&
                !Directory.EnumerateFiles(root, "*" + StateSchema.PlanFileSuffix + "*" + StateSchema.DatabaseExtension).Any(),
                "A failed survivor preserves its source and parent binding and releases the plan, lock and permit.");
            await jobs.Items.Folders.RemoveFolderAsync(row, side,
                cancellation.Token);
            Check.Require(store.Issues(context.Pair.PairId).Count == 0 &&
                await context.For(PathNames.Other(side)).ReadOneAsync(child.Path, default) is not null &&
                store.SyncedByKey(context.Pair.PairId, side, PathNames.KeyFor(side, child.Path)) is not null,
                "The next folder operation retries the failed survivor without a retained parent permit or stale plan.");
        }
    }

    private static async Task CounterpartCollisionAsync(string root)
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var present in new[] { false, true })
        {
            var database = Path.Combine(root, "counterpart-collision-" + side + present);
            using var store = StateStore.Open(database);
            var context = Context(store);
            var deleting = context.For(side);
            var other = context.For(PathNames.Other(side));
            SyncedEntry Bind(Entry entry, Entry counterpart) => side == Side.Local
                ? Save(context, entry, counterpart) : Save(context, counterpart, entry);
            var target = await deleting.EnsureFolderAsync("R", default);
            var savedRoot = Bind(target, await other.EnsureFolderAsync("Q", default));
            await deleting.EnsureFolderAsync("U", default);
            await other.EnsureFolderAsync("Z", default);
            var child = await PutAsync(deleting, "R/X");
            var savedChild = Bind(child, await PutAsync(other, "Z/X"));
            var outside = await PutAsync(deleting, "U/X");
            var savedOutside = Bind(outside, await PutAsync(other, "Q/X"));
            if (!present) await deleting.DeleteAsync(child, null, default);
            context = side == Side.Local ? context with { Dropbox = new FakeSide(Side.Dropbox) }
                : context with { Local = new FakeSide(Side.Local) };
            var folders = NewJobs(context).Items.Folders;
            var id = await folders.PrepareFolderRemovalAsync(Row(side == Side.Local ? target : null,
                side == Side.Dropbox ? target : null, savedRoot), side,
                new HashSet<string>(), new HashSet<string>(), default);
            Check.Require(id is not null, "cross-linked deletion prepares its frozen membership");
            var members = store.FolderRemovalItems(context.Pair.PairId, id!, -1, int.MaxValue).ToArray();
            var extra = members.Single(item => item.Saved == savedOutside);
            Check.Require(members.Any(item => item.Saved == savedChild) && extra.Expected is null &&
                extra.EffectKind == FolderEffectKind.None && extra.EffectState == FolderRemovalItem.EffectStatus.None &&
                !extra.FollowUp && !extra.SelectedBranch && PathNames.IsUnderKey(PathNames.KeyFor(side, extra.Path), savedRoot.KeyOn(side)) &&
                extra.Path == (present ? child.Path : PathNames.Child(child.Path, savedOutside.LocalKey)) &&
                !store.ReadyFolderEffects(context.Pair.PairId, id!, -1, int.MaxValue).Any(effect => effect.ItemOrdinal == extra.Ordinal),
                "both saved identities survive collisions; synthetic evidence stays in scope and never becomes a disk or provider effect");
            using (var reopened = StateStore.OpenReadOnly(database)!)
                Check.Require(reopened.FolderRemovalItems(context.Pair.PairId, id!, -1, int.MaxValue)
                    .Single(item => item.Saved == savedOutside) == extra, "synthetic identity is stable across reopen");
            await folders.RunPreparedFolderRemovalsAsync([id!], default);
            await folders.FinishFolderRemovalAsync(id!, null, default);
            var removal = store.FolderRemoval(context.Pair.PairId, id!);
            var rootAfter = await deleting.ReadOneAsync(target.Path, default);
            var outsideAfter = await deleting.ReadOneAsync(outside.Path, default);
            var outsideBinding = store.SyncedByKey(context.Pair.PairId, Side.Local, savedOutside.LocalKey);
            var issueCount = store.Issues(context.Pair.PairId).Count;
            Check.Require(!removal.Active && rootAfter is null && outsideAfter == outside &&
                outsideBinding is null && issueCount == 0,
                "both forget ranges complete without deleting the outside Local file: " +
                $"present={present}, phase={removal.Phase}/{removal.Cause}, rootGone={rootAfter is null}, " +
                $"outsideKept={outsideAfter == outside}, outsideForgotten={outsideBinding is null}, issues={issueCount}, " +
                $"after={(outsideAfter is null ? "null" : $"{outsideAfter.Path}|{outsideAfter.Id}|{outsideAfter.Size}")}, " +
                $"want={outside.Path}|{outside.Id}|{outside.Size}");
        }
    }

    private static async Task CounterpartSelectedBranchAsync(string root)
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var store = StateStore.Open(Path.Combine(root, "counterpart-branch-" + side));
            var context = Context(store);
            var deleting = context.For(side);
            var other = context.For(PathNames.Other(side));
            SyncedEntry Bind(Entry entry, Entry counterpart) => side == Side.Local
                ? Save(context, entry, counterpart) : Save(context, counterpart, entry);
            var target = await deleting.EnsureFolderAsync("R", default);
            var savedRoot = Bind(target, await other.EnsureFolderAsync("Q", default));
            var branch = await deleting.EnsureFolderAsync("R/B", default);
            var savedBranch = Bind(branch, await other.EnsureFolderAsync("Q/C", default));
            await deleting.EnsureFolderAsync("U", default);
            var outside = await PutAsync(deleting, "U/X");
            var savedOutside = Bind(outside, await PutAsync(other, "Q/C/X"));
            var survivor = await PutAsync(deleting, "R/Keep");
            context = side == Side.Local ? context with { Dropbox = new FakeSide(Side.Dropbox) }
                : context with { Local = new FakeSide(Side.Local) };
            var folders = NewJobs(context).Items.Folders;
            var id = await folders.PrepareFolderRemovalAsync(Row(side == Side.Local ? target : null,
                side == Side.Dropbox ? target : null, savedRoot), side, new HashSet<string>(), new HashSet<string>(), default);
            Check.Require(id is not null, "partial non-parallel plan is prepared");
            var members = store.FolderRemovalItems(context.Pair.PairId, id!, -1, int.MaxValue).ToArray();
            Check.Require(members.Single(item => item.Saved == savedBranch).SelectedBranch &&
                members.Single(item => item.Saved == savedOutside) is { Path: "R/B/X", EffectKind: FolderEffectKind.None },
                "outside binding is anchored inside the selected non-parallel branch, not the root-relative sibling");
            await folders.RunPreparedFolderRemovalsAsync([id!], default);
            await folders.FinishFolderRemovalAsync(id!, null, default);
            var settled = store.FolderRemovalItems(context.Pair.PairId, id!, -1, int.MaxValue)
                .Single(item => item.Saved == savedBranch);
            Check.Require(settled.IsTerminal && await deleting.ReadOneAsync(branch.Path, default) is null &&
                await deleting.ReadOneAsync(outside.Path, default) == outside &&
                await deleting.ReadOneAsync(survivor.Path, default) == survivor &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, savedBranch.LocalKey) is null &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, savedOutside.LocalKey) is null &&
                store.Issues(context.Pair.PairId).All(issue => issue.Kind != IssueKind.Failure),
                "non-parallel branch settles both forget ranges while retaining outside content and the survivor");
        }
    }

    private static async Task UnreadableFoldersAsync(string root)
    {
        foreach (var blockedPath in new[] { "Root", "Root/Folder", "Root/Folder/Child", "Unreadable" })
        {
            using var store = StateStore.Open(Path.Combine(root, "unreadable-" + Path.GetRandomFileName()));
            var context = Context(store);
            var local = await context.Local.EnsureFolderAsync("Root/Folder", default);
            var remote = await context.Dropbox.EnsureFolderAsync("Elsewhere", default);
            var saved = Save(context, local, remote);
            await context.Local.EnsureFolderAsync("Unreadable", default);
            var protectedLocal = await PutAsync(context.Local, "Unreadable/Child");
            var protectedRemote = await PutAsync(context.Dropbox, "Elsewhere/Child");
            var protectedBinding = Save(context, protectedLocal, protectedRemote);
            var effects = new List<RunEvent>();
            context = context with { Dropbox = new FakeSide(Side.Dropbox), Events = effects.Add };
            UnreadableMember.Record(store, context.Pair.PairId, blockedPath);
            var row = Row(local, null, saved);
            var jobs = NewJobs(context);
            long? issueId = null;
            foreach (var pass in new[] { 0, 1 })
            {
                await jobs.Items.Folders.RemoveFolderAsync(row, Side.Local,
                    default);
                var issue = store.Issues(context.Pair.PairId).Single(item => item.Kind != IssueKind.Skipped);
                Check.Require(issue.Kind == IssueKind.Failure && issue.Cause == TextKey.ReadLocalFailed &&
                    issue.Path == local.Path && (issueId is null || issue.Id == issueId),
                    $"Unreadable pass {pass} raises one stable, visible temporary Failure, including cross-linked footprints.");
                issueId = issue.Id;
                Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
                    store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) == saved &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, protectedBinding.LocalKey) == protectedBinding &&
                    store.SyncedByKey(context.Pair.PairId, Side.Dropbox, protectedBinding.DropboxKey) == protectedBinding &&
                    await context.Local.ReadOneAsync(local.Path, default) == local &&
                    await context.Local.ReadOneAsync(protectedLocal.Path, default) == protectedLocal &&
                    effects.All(value => value.Kind != RunEventKind.Effect) &&
                    ((FakeAdmission)context.Admission).Admitted == pass + 1 &&
                    ((FakeAdmission)context.Admission).Completed == 0,
                    "Blocked plans preserve every saved key and provider object, with no effect or inner permit.");
            }
            UnreadableMember.Release(store, context.Pair.PairId, blockedPath);
            await jobs.Items.Folders.RemoveFolderAsync(row, Side.Local,
                default);
            Check.Require(await context.Local.ReadOneAsync(local.Path, default) is null &&
                await context.Local.ReadOneAsync(protectedLocal.Path, default) == protectedLocal &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) is null &&
                store.SyncedByKey(context.Pair.PairId, Side.Dropbox, saved.DropboxKey) is null &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, protectedBinding.LocalKey) is null &&
                store.SyncedByKey(context.Pair.PairId, Side.Dropbox, protectedBinding.DropboxKey) is null &&
                store.Issues(context.Pair.PairId).Count == 0 &&
                effects.Count(value => value.Kind == RunEventKind.Effect) == 1,
                "A later readable plan finishes the deletion and both forget ranges, closes Failure, and leaves outside content alone.");
        }
    }

    private static async Task PendingAsync()
    {
        foreach (var outcome in new[] { RemovalOutcome.Removed, RemovalOutcome.NoEffect, RemovalOutcome.Unknown })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "removed", [1]);
            var pending = new PendingEffect(1, "removal", 0, PendingKind.LocalDelete,
                outcome == RemovalOutcome.Removed ? PendingPhase.Removed : PendingPhase.Armed,
                local.Path, new RemovalProof(local, "volume-a", await context.Local.ContentHashAsync(local, default)), null, null, null,
                null, null, false, null);
            store.SetPending(context.Pair.PairId, pending);
            ((FakeRecovery)context.Recovery).Result = new RemovalResult(outcome, null, null, null);
            await NewJobs(context).Recovery.ResolvePendingAsync(store.Pending(context.Pair.PairId), RecoveryChoice.Check, default);
            Check.Require(store.DeletedNameSpent(context.Pair.PairId, PathNames.DropboxKey(local.Path)) ==
                (outcome == RemovalOutcome.Removed), "Pending recovery spends only confirmed Removed, not Unknown or no-effect.");
        }
        foreach (var published in new[] { true, false })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var source = await PutAsync(context.Dropbox, "remote");
            var final = await PutAsync(context.Local, "local");
            var stage = new StageProof("stage", "volume-a", final.Id, final.Size, source.ContentHash);
            var pending = new PendingEffect(1, "operation-a", 0, PendingKind.LocalReplace, PendingPhase.Published,
                final.Path, new RemovalProof(final, "volume-a", source.ContentHash), null, source, stage,
                null, null, false, null);
            store.SetPending(context.Pair.PairId, pending);
            store.Commit(context.Pair.PairId, StateChange.Empty with
            {
                Raise =
                [
                    (IssueKind.Failure, TextKey.LocalNameInvalid, final.Path,
                        new IssueSubject([new IssueItem(Side.Local, final.Path, final.Id)], null)),
                    (IssueKind.Failure, TextKey.LocalNameInvalid, source.Path,
                        new IssueSubject([new IssueItem(Side.Dropbox, source.Path, source.Id)], null))
                ]
            });
            var recovery = (FakeRecovery)context.Recovery;
            recovery.Result = published
                ? new RemovalResult(RemovalOutcome.Published, null, final, null)
                : new RemovalResult(RemovalOutcome.NoEffect, null, null, null);
            var result = await NewJobs(context).Recovery.ResolvePendingAsync(
                store.Pending(context.Pair.PairId), RecoveryChoice.Check, CancellationToken.None);
            Check.Require(store.Pending(context.Pair.PairId) is NoPending && recovery.Resolved.Count == 1,
                "Pending recovery delegates once and clears the pending note");
            Check.Require((store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(final.Path)) is not null)
                == published, "Only a published recovery records the recovered binding");
            Check.Require((result.Outcome == RemovalOutcome.Published) == published,
                "The recovery reports the source owner's own classified outcome.");
            Check.Require(store.Issues(context.Pair.PairId).Count == (published ? 0 : 2),
                "A published recovery closes the Failure kinds at both paths; a discard leaves them.");
        }
    }

    private static async Task HashFailureAsync()
    {
        foreach (var edited in new[] { false, true })
        {
            SyncOutcome? ordinary = null;
            foreach (var raced in new[] { false, true })
            {
                using var store = new InMemoryStateStore();
                var context = Context(store);
                var local = await PutAsync(context.Local, "item");
                var remote = await PutAsync(context.Dropbox, "item");
                var saved = Save(context, local, remote);
                await context.Local.DeleteAsync(local, null, default);
                if (edited) remote = await PutAsync(context.Dropbox, remote.Path, [2, 3], remote, CompareAt);
                var deletes = 0;
                var events = new List<RunEvent>();
                context = context with
                {
                    Dropbox = new ProxySide(context.Dropbox) { OnDelete = _ => deletes++ }, Events = events.Add
                };
                var stale = raced ? local with { Modified = local.Modified.AddSeconds(1) } : null;
                var jobs = NewJobs(context);
                var outcome = await jobs.Items.RunRowAttemptAsync(Row(stale, remote, saved), default);
                if (!raced) ordinary = outcome;
                var issues = store.Issues(context.Pair.PairId);
                Check.Require(outcome == ordinary && outcome.Kind == (edited ? ActionKind.Download : ActionKind.DeleteDropbox) &&
                    deletes == (edited ? 0 : 1) &&
                    events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == (raced ? 1 : 0) &&
                    issues.Count == (edited ? 1 : 0) && issues.All(issue => issue.Kind == IssueKind.Info && issue.Cause == TextKey.InfoEditBeatsDelete),
                    "A vanished hash source follows the same saved-record decision as fresh absence; retry grants no extra deletion authority.");
                var final = await context.Local.ReadOneAsync(local.Path, default);
                Check.Require(edited ? final is not null && await context.Local.ContentHashAsync(final, default) == remote.ContentHash &&
                    await context.Dropbox.ReadOneAsync(remote.Path, default) == remote &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) == SyncedEntries.From(final, remote) :
                    final is null && await context.Dropbox.ReadOneAsync(remote.Path, default) is null &&
                    store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) is null,
                    "Fresh absence and reobserved absence preserve remote edits and retire only the unchanged saved copy.");
                var next = await PutAsync(context.Local, "next");
                Check.Require((await jobs.Items.RunRowAttemptAsync(Row(next, null, null), default)).Kind == ActionKind.Upload,
                    "A hash disappearance does not prevent the next row from syncing.");
            }
        }
    }

    private static async Task HashSourceReappearsAsync()
    {
        foreach (var raced in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "reappeared");
            var remote = await PutAsync(context.Dropbox, local.Path);
            var saved = Save(context, local, remote);
            await context.Local.DeleteAsync(local, null, default);
            var original = context.Local;
            var reads = 0;
            var deletes = 0;
            Entry? replacement = null;
            var events = new List<RunEvent>();
            context = context with
            {
                Local = new ProxySide(original) { BeforeRead = async (path, _) =>
                {
                    if (path == local.Path && ++reads == (raced ? 2 : 1))
                        replacement = await PutAsync(original, path, [2, 3], modified: CompareAt);
                } },
                Dropbox = new ProxySide(context.Dropbox) { OnDelete = _ => deletes++ }, Events = events.Add
            };
            var stale = raced ? local with { Modified = local.Modified.AddSeconds(1) } : null;
            var outcome = await NewJobs(context).Items.RunRowAttemptAsync(Row(stale, remote, saved), default);
            Check.Require(replacement is not null && deletes == 0 &&
                await original.ReadOneAsync(local.Path, default) == replacement &&
                await context.Dropbox.ReadOneAsync(remote.Path, default) is not null &&
                events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == 1 &&
                (raced ? outcome.Raised == IssueKind.Failure && outcome.Cause == TextKey.FailChanged : outcome.Kind == ActionKind.Upload),
                "Effect-time freshness refuses remote deletion when a local replacement appears; an already consumed diagnostic retry fails rather than looping.");
        }
    }

    private static async Task FailedReplacementCompletesRowAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "Parent/Child");
        var remote = await context.Dropbox.EnsureFolderAsync(local.Path, default);
        var row = Row(local, remote, null) with { Context = Facts() with
        { FirstRun = true, CompleteInventory = true, FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item } };
        var action = new SyncAction(ActionKind.ReplaceKind, row, Side: Side.Dropbox, Target: remote.Path);
        ((FakeSide)context.Dropbox).Seed(remote with { Id = "replacement-folder" }, null);
        var completed = new List<(Row Row, SyncOutcome Outcome)>();
        var outcome = await NewJobs(context).Items.Dispatch.DispatchPreparedAsync(action, default,
            completed: (item, result) => completed.Add((item, result)));
        Check.Require(outcome.Raised == IssueKind.Failure && completed.Count == 1 &&
            completed[0].Row == row && completed[0].Outcome == outcome &&
            !completed[0].Outcome.Succeeded(),
            "A failed planned replacement reports its original child exactly once, so scan parent finalization sees unfinished work.");
        Check.Require((await context.Dropbox.ReadOneAsync(remote.Path, default))?.Id == "replacement-folder",
            "Refused replacement preserves the changed folder.");
    }

    private static async Task PlannedReplacementInLiveContextAsync(string root)
    {
        using var store = StateStore.Open(Path.Combine(root, "planned-replacement.db"));
        var context = Context(store) with { Live = true };
        var local = await context.Local.EnsureFolderAsync("Parent/Child", default);
        var remote = await context.Dropbox.EnsureFolderAsync(local.Path, default);
        var saved = Save(context, local, remote);
        await context.Local.DeleteAsync(local, null, default);
        local = await PutAsync(context.Local, local.Path);
        var child = await PutAsync(context.Dropbox, remote.Path + "/Leaf");
        Save(context, child with { Id = "local-leaf" }, child);
        ((FakeSide)context.Dropbox).Seed(child with { Id = "replacement-leaf" }, [1]);
        var row = Row(local, remote, saved) with { Context = Facts() with
            { FirstRun = true, CompleteInventory = true, FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item } };
        var action = new SyncAction(ActionKind.ReplaceKind, row, Side: Side.Dropbox, Target: remote.Path);
        var outcome = await NewJobs(context).Items.Dispatch.DispatchPreparedAsync(action, default);
        Check.Require(outcome.Succeeded() && await context.Dropbox.ReadOneAsync(child.Path, default) is null &&
            await context.Dropbox.ReadOneAsync(remote.Path, default) is { IsFolder: false },
            "null-selection planned replacement keeps its ordinary child classification even in a live context; " +
            $"outcome={outcome}; child={await context.Dropbox.ReadOneAsync(child.Path, default)}; " +
            $"root={await context.Dropbox.ReadOneAsync(remote.Path, default)}; " +
            "issues=" + string.Join(" | ", store.Issues(context.Pair.PairId).Select(issue => issue.Path + ":" + issue.Cause)));
    }

    private static async Task FolderPreparationReadFailuresAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await context.Local.EnsureFolderAsync("folder", default);
            var remote = await context.Dropbox.EnsureFolderAsync("folder", default);
            var saved = Save(context, local, remote);
            await context.Local.DeleteAsync(local, null, default);
            var row = Row(null, remote, saved);
            ((FakeSide)context.For(side)).FailNext(remote.Path,
                new ItemFailedException(remote.Path, Text.ReadFailedOn(side), null));
            var jobs = NewJobs(context);
            var outcomes = new List<SyncOutcome>();
            var operation = await jobs.Items.Folders.PrepareFolderRemovalAsync(row, Side.Dropbox,
                new HashSet<string>(), new HashSet<string>(), default, (_, outcome) => outcomes.Add(outcome));
            Check.Require(operation is null && outcomes is [{ Raised: IssueKind.Failure }] &&
                store.Issues(context.Pair.PairId).Single().Cause == Text.ReadFailedOn(side) &&
                await context.Dropbox.ReadOneAsync(remote.Path, default) == remote,
                "Folder preparation read failures are one row's Failure, with no folder removal.");
        }
    }

    private static async Task AdmittedPreludeFailuresAsync()
    {
        foreach (var reobserve in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "item");
            var remote = await PutAsync(context.Dropbox, "item");
            var saved = Save(context, local, remote);
            if (!reobserve)
            {
                local = local with { Modified = local.Modified.AddSeconds(1) };
                ((FakeSide)context.Local).Seed(local, [1]);
            }
            var row = Row(local, remote, saved);
            ((FakeSide)context.Local).FailNext(local.Path,
                new ItemFailedException(local.Path, TextKey.ReadLocalFailed, null));
            var jobs = NewJobs(context);
            var outcome = await jobs.Items.Execution.RunAdmittedAsync(row, default,
                preparedAction: reobserve ? new SyncAction(ActionKind.Record, row) : null);
            Check.Require(outcome.Raised == IssueKind.Failure && outcome.Cause == TextKey.ReadLocalFailed &&
                store.Issues(context.Pair.PairId).Single().Path == local.Path,
                "Preparation hashing and pre-decision reobservation share the admitted row's failure boundary.");
            var other = await PutAsync(context.Local, "other");
            Check.Require((await jobs.Items.SyncPathAsync(Side.Local, other.Path, default)).Effect == EffectKind.Uploaded,
                "A pre-decision read failure leaves independent work runnable.");
        }
    }

    private static async Task ReadFailuresAsync()
    {
        foreach (var sourceSide in new[] { Side.Local, Side.Dropbox })
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var observing in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var source = await PutAsync(context.For(sourceSide), sourceSide == Side.Local ? "item" : "Mi:xed.txt");
            var other = sourceSide == Side.Local ? Side.Dropbox : Side.Local;
            var targetPath = new Parents(context).Destination(other, source.Path);
            var failedPath = side == sourceSide ? source.Path : targetPath;
            var cause = side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed;
            ((FakeSide)context.For(side)).FailNext(failedPath,
                new ItemFailedException(failedPath, cause, new System.Text.Json.JsonException()));
            var jobs = NewJobs(context);
            var row = sourceSide == Side.Local ? Row(source, null, null) : Row(null, source, null);
            var result = observing
                ? await jobs.Items.SyncPathAsync(sourceSide, source.Path, CancellationToken.None)
                : await jobs.Items.RunRowAttemptAsync(row, CancellationToken.None);
            var issue = store.Issues(context.Pair.PairId).Single();
            Check.Require(result.Raised == IssueKind.Failure && result.Effect is null &&
                issue.Kind == IssueKind.Failure && issue.Cause == cause && issue.Path == source.Path,
                "Observation and locked freshness read failures become the item's Failure with the original cause");
            var sourceAfter = await context.For(sourceSide).ReadOneAsync(source.Path, CancellationToken.None);
            var targetAfter = await context.For(other).ReadOneAsync(targetPath, CancellationToken.None);
            var bindingAfter = store.SyncedByKey(context.Pair.PairId, sourceSide, row.Key);
            // A pure upload takes the byte-stage admission, not an ordinary permit (see QueuedAdmissionAsync and
            // ItemSync.DispatchPreparedAsync); observation and download take the ordinary permit.
            var upload = sourceSide == Side.Local && !observing;
            var admission = (FakeAdmission)context.Admission;
            Check.Require(sourceAfter == source &&
                targetAfter is null &&
                bindingAfter is null &&
                (upload
                    ? admission.Admitted == 0 && admission.StageAcquired == 1
                    : admission.Admitted == 1 && admission.StageAcquired == 0) &&
                admission.Completed == 0,
                "A failed admitted read performs no copy, binding change or effect completion: " +
                $"sourceSide={sourceSide}, side={side}, observing={observing}, upload={upload}, " +
                $"source={(sourceAfter == source ? "intact" : "changed")}, target={(targetAfter is null ? "absent" : "present")}, " +
                $"binding={(bindingAfter is null ? "none" : "present")}, " +
                $"admitted={admission.Admitted}, completed={admission.Completed}, stage={admission.StageAcquired}");
            var next = await PutAsync(context.Local, "next");
            Check.Require((await jobs.Items.SyncPathAsync(Side.Local, next.Path, CancellationToken.None)).Kind
                == ActionKind.Upload && store.Issues(context.Pair.PairId).Count == 1,
                "A read failure leaves its notice but does not prevent the next item from syncing");
        }
    }

    private static async Task CancellationAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await NewJobs(context).Items.SyncPathAsync(Side.Local, "item", cancellation.Token);
            Check.Require(false, "Cancellation must propagate");
        }
        catch (OperationCanceledException) { }
        Check.Require(store.Issues(context.Pair.PairId).Count == 0, "Cancellation is not an item Failure");
    }

    // Measured evidence for the migrated per-item sites: each pair runs the owner call and the inline
    // expression it replaced over the same loop; NoInlining sinks keep both results alive.
    private static void CostProbe()
    {
        const string path = "Parent/Child:";
        const string name = "Child:";
        const string key = "PARENT/CHILD:";
        var entry = new Entry(path, false, 1, DateTimeOffset.UnixEpoch, "local-id", "", "", null);
        var remote = new Entry(path, false, 1, DateTimeOffset.UnixEpoch, "dropbox-id", "", "", null);
        var row = new Row(key, entry, null, null, [], null, Facts());
        var foreign = SyncedEntries.From(entry, remote) with { LocalId = "other-id" };
        var facts = Facts() with { UnderFolderDelete = Side.Local, HoldsNestedRoot = true };

        var leaf = Compare("Observer leaf name",
            index => SinkString(BaselineLeaf(path)), index => SinkString(MigratedLeaf(path)));
        Check.Require(leaf.Migrated <= leaf.Baseline, "PathNames.Name adds no allocation: " + leaf.Migrated + " B");

        var mapped = Compare("Observer mapped-name predicate",
            index => SinkBool(BaselineMapped(name)), index => SinkBool(MigratedMapped(name)));
        Check.Require(mapped.Migrated <= mapped.Baseline, "PathNames.IsMapped maps once: " + mapped.Migrated + " B");

        var separator = Compare("Observer local-leaf boundary",
            index => SinkInt(BaselineLastSeparator(path)), index => SinkInt(MigratedLastSeparator(path)));
        Check.Require(separator.Migrated <= separator.Baseline, "PathNames.LastSeparator adds no allocation");

        var same = Compare("Observer sibling identity",
            index => SinkBool(BaselineSame(path, "parent/child:")), index => SinkBool(MigratedSame(path, "parent/child:")));
        Check.Require(same.Migrated <= same.Baseline, "PathNames.Same adds no allocation");

        var spelling = Compare("Parents spelling branch",
            index => SinkString(BaselineSpelling(Side.Local, path)), index => SinkString(MigratedSpelling(Side.Local, path)));
        Check.Require(spelling.Migrated <= spelling.Baseline, "PathNames.SpellingOn maps once: " + spelling.Migrated + " B");

        var parent = Compare("Parents ancestor",
            index => SinkString(BaselineParent(path)), index => SinkString(MigratedParent(path)));
        Check.Require(parent.Migrated <= parent.Baseline, "PathNames.Parent keeps the one substring: " + parent.Migrated + " B");

        var pathOn = Compare("Syncer.Paths row path",
            index => SinkString(BaselinePathOn(row, Side.Local, null)), index => SinkString(MigratedPathOn(row, Side.Local, null)));
        Check.Require(pathOn.Migrated <= pathOn.Baseline, "Row.PathOn adds no allocation");

        var free = Compare("Syncer free-incoming predicate",
            index => SinkBool(BaselineFreeIncoming(entry, null, foreign, null)),
            index => SinkBool(MigratedFreeIncoming(entry, null, foreign, null)));
        Check.Require(free.Migrated <= free.Baseline, "Decide.IsFreeIncoming adds no allocation");

        var from = Compare("SyncedEntries.From side overload",
            index => SinkEntry(BaselineFrom(entry, remote)), index => SinkEntry(MigratedFrom(Side.Dropbox, remote, entry)));
        Check.Require(from.Migrated <= from.Baseline, "the side overload builds one record: " + from.Migrated + " B");

        var effect = Compare("RunEvent.Effect at the pending site",
            index => SinkEvent(BaselineEffect(path)), index => SinkEvent(MigratedEffect(path)));
        Check.Require(effect.Migrated <= effect.Baseline, "RunEvent.Effect adds no event allocation");

    }

    private static (long Baseline, long Migrated) Compare(string label, Action<int> baseline, Action<int> migrated)
    {
        var baselineBytes = AllocProbe.Measure(baseline, 10_000);
        var migratedBytes = AllocProbe.Measure(migrated, 10_000);
        Console.WriteLine("  " + label + ": baseline=" + baselineBytes + " B, migrated=" + migratedBytes +
            " B per 10000 calls");
        return (baselineBytes, migratedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkString(string? value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkBool(bool value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkInt(int value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkEntry(SyncedEntry? value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkEvent(RunEvent value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkContext(RowContext value) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BaselineLeaf(string path) =>
        path[(path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal) + 1)..];
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string MigratedLeaf(string path) => PathNames.Name(path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineMapped(string dropboxPath) => PathNames.LocalSpelling(dropboxPath) != dropboxPath;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool MigratedMapped(string dropboxPath) => PathNames.IsMapped(dropboxPath);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int BaselineLastSeparator(string path) =>
        path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int MigratedLastSeparator(string path) => PathNames.LastSeparator(path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineSame(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool MigratedSame(string? left, string? right) => PathNames.Same(left, right);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BaselineSpelling(Side side, string dropboxPath) =>
        side == Side.Local ? PathNames.LocalSpelling(dropboxPath) : dropboxPath;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string MigratedSpelling(Side side, string dropboxPath) => PathNames.SpellingOn(side, dropboxPath);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BaselineParent(string path)
    {
        var up = path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal);
        return up < 0 ? "" : path[..up];
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string MigratedParent(string path) => PathNames.Parent(path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? BaselinePathOn(Row row, Side side, string? target) => side == Side.Local
        ? row.Local?.Path ?? target ?? row.Synced?.LocalPath ?? row.Context.Destination ?? row.Key
        : row.Dropbox?.Path ?? target ?? row.Synced?.DropboxPath ?? row.Context.Destination ?? row.Key;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? MigratedPathOn(Row row, Side side, string? target) => row.PathOn(side, target);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineFreeIncoming(Entry? local, Entry? dropbox, SyncedEntry? localBinding,
        SyncedEntry? dropboxBinding) =>
        (localBinding is not null || dropboxBinding is not null) &&
        (local is not null && dropbox is null && local.Id != localBinding?.LocalId ||
         dropbox is not null && local is null && dropbox.Id != dropboxBinding?.DropboxId);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool MigratedFreeIncoming(Entry? local, Entry? dropbox, SyncedEntry? localBinding,
        SyncedEntry? dropboxBinding) => Decide.IsFreeIncoming(local, dropbox, localBinding, dropboxBinding);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SyncedEntry BaselineFrom(Entry entry, Entry counterpart) => SyncedEntries.From(entry, counterpart);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SyncedEntry MigratedFrom(Side side, Entry entry, Entry counterpart) =>
        SyncedEntries.From(side, entry, counterpart);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RunEvent BaselineEffect(string path) =>
        new(RunEventKind.Effect, EffectKind.Downloaded.ToString(), path, "", Side.Local.ToString());
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RunEvent MigratedEffect(string path) =>
        RunEvent.Effect(EffectKind.Downloaded, path, "", Side.Local);


    private static readonly DateTimeOffset CompareAt = DateTimeOffset.UnixEpoch.AddDays(1);

    private static RowContext Facts() => new(false, false, false, null, false, false, true, false);

    private static Row Row(Entry? local, Entry? dropbox, SyncedEntry? saved) =>
        new(local is not null ? PathNames.Key(local.Path) : dropbox is not null ? PathNames.DropboxKey(dropbox.Path) : saved!.LocalKey,
            local, dropbox, saved, [], null, Facts());

    private static (ItemSync Items, FirstRunFolders FirstRunFolders, AncestorFence Ancestors, Recovery Recovery,
        ExclusionRunner Exclusions, KeepBothRunner KeepBoth, ActionExecutor Actions)
        NewJobs(ExecutorContext context, FakeBoundary? boundary = null)
    {
        boundary ??= new FakeBoundary(context.Admission);
        return SyncJobs.Create(context, new SyncPorts(boundary, RefusingExecutionBoundary.Instance, boundary, 0));
    }

    private static Syncer NewSyncer(ExecutorContext context) =>
        NewSyncer(context, new FakeBoundary(context.Admission));

    private static Syncer NewSyncer(ExecutorContext context, FakeBoundary boundary) =>
        new(context, new SyncPorts(boundary, RefusingExecutionBoundary.Instance, boundary, 0));

    private static ExecutorContext Context(IStateStore store, string dropboxRoot = "/SyncerFixture")
    {
        var pair = SourceFixture.Bound(new PairConfig("pair-a", "Pair A", @"C:\SyncerFixture", dropboxRoot,
            "connection-a", [], [], FirstRunPolicy.NewestWins, true, null));
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        var settings = new EngineSettings("{name} ({side} {time}){ext}", "yyyyMMddHHmmss", "Local", "Dropbox",
            ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows) {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes, StoragePageSize = ShippedPageSize() };
        var local = new FakeSide(Side.Local);
        return new ExecutorContext(Local: local, Dropbox: new FakeSide(Side.Dropbox),
            // FakeRecovery stands in for LocalSide: it proves removal by hashing the checked file when no saved
            // hash is supplied, exactly as LocalSide.ProveRemovalAsync does.
            Recovery: new FakeRecovery
            {
                Evidence = new DriveEvidence(1, null, null), Hash = local.ContentHashAsync
            }, Store: store, Pair: pair,
            Scope: new PathScope(pair, [pair]),
            Settings: settings, Runtime: new ReplaceTurn(), Admission: new FakeAdmission { Level = 1 },
            Priority: false, Events: _ => { },
            ReserveDownloadAsync: (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoopDisposable.Instance),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (path, _, _) => Task.FromResult<DeletionEvidence>(
                new DeletionEvidence.Unknown(path, TextKey.ReadLocalFailed)),
            BeginDownloadAttempt: operation => new RequestAttempt(operation, AttemptOwner.Transfer),
            CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(true),
            UploadAdmissionIdentity: ("account-a", "route-a", "source-a"),
            ItemAttempts: new FakeItemAttempts(), Opportunity: new object(), ExpectedGeneration: 0)
        {
            CaptureFolderCursorAsync = _ => Task.FromResult("fixture-cursor"),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
    }

    private static async IAsyncEnumerable<Listed> NoFolderChangesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static int ShippedPageSize()
    {
        using var stream = typeof(EngineSettings).Assembly.GetManifestResourceStream(ResourceNames.Settings)!;
        using var reader = new StreamReader(stream);
        return SettingsParser.Parse(reader.ReadToEnd(), null).StoragePageSize;
    }

    private static async Task DisappearedCopyParentAsync()
    {
        foreach (var written in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            const string path = "Parent/Target";
            await context.Local.EnsureFolderAsync("Parent", default);
            await context.Dropbox.EnsureFolderAsync("Parent", default);
            var local = await PutAsync(context.Local, path);
            var remote = await PutAsync(context.Dropbox, path);
            var saved = SyncedEntries.From(local, remote);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var source = context.For(PathNames.Other(written));
            var changed = await PutAsync(source, path, [2, 3], written == Side.Local ? remote : local,
                CompareAt);
            var destination = context.For(written);
            var parent = await destination.EnsureFolderAsync("Parent", default);
            var expected = written == Side.Local ? local : remote;
            var publishes = 0;
            var events = new System.Collections.Concurrent.ConcurrentQueue<RunEvent>();
            var racing = new ProxySide(destination)
            {
                BlockPublish = async (_, ct) =>
                {
                    if (Interlocked.Increment(ref publishes) != 1) return;
                    await destination.DeleteAsync(expected, null, ct);
                    await destination.DeleteAsync(parent, null, ct);
                }
            };
            context = written == Side.Local ? context with { Local = racing, Events = events.Enqueue }
                : context with { Dropbox = racing, Events = events.Enqueue };
            var row = Row(written == Side.Local ? local : changed,
                written == Side.Dropbox ? remote : changed, saved);
            var outcome = await NewJobs(context).Items.RunRowAttemptAsync(row, default);
            var result = await destination.ReadOneAsync(path, default);
            Check.Require(publishes == 2 && result is not null &&
                await destination.ContentHashAsync(result, default) == await source.ContentHashAsync(changed, default) &&
                await destination.ReadOneAsync("Parent", default) is not null &&
                store.SyncedByKey(context.Pair.PairId, written, PathNames.KeyFor(written, path)) != saved &&
                store.Issues(context.Pair.PairId).All(issue => issue.Kind == IssueKind.Info) &&
                outcome.Effect == (written == Side.Local ? EffectKind.Downloaded : EffectKind.Uploaded) &&
                events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == 1,
                "The caller reobserves a disappeared copy target and parent, preserves the edited source, and completes publication once.");
        }
    }

    private static async Task LearnedLocalContentAsync()
    {
        foreach (var outcomeKind in new[] { EffectKind.KeptBoth, EffectKind.Recorded, EffectKind.Uploaded })
        foreach (var diagnostic in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var localSide = (FakeSide)context.Local;
            var local = await PutAsync(localSide, "content-observation", [1]);
            var remote = await PutAsync(context.Dropbox, local.Path, [1]);
            var saved = SyncedEntries.From(local, remote);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            localSide.Seed(local, [2]);
            if (outcomeKind == EffectKind.Uploaded)
            {
                await context.Dropbox.DeleteAsync(remote, null, default);
                remote = null;
            }
            else remote = await PutAsync(context.Dropbox, local.Path,
                outcomeKind == EffectKind.Recorded ? [2] : [3], remote, CompareAt);
            var hash = DropboxContentHash.Compute([2]);
            var refusals = 0;
            var disposed = localSide.StagesDisposed;
            void Refuse()
            {
                refusals++;
                Check.Require(refusals == 1, "Contained content is redecided rather than repeating the failed removal.");
                throw new ChangedUnderneathException(local.Path)
                {
                    ObservedLocal = local with { ContentHash = hash }
                };
            }
            var proxy = new ProxySide(localSide)
            {
                OnDelete = _ => Refuse(),
                OnPublish = (expected, _) => { if (expected is not null) Refuse(); },
                OnRead = _ =>
                {
                    if (refusals != 0 && outcomeKind != EffectKind.Uploaded)
                        Check.Require(localSide.StagesDisposed > disposed,
                            "Failed incoming stage disposal precedes the first diagnostic observation.");
                }
            };
            context = context with { Local = proxy };
            var row = Row(local, remote, saved);
            var outcome = await NewJobs(context).Items.Execution.RunAdmittedAsync(row, default, reobserve: diagnostic);
            Check.Require(outcome.Effect == outcomeKind && refusals == 1 &&
                proxy.Hashes == 0 && await localSide.ContentHashAsync(local, default) == hash &&
                store.Issues(context.Pair.PairId).All(issue => issue.Kind == IssueKind.Info),
                "A same-stamp edit converges through existing Record, KeepBoth or Upload even inside an existing diagnostic retry.");
            Check.Require(context.Evidence.CurrentHash(row with { LocalHash = hash }) is null,
                "A retained observed Entry has no digest authority after completion.");
        }
    }

    private static async Task ReadmittedOutcomeCasesAsync()
    {
        foreach (var mode in new[] { "held", "skipped", "outside", "in-sync", "replacement", "upload", "keep" })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var attempts = (FakeItemAttempts)context.ItemAttempts;
            var local = await PutAsync(context.Local, "readmitted", [1]);
            var remote = await PutAsync(context.Dropbox, local.Path, [1]);
            var saved = SyncedEntries.From(local, remote);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            if (mode is "upload" or "keep" or "replacement")
            {
                local = await PutAsync(context.Local, local.Path, [2, 2], local);
                if (mode == "keep") remote = await PutAsync(context.Dropbox, local.Path, [3, 3], remote, CompareAt);
                if (mode == "replacement")
                {
                    await context.Dropbox.DeleteAsync(remote, null, default);
                    remote = await context.Dropbox.EnsureFolderAsync(local.Path, default);
                    saved = SyncedEntries.From(local with { IsFolder = true, Size = 0 }, remote);
                    store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
                }
            }
            var row = Row(local, remote, saved) with
            {
                Context = Facts() with
                {
                    Held = mode == "held", HeldKind = mode == "held" ? IssueKind.Failure : null,
                    Skipped = mode == "skipped", OutOfScope = mode == "outside"
                }
            };
            var jobs = NewJobs(context);
            var preparation = await jobs.Items.Preparation.PrepareLiveAsync(row, default);
            preparation = await jobs.Items.Preparation.PrepareLiveAsync(preparation.Action.Row, default,
                previous: preparation with { Retried = true });
            var action = preparation.Action;
            Check.Require(preparation.Retried, "Every readmitted outcome retains the consumed retry: " + mode);
            var completed = 0;
            var attached = 0;
            async Task<SyncOutcome> Run(Guid attempt, CancellationToken token) =>
                await jobs.Items.Execution.RunAdmittedAsync(action.Row, token, preparation: preparation,
                    preparedLabels: preparation.Labels, attemptId: attempt,
                    retainUnfinished: (_, _) => attached++, completed: (_, _) => completed++);
            if (mode == "replacement")
            {
                Check.Require(preparation.TakeReplacementPhase &&
                    await RefusedAsync(() => attempts.ExecuteAsync(context.Opportunity, null,
                        ActionExecutor.AttemptSubject(row), Run, default)),
                    "A readmitted replacement refuses instead of entering a nested replacement registration.");
            }
            else
            {
                var outcome = await attempts.ExecuteAsync(context.Opportunity, null,
                    ActionExecutor.AttemptSubject(row), Run, default);
                var expectedNone = mode switch
                {
                    "held" => NoneReason.Held, "skipped" => NoneReason.Skipped,
                    "outside" => NoneReason.OutOfScope, "in-sync" => NoneReason.InSync, _ => (NoneReason?)null
                };
                var expectedEffect = mode switch
                {
                    "upload" => EffectKind.Uploaded,
                    "keep" => EffectKind.KeptBoth, _ => (EffectKind?)null
                };
                Check.Require(outcome.None == expectedNone && outcome.Effect == expectedEffect &&
                    outcome.Succeeded() == (expectedNone is null or NoneReason.InSync) && completed == 1 &&
                    attached == (mode == "keep" ? 1 : 0),
                    $"Readmission preserves its outcome: {mode}; none={outcome.None}; effect={outcome.Effect}; completed={completed}; attached={attached}");
                Check.Require(mode == "keep"
                    ? store.Issues(context.Pair.PairId) is [{ Kind: IssueKind.Info, Cause: TextKey.InfoKeptBoth }]
                    : store.Issues(context.Pair.PairId).Count == 0,
                    "Only KeepBoth reports its existing informational outcome: " + mode);
            }
            Check.Require(attempts.Executions == 1 &&
                (mode != "upload" || ((FakeAdmission)context.Admission).Admitted == 0),
                "Every outcome uses one registered attempt and Upload adds no admission: " + mode);
        }
    }

    private static async Task BeforeProofReadBudgetAsync()
    {
        var failures = new List<Exception>();
        foreach (var download in new[] { false, true })
        foreach (var changedProof in new[] { false, true })
        {
            var inner = new InMemoryStateStore();
            using var store = new DirtyReadStore(inner, changedProof);
            var context = Context(store);
            var local = await PutAsync(context.Local, "before-proof", [1]);
            var remote = await PutAsync(context.Dropbox, local.Path, [1]);
            var saved = SyncedEntries.From(local, remote);
            inner.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            if (download) remote = await PutAsync(context.Dropbox, local.Path, [2], remote, CompareAt);
            else
            {
                await context.Dropbox.DeleteAsync(remote, null, default);
                remote = null;
            }
            var counted = new CountingSide(context.Local);
            context = context with
            {
                Local = counted,
                Recovery = new FakeRecovery
                {
                    Evidence = new DriveEvidence(1, null, null), Hash = counted.ContentHashAsync
                }
            };
            var outcome = await NewJobs(context).Items.RunRowAttemptAsync(Row(local, remote, saved), default);
            var expected = download ? EffectKind.Downloaded : EffectKind.DeletedLocal;
            Console.WriteLine($"before-proof download={download} changed-proof={changedProof} dirty-reads={store.Reads} metadata={counted.Reads} opens={counted.Opens} hashes={counted.Hashes} hash-bytes={counted.HashBytes} effect={outcome.Effect}");
            if (store.Reads != 1)
                failures.Add(new InvalidOperationException($"Before-proof dirty-read count: download={download}; expected=1; actual={store.Reads}"));
            if (counted.Opens != 0 || counted.Hashes != 0 || outcome.Effect != expected ||
                inner.Issues(context.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure))
                failures.Add(new InvalidOperationException($"Before-proof behavior: download={download}; no preparation-only recheck may hash or refuse a matching {expected}."));
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private sealed class DirtyReadStore(IStateStore inner, bool changedProof) : ForwardingStateStore(inner)
    {
        internal int Reads;
        public override (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
            string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain, string? fullIdentity,
            string? currentPathKey, string? recordedPathKey)
        {
            var result = base.ReadLocalDirty(pairId, expectedGeneration, localSourceEpoch, nativeDomain,
                fullIdentity, currentPathKey, recordedPathKey);
            Reads++;
            return changedProof && Reads > 1
                ? (true, result.SourceBoundary, result.EvidenceVersion + 1) : result;
        }
    }

    private static async Task LocalProofLifetimeAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var local = await PutAsync(context.Local, "proof-lifetime", [1]);
        var remote = await PutAsync(context.Dropbox, local.Path, [1]);
        var saved = SyncedEntries.From(local, remote);
        var row = Row(local, remote, saved);
        var reading = context.Evidence.Read(local, saved);
        var evidence = new LocalEvidence.DirtyProof(reading.Dirty, reading.Boundary, reading.Version);
        var hash = DropboxContentHash.Compute([1]);
        var historical = row with { LocalHash = hash };
        Check.Require(context.Evidence.CurrentHash(historical, evidence) is null,
            "A historical hash alone has no acquired authority.");
        var before = context.Evidence.Remember(row, evidence);
        Check.Require(context.Evidence.CurrentHash(historical, evidence) is null,
            "Before-proof retention does not authorize even an equal digest.");
        var current = context.Evidence.Remember(historical, evidence, hash);
        var contextual = historical with { Context = historical.Context with { CompleteInventory = false } };
        Check.Require(context.Evidence.CurrentHash(contextual, evidence) == hash &&
            context.Evidence.CurrentHash(historical with { Local = local with { } }, evidence) is null &&
            context.Evidence.CurrentHash(historical with { Local = null }, evidence) is null &&
            context.Evidence.CurrentHash(historical with { Synced = saved with { DropboxId = "replacement" } }, evidence) is null &&
            context.Evidence.CurrentHash(historical, evidence with { Version = evidence.Version + 1 }) is null &&
            context.Evidence.CurrentHash(historical, evidence with { Dirty = !evidence.Dirty }) is null,
            "Exact Entry, saved binding and acquired evidence own authority; context copies retain it, projections do not.");
        context.Evidence.Release(historical, before);
        Check.Require(context.Evidence.CurrentHash(historical, evidence) == hash,
            "Preparation-only cleanup cannot remove a later digest.");
        var replacement = context.Evidence.Remember(historical, evidence, hash);
        context.Evidence.Release(historical, current);
        Check.Require(context.Evidence.CurrentHash(historical, evidence) == hash,
            "Stale cleanup cannot revoke a replacement association.");
        context.Evidence.Release(historical, replacement);
        Check.Require(context.Evidence.CurrentHash(historical, evidence) is null,
            "Explicit final release removes digest authority despite a retained Entry.");
        context.Evidence.Remember(row, evidence);
        foreach (var changed in new[]
        {
            local with { Id = "replacement" }, local with { Path = "another-path" },
            local with { IsFolder = true }, local with { Size = local.Size + 1 },
            local with { Modified = local.Modified.AddTicks(1) }
        })
        {
            var rejected = context.Evidence.Rebind(row, row with { Local = changed }, local with { ContentHash = hash });
            Check.Require(rejected.Row.LocalHash is null && context.Evidence.CurrentHash(rejected.Row) is null,
                "A changed identity, path, kind, size or time cannot acquire the learned digest.");
        }
        var wrongBinding = context.Evidence.Rebind(row, row with { Synced = saved with { DropboxId = "replacement" } },
            local with { ContentHash = hash });
        var incomplete = context.Evidence.Rebind(row, row with { Local = local with { } }, local with { ContentHash = "" });
        Check.Require(wrongBinding.Row.LocalHash is null && incomplete.Row.LocalHash is null,
            "Changed binding and incomplete observations provide no current authority.");
        var fresh = row with { Local = local with { } };
        var rebound = context.Evidence.Rebind(row, fresh, local with { ContentHash = hash });
        Check.Require(context.Evidence.CurrentHash(rebound.Row, rebound.Evidence) == hash,
            "A separately observed exact metadata match gains authority only through validated rebinding.");
        var reboundOwner = context.Evidence.Capture(rebound.Row);
        context.Evidence.Release(rebound.Row, reboundOwner);
        Check.Require(context.Evidence.CurrentHash(rebound.Row) is null,
            "The adopted association is explicitly releasable independently of its retained source proof.");
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var separate = historical with { Local = local with { Path = $"independent-{index}" } };
            var proof = context.Evidence.Remember(separate, evidence, hash);
            Check.Require(context.Evidence.CurrentHash(separate, evidence) == hash,
                "Concurrent different Entry keys retain independent authority.");
            context.Evidence.Release(separate, proof);
            Check.Require(context.Evidence.CurrentHash(separate) is null,
                "Concurrent releases affect only their owned association.");
        })));
    }

    private static async Task RemovalReadFreshnessAsync()
    {
        foreach (var repeat in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = await PutAsync(context.Local, "proof-read", [1]);
            var remote = await PutAsync(context.Dropbox, local.Path, [2, 3], modified: CompareAt);
            var original = context.Local;
            var hashes = 0;
            var publishes = 0;
            var events = new List<RunEvent>();
            context = context with { Local = new ProxySide(original)
            {
                Hash = async (entry, ct) =>
                {
                    hashes++;
                    Check.Require(hashes <= 2, "Removal preparation shares the bounded item retry.");
                    if (hashes == 1 || repeat) throw new ChangedUnderneathException(entry.Path);
                    return await original.ContentHashAsync(entry, ct);
                },
                OnPublish = (_, _) => publishes++
            }, Events = events.Add };
            var outcome = await NewJobs(context).Items.RunRowAttemptAsync(Row(local, remote, null), default);
            var final = await original.ReadOneAsync(local.Path, default);
            var issues = store.Issues(context.Pair.PairId);
            Check.Require(hashes == 2 && publishes == (repeat ? 0 : 1) &&
                events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == 1 &&
                issues.Count == 1 && issues[0].Cause == (repeat ? TextKey.FailChanged : TextKey.InfoNewestWins),
                "An ordinary losing-file read race retries once; repeated refusal fails before publication.");
            Check.Require(repeat ? final == local && outcome.Effect is null :
                final is not null && outcome.Effect == EffectKind.Downloaded &&
                await original.ContentHashAsync(final, default) == remote.ContentHash &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) == SyncedEntries.From(final, remote),
                "Checked removal preparation never authorizes publication without fresh proof and records only the successful download.");
        }
    }

    private static async Task<Entry> PutAsync(ISide side, string path, byte[]? bytes = null, Entry? expected = null,
        DateTimeOffset? modified = null)
    {
        var source = new Entry(path, false, 1, modified ?? DateTimeOffset.UnixEpoch, path, "", "", null);
        using var content = new MemoryStream(bytes ?? [1]);
        await using var stage = await side.StageAsync(path, content, source, CancellationToken.None);
        return (await side.PublishAsync(stage, expected, null, CancellationToken.None)).Final!;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => default;
    }

    private sealed class FakeItemAttempts : IItemAttempts
    {
        internal int Executions;
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Executions++;
            return execute(Guid.NewGuid(), ct);
        }
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) => throw new NotSupportedException();
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) { }
    }

    /// <summary>Admits the callback without adding production scope; a failed effect is still the product's own. It
    /// records each recovery call's outstanding work scopes and lease, so folder-plan checks can prove a survivor
    /// runs through the boundary after its parent scope is released (engine.md 7.6).</summary>
    /// <summary>A recovery boundary that holds the real process path locks, as production does, and whose
    /// lease refuses any key outside that hold.</summary>
    private sealed class RealPathLocksBoundary(IReadOnlyList<PathLockScope> scopes) : IRecoveryExecutionBoundary
    {
        public void RequireCurrent() { }

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public async Task<T> Run<T>(Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            await using var hold = await PathLocks.Shared.EnterAsync(scopes, ct);
            return await execute(new Lease(scopes), ct);
        }

        private sealed class Lease(IReadOnlyList<PathLockScope> scopes) : IAdmittedExecution
        {
            public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys)
            {
                foreach (var key in requiredLockKeys)
                {
                    var required = new PathLockScope(key, false);
                    if (!scopes.Any(held => PathLocks.Covers(held, required)))
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
                }
            }

            public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> required) =>
                Require(pairId, generation, required.Select(scope => scope.Key).ToArray());
            public void Revalidate() { }
            public ValueTask ReleaseAdmissionAsync() => default;
            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class FakeBoundary(IAdmission admission, ExecutorContext? context = null) : IRecoveryExecutionBoundary, ISelectedExecutionBoundary
    {
        private readonly List<RecoveryCall> _recovery = [];
        internal Func<Task>? AfterDeferred { get; set; }

        /// <summary>Each recovery callback's outstanding work scopes at entry and the lease keys it required.</summary>
        public IReadOnlyList<RecoveryCall> RecoveryCalls { get { lock (_recovery) return [.. _recovery]; } }

        public void RequireCurrent() { }
        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            Run(execute, ct);

        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            Run(execute, ct);
        public async Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            T result;
            if (context is null) result = await Run(execute, ct);
            else
            {
                await using var hold = await PathLocks.Shared.EnterAsync(
                    LockKeys.ForRecovery(context.Pair, operation, context.Store.Pending(context.Pair.PairId)), ct);
                result = await Run(execute, ct);
            }
            if (result is SyncOutcome { Deferred: true } && AfterDeferred is { } after) await after();
            return result;
        }
        public Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) => Run(execute, ct);
        public async Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            // The selected root phase holds one permit, as the production boundary does.
            await using var permit = await admission.AdmitAsync(false, ct);
            return await Run(execute, ct);
        }

        private Task<T> Run<T>(Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            var call = new RecoveryCall(admission is FakeAdmission fake ? fake.WorkActive : 0);
            lock (_recovery) _recovery.Add(call);
            return execute(new Lease(call), ct);
        }

        private Task Run(Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) =>
            Run(async (execution, token) => { await execute(execution, token); return true; }, ct);

        /// <summary>One recovery callback: work scopes still held when it was invoked, and its lease requirements.</summary>
        public sealed class RecoveryCall(int workActive)
        {
            public int WorkActive { get; } = workActive;
            public List<string> Required { get; } = [];
        }

        private sealed class Lease(RecoveryCall call) : IAdmittedExecution
        {
            public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys) =>
                call.Required.AddRange(requiredLockKeys);
            public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) { }
            public void Revalidate() { }
            public ValueTask ReleaseAdmissionAsync() => default;
            public ValueTask DisposeAsync() => default;
        }
    }

    private static SyncedEntry Save(ExecutorContext context, Entry local, Entry dropbox)
    {
        var saved = SyncedEntries.From(local, dropbox);
        context.Store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        return saved;
    }
}
