namespace Massdex.SyncShark.V2;

/// <summary>The full plan for one pair generation.</summary>
public sealed record Plan(string PairId, long Gen, IReadOnlyList<SyncAction> Actions, IReadOnlyList<Notice> Notices)
{
    public string? RunUnavailable { get; init; }
    public IReadOnlyList<SyncAction> DeferredPlanningActions { get; init; } = [];
    public IReadOnlyList<string> RecoveryPending { get; init; } = [];
    /// <summary>Safety-filtered effects, reported as Skipped but never submitted to an executor.</summary>
    public IReadOnlyList<SyncAction> SkippedActions { get; init; } = [];

    // Only rules newly added to a reviewed prepare protect carried descendants; normal decision 80 is unchanged.
    internal IReadOnlyList<string> NarrowedIgnoreRules { get; init; } = [];

    public IReadOnlyList<HeldDeletion> HeldDeletions { get; init; } = [];
    public int DeletionEvidenceResolved { get; init; }
    public IReadOnlyList<HeldDeletion> UnreviewedHeldDeletions { get; init; } = [];
    public PlanOptions? FirstSyncOptions { get; init; }

    /// <summary>
    /// Paths the prepare saw on both sides with the state each side showed the reviewed apply
    /// treats their later one-sidedness as ordinary deletion propagation when the survivor is unchanged. The CLI
    /// persists this in the prepare record; planner-only callers leave it empty.
    /// </summary>
    public IReadOnlyList<MatchedPair> MatchedBothSides { get; init; } = [];

    /// <summary>
    /// each side's change-feed cursor taken before this plan's scan read that side (for a full
    /// Dropbox listing, the cursor taken before the listing), so any change during or after the scan is in the feed
    /// from it. A Dropbox whole-folder delete proves what it removed from this feed; null keeps the per-item path.
    /// </summary>
    public string? ScanCursorA { get; init; }

    /// <inheritdoc cref="ScanCursorA" />
    public string? ScanCursorB { get; init; }

    // Observation checkpoints, not execution progress. Pending evidence may pin these behind ScanCursor*.
    // The prepare edge persists them; successful first-sync apply promotes them to root sync cursors.
    internal IReadOnlyDictionary<string, string> InventoryCursors { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    internal string? InventoryScope { get; init; }
}
