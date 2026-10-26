namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<bool> SelectStableReconciliationAsync(PairDef pair, ActiveReconciliation active,
        ProgressThrottle throttle, CancellationToken ct)
    {
        await SelectReconciliationAsync(pair, active, throttle, ct);
        if (!ReconciliationEpochReplaced(active)) return true;
        RestartReconciliationSelection(active, throttle);
        await SelectReconciliationAsync(pair, active, throttle, ct);
        if (!ReconciliationEpochReplaced(active)) return true;
        RestartReconciliationSelection(active, throttle, exhausted: true);
        return false;
    }

    private bool ReconciliationEpochReplaced(ActiveReconciliation active) =>
        active.GapEpochs.Any(entry => ((ILocalGapStore)_store).GapEpoch(entry.Key)?.Epoch != entry.Value);

    private void RestartReconciliationSelection(ActiveReconciliation active, ProgressThrottle throttle, bool exhausted = false)
    {
        if (!exhausted)
        {
            TraceReconciliationPhase(RunPhases.RestartingSelection);
            throttle.Report(RunPhases.RestartingSelection, null, 0, 0);
            throttle.Flush();
        }
        RunTrace.Emit(MessageCatalog.Default.Template(exhausted ? "reconciliation-epoch-exhausted" : "reconciliation-epoch-replaced"));
        var sql = (SqliteStateStore)_store;
        sql.ClearRemovalObservation(active.Session + ":work");
        sql.ClearRemovalObservation(active.Session + ":deferred");
        sql.ClearRemovalObservation(active.Session + ":seen");
        active.GapEpochs.Clear();
        active.InitialGapItems.Clear();
        active.ListingBases.Clear();
        active.CompletedSelectionRoots.Clear();
    }
}
