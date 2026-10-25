using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class DropboxWriter
{
    private const int CaseRecoveryCandidates = 8;

    // Recovery is an ordinary create-only move, never replacement. Record each chosen location before
    // sending it so an acknowledged move followed by a crash resumes by identity, not by replaying it.
    private async Task<bool> RetainCaseConflictAsync(SyncAction action, CaseRenameCheckpoint checkpoint,
        ItemState current, CancellationToken ct)
    {
        var context = CaseContext();
        checkpoint = checkpoint with { Step = 3, Retained = checkpoint.Retained ?? checkpoint.From };
        await RecordCaseRetentionAsync(context, checkpoint, current).ConfigureAwait(false);
        var candidates = 0;
        while (DropboxPilotClient.SameMoveSpelling(current.Path, checkpoint.Temporary))
        {
            ct.ThrowIfCancellationRequested();
            if (++candidates > CaseRecoveryCandidates)
                throw new UncertainEffectException(ExecutionReasons.ProviderUnknown.Detail);
            var retained = await RestoreCaseLocationAsync(current, checkpoint.Retained!, ct).ConfigureAwait(false);
            if (retained is not null) { current = retained; break; }
            checkpoint = checkpoint with { Retained = VisibleCasePath(action, checkpoint) };
            await RecordCaseRetentionAsync(context, checkpoint, current).ConfigureAwait(false);
        }
        var occupant = await ObserveAsync(checkpoint.To, ct).ConfigureAwait(false);
        await context.RecordAsync(CaseRenameCheckpointKind, JsonSerializer.Serialize(checkpoint), current).ConfigureAwait(false);
        if (occupant is not null && occupant.ObjectId != current.ObjectId)
            throw new ProviderEffectBlockedException($"The rename destination '{checkpoint.To}' is occupied; the incoming item is retained at '{current.Path}'. Retry after freeing the destination; Ignore silences this cause without abandoning the rename.");
        if (!DropboxPilotClient.SameMoveSpelling(current.Path, checkpoint.To))
        {
            if (PairPath.Same(current.Path, checkpoint.To))
                await MoveCaseAsync(action with { ProviderEffect = null, ExpectedTarget = current }, ct).ConfigureAwait(false);
            else
                await MoveCaseLegAsync(DropboxItems.ToEntry(current), checkpoint.To, false, ct).ConfigureAwait(false);
        }
        return true;
    }

    private static Task RecordCaseRetentionAsync(ProviderEffectContext context, CaseRenameCheckpoint checkpoint, ItemState current) =>
        context.RecordAsync(CaseRenameCheckpointKind, JsonSerializer.Serialize(checkpoint),
            current with { Path = checkpoint.Retained! });

    private async Task<ItemState?> RestoreCaseLocationAsync(ItemState current, string destination, CancellationToken ct)
    {
        var occupant = await ObserveAsync(destination, ct).ConfigureAwait(false);
        if (occupant is not null && occupant.ObjectId != current.ObjectId) return null;
        try
        {
            return DropboxItems.ToItem(await MoveCaseLegAsync(DropboxItems.ToEntry(current), destination,
                false, ct).ConfigureAwait(false));
        }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage: _isStateDamage) == FailureRetryOwner.FailureScope.Item)
        {
            // A lost response may already have exposed the item. Never move an identity the user moved.
            var located = await _client.FindIdentityForMoveAsync(current.ObjectId, ct).ConfigureAwait(false);
            if (located.OutsideRoot || located.Entry is null) throw;
            var observed = DropboxItems.ToItem(located.Entry);
            if (!DropboxPilotClient.SameMoveSpelling(observed.Path, current.Path)) return observed;
            occupant = await ObserveAsync(destination, ct).ConfigureAwait(false);
            if (occupant is not null && occupant.ObjectId != current.ObjectId) return null;
            throw;
        }
    }

    private static string VisibleCasePath(SyncAction action, CaseRenameCheckpoint checkpoint)
    {
        var parent = checkpoint.From[..(checkpoint.From.LastIndexOf('/') + 1)];
        return parent + "Case " + ShortId.Hex16(action.ActionId, action.ExpectedTarget!.ObjectId, checkpoint.Retained!) +
            Path.GetExtension(checkpoint.From);
    }

}
