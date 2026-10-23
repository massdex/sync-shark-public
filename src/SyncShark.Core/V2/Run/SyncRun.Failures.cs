namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<(Plan Plan, RebuildableResolution Rebuildables)> PreviewCoreAsync(string pairId,
        IReadOnlyDictionary<string, Side?>? winners, IReadOnlyList<OneSidedChoice>? oneSided,
        IReadOnlyList<RebuildableChoice>? rebuildableTypes, IReadOnlyList<RebuildableFolder>? frozenRebuildables,
        IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare, IProgress<RunProgress> progress,
        CancellationToken ct, IReadOnlyDictionary<string, string>? inventoryCursors = null,
        bool captureMatchedBothSides = true, IReadOnlyList<string>? narrowedIgnoreRules = null)
    {
        try
        {
            return await PreviewBodyAsync(pairId, winners, oneSided, rebuildableTypes, frozenRebuildables,
                matchedAtPrepare, progress, ct, inventoryCursors, captureMatchedBothSides, narrowedIgnoreRules);
        }
        catch (Exception error) when (RunFailure(error, ct) || error is SyncRunRefusedException && error.Message == IncompleteFirstSync)
        {
            RecordUnavailable(pairId, error);
            return (new Plan(pairId, 0, [], []) { RunUnavailable = error.Message }, RebuildableResolution.None);
        }
    }

    private async Task<RunReport> ApplyCoreAsync(string pairId, Plan approved, IProgress<RunProgress> progress,
        IReadOnlySet<string>? reviewedDeletes, RebuildableResolution? rebuildables, CancellationToken ct,
        EndpointContinuation? continuation)
    {
        if (approved.RunUnavailable is { } reason)
            return FailedRun(pairId, [new("", Outcome.Failed, null, null, null, reason)]);
        try
        {
            var report = await ApplyBodyAsync(pairId, approved, progress, reviewedDeletes, rebuildables, ct, continuation);
            if (!report.CompletionFailed) report = CompleteAvailable(pairId, report, ct);
            return report;
        }
        catch (Exception error) when (error.Data["SyncRun.ExecutionReport"] is RunReport || RunFailure(error, ct))
        {
            if (error.Data["SyncRun.ExecutionReport"] is RunReport counted)
            {
                var completion = new CompletionSteps(this, pairId, ct);
                completion.Failed(error);
                var report = completion.Finish(counted with { AttemptFailed = true });
                return report;
            }
            RecordUnavailable(pairId, error);
            return FailedRun(pairId, [new("", Outcome.Failed, null, null, null, error.Message)]);
        }
    }

    private Task<RunReport> IncrementalCoreAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct) =>
        IncrementalCoreAsync(pairId, decisions, resolvingNoticeId, progress, ct, null);

    private async Task<RunReport> IncrementalCoreAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct, RunOptions? options)
    {
        try
        {
            RetryNotices(pairId);
            var report = await IncrementalBodyAsync(pairId, decisions, resolvingNoticeId, progress, ct, options);
            if (!report.CompletionFailed && !report.AttemptFailed) report = CompleteAvailable(pairId, report, ct);
            return report;
        }
        catch (Exception error) when (error.Data["SyncRun.ExecutionReport"] is RunReport || RunFailure(error, ct))
        {
            if (error.Data["SyncRun.ExecutionReport"] is RunReport counted)
            {
                var completion = new CompletionSteps(this, pairId, ct);
                completion.Failed(error);
                var report = completion.Finish(counted with { AttemptFailed = true });
                return report;
            }
            RecordUnavailable(pairId, error);
            return FailedRun(pairId, [new("", Outcome.Failed, null, null, null, error.Message)]);
        }
    }

    private Plan HoldUnreviewedAtApply(PairDef pair, Plan plan, IReadOnlySet<string>? reviewed)
    {
        try { RefuseUnreviewedDeletes(pair, plan, reviewed); return plan; }
        catch (SyncRunRefusedException) when (reviewed is not null)
        {
            var paths = FolderDelete.SplitVictims(_store, pair, plan, reviewed).Guarded
                .Select(key => key[2..]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var held = ApplySafetyFilter(pair, ActionPredicates(pair, LeftAlone(pair).WithPending(paths)), plan);
            foreach (var path in paths)
                SaveFailedRetry(pair, path, "Deletion was not reviewed. Review the item or Ignore it; nothing was deleted.",
                    _store.SyncRetryAt(pair.PairId, path));
            RefuseUnreviewedDeletes(pair, held, reviewed); // Keep the absolute deletion assertion on the executable remainder.
            return held;
        }
    }

    private void RetainPlanningFailures(PairDef pair, Plan plan)
    {
        foreach (var group in plan.DeferredPlanningActions.GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase))
        {
            var previous = _store.SyncRetryAt(pair.PairId, group.Key);
            if (previous is not null && previous.Kind != "planning") continue;
            if (previous?.RetryAfter > _time.GetUtcNow()) continue;
            var failure = FailureRetryOwner.Failed(previous?.Attempts ?? 0, _time.GetUtcNow());
            _store.SaveSyncRetry(pair.PairId, new(group.Key, "planning", failure.Attempts,
                "Planning post-pass failed; original actions require safe replanning.", failure.RetryAfter,
                ProposedActions: group.ToArray()));
        }
        if (!plan.Notices.Any(notice => notice.ReasonKey == "planning-degraded") &&
            !_store.ReadSyncRetries(pair.PairId).Any(retry => retry.Kind == "planning"))
            foreach (var notice in _store.UnresolvedNotices(pair.PairId))
                if (notice.ReasonKey == "planning-degraded" && notice.NoticeId is not null)
                    _store.CloseNotice(notice.NoticeId, "resolved");
    }

    private RunReport CompleteAvailable(string pairId, RunReport report, CancellationToken ct)
    {
        var completion = new CompletionSteps(this, pairId, ct);
        if (_store is SqliteStateStore sql) completion.Step(sql.RetryResultMaintenance);
        if (completion.Step(() => CloseUnavailable(pairId)) && report.IncludesRetryDebt)
            completion.Step(() => report = report with { Deferred = _store.CountSyncRetries(pairId) });
        return completion.Finish(report);
    }

    private void CloseUnavailable(string pairId)
    {
        if (_store.SyncRetryAt(pairId, string.Empty)?.Kind == "run")
            _store.ClearSyncRetry(pairId, string.Empty);
        foreach (var notice in _store.UnresolvedNotices(pairId))
            if (notice.ReasonKey == "run-unavailable" && notice.NoticeId is not null)
                _store.CloseNotice(notice.NoticeId, "resolved");
    }

    private void WriteNotices(IEnumerable<Notice> notices)
    {
        foreach (var notice in notices)
        {
            try
            {
                if (_store.SyncRetryAt(notice.PairId, notice.Path)?.PendingNotice is { DeliveryRetry: { } delivery } pending &&
                    delivery.RetryAfter > _time.GetUtcNow() && pending.ReasonKey == notice.ReasonKey)
                    continue; // Repeated publication is not another delivery attempt before its durable deadline.
                if (notice.ReasonKey == PlanNotices.CannotRead.Key && _store.Pair(notice.PairId) is { } unreadablePair)
                {
                    var prior = _store.SyncRetryAt(notice.PairId, notice.Path);
                    if (prior is null || prior.Kind == "scan" && prior.RetryAfter <= _time.GetUtcNow())
                    {
                        var state = FailureRetryOwner.Failed(prior?.Attempts ?? 0, _time.GetUtcNow());
                        _store.SaveSyncRetry(notice.PairId, new(notice.Path, "scan", state.Attempts,
                            notice.Detail, state.RetryAfter, ReadRoots: [unreadablePair.A.RootId, unreadablePair.B.RootId]));
                    }
                }
                _store.AddNotices([notice with { DeliveryRetry = null }]);
                if (_store.SyncRetryAt(notice.PairId, notice.Path) is { PendingNotice: not null } saved)
                    _store.SaveSyncRetry(notice.PairId, saved with { PendingNotice = null,
                        RetryAfter = saved.Kind == "decision" ? DateTimeOffset.MaxValue : saved.RetryAfter });
            }
            catch (Exception error) when (RunFailure(error, CancellationToken.None))
            {
                RunLog.Trace("Notice incomplete for " + notice.Path + ": " + error.Message);
                SavePendingNoticeFailure(notice, error);
            }
        }
    }

    private void SavePendingNoticeFailure(Notice notice, Exception error)
    {
        var previous = _store.SyncRetryAt(notice.PairId, notice.Path);
        var delivery = FailureRetryOwner.Failed(previous?.PendingNotice?.DeliveryRetry?.Attempts ?? 0, _time.GetUtcNow());
        _store.SaveSyncRetry(notice.PairId,
            (previous ?? new SyncRetry(notice.Path, "sync", 0, "Notice: " + error.Message, DateTimeOffset.MinValue)) with
            { PendingNotice = notice with { DeliveryRetry = delivery } });
    }

    private void RetryNotices(string pairId)
    {
        foreach (var retry in _store.ReadSyncRetries(pairId))
        {
            if (retry.PendingNotice is not { } notice || notice.DeliveryRetry?.RetryAfter > _time.GetUtcNow()) continue;
            try { _store.AddNotices([notice with { DeliveryRetry = null }]); }
            catch (Exception error) when (RunFailure(error, CancellationToken.None))
            {
                RunLog.Trace("Notice retry remains incomplete: " + error.Message);
                SavePendingNoticeFailure(notice, error);
                continue;
            }
            _store.SaveSyncRetry(pairId, retry with { PendingNotice = null,
                RetryAfter = retry.Kind == "decision" ? DateTimeOffset.MaxValue : retry.RetryAfter });
        }
    }

    private void RecordUnavailable(string pairId, Exception error, bool completion = false)
    {
        RunLog.Trace("Run incomplete; automatic retry required: " + error.ToString());
        if (FailureRetryOwner.ScopeOf(error, CancellationToken.None) == FailureRetryOwner.FailureScope.State)
            return; // Fatal persistence is reported in memory, never written back through the damaged store.
        try
        {
            if (_store.Pair(pairId) is null) return;
            FailureRetryOwner.RecordRunFailure(_store, pairId, error, _time.GetUtcNow());
        }
        catch (Exception writeError) when (RunFailure(writeError, CancellationToken.None))
        { RunLog.Trace("Unable to persist run notice; retry on next run: " + writeError.ToString()); }
    }

    private static bool RunFailure(Exception error, CancellationToken ct) =>
        FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item &&
        error is not SyncRunRefusedException;

    // Never reinterpret cancellation, lost endpoint authority or damaged persistence as a
    // successful item. Those conditions cannot be repaired by skipping an individual file.
    private static bool ScopeFailure(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        error is not (PairPausedException or EndpointAccessException or StateDamagedException or SyncRunRefusedException) &&
        (error is not OperationCanceledException || ProviderTransientFailure.IsTransient(error, ct)) &&
        error is not Microsoft.Data.Sqlite.SqliteException;
}
