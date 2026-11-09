using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

public sealed record SyncAuthorityRunHistory(string Id, string Kind, DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc, string Status, string RequestJson, string? ResultJson);

public enum SyncAuthorityOperationKind
{
    CreateLocal,
    CreateRemote,
    UpdateLocal,
    UpdateRemote,
    DeleteLocal,
    DeleteRemote,
    ResolveConflict,
    MoveLocal,
    MoveRemote,
    CrossPairMove,
    AllocateControl,
    Cleanup,
    Reconcile
}

public enum SyncAuthorityOperationStatus
{
    Planned,
    Ready,
    Leased,
    Validating,
    AllocationAuthorized,
    Staged,
    EffectIssuing,
    EffectUnknown,
    EffectObserved,
    Reconciling,
    Verifying,
    Committed,
    CleanupPending,
    Completed,
    Blocked,
    Cancelled,
    Superseded
}

public enum SyncAuthorityAttemptStatus
{
    Started,
    RequestSent,
    ResponseReceived,
    EffectUnknown,
    VerifiedSuccess,
    VerifiedNotApplied,
    Blocked,
    Abandoned
}

public enum SyncAuthorityReceiptCertainty
{
    KnownSuccess,
    KnownNotApplied,
    Unknown,
    KnownConflict,
    KnownPartial
}

public enum SyncAuthorityAllocationKind
{
    TransactionDirectory,
    StageFile,
    LocalRecoveryFile,
    RemoteRecoveryFile,
    UploadSession,
    ControlNamespace
}

public enum SyncAuthorityAllocationStatus
{
    Authorized,
    Creating,
    Created,
    Bound,
    Verified,
    Released,
    Blocked
}

public sealed record SyncAuthorityMember(
    string MemberId,
    string PairId,
    string ObjectId,
    string Role,
    string RootBindingId,
    string? MembershipId,
    string? ObjectVersionId,
    long? ExpectedMembershipGeneration,
    string? ExpectedPathKey,
    string ExpectedStateJson);

public sealed record SyncAuthoritySubtreeReservation(
    string PairId,
    string RootBindingId,
    string RelativePath);

public sealed record SyncAuthorityRetainedSubtreeReservation(
    string OperationId,
    SyncAuthorityOperationStatus Status,
    string PairId,
    string RootBindingId,
    string RelativePath);

public sealed record SyncAuthorityDependency(
    string DependsOnOperationId,
    string Kind);

public sealed record SyncAuthorityAllocation(
    string AllocationId,
    string ControlNamespaceId,
    SyncAuthorityAllocationKind Kind,
    string DeterministicName,
    string IntendedParentIdentity,
    string OwnerNonceHash,
    SyncAuthorityAllocationStatus Status = SyncAuthorityAllocationStatus.Authorized,
    string? BoundIdentity = null,
    string? ContentHash = null,
    long? Size = null);

public sealed record SyncAuthorityNamespaceAllocation(
    string NamespaceAllocationId,
    string OperationId,
    string Side,
    string ParentIdentity,
    string DeterministicName,
    string OwnerNonceHash,
    SyncAuthorityAllocationStatus Status = SyncAuthorityAllocationStatus.Authorized,
    string? BoundIdentity = null);

public sealed record SyncAuthorityControlNamespace(
    string ControlNamespaceId,
    string NamespaceAllocationId,
    string Side,
    string IdentityJson,
    string MarkerIdentityJson,
    string OwnerMarkerHash,
    string Status);

public sealed record SyncAuthorityOperationPlan(
    string OperationId,
    string IdempotencyKey,
    SyncAuthorityOperationKind Kind,
    string? PairId,
    string? ObjectId,
    long ConfigurationRevision,
    string CapabilityProfileId,
    string PreconditionsJson,
    int Priority,
    IReadOnlyList<SyncAuthorityMember> Members,
    IReadOnlyList<SyncAuthorityDependency> Dependencies,
    IReadOnlyList<SyncAuthorityAllocation> Allocations,
    string? OperationGroupId = null,
    IReadOnlyList<SyncAuthorityPlanningAuthority>? PlanningAuthorities = null,
    IReadOnlyList<SyncAuthoritySubtreeReservation>? SubtreeReservations = null);

