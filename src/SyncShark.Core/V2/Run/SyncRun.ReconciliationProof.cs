namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task ProveReconciliationBatchAsync(RootDef root, IInventorySource inventory,
        ReconcileBatch batch, ScopeRead read, CancellationToken ct)
    {
        if (root.Provider != SyncProviders.Local) return;
        var observed = read.Observed.Where(path => PairPath.Observed(path, read.Incomplete)).ToArray();
        foreach (var item in _store.Live(root.RootId, observed))
        {
            if (item.Kind != ItemKind.File || !item.Flags.HasFlag(ItemFlags.NeedsRehash)) continue;
            var bytes = Math.Max(default(long), item.Size);
            if (bytes > _settings.ReconciliationBatch.MaxBytes - batch.ProofBytes)
            {
                RunTrace.Emit($"reconciliation proof deferred: path={item.Path} bytes={bytes} remaining={_settings.ReconciliationBatch.MaxBytes - batch.ProofBytes}");
                continue;
            }
            // Charge attempted reads too: failed proof must not replenish the shared two-root budget.
            batch.ProofBytes += bytes;
            var hashes = await inventory.HashAsync([item.Path], ct);
            if (hashes.ContainsKey(item.Path)) batch.ProofFiles++;
        }
    }
}
