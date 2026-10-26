namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task PrepareDeltaProofAsync(PairDef pair, BothScan scan, IReadOnlySet<string> reviewScopes, CancellationToken ct)
    {
        RetainReviewedRemovalObservations(pair, scan, reviewScopes, ct);
        await ProveDeltaChangesAsync(pair, scan, ct);
    }

    private async Task ProveDeltaChangesAsync(PairDef pair, BothScan scan, CancellationToken ct)
    {
        long bytes = default;
        var proven = default(int);
        foreach (var (root, side) in new[] { (pair.A, scan.A), (pair.B, scan.B) })
        {
            if (root.Provider != SyncProviders.Local || side.WalkObserved || side.InventoryFailure is not null) continue;
            var changes = side.UserChanges ?? [];
            var rows = _store.Live(root.RootId, changes.Select(change => change.Path))
                .Concat(changes.Select(change => _store.ByObject(root.RootId, change.ObjectId)).OfType<ItemState>())
                .DistinctBy(item => item.ObjectId);
            foreach (var item in rows)
            {
                if (item.Kind != ItemKind.File || !item.Flags.HasFlag(ItemFlags.NeedsRehash) ||
                    !PairPath.Observed(item.Path, side.IncompleteSubtrees)) continue;
                var size = Math.Max(default(long), item.Size);
                if (size > _settings.ReconciliationBatch.MaxBytes - bytes) continue;
                bytes += size;
                var hashes = await _endpoints.Inventory(root).HashAsync([item.Path], ct);
                if (hashes.ContainsKey(item.Path) && _store.ByObject(root.RootId, item.ObjectId) is { } current &&
                    !current.Flags.HasFlag(ItemFlags.NeedsRehash)) proven++;
            }
        }
        RunTrace.Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("autosync-delta-proof"), proven, bytes));
    }
}
