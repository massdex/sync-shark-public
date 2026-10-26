namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task InvalidateReconciliationAsync(PairDef pair, RootDef root, Side side,
        LocalGapEpoch epoch, ProgressThrottle throttle, CancellationToken ct)
    {
        if (epoch.TrustFileUsn || root.Provider != SyncProviders.Local) return;
        var sql = (SqliteStateStore)_store;
        var eligibility = new ItemEligibility(_store.Pairs(), _productOwned(pair));
        bool eligible(ItemState item) => eligibility.EvaluateObservation(pair, side, item,
            !_settings.ReplaceOnlineOnlyStubs).Eligible;
        var total = await _admission.WithAsync(pair.PairId,
            () => Task.FromResult(sql.ReconciliationRetainedFileCount(root.RootId)), ct);
        TraceReconciliationPhase(RunPhases.Invalidating);
        throttle.Report(RunPhases.Invalidating, null, 0, total);
        var page = default(int);
        var examined = default(long);
        var invalidated = default(long);
        bool more;
        do
        {
            ct.ThrowIfCancellationRequested();
            var pageInvalidated = default(int);
            more = await _admission.WithAsync(pair.PairId,
                () => Task.FromResult(sql.InvalidateReconciliationPage(root.RootId, epoch.Epoch, eligible,
                    (read, changed) => { examined += read; invalidated += changed; pageInvalidated = changed; })), ct);
            RunTrace.Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                MessageCatalog.Default.Template("untrusted-journal-invalidation"), ++page,
                examined, total, pageInvalidated, invalidated));
            throttle.Report(RunPhases.Invalidating, null, examined, total);
        } while (more);
        throttle.Flush();
        // NeedsRehash now marks unproven content. Live edits after proof are re-flagged by
        // LocalInventory.JournalCatchUp's RequireRehashBatch; never restart blanket invalidation here.
    }
}
