namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private void InvalidateLegacyGapItems(PairDef pair, RootDef root, Side side, IReadOnlyList<ItemState> page,
        ItemEligibility eligibility, SqliteStateStore sql)
    {
        var obligations = ((ILocalGapStore)sql).GapObligations(root.RootId, page.Select(item => item.ObjectId).ToArray());
        var files = page.Where(item => obligations.Contains(item.ObjectId) && item.Kind == ItemKind.File &&
                eligibility.EvaluateObservation(pair, side, item, !_settings.ReplaceOnlineOnlyStubs).Eligible)
            .Select(item => _store.ByObject(root.RootId, item.ObjectId))
            .OfType<ItemState>().Where(item => !item.Flags.HasFlag(ItemFlags.NeedsRehash));
        _store.RequireRehashBatch(root.RootId, files, MessageCatalog.Default.Template("reconcile-journal-proof"));
    }

    private async Task InvalidateReconciliationAsync(PairDef pair, RootDef root, Side side,
        LocalGapEpoch epoch, CancellationToken ct)
    {
        if (epoch.TrustFileUsn || root.Provider != SyncProviders.Local) return;
        var sql = (SqliteStateStore)_store;
        var eligibility = new ItemEligibility(_store.Pairs(), _productOwned(pair));
        bool eligible(ItemState item) => eligibility.EvaluateObservation(pair, side, item,
            !_settings.ReplaceOnlineOnlyStubs).Eligible;
        var total = await _admission.WithAsync(pair.PairId,
            () => Task.FromResult(sql.ReconciliationRetainedFileCount(root.RootId)), ct);
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
        } while (more);
        // NeedsRehash now marks unproven content. Live edits after proof are re-flagged by
        // LocalInventory.JournalCatchUp's RequireRehashBatch; never restart blanket invalidation here.
    }
}
