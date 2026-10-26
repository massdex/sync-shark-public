namespace Massdex.SyncShark.V2;

public sealed record NoticeComparison(byte[] A, byte[] B, string? BaseText, string? MergedText, string Note,
    string? SnapshotA = null, string? SnapshotB = null);
