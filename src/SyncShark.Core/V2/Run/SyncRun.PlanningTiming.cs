using System.Diagnostics;
using System.Globalization;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A planning sub-stage used to run silently between the comparison trace and the phase boundary line,
    // so a slow stage was invisible in the run log. Each stage now names itself, its elapsed time and
    // the counts it produced, through the same RunLog trace the run already writes.
    private static long PlanningStepStarted() => Stopwatch.GetTimestamp();

    // A stage names itself before it starts, so a stall points at the stage in progress rather than
    // at the previous stage's completion line. Its input counts stay on the same trace line.
    private static long PlanningStepStarted(string step, int actions)
    {
        RunLog.Trace(string.Create(CultureInfo.InvariantCulture, $"planning {step}: start; actions={actions}"));
        return Stopwatch.GetTimestamp();
    }

    private static void RunTracePlanningStep(string step, long started, string counts) =>
        RunLog.Trace(string.Create(CultureInfo.InvariantCulture, $"planning {step}: elapsed-ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}; {counts}"));

    // One safety-filter pass with the action counts it kept and dropped; ApplySafetyFilter's other
    // callers (apply, deferred evidence) keep their untraced shape.
    private Plan FilteredPlan(PairDef pair, string step, ActionGuard? guard, Plan plan)
    {
        var started = PlanningStepStarted("filter " + step, plan.Actions.Count);
        var filtered = ApplySafetyFilter(pair, guard, plan);
        RunTracePlanningStep("filter " + step, started, $"actions={plan.Actions.Count}->{filtered.Actions.Count}; blocked={filtered.SkippedActions.Count - plan.SkippedActions.Count}");
        return filtered;
    }

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
        {
            var retired = 0;
            store.AcknowledgeDeferredPlanning(pair.PairId, excluded, notSynced, acknowledged: _ => retired++);
            RunLog.Trace($"Planning intake retired {retired} non-work entities from {excluded.Length} candidates.");
        }
        return page.Except(excluded).ToArray();
    }
}
