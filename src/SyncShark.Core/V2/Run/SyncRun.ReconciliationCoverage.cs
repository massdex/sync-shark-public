namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static RunScope.Scope LoadReconciliationCoverage(RunScope.Scope scope, IReadOnlyList<ReconciliationCoverage> coverages)
    {
        foreach (var coverage in coverages) coverage.Load(scope);
        return scope;
    }

    private IReadOnlyList<ReconciliationCoverage> ActiveCoverages(PairDef pair, ref BothScan scan, ReconcileBatch? batch = null)
    {
        if (_store is not SqliteStateStore sql) return [];
        if (batch is not null)
        {
            var coverage = new ReconciliationCoverage(StateMonitor.PlanningBacklog(), false, batch.Session, sql,
                new HashSet<Side> { Side.A, Side.B }, scan, batch.UnobservedAlias, batch.ObservedMovingPath);
            scan = scan with { Unknown = (side, path) => batch.UnobservedAlias(path) ||
                !batch.ObservedAliases.Contains(path) && batch.Remaining(side, path) || coverage.Unknown(side, path) };
            return [coverage];
        }
        var observed = scan;
        var result = new List<ReconciliationCoverage>();
        var covered = new HashSet<Side>();
        lock (_stopGate)
        {
            if (_reconciling.TryGetValue(pair.PairId, out var active))
                Add(active.Decision, active.Selecting, active.Session);
            foreach (var reset in _pendingResets.Where(entry => entry.Key.PairId == pair.PairId))
                Add(reset.Value, true, string.Empty);
        }
        // A failed run or a second root's queued signal is still unknown, even without an active worker.
        foreach (var root in new[] { pair.A, pair.B })
            if (root.Provider == SyncProviders.Local && !covered.Contains(root == pair.A ? Side.A : Side.B) &&
                sql.PendingGapInventory(root.RootId) && ((ILocalGapStore)sql).GapEpoch(root.RootId) is { } gap)
                Add(StateMonitor.FromGap(gap) with { RootId = root.RootId }, true, string.Empty);
        if (result.Count != 0) scan = scan with { Unknown = (side, path) => result.Any(item => item.Unknown(side, path)) };
        return result;

        void Add(StateMonitorDecision decision, bool selecting, string session)
        {
            var sides = ReconciliationSides(pair, decision.Trigger)
                .Where(side => !covered.Contains(side) && (decision.RootId is null ||
                    (side == Side.A ? pair.A : pair.B).RootId == decision.RootId)).ToHashSet();
            if (sides.Count == 0) return;
            covered.UnionWith(sides);
            result.Add(new(decision, selecting, session, sql, sides, observed));
        }
    }
}
