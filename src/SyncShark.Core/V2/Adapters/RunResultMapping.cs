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
        var uploaded = new List<string>();
        var downloaded = new List<string>();
        foreach (var item in observed)
        {
            var decision = item.Outcome is null
                ? new SyncDecision(item.Path, SyncDecisionKind.Conflict, PlanNotices.NeedDecision.Detail)
                : Decision(item, pairDef);
            if (decision is null) continue;
            decisions.Add(decision);
            if (decision.Kind == SyncDecisionKind.Uploaded) uploaded.Add(decision.RelativePath);
            else if (decision.Kind == SyncDecisionKind.Downloaded) downloaded.Add(decision.RelativePath);
        }
        var result = new SyncRunResult(pair.Id, decisions)
        {
            TransferTotals = new SyncRunTransferTotals(uploaded, downloaded)
        };
        var failed = string.Equals(report.Outcome, RunOutcomes.Failed, StringComparison.Ordinal);
        return new SyncAuthorityBatchItemResult(pair, result, failed ? "failed" : null,
            failed ? FailureReasons(report) : null);
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
