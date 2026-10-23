namespace Massdex.SyncShark.V2;

/// <summary>One bounded Recovery restore result. RESTORE is a preview; RESTORED is a verified effect.</summary>
public sealed record RecoveryRestoreItem(string Id, string Side, string Path, string? Version, long Size,
    DateTimeOffset Created, string Classification, string? Destination = null, string? Detail = null);
