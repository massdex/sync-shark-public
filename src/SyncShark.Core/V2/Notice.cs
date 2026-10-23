namespace Massdex.SyncShark.V2;

/// <summary>
/// One user-facing notice for a decision or conflict. Detail is display text only; ReasonKey is the stable reason
/// code that code matches on (with Rule). NoticeId is assigned by the store.
/// </summary>
public sealed record Notice(string PairId, int Rule, NoticeKind Kind, string Path, string Detail, string ReasonKey,
    string? NoticeId = null, FreeSpaceNoticeMetadata? FreeSpace = null,
    RetainedNoticeVersion? RetainedVersion = null, QuarantineNotice? Quarantine = null, DefenderHistoryNotice? DefenderHistory = null, ConflictNoticeVersions? ConflictVersions = null, ConflictNoticeOperation? ConflictOperation = null, IReadOnlyList<Side>? RemovalSides = null, string? RemovalOriginReason = null, NoticeRepair? Repair = null, bool FolderInspection = false, bool? UploadSuspended = null, Side? UploadQuotaSide = null, long? UploadQuotaNeedBytes = null,
    NoticeRebuildableRule? RebuildableRule = null, NoticeExportInfo? ExportInfo = null, StorageNotice? Storage = null, string? FolderRecoveryAnchor = null, string? FolderDeleteEpisode = null,
    FailureRetryOwner.State? FailureRetry = null, string? CauseKey = null,
    FailureRetryOwner.State? DeliveryRetry = null)
{
    public NoticePlanBound? PlanBound { get; init; }
    /// <summary>Stable suppression identity, independent of diagnostic cause text and attempt metadata.</summary>
    public string? CauseIdentity { get; init; }
}
