namespace Massdex.SyncShark.V2;

internal static partial class EffectResolver
{
    private static ActionResult? ResolveWithoutObservation(SyncAction action) =>
        ResultEvidence.CommitOnly(action) ? Skipped(action, NotApplied) :
        action.ConflictRestore ? Skipped(action, RestoreAbandoned) : null;

    private static async Task<ActionResult> ResolveAbsentDeleteAsync(SyncAction action, IEndpointWriter target,
        IEndpointWriter source, ItemState? historical, RecoveryRef? preserved, CancellationToken ct, bool restorePending, Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        try
        {
            var blocked = await DeleteSourceGuard.CheckReconcileAsync(action, source, historical, restorePending, ct, isStateDamage: isStateDamage)
                .ConfigureAwait(false);
            return blocked ?? await DoneAsync(action, null, target, ct, preserved).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage: isStateDamage) == FailureRetryOwner.FailureScope.Item)
        {
            return Unverified(action, "Interrupted deletion source check: " + Describe(error)) with
                { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
        }
    }
}
