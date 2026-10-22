namespace Massdex.SyncShark.V2;

/// <summary>Opaque provider evidence persisted in an action's existing intent payload for read-only reconciliation.</summary>
public sealed record ProviderEffectCheckpoint(string Kind, string Id, DateTimeOffset? RecordedAtUtc = null);
