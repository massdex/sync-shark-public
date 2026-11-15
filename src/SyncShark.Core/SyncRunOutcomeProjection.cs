namespace Massdex.SyncShark;

/// <summary>
/// SS-D02: shared presentation of the run owner's explicit disposition. Core v2 reports
/// carry their outcome; presentation never re-derives it from counters. Legacy records
/// without that field retain their conservative count-based fallback. Partial means a
/// completed attempt with retained work, not convergence or automatic manual review.
/// </summary>
public enum SyncRunOutcomeKind
{
    /// Recorded counts, a completed run, no stop and no unresolved work.
    Success,

    /// The attempt completed with retained work; not an automatic request for manual review.
    Partial,

    /// The pair paused at a safe point.
    Paused,

    /// Pre-effect admission exceeded the requested plan bound.
    PlanBoundExceeded,

    /// Authoritative state is damaged; recovery is required before further effects.
    StateDamaged,

    /// The run carries failed, blocked, conflicting, unverified or not-attempted work.
    NeedsAttention,

    /// The run was stopped. Its remaining unresolved counts stay visible beside the stop.
    Stopped,

    /// The run has not completed yet, or its retained record does not state the counts needed to judge
    /// it. This is never a success claim.
    NotRecorded
}

/// <summary>
/// The explicit inputs one run's outcome is classified from. A caller whose retained record omits,
/// corrupts or incompletely states the counts sets <see cref="CountsRecorded"/> false, which keeps the
/// outcome out of <see cref="SyncRunOutcomeKind.Success"/>. Fault evidence supplied through
/// <see cref="Failed"/> is honoured even when the remaining counts are not recorded, because a retained
/// failure is direct evidence about that run regardless of the counts beside it.
/// </summary>
public sealed record SyncRunOutcomeInput
{
    public string? Outcome { get; init; }
    public int Pending { get; init; }
    public IReadOnlyDictionary<string, long> IndexingRemainingByRoot { get; init; } = new Dictionary<string, long>();
    public int Deferred { get; init; }
    public bool Completed { get; init; } = true;
    public bool Stopped { get; init; }
    public bool CountsRecorded { get; init; } = true;
    public int Uploaded { get; init; }
    public int Downloaded { get; init; }
    public int Unchanged { get; init; }
    public int Applied { get; init; }
    public int Failed { get; init; }
    public int Blocked { get; init; }
    public int Conflicts { get; init; }
    public int NeedsVerification { get; init; }
    public int NotAttempted { get; init; }
    public int Skipped { get; init; }
    public int Recovered { get; init; }
    public int OnlineOnly { get; init; }
}

/// <summary>
/// One classified run outcome. Consumers format their own surface from these facts; none of them
/// re-derives success or failure from a timestamp, a recorded status word or a single count.
/// </summary>
public sealed record SyncRunOutcomeProjection
{
    private SyncRunOutcomeProjection(SyncRunOutcomeInput input, SyncRunOutcomeKind kind)
    {
        Input = input;
        Kind = kind;
    }

    private SyncRunOutcomeInput Input { get; }

    public SyncRunOutcomeKind Kind { get; }
    public bool Completed => Input.Completed;
    public bool Stopped => Input.Stopped;
    public bool CountsRecorded => Input.CountsRecorded;
    public int Uploaded => Input.Uploaded;
    public int Downloaded => Input.Downloaded;
    public int Unchanged => Input.Unchanged;
    public int Applied => Input.Applied;
    public int Failed => Input.Failed;
    public int Blocked => Input.Blocked;
    public int Conflicts => Input.Conflicts;
    public int NeedsVerification => Input.NeedsVerification;
    public int NotAttempted => Input.NotAttempted;
    public int Skipped => Input.Skipped;
    public int Recovered => Input.Recovered;
    public int OnlineOnly => Input.OnlineOnly;

    /// <summary>The failed and conflicting count every surface already labelled "Needs attention".</summary>
    public int AttentionItems => Input.Blocked + Input.Conflicts;

    /// <summary>
    /// Every item that still needs a person or a later run: failed/blocked, conflicting, awaiting
    /// verification and not attempted. A recorded failure is a subset of the recorded blocked count, so
    /// the larger of the two counts the same work once without losing a record that states only failures.
    /// </summary>
    public int UnresolvedItems => Math.Max(Input.Blocked, Input.Failed) + Input.Conflicts +
        Input.NeedsVerification + Input.NotAttempted;

    public bool IsSuccess => Kind == SyncRunOutcomeKind.Success;

    /// <summary>True whenever the outcome is not a plain success and needs truthful attention wording.</summary>
    public bool Attention => Kind != SyncRunOutcomeKind.Success;

    /// <summary>The short outcome title every surface and the tray use, so wording cannot diverge.</summary>
    public string Headline => Kind switch
    {
        SyncRunOutcomeKind.Success => "Sync Completed",
        SyncRunOutcomeKind.Partial => "Sync Completed With Pending Work",
        SyncRunOutcomeKind.Paused => "Sync Paused",
        SyncRunOutcomeKind.PlanBoundExceeded => "Plan Bound Exceeded",
        SyncRunOutcomeKind.StateDamaged => "State Damaged",
        SyncRunOutcomeKind.Stopped => "Sync Stopped",
        SyncRunOutcomeKind.NeedsAttention => "Sync Needs Attention",
        _ => "Sync Outcome Not Recorded"
    };

