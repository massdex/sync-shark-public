namespace Massdex.SyncShark;

public sealed record SyncCliFirstSyncResultData(string PairId, string PairName, int AppliedItems, int SkippedItems,
    bool FirstSyncComplete, int Uploaded, int Downloaded, int Unchanged, int Conflicts, int Blocked,
    IReadOnlyList<SyncDecision>? Files = null, string? RunId = null, int TotalFiles = 0,
    int Returned = 0, bool HasMore = false, IReadOnlyList<SyncCliChoiceEffectData>? ChoiceEffects = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Failed { get; init; }
    public int NotAttempted { get; init; }
    public int NeedsVerification { get; init; }
    public bool Stopped { get; init; }
    public string? Outcome { get; init; }
    public int Pending { get; init; }
    public long UrgentPending { get; init; }
    public IReadOnlyDictionary<string, long> IndexingRemainingByRoot { get; init; } = new Dictionary<string, long>();

    /// <summary>Core v2 per-run throughput measurements; absent when collection is disabled.</summary>
    public V2.RunDiagnostics? Diagnostics { get; init; }

    /// <summary>Core v2: reviewed effects not completed, so the first sync stays open; 0 when complete.</summary>
    public int Deferred { get; init; }

    /// <summary>Core v2: exact held paths whose deletion evidence remains unresolved after apply.</summary>
    public int DeletionEvidencePendingCount { get; init; }

    /// <summary>Reviewed evidence paths still pending; rerunning the saved apply may resolve them.</summary>
    public int ReviewedDeletionEvidencePendingCount { get; init; }

    /// <summary>New held paths not in the frozen approval; require a follow-up review, never automatic authorization.</summary>
    public int UnreviewedPending { get; init; }

    /// <summary>At most fifty pending deletion-evidence paths, not an authorization for their descendants.</summary>
    public IReadOnlyList<string> DeletionEvidencePendingExamples { get; init; } = [];

    /// <summary>Distinct paths whose content is not confirmed, keeping the first sync incomplete.</summary>
    public int ContentUnconfirmedCount { get; init; }

    /// <summary>At most fifty paths whose content is not confirmed.</summary>
    public IReadOnlyList<string> ContentUnconfirmedExamples { get; init; } = [];

    /// <summary>Core v2: the catalog's "first sync not complete" line when <see cref="Deferred"/> is not 0.</summary>
    public string? Incomplete { get; init; }

    /// <summary>
    /// Core v2: rebuildable paths whose cloud copy could not be removed (the delete was blocked), so the first sync
    /// stays incomplete and each path is left to ordinary planning.
    /// </summary>
    public IReadOnlyList<string> BlockedRebuildables { get; init; } = [];

    /// <summary>Durable open notices by reason; completion does not imply every item succeeded.</summary>
    public IReadOnlyDictionary<string, int> OpenIssues { get; init; } = new Dictionary<string, int>();
}
