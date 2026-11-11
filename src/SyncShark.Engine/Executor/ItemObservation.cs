using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Observes standalone follow-ups and maintains their skipped-entry notices.</summary>
internal readonly struct ItemObservation(ExecutorContext _context, Parents _parents, ActionExecutor _executor, ItemSync items)
{
    /// <summary>Observes one follow-up or survivor and runs it. An observation or context read that fails is that item's
    /// Failure and the caller carries on; cancellation and authority refusals still escape.</summary>
    internal async Task<SyncOutcome> SyncFollowUpAsync(Side side, string path, CancellationToken ct,
        IAdmittedExecution? execution = null,
        Action<Row, Issue>? retainUnfinished = null, Action<Row, SyncOutcome>? completed = null)
    {
        var observer = new Observer(_context, _parents);
        Row row;
        try { row = await ObserveFollowUpAsync(observer, side, path, ct); }
        catch (ItemFailedException error)
        {
            // Keep the requested path for callbacks; the failure names the exception's path.
            var unobserved = new Row(path, null, null, null, [], null, RowContext.Neutral);
            Action<Issue>? committed = retainUnfinished is null ? null : issue => retainUnfinished(unobserved, issue);
            var failed = await _executor.Failures.FailAsync(unobserved, null, error, ct, committed);
            completed?.Invoke(unobserved, failed);
            return failed;
        }
        if (row.Context.Skipped) await ObserveSkippedAsync(row, execution, ct);
        // An admitted survivor keeps its batch's lifetime (FolderSync). A standalone follow-up (a Retry) captures after its
        // own fresh observation, as a Repair row does: a replaced file's episode is retired on a current read, and a
        // matching episode runs as this attempt, so even a Record or in-sync outcome completes it, and the admitted run
        // needs no second capture (B-177). A refused capture or completion fails that item as changed underneath, as a
        // Repair row's does (ItemSync.RunRowAttemptAsync): visible, its episode and work kept for the next opportunity.
        if (execution is not null)
            return await items.Execution.RunAdmittedAsync(row, ct, observer: observer, execution: execution,
                retainUnfinished: retainUnfinished, completed: completed);
        var run = items.Execution;
        try
        {
            var (subject, expected) = await items.CaptureAttemptAsync(row, ct);
            return await _context.ItemAttempts.ExecuteAsync(_context.Opportunity, expected, subject,
                (attempt, token) => run.RunAdmittedAsync(row, token, observer: observer,
                    retainUnfinished: retainUnfinished, completed: completed, attemptId: attempt, expectedAttempt: expected), ct);
        }
        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.SelectionChanged)
        {
            Action<Issue>? committed = retainUnfinished is null ? null : issue => retainUnfinished(row, issue);
            return await run.FailAndFinishAsync(row, null, new ChangedUnderneathException(Row.Path(row), error), ct,
                committed, completed, null, null, null);
        }
    }

    // A follow-up or survivor belongs to no listing: its facts are the Store's at this moment, on no run's sets.
    private async Task<Row> ObserveFollowUpAsync(Observer observer, Side side, string path, CancellationToken ct)
    {
        Row row;
        try { row = await observer.ObserveAsync(side, path, ct); }
        catch (InventoryIncompleteException error)
        {
            throw ItemFailures.InventoryFailure(error, path, side);
        }
        return row with { Context = await items.Rows.ForFollowUpAsync(row, ct) };
    }

    private async Task ObserveSkippedAsync(Row row, IAdmittedExecution? execution, CancellationToken ct)
    {
        var paths = Row.Paths(row);
        var keys = LockKeys.ForPaths(_context.Pair, [(Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox)]);
        if (execution is null)
        {
            await using var hold = await PathLocks.Shared.EnterAsync(keys, ct);
            UpdateSkipped(row, paths);
            return;
        }
        // A survivor runs inside an outer admission that already holds these keys; PathLocks is not reentrant,
        // so the existing execution is validated instead of taking the keys again.
        execution.Require(_context.Pair.PairId, _context.ExpectedGeneration, keys.ToArray());
        UpdateSkipped(row, paths);
    }

    private void UpdateSkipped(Row row, (string Local, string Dropbox) paths)
    {
        // The row is already observed: maintaining its skipped notice needs no further provider read.
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var entry = row.EntryOn(side);
            var path = side == Side.Local ? paths.Local : paths.Dropbox;
            if (side == Side.Local && !PathNames.IsTransferable(path)) continue;
            _context.Store.ObserveSkipped(_context.Pair.PairId, _context.ExpectedGeneration, side, path,
                entry?.Skip is { } cause
                    ? (Enum.Parse<TextKey>(cause), new IssueSubject([new IssueItem(side, entry.Path, entry.Id)], side)) : null);
        }
    }

}
