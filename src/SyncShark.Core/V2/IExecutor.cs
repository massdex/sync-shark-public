namespace Massdex.SyncShark.V2;

/// <summary>Applies and reconciles plans against both endpoints.</summary>
public interface IExecutor
{
    IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b, CancellationToken ct);

    /// <summary>
    /// Resolves recorded intents by observation, committing each outcome against <paramref name="gen"/>. Intents are
    /// independent, so one implementation may observe several at once; <paramref name="progress"/> reports the number
    /// whose observation and durable commit have completed (never a forecast).
    /// </summary>
    Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen, IEndpointWriter a,
        IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null);
}
