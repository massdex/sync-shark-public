namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<BothScan> ScanBatchAsync(PairDef pair, ReconcileBatch batch,
        ProgressThrottle throttle, CancellationToken ct)
    {
        var a = await ReadSide(pair.A, Side.A);
        var b = await ReadSide(pair.B, Side.B);
        return new(a, b) { Unknown = batch.Remaining };

        async Task<SideScan> ReadSide(RootDef root, Side side)
        {
            var cursor = _store.Cursor(root.RootId) ?? string.Empty;
            var gen = _store.BeginGeneration(root.RootId, "delta");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var inventory = _endpoints.Inventory(root);
            var predicates = Predicates(pair, LinkExclusions.None);
            var include = side == Side.A ? predicates.A : predicates.B;
            var read = await ReadScopesAsync(pair, root, side, inventory, batch.Paths, cursor, gen,
                include, id => ids.Add(id), throttle, ct);
            return new(gen, cursor, !read.Incomplete.Contains(string.Empty), false,
                read.Touched, ids.ToArray(), NoDeletions, [])
            {
                IncompleteSubtrees = read.Incomplete, IncompleteReasons = read.Reasons,
                ExactCreationObserved = read.Observed.ToHashSet(StringComparer.OrdinalIgnoreCase),
                RehashThrough = _store.RehashWatermark(root.RootId)
            };
        }
    }
}
