namespace Massdex.SyncShark.Engine;

/// <summary>The live execution boundary port (LIVE 2): Run supplies sessions, root checks and selected coverage
/// behind it, so a caller passes only each request's finite selection and complete effect footprint.</summary>
internal interface ILiveExecutionBoundary
{
    bool TryHoldGeneration(string pairId);

    /// <summary>Admits the requests and invokes exactly one effect callback under the lease, returning its value.</summary>
    Task<T> ExecuteAsync<T>(IReadOnlyList<LiveExecutionRequest> requests,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute,
        CancellationToken ct);

    /// <summary>Acquires the real source owners' committed coverage for this selection's footprint.</summary>
    Task<LiveCoverage> CaptureCoverageAsync(WorkSelection selection,
        IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct);
}
