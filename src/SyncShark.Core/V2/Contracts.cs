namespace Massdex.SyncShark.V2;

/// <summary>Identifies which side of a pair an endpoint occupies.</summary>
public enum Side { A, B }

/// <summary>Whether an item is a file or a folder.</summary>
public enum ItemKind { File, Folder }

/// <summary>Bit flags describing special states of an item.</summary>
/// <remarks>Placeholder: an online-only cloud-files stub (decision 57). Link: a junction, symlink or mount point that is
/// never followed; it and its path on both sides are excluded from the pair (decision 59).</remarks>
[Flags] public enum ItemFlags { None = 0, Placeholder = 1, Locked = 2, Unreadable = 4, Ignored = 8, Link = 16, CarriedIgnore = 32, ProviderNameRefused = 64, NeedsRehash = 256 }

/// <summary>One item's observed state on one side.</summary>
public sealed record ItemState(string Path, ItemKind Kind, string ObjectId, string? VersionId,
    string? Hash, long Size, DateTimeOffset Mtime, ItemFlags Flags, string? IdentityJson = null)
{
    /// <summary>Planner-only protection of the existing counterpart of a refused move; never persisted as a source flag.</summary>
    public bool ProviderRefusalRetained { get; init; }
}

/// <summary>Whether a change is an add/update or a deletion.</summary>
public enum ChangeKind { Upsert, Delete }

/// <summary>A single inventory change at a cursor position.</summary>
public sealed record Change(ChangeKind Kind, string ObjectId, string Path, ItemState? State, DateTimeOffset? DeletedUtc);

/// <summary>A complete inventory of one root; an empty IncompleteSubtrees means complete.</summary>
public sealed record Snapshot(string RootId, Side Side, long Gen, string CursorAfter,
    IReadOnlyList<ItemState> Items, IReadOnlyList<string> IncompleteSubtrees)
{
    /// <summary>The scanned scope. Outside it, omission is not absence; excluded rows stay untouched.</summary>
    public Func<string, bool>? IncludesPath { get; init; }
}

