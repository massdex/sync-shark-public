namespace Massdex.SyncShark.Engine;

/// <summary>The session's execution boundaries and Local hash capacity, shared by its sync passes.</summary>
internal sealed record SyncPorts(IRecoveryExecutionBoundary Recovery, ILiveExecutionBoundary Live,
    ISelectedExecutionBoundary Selected, int LocalHashSlots)
{
    internal static bool AdmitsBatch(IStateStore store, string pairId) => store.FirstRepairDone(pairId);
    internal Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? RunBatchAsync { get; init; }
}