public sealed record SyncAuthorityPlanningAuthority(
    string ObservationSetId,
    string PairId,
    long ConfigurationRevision,
    string LocalRootBindingId,
    string DropboxRootBindingId,
    string LocalScanGenerationId,
    string DropboxScanGenerationId,
    string PlanningStateFingerprint,
    string? ObjectId = null,
    string? SynchronizationPointId = null,
    string? ConnectionId = null,
    string? ObservationContentFingerprint = null,
    string? SynchronizationPointSetFingerprint = null,
    string? RootAuthorityFingerprint = null);

public sealed record SyncAuthorityPairObservationSet(
    string PairId,
    string? ConnectionId,
    long ConfigurationRevision,
    string LocalRootBindingId,
    string DropboxRootBindingId,
    string LocalScanGenerationId,
    string DropboxScanGenerationId,
    IReadOnlyList<SyncAuthorityObservedEntry> Local,
    IReadOnlyList<SyncAuthorityObservedEntry> Dropbox,
    IReadOnlySet<string> LocalUncertainPaths,
    IReadOnlySet<string> DropboxUncertainPaths,
    IReadOnlySet<string> ExcludedPaths,
    IReadOnlyList<SyncAuthoritySynchronizationPoint> SynchronizationPoints,
    string ContentFingerprint,
    string PlanningStateFingerprint,
    string SynchronizationPointSetFingerprint,
    string RootAuthorityFingerprint)
{
    public SyncAuthorityPlanningAuthority Bind(string observationSetId, string? objectId = null) =>
        new(observationSetId, PairId, ConfigurationRevision, LocalRootBindingId, DropboxRootBindingId,
            LocalScanGenerationId, DropboxScanGenerationId, PlanningStateFingerprint, objectId,
            objectId is null
                ? null
                : SynchronizationPoints.SingleOrDefault(item =>
                    string.Equals(item.ObjectId, objectId, StringComparison.Ordinal))?.SynchronizationPointId,
            ConnectionId, ContentFingerprint, SynchronizationPointSetFingerprint, RootAuthorityFingerprint);
}

internal sealed record SyncAuthorityObservationObjectBinding(
    string Side,
    string SourceRelativePath,
    string RelativePath,
    string ObjectId,
    string ObjectKind,
    string SourceObjectVersionId,
    string SourceIdentityJson,
    string? ContentHash,
    long? Size,
    bool RequirePhysicalIdentity);

internal sealed record SyncAuthorityObservationBindingAuthority(
    string OperationId,
    SyncAuthorityLease Lease,
    IReadOnlyList<SyncAuthorityObservationObjectBinding> Bindings);

internal sealed class SyncAuthorityPlanningInvalidatedException(string operationId)
    : InvalidOperationException($"Operation {operationId} was superseded because its planning authority changed before effect admission.")
{
    public string OperationId { get; } = operationId;
}

public sealed record SyncAuthorityLease(
    string LeaseId,
    string OperationId,
    string WorkerId,
    long FencingToken,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset ExpiresUtc);

public sealed record SyncAuthorityAttempt(
    string AttemptId,
    string OperationId,
    int AttemptNumber,
    string LeaseId,
    long FencingToken,
    string RequestFingerprint,
    string EffectKind,
    SyncAuthorityAttemptStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc);

/// <summary>
/// The narrow effect class one accepted command may carry. It is bound into the command row at
/// acceptance and never derived from request JSON, a pair id, an operation group or ambient state.
/// <see cref="NormalEffect"/> is the ordinary file-effect authority; <see cref="ReadOnly"/> may never
/// issue a request regardless of its sequence; <see cref="ControlAllocation"/> is the one explicitly
/// scoped pair-binding setup allocation and can never authorize another operation kind or scope.
/// </summary>
public enum SyncAuthorityCommandPurpose
{
    NormalEffect,
    ReadOnly,
    ControlAllocation
}

internal static class SyncAuthorityCommandPurposeNames
{
    public const string NormalEffect = "normal_effect";
    public const string ReadOnly = "read_only";
    public const string ControlAllocation = "control_allocation";

