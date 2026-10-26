using System.Diagnostics;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A settle chunk line is time-throttled like the protection-scope progress lines: the first and
    // last chunk always report, and a long lane reports at most about one chunk line this often.
    private const double SettleChunkTraceSeconds = 3;

    // The same scope/join/decision owners as ordinary intake, but never hash or execute an endpoint
    // effect for this lane. A rejected chunk remains queued for ordinary intake, without rotation.
    private (IReadOnlyList<SqliteStateStore.DeferredPlanning> Rows, IReadOnlyList<SyncAction> Adopts, int Candidates)
        PlanSettlePage(PairDef pair, BothScan scan, LinkExclusions links,
            IReadOnlyList<SqliteStateStore.DeferredPlanning> ordinary, IReadOnlyList<string> touched,
            Func<string, bool> includeA, Func<string, bool> includeB, CancellationToken ct)
    {
        if (_store is not SqliteStateStore store || scan.AnyFull || !scan.A.Complete || !scan.B.Complete)
        {
            RunLog.Trace("planning settle: skipped; " + (_store is not SqliteStateStore
                ? "store-not-settle-capable" : scan.AnyFull ? "full-scan" : "incomplete-scan"));
            return ([], [], 0);
        }
        var started = PlanningStepStarted();
        RunLog.Trace($"planning settle: start; ordinary={ordinary.Count}; capacity={SqliteStateStore.SettlePathCapacity(ordinary)}");
        var candidates = store.SettlePlanningCandidates(pair.PairId, _time.GetUtcNow().UtcTicks,
            SqliteStateStore.SettlePathCapacity(ordinary), ordinary, advance: _endpoints is not PlanOnlyEndpoints);
        var accepted = new List<SqliteStateStore.DeferredPlanning>();
        var adopts = new List<SyncAction>();
        // No active-retry or user-decision exemption: settle must leave ALL such work to ordinary intake.
        var protection = links.With(Protection(pair, scan, activeRetries: new HashSet<string>()));
        // Inventory predicates cover boundaries; the existing scan policy also covers ignores.
        // Reject the entire entity if any alias is outside sync authority, without acknowledging it.
        var notSynced = ScanPathsNotSynced(pair);
        var chunks = candidates.Where(row => row.Paths.All(path =>
            includeA(path) && includeB(path) && !notSynced(path))).Chunk(128).ToArray();
        EvaluateSettleChunks(chunks, pair, scan, protection, touched, accepted, adopts, ct);
        var settled = adopts.Distinct().ToArray();
        RunTracePlanningStep("settle done", started, $"candidates={candidates.Count}; chunks={chunks.Length}; accepted={accepted.Count}; rejected={candidates.Count - accepted.Count}; adopts={settled.Length}");
        return (accepted, settled, candidates.Count);
    }

    // Chunk progress, in the protection-scope shape: the first and last chunk always report, and a
    // long lane reports at most about one line every few seconds.
    private void EvaluateSettleChunks(SqliteStateStore.DeferredPlanning[][] chunks, PairDef pair, BothScan scan,
        LinkExclusions protection, IReadOnlyList<string> touched, List<SqliteStateStore.DeferredPlanning> accepted,
        List<SyncAction> adopts, CancellationToken ct)
    {
        var lastTrace = Stopwatch.GetTimestamp();
        for (var index = 0; index < chunks.Length; index++)
        {
            var chunk = chunks[index];
            var acceptedBefore = accepted.Count;
            EvaluateSettleChunk(pair, scan, protection, touched, accepted, adopts, chunk, ct);
            var chunkAccepted = accepted.Count - acceptedBefore;
            if (index != 0 && index != chunks.Length - 1 &&
                Stopwatch.GetElapsedTime(lastTrace).TotalSeconds < SettleChunkTraceSeconds) continue;
            lastTrace = Stopwatch.GetTimestamp();
            RunLog.Trace($"planning settle chunk {index + 1}/{chunks.Length}: candidates={chunk.Length}; accepted={chunkAccepted}; rejected={chunk.Length - chunkAccepted}; running-accepted={accepted.Count}");
        }
    }

    private void EvaluateSettleChunk(PairDef pair, BothScan scan, LinkExclusions protection,
        IReadOnlyList<string> touched, List<SqliteStateStore.DeferredPlanning> accepted, List<SyncAction> adopts,
        SqliteStateStore.DeferredPlanning[] initial, CancellationToken ct)
    {
        void Evaluate(SqliteStateStore.DeferredPlanning[] chunk)
        {
            ct.ThrowIfCancellationRequested();
            var paths = chunk.SelectMany(row => row.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Split oversized alias chunks too; one entity must not block its neighbours.
            if (paths.Length > 1024) { Split(chunk); return; }
            var guarded = protection.ForPaths(paths);
            var page = FilterSettleProtection(pair, chunk, guarded, touched);
            if (page.Length == 0) return;
            paths = page.SelectMany(row => row.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var scope = LoadSettleScope(pair, guarded, page, paths, ct);
            var rows = JoinRows(scope, scan, false);
            var plan = Decide(pair, scan, rows, null, scope, guarded);
            var evaluated = EvaluatedPaths(rows);
            // A partially hidden entity must not adopt a subset of its aliases. Re-decide smaller
            // groups, never partition the rejected plan's actions or bypass its safety gate.
            if (!Planner.IsEffectFreeSettlement(plan) || page.Any(row => !row.Paths.All(evaluated)))
            { Split(page); return; }
            accepted.AddRange(page);
            adopts.AddRange(plan.Actions);
        }

        void Split(SqliteStateStore.DeferredPlanning[] page)
        {
            if (page.Length <= 1) return;
            var width = page.Length > 16 ? 16 : 1;
            foreach (var part in page.Chunk(width)) Evaluate(part);
        }
        Evaluate(initial);
    }

    private SqliteStateStore.DeferredPlanning[] FilterSettleProtection(PairDef pair,
        SqliteStateStore.DeferredPlanning[] chunk, LinkExclusions guarded, IReadOnlyList<string> touched)
    {
        var notSynced = ScanPathsNotSynced(pair, ItemEligibility.Purpose.Settle,
            (_, _, item, _) => guarded.Covers(item.Path) || guarded.Contains(item.Path) ? "held:protection" : null);
        return chunk.Where(row => row.Paths.All(path => !notSynced(path) &&
            !touched.Any(other => PairPath.IsSelfOrUnder(path, other) || PairPath.IsSelfOrUnder(other, path)))).ToArray();
    }

    private RunScope.Scope LoadSettleScope(PairDef pair, LinkExclusions guarded,
        SqliteStateStore.DeferredPlanning[] page, string[] paths, CancellationToken ct)
    {
        var predicates = Predicates(pair, guarded);
        var scanner = new RunScope(_store, predicates.A, predicates.B, guarded,
            rules: PairInclude.IgnoreRules(pair), restoreProtection: RecoveryProtection(pair), cancellationToken: ct);
        return scanner.LoadTouched(pair, paths,
            page.Where(row => row.RootId == pair.A.RootId).Select(row => row.ObjectId).ToArray(),
            page.Where(row => row.RootId == pair.B.RootId).Select(row => row.ObjectId).ToArray());
    }

    // One summary line per run; the return value is the remaining queue count for RunReport. The
    // re-run decision belongs to the loop (PairLoop.AfterReport), never to an admission count here.
    private int ReportSettledPlanning(string pairId, int settled, int candidates, IProgress<RunProgress> progress,
        int rejected = 0)
    {
        var remaining = _store is SqliteStateStore store ? store.CountDeferredPlanning(pairId) : 0;
        var message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Message("planning-settled"), settled, candidates, remaining, rejected);
        RunTrace.Emit(message);
        progress.Report(new RunProgress(RunPhases.Finishing, null, 0, 0) { Message = message });
        return remaining;
    }
}
