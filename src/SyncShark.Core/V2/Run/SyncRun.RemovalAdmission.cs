using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Direct notice/recovery/conflict entry points share the same admission as run execution.
    // Nonexpanding deletes retain their per-item conditional effect proof. A RemovalReview's
    // frozen complete observation is checked by DeleteSourceGuard before these row batches.
    private async IAsyncEnumerable<ActionResult> ApplyAdmittedAsync(PairDef pair, Plan plan,
        IEndpointWriter a, IEndpointWriter b, [EnumeratorCancellation] CancellationToken ct)
    {
        var admitted = await AdmitReviewedRemovalsAsync(plan, pair, ct).ConfigureAwait(false);
        foreach (var held in admitted.SkippedActions.Except(plan.SkippedActions))
            yield return SafetySkipped(held);
        await foreach (var result in _executor.ApplyAsync(admitted, a, b, ct).WithCancellation(ct))
            yield return result;
    }
}
