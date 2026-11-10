namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Refuses live or selected execution in fixtures that exercise only ordinary or recovery sync.</summary>
internal sealed class RefusingExecutionBoundary : ILiveExecutionBoundary, ISelectedExecutionBoundary,
    IRecoveryExecutionBoundary
{
    internal static readonly RefusingExecutionBoundary Instance = new();

    public bool TryHoldGeneration(string pairId) => throw new NotSupportedException();

    public void RequireCurrent() => throw new NotSupportedException();

    public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<T> ExecuteAsync<T>(IReadOnlyList<LiveExecutionRequest> requests,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<LiveCoverage> CaptureCoverageAsync(WorkSelection selection,
        IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct) => throw new NotSupportedException();

    public Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        throw new NotSupportedException();
}
