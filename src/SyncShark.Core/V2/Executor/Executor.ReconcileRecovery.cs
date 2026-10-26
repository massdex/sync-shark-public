namespace Massdex.SyncShark.V2;

public sealed partial class Executor
{
    // Resolves one intent's effect after reading its durable archive receipt. Resolving from the receipt is read-only,
    // so an original that reappeared after the crash is never archived or removed. A receipt that cannot be read is
    // not a missing receipt: the outcome is unknown, so the intent stays unresolved (never replayed) and one failed
    // lookup never aborts the rest of the reconcile.
    private async Task<(ActionResult Result, SyncAction Intent)> ResolveReconciledAsync(SyncAction intent,
        IEndpointWriter target, IEndpointWriter source, string? pairId, long gen, bool resumesProviderEffect,
        ProviderJobCheckCache jobChecks, string? materializationBlock, ItemState? historicalDeleteSource,
        bool deleteRestorePending, CancellationToken ct)
    {
        RecoveryRef? preserved = null;
        if (pairId is not null && intent.RecoveryFirst)
        {
            try
            {
                await StoreAsync(() => preserved = RecoverySafety.PreservedReceipt(_store, pairId, intent), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                FailureRetryOwner.ScopeOf(error, ct, _isStateDamage) == FailureRetryOwner.FailureScope.Item)
            {
                var uncertain = new ActionResult(intent.ActionId, Outcome.Uncertain, null, null, null,
                    string.Format(ExecutionReasons.RecoveryUnverified.Detail, error.Message));
                return (await ResultVerification.NormalizeAsync(intent, uncertain, source, ct, _isStateDamage)
                    .ConfigureAwait(false), intent);
            }
        }
        var providerCommitter = pairId is not null && resumesProviderEffect
            ? new StoreCommitter(_store, _storeGate, pairId, gen) : null;
        var result = await ResultVerification.ResolveAsync(intent, target, source, ct, _isStateDamage,
            _settings.UnknownCommitGracePeriod, jobChecks, materializationBlock, historicalDeleteSource,
            preserved, deleteRestorePending, providerCommitter).ConfigureAwait(false);
        if (providerCommitter is not null) intent = providerCommitter.LatestAction(intent);
        return (result, intent);
    }
}
