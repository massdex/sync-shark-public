namespace Massdex.SyncShark.Engine;

/// <summary>Admits a prepared selected root action without lending its lease to later follow-ups.</summary>
internal interface ISelectedExecutionBoundary
{
    Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct);
}
