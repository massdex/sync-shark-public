namespace Massdex.SyncShark.V2;

/// <summary>The outcome summary of one run, with per-direction action counts.</summary>
public sealed record RunReport(string PairId, string Outcome, int Done, int Failed, int Held, int Uploaded,
    int Downloaded, int Deleted, int Moved, int KeptBoth, IReadOnlyList<ActionResult> Failures)
{
    internal bool CompletionFailed { get; init; }
    internal bool AttemptFailed { get; init; }
    internal bool Paused { get; init; }
    internal bool Cancelled { get; init; }
    internal bool PlanBoundExceeded { get; init; }
    internal bool IncludesRetryDebt { get; init; }
    internal Exception? HaltedBy { get; init; }

    /// <summary>Inventory/planning reached the provider; individual effect failures have their own notices.</summary>
    public bool ProviderReached { get; init; }

    /// <summary>Effects whose commit could not be confirmed, including those outside the failure sample.</summary>
    public int Uncertain { get; init; }

    /// <summary>Pending intents protected by temporary verification/evidence holds, not failed effects.</summary>
    public int PendingVerification { get; init; }
    public long UrgentPending { get; init; }

    /// <summary>Total failed or unconfirmed effects, independent of the retained examples.</summary>
    public int FailureCount => Failed + Uncertain;

    /// <summary>Unfinished effect/verification obligations, not a distinct-file count (categories can overlap).</summary>
    public int Pending => RunOutcomes.Pending(Failed, Uncertain, Held, Skipped);

    /// <summary>Live root indexing status sampled for presentation, never sync debt or retained run history.</summary>
    public IReadOnlyDictionary<string, long> IndexingRemainingByRoot { get; init; } = new Dictionary<string, long>();

    /// <summary>The requested decision's failure explanation, even when it falls outside the example sample.</summary>
    public string? DecisionFailureReason { get; init; }

    /// <summary>First-sync apply: reviewed effects not completed (dropped, held, failed, skipped); 0 = complete.</summary>
    public int Deferred { get; init; }

    public IReadOnlyList<string> DeletionEvidencePending { get; init; } = [];
    public int ReviewedDeletionEvidencePendingCount { get; init; }
    public int UnreviewedPending { get; init; }

    /// <summary>Distinct paths awaiting content confirmation, including any also awaiting deletion evidence.</summary>
    public int ContentUnconfirmedCount { get; init; }

    /// <summary>At most fifty paths awaiting content confirmation.</summary>
    public IReadOnlyList<string> ContentUnconfirmedExamples { get; init; } = [];

    /// <summary>
    /// First-sync rebuildable paths whose cloud delete never reached the endpoint, so the first sync stays incomplete;
    /// each is left to ordinary planning and listed for the user.
    /// </summary>
    public IReadOnlyList<string> RebuildablesBlocked { get; init; } = [];

    /// <summary>Effects skipped because their reviewed preconditions no longer held.</summary>
    public int Skipped { get; init; }

    /// <summary>Display-only grouped pending space notices; durable retry state remains per path.</summary>
    public IReadOnlyList<FreeSpaceNoticeSummary> FreeSpacePending { get; init; } = [];

    /// <summary>Final throughput measurements, or null when instrumentation is disabled.</summary>
    public RunDiagnostics? Diagnostics { get; init; }

    /// <summary>The instant the run finished; the owner stamps it, so the edge can order two completions honestly.</summary>
    public DateTimeOffset CompletedUtc { get; init; }
}
