namespace Massdex.SyncShark.V2;

internal static partial class EffectResolver
{
    private static async Task<ActionResult> VerifiedMoveDoneAsync(SyncAction action, ItemState? moved,
        IEndpointWriter target, IEndpointWriter source, CancellationToken ct, RecoveryRef? preserved,
        Func<Exception, bool> isStateDamage)
    {
        var result = await DoneAsync(action, moved, target, ct, preserved).ConfigureAwait(false);
        return await FolderCaseMoveProof.VerifyAsync(action, result, target, source, ct, isStateDamage).ConfigureAwait(false);
    }

    private static async Task<ActionResult> ResolveVerifiedMoveAsync(SyncAction action, ItemState? observed,
        IEndpointWriter target, IEndpointWriter source, CancellationToken ct, RecoveryRef? preserved,
        Func<Exception, bool> isStateDamage)
    {
        var result = await ResolveMoveAsync(action, observed, target, ct, preserved, isStateDamage).ConfigureAwait(false);
        return await FolderCaseMoveProof.VerifyAsync(action, result, target, source, ct, isStateDamage).ConfigureAwait(false);
    }
}
