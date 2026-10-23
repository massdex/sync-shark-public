namespace Massdex.SyncShark.V2;

/// <summary>Refreshes missing source evidence after a confirmed effect; never repeats the effect.</summary>
internal static class ResultVerification
{
    internal static async Task<ActionResult> ResolveAsync(SyncAction action, IEndpointWriter target,
        IEndpointWriter source, CancellationToken ct, Func<Exception, bool> isStateDamage,
        TimeSpan? unknownCommitGracePeriod, ProviderJobCheckCache? jobChecks, string? materializationBlock,
        ItemState? historicalDeleteSource, RecoveryRef? preserved, bool deleteRestorePending,
        StoreCommitter? providerCommitter)
    {
        var previousProviderContext = ProviderEffectContext.Current.Value;
        ActionResult result;
        try
        {
            if (providerCommitter is not null)
                ProviderEffectContext.Current.Value = providerCommitter.CreateProviderContext(action);
            result = await EffectResolver.ResolveAsync(action, target, source, ct, isStateDamage,
                unknownCommitGracePeriod, jobChecks: jobChecks, materializationBlock: materializationBlock,
                historicalDeleteSource: historicalDeleteSource, preserved: preserved,
                deleteRestorePending: deleteRestorePending).ConfigureAwait(false);
        }
        finally { ProviderEffectContext.Current.Value = previousProviderContext; }
        return await NormalizeAsync(action, result, source, ct, isStateDamage).ConfigureAwait(false);
    }

    internal static async Task<ActionResult> NormalizeAsync(SyncAction action, ActionResult result,
        IEndpointWriter source, CancellationToken ct, Func<Exception, bool> isStateDamage)
    {
        // Move observation proves the target identity against ExpectedTarget, not the opposite source.
        // A frozen unhashed Source must be refreshable, or every later reconciliation fails identically.
        if (result.Outcome == Outcome.Done && action.Kind == ActionKind.Move &&
            action.Source is { Kind: ItemKind.File, Hash: null } original)
        {
            try
            {
                var current = await source.ObserveAsync(original.Path, ct).ConfigureAwait(false);
                if (current is null || current.ObjectId != original.ObjectId ||
                    Content.SameVersion(current, original) == false)
                    return result with { Outcome = Outcome.Skipped, Reason = EffectReasons.Superseded };
                if (current.Hash is not null && Content.SameVersion(current, original) == true)
                    result = action.Target == Side.A ? result with { NewB = current } : result with { NewA = current };
            }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage) == FailureRetryOwner.FailureScope.Item)
            {
                // The original result retains the receipt and becomes ordinary durable retry debt below.
            }
        }
        return ResultEvidence.Normalize(action, result);
    }
}