    public static string Wire(SyncAuthorityCommandPurpose value) => value switch
    {
        SyncAuthorityCommandPurpose.NormalEffect => NormalEffect,
        SyncAuthorityCommandPurpose.ReadOnly => ReadOnly,
        SyncAuthorityCommandPurpose.ControlAllocation => ControlAllocation,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static SyncAuthorityCommandPurpose Parse(string value) => value switch
    {
        NormalEffect => SyncAuthorityCommandPurpose.NormalEffect,
        ReadOnly => SyncAuthorityCommandPurpose.ReadOnly,
        ControlAllocation => SyncAuthorityCommandPurpose.ControlAllocation,
        _ => throw new InvalidDataException("The retained command admission purpose is not supported.")
    };

    public static bool IsWireValue(string value) => value is NormalEffect or ReadOnly or ControlAllocation;
}

/// <summary>
/// The typed admission of ONE durably accepted command: the exact installation database generation it
/// was accepted under, its unique monotonic admission sequence at that generation, and its narrow
/// effect purpose. The value is descriptive evidence of what the retained command row already says; a
/// caller may hold it, but every consumer re-reads the retained row and re-validates generation,
/// sequence, purpose and status before it permits anything, so a stale, forged or copied value can
/// never become permission. It is never constructed from caller payload.
/// </summary>
public sealed record SyncAuthorityCommandAdmission(
    string CommandRequestId,
    string DatabaseGeneration,
    long AdmissionSequence,
    SyncAuthorityCommandPurpose Purpose)
{
    /// <summary>The ordinary file-effect class. It still passes every existing per-kind operation guard.</summary>
    internal bool AllowsNormalEffect => Purpose == SyncAuthorityCommandPurpose.NormalEffect;

    /// <summary>The narrow pair-binding control-allocation class.</summary>
    internal bool AllowsControlAllocation => Purpose == SyncAuthorityCommandPurpose.ControlAllocation;

    /// <summary>
    /// Whether this admission may register an operation of the given kind. A read-only admission may
    /// never register an operation at all, and the control-allocation admission owns only the one
    /// <see cref="SyncAuthorityOperationKind.AllocateControl"/> registration its immutable payload binds.
    /// </summary>
    internal bool AllowsOperationKind(SyncAuthorityOperationKind kind) => Purpose switch
    {
        SyncAuthorityCommandPurpose.NormalEffect => true,
        SyncAuthorityCommandPurpose.ReadOnly => false,
        SyncAuthorityCommandPurpose.ControlAllocation => kind == SyncAuthorityOperationKind.AllocateControl,
        _ => false
    };

    /// <summary>
    /// Whether this admission may carry a dispatching (request_sent) lease for the given operation kind.
    /// A read-only admission never may; a control-allocation admission may dispatch only its own
    /// allocation operation, which performs no file effect.
    /// </summary>
    internal bool AllowsLeaseDispatch(SyncAuthorityOperationKind kind) => AllowsOperationKind(kind);
}

/// <summary>
/// The pending command identity a caller may hold BEFORE the durable command exists. The type is
/// deliberately non-serializable and non-constructible from data: construction is private and only the
/// internal <see cref="Mint"/> factory creates one, always in the existing command-Guid display format.
/// No public factory accepts an arbitrary string, so a caller can never present a fabricated retained id
/// as its own reservation.
/// It carries the single-use insertion claim for exactly this identity. The claim is consumed by the
/// first real INSERT attempt, so an ambiguous or rolled-back first attempt can never later create a
/// missing row; a genuinely new command requires a fresh pending identity. Reading the exact retained id
/// remains available without consuming anything.
/// </summary>
public sealed class SyncAuthorityCommandReservation
{
    private int _insertionClaimed;

    private SyncAuthorityCommandReservation(string commandId) => CommandId = commandId;

    /// <summary>The exact existing pending identity, in the established command-&lt;Guid&gt; format.</summary>
    public string CommandId { get; }

    /// <summary>True once this ticket's single insertion attempt has been started.</summary>
    internal bool InsertionClaimed => Volatile.Read(ref _insertionClaimed) != 0;

    internal static SyncAuthorityCommandReservation Mint() => new("command-" + Guid.NewGuid().ToString("N"));

