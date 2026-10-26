namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private Func<string, bool> ScanPathsNotSynced(PairDef pair,
        ItemEligibility.Purpose purpose = ItemEligibility.Purpose.Plan,
        Func<PairDef, Side, ItemState, ItemEligibility.Purpose, string?>? held = null)
    {
        var pairs = _store.Pairs();
        var eligibility = new ItemEligibility(pairs, _productOwned(pair), held);
        return path =>
        {
            var item = _store.Live(pair.A.RootId, [path]).FirstOrDefault() ??
                _store.Live(pair.B.RootId, [path]).FirstOrDefault() ??
                new ItemState(path, ItemKind.File, string.Empty, null, null, 0, DateTimeOffset.MinValue, ItemFlags.None);
            return !eligibility.Evaluate(pair, Side.A, item, purpose).Eligible ||
                !eligibility.Evaluate(pair, Side.B, item, purpose).Eligible;
        };
    }

    // A full walk (including gap recovery and cursor fallback) bypasses targeted scan retries.
    // Capture before it can stream snapshot writes, then settle through the same identity owner.
    private async Task<SideScan> ScanWithIdentityRefreshAsync(PairDef pair, Side side, ProgressThrottle throttle,
        CancellationToken ct, IReadOnlyDictionary<string, string>? inventoryCursors, bool firstSync)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var predicates = Predicates(pair, LinkExclusions.None);
        var include = side == Side.A ? predicates.A : predicates.B;
        var notSynced = ScanPathsNotSynced(pair);
        var scopes = root.Provider == SyncProviders.Local
            ? _store.ReadSyncRetries(pair.PairId).Where(retry => CachedIdentityRefresh.NeedsObservation(retry) &&
                (retry.ReadRoots is null || retry.ReadRoots.Contains(root.RootId)) && include(retry.Path) &&
                !notSynced(retry.Path))
                .Select(retry => retry.Path).Take(V2Settings.ScanRetryScopeLimit).ToArray() : [];
        IReadOnlyList<ItemState> before = scopes.Length == 0 ? [] : _store.Live(root.RootId, scopes);
        var scan = await ScanSideCoreAsync(pair, side, throttle, ct, inventoryCursors, firstSync);
        if (!scan.WalkObserved || scopes.Length == 0) return scan;
        var incomplete = scan.IncompleteSubtrees.ToList();
        var reasons = new Dictionary<string, string>(scan.IncompleteReasons, StringComparer.OrdinalIgnoreCase);
        CachedIdentityRefresh.SettleObserved(_store, pair.PairId, scopes, scan.IncompleteSubtrees,
            root.RootId, before, incomplete, reasons);
        return scan with
        {
            Complete = scan.Complete && incomplete.Count == 0,
            ScopedFullCoverage = incomplete.Count > 0 && !incomplete.Contains(string.Empty),
            IncompleteSubtrees = incomplete, IncompleteReasons = reasons
        };
    }
}
