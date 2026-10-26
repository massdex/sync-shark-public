namespace Massdex.SyncShark.V2;

/// <summary>A durable index row of one Recovery copy (storage.md F-S2); the Recovery owner restores or expires it.</summary>
public sealed record RecoveryRecord(string RecoveryId, string PairId, string ActionId, Side Side, string Path,
    string Location, string? VersionId, string Hash, long Size, DateTimeOffset Created, DateTimeOffset Expires);
