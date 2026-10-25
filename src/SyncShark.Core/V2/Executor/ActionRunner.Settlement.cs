namespace Massdex.SyncShark.V2;

internal sealed partial class ActionRunner
{
    private async Task<ActionResult?> SettleEffectAsync(SyncAction action, ActionResult result,
        RecoveryHolder recoveryHolder, IEndpointWriter target, bool definitelyNotApplied, CancellationToken ct)
    {
        if (result.Recovery is null && recoveryHolder.Reference is { } retained)
            result = result with { Recovery = retained };
        var preservedDestructive = RecoverySafety.HasPreservedFileVictim(action) &&
            (recoveryHolder.Reference is not null || recoveryHolder.CheckpointRecorded);
        if (preservedDestructive && result.Outcome is Outcome.Skipped or Outcome.Refused &&
            !(definitelyNotApplied && await HistoryTargetIntactAsync(action, result.Recovery, target, ct).ConfigureAwait(false)))
            result = result with
            {
                Outcome = Outcome.Uncertain,
                Recovery = result.Recovery ?? recoveryHolder.Reference,
                Reason = "The displaced original is retained; this effect did not complete. " +
                    "Reconcile will prove the target state; restore from Recovery if needed. " + result.Reason,
                RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds,
                ProviderNameRefused = false
            };
        return await CommitAsync(action, result).ConfigureAwait(false);
    }

    // A history receipt does not move bytes. Only a definite refusal plus a fresh exact-version
    // observation can retain its non-applied outcome; local/displaced or unprovable victims stay protected.
    private async Task<bool> HistoryTargetIntactAsync(SyncAction action, RecoveryRef? receipt,
        IEndpointWriter target, CancellationToken ct)
    {
        if (!_committer.TargetUsesDropboxHistory(action) ||
            action.ExpectedTarget is not { Kind: ItemKind.File, Hash: not null, VersionId: not null } expected ||
            receipt is null || receipt.Side != action.Target || !PairPath.Same(receipt.Location, expected.Path) ||
            receipt.VersionId != expected.VersionId || receipt.Size != expected.Size ||
            Content.SameHash(receipt.Hash, expected.Hash) != true || !PairPath.Same(action.Path, expected.Path))
            return false;
        try
        {
            var observed = await target.ObserveAsync(action.Path, ct).ConfigureAwait(false);
            return observed is not null && PairPath.Same(observed.Path, expected.Path) &&
                observed.ObjectId == expected.ObjectId && observed.VersionId == expected.VersionId &&
                Content.SameContent(observed, expected) == true;
        }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, _isStateDamage) is
            FailureRetryOwner.FailureScope.Item or FailureRetryOwner.FailureScope.Endpoint)
        { return false; }
    }
}
