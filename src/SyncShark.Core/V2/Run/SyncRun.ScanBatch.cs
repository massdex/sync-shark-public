namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private IReadOnlyList<string> ReconciliationAliasPaths(PairDef pair, ReconcileBatch batch)
    {
        var paths = batch.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var side in new[] { Side.A, Side.B })
        {
            var root = side == Side.A ? pair.A : pair.B;
            var ids = (side == Side.A ? batch.ObjectIdsA : batch.ObjectIdsB)
                .Concat(_store.Live(root.RootId, batch.Paths).Select(item => item.ObjectId)).Distinct().ToArray();
            foreach (var id in ids)
                if (_store.ByObject(root.RootId, id) is { } current) paths.Add(current.Path);
            foreach (var basis in _store.BaselinesTouching(pair.PairId, side, ids, batch.Paths))
            {
                paths.Add(basis.A.Path); paths.Add(basis.B.Path);
                if (_store.ByObject(pair.A.RootId, basis.A.ObjectId) is { } a) paths.Add(a.Path);
                if (_store.ByObject(pair.B.RootId, basis.B.ObjectId) is { } b) paths.Add(b.Path);
            }
        }
        var predicates = Predicates(pair, LinkExclusions.None);
        paths.RemoveWhere(path => !predicates.A(path) || !predicates.B(path));
        batch.AliasPaths.UnionWith(paths.Except(batch.Paths, StringComparer.OrdinalIgnoreCase));
        RunTrace.Emit($"reconciliation alias paths read: selected={batch.Paths.Count} aliases={batch.AliasPaths.Count}");
        return paths.ToArray();
    }

    private async Task<BothScan> ScanBatchAsync(PairDef pair, ReconcileBatch batch,
        ProgressThrottle throttle, CancellationToken ct)
    {
        var paths = ReconciliationAliasPaths(pair, batch);
        var a = await ReadSide(pair.A, Side.A);
        var b = await ReadSide(pair.B, Side.B);
        batch.ObservedAliases.UnionWith(a.ExactCreationObserved.Intersect(b.ExactCreationObserved,
            StringComparer.OrdinalIgnoreCase).Where(path => PairPath.Observed(path, a.IncompleteSubtrees) &&
                PairPath.Observed(path, b.IncompleteSubtrees)));
        batch.MovingUnitObserved = batch.MovingUnit && paths.All(batch.ObservedAliases.Contains);
        RunTrace.Emit($"hashed {batch.ProofFiles} files / {batch.ProofBytes} bytes in batch");
        ReconciliationCoverage.ObserveBatch(batch, a, b);
        return new(a, b) { Unknown = batch.Remaining };

        async Task<SideScan> ReadSide(RootDef root, Side side)
        {
            var cursor = _store.Cursor(root.RootId) ?? string.Empty;
            var gen = _store.BeginGeneration(root.RootId, "delta");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var inventory = _endpoints.Inventory(root);
            var predicates = Predicates(pair, LinkExclusions.None);
            var include = side == Side.A ? predicates.A : predicates.B;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            RunTrace.Emit($"reconciliation scoped refresh start: side={side} paths={paths.Count}");
            var read = await ReadScopesAsync(pair, root, side, inventory, paths, cursor, gen,
                include, id => ids.Add(id), throttle, ct, exact: true);
            RunTrace.Emit($"reconciliation scoped refresh end: side={side} paths={paths.Count} observed={read.Observed.Count} ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            await ProveReconciliationBatchAsync(root, inventory, batch, read, ct);
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
