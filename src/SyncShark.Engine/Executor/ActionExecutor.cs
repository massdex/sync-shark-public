using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Executes the action table and settles each effect through its existing owner.</summary>
public sealed class ActionExecutor(
    ExecutorContext context, Transfers transfers, Parents parents, KeepBothRunner keepBoth)
{
    /// <summary>The shared item-failure owner for this execution context.</summary>
    public ItemFailures Failures => new(context);

    /// <summary>Deletes an entry, commits the caller's preflighted change and reports the effect. The caller owns
    /// admission and failure mapping (shared by FolderDeleteRunner and ExclusionRunner); authorized carries the
    /// caller's Local authority to the late debt check.</summary>
    public Task<RemovalResult> RemoveAsync(Side side, Entry entry, RemovalSelection? selection,
        StateChange change, CancellationToken ct, Guid? attempt = null, ItemRetryCandidate? expectedAttempt = null,
        bool authorized = false) =>
        RemoveCoreAsync(side, entry, selection, change, ct, attempt, expectedAttempt, reportCompletion: true,
            authorized: authorized);

    private async Task<RemovalResult> RemoveCoreAsync(Side side, Entry entry, RemovalSelection? selection,
        StateChange change, CancellationToken ct, Guid? attempt, ItemRetryCandidate? expectedAttempt,
        bool reportCompletion, Action<Issue>? committedIssue = null, WorkSelection? workSelection = null,
        bool authorized = false)
    {
        ct.ThrowIfCancellationRequested();
        var success = false;
        try
        {
            return await transfers.RemoveAsync(side, entry, selection, result =>
            {
                if (result.Outcome == RemovalOutcome.Removed)
                {
                    var pending = CompletedPending(result);
                    List<RunEvent>? settled = null;
                    Settle(context.Store, context.Pair.PairId, context.ItemAttempts,
                        pending is null ? change : FileSettlement(change), attempt, expectedAttempt, workSelection, committedIssue, pending,
                        change.Raise.Count == 0 ? null : (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew));
                    context.Events(RunEvent.Effect(EffectKinds.Deleted(side), entry.Path, string.Empty, side));
                    ActionChanges.EmitSettled(context.Events, settled);
                    success = true;
                }
                else if (result.Outcome == RemovalOutcome.NoEffect && result.OperationId is not null)
                    Settle(context.Store, context.Pair.PairId, context.ItemAttempts, StateChange.Empty,
                        null, null, null, null, CompletedPending(result));
            }, ct, authorized);
        }
        finally
        {
            if (reportCompletion && side == Side.Dropbox) context.Admission.ActionCompleted(success);
        }
    }

    internal async Task<RemovalSelection?> SelectRemovalAsync(Row row, Side side, Entry expected, CancellationToken ct)
    {
        if (side != Side.Local || expected.IsFolder) return null;
        var proof = await context.Recovery.ProveRemovalAsync(expected,
            row.LocalHash ?? row.Synced?.DropboxContentHash, ct).ConfigureAwait(false);
        return new RemovalSelection(proof, row.Synced);
    }

    private static Exception RemovalFailure(string path, RemovalResult result) =>
        result.Outcome == RemovalOutcome.NoEffect && result.ObservedLocal is { ContentHash.Length: > 0 } observed
            ? new ChangedUnderneathException(path) { ObservedLocal = observed }
            : new ItemFailedException(path, result.Cause ?? TextKey.FailChanged, null);

    private PendingEffect? CompletedPending(RemovalResult result)
    {
        if (result.OperationId is null) return null;
        if (context.Store.Pending(context.Pair.PairId) is not CurrentPending pending ||
            pending.Value.OperationId != result.OperationId)
            throw new ChangedUnderneathException(result.Final?.Path ?? context.Pair.LocalRoot);
        return pending.Value;
    }

    private static StateChange FileSettlement(StateChange change) => change.ForgetSubtrees.Count == 0 ? change :
        change with { ForgetSelf = [.. change.ForgetSelf, .. change.ForgetSubtrees], ForgetSubtrees = [] };

    internal static void Settle(IStateStore store, string pairId, IItemAttempts attempts, StateChange change,
        Guid? attempt, ItemRetryCandidate? expected, WorkSelection? selection, Action<Issue>? committedIssue,
        PendingEffect? pending = null, Action<Issue, bool>? settledIssue = null)
    {
        // These synchronous writes drain queued first-run Records before settling the attempt or selection.
        if (attempt is { } registered && expected is not null)
            attempts.Complete(registered, expected, change, pending, selection, committedIssue, settledIssue);
        else if (pending is not null) store.SettlePending(pairId, pending, change, selection, committedIssue, settledIssue);
        else if (selection is not null) store.Commit(pairId, change, selection, committedIssue, settledIssue);
        else store.Commit(pairId, change, committedIssue, settledIssue);
    }

    /// <summary>Applies the action inside the caller's lock and admission scope.
    /// closeIssues == false: the action closes no issue (a resolution closes its issue itself, only after every
    /// action of the selected outcome succeeded, section 7.9). localAuthority is the admission's
    /// <see cref="ItemSync.LocalAuthority"/> fact for this observedTarget, carried to the matching final effect.</summary>
    public async Task<SyncOutcome> ExecuteAsync(SyncAction action, KeepBothLabels? labels, CancellationToken ct,
        bool closeIssues = true, Func<ValueTask>? uploadStaged = null,
        (Side Side, string Path, Entry? Entry)? observedTarget = null, Action<Issue>? committedIssue = null,
        Row? firstRunFolder = null, Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null,
        WorkSelection? selection = null, Guid? listedScanId = null, bool localAuthority = false)
    {
        if (attemptId is not null || action.Kind is ActionKind.None or ActionKind.Record or ActionKind.Notice or ActionKind.DeferFolder or ActionKind.ForgetOnly)
            return await ExecuteCoreAsync(action, labels, ct, closeIssues, uploadStaged,
                observedTarget, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection, listedScanId, localAuthority);
        var subject = ActionChanges.Subject(action.Row) with { FirstRunDeletion = ActionChanges.Owed(firstRunFolder ?? action.Row, action) };
        var expected = action.Row.Local is null ? null : context.Store.ReadItemRetryForSubject(
            context.Pair.PairId, context.ExpectedGeneration, action.Row.Local.Path, subject);
        return await context.ItemAttempts.ExecuteAsync(selection is null ? context.Opportunity : selection, expected, subject,
            (attempt, token) => ExecuteCoreAsync(action, labels, token, closeIssues, uploadStaged,
                observedTarget, committedIssue, firstRunFolder, attempt, expected, selection, listedScanId, localAuthority), ct);
    }

    internal static (IssueSubject Subject, ItemRetryCandidate? Expected) CaptureAttempt(
        IStateStore store, string pairId, long generation, string? path, IssueSubject subject)
    {
        var expected = path is null ? null : store.ReadItemRetryForSubject(pairId, generation, path, subject);
        return (expected?.Token.Subject ?? subject, expected);
    }

    internal static IssueSubject AttemptSubject(Row row, Row? firstRunFolder = null, SyncAction? action = null)
    {
        var subject = ActionChanges.Subject(row);
        // History-only rows still own distinct attempts; absent entries are not current issue candidates.
        if (subject.Items.Count == 0 && row.Synced is { } saved)
        {
            var items = (List<IssueItem>)subject.Items;
            items.Add(new IssueItem(Side.Local, saved.LocalPath, saved.LocalId));
            items.Add(new IssueItem(Side.Dropbox, saved.DropboxPath, saved.DropboxId));
        }
        return subject with { FirstRunDeletion = ActionChanges.Owed(firstRunFolder ?? row, action) };
    }

    private async Task<SyncOutcome> ExecuteCoreAsync(SyncAction action, KeepBothLabels? labels, CancellationToken ct,
        bool closeIssues, Func<ValueTask>? uploadStaged,
        (Side Side, string Path, Entry? Entry)? observedTarget, Action<Issue>? committedIssue,
        Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expected, WorkSelection? selection, Guid? listedScanId, bool localAuthority)
    {
        if (firstRunFolder is not null && !IssuePolicy.SameFirstRunLocal(firstRunFolder.Local, action.Row.Local))
            throw new ChangedUnderneathException(Row.Path(firstRunFolder));
        ct.ThrowIfCancellationRequested();
        // Completion accounting retains the effect table; ItemSync owns the broader before-observation admission rule.
        var reportsCompletion = action.Kind is ActionKind.Upload or ActionKind.Download or
            ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder or
            ActionKind.MoveDropbox or ActionKind.DeleteDropbox or ActionKind.KeepBoth or ActionKind.ReplaceKind ||
            action.Kind == ActionKind.MoveUnlinked && action.Side == Side.Dropbox;
        var success = false;
        var callbackFailed = false;
        var notify = committedIssue;
        if (notify is not null)
            committedIssue = issue =>
            {
                try { notify(issue); }
                catch { callbackFailed = true; throw; }
            };
        try
        {
            var outcome = await ApplyAsync(action, labels, closeIssues, ct, uploadStaged,
                observedTarget, committedIssue, firstRunFolder, attempt, expected, selection, listedScanId, localAuthority);
            success = outcome.Succeeded();
            return outcome;
        }
        catch (Exception error) when (!callbackFailed && ItemFailures.IsItemError(error) &&
            (error is not ChangedUnderneathException changed || !IssuePolicy.CanRetryChanged(changed) ||
                action.Kind == ActionKind.KeepBoth))
        {
            return await Failures.FailCoreAsync(action.Row, action, error, ct, committedIssue, firstRunFolder, attempt, expected, selection);
        }
        finally
        {
            if (reportsCompletion && uploadStaged is null) context.Admission.ActionCompleted(success);
        }
    }

    // One execution-table dispatch shares settlement and reporting; transfer and removal lifetimes remain in their effect owners.
    private async Task<SyncOutcome> ApplyAsync(SyncAction action, KeepBothLabels? labels, bool closeIssues, CancellationToken ct, Func<ValueTask>? uploadStaged,
        (Side Side, string Path, Entry? Entry)? observedTarget, Action<Issue>? committedIssue,
        Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expectedAttempt, WorkSelection? selection, Guid? listedScanId, bool localAuthority)
    {
        var row = action.Row;
        var change = StateChange.Empty;
        EffectKind? effect = null;
        Side? written = null;
        Entry? applied = null;
        var path = Row.Path(row);
        var detail = "";
        switch (action.Kind)
        {
            case ActionKind.DeferFolder:
                return new SyncOutcome(action.Kind, null, null, null, true);
            case ActionKind.None:
                if (action.None != NoneReason.InSync)
                {
                    change = ActionChanges.Decorate(change, row, action, false, firstRunFolder);
                    List<RunEvent>? waiting = null;
                    if (change.Raise.Count != 0) context.Store.Commit(context.Pair.PairId, change, committedIssue,
                        (issue, isNew) => ActionChanges.CollectSettlement(ref waiting, issue, isNew, attemptedOperation: action.Kind));
                    ActionChanges.EmitSettled(context.Events, waiting);
                    return ActionChanges.Outcome(action, null, change);
                }
                // An in-sync result is a success: it acknowledges a preserved first-run marker and its history Info.
                change = ActionChanges.Decorate(change, row, action, action.None == NoneReason.InSync, firstRunFolder);
                change = change with
                {
                    Close = ActionChanges.Closures(action, row.Context.Held, closeIssues),
                    CloseScope = (row.Synced, row.Local is null && row.Dropbox is null && row.Synced is null && !row.CounterpartElsewhere)
                };
                break;
            case ActionKind.Record:
                effect = EffectKind.Recorded;
                var recorded = SyncedEntries.From(row.Local!, row.Dropbox!);
                change = change with { Record = [recorded], Close = ActionChanges.Closures(action, row.Context.Held, closeIssues),
                    CloseScope = (recorded, false), ListedScanId = listedScanId };
                change = await RecordBindings.PrepareAsync(context, change, ct, LockKeys.For(action, context.Pair, null));
                break;
            case ActionKind.ForgetOnly:
                effect = EffectKind.Forgotten;
                change = change with { ForgetBothSubtrees = [(row.Synced!.LocalKey, row.Synced.DropboxKey)],
                    Close = ActionChanges.Closures(action, false, closeIssues), CloseScope = (null, false) };
                RequireReadable(action, ct, ActionChanges.Subtrees(row));
                break;
            case ActionKind.Notice:
                change = change with { Raise = [(action.NoticeKind, action.Cause, path, ActionChanges.Subject(row))] };
                break;
            case ActionKind.KeepBoth:
                change = await keepBoth.RunAsync(row, labels ?? throw new ArgumentNullException(nameof(labels)), ct);
                if (!closeIssues) change = change with { Close = [] };
                effect = EffectKind.KeptBoth;
                break;
            case ActionKind.Upload or ActionKind.Download or
                ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder or ActionKind.ReplaceKind:
                return await CopyAsync(action, closeIssues, ct, uploadStaged, observedTarget, committedIssue, firstRunFolder, attempt, expectedAttempt, selection, localAuthority);
            case ActionKind.DeleteDropbox or ActionKind.DeleteLocal:
                written = action.WrittenSide();
                var deleted = row.EntryOn(written.Value)!;
                path = deleted.Path;
                effect = EffectKinds.Deleted(written.Value);
                change = change with
                {
                    ForgetSelf = [(written.Value, PathNames.KeyFor(written.Value, path))],
                    Close = ActionChanges.Closures(action, true, closeIssues),
                    CloseScope = (null, false)
                };
                RequireReadable(action, ct, change.ForgetSelf);
                change = ActionChanges.Decorate(change, row, action, true, firstRunFolder);
                // The action completion belongs to the outer execution so a refusal before the removal still reports.
                var removed = await RemoveCoreAsync(written.Value, deleted,
                    await SelectRemovalAsync(row, written.Value, deleted, ct), change, ct, attempt, expectedAttempt,
                    reportCompletion: action.Kind != ActionKind.DeleteDropbox, committedIssue, selection, localAuthority);
                if (removed.Outcome != RemovalOutcome.Removed)
                    throw RemovalFailure(path, removed);
                return ActionChanges.Outcome(action, effect, change);
            case ActionKind.MoveDropbox or ActionKind.MoveLocal or ActionKind.MoveUnlinked:
                written = action.WrittenSide();
                var before = row.EntryOn(written.Value)!;
                path = Target(action);
                RequireReadable(action, ct);
                var moved = await context.For(written.Value).MoveAsync(before, path, ct);
                applied = moved;
                detail = before.Path;
                effect = EffectKinds.Moved(written.Value);
                change = change with { Close = ActionChanges.Closures(action, true, closeIssues), CloseScope = (null, false) };
                if (action.Kind != ActionKind.MoveUnlinked)
                {
                    var local = written == Side.Local ? moved : row.Local!;
                    var dropbox = written == Side.Dropbox ? moved : row.Dropbox!;
                    var after = row.Synced is { } synced
                        ? SyncedEntries.Relink(synced, local, dropbox) : SyncedEntries.From(local, dropbox);
                    change = change with { Renames = [new RenameApplied(row.Synced, after, written, before.Path, path)],
                        CloseScope = (after, false) };
                }
                change = ActionChanges.WithInfo(change, action);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        change = ActionChanges.Decorate(change, row, action, action.Kind != ActionKind.Notice, firstRunFolder);
        List<RunEvent>? settled = null;
        Action<Issue, bool>? collect = change.Raise.Count == 0 ? null :
            (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew, attemptedOperation: action.Kind);
        if (action.Kind == ActionKind.Record && row.Context.FirstRun && expectedAttempt is null && selection is null)
            await context.Store.CommitRecordAsync(context.Pair.PairId, change, committedIssue, collect);
        else
            Settle(context.Store, context.Pair.PairId, context.ItemAttempts, change,
                attempt, expectedAttempt, selection, committedIssue, settledIssue: collect);
        ActionChanges.Emit(context.Events, effect, written, path, detail, labels, row, settled);
        return ActionChanges.Outcome(action, effect, change);
    }

    // One copy: replace removal, parent ensure, destination write and commit are one ordered operation on one target.
    private async Task<SyncOutcome> CopyAsync(SyncAction action, bool closeIssues,
        CancellationToken ct, Func<ValueTask>? uploadStaged,
        (Side Side, string Path, Entry? Entry)? observedTarget, Action<Issue>? committedIssue,
        Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expectedAttempt, WorkSelection? workSelection, bool localAuthority)
    {
        var info = action.InfoAfter;
        if (info == TextKey.InfoIgnoredFolderKept)
        {
            var retained = ActionChanges.WithInfo(StateChange.Empty, action);
            List<RunEvent>? retainedOutput = null;
            context.Store.Commit(context.Pair.PairId, retained, committedIssue,
                (issue, isNew) => ActionChanges.CollectSettlement(ref retainedOutput, issue, isNew, attemptedOperation: action.Kind));
            ActionChanges.EmitSettled(context.Events, retainedOutput);
            info = null;
        }
        var row = action.Row;
        var written = action.WrittenSide();
        var source = row.EntryOn(PathNames.Other(written))!;
        var expected = row.EntryOn(written);
        var target = Target(action);
        var replaceKind = action.Kind == ActionKind.ReplaceKind;
        var forgotten = replaceKind ? ActionChanges.Subtrees(row) : StateChange.Empty.ForgetSubtrees;
        var losing = row;
        if (written == Side.Local && expected is { IsFolder: false } &&
            string.IsNullOrEmpty(row.LocalHash ?? row.Synced?.DropboxContentHash))
            losing = row with { LocalHash = await context.Local.ContentHashAsync(expected, ct).ConfigureAwait(false) };
        var selection = expected is null ? null : await SelectRemovalAsync(losing, written, expected, ct);
        if (replaceKind)
        {
            RequireReadable(action, ct, forgotten);
            // Local downloads must validate their stage before removing the losing file or folder.
            if (expected is not null && (written != Side.Local || source.IsFolder))
            {
                observedTarget = null;
                RemovalResult removed;
                if (expected.IsFolder && written == Side.Dropbox)
                {
                    // Orchestration must leave the caller-owned footprint before the independent folder phase.
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
                }
                else
                    removed = await RemoveCoreAsync(written, expected, selection, StateChange.Empty, ct,
                        null, null, reportCompletion: false, authorized: localAuthority);
                if (removed.Outcome != RemovalOutcome.Removed)
                    throw RemovalFailure(target, removed);
                if (PathNames.Same(expected.Path, target)) observedTarget = (written, target, null);
                expected = null;
                selection = null;
            }
        }
        if (expected?.Path != target)
        {
            if (observedTarget is { } observed && observed.Side == written && PathNames.Same(observed.Path, target))
                await parents.EnsureAsync(written, target, observed.Entry, ct);
            else
                await parents.EnsureAsync(written, target, ct);
        }
        var effect = replaceKind ? EffectKind.Replaced : source.IsFolder ? EffectKind.FolderCreated :
            written == Side.Local ? EffectKind.Downloaded : EffectKind.Uploaded;
        var change = StateChange.Empty;
        StateChange Change(Entry result, JournalPosition? published = null)
        {
            var recorded = SyncedEntries.From(written, result, source);
            return ActionChanges.Decorate(ActionChanges.WithInfo(StateChange.Empty with
            {
                Close = ActionChanges.Closures(action, true, closeIssues),
                CloseScope = (recorded, false),
                ForgetSubtrees = forgotten,
                Record = [recorded],
                LocalPublished = published is null ? null : (result.Id, published)
            }, info, row), row, action, true, firstRunFolder);
        }
        List<RunEvent>? settled = null;
        void Collect(Issue issue, bool isNew) =>
            ActionChanges.CollectSettlement(ref settled, issue, isNew, attemptedOperation: action.Kind);
        if (source.IsFolder)
        {
            ItemSync.RequireLiveWrite(context, written == Side.Local, localAuthority);
            change = FileSettlement(Change(await context.For(written).EnsureFolderAsync(target, ct)));
            change = await RecordBindings.PrepareAsync(context, change, ct, LockKeys.For(action, context.Pair, null));
            Commit(change);
        }
        else if (written == Side.Dropbox)
        {
            change = Change(await transfers.UpAsync(source, target, expected, ct, uploadStaged));
            change = await RecordBindings.PrepareAsync(context, change, ct, LockKeys.For(action, context.Pair, null));
            if (expectedAttempt is null && committedIssue is null && workSelection is null)
                await context.Store.CommitAsync(context.Pair.PairId, change, change.Raise.Count == 0 ? null : Collect);
            else Commit(change);
        }
        else
        {
            var result = await transfers.DownAsync(source, target, expected, selection, published =>
            {
                change = Change(published.Final ?? throw new ChangedUnderneathException(target),
                    published.LocalPublished);
                var pending = CompletedPending(published);
                Settle(context.Store, context.Pair.PairId, context.ItemAttempts,
                    pending is null ? change : FileSettlement(change), attempt, expectedAttempt,
                    workSelection, committedIssue, pending, change.Raise.Count == 0 ? null : Collect);
            }, ct, localAuthority);
            if (result.Outcome != RemovalOutcome.Published)
                throw RemovalFailure(target, result);
        }
        void Commit(StateChange value) => Settle(context.Store, context.Pair.PairId, context.ItemAttempts,
            value, attempt, expectedAttempt, workSelection, committedIssue,
            settledIssue: value.Raise.Count == 0 ? null : Collect);
        ActionChanges.Emit(context.Events, effect, written, target, string.Empty, null, row, settled);
        return ActionChanges.Outcome(action, effect, change);
    }

    /// <summary>The one unreadable-refusal rule shared by every forget range: a range touching known unreadable
    /// Local content refuses with the caller's failure path and cause. isFolder describes the old footprint.</summary>
    internal static void RequireReadable(IStateStore store, string pairId, string failurePath,
        IReadOnlyList<(Side Side, string Key)> roots, bool isFolder, CancellationToken ct)
    {
        // A file has no descendants, so its Local roots run no range; a folder's go with its mapped roots in one pass.
        if (isFolder ? store.ForgetTouchesUnreadable(pairId, roots, ct)
            : roots.Where(root => root.Side == Side.Local).Distinct()
                .Any(root => store.UnreadableTouches(pairId, root.Key, false)) ||
              store.ForgetTouchesUnreadable(pairId, roots.Where(root => root.Side == Side.Dropbox).ToArray(), ct))
            throw new ItemFailedException(failurePath, Text.ReadFailedOn(Side.Local), null);
    }

    private void RequireReadable(SyncAction action, CancellationToken ct,
        IReadOnlyList<(Side Side, string Key)>? roots = null)
    {
        var row = action.Row;
        if (roots is null)
        {
            var side = action.WrittenSide();
            roots = row.Synced is null
                ? [(side, PathNames.KeyFor(side, row.EntryOn(side)!.Path))] : ActionChanges.Subtrees(row);
        }
        // Fold the row's own Local identity keys into the roots so the owner's single rule covers them.
        var all = new List<(Side Side, string Key)>();
        if (row.Local is { } local) all.Add((Side.Local, PathNames.Key(local.Path)));
        if (row.Synced is { } saved) all.Add((Side.Local, saved.LocalKey));
        all.AddRange(roots);
        RequireReadable(context.Store, context.Pair.PairId, Row.Path(row), all,
            row.Local is { IsFolder: true } || row.Synced is { IsFolder: true }, ct);
    }

    internal static void RequireReadableIdentity(IStateStore store, string pairId, Row row, Side side)
    {
        var localKey = row.Local is { } local ? PathNames.Key(local.Path) : null;
        if (localKey is not null && store.UnreadableTouches(pairId, localKey, false) ||
            row.Synced is { } saved && saved.LocalKey != localKey && store.UnreadableTouches(pairId, saved.LocalKey, false))
            throw new ItemFailedException(row.EntryOn(side)!.Path, Text.ReadFailedOn(Side.Local), null);
    }

    internal static void RequireReadable(FolderPlan plan, CancellationToken ct)
    {
        // Partial plans preserve unreadable children; only the plan knows which descendant ranges are forgotten.
        if (plan.ForgetTouchesUnreadable(ct))
            throw new ItemFailedException(plan.Folder.EntryOn(plan.DeletingFrom)!.Path, Text.ReadFailedOn(Side.Local), null);
    }

    /// <summary>One Issue event per raised issue (shared with RenameRunner).</summary>
    private static string Target(SyncAction action) => action.Target ?? throw new ArgumentException(nameof(action.Target));
}
