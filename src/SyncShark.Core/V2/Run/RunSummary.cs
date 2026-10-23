namespace Massdex.SyncShark.V2;

/// <summary>
/// One retained run outcome, bounded to its identity, times and counts. Per-file results are not part of this
/// summary: decision 28's separate per-file history remains follow-up work.
/// </summary>
public sealed record RunSummary(
    long Seq,
    string RunId,
    string PairId,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string Outcome,
    int Done,
    int Uploaded,
    int Downloaded,
    int Deleted,
    int Moved,
    int KeptBoth,
    int Held,
    int Failed,
    int Uncertain,
    int Skipped,
    int Deferred)
{
    public int Pending => RunOutcomes.Pending(Failed, Uncertain, Held, Skipped);

    /// <summary>The bounded summary of one finished report, before Storage assigns its sequence.</summary>
    internal static RunSummary Of(string runId, RunReport report, DateTimeOffset startedUtc,
        DateTimeOffset completedUtc) => new(0, runId, report.PairId, startedUtc, completedUtc, report.Outcome,
        report.Done, report.Uploaded, report.Downloaded, report.Deleted, report.Moved, report.KeptBoth,
        report.Held, report.Failed, report.Uncertain, report.Skipped, report.Deferred);
}
