namespace Massdex.SyncShark;

/// <summary>
/// SS-D02: the one shared classification of a sync run's outcome. Every surface that states whether a
/// run succeeded - the Home latest-run summary, a retained run summary, the Runs surface and the tray
/// notification - projects the same explicit counts and status flags through this type, so a stopped,
/// partial or unrecorded run can never be presented as a success by one consumer while another knows
/// better.
///
/// Success requires counts that are actually recorded, a completed run, no stop and no failed, blocked,
/// conflicting, unverified or not-attempted work. An intentional skip is counted separately from
/// unresolved work, so a deliberate skip stays visible without being reported as a failure, and a
/// retained record whose counts are absent, unreadable or incomplete is never a success.
/// </summary>
public enum SyncRunOutcomeKind
{
    /// Recorded counts, a completed run, no stop and no unresolved work.
    Success,

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
        SyncRunOutcomeKind.Stopped => "Sync Stopped",
        SyncRunOutcomeKind.NeedsAttention => "Sync Needs Attention",
        _ => "Sync Outcome Not Recorded"
    };

    /// <summary>The one-sentence body the tray states beside <see cref="Headline"/>.</summary>
    public string NotificationDetail => Kind switch
    {
        SyncRunOutcomeKind.Success => "The latest run finished and is available in Activity.",
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
            if (NeedsVerification > 0) lines.Add("Needs verification: " + NeedsVerification);
            if (NotAttempted > 0) lines.Add("Not attempted: " + NotAttempted);
            if (Skipped > 0) lines.Add("Skipped: " + Skipped);
            return lines;
        }
    }

    public static SyncRunOutcomeProjection Classify(SyncRunOutcomeInput input)
    {
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
