namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A full walk (including gap recovery and cursor fallback) bypasses targeted scan retries.
    // Capture before it can stream snapshot writes, then settle through the same identity owner.
    private async Task<SideScan> ScanWithIdentityRefreshAsync(PairDef pair, Side side, ProgressThrottle throttle,
        CancellationToken ct, IReadOnlyDictionary<string, string>? inventoryCursors, bool firstSync)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var predicates = Predicates(pair, LinkExclusions.None);
        var include = side == Side.A ? predicates.A : predicates.B;
        var scopes = root.Provider == SyncProviders.Local
            ? _store.ReadSyncRetries(pair.PairId).Where(retry => CachedIdentityRefresh.NeedsObservation(retry) &&
                (retry.ReadRoots is null || retry.ReadRoots.Contains(root.RootId)) && include(retry.Path))
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
