namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task SelectReconciliationAsync(PairDef pair, ActiveReconciliation active,
        ProgressThrottle throttle, CancellationToken ct)
    {
        foreach (var side in ReconciliationSides(pair, active.Decision.Trigger))
        {
            var root = side == Side.A ? pair.A : pair.B;
            if (active.Decision.RootId is { } rootId && root.RootId != rootId) continue;
            if ((active.Decision.Trigger is ReconciliationTrigger.GapSameJournal or
                ReconciliationTrigger.JournalReplaced or ReconciliationTrigger.JournalPositionInvalid) &&
                _store is ILocalGapStore gaps && gaps.GapEpoch(root.RootId) is { } epoch)
                active.GapEpochs[root.RootId] = epoch.Epoch;
            await SelectAsync(pair, side, active.Decision, active.Session, throttle, ct,
                basis => active.ListingBases[root.RootId] = basis);
        }
    }

    private Task<RunReport> CompleteReconciliationAsync(PairDef pair, ActiveReconciliation active,
        RunReport report, CancellationToken ct) => _admission.WithAsync(pair.PairId, () =>
    {
        ct.ThrowIfCancellationRequested();
        var sql = (SqliteStateStore)_store;
        var completion = new CompletionSteps(this, pair.PairId, ct);
        // A subtree inventory cannot discharge a root-wide restart obligation or replace its cursor.
        if ((active.Decision.UnknownScope ?? ReconciliationScope.Root).ReadScopes.Contains(string.Empty))
        {
            foreach (var (rootId, epoch) in active.GapEpochs)
                completion.Step(() => sql.CompleteGapInventory(rootId, epoch));
            if (active.Decision.Trigger == ReconciliationTrigger.DropboxReset)
                foreach (var (rootId, basis) in active.ListingBases)
                    completion.Step(() => _store.CompleteGeneration(
                        _store.BeginGeneration(rootId, "delta"), basis, true));
        }
        completion.Step(_store.Prune);
        completion.Step(() => sql.ClearRemovalObservation(active.Session + ":work"));
        return Task.FromResult(completion.Finish(report));
    }, ct);
}
