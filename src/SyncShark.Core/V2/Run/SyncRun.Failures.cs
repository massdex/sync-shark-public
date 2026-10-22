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
                return completion.Finish(counted);
            }
            RecordUnavailable(pairId, error);
            return FailedRun(pairId, [new("", Outcome.Failed, null, null, null, error.Message)]);
        }
    }

    private async Task<RunReport> IncrementalCoreAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct)
    {
        try
        {
            RetryNotices(pairId);
            var report = await IncrementalBodyAsync(pairId, decisions, resolvingNoticeId, progress, ct);
            if (!report.CompletionFailed && report.Outcome != RunOutcomes.Failed) report = CompleteAvailable(pairId, report, ct);
            return report;
        }
        catch (Exception error) when (error.Data["SyncRun.ExecutionReport"] is RunReport || RunFailure(error, ct))
        {
            if (error.Data["SyncRun.ExecutionReport"] is RunReport counted)
            {
                var completion = new CompletionSteps(this, pairId, ct);
                completion.Failed(error);
                return completion.Finish(counted);
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
            _store.SaveSyncRetry(pair.PairId, new(group.Key, "planning", (previous?.Attempts ?? 0) + 1,
                "Planning post-pass failed; original actions require safe replanning.", DateTimeOffset.MinValue,
                ProposedActions: group.ToArray()));
        }
        if (!plan.Notices.Any(notice => notice.ReasonKey == "planning-degraded") &&
            !_store.ReadSyncRetries(pair.PairId).Any(retry => retry.Kind == "planning"))
            foreach (var notice in _store.OpenNotices(pair.PairId))
                if (notice.ReasonKey == "planning-degraded" && notice.NoticeId is not null)
                    _store.CloseNotice(notice.NoticeId, "resolved");
    }

    private RunReport CompleteAvailable(string pairId, RunReport report, CancellationToken ct)
    {
        var completion = new CompletionSteps(this, pairId, ct);
        if (_store is SqliteStateStore sql) completion.Step(sql.RetryResultMaintenance);
        if (completion.Step(() => CloseUnavailable(pairId)) && report.OutcomeBeforeRetryDebt is { } effectOutcome)
            completion.Step(() => report = IncrementalOutcome(report with
            {
                Outcome = effectOutcome,
                Deferred = _store.CountSyncRetries(pairId),
                OutcomeBeforeRetryDebt = null
            }, report.DeletionEvidencePending));
        return completion.Finish(report);
    }

    private void CloseUnavailable(string pairId)
    {
        if (_store.SyncRetryAt(pairId, string.Empty)?.Kind == "run")
            _store.ClearSyncRetry(pairId, string.Empty);
        foreach (var notice in _store.OpenNotices(pairId))
            if (notice.ReasonKey == "run-unavailable" && notice.NoticeId is not null)
                _store.CloseNotice(notice.NoticeId, "resolved");
    }

    private void WriteNotices(IEnumerable<Notice> notices)
    {
        foreach (var notice in notices)
        {
            try
            {
                _store.AddNotices([notice]);
                if (_store.SyncRetryAt(notice.PairId, notice.Path) is { PendingNotice: not null } saved)
                    _store.SaveSyncRetry(notice.PairId, saved with { PendingNotice = null,
                        RetryAfter = saved.Kind == "decision" ? DateTimeOffset.MaxValue : saved.RetryAfter });
            }
            catch (Exception error) when (RunFailure(error, CancellationToken.None))
            {
                RunLog.Trace("Notice incomplete for " + notice.Path + ": " + error.Message);
                var previous = _store.SyncRetryAt(notice.PairId, notice.Path);
                _store.SaveSyncRetry(notice.PairId,
                    (previous ?? new SyncRetry(notice.Path, "sync", 0, "", DateTimeOffset.MinValue)) with
                    { PendingNotice = notice, LastError = "Notice: " + error.Message, RetryAfter = DateTimeOffset.MinValue });
            }
        }
    }

    private void RetryNotices(string pairId)
    {
        foreach (var retry in _store.ReadSyncRetries(pairId))
        {
            if (retry.PendingNotice is not { } notice) continue;
            try { _store.AddNotices([notice]); }
            catch (Exception error) when (RunFailure(error, CancellationToken.None))
            { RunLog.Trace("Notice retry remains incomplete: " + error.Message); continue; }
            _store.SaveSyncRetry(pairId, retry with { PendingNotice = null,
                RetryAfter = retry.Kind == "decision" ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue });
        }
    }

    private void RecordUnavailable(string pairId, Exception error, bool completion = false)
    {
        RunLog.Trace("Run incomplete; automatic retry required: " + error.ToString());
        try
        {
            if (_store.Pair(pairId) is null) return;
            var previous = _store.SyncRetryAt(pairId, string.Empty);
            if (previous is null || previous.Kind == "run")
                _store.SaveSyncRetry(pairId, new(string.Empty, "run", (previous?.Attempts ?? 0) + 1,
                    error.Message, _time.GetUtcNow().AddSeconds(30)));
            var cause = error is StateDamagedException ? "store-damage" : error is EndpointAccessException ? "account-access" : "run-unavailable";
            var fix = error is StateDamagedException
                ? "Check state-store diagnostics and restore a verified backup if needed, then Retry."
                : error is EndpointAccessException ? "Reconnect the account, then Retry."
                : "Retry to save the remaining bookkeeping; automatic retries continue. If it persists, inspect the run log for the failed operation. Ignore dismisses this notice without excluding files.";
            var message = completion
                ? "Sync finished its file changes but could not save its final bookkeeping (" + cause + "): " + error.Message + ". " + fix
                : "Sync could not proceed: " + error.Message + ". Check endpoint access and available disk space; sync will retry automatically.";
            _store.AddNotices([new Notice(pairId, cause == "run-unavailable" ? 40 : 0, NoticeKind.Info, "", message, cause == "run-unavailable" ? "run-unavailable" : "autosync-failed")]);
        }
        catch (Exception writeError) when (RunFailure(writeError, CancellationToken.None))
        { RunLog.Trace("Unable to persist run notice; retry on next run: " + writeError.ToString()); }
    }

    private static bool RunFailure(Exception error, CancellationToken ct) =>
        !(error is OperationCanceledException && ct.IsCancellationRequested) &&
        error is not (PairPausedException or StateDamagedException or SyncRunRefusedException or EndpointAccessException) &&
        !(error is Microsoft.Data.Sqlite.SqliteException sql && StoreSchema.IsDamaged(sql));

    // Never reinterpret cancellation, lost endpoint authority or damaged persistence as a
    // successful item. Those conditions cannot be repaired by skipping an individual file.
    private static bool ScopeFailure(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        error is not (PairPausedException or EndpointAccessException or StateDamagedException or SyncRunRefusedException) &&
        (error is not OperationCanceledException || ProviderTransientFailure.IsTransient(error, ct)) &&
        error is not Microsoft.Data.Sqlite.SqliteException;
}
