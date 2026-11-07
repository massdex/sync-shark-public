using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The ExecuteAsync table of section 7.1 (one Commit per action) and the item-error mapping.</summary>
public sealed class ActionExecutor(
    ExecutorContext context, Transfers transfers, Parents parents, KeepBothRunner keepBoth)
{
    /// <summary>Deletes an entry, commits the caller's preflighted change and reports the effect. The caller owns
    /// admission and failure mapping (shared by FolderDeleteRunner and ExclusionRunner).</summary>
    public Task<RemovalResult> RemoveAsync(Side side, Entry entry, RemovalSelection? selection,
        StateChange change, CancellationToken ct, Guid? attempt = null, ItemRetryCandidate? expectedAttempt = null) =>
        RemoveCoreAsync(side, entry, selection, change, ct, attempt, expectedAttempt, reportCompletion: true);

    private async Task<RemovalResult> RemoveCoreAsync(Side side, Entry entry, RemovalSelection? selection,
        StateChange change, CancellationToken ct, Guid? attempt, ItemRetryCandidate? expectedAttempt,
        bool reportCompletion, Action<Issue>? committedIssue = null, WorkSelection? workSelection = null)
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
                    Settle(context.Store, context.Pair.PairId, context.ItemAttempts,
                        pending is null ? change : FileSettlement(change), attempt, expectedAttempt, workSelection, committedIssue, pending);
                    context.Events(RunEvent.Effect(EffectKinds.Deleted(side), entry.Path, string.Empty, side));
                    success = true;
                }
                else if (result.Outcome == RemovalOutcome.NoEffect && result.OperationId is not null)
                    Settle(context.Store, context.Pair.PairId, context.ItemAttempts, StateChange.Empty,
                        null, null, null, null, CompletedPending(result));
            }, ct);
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
        PendingEffect? pending = null)
    {
        // These synchronous writes drain queued first-run Records before settling the attempt or selection.
        if (attempt is { } registered && expected is not null)
            attempts.Complete(registered, expected, change, pending, selection, committedIssue);
        else if (pending is not null) store.SettlePending(pairId, pending, change, selection, committedIssue);
        else if (selection is not null) store.Commit(pairId, change, selection, committedIssue);
        else store.Commit(pairId, change, committedIssue);
    }

    /// <summary>Applies the action inside the caller's lock and admission scope.
    /// closeIssues == false: the action closes no issue (a resolution closes its issue itself, only after every
    /// action of the selected outcome succeeded, section 7.9).</summary>
    public async Task<SyncOutcome> ExecuteAsync(SyncAction action, KeepBothLabels? labels, CancellationToken ct,
        bool closeIssues = true, Func<ValueTask>? uploadStaged = null,
        (Side Side, string Path, Entry? Entry)? observedTarget = null, Action<Issue>? committedIssue = null,
        Row? firstRunFolder = null, Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null,
        WorkSelection? selection = null)
    {
        if (attemptId is not null || action.Kind is ActionKind.None or ActionKind.Record or ActionKind.Notice or ActionKind.DeferFolder or ActionKind.ForgetOnly)
            return await ExecuteCoreAsync(action, labels, ct, closeIssues, uploadStaged,
                observedTarget, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        var subject = Subject(action.Row) with { FirstRunDeletion = Owed(firstRunFolder ?? action.Row, action) };
        var expected = action.Row.Local is null ? null : context.Store.ReadItemRetryForSubject(
            context.Pair.PairId, context.ExpectedGeneration, action.Row.Local.Path, subject);
        return await context.ItemAttempts.ExecuteAsync(selection is null ? context.Opportunity : selection, expected, subject,
            (attempt, token) => ExecuteCoreAsync(action, labels, token, closeIssues, uploadStaged,
                observedTarget, committedIssue, firstRunFolder, attempt, expected, selection), ct);
    }

    internal static (IssueSubject Subject, ItemRetryCandidate? Expected) CaptureAttempt(
        IStateStore store, string pairId, long generation, string? path, IssueSubject subject)
    {
        var expected = path is null ? null : store.ReadItemRetryForSubject(pairId, generation, path, subject);
        return (expected?.Token.Subject ?? subject, expected);
    }

    internal static IssueSubject AttemptSubject(Row row, Row? firstRunFolder = null, SyncAction? action = null)
    {
        var subject = Subject(row);
        // History-only rows still own distinct attempts; absent entries are not current issue candidates.
        if (subject.Items.Count == 0 && row.Synced is { } saved)
        {
            var items = (List<IssueItem>)subject.Items;
            items.Add(new IssueItem(Side.Local, saved.LocalPath, saved.LocalId));
            items.Add(new IssueItem(Side.Dropbox, saved.DropboxPath, saved.DropboxId));
        }
        return subject with { FirstRunDeletion = Owed(firstRunFolder ?? row, action) };
    }

    private async Task<SyncOutcome> ExecuteCoreAsync(SyncAction action, KeepBothLabels? labels, CancellationToken ct,
        bool closeIssues, Func<ValueTask>? uploadStaged,
        (Side Side, string Path, Entry? Entry)? observedTarget, Action<Issue>? committedIssue,
        Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expected, WorkSelection? selection)
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
                observedTarget, committedIssue, firstRunFolder, attempt, expected, selection);
            success = outcome.Succeeded();
            return outcome;
        }
        catch (Exception error) when (!callbackFailed && IsItemError(error))
        {
            return await FailCoreAsync(action.Row, action, error, ct, committedIssue, firstRunFolder, attempt, expected, selection);
        }
        finally
        {
            if (reportsCompletion && uploadStaged is null) context.Admission.ActionCompleted(success);
        }
    }

    /// <summary>The one item-error mapping (section 7.1 errors): ChangedUnderneath, ItemFailed and PathOccupied become the
    /// row's issue, committed, and the outcome is returned; any other exception is rethrown. action == null: the failure
    /// came before a decision and is a Failure; without observed entries it names the exception's path. PathOccupied: the
    /// incoming side is the side the action came from (its Kind/Side), and the occupant is read at the exception's path.</summary>
    public async Task<SyncOutcome> FailAsync(Row row, SyncAction? action, Exception error, CancellationToken ct,
        Action<Issue>? committedIssue = null, Row? firstRunFolder = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, WorkSelection? selection = null)
    {
        if (attemptId is not null)
            return await FailCoreAsync(row, action, error, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        if (expectedAttempt is not null || error is ItemFailedException failed && IssuePolicy.IsLocalAccessRetryCause(failed.Cause))
        {
            var subject = expectedAttempt?.Token.Subject ??
                AttemptSubject(row, firstRunFolder, action);
            var expected = expectedAttempt ?? (row.Local is null ? null : context.Store.ReadItemRetryForSubject(
                context.Pair.PairId, context.ExpectedGeneration, row.Local.Path, subject));
            return await context.ItemAttempts.ExecuteAsync(selection is null ? context.Opportunity : selection, expected, subject,
                (attempt, token) => FailCoreAsync(row, action, error, token, committedIssue, firstRunFolder,
                    attempt, expected, selection), ct);
        }
        return await FailCoreAsync(row, action, error, ct, committedIssue, firstRunFolder, null, null, selection);
    }

    internal static void FailItem(IStateStore store, string pairId, IItemAttempts attempts,
        Action<RunEvent> events, Guid attempt, ItemRetryCandidate? expected, long epoch,
        string path, IssueSubject subject, TextKey cause)
    {
        var change = StateChange.Empty with { Raise = [(IssueKind.Failure, cause, path, subject)] };
        SettleFailure(store, pairId, attempts, change, attempt, expected, epoch,
            ItemRetryDispatch.LocalBranch, null, null);
        EmitIssues(events, change);
    }

    internal static void SettleFailure(IStateStore store, string pairId, IItemAttempts attempts,
        StateChange change, Guid? attempt, ItemRetryCandidate? expected, long epoch,
        ItemRetryDispatch dispatch, PendingEffect? pending, Action<Issue>? committedIssue, WorkSelection? selection = null)
    {
        var failure = change.Raise[0];
        if (expected is null && failure.Kind == IssueKind.Failure && IssuePolicy.IsLocalAccessRetryCause(failure.Cause))
        {
            if (attempt is not { } registered) throw new InvalidOperationException(nameof(attempt));
            var candidate = attempts.ReportFailure(registered, epoch, failure.Path, failure.Subject,
                failure.Cause, selection, dispatch, pending, null);
            if (committedIssue is not null)
                committedIssue(store.IssueById(pairId, candidate.Token.IssueId) ?? throw new InvalidOperationException(nameof(candidate)));
        }
        else Settle(store, pairId, attempts, change, attempt, expected, selection, committedIssue);
    }

    internal static bool IsItemError(Exception error) =>
        error is ChangedUnderneathException or ItemFailedException or PathOccupiedException;

    // One error-to-issue settlement: cause, kind, path, subject and retry dispatch are decided from the same exception and row.
    private async Task<SyncOutcome> FailCoreAsync(Row row, SyncAction? action, Exception error, CancellationToken ct,
        Action<Issue>? committedIssue, Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expected, WorkSelection? selection)
    {
        if (firstRunFolder is not null && IssuePolicy.SameFirstRunLocal(firstRunFolder.Local, row.Local))
            row = row with { Context = row.Context with { FirstRunDeletion = Owed(firstRunFolder, action) } };
        if (!IsItemError(error))
            ExceptionDispatchInfo.Throw(error);
        ct.ThrowIfCancellationRequested();
        var cause = error is PathOccupiedException && action?.Kind != ActionKind.KeepBoth
            ? TextKey.ClashNameTaken : FailureCause(error);
        var kind = IssueKind.Failure;
        var path = row.Local is null && row.Dropbox is null ? FailurePath(error) : Row.Path(row);
        var subject = IssuePolicy.IsLocalAccessRetryCause(cause) ? AttemptSubject(row) : Subject(row);
        // A keep-both label taken after it was chosen is a race, not a name clash: retried with fresh labels next run.
        if (error is PathOccupiedException occupied && action is { Kind: not ActionKind.KeepBoth })
        {
            kind = IssueKind.NameClash;
            var written = action.WrittenSide();
            var incoming = action.Kind == ActionKind.MoveUnlinked ? written : PathNames.Other(written);
            var colliding = occupied.CollidingSide ?? written;
            path = occupied.Path;
            Entry? occupant;
            try
            {
                occupant = await context.For(colliding).ReadOneAsync(path, ct);
            }
            catch (Exception readError) when (readError is ChangedUnderneathException or ItemFailedException)
            {
                return await FailCoreAsync(row, action, readError, ct, committedIssue, firstRunFolder, attempt, expected, selection);
            }
            // Subject owns a fresh list until this failure is committed.
            var items = (List<IssueItem>)subject.Items;
            if (occupant != null) items.Add(new IssueItem(colliding, occupant.Path, occupant.Id));
            subject = subject with { Items = IssueSubject.DistinctItems(items), Incoming = incoming };
        }
        var change = StateChange.Empty with { Raise = [(kind, cause, path, subject)] };
        change = Decorate(change, row, action, false, firstRunFolder);
        var newInUse = expected is null && IssuePolicy.IsLocalAccessRetryCause(cause);
        var epoch = newInUse ? selection?.SourceEpoch ?? context.Evidence.ReadEpoch() : default;
        var pending = newInUse ? context.Store.Pending(context.Pair.PairId) as CurrentPending : null;
        SettleFailure(context.Store, context.Pair.PairId, context.ItemAttempts, change, attempt, expected,
            epoch, pending is null ? ItemRetryDispatch.OrdinaryItem : ItemRetryDispatch.LocalRecovery,
            pending?.Value, committedIssue, selection);
        EmitIssues(context.Events, change, error);
        return new SyncOutcome(action?.Kind ?? ActionKind.None, null, null, kind, false, cause);
    }

    // Each branch is one row of the execution table; transfers own staging and the replace turn.
    private async Task<SyncOutcome> ApplyAsync(SyncAction action, KeepBothLabels? labels, bool closeIssues, CancellationToken ct, Func<ValueTask>? uploadStaged,
        (Side Side, string Path, Entry? Entry)? observedTarget, Action<Issue>? committedIssue,
        Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expectedAttempt, WorkSelection? selection)
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
                    change = Decorate(change, row, action, false, firstRunFolder);
                    if (change.Raise.Count != 0) context.Store.Commit(context.Pair.PairId, change, committedIssue);
                    EmitIssues(context.Events, change);
                    return Outcome(action, null, change);
                }
                // An in-sync result is a success: it acknowledges a preserved first-run marker and its history Info.
                change = Decorate(change, row, action, action.None == NoneReason.InSync, firstRunFolder);
                change = change with { Close = Closures(action, row.Context.Held, closeIssues) };
                break;
            case ActionKind.Record:
                effect = EffectKind.Recorded;
                change = change with { Record = [SyncedEntries.From(row.Local!, row.Dropbox!)], Close = Closures(action, row.Context.Held, closeIssues) };
                change = await RecordBindings.PrepareAsync(context, change, ct, LockKeys.For(action, context.Pair, null));
                break;
            case ActionKind.ForgetOnly:
                effect = EffectKind.Forgotten;
                change = change with { ForgetBothSubtrees = [(row.Synced!.LocalKey, row.Synced.DropboxKey)],
                    Close = Closures(action, false, closeIssues) };
                RequireReadable(action, ct, Subtrees(row));
                break;
            case ActionKind.Notice:
                change = change with { Raise = [(action.NoticeKind, action.Cause, path, Subject(row))] };
                break;
            case ActionKind.KeepBoth:
                change = await keepBoth.RunAsync(row, labels ?? throw new ArgumentNullException(nameof(labels)), ct);
                if (!closeIssues) change = change with { Close = [] };
                effect = EffectKind.KeptBoth;
                break;
            case ActionKind.Upload or ActionKind.Download or
                ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder or ActionKind.ReplaceKind:
                return await CopyAsync(action, closeIssues, ct, uploadStaged, observedTarget, committedIssue, firstRunFolder, attempt, expectedAttempt, selection);
            case ActionKind.DeleteDropbox or ActionKind.DeleteLocal:
                written = action.WrittenSide();
                var deleted = row.EntryOn(written.Value)!;
                path = deleted.Path;
                effect = EffectKinds.Deleted(written.Value);
                change = change with
                {
                    ForgetSelf = [(written.Value, PathNames.KeyFor(written.Value, path))],
                    Close = Closures(action, true, closeIssues)
                };
                RequireReadable(action, ct, change.ForgetSelf);
                change = Decorate(change, row, action, true, firstRunFolder);
                // The action completion belongs to the outer execution so a refusal before the removal still reports.
                var removed = await RemoveCoreAsync(written.Value, deleted,
                    await SelectRemovalAsync(row, written.Value, deleted, ct), change, ct, attempt, expectedAttempt,
                    reportCompletion: action.Kind != ActionKind.DeleteDropbox, committedIssue, selection);
                if (removed.Outcome != RemovalOutcome.Removed)
                    throw new ItemFailedException(path, removed.Cause ?? TextKey.FailChanged, null);
                return Outcome(action, effect, change);
            case ActionKind.MoveDropbox or ActionKind.MoveLocal or ActionKind.MoveUnlinked:
                written = action.WrittenSide();
                var before = row.EntryOn(written.Value)!;
                path = Target(action);
                RequireReadable(action, ct);
                var moved = await context.For(written.Value).MoveAsync(before, path, ct);
                applied = moved;
                detail = before.Path;
                effect = EffectKinds.Moved(written.Value);
                change = change with { Close = Closures(action, true, closeIssues) };
                if (action.Kind != ActionKind.MoveUnlinked)
                {
                    var local = written == Side.Local ? moved : row.Local!;
                    var dropbox = written == Side.Dropbox ? moved : row.Dropbox!;
                    var after = row.Synced is { } synced
                        ? SyncedEntries.Relink(synced, local, dropbox) : SyncedEntries.From(local, dropbox);
                    change = change with { Renames = [new RenameApplied(row.Synced, after, written, before.Path, path)] };
                }
                change = WithInfo(change, action);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        change = Decorate(change, row, action, action.Kind != ActionKind.Notice, firstRunFolder);
        if (action.Kind == ActionKind.Record && row.Context.FirstRun && expectedAttempt is null && selection is null)
            await context.Store.CommitRecordAsync(context.Pair.PairId, change, committedIssue);
        else
            Settle(context.Store, context.Pair.PairId, context.ItemAttempts, change,
                attempt, expectedAttempt, selection, committedIssue);
        Emit(change, effect, written, path, detail, labels, row);
        return Outcome(action, effect, change);
    }

    // One copy: replace removal, parent ensure, destination write and commit are one ordered operation on one target.
    private async Task<SyncOutcome> CopyAsync(SyncAction action, bool closeIssues,
        CancellationToken ct, Func<ValueTask>? uploadStaged,
        (Side Side, string Path, Entry? Entry)? observedTarget, Action<Issue>? committedIssue,
        Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expectedAttempt, WorkSelection? workSelection)
    {
        var info = action.InfoAfter;
        if (info == TextKey.InfoIgnoredFolderKept)
        {
            var retained = WithInfo(StateChange.Empty, action);
            context.Store.Commit(context.Pair.PairId, retained, committedIssue);
            EmitIssues(context.Events, retained);
            info = null;
        }
        var row = action.Row;
        var written = action.WrittenSide();
        var source = row.EntryOn(PathNames.Other(written))!;
        var expected = row.EntryOn(written);
        var target = Target(action);
        var replaceKind = action.Kind == ActionKind.ReplaceKind;
        var forgotten = replaceKind ? Subtrees(row) : StateChange.Empty.ForgetSubtrees;
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
                        null, null, reportCompletion: false);
                if (removed.Outcome != RemovalOutcome.Removed)
                    throw new ItemFailedException(target, removed.Cause ?? TextKey.FailChanged, null);
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
        StateChange Change(Entry result) => Decorate(WithInfo(StateChange.Empty with
        {
            Close = Closures(action, true, closeIssues),
            ForgetSubtrees = forgotten,
            Record = [SyncedEntries.From(written, result, source)]
        }, info, row), row, action, true, firstRunFolder);
        if (source.IsFolder)
        {
            change = FileSettlement(Change(await context.For(written).EnsureFolderAsync(target, ct)));
            change = await RecordBindings.PrepareAsync(context, change, ct, LockKeys.For(action, context.Pair, null));
            Commit(change);
        }
        else if (written == Side.Dropbox)
        {
            change = Change(await transfers.UpAsync(source, target, expected, ct, uploadStaged));
            change = await RecordBindings.PrepareAsync(context, change, ct, LockKeys.For(action, context.Pair, null));
            if (expectedAttempt is null && committedIssue is null && workSelection is null)
                await context.Store.CommitAsync(context.Pair.PairId, change);
            else Commit(change);
        }
        else
        {
            var result = await transfers.DownAsync(source, target, expected, selection, published =>
            {
                change = Change(published.Final ?? throw new ChangedUnderneathException(target));
                var pending = CompletedPending(published);
                Settle(context.Store, context.Pair.PairId, context.ItemAttempts,
                    pending is null ? change : FileSettlement(change), attempt, expectedAttempt,
                    workSelection, committedIssue, pending);
            }, ct);
            if (result.Outcome != RemovalOutcome.Published)
                throw new ItemFailedException(target, result.Cause ?? TextKey.FailChanged, null);
        }
        void Commit(StateChange value) => Settle(context.Store, context.Pair.PairId, context.ItemAttempts,
            value, attempt, expectedAttempt, workSelection, committedIssue);
        Emit(change, effect, written, target, string.Empty, null, row);
        return Outcome(action, effect, change);
    }

    private static IssueSubject.FirstRunDeletionKind Owed(Row row, SyncAction? action)
    {
        var kept = row.Context.FirstRunDeletion;
        if (kept == IssueSubject.FirstRunDeletionKind.None &&
            !(row.Context.CompleteInventory && row.Context.FirstRun && IssuePolicy.UnbaselinedLocal(row)))
            return kept;
        if (action is { Kind: ActionKind.Upload or ActionKind.EnsureDropboxFolder,
            InfoAfter: TextKey.InfoDropboxHistoryUnchecked })
            return IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
        return kept == IssueSubject.FirstRunDeletionKind.None ? IssueSubject.FirstRunDeletionKind.Item : kept;
    }

    private static StateChange Decorate(StateChange change, Row row, SyncAction? action, bool succeeded, Row? firstRunFolder)
    {
        var original = firstRunFolder ?? row;
        var owed = Owed(original, action);
        if (owed == IssueSubject.FirstRunDeletionKind.None || original.Local is not { } local) return change;
        if (succeeded && (!local.IsFolder || firstRunFolder is not null))
        {
            var raises = change.Raise;
            if (owed == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
                !raises.Any(item => item.Cause == TextKey.InfoDropboxHistoryUnchecked))
                raises = [.. raises, (IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, local.Path, Subject(original))];
            return change with { FirstRunAcknowledged = [new IssueItem(Side.Local, local.Path, local.Id)], Raise = raises };
        }
        if (succeeded || action?.Kind == ActionKind.DeferFolder ||
            action is { None: NoneReason.OutOfScope or NoneReason.RemovedWithFolder }) return change;
        // A definite refusal already carries the retained obligation; a waiting cause would replace it and free the row.
        if (action is { None: NoneReason.Held } && original.Context.HeldKind == IssueKind.Failure &&
            original.Context.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None) return change;
        var subject = Subject(original) with { FirstRunDeletion = owed };
        var failures = change.Raise.ToList();
        var index = failures.FindIndex(item => item.Kind == IssueKind.Failure);
        if (index >= 0) failures[index] = (failures[index].Kind, failures[index].Cause, failures[index].Path, subject);
        else failures.Add((IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, local.Path, subject));
        return change with { Raise = failures };
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
                ? [(side, PathNames.KeyFor(side, row.EntryOn(side)!.Path))] : Subtrees(row);
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

    private StateChange WithInfo(StateChange change, SyncAction action) => WithInfo(change, action.InfoAfter, action.Row);

    private StateChange WithInfo(StateChange change, TextKey? cause, Row row) => cause is { } info &&
        !(row.Local is { IsFolder: true } &&
          row.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
        ? change with { Raise = [.. change.Raise, (IssueKind.Info, info, Row.Path(row), Subject(row))] } : change;

    private IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> Closures(SyncAction action, bool all, bool closeIssues)
    {
        if (!closeIssues) return [];
        var row = action.Row;
        var paths = new[] { row.Local?.Path, row.Dropbox?.Path, row.Synced?.LocalPath, row.Synced?.DropboxPath, action.Target }
            .OfType<string>().DefaultIfEmpty(row.Key).Distinct();
        var inSync = action.Kind == ActionKind.Record || action.IsInSync();
        var kinds = all ? IssueClosure.AfterHeldInSync
            : inSync ? IssueClosure.AfterInSync : IssueClosure.AfterEffect;
        return paths.Select(path => (path, kinds)).ToArray();
    }

    private static IReadOnlyList<(Side Side, string Key)> Subtrees(Row row) =>
        [(Side.Local, row.Synced?.LocalKey ?? PathNames.Key(row.Local!.Path)),
         (Side.Dropbox, row.Synced?.DropboxKey ?? PathNames.DropboxKey(row.Dropbox!.Path))];

    private static IssueSubject Subject(Row row)
    {
        var items = new List<IssueItem>();
        if (row.Local is { } local) items.Add(new IssueItem(Side.Local, local.Path, local.Id));
        if (row.Dropbox is { } dropbox) items.Add(new IssueItem(Side.Dropbox, dropbox.Path, dropbox.Id));
        // Extra candidates are always Dropbox entries: local names can't collide case-insensitively on Windows.
        items.AddRange(row.Extra.Select(extra => new IssueItem(Side.Dropbox, extra.Path, extra.Id)));
        var proof = row.LocalHash is { } hash && row.Local is { IsFolder: false, Skip: null } observed
            ? new LocalHashProof(observed.Size, observed.Modified.UtcTicks, hash) : null;
        return new IssueSubject(items, null, proof);
    }

    // Event table (engine-files.md): a state-only effect (Recorded, Forgotten) has Outcome ""; keep-both writes one
    // Effect per side, Path = the label, Detail = the original it copies.
    private void Emit(StateChange change, EffectKind? effect, Side? written, string path, string detail,
        KeepBothLabels? labels, Row row)
    {
        if (effect == EffectKind.KeptBoth)
        {
            if (labels!.DropboxLabel is { } dropboxLabel)
                context.Events(RunEvent.Effect(EffectKind.KeptBoth, dropboxLabel,
                    row.Local?.Path ?? "", Side.Dropbox));
            if (labels.LocalLabel is { } localLabel)
                context.Events(RunEvent.Effect(EffectKind.KeptBoth, localLabel,
                    row.Dropbox?.Path ?? "", Side.Local));
        }
        else if (effect != null)
            context.Events(RunEvent.Effect(effect.Value, path, detail, written));
        EmitIssues(context.Events, change);
    }

    /// <summary>One Issue event per raised issue (shared with RenameRunner).</summary>
    public static void EmitIssues(Action<RunEvent> events, StateChange change, Exception? error = null)
    {
        foreach (var issue in change.Raise)
            events(RunEvent.Issue(issue.Kind, issue.Cause, issue.Path, error));
    }

    private static SyncOutcome Outcome(SyncAction action, EffectKind? effect, StateChange change) =>
        action.IsInSync() ? SyncOutcome.InSync :
        new(action.Kind, action.Kind == ActionKind.None ? action.None : null, effect,
            change.Raise.Count == 0 ? null : change.Raise[0].Kind, false,
            change.Raise.Count == 0 ? null : change.Raise[0].Cause);

    internal static ItemFailedException InventoryFailure(InventoryIncompleteException error, string path, Side side) =>
        error.InnerException as ItemFailedException ?? new ItemFailedException(path, Text.ReadFailedOn(side), error);

    internal static TextKey FailureCause(Exception error) =>
        error is ItemFailedException failed ? failed.Cause : TextKey.FailChanged;

    private static string FailurePath(Exception error) => error switch
    {
        ChangedUnderneathException changed => changed.Path,
        ItemFailedException failed => failed.Path,
        PathOccupiedException occupied => occupied.Path,
        _ => throw new ArgumentException(nameof(error))
    };

    private static string Target(SyncAction action) => action.Target ?? throw new ArgumentException(nameof(action.Target));
}
