using System.Text.Json;

namespace Massdex.SyncShark.V2;

// Optional per-path metadata uses the existing detail column; old plain-text rows remain readable.
internal static class NoticeDetailCodec
{
    internal const string Prefix = "sync-shark-notice-v1:";
    private sealed record Payload(string Detail, FreeSpaceNoticeMetadata? FreeSpace,
        RetainedNoticeVersion? RetainedVersion = null, NoticeRepair? Repair = null, bool FolderInspection = false, bool? UploadSuspended = null, Side? UploadQuotaSide = null, long? UploadQuotaNeedBytes = null,
        NoticeRebuildableRule? RebuildableRule = null, NoticeExportInfo? ExportInfo = null);

    internal static string Encode(Notice notice) => notice.FreeSpace is null && notice.RetainedVersion is null && notice.Repair is null && !notice.FolderInspection && notice.UploadSuspended is null && notice.RebuildableRule is null && notice.ExportInfo is null ? notice.Detail :
        Prefix + JsonSerializer.Serialize(new Payload(notice.Detail, notice.FreeSpace, notice.RetainedVersion, notice.Repair, notice.FolderInspection, notice.UploadSuspended, notice.UploadQuotaSide, notice.UploadQuotaNeedBytes, notice.RebuildableRule, notice.ExportInfo));

    internal static Notice Decode(Notice notice)
    {
        if (!notice.Detail.StartsWith(Prefix, StringComparison.Ordinal)) return notice;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(notice.Detail[Prefix.Length..]);
            return payload?.Detail is not null && (payload.FreeSpace is not null || payload.RetainedVersion is not null || payload.Repair is not null || payload.FolderInspection || payload.UploadSuspended is not null || payload.RebuildableRule is not null || payload.ExportInfo is not null)
                ? notice with { Detail = payload.Detail, FreeSpace = payload.FreeSpace, RetainedVersion = payload.RetainedVersion, Repair = payload.Repair, FolderInspection = payload.FolderInspection, UploadSuspended = payload.UploadSuspended, UploadQuotaSide = payload.UploadQuotaSide, UploadQuotaNeedBytes = payload.UploadQuotaNeedBytes, RebuildableRule = payload.RebuildableRule, ExportInfo = payload.ExportInfo }
                : notice;
        }
        catch (JsonException) { return notice; }
    }
}
