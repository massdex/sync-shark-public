namespace Massdex.SyncShark.V2;

/// <summary>A recoverable copy of a displaced version.</summary>
public sealed record RecoveryRef(Side Side, string Location, string? VersionId, string Hash, long Size);
