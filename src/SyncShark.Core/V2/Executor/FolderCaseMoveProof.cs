namespace Massdex.SyncShark.V2;

// A folder has no content hash: a successful Move receipt alone cannot prove spelling convergence.
// Share the fresh two-sided proof between first execution and interrupted-effect reconciliation.
internal static class FolderCaseMoveProof
{
    internal static async Task<ActionResult> VerifyAsync(SyncAction action, ActionResult result,
        IEndpointWriter target, IEndpointWriter source, CancellationToken ct, Func<Exception, bool> isStateDamage)
    {
        if (result.Outcome != Outcome.Done || action.Kind != ActionKind.Move ||
            action.Source is not { Kind: ItemKind.Folder } expectedSource ||
            action.ExpectedTarget is not { Kind: ItemKind.Folder } expectedTarget ||
            action.FromPath is null || !PairPath.Same(action.FromPath, action.Path) ||
            PairPath.SameLeafLocation(action.FromPath, action.Path)) return result;
        try
        {
            var observedSource = await source.ObserveAsync(expectedSource.Path, ct).ConfigureAwait(false);
            var observedTarget = await target.ObserveAsync(action.Path, ct).ConfigureAwait(false);
            var chosen = DropboxNameMapping.MatchPath(expectedSource);
            if (observedSource is { Kind: ItemKind.Folder } && observedTarget is { Kind: ItemKind.Folder } &&
                observedSource.ObjectId == expectedSource.ObjectId && observedTarget.ObjectId == expectedTarget.ObjectId &&
                PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(observedSource), chosen) &&
                PairPath.SameLeafLocation(DropboxNameMapping.MatchPath(observedTarget), chosen))
                return action.Target == Side.A
                    ? result with { NewA =observedTarget, NewB =observedSource }
                    : result with { NewA =observedSource, NewB =observedTarget };
        }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage) == FailureRetryOwner.FailureScope.Item)
        {
            return Pending(result, error.Message);
        }
        return Pending(result, "Both folder identities and the chosen leaf spelling have not been freshly confirmed.");
    }

    private static ActionResult Pending(ActionResult result, string reason) => result with
    {
        Outcome = Outcome.Uncertain, NewA =null, NewB =null, Reason = reason,
        RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds
    };
}
