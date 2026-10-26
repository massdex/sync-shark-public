namespace Massdex.SyncShark.V2;

public sealed record StorageOptInResult(bool Enabled, string? BackupPath, string? Warning);
