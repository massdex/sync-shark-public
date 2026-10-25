namespace Massdex.SyncShark.V2;

/// <summary>One structured restore row: identity, side, path, verified classification, destination and action.</summary>
public sealed record SyncCliRecoveryRestoreItemData(string Id, string Side, string Path, string? Version, long Size,
    DateTimeOffset Created, string Classification, string? Destination, string? Detail, string Action);
