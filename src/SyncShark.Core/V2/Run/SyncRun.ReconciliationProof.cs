namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task ProveReconciliationBatchAsync(RootDef root, IInventorySource inventory,
        ReconcileBatch batch, ScopeRead read, CancellationToken ct)
    {
        // A folder move needs identity/metadata, not proactive child-content proof.
        // Any actual child edit is hashed on demand by the planner through the same budget.
        if (root.Provider != SyncProviders.Local || batch.MovingUnit) return;
        var observed = read.Observed.Where(path => PairPath.Observed(path, read.Incomplete)).ToArray();
        foreach (var item in _store.Live(root.RootId, observed))
        {
            if (item.Kind != ItemKind.File || !item.Flags.HasFlag(ItemFlags.NeedsRehash)) continue;
            var before = batch.DecisionBytes;
            var hashes = await HashDecisionAsync(root, inventory, [item.Path], ct);
            batch.ProofBytes += batch.DecisionBytes - before;
            if (hashes.ContainsKey(item.Path)) batch.ProofFiles++;
        }
    }

    // Shared by proof, both concurrent planner sides, folder victims and isolated retries.
    // Reservations are never refunded: an unsuccessful provider call may already have read bytes.
    private Task<IReadOnlyDictionary<string, string>> HashDecisionAsync(RootDef root,
        IInventorySource inventory, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (ReconcileBatch.Current.Value is not { } batch || root.Provider != SyncProviders.Local)
            return inventory.HashAsync(paths, ct);
        var admitted = new List<string>();
        foreach (var item in _store.Live(root.RootId, paths))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind != ItemKind.File) continue;
            if (batch.AdmitDecisionHash(item)) admitted.Add(item.Path);
            else
            {
                var revision = _store.RehashRevision(root.RootId, item.ObjectId);
                if (_store.ByObject(root.RootId, item.ObjectId) is { Hash: null } current &&
                    !current.Flags.HasFlag(ItemFlags.NeedsRehash))
                    _store.RequireRehashAtRevision(root.RootId, current,
                        MessageCatalog.Default.Message("proof-selection-deferred"), revision);
            }
        }
        return admitted.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())
            : inventory.HashAsync(admitted, ct);
    }
}
