namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private IReadOnlyList<ReconciliationCoverage> ActiveCoverages(PairDef pair, BothScan scan)
    {
        if (_store is not SqliteStateStore sql) return [];
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
        return result;

        void Add(StateMonitorDecision decision, bool selecting, string session)
        {
            var sides = ReconciliationSides(pair, decision.Trigger)
                .Where(side => !covered.Contains(side) && (decision.RootId is null ||
                    (side == Side.A ? pair.A : pair.B).RootId == decision.RootId)).ToHashSet();
            if (sides.Count == 0) return;
            covered.UnionWith(sides);
            result.Add(new(decision, selecting, session, sql, sides, scan));
        }
    }
}
