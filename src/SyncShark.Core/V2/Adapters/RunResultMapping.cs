namespace Massdex.SyncShark.V2;

/// <summary>Projects one pair's run outcome onto the window's batch records.</summary>
internal static class RunResultMapping
{
    /// <summary>One progress report as the window's batch progress; the phase identity stays stable, display text is Detail.</summary>
    internal static SyncAuthorityBatchProgress ToProgress(SyncPair pair, RunProgress progress,
        DateTimeOffset started, DateTimeOffset now) => new(pair, progress.Phase, null, null, null,
        Comparison: new SyncComparisonProgress(progress.Phase, progress.Path ?? string.Empty, (int)progress.Done,
            (int)progress.Total, started, now) { Detail = progress.Message ?? string.Empty });

    /// <summary>One pair's report and observed results as the window's batch item.</summary>
    internal static SyncAuthorityBatchItemResult ToItem(SyncPair pair, PairDef pairDef, RunReport report,
        IReadOnlyList<ObservedExecutor.Observation> observed)
    {
        var decisions = new List<SyncDecision>();
        foreach (var item in observed)
        {
            var decision = item.Outcome is null
                ? new SyncDecision(item.Path, SyncDecisionKind.Conflict, PlanNotices.NeedDecision.Detail)
                : Decision(item, pairDef);
            if (decision is null) continue;
            decisions.Add(decision);
        }
        var result = new SyncRunResult(pair.Id, decisions)
        {
            Outcome = report.Outcome, Pending = report.Pending, Deferred = report.Deferred,
            IndexingRemainingByRoot = report.IndexingRemainingByRoot,
            Counts = new SyncRunCounts(report.Done, report.Uploaded, report.Downloaded, report.Deleted,
                report.Moved, report.KeptBoth, report.Held, report.Failed, report.Uncertain, report.Skipped, report.Deferred)
        };
        var failureType = report.Outcome switch
        {
            RunOutcomes.Failed => "failed",
            RunOutcomes.StateDamaged => RunOutcomes.StateDamaged,
            _ => null
        };
        var reason = report.Outcome == RunOutcomes.StateDamaged
            ? SyncRunOutcomeProjection.FromResult(result).NotificationDetail
            : failureType is not null ? FailureReasons(report) : null;
        return new SyncAuthorityBatchItemResult(pair, result, failureType, reason);
    }

    private static SyncDecision? Decision(ObservedExecutor.Observation row, PairDef pairDef) =>
        row.Outcome switch
        {
            Outcome.Done => DoneDecision(row, pairDef),
            Outcome.Skipped or Outcome.Refused => new SyncDecision(row.Path, SyncDecisionKind.Skipped, row.Reason ?? string.Empty),
            Outcome.Failed => new SyncDecision(row.Path, SyncDecisionKind.Blocked, row.Reason ?? string.Empty),
            Outcome.Uncertain => new SyncDecision(row.Path, SyncDecisionKind.ResponseUnknown,
                row.Reason ?? string.Empty),
            _ => null
        };

    private static SyncDecision? DoneDecision(ObservedExecutor.Observation action, PairDef pairDef) => action.Kind switch
    {
        ActionKind.CopyNew or ActionKind.Update or ActionKind.Move or ActionKind.KeepBoth => new SyncDecision(
            action.Path, IsLocalTarget(action.Target, pairDef) ? SyncDecisionKind.Downloaded : SyncDecisionKind.Uploaded,
            action.Kind.ToString()),
        ActionKind.Delete => new SyncDecision(action.Path, SyncDecisionKind.Archived, action.Kind.ToString()),
        _ => null
    };

    private static bool IsLocalTarget(Side target, PairDef pairDef) =>
        string.Equals((target == Side.A ? pairDef.A : pairDef.B).Provider, SyncProviders.Local,
            StringComparison.OrdinalIgnoreCase);

    private static string FailureReasons(RunReport report) =>
        string.Join("; ", report.Failures.Where(result => result.Outcome == Outcome.Failed && result.Reason is not null)
            .Select(result => result.Reason!).Distinct(StringComparer.Ordinal));
}
