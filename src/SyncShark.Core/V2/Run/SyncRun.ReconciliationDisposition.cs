namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private IReadOnlyList<SqliteStateStore.DeferredPlanning> ReconciliationPlanningPage(PairDef pair, ReconcileBatch batch)
    {
        var sql = (SqliteStateStore)_store;
        var rows = sql.DeferredPlanningFor(pair.PairId, pair.A.RootId, batch.ObjectIdsA)
            .Concat(sql.DeferredPlanningFor(pair.PairId, pair.B.RootId, batch.ObjectIdsB)).ToArray();
        return RetireNonWorkPlanning(sql, pair, rows);
    }

    // Completion retires only selected planning retries, never importing their saved actions.
    private IReadOnlyList<SyncRetry> CompletionRetries(string pairId, ReconcileBatch? batch,
        Plan plan, IReadOnlyList<SyncRetry> retries) => batch is null ? retries : batch.Paths
        .Select(path => _store.SyncRetryAt(pairId, path)).OfType<SyncRetry>()
        .Where(retry => retry.Kind == "planning" && !plan.DeferredPlanningActions.Any(
            action => PairPath.Same(action.Path, retry.Path))).ToArray();

    private void AccountReconciliationBatch(PairDef pair, ReconcileBatch batch, Plan plan, Tally tally,
        Func<string, bool> settled, Func<string, bool> includeA, Func<string, bool> includeB)
    {
        foreach (var action in plan.DeferredPlanningActions)
            if (action.Source?.Kind == ItemKind.Folder || action.ExpectedTarget?.Kind == ItemKind.Folder)
            {
                batch.DeferredFolders.Add(action.Path);
                if (action.FromPath is { } from) batch.DeferredFolders.Add(from);
                if (action.Source is { } source) batch.DeferredFolders.Add(source.Path);
            }
        var excluded = ScanPathsNotSynced(pair);
        foreach (var path in batch.Paths)
        {
            if (!includeA(path) || !includeB(path) || excluded(path))
                batch.Dispositions[path] = MessageCatalog.Default.Template("reconciliation-excluded");
            else if (plan.Notices.FirstOrDefault(notice => PlanNotices.HasPathScope(notice) &&
                PairPath.IsSelfOrUnder(path, notice.Path)) is { } notice)
                batch.Dispositions[path] = "notice: " + notice.ReasonKey;
            else if (_store.SyncRetryAt(pair.PairId, path) is { LastError.Length: > 0 } retry)
                batch.Dispositions[path] = "deferred: " + retry.LastError;
            else if (!batch.Dispositions.ContainsKey(path))
            {
                var actions = plan.Actions.Where(action => PairPath.Same(action.Path, path) ||
                    action.Source?.Path is { } source && PairPath.Same(source, path) ||
                    action.FromPath is { } from && PairPath.Same(from, path)).ToArray();
                if (actions.Length > 0 && actions.All(action => tally.WasDone(action.ActionId)))
                    batch.Dispositions[path] = "actioned: " + string.Join(",", actions.Select(action => action.Kind).Distinct());
                else if (settled(path)) batch.Dispositions[path] = MessageCatalog.Default.Template("reconciliation-settled");
            }
        }
    }

    private void RecordReconciliationBlocker(PairDef pair, ReconcileBatch batch,
        SqliteStateStore.DeferredPlanning row, string blocker, BothScan scan, Plan plan)
    {
        foreach (var path in row.Paths)
        {
            // A concrete read/action cause takes precedence over structural acknowledgement diagnostics.
            var notice = plan.Notices.FirstOrDefault(item => PlanNotices.HasPathScope(item) &&
                PairPath.IsSelfOrUnder(path, item.Path));
            var retry = _store.SyncRetryAt(pair.PairId, path);
            var cause = scan.A.IncompleteReasons.GetValueOrDefault(path) ??
                scan.B.IncompleteReasons.GetValueOrDefault(path) ?? notice?.Detail ??
                (retry?.LastError is { Length: > 0 } error ? error : null) ??
                plan.Notices.FirstOrDefault(item => item.ReasonKey == "planning-degraded")?.Detail ?? blocker;
            batch.Dispositions[path] = "deferred: " + cause;
            if (notice is null || !PairPath.Same(notice.Path, path))
                WriteNotices([FailureRetryOwner.RetryNotice(pair.PairId, path, cause, 1)]);
            RunTrace.Emit($"reconciliation acknowledgement blocked: path={path} reason={cause}");
        }
    }

    private void FinishReconciliationDisposition(PairDef pair, ReconcileBatch batch)
    {
        foreach (var path in batch.Paths)
        {
            if (!batch.Dispositions.ContainsKey(path))
            {
                var retry = _store.SyncRetryAt(pair.PairId, path);
                var reason = retry?.LastError is { Length: > 0 } detail ? detail :
                    MessageCatalog.Default.Template("reconciliation-unsettled");
                // The existing failure owner supplies Retry/Ignore and durable retry input. Retiring TEMP
                // selection is never a substitute for a real disposition or a saved unfinished item.
                SaveFailedRetry(pair, path, reason, retry);
                batch.Dispositions[path] = "notice: " + reason;
            }
            RunTrace.Emit($"reconciliation disposition: path={path} {batch.Dispositions[path]}");
        }
        var counts = batch.Dispositions.Values.GroupBy(value => value.Split(':')[0])
            .Select(group => group.Key + "=" + group.Count());
        RunTrace.Emit("reconciliation batch dispositions: selected=" + batch.Paths.Count + " " + string.Join(" ", counts));
    }
}
