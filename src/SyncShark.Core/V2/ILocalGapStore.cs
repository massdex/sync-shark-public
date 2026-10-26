namespace Massdex.SyncShark.V2;

/// <summary>Optional local-store capability. All pages and publication transactions are bounded.</summary>
internal interface ILocalGapStore
{
    LocalGapEpoch? GapEpoch(string rootId);
    void BeginGap(string rootId, string journalCursor, long boundary, bool trustFileUsn,
        string reason = "journal history discontinuity", bool protect = true);
    IReadOnlyList<ItemState> GapDiscoveryPage(string rootId, LocalGapEpoch epoch, int limit);
    void GapDiscovered(string rootId, LocalGapEpoch epoch, ItemState item, bool requiresHash);
    void GapDiscoveredBatch(string rootId, LocalGapEpoch epoch, IReadOnlyList<(ItemState Item, bool RequiresHash)> items);
    /// <summary>Records the discovery outcome on remaining entity work; queues and holds are unchanged.</summary>
    void SetGapDiscoveryReasons(string rootId, LocalGapEpoch epoch, IReadOnlyList<(string ObjectId, string Reason)> reasons) { }
    void GapDiscoveryDone(string rootId, LocalGapEpoch epoch);
    /// <summary>Run-start only: publishes staged metadata discharges and discovery frontier.</summary>
    void PromoteGapDiscovery(string rootId) { }
    IReadOnlyList<ItemState> GapHashPage(string rootId, LocalGapEpoch epoch, int limit);
    IReadOnlyList<(ItemState Item, bool Urgent)> GapHashWorkPage(string rootId, LocalGapEpoch epoch, int limit, bool includeBulk) =>
        includeBulk ? GapHashPage(rootId, epoch, limit).Select(item => (item, false)).ToArray() : [];
    void StageGapHash(string rootId, LocalGapEpoch epoch, ItemState item, string hash, LocalGapProof proof);
    long GapResultWatermark(string rootId);
    IReadOnlyList<LocalGapResult> GapResults(string rootId, long through, int limit, long after = 0);
    bool AcceptGapResult(string rootId, LocalGapResult result);
    void RejectGapResult(string rootId, LocalGapResult result);
    bool HasGapProtection(string rootId);
    /// <summary>Exact entity obligations only; excludes the lazy root discovery frontier and planning handoff.</summary>
    IReadOnlySet<string> GapObligations(string rootId, IReadOnlyList<string> objectIds);
    void RetireGapItem(string rootId, LocalGapEpoch epoch, ItemState item);
    void SetGapStatus(string rootId, bool discovery, string status);
    string GapProgress(string rootId);
    void DeferGapResult(string rootId, LocalGapResult result, string reason, bool recordFailure = true);
    void DeferGapHash(string rootId, LocalGapEpoch epoch, ItemState item, string reason);
    void FailGapItem(string rootId, LocalGapEpoch epoch, ItemState item, string reason);
    void RetryGapFailures(string rootId);
    bool IsIncompleteInitialInventory(string rootId);
}
