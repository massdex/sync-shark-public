namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A bounded exact-path observation is creation evidence only. It never changes a cursor,
    // whole-side completeness, baseline deletion evidence, or authority to Forget an entity.
    private async Task<bool> ObserveCreationDestinationsAsync(PairDef pair, BothScan scan,
        IReadOnlyList<PlanRow> rows, CancellationToken ct)
    {
        var candidates = rows.Where(Planner.NeedsCreationObservation)
            .Take(V2Settings.ScanRetryScopeLimit).ToArray();
        if (candidates.Length == 0) return false;
        var paths = candidates.Select(row => DropboxNameMapping.MatchPath(row.A ?? row.B!)).ToArray();
        var protection = Protection(pair, scan, additional: ScanFailures(scan)).ForPaths(paths);
        var eligibility = new ItemEligibility(_store.Pairs(), _productOwned(pair), (_, _, item, _) =>
            protection.Covers(item.Path) || protection.Contains(item.Path) ? "held:protection" : null);
        var changed = false;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var side = row.A is null ? Side.A : Side.B;
            var target = side == Side.A ? scan.A : scan.B;
            var root = side == Side.A ? pair.A : pair.B;
            var source = row.A ?? row.B!;
            var path = DropboxNameMapping.MatchPath(source);
            if (target.ExactCreationObserved.Contains(path) || !PairPath.Observed(path, target.IncompleteSubtrees) ||
                !eligibility.Evaluate(pair, side, source with { Path = path }, ItemEligibility.Purpose.Plan).Eligible) continue;
            try
            {
                // The endpoint contract returns null ONLY for proven absence under a verified root
                // and guarded ancestors. Access failure, unsafe paths and root failure throw instead.
                if (_endpoints.Writer(root) is not ISourceAbsenceProbe observer) continue;
                var observed = await observer.ObserveSourceMetadataAsync(path, ct).ConfigureAwait(false);
                if (observed is not null) continue; // Presence needs normal inventory/join, never a create assumption.
                target.ExactCreationObserved.Add(path);
                changed = true;
            }
            catch (Exception error) when (ScopeFailure(error, ct))
            {
                RunLog.Trace("Destination observation remains unknown: " + path + ": " + error.Message);
            }
        }
        return changed;
    }
}
