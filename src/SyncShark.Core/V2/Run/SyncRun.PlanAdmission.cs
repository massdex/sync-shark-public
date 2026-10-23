namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<string> ApplyPlanBoundNoticeChoiceAsync(Notice notice, string choice,
        IProgress<RunProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!NoticeChoices.Options(notice).Any(option => option.Choice == choice))
            throw new SyncRunRefusedException("That admission option is no longer available. Refresh notices.");
        if (choice == NoticeChoices.RepairHelp) return notice.Detail;
        if (choice == NoticeChoices.Ignore)
        {
            _store.CloseNotice(notice.NoticeId!, choice);
            return "Admission notice ignored; no work was admitted and no path was excluded.";
        }
        if (notice.PlanBound is not { RequestedBound: >= 0, PlannedCount: >= 0 } admission)
            throw new SyncRunRefusedException("This admission notice has no saved limit. Review preview/provenance and rerun with an explicit bound.");
        var bound = choice switch
        {
            NoticeChoices.RetryNow => admission.RequestedBound,
            NoticeChoices.ApprovePlanSize => admission.PlannedCount,
            _ => throw new SyncRunRefusedException("That admission option is no longer available. Refresh notices.")
        };
        // A one-shot option, not pair settings and not an unbounded scheduler wake. Planning is
        // repeated; growth above the displayed approved size raises the same notice again.
        var report = await IncrementalAsync(notice.PairId, progress ?? new NoAdmissionProgress(), ct,
            new RunOptions { MaxPlannedActions = bound });
        return $"Run with bound {bound}: {report.Outcome}; {report.Done} completed.";
    }

    private sealed class NoAdmissionProgress : IProgress<RunProgress>
    {
        public void Report(RunProgress value) { }
    }
}
