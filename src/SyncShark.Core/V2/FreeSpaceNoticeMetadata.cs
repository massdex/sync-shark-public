namespace Massdex.SyncShark.V2;

/// <summary>Per-path admission facts, retained for presentation without replacing the retry notice.</summary>
public sealed record FreeSpaceNoticeMetadata(string? VolumeRoot, long RequiredBytes,
    long? FreeBytes = null, long? ReserveBytes = null);
