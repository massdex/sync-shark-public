namespace Massdex.SyncShark.V2;

/// <summary>The notice reasons the Planner raises: each stable key and its wording live here and nowhere else.</summary>
public static class PlanNotices
{
    /// <summary>A run's explicit admission limit held newly planned work without applying it.</summary>
    public const string PlanBoundExceededKey = "plan-bound-exceeded";

    public static Notice PlanBoundExceeded(string pairId, long count, long bound) => new(pairId, 0,
        NoticeKind.Info, string.Empty,
        $"plan of {count} actions exceeds your bound {bound}: review with preview or provenance; rerun with a higher bound to proceed",
        PlanBoundExceededKey) { PlanBound = new NoticePlanBound(bound, count) };

    /// <summary>An item could not be read and was skipped (rule 30).</summary>
    public static readonly NoticeReason CannotRead = new("unreadable", "skipped: cannot read");
    public static readonly NoticeReason DestinationUnobserved = new("destination-unobserved",
        MessageCatalog.Default.Message("destination-unobserved"));

    internal static readonly NoticeReason[] ObservationReasons = [CannotRead, DestinationUnobserved];

    internal static bool NeedsObservation(Notice notice) =>
        ObservationReasons.Any(reason => notice.ReasonKey == reason.Key);

    /// <summary>A cloud-native item is present, but needs an explicit export rather than download.</summary>
    public static readonly NoticeReason ExportRequired = new("export-required",
        "This cloud document requires export to a standard file in Dropbox. The original and any local copy remain unchanged.");

    /// <summary>Two versions could not be compared yet (hashing failed or pending); nothing was done (rule 30).</summary>
    public static readonly NoticeReason ContentUnconfirmed = new("content-unconfirmed", "content not confirmed; will retry");

    /// <summary>An action fell outside the target side's synced area (rule 33).</summary>
    public static readonly NoticeReason OutsidePairArea = new("outside-pair-area", "outside this pair's area");

    /// <summary>The pair has active exclusions (raised by Run with rule 33; the detail lists the paths).</summary>
    public static readonly NoticeReason PairExcluded = new("pair-excluded", "Excluded from sync");

    /// <summary>Both versions were kept, one parked under a label (rules 10, 11, 14, 17, 27).</summary>
    public static readonly NoticeReason KeepBoth = new("kept-both", "kept both versions", AwaitsAcknowledgement: true);

    /// <summary>The edited version was kept over a delete (rules 7, 19, 24).</summary>
    public static readonly NoticeReason EditBeatsDelete =
        new("edit-beats-delete", "kept the edited version", AwaitsAcknowledgement: true);

    /// <summary>The moved version was kept where it moved to (rule 8).</summary>
    public static readonly NoticeReason MovedVersionKept =
        new("moved-version-kept", "kept the moved file", AwaitsAcknowledgement: true);

    /// <summary>A decision was made automatically (rules 4, 13, 32; rule 4 also for an occupied first-sync move).</summary>
    public static readonly NoticeReason AutoConflict =
        new("decided-automatically", "decided automatically", AwaitsAcknowledgement: true);

    /// <summary>An online-only cloud-files stub has no counterpart on the other side; it is kept, never deleted (rule 35).</summary>
    public static readonly NoticeReason StubWithoutCounterpart = new("stub-without-counterpart",
        "online-only placeholder has no copy on the other side; kept, nothing deleted");

    /// <summary>The user must decide (rules 5, 29).</summary>
    public static readonly NoticeReason NeedDecision = new("needs-decision", "needs your decision");

    /// <summary>
    /// A folder delete is deferred because a protected child is under it , third
    /// review): deleting the folder would remove the hidden child without review. Keep the stable
    /// pending-evidence notice identity, but describe all child protections, not only evidence holds.
    /// </summary>
    public static readonly NoticeReason DeletionEvidencePending = new("deletion-evidence-pending",
        "this folder contains protected or pending children; kept until those children are resolved");

    // Both names describe the same folder hold; do not create a second lifecycle identity.
    public static readonly NoticeReason ProtectedChildPending = DeletionEvidencePending;

    public static readonly NoticeReason CloudFilesBindingNeedsReview = new(
        "cloud-files-binding-needs-review", "Cloud Files binding needs review", AwaitsAcknowledgement: true);

    private static readonly HashSet<string> AwaitingKeys = new(
        new[] { CannotRead, ContentUnconfirmed, OutsidePairArea, KeepBoth, EditBeatsDelete, MovedVersionKept,
                AutoConflict, NeedDecision, CloudFilesBindingNeedsReview }
            .Where(reason => reason.AwaitsAcknowledgement).Select(reason => reason.Key)
            // Decisions 26/73: folder-delete outcomes record automatic decisions the user must see.
            .Concat(new[] { "folder-delete-restored", "folder-delete-unrestored", "folder-delete-history",
                "folder-delete-carried-ignored", "folder-delete-ignored-growth" }),
        StringComparer.Ordinal);

    /// <summary>
    /// Whether a notice names an item/scope, rather than a pair-wide display summary.
    /// Empty-path notices of any kind (including informational and user-decision summaries)
    /// never imply a root hold. A typed quarantine may explicitly hold the root itself.
    /// Actual root scan failures remain protected by scan/retry evidence, not display text.
    /// </summary>
    public static bool HasPathScope(Notice notice) => notice.DefenderHistory is null && notice.Storage is null &&
        (notice.Path.Length > 0 || notice.Quarantine is not null);

    /// <summary>
    /// True when Run must never close this notice on its own: a record of an automatic decision (the keyed reasons)
    /// or a pending user decision (any <see cref="NoticeKind.UserDecision"/> notice, e.g. a first-sync tie). The user
    /// closes a decision notice; Run closes it only through a checked resolution (SyncRun.ResolveDecisionAsync).
    /// Matched on the stored Kind and ReasonKey, so it holds for notices read back from the store. Every other reason
    /// is a problem or a transient state that Run closes once the path syncs cleanly (decision 26).
    /// </summary>
    public static bool AwaitsAcknowledgement(Notice notice) =>
        notice.Kind == NoticeKind.UserDecision || AwaitingKeys.Contains(notice.ReasonKey) ||
        notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked;
}
