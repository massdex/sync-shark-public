namespace Massdex.SyncShark.V2;

/// <summary>One retained Recovery row on the bounded page surface, with its current-state hint.</summary>
public sealed record SyncCliRecoveryPageItemData(string Id, string Side, string Path, string? Version, long Size,
    DateTimeOffset Created, string Classification, string? Destination, string? Detail);