    /// <summary>Consumes the one insertion claim. Exactly one caller can ever observe true.</summary>
    internal bool TryClaimInsertion() => Interlocked.Exchange(ref _insertionClaimed, 1) == 0;
}

public sealed record SyncAuthorityOperationSnapshot(
    string OperationId,
    SyncAuthorityOperationKind Kind,
    SyncAuthorityOperationStatus Status,
    string? PairId,
    string? ObjectId,
    long ConfigurationRevision,
    string CapabilityProfileId,
    string IdempotencyKey,
    string PreconditionsJson,
    string? CurrentLeaseId,
    long? CurrentFencingToken,
    IReadOnlyList<SyncAuthorityMember> Members,
    IReadOnlyList<SyncAuthorityDependency> Dependencies,
    IReadOnlyList<SyncAuthorityAllocation> Allocations,
    IReadOnlyList<SyncAuthorityAttempt> Attempts,
    IReadOnlyList<SyncAuthorityPlanningAuthority> PlanningAuthorities,
    IReadOnlyList<SyncAuthoritySubtreeReservation> SubtreeReservations);

public sealed record SyncAuthorityQueueItem(
    string OperationId,
    SyncAuthorityOperationKind Kind,
    SyncAuthorityOperationStatus Status,
    string? PairId,
    string? ObjectId,
    int Priority,
    DateTimeOffset CreatedUtc,
    bool RecoveryRequired);

public sealed record SyncAuthorityActivityProjection(
    DateTimeOffset OccurredUtc,
    string PairId,
    string RelativePath,
    string Outcome,
    string Reason,
    string? OperationId,
    string ItemKind = "Unknown",
    string FileType = "Unknown",
    long? SizeBytes = null,
    string? LocalRoot = null,
    string? DropboxRoot = null,
    string? EventId = null,
    long? Sequence = null,
    IReadOnlyList<string>? Categories = null,
    string Severity = "Information",
    string Entity = "Unknown",
    string? ConnectionId = null,
    string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncEndpointContext? EndpointContext = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncAuthorityOperationKind? OperationKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncActivityPhase? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DecisionCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncActivityProgress? Progress = null);

public sealed record SyncAuthorityQueueProjection(
    string OperationId,
    string PairId,
    string RelativePath,
    DateTimeOffset EnqueuedUtc,
    int Priority,
    string Operation,
    string State,
    bool RecoveryRequired,
    string ItemKind = "Unknown",
    string FileType = "Unknown");

public sealed record SyncAuthorityPathState(
    string RelativePath,
    bool Pending,
    bool Error);

public sealed record SyncRecoveryArtifactProjection(
    string RecoveryArtifactId,
    string OperationId,
    string PairId,
    string ObjectId,
    string Side,
    string OriginalRelativePath,
    string IdentityJson,
    string ContentHash,
    long Size,
    string Status,
    DateTimeOffset RetainedUtc);

public sealed record SyncAuthorityPairConfiguration(
    string DeletionPolicy,
    int DeletionGraceSeconds,
    int RequiredAbsenceObservations,
    string ConflictPolicy,
    string MetadataPolicy,
    bool RecursiveMode,
    bool AutomaticPruning,
    int RecoveryRetentionDays)
{
    internal static SyncAuthorityPairConfiguration FromPair(SyncPair pair) =>
        new(pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1, "manual_preserve_both",
            "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
            RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue);
}

public sealed record SyncAuthorityScanGeneration(
    string ScanGenerationId,
    string PairId,
    string RootBindingId,
    string Side,
    long Generation,
    string TriggerKind,
    DateTimeOffset StartedUtc);

public sealed record SyncAuthorityObservationInput(
    string RelativePath,
    string ObjectKind,
    string IdentityJson,
    string? ContentHash,
    long? Size,
    string EvidenceJson,
    string? BoundObjectId = null);

public sealed record SyncAuthorityBootstrapEvidence(
    string RelativePath,
    string Group,
    string ObjectKind,
    long? LocalSize,
    long? DropboxSize,
    DateTimeOffset? LocalModifiedUtc,
    DateTimeOffset? DropboxModifiedUtc,
    SyncBootstrapMoveReview? Move = null,
    string? BlockedReason = null,
    string? LocalObjectId = null,
    string? LocalObjectVersionId = null,
    bool HasBaseline = false);

public sealed record SyncAuthorityBootstrapEvidencePage(
    IReadOnlyList<SyncAuthorityBootstrapEvidence> Items,
    int Offset,
    int TotalMatchingItems,
    int TotalItems,
    int LocalOnlyItems,
    int DropboxOnlyItems,
    int ConflictItems,
    bool HasMore,
    string LocalScanGenerationId,
    string DropboxScanGenerationId,
    int LocalUpdatedItems = 0,
    int DropboxUpdatedItems = 0);

public sealed record SyncAuthorityMonitorEventInput(
    string EventId,
    string SourceEventKey,
    string? RelativePath,
    string EventKind,
    string EventJson);

public sealed record SyncAuthorityMonitorCursorSnapshot(
    string MonitorCursorId,
    string PairId,
    string RootBindingId,
    string Kind,
    long Generation,
    string? CursorValue,
    string SourceIdentityJson,
    string Status);

public sealed record SyncAuthorityMonitorProjection(
    string PairId,
    string Kind,
    string State,
    DateTimeOffset UpdatedUtc,
    int PendingDirtyItems);

public sealed record SyncAuthorityObservedEntry(
    string RelativePath,
    string ObjectId,
    string ObjectVersionId,
    string MembershipId,
    string RootBindingId,
    string ScanGenerationId,
    long MembershipGeneration,
    string? ContentHash,
    long? Size,
    string EvidenceJson,
    string ObjectKind = "file");

public sealed record SyncAuthorityUploadSessionCheckpoint(
    string AllocationId,
    string OperationId,
    string SessionId,
    long ConfirmedOffset,
    string SourceObjectVersionId,
    long SourceSize,
    string SourceContentHash,
    string Status,
    DateTimeOffset UpdatedUtc);

public sealed record SyncAuthoritySynchronizationPoint(
    string SynchronizationPointId,
    string PairId,
    string ObjectId,
    long AuthorityGeneration,
    string? LocalObjectVersionId,
    string? DropboxObjectVersionId,
    string? LocalMembershipId,
    string? DropboxMembershipId,
    string? LocalRelativePath,
    string? DropboxRelativePath);

public sealed record SyncAuthorityObjectVersion(
    string ObjectVersionId,
    string ObjectId,
    string Side,
    string IdentityJson,
    string? ContentHash,
    long? Size);

public sealed record SyncAuthorityLocalCloudRepresentation(
    string RelativePath,
    string LocalIdentityJson,
    string? LocalContentHash,
    long? LocalSize,
    string DropboxIdentityJson,
    string? DropboxContentHash,
    long? DropboxSize);

public enum SyncAuthorityPlannerOutcomeKind
{
    Unchanged,
    Adopted,
    Operation,
    Conflict,
    Tombstone,
    Move,
    Blocked,
    Skipped
}

public sealed record SyncAuthorityPlannerOutcome(
    string RelativePath,
    string ObjectId,
    SyncAuthorityPlannerOutcomeKind Kind,
    string? OperationId,
    string? ConflictId,
    string? TombstoneId,
    string Reason,
    bool IsBothAbsentConfirmation = false);

public sealed record SyncAuthorityPlannerOptions(
    long ConfigurationRevision,
    string CapabilityProfileId,
    string LocalControlNamespaceId,
    string DropboxControlNamespaceId,
    bool EnableCreateEffects = true,
    bool EnableReplacementEffects = true,
    bool EnableDeletionEffects = false,
    bool EnableMoveEffects = false,
    int DeletionMinimumCompleteObservations = 1,
    TimeSpan? DeletionMinimumInterval = null,
    SyncMode Direction = SyncMode.TwoWay,
    // CC3B-1: one optional final cloud-allocation configuration. It adds no positional argument and no
    // existing call site changes; a null value keeps the legacy allocation policy byte-equivalent.
    SyncAuthorityCloudAllocationConfiguration? CloudAllocations = null,
    SyncUploadSessionPolicy? RemoteUploadSessionPolicy = null);

public sealed record SyncAuthorityTombstoneObservation(
    string TombstoneId,
    int CompleteObservationCount,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    string Status);

public enum SyncAuthorityConflictResolutionKind
{
    KeepLocal,
    KeepDropbox,
    PreserveBoth
}

public sealed record SyncAuthorityConflictSnapshot(
    string ConflictId,
    string PairId,
    string ObjectId,
    long ConflictGeneration,
    string Status,
    string LocalObjectVersionId,
    string DropboxObjectVersionId,
    string? LocalMembershipId,
    string? DropboxMembershipId,
    string LocalRootBindingId,
    string DropboxRootBindingId,
    long LocalMembershipGeneration,
    long DropboxMembershipGeneration,
    string LocalPath,
    string DropboxPath,
    DateTimeOffset CreatedUtc);

public sealed record SyncAuthorityConflictProjection(
    string ConflictId,
    string PairId,
    string RelativePath,
    long ConflictGeneration,
    long? LocalSize,
    long? DropboxSize,
    string? LocalHash,
    string? DropboxHash,
    string LocalIdentityJson,
    string? DropboxEvidenceJson,
    string Status);

internal static class SyncAuthorityNames
{
    public static string OperationKind(SyncAuthorityOperationKind value) => value switch
    {
        SyncAuthorityOperationKind.CreateLocal => "create_local",
        SyncAuthorityOperationKind.CreateRemote => "create_remote",
        SyncAuthorityOperationKind.UpdateLocal => "update_local",
        SyncAuthorityOperationKind.UpdateRemote => "update_remote",
        SyncAuthorityOperationKind.DeleteLocal => "delete_local",
        SyncAuthorityOperationKind.DeleteRemote => "delete_remote",
        SyncAuthorityOperationKind.ResolveConflict => "resolve_conflict",
        SyncAuthorityOperationKind.MoveLocal => "move_local",
        SyncAuthorityOperationKind.MoveRemote => "move_remote",
        SyncAuthorityOperationKind.CrossPairMove => "cross_pair_move",
        SyncAuthorityOperationKind.AllocateControl => "allocate_control",
        SyncAuthorityOperationKind.Cleanup => "cleanup",
        SyncAuthorityOperationKind.Reconcile => "reconcile",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static SyncAuthorityOperationKind OperationKind(string value) => value switch
    {
        "create_local" => SyncAuthorityOperationKind.CreateLocal,
        "create_remote" => SyncAuthorityOperationKind.CreateRemote,
        "update_local" => SyncAuthorityOperationKind.UpdateLocal,
        "update_remote" => SyncAuthorityOperationKind.UpdateRemote,
        "delete_local" => SyncAuthorityOperationKind.DeleteLocal,
        "delete_remote" => SyncAuthorityOperationKind.DeleteRemote,
        "resolve_conflict" => SyncAuthorityOperationKind.ResolveConflict,
        "move_local" => SyncAuthorityOperationKind.MoveLocal,
        "move_remote" => SyncAuthorityOperationKind.MoveRemote,
        "cross_pair_move" => SyncAuthorityOperationKind.CrossPairMove,
        "allocate_control" => SyncAuthorityOperationKind.AllocateControl,
        "cleanup" => SyncAuthorityOperationKind.Cleanup,
        "reconcile" => SyncAuthorityOperationKind.Reconcile,
        _ => throw new InvalidDataException("The retained authority operation kind is not supported.")
    };

    public static string OperationStatus(SyncAuthorityOperationStatus value) => value switch
    {
        SyncAuthorityOperationStatus.Planned => "planned",
        SyncAuthorityOperationStatus.Ready => "ready",
        SyncAuthorityOperationStatus.Leased => "leased",
        SyncAuthorityOperationStatus.Validating => "validating",
        SyncAuthorityOperationStatus.AllocationAuthorized => "allocation_authorized",
        SyncAuthorityOperationStatus.Staged => "staged",
        SyncAuthorityOperationStatus.EffectIssuing => "effect_issuing",
        SyncAuthorityOperationStatus.EffectUnknown => "effect_unknown",
        SyncAuthorityOperationStatus.EffectObserved => "effect_observed",
        SyncAuthorityOperationStatus.Reconciling => "reconciling",
        SyncAuthorityOperationStatus.Verifying => "verifying",
        SyncAuthorityOperationStatus.Committed => "committed",
        SyncAuthorityOperationStatus.CleanupPending => "cleanup_pending",
        SyncAuthorityOperationStatus.Completed => "completed",
        SyncAuthorityOperationStatus.Blocked => "blocked",
        SyncAuthorityOperationStatus.Cancelled => "cancelled",
        SyncAuthorityOperationStatus.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static SyncAuthorityOperationStatus OperationStatus(string value) => value switch
    {
        "planned" => SyncAuthorityOperationStatus.Planned,
        "ready" => SyncAuthorityOperationStatus.Ready,
        "leased" => SyncAuthorityOperationStatus.Leased,
        "validating" => SyncAuthorityOperationStatus.Validating,
        "allocation_authorized" => SyncAuthorityOperationStatus.AllocationAuthorized,
        "staged" => SyncAuthorityOperationStatus.Staged,
        "effect_issuing" => SyncAuthorityOperationStatus.EffectIssuing,
        "effect_unknown" => SyncAuthorityOperationStatus.EffectUnknown,
        "effect_observed" => SyncAuthorityOperationStatus.EffectObserved,
        "reconciling" => SyncAuthorityOperationStatus.Reconciling,
        "verifying" => SyncAuthorityOperationStatus.Verifying,
        "committed" => SyncAuthorityOperationStatus.Committed,
        "cleanup_pending" => SyncAuthorityOperationStatus.CleanupPending,
        "completed" => SyncAuthorityOperationStatus.Completed,
        "blocked" => SyncAuthorityOperationStatus.Blocked,
        "cancelled" => SyncAuthorityOperationStatus.Cancelled,
        "superseded" => SyncAuthorityOperationStatus.Superseded,
        _ => throw new InvalidDataException("The retained authority operation status is not supported.")
    };

    public static string AttemptStatus(SyncAuthorityAttemptStatus value) => value switch
    {
        SyncAuthorityAttemptStatus.Started => "started",
        SyncAuthorityAttemptStatus.RequestSent => "request_sent",
        SyncAuthorityAttemptStatus.ResponseReceived => "response_received",
        SyncAuthorityAttemptStatus.EffectUnknown => "effect_unknown",
        SyncAuthorityAttemptStatus.VerifiedSuccess => "verified_success",
        SyncAuthorityAttemptStatus.VerifiedNotApplied => "verified_not_applied",
        SyncAuthorityAttemptStatus.Blocked => "blocked",
        SyncAuthorityAttemptStatus.Abandoned => "abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static SyncAuthorityAttemptStatus AttemptStatus(string value) => value switch
    {
        "started" => SyncAuthorityAttemptStatus.Started,
        "request_sent" => SyncAuthorityAttemptStatus.RequestSent,
        "response_received" => SyncAuthorityAttemptStatus.ResponseReceived,
        "effect_unknown" => SyncAuthorityAttemptStatus.EffectUnknown,
        "verified_success" => SyncAuthorityAttemptStatus.VerifiedSuccess,
        "verified_not_applied" => SyncAuthorityAttemptStatus.VerifiedNotApplied,
        "blocked" => SyncAuthorityAttemptStatus.Blocked,
        "abandoned" => SyncAuthorityAttemptStatus.Abandoned,
        _ => throw new InvalidDataException("The retained authority attempt status is not supported.")
    };

    public static string ReceiptCertainty(SyncAuthorityReceiptCertainty value) => value switch
    {
        SyncAuthorityReceiptCertainty.KnownSuccess => "known_success",
        SyncAuthorityReceiptCertainty.KnownNotApplied => "known_not_applied",
        SyncAuthorityReceiptCertainty.Unknown => "unknown",
        SyncAuthorityReceiptCertainty.KnownConflict => "known_conflict",
        SyncAuthorityReceiptCertainty.KnownPartial => "known_partial",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string AllocationKind(SyncAuthorityAllocationKind value) => value switch
    {
        SyncAuthorityAllocationKind.TransactionDirectory => "transaction_directory",
        SyncAuthorityAllocationKind.StageFile => "stage_file",
        SyncAuthorityAllocationKind.LocalRecoveryFile => "local_recovery_file",
        SyncAuthorityAllocationKind.RemoteRecoveryFile => "remote_recovery_file",
        SyncAuthorityAllocationKind.UploadSession => "upload_session",
        SyncAuthorityAllocationKind.ControlNamespace => "control_namespace",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static SyncAuthorityAllocationKind AllocationKind(string value) => value switch
    {
        "transaction_directory" => SyncAuthorityAllocationKind.TransactionDirectory,
        "stage_file" => SyncAuthorityAllocationKind.StageFile,
        "local_recovery_file" => SyncAuthorityAllocationKind.LocalRecoveryFile,
        "remote_recovery_file" => SyncAuthorityAllocationKind.RemoteRecoveryFile,
        "upload_session" => SyncAuthorityAllocationKind.UploadSession,
        "control_namespace" => SyncAuthorityAllocationKind.ControlNamespace,
        _ => throw new InvalidDataException("The retained resource-allocation kind is not supported.")
    };

    public static string AllocationStatus(SyncAuthorityAllocationStatus value) => value switch
    {
        SyncAuthorityAllocationStatus.Authorized => "authorized",
        SyncAuthorityAllocationStatus.Creating => "creating",
        SyncAuthorityAllocationStatus.Created => "created",
        SyncAuthorityAllocationStatus.Bound => "bound",
        SyncAuthorityAllocationStatus.Verified => "verified",
        SyncAuthorityAllocationStatus.Released => "released",
        SyncAuthorityAllocationStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
