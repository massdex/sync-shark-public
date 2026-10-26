using System.Text.Json;

namespace Massdex.SyncShark.V2;

// The window's Runs surface reads core v2's retained run summaries . The translation keeps the
// window's existing retained-run record: no provider, core or CLI behaviour changes here.
public sealed partial class V2SyncHost
{
    /// <inheritdoc />
    public Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadRunHistoryAsync(int limit = 50, int offset = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = _store.ReadRunHistoryPage(null, limit, offset);
        return Task.FromResult<IReadOnlyList<SyncAuthorityRunHistory>>(rows.Select(History).ToArray());
    }

    /// <inheritdoc />
    public Task<SyncAuthorityRunHistory?> LoadRunByIdAsync(string runId, CancellationToken cancellationToken = default,
        Action<string>? onReadProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProgressGuard.Report(onReadProgress, "Reading the retained run…", cancellationToken);
        return Task.FromResult(_store.RunSummaryById(runId) is { } run ? History(run) : null);
    }

    // The window's recorded status vocabulary is owned by RunSummary.Status: the retained result's counts decide
    // the presented outcome either way (SyncRunOutcomeProjection), so this word never claims more than the run recorded.
    private static SyncAuthorityRunHistory History(RunSummary run) => new(
        run.RunId, "run_pair", run.StartedUtc, run.CompletedUtc, run.Status,
        JsonSerializer.Serialize(new { pairIds = new[] { run.PairId } }), Result(run));

    // The retained-result shape the window already reads: per-pair counts, with first-sync deferred work kept as
    // not-attempted so the shared classifier can never present it as a success. Unchanged is not recorded by v2
    // (the Planner plans effects, not identical rows) and is deliberately absent rather than written as zero.
    private static string Result(RunSummary run)
    {
        var pair = new Dictionary<string, object?>
        {
            ["pairId"] = run.PairId,
            ["uploaded"] = run.Uploaded,
            ["downloaded"] = run.Downloaded,
            ["deleted"] = run.Deleted,
            ["moved"] = run.Moved,
            ["keptBoth"] = run.KeptBoth,
            ["done"] = run.Done,
            ["blocked"] = run.Failed,
            ["failed"] = run.Failed,
            ["conflicts"] = run.Held,
            ["needsVerification"] = run.Uncertain,
            ["notAttempted"] = run.Deferred,
            ["skipped"] = run.Skipped,
            ["stopped"] = string.Equals(run.Outcome, RunOutcomes.Stopped, StringComparison.Ordinal),
            ["outcome"] = run.Outcome
        };
        return JsonSerializer.Serialize(new
        {
            outcome = run.Outcome,
            pending = run.Pending,
            deferred = run.Deferred,
            pairId = run.PairId,
            pairs = new[] { pair },
            stopped = string.Equals(run.Outcome, RunOutcomes.Stopped, StringComparison.Ordinal),
            failedPairs = string.Equals(run.Outcome, RunOutcomes.Failed, StringComparison.Ordinal) ? 1 : 0
        });
    }
}
