namespace Massdex.SyncShark.Engine;

/// <summary>
/// Identifies a recovery stage allocation before creation; it does not prove ownership.
/// </summary>
public sealed record StageAllocation(
    string OperationId,
    long Ordinal,
    string AllocationId,
    string Path);
