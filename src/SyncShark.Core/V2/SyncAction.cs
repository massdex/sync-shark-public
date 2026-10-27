namespace Massdex.SyncShark.V2;

/// <summary>One planned effect on one side.</summary>
public sealed record SyncAction(string ActionId, int Rule, ActionKind Kind, Side Target, string Path,
    string? FromPath, ItemState? Source, ItemState? ExpectedTarget, bool RecoveryFirst, string? ObjectKey)
{
    /// <summary>
    /// Set by the Planner on a rule-32 one-sided remove (a user's first-sync remove choice) and kept when rule 23
    /// collapses that delete; the first-sync choice effects count only these deletes.
    /// </summary>
    public bool ChoiceRemove { get; init; }

    /// <summary>Fresh reviewed signature of ALL descendants, including carried ignored/provider-refused children. Null denotes a legacy review.</summary>
    public string? FrozenRemovalSignature { get; init; }

    /// <summary>The correspondence atomically replaced by this frozen create-only conflict successor.
    /// Until Done, reconciliation retains its authority rather than re-planning a no-baseline winner.</summary>
    public string? ConflictTransitionKey { get; init; }

    /// <summary>A baseline this Done link replaces. The store retires it in the same transaction as the link's
    /// baseline, so planning never emits a separate commit-only Forget before the successor.</summary>
    public string? ObsoleteBaselineKey { get; init; }

    /// <summary>Trusted historical opposite object ID for a prepared deletion without a baseline; absence checks
    /// verify this identity as well as the exact path. Older action JSON may omit it.</summary>
    public string? DeletionSourceObjectId { get; init; }

    private string _actionId = ActionId;
    /// <summary>The durable effect id; finalized by the planner before the action escapes its draft.</summary>
    public string ActionId { get => _actionId; init => _actionId = value; }
    private bool _oppositeAbsent;

    // Only Planner.Decide may finalize; draft actions must not escape that boundary.
    // Emissions carry s+index; post-pass NewAction replacements may still carry the empty draft id.
    internal void FinalizeDraft(string actionId, bool oppositeAbsent)
    {
        if (_actionId.Length != 0 && (_actionId.Length < 2 || _actionId[0] != 's' || !int.TryParse(_actionId.AsSpan(1), out _)))
            throw new InvalidOperationException("Only an unfinalized planner draft can be finalized.");
        _actionId = actionId;
        _oppositeAbsent = oppositeAbsent;
    }
    /// <summary>Explicitly reviewed Defender exclusion covers the deterministic exact sibling stage.</summary>
    public bool QuarantineStage { get; init; }

    /// <summary>Re-include retired this intent's old history. Verify its effect, but never restore a baseline from it.</summary>
    public bool SuppressReconciledBaseline { get; init; }

    /// <summary>The caller reports failures on its own notice or receipt; do not add a free-space path notice.</summary>
    public bool CallerOwnsNotice { get; init; }

    /// <summary>Explicit blocker repair requires the observed revision as well as the stable identity.</summary>
    public bool RequireMoveRevision { get; init; }

    /// <summary>Dedicated repair child effect requires this same-plan folder move to finish first.</summary>
    public string? AfterFolderMove { get; init; }

    /// <summary>Close this same-pair notice atomically only when its explicit restore effect commits Done.</summary>
    public string? ResolvedNoticeId { get; init; }

    /// <summary>
    /// Set by the Planner on a folder Delete it did not collapse because its descendants keep their own Deletes
    /// (mixed ChoiceRemove); the Executor then deletes only the emptied folder instead of expanding it.
    /// </summary>
    public bool DescendantsPlanned { get; init; }

    /// <summary>Post-completion review never carries ignored leftovers into an enclosing delete.</summary>
    public bool PreserveIgnoredChildren { get; init; }

    /// <summary>
    /// A folder Delete whose every frozen descendant was settled one by one (decisions 87/88). The executor skips its
    /// subtree-wide carried-ignored snapshot; the caller already checked growth beyond the frozen set in SQLite.
    /// </summary>
    public bool ContentsSettled { get; init; }

    /// <summary>
    /// Set by the Planner on a Delete whose row had nothing on the other side when it was planned (a one-sided
    /// removal). The first-sync apply drops such a delete when the other (local) side now holds the path, reading
    /// this captured fact rather than the mutable store.
    /// </summary>
    public bool OppositeAbsent { get => _oppositeAbsent; init => _oppositeAbsent = value; }

    /// <summary>
    /// Set by the Planner on the one folder Delete it appends for a chosen rebuildable folder: the single effect that
    /// removes the cloud copy. The run persists the folder as an exclusion only for the paths whose delete finished,
    /// or that the cloud never held.
    /// </summary>
    public bool RebuildableRemove { get; init; }

    /// <summary>
    /// Durable provenance this Delete exists because of a first-sync decision the prepare
    /// reviewed — a one-sided remove choice, a per-path winner's replacement, or a rebuildable removal. It survives
    /// rule-23 collapse and collision rewriting, so the review guard never has to infer provenance from a rule number.
    /// </summary>
    public bool FirstSyncChoice { get; init; }

    /// <summary>Explicit post-completion folder review provenance.</summary>
    public bool RemovalReviewChoice { get; init; }

    /// <summary>
    /// Set by the Planner on a rule-19 deletion-evidence Delete finding 2): it removes an item
    /// that was never matched on both sides, so it may proceed only when the path was in the reviewed prepare or has
    /// a baseline; otherwise the apply refuses it for a fresh review.
    /// </summary>
    public bool NeedsReview { get; init; }

    /// <summary>
    /// finding 4: the signature of the live descendants frozen when the plan was made. The Executor
    /// expands a folder Delete only when the live descendants still match; a change skips the folder (Superseded) and
    /// the next run replans, so a partial subtree is never deleted over new children.
    /// </summary>
    public string? FrozenVictimSignature { get; init; }

    /// <summary>Reviewed ceiling for ignored content carried by this folder delete, not standalone sync actions.</summary>
    public CarriedIgnoredContent? CarriedIgnoredBudget { get; init; }

    /// <summary>Provider session or job evidence retained in the existing intent JSON until its result is resolved.</summary>
    public ProviderEffectCheckpoint? ProviderEffect { get; init; }

    /// <summary>Retain the exact created identity for an explicit restore, atomically with its result.</summary>
    public bool ConflictRestore { get; init; }
    public string? FrozenChoiceId { get; init; }
}
