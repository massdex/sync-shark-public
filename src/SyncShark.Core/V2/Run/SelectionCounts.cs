namespace Massdex.SyncShark.V2;

/// <summary>Selection totals; amendment reasons are subsets of changed, not extra selected rows.</summary>
internal sealed record SelectionCounts(long Enumerated, long Changed, long New, long Missing, long Unaffected)
{
    internal long Orphan { get; init; }
    internal long Moved { get; init; }
    internal long Case { get; init; }
}
