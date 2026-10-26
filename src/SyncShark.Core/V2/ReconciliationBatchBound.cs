namespace Massdex.SyncShark.V2;

/// <summary>Path and byte limits for one reconciliation admission lease.</summary>
public readonly record struct ReconciliationBatchBound(int MaxPaths, long MaxBytes);
