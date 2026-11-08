namespace Massdex.SyncShark.Engine;

/// <summary>The exact operation and optional member versions selected for a recovery choice.</summary>
public sealed record RecoveryTarget(string OperationId, long OperationGeneration, long? Item, long? ItemGeneration);
