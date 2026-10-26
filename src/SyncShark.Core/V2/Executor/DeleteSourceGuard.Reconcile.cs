namespace Massdex.SyncShark.V2;

internal static partial class DeleteSourceGuard
{
    internal static async Task<ActionResult?> CheckReconcileAsync(SyncAction action, IEndpointWriter source,
        ItemState? historical, bool restorePending, CancellationToken ct, Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        // Already-submitted explicit victims and replacements retain their durable non-absence authority.
        if (ExplicitRemoval(action) || action.Rule is 26 or 32) return null;
        if (!restorePending && historical is not null && !string.IsNullOrWhiteSpace(historical.ObjectId) &&
            source is ISourceAbsenceProbe probe &&
            await probe.IsHistoricalSourcePresentAsync(historical, ct).ConfigureAwait(false) == true)
        {
            var current = await probe.ObserveSourceMetadataAsync(action.Path, ct).ConfigureAwait(false);
            if (current is not null && string.Equals(current.ObjectId, historical.ObjectId, StringComparison.Ordinal))
                return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, EffectResolver.NotApplied)
                {
                    ForgetUnsafeDeleteBaseline = true,
                    NewA = action.Target == Side.B ? current : null,
                    NewB = action.Target == Side.A ? current : null
                };
            // Identity presence at a different path invalidates absence authority. Retain the
            // correspondence for move planning, but retire this observation-only delete attempt.
            return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null,
                ExecutionReasons.DeleteSourceMoved.Detail) { DeleteSourceUnconfirmed = true };
        }
        // A paired move's already-submitted effect is resolved by identity absence, not plan-local admission.
        var result = await CheckEvidenceAsync(action with { Rule = 6 }, source, historical, restorePending, ct, isStateDamage: isStateDamage)
            .ConfigureAwait(false);
        return result is null ? null : result with { Outcome = Outcome.Uncertain,
            DeleteSourceUnconfirmed = false, DeleteSourceIdentityMissing = false,
            RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
    }
}
