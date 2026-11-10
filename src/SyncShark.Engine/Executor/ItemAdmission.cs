using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Validates and locks one admitted action before executing its exact footprint.</summary>
internal readonly struct ItemAdmission(ExecutorContext _context, Parents _parents, ActionExecutor _executor,
    KeepBothRunner _keepBoth, AncestorFence _ancestors, ItemSync items)
{
    internal async ValueTask<(SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal)> RunAsync(
        SyncAction action, KeepBothLabels? preparedLabels, IAdmittedExecution? execution, bool adopted,
        CancellationToken ct, Func<ValueTask>? uploadStaged, Action<Issue>? committed,
        Action<Row, SyncOutcome>? completed, WorkSelection? selection, Guid? attemptId, ItemRetryCandidate? expectedAttempt)
    {
        var labels = action.Kind == ActionKind.KeepBoth
            ? preparedLabels ?? await _keepBoth.ChooseLabelsAsync(action.Row, ct) : null;
        var keys = LockKeys.For(action, _context.Pair, labels);
        if (execution is not null)
        {
            try { LockKeys.Require(execution, _context.Pair, _context.ExpectedGeneration, action, labels, keys); }
            catch (LiveAdmissionStaleException error) when (adopted &&
                error.Reason == LiveAdmissionStaleReason.FootprintChanged)
            {
                throw new LiveAdmissionStaleException(error.Reason) { HandoffRow = action.Row };
            }
        }
        await using var hold = execution is null ? await PathLocks.Shared.EnterAsync(keys, ct) : null;
        var result = await ExecuteLockedAsync(action, labels, keys, keys, ct, uploadStaged, committed,
            selection: selection, attemptId: attemptId, expectedAttempt: expectedAttempt);
        if (result.Outcome is { } outcome) items.Finish(action, outcome, completed);
        if (result.Outcome is null && !result.Reobserve && execution is not null)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        return result;
    }

    // Ordered admission gates return one execution-or-reobserve answer and pass their observed target to the effect without another read.
    internal async ValueTask<(SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal)> ExecuteLockedAsync(
        SyncAction action, KeepBothLabels? labels, IReadOnlyCollection<string> requiredKeys,
        IReadOnlyCollection<string> heldKeys, CancellationToken ct,
        Func<ValueTask>? uploadStaged, Action<Issue>? committedIssue, Row? firstRunFolder = null, WorkSelection? selection = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, bool closeIssues = true, Guid? listedScanId = null)
    {
        ArgumentNullException.ThrowIfNull(requiredKeys);
        ArgumentNullException.ThrowIfNull(heldKeys);
        if (!ReferenceEquals(requiredKeys, heldKeys) && requiredKeys.Any(key => !heldKeys.Contains(key, PathLocks.KeyComparer)))
            return (null, true, null);
        if (OutsideBatchScope(action, labels))
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection,
                outsideBatchScope: true);
        var news = await _ancestors.BeforeExecutionAsync(action, _parents, ct);
        if (news == AncestorNews.Changed)
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        (bool Fresh, (Side Side, string Path, Entry? Entry)? ObservedTarget) fresh;
        try { fresh = await new ItemFreshness(_context, items).IsFreshAsync(action.Row, action, ct, listedScanId); }
        catch (PathOccupiedException) when (news == AncestorNews.Restated)
        {
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        }
        if (news == AncestorNews.Restated && (!fresh.Fresh || fresh.ObservedTarget is not { Entry: null }))
        {
            return await RefusedAsync(action, ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection);
        }
        if (!fresh.Fresh || !ReferenceEquals(items.Rows.RefreshInventory(action.Row), action.Row))
        {
            return (null, true, null);
        }
        if (labels is not null && !await _keepBoth.AreUsableAsync(action.Row, labels, ct)) return (null, false, null);
        // Refuse known debt before preparation; effect owners recheck after inputs may change asynchronously.
        ItemSync.RequireLiveWrite(_context, action.WritesLocal());
        try
        {
            return (await _executor.ExecuteAsync(action, labels, ct, closeIssues, uploadStaged,
                observedTarget: fresh.ObservedTarget, committedIssue: committedIssue, firstRunFolder: firstRunFolder,
                attemptId: attemptId, expectedAttempt: expectedAttempt, selection: selection, listedScanId: listedScanId), false, null);
        }
        catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
        {
            return (null, true, error.ObservedLocal);
        }
    }

    internal bool OutsideBatchScope(SyncAction action, KeepBothLabels? labels)
    {
        var pair = _context.Pair;
        if (_context.BatchScope is not { } scope) return false;
        return LockKeys.ForLiveAction(action, pair, labels)
            .Any(item => item.RelativeIn(pair) is not { } relative || !PathNames.IsSelfOrUnder(relative, scope));
    }

    // A private method, not a local function: a local would capture action and ct in a heap closure on every call.
    private async ValueTask<(SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal)> RefusedAsync(SyncAction action, CancellationToken ct,
        Action<Issue>? committedIssue, Row? firstRunFolder, Guid? attemptId, ItemRetryCandidate? expectedAttempt,
        WorkSelection? selection, bool outsideBatchScope = false)
    {
        if (_context.BatchScope is not null && !outsideBatchScope)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        return (await _executor.Failures.FailAsync(action.Row, action, new ChangedUnderneathException(Row.Path(action.Row)),
            ct, committedIssue, firstRunFolder, attemptId, expectedAttempt, selection), false, null);
    }

}
