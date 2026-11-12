namespace Massdex.SyncShark.Engine;

/// <summary>
/// One read-only page of a known recovery operation's members and its ordinal continuation.
/// Recorded allocations are locators, not custody proof; recorded stages still require Local verification.
/// </summary>
public sealed record RecoveryItemsView
{
    public IReadOnlyList<FolderRemovalItem> Items { get; }
    public long LastOrdinal { get; }
    public bool HasMore { get; }

    /// <summary>
    /// Captures the bounded page supplied by the recovery authority, preserving each member's evidence and generation.
    /// An empty page retains the requested after-ordinal; only the authority determines whether more rows exist.
    /// </summary>
    public RecoveryItemsView(IReadOnlyList<FolderRemovalItem> items, long lastOrdinal, bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
        LastOrdinal = lastOrdinal;
        HasMore = hasMore;
    }
}
