using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

/// <summary>A test executor that yields a fixed set of results in order for the plan the run pushes through it.</summary>
internal sealed class ScriptedExecutor : IExecutor
{
    /// <summary>The results to report, in order; each names the plan action id it belongs to.</summary>
    public IReadOnlyList<ActionResult> Results { get; set; } = [];

    public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var result in Results) yield return result;
    }

    public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
        IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
        Task.FromResult<IReadOnlyList<ActionResult>>([]);
}
