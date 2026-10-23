using System.Text.Json;

namespace Massdex.SyncShark.V2;

// Optional per-path metadata uses the existing detail column; old plain-text rows remain readable.
internal static class NoticeDetailCodec
{
    internal const string Prefix = "sync-shark-notice-v1:";
    private sealed record Payload(string Detail, FreeSpaceNoticeMetadata? FreeSpace,
        RetainedNoticeVersion? RetainedVersion = null, QuarantineNotice? Quarantine = null, DefenderHistoryNotice? DefenderHistory = null, ConflictNoticeVersions? ConflictVersions = null, ConflictNoticeOperation? ConflictOperation = null, IReadOnlyList<Side>? RemovalSides = null, string? RemovalOriginReason = null, NoticeRepair? Repair = null, bool FolderInspection = false, bool? UploadSuspended = null, Side? UploadQuotaSide = null, long? UploadQuotaNeedBytes = null,
        NoticeRebuildableRule? RebuildableRule = null, NoticeExportInfo? ExportInfo = null, StorageNotice? Storage = null, string? FolderRecoveryAnchor = null, string? FolderDeleteEpisode = null,
        FailureRetryOwner.State? FailureRetry = null, string? CauseKey = null, NoticePlanBound? PlanBound = null, FailureRetryOwner.State? DeliveryRetry = null);

    internal static string Encode(Notice notice) => notice.FreeSpace is null && notice.RetainedVersion is null && notice.Quarantine is null && notice.DefenderHistory is null && notice.ConflictVersions is null && notice.ConflictOperation is null && notice.RemovalSides is null && notice.RemovalOriginReason is null && notice.Repair is null && !notice.FolderInspection && notice.UploadSuspended is null && notice.RebuildableRule is null && notice.ExportInfo is null && notice.Storage is null && notice.FolderRecoveryAnchor is null && notice.FolderDeleteEpisode is null && notice.FailureRetry is null && notice.CauseKey is null && notice.PlanBound is null && notice.DeliveryRetry is null ? notice.Detail :
        Prefix + JsonSerializer.Serialize(new Payload(notice.Detail, notice.FreeSpace, notice.RetainedVersion, notice.Quarantine, notice.DefenderHistory, notice.ConflictVersions, notice.ConflictOperation, notice.RemovalSides, notice.RemovalOriginReason, notice.Repair, notice.FolderInspection, notice.UploadSuspended, notice.UploadQuotaSide, notice.UploadQuotaNeedBytes, notice.RebuildableRule, notice.ExportInfo, notice.Storage, notice.FolderRecoveryAnchor, notice.FolderDeleteEpisode, notice.FailureRetry, notice.CauseKey, notice.PlanBound, notice.DeliveryRetry));

    internal static Notice Decode(Notice notice)
    {
        if (!notice.Detail.StartsWith(Prefix, StringComparison.Ordinal)) return notice;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(notice.Detail[Prefix.Length..]);
            return payload?.Detail is not null && (payload.FreeSpace is not null || payload.RetainedVersion is not null || payload.Quarantine is not null || payload.DefenderHistory is not null || payload.ConflictVersions is not null || payload.ConflictOperation is not null || payload.RemovalSides is not null || payload.RemovalOriginReason is not null || payload.Repair is not null || payload.FolderInspection || payload.UploadSuspended is not null || payload.RebuildableRule is not null || payload.ExportInfo is not null || payload.Storage is not null || payload.FolderRecoveryAnchor is not null || payload.FolderDeleteEpisode is not null || payload.FailureRetry is not null || payload.CauseKey is not null || payload.PlanBound is not null || payload.DeliveryRetry is not null)
                ? notice with { Detail = payload.Detail, FreeSpace = payload.FreeSpace, RetainedVersion = payload.RetainedVersion, Quarantine = payload.Quarantine, DefenderHistory = payload.DefenderHistory, ConflictVersions = payload.ConflictVersions, ConflictOperation = payload.ConflictOperation, RemovalSides = payload.RemovalSides, RemovalOriginReason = payload.RemovalOriginReason, Repair = payload.Repair, FolderInspection = payload.FolderInspection, UploadSuspended = payload.UploadSuspended, UploadQuotaSide = payload.UploadQuotaSide, UploadQuotaNeedBytes = payload.UploadQuotaNeedBytes, RebuildableRule = payload.RebuildableRule, ExportInfo = payload.ExportInfo, Storage = payload.Storage, FolderRecoveryAnchor = payload.FolderRecoveryAnchor, FolderDeleteEpisode = payload.FolderDeleteEpisode, FailureRetry = payload.FailureRetry, CauseKey = payload.CauseKey, PlanBound = payload.PlanBound, DeliveryRetry = payload.DeliveryRetry }
                : notice;
        }
        catch (JsonException) { return notice; }
    }
}
