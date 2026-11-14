using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Maps item failures to their exact issue and retry settlement.</summary>
public readonly struct ItemFailures(ExecutorContext _context)
{
    /// <summary>The one item-error mapping (section 7.1 errors): ChangedUnderneath, ItemFailed and PathOccupied become the
    /// row's issue, committed, and the outcome is returned; any other exception is rethrown. action == null: the failure
    /// came before a decision and is a Failure; without observed entries it names the exception's path. PathOccupied: the
    /// incoming side is the side the action came from (its Kind/Side), and the occupant is read at the exception's path.</summary>
    public async Task<SyncOutcome> FailAsync(Row row, SyncAction? action, Exception error, CancellationToken ct,
        Action<Issue>? committedIssue = null, Row? firstRunFolder = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, WorkSelection? selection = null)
    {
        var context = _context;
        var failures = this;
        if (attemptId is not null)
            return await FailCoreAsync(row, action, error, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        if (expectedAttempt is not null || error is ItemFailedException failed &&
            (failed.Disposition == FailureDisposition.TemporaryNoEffect || IssuePolicy.IsLocalAccessRetryCause(failed.Cause)))
        {
            var subject = expectedAttempt?.Token.Subject ??
                ActionExecutor.AttemptSubject(row, firstRunFolder, action);
            var expected = expectedAttempt ?? (row.Local is null ? null : context.Store.ReadItemRetryForSubject(
                context.Pair.PairId, context.ExpectedGeneration, row.Local.Path, subject));
            return await context.ItemAttempts.ExecuteAsync(selection is null ? context.Opportunity : selection, expected,
                expected?.Token.Subject ?? subject, (attempt, token) => failures.FailCoreAsync(row, action, error, token, committedIssue, firstRunFolder,
                    attempt, expected, selection), ct);
        }
        return await FailCoreAsync(row, action, error, ct, committedIssue, firstRunFolder, null, null, selection);
    }

    internal static void FailItem(IStateStore store, string pairId, IItemAttempts attempts,
        Action<RunEvent> events, Guid attempt, ItemRetryCandidate? expected, long epoch,
        string path, IssueSubject subject, TextKey cause)
    {
        // Capture proved identity; continuing inventory and journal reports belong to the captured notice spelling.
        if (expected is not null) path = subject.LocalItemAt(PathNames.KeyFor(Side.Local, path))?.Path ?? path;
        var change = StateChange.Empty with { Raise = [(IssueKind.Failure, cause, path, subject)] };
        List<RunEvent>? settled = null;
        SettleFailure(store, pairId, attempts, change, attempt, expected, epoch,
            ItemRetryDispatch.LocalBranch, null, null, null, FailureDisposition.Permanent, cause, path, subject, null, null, null,
            (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew));
        ActionChanges.EmitSettled(events, settled);
    }

    /// <summary>Settles one Storage-certified completion failure as a current Failure for the exact acquired
    /// selection. The notice-only commit carries no attempt, episode, pending effect or selection, so the durable
    /// work and its baseline stay untouched.</summary>
    internal void FailSelectionMutation(ItemStoreMutationException error, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selection = error.Selection;
        selection.RequireFor(_context.Pair.PairId, _context.ExpectedGeneration);
        var path = selection.Key;
        var cause = TextKey.FailStoreMutation;
        var subject = new IssueSubject([new IssueItem(selection.Side, path, selection.Identity ?? string.Empty)], null);
        // The certificate keeps its diagnostic identity even if the notice transaction below fails.
        _context.Events(RunEvent.Failed(error, path: path) with { Side = selection.Side, WorkId = selection.AnchorWorkId });
        var change = StateChange.Empty with { Raise = [(IssueKind.Failure, cause, path, subject)] };
        List<RunEvent>? settled = null;
        SettleFailure(_context.Store, _context.Pair.PairId, _context.ItemAttempts, change, null, null, default,
            ItemRetryDispatch.OrdinaryItem, null, null, null, FailureDisposition.Permanent, cause, path, subject,
            null, null, null, (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew));
        ActionChanges.EmitSettled(_context.Events, settled);
    }

    internal static (bool NewlyAccepted, DateTimeOffset? RetryAt) SettleFailure(
        IStateStore store, string pairId, IItemAttempts attempts,
        StateChange change, Guid? attempt, ItemRetryCandidate? expected, long epoch,
        ItemRetryDispatch dispatch, PendingEffect? pending, Action<Issue>? committedIssue, WorkSelection? selection,
        FailureDisposition disposition, TextKey cause, string path, IssueSubject subject, ActionKind? attemptedOperation,
        TimeSpan? providerWait, long? providerWaitStarted, Action<Issue, bool>? settledIssue = null)
    {
        if (disposition == FailureDisposition.TemporaryNoEffect)
            return attempts.ReportTemporary(attempt ?? throw new InvalidOperationException(nameof(attempt)), epoch,
                path, subject, cause, selection, dispatch, providerWait, providerWaitStarted);
        var failure = change.Raise[0];
        if (expected is null && failure.Kind == IssueKind.Failure && IssuePolicy.IsLocalAccessRetryCause(failure.Cause))
        {
            if (attempt is not { } registered) throw new InvalidOperationException(nameof(attempt));
            attempts.ReportFailure(registered, epoch, failure.Path, failure.Subject,
                failure.Cause, selection, dispatch, pending, null, settledIssue, committedIssue);
        }
        else ActionExecutor.Settle(store, pairId, attempts, change, attempt, expected, selection, committedIssue,
            settledIssue: settledIssue);
        return (false, null);
    }

    /// <summary>One Wait for a newly accepted temporary turn, with its committed deadline; a replay publishes nothing.</summary>
    internal static void EmitTemporary(Action<RunEvent> events, (bool NewlyAccepted, DateTimeOffset? RetryAt) settled,
        TextKey cause, string path, Exception error, ActionKind? attempted)
    {
        if (settled.NewlyAccepted) events(RunEvent.Wait(cause, path, error, attempted, settled.RetryAt));
    }

    /// <summary>The one rule attaching an unknown-effect move's intended operation to its Failure subject.</summary>
    internal static IssueSubject WithUnknownMove(IssueSubject subject, FailureDisposition disposition,
        ItemFailedException? failed) =>
        disposition == FailureDisposition.UnknownEffect && failed?.Move is { } move ? subject with { Move = move } : subject;

    internal static bool IsItemError(Exception error) =>
        error is ChangedUnderneathException or ItemFailedException or PathOccupiedException;

    // One error-to-issue settlement: cause, kind, path, subject and retry dispatch are decided from the same exception and row.
    internal async Task<SyncOutcome> FailCoreAsync(Row row, SyncAction? action, Exception error, CancellationToken ct,
        Action<Issue>? committedIssue, Row? firstRunFolder, Guid? attempt, ItemRetryCandidate? expected, WorkSelection? selection)
    {
        var context = _context;
        if (firstRunFolder is not null && IssuePolicy.SameFirstRunLocal(firstRunFolder.Local, row.Local))
            row = row with { Context = row.Context with { FirstRunDeletion = ActionChanges.Owed(firstRunFolder, action) } };
        if (!IsItemError(error))
            ExceptionDispatchInfo.Throw(error);
        ct.ThrowIfCancellationRequested();
        var cause = error is PathOccupiedException && action?.Kind != ActionKind.KeepBoth
            ? TextKey.ClashNameTaken : FailureCause(error);
        var kind = IssueKind.Failure;
        var path = row.Local is null && row.Dropbox is null ? FailurePath(error) : Row.Path(row);
        var subject = IssuePolicy.IsLocalAccessRetryCause(cause) ? ActionExecutor.AttemptSubject(row) : ActionChanges.Subject(row);
        var failed = error as ItemFailedException;
        // First-run work lives only on its Failure (Decorate); a silent temporary wait would drop that obligation.
        var disposition = failed?.Disposition is FailureDisposition.TemporaryNoEffect &&
            ActionChanges.Owed(firstRunFolder ?? row, action) != IssueSubject.FirstRunDeletionKind.None
                ? FailureDisposition.Permanent : failed?.Disposition ?? FailureDisposition.Permanent;
        if (disposition == FailureDisposition.TemporaryNoEffect)
        {
            var accepted = SettleFailure(context.Store, context.Pair.PairId, context.ItemAttempts, StateChange.Empty, attempt,
                expected, selection?.SourceEpoch ?? context.Evidence.ReadEpoch(), ItemRetryDispatch.OrdinaryItem, null,
                null, selection, disposition, cause, path, subject, action?.Kind,
                failed?.ProviderWait, failed?.ProviderWaitStarted);
            EmitTemporary(context.Events, accepted, cause, path, error, action?.Kind);
            return new SyncOutcome(action?.Kind ?? ActionKind.None, NoneReason.Unfinished, null, null, false, cause);
        }
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
        // A continuing wait reports its captured items and Local spelling: a case-only rename may have respelled the
        // file, and the store finds the wait's notice by its exact path and items (43a).
        var captured = expected is not null && IssuePolicy.IsLocalAccessRetryCause(cause) ? expected.Token.Subject : null;
        path = captured?.Items.FirstOrDefault(item => item.Side == Side.Local)?.Path ?? path;
        var change = StateChange.Empty with { Raise = [(kind, cause, path, subject)] };
        change = ActionChanges.Decorate(change, row, action, false, firstRunFolder);
        subject = WithUnknownMove(captured is null ? change.Raise[0].Subject
            : change.Raise[0].Subject with { Items = captured.Items }, disposition, failed);
        if (!ReferenceEquals(subject, change.Raise[0].Subject)) change = change with { Raise = [(kind, cause, path, subject)] };
        var newInUse = expected is null && IssuePolicy.IsLocalAccessRetryCause(cause);
        var epoch = newInUse ? selection?.SourceEpoch ?? context.Evidence.ReadEpoch() : default;
        var pending = newInUse ? context.Store.Pending(context.Pair.PairId) as CurrentPending : null;
        List<RunEvent>? settled = null;
        SettleFailure(context.Store, context.Pair.PairId, context.ItemAttempts, change, attempt, expected,
            epoch, pending is null ? ItemRetryDispatch.OrdinaryItem : ItemRetryDispatch.LocalRecovery,
            pending?.Value, committedIssue, selection, disposition, cause, path,
            change.Raise[0].Subject, action?.Kind, failed?.ProviderWait, failed?.ProviderWaitStarted,
            (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew, error, cause, action?.Kind));
        ActionChanges.EmitSettled(context.Events, settled);
        return new SyncOutcome(action?.Kind ?? ActionKind.None, null, null, kind, false, cause);
    }

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

}
