namespace Massdex.SyncShark.V2;

internal sealed partial class RunScope
{
    private (IReadOnlyList<ItemState> A, IReadOnlyList<ItemState> B, IReadOnlyList<Baseline> Baselines)
        HideProtectedTriples(IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b, IReadOnlyList<Baseline> baselines,
            HashSet<string> hiddenA, HashSet<string> hiddenB) =>
        HideClaimedTriples(a, b, baselines, hiddenA, hiddenB, Planner.ClaimBaselines(baselines, a, b));

    private (IReadOnlyList<ItemState> A, IReadOnlyList<ItemState> B, IReadOnlyList<Baseline> Baselines)
        HideClaimedTriples(IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b, IReadOnlyList<Baseline> baselines,
            HashSet<string> hiddenA, HashSet<string> hiddenB,
            IEnumerable<(Baseline Basis, ItemState? A, ItemState? B)> claims)
    {
        List<Baseline>? visible = null;
        using var claimed = claims.GetEnumerator();
        var claimIndex = -1;
        baselines = Planner.OrderBaselines(baselines);
        var links = _scopeLinks ?? _links;
        for (var index = 0; index < baselines.Count; index++)
        {
            if (index % BaselineBatchSize == 0)
            {
                ProtectionBatchStarting();
                links = (_scopeLinks ?? _links).ForPaths(baselines.Skip(index).Take(BaselineBatchSize)
                    .SelectMany(basis => new[] { basis.A.Path, DropboxNameMapping.MatchPath(basis.A),
                        basis.B.Path, DropboxNameMapping.MatchPath(basis.B) }));
            }
            var basis = baselines[index];
            if (HideClaimedTriple(basis, index, claimed, ref claimIndex, hiddenA, hiddenB, links))
                visible ??= baselines.Take(index).ToList();
            else visible?.Add(basis);
        }
        if (visible is null) return (a, b, baselines);
        if (PlanProvenance.Current.Value is { } provenance)
        {
            foreach (var item in a) if (hiddenA.Contains(item.ObjectId)) provenance.Visibility(Side.A, item, true, "protected-baseline-triple");
            foreach (var item in b) if (hiddenB.Contains(item.ObjectId)) provenance.Visibility(Side.B, item, true, "protected-baseline-triple");
        }
        return (a.Where(item => !hiddenA.Contains(item.ObjectId)).ToArray(),
            b.Where(item => !hiddenB.Contains(item.ObjectId)).ToArray(), visible);
    }

    private bool HideClaimedTriple(Baseline basis, int index,
        IEnumerator<(Baseline Basis, ItemState? A, ItemState? B)> claimed, ref int claimIndex,
        HashSet<string> hiddenA, HashSet<string> hiddenB, LinkExclusions links)
    {
        // No hidden endpoint and no covered baseline means no triple can be removed.
        // Do not build full-pair claim indexes just to return the unchanged scope.
        if (PlanProvenance.Current.Value is null && hiddenA.Count == 0 && hiddenB.Count == 0 &&
            !Covered(basis.A, Side.A, links) && !Covered(basis.B, Side.B, links)) return false;
        while (claimIndex < index)
        {
            if (!claimed.MoveNext()) throw new InvalidOperationException("Incomplete baseline claims.");
            claimIndex++;
        }
        var (_, claimA, claimB) = claimed.Current;
        var covered = (claimA is not null && hiddenA.Contains(claimA.ObjectId)) ||
            (claimB is not null && hiddenB.Contains(claimB.ObjectId)) ||
            hiddenA.Contains(basis.A.ObjectId) || hiddenB.Contains(basis.B.ObjectId) ||
            Covered(basis.A, Side.A, links) || Covered(basis.B, Side.B, links);
        if (!covered) return false;
        if (!PermanentHold(basis.A, links) && !PermanentHold(basis.B, links))
        {
            Defer(basis.A, Side.A, links);
            Defer(basis.B, Side.B, links);
        }
        hiddenA.Add(basis.A.ObjectId);
        hiddenB.Add(basis.B.ObjectId);
        if (claimA is not null) hiddenA.Add(claimA.ObjectId);
        if (claimB is not null) hiddenB.Add(claimB.ObjectId);
        return true;
    }
}
