namespace Massdex.SyncShark.V2;

internal sealed partial class RunScope
{
    // Peak logical rows in a materialized scope, including closure/context before filtering.
    // Counts row cardinality, not temporary collection copies or managed-heap allocations.
    internal long PeakMaterializedRows { get; private set; }

    private void BeginMeasuredScope(string provenanceScope, long rows)
    {
        PeakMaterializedRows = Math.Max(PeakMaterializedRows, rows);
        PlanProvenance.Current.Value?.BeginScope(provenanceScope);
    }

    private Scope LoadMeasuredScope(PairDef pair, HashSet<string> prefixes,
        IReadOnlyCollection<string> changeIdsA, IReadOnlyCollection<string> changeIdsB, long contextRows)
    {
        var a = _store.LiveUnder(pair.A.RootId, prefixes);
        var b = _store.LiveUnder(pair.B.RootId, prefixes);
        // Context ancestors must never discover baselines or widen the item scope.
        var baselines = _store.BaselinesTouching(pair.PairId, Side.A, a.Select(i => i.ObjectId).Concat(changeIdsA), prefixes)
            .Concat(_store.BaselinesTouching(pair.PairId, Side.B, b.Select(i => i.ObjectId).Concat(changeIdsB), prefixes))
            .Distinct().ToArray();
        PeakMaterializedRows = Math.Max(PeakMaterializedRows, (long)a.Count + b.Count + baselines.Length + contextRows);
        return new(a, b, baselines);
    }
}
