namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<DeferredExecution> ExecuteMeasuredPlanAsync(Plan plan, PairDef pair,
        ProgressThrottle throttle, IReadOnlySet<string> reviewedDeletes, CancellationToken ct)
    {
        using var timing = new RunBoundaryTiming("apply", _settings.EnableRunInstrumentation);
        return await ExecuteWithEvidenceAsync(plan, pair, throttle, reviewedDeletes, ct, allowUnreviewedEvidence: true);
    }

    // One oldest-first intake page. Apply rotates it; previews never mutate the queue.
    internal IReadOnlyList<SqliteStateStore.DeferredPlanning> AdmitPlanningPage(string pairId)
    {
        using var timing = new RunBoundaryTiming("intake", _settings.EnableRunInstrumentation);
        IReadOnlyList<SqliteStateStore.DeferredPlanning> page = _store is SqliteStateStore store
            ? store.AdmitDeferredPlanning(pairId, _time.GetUtcNow().UtcTicks,
                rotate: _endpoints is not PlanOnlyEndpoints) : [];
        timing.Entities = page.Count;
        timing.Aliases = page.Sum(row => (long)row.Paths.Count);
        return page;
    }
}
