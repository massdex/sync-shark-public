namespace Massdex.SyncShark.V2;

/// <summary>
/// One bounded keyset page of a pair's retained Recovery rows: the rows, the limit used, whether more rows are
/// known to follow (or may follow when the 500-row engine bound prevented an extra probe), the exclusive cursor
/// that continues the scan, and the hint that explains what the page means (one page per call).
/// </summary>
public sealed record SyncCliRecoveryPageData(string PairId, IReadOnlyList<SyncCliRecoveryPageItemData> Items,
    int Limit, int Returned, bool HasMore, bool MayHaveMore, string? NextCursor, string CurrentStateHint,
    IReadOnlyList<string> Warnings);
