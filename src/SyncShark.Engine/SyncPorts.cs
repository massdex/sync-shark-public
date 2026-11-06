namespace Massdex.SyncShark.Engine;

/// <summary>The session's execution boundaries and Local hash capacity, shared by its sync passes.</summary>
internal sealed record SyncPorts(IRecoveryExecutionBoundary Recovery, ILiveExecutionBoundary Live,
    ISelectedExecutionBoundary Selected, int LocalHashSlots);
