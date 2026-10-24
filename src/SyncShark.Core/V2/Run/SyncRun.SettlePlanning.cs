namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // The same scope/join/decision owners as ordinary intake, but never hash or execute an endpoint
    // effect for this lane. A rejected chunk remains queued for ordinary intake, without rotation.
    private (IReadOnlyList<SqliteStateStore.DeferredPlanning> Rows, IReadOnlyList<SyncAction> Adopts, int Candidates)
        PlanSettlePage(PairDef pair, BothScan scan, LinkExclusions links,
            IReadOnlyList<SqliteStateStore.DeferredPlanning> ordinary, IReadOnlyList<string> touched,
            Func<string, bool> includeA, Func<string, bool> includeB, CancellationToken ct)
    {
        if (_store is not SqliteStateStore store || scan.AnyFull || !scan.A.Complete || !scan.B.Complete)
            return ([], [], 0);
        var candidates = store.SettlePlanningCandidates(pair.PairId, _time.GetUtcNow().UtcTicks,
            SqliteStateStore.SettlePathCapacity(ordinary), ordinary, advance: _endpoints is not PlanOnlyEndpoints);
        var accepted = new List<SqliteStateStore.DeferredPlanning>();
        var adopts = new List<SyncAction>();
        // No active-retry or user-decision exemption: settle must leave ALL such work to ordinary intake.
        var protection = links.With(Protection(pair, scan, activeRetries: new HashSet<string>()));
        // Inventory predicates cover boundaries; the existing scan policy also covers ignores.
        // Reject the entire entity if any alias is outside sync authority, without acknowledging it.
        var notSynced = ScanPathsNotSynced(pair);
        foreach (var chunk in candidates.Where(row => row.Paths.All(path =>
                     includeA(path) && includeB(path) && !notSynced(path))).Chunk(128))
            EvaluateSettleChunk(pair, scan, protection, touched, accepted, adopts, chunk, ct);
        return (accepted, adopts.Distinct().ToArray(), candidates.Count);
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
            var page = FilterSettleProtection(chunk, guarded, touched);
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

    private static SqliteStateStore.DeferredPlanning[] FilterSettleProtection(
        SqliteStateStore.DeferredPlanning[] chunk, LinkExclusions guarded, IReadOnlyList<string> touched) =>
        chunk.Where(row => row.Paths.All(path => !guarded.Covers(path) && !guarded.Contains(path) &&
            !touched.Any(other => PairPath.IsSelfOrUnder(path, other) || PairPath.IsSelfOrUnder(other, path)))).ToArray();

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
