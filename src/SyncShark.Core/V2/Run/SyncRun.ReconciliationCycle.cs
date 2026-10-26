namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<bool> SelectStableReconciliationAsync(PairDef pair, ActiveReconciliation active,
        ProgressThrottle throttle, CancellationToken ct)
    {
        await SelectReconciliationAsync(pair, active, throttle, ct);
        if (!ReconciliationEpochReplaced(active)) return true;
        RestartReconciliationSelection(active);
        await SelectReconciliationAsync(pair, active, throttle, ct);
        if (!ReconciliationEpochReplaced(active)) return true;
        RestartReconciliationSelection(active);
        return false;
    }

    private bool ReconciliationEpochReplaced(ActiveReconciliation active) =>
        active.GapEpochs.Any(entry => ((ILocalGapStore)_store).GapEpoch(entry.Key)?.Epoch != entry.Value);

    private void RestartReconciliationSelection(ActiveReconciliation active)
    {
        RunTrace.Emit(MessageCatalog.Default.Template("reconciliation-epoch-replaced"));
        var sql = (SqliteStateStore)_store;
        sql.ClearRemovalObservation(active.Session + ":work");
        sql.ClearRemovalObservation(active.Session + ":deferred");
        sql.ClearRemovalObservation(active.Session + ":seen");
        active.GapEpochs.Clear();
        active.ListingBases.Clear();
        active.CompletedSelectionRoots.Clear();
    }
}