/// <summary>An incremental set of changes since a cursor.</summary>
public sealed record Delta(string RootId, Side Side, long Gen, string CursorBefore, string CursorAfter,
    IReadOnlyList<Change> Changes, bool CursorInvalid, IReadOnlyList<string> IncompleteSubtrees)
{
    /// <summary>Held paths whose retry bound expired; these no longer retain the side's cursor.</summary>
    public IReadOnlySet<string> ReleasedDeletionEvidence { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unresolved deletion lookups, excluded from Changes and replayed from the retained cursor.</summary>
    public IReadOnlyDictionary<string, string> PendingDeletionEvidence { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>The scanned scope, also protecting excluded descendants of a deleted folder.</summary>
    public Func<string, bool>? IncludesPath { get; init; }
}

/// <summary>The last-synced state of one file identity on both sides.</summary>
public sealed record Baseline(string PairId, string ObjectKey, ItemState A, ItemState B, long Gen);

/// <summary>Deletion and completeness evidence for one joined row; DeletedAt* is the deletion time when known, and
/// DeletedHash* is the last Dropbox revision's content hash when the history supplies one.</summary>
public sealed record Evidence(bool DeletedA, bool DeletedB, bool KnownA, bool KnownB,
    DateTimeOffset? DeletedAtA = null, DateTimeOffset? DeletedAtB = null,
    string? DeletedHashA = null, string? DeletedHashB = null);

/// <summary>One joined identity: its baseline, the current sides and the evidence.</summary>
public sealed record PlanRow(Baseline? Base, ItemState? A, ItemState? B, Evidence Ev)
{
    /// <summary>Ambiguous Windows spelling; neither endpoint may be changed by an inferred match.</summary>
    public bool NameMappingCollision { get; init; }
}

/// <summary>The kind of effect the planner chooses for a row.</summary>
public enum ActionKind { CopyNew, Update, Move, Delete, MkDir, Adopt, Forget, Hold, KeepBoth }

/// <summary>One planned effect on one side.</summary>
public sealed record SyncAction(string ActionId, int Rule, ActionKind Kind, Side Target, string Path,
    string? FromPath, ItemState? Source, ItemState? ExpectedTarget, bool RecoveryFirst, string? ObjectKey)
{
    /// <summary>
    /// Set by the Planner on a rule-32 one-sided remove (a user's first-sync remove choice) and kept when rule 23
    /// collapses that delete; the first-sync choice effects count only these deletes.
    /// </summary>
    public bool ChoiceRemove { get; init; }

    /// <summary>Re-include retired this intent's old history. Verify its effect, but never restore a baseline from it.</summary>
    public bool SuppressReconciledBaseline { get; init; }

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
    /// Set by the Planner on a Delete whose row had nothing on the other side when it was planned (a one-sided
    /// removal). The first-sync apply drops such a delete when the other (local) side now holds the path, reading
    /// this captured fact rather than the mutable store.
    /// </summary>
    public bool OppositeAbsent { get; init; }

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

}

/// <summary>A path that must be revisited even when its inventory cursor has advanced.</summary>
public sealed record SyncRetry(string Path, string Kind, int Attempts, string LastError,
    DateTimeOffset RetryAfter, PrepareMatch? Match = null, HeldDeletion? Held = null,
    IReadOnlyList<SyncAction>? ReviewedActions = null, bool HeldReviewed = false,
    IReadOnlyList<SyncAction>? ProposedActions = null, ItemState? HeldExpected = null);

/// <summary>The kind of notice raised to the user.</summary>
public enum NoticeKind { Info, KeepBoth, EditBeatsDelete, AutoConflict, UserDecision }

/// <summary>
/// One user-facing notice for a decision or conflict. Detail is display text only; ReasonKey is the stable reason
/// code that code matches on (with Rule). NoticeId is assigned by the store.
/// </summary>
public sealed record Notice(string PairId, int Rule, NoticeKind Kind, string Path, string Detail, string ReasonKey,
    string? NoticeId = null, FreeSpaceNoticeMetadata? FreeSpace = null,
    RetainedNoticeVersion? RetainedVersion = null, IReadOnlyList<Side>? RemovalSides = null, string? RemovalOriginReason = null);

/// <summary>Exact per-item provenance for explicitly recovering a newest-wins loser, never a path-based guess.</summary>
public sealed record RetainedNoticeVersion(string ActionId, Side Side, ItemState Loser, ItemState Winner);

/// <summary>Per-path admission facts, retained for presentation without replacing the retry notice.</summary>
public sealed record FreeSpaceNoticeMetadata(string? VolumeRoot, long RequiredBytes,
    long? FreeBytes = null, long? ReserveBytes = null);

/// <summary>The full plan for one pair generation.</summary>
public sealed record Plan(string PairId, long Gen, IReadOnlyList<SyncAction> Actions, IReadOnlyList<Notice> Notices)
{
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

/// <summary>One path seen on both sides at a prepare, with each side's state at that moment.</summary>
public sealed record MatchedPair(string Path, ItemState A, ItemState B);

/// <summary>
/// The prepare-time identity and content stamp of a path matched on both sides, as persisted in the prepare record
/// . A reviewed apply plans a later one-sided survivor as ordinary deletion propagation only when
/// it still equals this state; a changed survivor wins instead (RQ-043).
/// </summary>
public sealed record PrepareMatch(string A, string B)
{
    /// <summary>
    /// Whether the survivor on <paramref name="present"/> is still exactly the prepare-time state. The comparison
    /// follows the stamp KIND recorded at prepare , third-review finding 2): a metadata stamp is
    /// re-checked from size + modification ticks even when a hash is now available, and a hash stamp is re-checked
    /// against the hash (unavailable now = conservatively changed, so the change wins as a safe copy-back).
    /// </summary>
    public bool Unchanged(Side present, ItemState current)
    {
        var stored = present == Side.A ? A : B;
        var split = stored.IndexOf('|');
        if (split < 0) return false;
        if (!string.Equals(stored[..split], current.ObjectId, StringComparison.Ordinal)) return false;
        var kind = stored[(split + 1)..];
        if (kind == "d") return current.Kind == ItemKind.Folder;
        if (current.Kind != ItemKind.File) return false;
        if (kind.StartsWith("m:", StringComparison.Ordinal))
            return string.Equals(kind, Content.MetadataStamp(current), StringComparison.Ordinal);
        if (kind.StartsWith("h:", StringComparison.Ordinal))
            return current.Hash is not null &&
                string.Equals(kind, "h:" + current.Hash, StringComparison.OrdinalIgnoreCase);
        if (kind.StartsWith("v:", StringComparison.Ordinal))
            return current.VersionId is not null &&
                string.Equals(kind, "v:" + current.VersionId, StringComparison.Ordinal);
        return false;
    }
}

/// <summary>A reviewed local-only path and the conditional rule authorized for it, not an effect yet.</summary>
public sealed record HeldDeletion(string Path, Side Local, string Rule = HeldDeletion.NewerDeletionRule)
{
    // Persisted review identifier retained for existing prepares. Decision 78 refines its file semantics:
    // later deletion + equal hashes deletes without Recovery; different restores; unknown stays pending.
    public const string NewerDeletionRule = "dropbox-deletion-newer-than-local:recovery-first-delete;otherwise-upload";

    public const string NewerDeletionDisplayRule =
        "dropbox-deletion-newer-than-local: delete locally without Recovery if identical to the deleted version; " +
        "restore to Dropbox if different; stay pending if either hash is unknown; " +
        "upload if deleted before the local change or never existed";
}

/// <summary>How an applied action ended.</summary>
public enum Outcome { Done, Skipped, Failed, Uncertain }

/// <summary>A recoverable copy of a displaced version.</summary>
public sealed record RecoveryRef(Side Side, string Location, string? VersionId, string Hash, long Size);

/// <summary>A durable index row of one Recovery copy (storage.md F-S2); the Recovery owner restores or expires it.</summary>
public sealed record RecoveryRecord(string RecoveryId, string PairId, string ActionId, Side Side, string Path,
    string Location, string? VersionId, string Hash, long Size, DateTimeOffset Created, DateTimeOffset Expires);

/// <summary>The result of applying one action.</summary>
public sealed record ActionResult(string ActionId, Outcome Outcome, ItemState? NewA, ItemState? NewB,
    RecoveryRef? Recovery, string? Reason)
{
    /// <summary>Fresh reconciliation proved only a temporary wait; null requires ordinary review.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>Handled by ignoring the refused name, not by completing an endpoint effect.</summary>
    public bool ProviderNameRefused { get; init; }

    /// <summary>No effect attempted because an enclosing folder name was refused; no child flag is persisted.</summary>
    public bool ParentNameRefused { get; init; }
    /// <summary>Settled download has no target: atomically forget only its baseline, never propagate a deletion.</summary>
    public bool ForgetAbsentDownloadBaseline { get; init; }
}

/// <summary>A configured pair and its rules.</summary>
public sealed record PairDef(string PairId, string Name, RootDef A, RootDef B, IReadOnlyList<string> Excluded,
    IReadOnlyList<string> IgnoreRules, bool Enabled);

/// <summary>One endpoint root of a pair.</summary>
public sealed record RootDef(string RootId, string Provider, string? AccountId, string RootPath, string RootObjectId);

/// <summary>How a keep-both copy is labelled: format tokens {name}, {side}, {time}, {ext}; side display names.</summary>
public sealed record LabelRule(string Format, string TimeFormat, TimeSpan UtcOffset, string NameA, string NameB);

/// <summary>Planner inputs: the tie window, first-sync flag, label rule, label-attempt bound and any first-sync user choices.</summary>
public sealed record PlanOptions(TimeSpan TieWindow, bool FirstSync, LabelRule Label, int MaxLabelAttempts,
    IReadOnlyDictionary<string, Side?>? Winners = null, IReadOnlyList<OneSidedChoice>? OneSided = null,
    RebuildableSelection? Rebuildables = null,
    IReadOnlyDictionary<string, PrepareMatch>? MatchedAtPrepare = null,
    IReadOnlySet<string>? HeldPaths = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ItemState> MappingContextA { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ItemState> MappingContextB { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.MinValue;

}

/// <summary>
/// A first-sync choice for files only on <paramref name="Present"/> under a pair-relative folder prefix ("" = whole pair):
/// remove them (recovery-first) or copy them to the other side (rule 32).
/// </summary>
public sealed record OneSidedChoice(string Prefix, Side Present, bool Remove);

/// <summary>How complete each side's inventory is for this run.</summary>
public sealed record Completeness(bool CompleteA, bool CompleteB, IReadOnlySet<string> IncompleteSubtreesA, IReadOnlySet<string> IncompleteSubtreesB);
