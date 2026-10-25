namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<DeferredExecution> ExecuteMeasuredPlanAsync(Plan plan, PairDef pair,
        ProgressThrottle throttle, IReadOnlySet<string> reviewedDeletes, CancellationToken ct)
    {
        using var timing = new RunBoundaryTiming("apply", _settings.EnableRunInstrumentation);
        return await ExecuteWithEvidenceAsync(plan, pair, throttle, reviewedDeletes, ct, allowUnreviewedEvidence: true);
    }

    private Func<Func<string, bool, bool>> RunRetryProtectionFactory(string pairId,
        IReadOnlySet<string> activeRetries, IProgress<RunProgress> progress)
    {
        var fallbackReported = false;
        void ReportRetryFallback()
        {
            if (fallbackReported) return;
            fallbackReported = true;
            try { progress.Report(new RunProgress(RunPhases.Comparing, null, 0, 0)
                { Message = "Retry protection snapshot limit reached; using indexed lookups with a bounded folder cache." }); }
            catch { /* Display failures never change protection. */ }
        }
        return () => RetryProtection.Create(_store, pairId, activeRetries, ReportRetryFallback);
    }

    // One oldest-first intake page. Apply rotates it; previews never mutate the queue.
    internal IReadOnlyList<SqliteStateStore.DeferredPlanning> AdmitPlanningPage(string pairId, PairDef? observedPair = null)
    {
        using var timing = new RunBoundaryTiming("intake", _settings.EnableRunInstrumentation);
        IReadOnlyList<SqliteStateStore.DeferredPlanning> page = _store is SqliteStateStore store
            ? store.AdmitDeferredPlanning(pairId, _time.GetUtcNow().UtcTicks,
                rotate: _endpoints is not PlanOnlyEndpoints) : [];
        if (observedPair is not null && _store is SqliteStateStore retirement)
            page = RetireNonWorkPlanning(retirement, observedPair, page);
        timing.Entities = page.Count;
        timing.Aliases = page.Sum(row => (long)row.Paths.Count);
        return page;
    }

    private IReadOnlyList<SqliteStateStore.DeferredPlanning> RetireNonWorkPlanning(SqliteStateStore store,
        PairDef pair, IReadOnlyList<SqliteStateStore.DeferredPlanning> page)
    {
        var notSynced = ScanPathsNotSynced(pair);
        var excluded = page.Where(row => row.Paths.Count > 0 && row.Paths.All(notSynced)).ToArray();
        // This retires scheduling, never content proof, baselines or independent reviewed authority.
        // The queue owner still checks versions and requires a complete successful alias cycle.
        if (_endpoints is not PlanOnlyEndpoints && excluded.Length > 0)
            store.AcknowledgeDeferredPlanning(pair.PairId, excluded, notSynced);
        return page.Except(excluded).ToArray();
    }
}
