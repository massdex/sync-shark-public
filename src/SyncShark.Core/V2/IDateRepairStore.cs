namespace Massdex.SyncShark.V2;

internal interface IDateRepairStore
{
    void PrepareDateRepairExclusions(IEnumerable<string> normalizedPaths);
    void PrepareDateWriteEvidence(string pairId,
        IEnumerable<(string Path, DateTimeOffset Occurred)> rows, bool requireLogs);
    IReadOnlyList<Baseline> DateRepairPage(string pairId, string after, int limit, Side side,
        long startUtcTicks, long endUtcTicks, bool mismatch);
    void PrepareDateRepair(string pairId);
    bool DateRepairExcluded(string path);
    bool DateRepairHeld(PairDef pair, Baseline baseline);
    string? DateWriteSkipReason(Side side, ItemState local, string? hash);
    bool DateLogCorrelated(ItemState local);
    DateTimeOffset RepairDate(Baseline baseline, Side side, DateTimeOffset time,
        Func<DateTimeOffset, DateTimeOffset> setTime, DateTimeOffset? originalTime,
        ItemState? live, string? verifiedHash);
}
