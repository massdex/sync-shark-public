namespace Massdex.SyncShark.V2;

/// <summary>Deferred planning rows reconciliation retired in its last batch and across the active run.</summary>
internal readonly record struct IntakeRetirement(long LastBatch, long Total);
