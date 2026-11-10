using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Runs a prepared item inside its admitted footprint and owns its one retry lifetime.</summary>
internal readonly struct ItemExecution(ExecutorContext _context, Parents _parents, ActionExecutor _executor,
    ItemSync items)
{
    // The loop holds the re-observation decision and the prepared action it must re-run in one admission lifetime.
    internal async Task<SyncOutcome> RunAdmittedAsync(Row row, CancellationToken ct, Observer? observer = null, SyncAction? preparedAction = null,
        IAdmittedExecution? execution = null, WorkSelection? selection = null,
        Action<Row, Issue>? retainUnfinished = null,
        Action<Row, SyncOutcome>? completed = null, KeepBothLabels? preparedLabels = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, bool reobserve = false,
        ItemSync.LivePreparation? preparation = null, Func<ValueTask>? uploadStaged = null)
    {
        var context = _context;
        var parents = _parents;
        var preparationOwner = items.Preparation;
        var action = preparation?.Action ?? preparedAction;
        var unchanged = preparation?.Unchanged == true;
        var retried = reobserve || preparation?.Retried == true;
        var mayAdopt = preparation?.Retried != true;
        var adopted = false;
        var transferred = false;
        var owned = _context.Evidence.CaptureIfHashed(row);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Action<Issue>? committed = retainUnfinished is null ? null : issue => retainUnfinished(row, issue);
                try
                {
                    if (!adopted && (reobserve || action?.Kind is ActionKind.Record or ActionKind.DeferFolder))
                    {
                        if (reobserve) _context.Events(RunEvent.RetryChanged(Row.Path(row)));
                        // A live selection runs with no work scope of its own, so an unrelated sole scope must not let the
                        // replacement action stage direct; the limiter keeps deciding, this only withdraws it for the turn.
                        _context.Admission.DisallowDirectUpload();
                        await RefreshAndRevalidateAsync(compare: reobserve);
                        action = null;
                    }
                    action ??= await items.Preparation.PrepareAsync(row, ct);
                    row = action.Row;
                    owned = _context.Evidence.CaptureIfHashed(row);
                    if (ItemReplacement.RequiresFolderReplacement(action))
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
                    // A folder plan's footprint admits its descendants only for that plan (B-153): a different action
                    // hands its row back once, so it is re-admitted on its own ordinary footprint.
                    if (selection is null && preparation?.Action.Kind == ActionKind.DeferFolder &&
                        action.Kind != ActionKind.DeferFolder)
                    {
                        transferred = mayAdopt;
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged)
                            { HandoffRow = mayAdopt ? row : null };
                    }
                    if (action.Kind is ActionKind.None or ActionKind.DeferFolder)
                    {
                        var outcome = await _executor.ExecuteAsync(action, null, ct, committedIssue: committed,
                            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
                        items.Finish(action, outcome, completed);
                        return outcome;
                    }
                    (SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal) result;
                    try
                    {
                        result = await items.Admission.RunAsync(action, preparedLabels, execution, adopted, ct,
                            uploadStaged, committed, completed, selection, attemptId, expectedAttempt);
                    }
                    catch (LiveAdmissionStaleException error) when (error.HandoffRow is not null)
                    {
                        transferred = true;
                        throw;
                    }
                    if (result.Outcome is { } finished)
                    {
                        return finished;
                    }
                    if (!result.Reobserve)
                    {
                        preparedLabels = null;
                        action = null;
                        reobserve = false;
                        continue;
                    }
                    var observed = result.ObservedLocal;
                    if (mayAdopt && observed is not null)
                    {
                        var rebound = await RefreshAndRevalidateAsync(observed);
                        var currentHash = rebound.Hash;
                        if (currentHash is not null && !unchanged)
                        {
                            action = await items.Preparation.PrepareAsync(row, ct, rebound.Evidence);
                            row = action.Row;
                            owned = _context.Evidence.CaptureIfHashed(row);
                            adopted = true;
                            mayAdopt = false;
                            if (!retried) _context.Events(RunEvent.RetryChanged(Row.Path(row)));
                            retried = true;
                            reobserve = false;
                            preparedLabels = null;
                            continue;
                        }
                    }
                    adopted = false;
                }
                catch (ChangedUnderneathException error) when (selection is not null && !IssuePolicy.CanRetryChanged(error))
                {
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                }
                catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
                {
                    // A thrown race proves new change even when the preceding observation matched.
                    unchanged = false;
                }
                catch (Exception error) when (ItemFailures.IsItemError(error))
                {
                    return await FailAndFinishAsync(row, action, error, ct, committed, completed,
                        attemptId, expectedAttempt, selection);
                }
                if (IssuePolicy.StaleRetryFailed(retried, unchanged, selection is not null))
                {
                    return await FailAndFinishAsync(row, action, new ChangedUnderneathException(Row.Path(row)), ct,
                        committed, completed, attemptId, expectedAttempt, selection);
                }
                // A live item awaits its own next acquisition; a batch child fails without holding its siblings.
                if (IssuePolicy.StaleRetryWaits(retried, selection is not null))
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                retried = reobserve = true;
            }
        }
        finally
        {
            if (!transferred) _context.Evidence.Release(row, owned);
        }

        async ValueTask<(LocalEvidence.DirtyProof? Evidence, string? Hash)> RefreshAndRevalidateAsync(
            Entry? observed = null, bool compare = true)
        {
            observer ??= new Observer(context, parents);
            var refreshed = await preparationOwner.RefreshAsync(row, owned, observer, ct, observed, compare);
            unchanged = refreshed.Unchanged;
            row = refreshed.Row;
            owned = refreshed.Owned;
            // Transfer custody before revalidation: a refusal must release the newly observed proof.
            if (execution is not null && selection is not null) execution.Revalidate();
            return (refreshed.Evidence, refreshed.Hash);
        }
    }

    internal async ValueTask<SyncOutcome> FailAndFinishAsync(Row row, SyncAction? action, Exception error,
        CancellationToken ct, Action<Issue>? committed, Action<Row, SyncOutcome>? completed,
        Guid? attemptId, ItemRetryCandidate? expectedAttempt, WorkSelection? selection)
    {
        var failed = await _executor.Failures.FailAsync(row, action, error, ct, committed,
            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
        if (action is not null) items.Finish(action, failed, completed);
        else completed?.Invoke(row, failed);
        return failed;
    }


}