    /// <summary>The one-sentence body the tray states beside <see cref="Headline"/>.</summary>
    public string NotificationDetail => Kind switch
    {
        SyncRunOutcomeKind.Success => "The latest run finished and is available in Activity.",
        SyncRunOutcomeKind.Partial => "The attempt finished. Pending work remains saved for retry; see Activity for details.",
        SyncRunOutcomeKind.Paused => "The pair paused at a safe point. Resume it to continue.",
        SyncRunOutcomeKind.PlanBoundExceeded => "No new effects were started because the plan exceeded the requested bound.",
        SyncRunOutcomeKind.StateDamaged => "No further state writes or effects are allowed. Restore a verified backup or use the verified rollback recovery procedure before retrying.",
        SyncRunOutcomeKind.Stopped =>
            "The latest run was stopped. Open Activity to see what finished and what still needs verification.",
        SyncRunOutcomeKind.NeedsAttention =>
            "The latest run reported items that need your input. Open Activity for the exact files and reasons.",
        _ => "The latest run has no complete recorded outcome yet. Open Activity for its recorded state."
    };

    /// <summary>
    /// The word stated beside a run's time and counts. A run whose record claims completion without the
    /// counts needed to judge it says so instead of claiming it finished.
    /// </summary>
    public string StatusWord => Kind switch
    {
        SyncRunOutcomeKind.Success => "Finished",
        SyncRunOutcomeKind.Partial => "Partial",
        SyncRunOutcomeKind.Paused => "Paused",
        SyncRunOutcomeKind.PlanBoundExceeded => "Plan bound exceeded",
        SyncRunOutcomeKind.StateDamaged => "State damaged",
        SyncRunOutcomeKind.Stopped => "Stopped",
        SyncRunOutcomeKind.NeedsAttention => "Needs Attention",
        _ => "Outcome not recorded"
    };

    /// <summary>
    /// The labelled counts every summary shows. Intentional skips, unverified items and not-attempted
    /// work each keep their own line so none of them is read as a transfer or as a failure.
    /// </summary>
    public IReadOnlyList<string> CountLines
    {
        get
        {
            var lines = new List<string>
            {
                "Uploaded: " + Uploaded,
                "Downloaded: " + Downloaded,
                "Unchanged: " + Unchanged,
                "Needs attention: " + AttentionItems
            };
            if (Input.Outcome is not null)
            {
                lines.Add("Outcome: " + Input.Outcome);
                lines.Add("Pending: " + Input.Pending);
                lines.Add("Deferred: " + Input.Deferred);
            }
            foreach (var root in Input.IndexingRemainingByRoot.Where(root => root.Value > 0))
                lines.Add($"Indexing in progress ({root.Key}): {root.Value} remaining");
            if (NeedsVerification > 0) lines.Add("Needs verification: " + NeedsVerification);
            if (NotAttempted > 0) lines.Add("Not attempted: " + NotAttempted);
            if (Skipped > 0) lines.Add("Skipped: " + Skipped);
            return lines;
        }
    }

    public static SyncRunOutcomeProjection Classify(SyncRunOutcomeInput input)
    {
        if (input.Outcome is not null)
            return new SyncRunOutcomeProjection(input, input.Outcome switch
            {
                V2.RunOutcomes.Complete => SyncRunOutcomeKind.Success,
                V2.RunOutcomes.Partial => SyncRunOutcomeKind.Partial,
                V2.RunOutcomes.Paused => SyncRunOutcomeKind.Paused,
                V2.RunOutcomes.PlanBoundExceeded => SyncRunOutcomeKind.PlanBoundExceeded,
                V2.RunOutcomes.StateDamaged => SyncRunOutcomeKind.StateDamaged,
                V2.RunOutcomes.Stopped => SyncRunOutcomeKind.Stopped,
                V2.RunOutcomes.Failed => SyncRunOutcomeKind.NeedsAttention,
                _ => SyncRunOutcomeKind.NotRecorded
            });
        var kind = input.Stopped ? SyncRunOutcomeKind.Stopped
            : Math.Max(input.Blocked, input.Failed) + input.Conflicts + input.NeedsVerification +
                input.NotAttempted > 0 ? SyncRunOutcomeKind.NeedsAttention
            : !input.CountsRecorded || !input.Completed ? SyncRunOutcomeKind.NotRecorded
            : SyncRunOutcomeKind.Success;
        return new SyncRunOutcomeProjection(input, kind);
    }

    /// <summary>
    /// The projection of one in-memory run result. Its counts are complete by construction: the result
    /// itself is the record. <paramref name="completed"/> stays false for a run that is still going.
    /// </summary>
    public static SyncRunOutcomeProjection FromResult(SyncRunResult result, bool completed = true) =>
        Classify(new SyncRunOutcomeInput
        {
            Outcome = result.Outcome, Pending = result.Pending, Deferred = result.Deferred,
            IndexingRemainingByRoot = result.IndexingRemainingByRoot,
            Completed = completed,
            CountsRecorded = true,
            Stopped = result.Stopped,
            Uploaded = result.Uploaded,
            Downloaded = result.Downloaded,
            Unchanged = result.Identical,
            Applied = result.AppliedItems,
            Failed = result.Failed,
            Blocked = result.Blocked,
            Conflicts = result.Conflicts,
            NeedsVerification = result.NeedsVerification,
            NotAttempted = result.NotAttempted,
            Skipped = result.Skipped,
            Recovered = result.Recovered,
            OnlineOnly = result.OnlineOnly
        });
}
