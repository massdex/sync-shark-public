using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Sequences folder replacement removal and the fresh item admission that follows it.</summary>
internal readonly struct ItemReplacement(ExecutorContext _context, Parents _parents, ActionExecutor _executor,
    ILiveExecutionBoundary _live, ItemSync _items)
{
    internal static bool RequiresFolderReplacement(SyncAction action) =>
        action.Kind == ActionKind.ReplaceKind && action.Side == Side.Dropbox && action.Row.Dropbox is { IsFolder: true };

    /// <summary>Runs one live folder-replacement phase, registering its attempt exactly as the live item path does.</summary>
    internal Task<SyncOutcome> RunLiveReplacementAsync(ItemSync.LivePreparation prepared, WorkSelection selection, IssueSubject subject,
        ItemRetryCandidate? expected, Action<Row, Issue> retainUnfinished, CancellationToken ct)
    {
        var replacement = this;
        var executor = _executor;
        return _context.ItemAttempts.ExecuteAsync(selection, expected, subject, async (attemptId, token) =>
        {
            try
            {
                return await replacement.ReplaceDropboxFolderRegisteredAsync(prepared.Action, token, attemptId, expected, null,
                    selection, retainUnfinished, prepared);
            }
            catch (ChangedUnderneathException)
            {
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                return await executor.Failures.FailAsync(prepared.Action.Row, prepared.Action, error, token,
                    issue => retainUnfinished(prepared.Action.Row, issue), attemptId: attemptId, expectedAttempt: expected, selection: selection);
            }
        }, ct);
    }

    internal async Task<SyncOutcome> ReplaceDropboxFolderRegisteredAsync(SyncAction action, CancellationToken ct,
        Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, SyncOutcome>? completed,
        WorkSelection? selection, Action<Row, Issue>? retainUnfinished, ItemSync.LivePreparation? preparation = null)
    {
        var items = _items;
        var row = action.Row;
        if (_items.Admission.OutsideBatchScope(action, null)) throw new ChangedUnderneathException(Row.Path(row));
        var result = selection is null ? await _items.Folders.RemovePlannedReplacementAsync(row, ct) :
            await _items.Folders.RemoveLiveReplacementAsync(action, selection, ct);
        var (fresh, failed) = await AfterReplacementRemovalAsync(result, row, null, ct, selection, attemptId, expectedAttempt, retainUnfinished);
        if (failed is not null)
        {
            _items.Finish(action, failed, completed);
            return failed;
        }
        if (selection is not null)
        {
            var prepared = await _items.Preparation.PrepareLiveAsync(fresh, ct, previous: preparation);
            var footprint = LockKeys.ForLiveAction(prepared.Action, _context.Pair, prepared.Labels);
            var coverage = (await _live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            return await _live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (execution, token) => items.Execution.RunAdmittedAsync(prepared.Action.Row, token, preparation: prepared,
                    execution: execution, selection: selection, retainUnfinished: retainUnfinished, completed: completed,
                    preparedLabels: prepared.Labels, attemptId: attemptId, expectedAttempt: expectedAttempt), ct);
        }
        if (attemptId is { } registered)
        {
            var prepared = await _items.PrepareRowAsync(fresh, ct, completed,
                attemptId: registered, expectedAttempt: expectedAttempt);
            return prepared.Completed ?? await _items.Dispatch.DispatchPreparedAsync(prepared.Action, ct,
                completed: completed, attemptId: registered, expectedAttempt: expectedAttempt);
        }
        return await _items.RunRowAttemptAsync(fresh, ct, completed: completed);
    }

    internal async Task<SyncOutcome?> ReplacementRemovalFailureAsync(
        FolderRemovalResult? result, Row row, SyncAction? action, CancellationToken ct,
        WorkSelection? selection, Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, Issue>? retainUnfinished)
    {
        if (result?.Status == FolderRemovalStatus.Complete) return null;
        Exception error = result is null || action is null
            ? new ChangedUnderneathException(Row.Path(row))
            : new ItemFailedException(Row.Path(row),
                _context.Store.FolderRemoval(_context.Pair.PairId, result.OperationId).Cause ?? TextKey.FailReplacementRetained, null);
        return await _executor.Failures.FailAsync(row, action, error, ct,
            retainUnfinished is null ? null : issue => retainUnfinished(row, issue),
            attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection);
    }

    internal async Task<(Row Fresh, SyncOutcome? Failed)> AfterReplacementRemovalAsync(
        FolderRemovalResult? result, Row row, SyncAction? failureAction, CancellationToken ct,
        WorkSelection? selection, Guid? attemptId, ItemRetryCandidate? expectedAttempt, Action<Row, Issue>? retainUnfinished)
    {
        if (await ReplacementRemovalFailureAsync(result, row, failureAction, ct, selection, attemptId, expectedAttempt, retainUnfinished) is { } failed)
            return (row, failed);
        var fresh = await _items.Preparation.ReobserveAsync(row, new Observer(_context, _parents), ct);
        if (fresh.Dropbox is not null || fresh.Local != row.Local)
            return (fresh, await _executor.Failures.FailAsync(fresh, failureAction,
                new ChangedUnderneathException(Row.Path(fresh)), ct,
                retainUnfinished is null ? null : issue => retainUnfinished(fresh, issue),
                attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection));
        return (fresh, null);
    }

}
