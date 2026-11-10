using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Executor;

// One Local submission lifetime: each effect, its receipt check and the clean reports wait for one durable Terminal.
/// <summary>Performs a Local folder submission's effects through the shared action owner and checks each recycled
/// receipt. The folder runner keeps frozen-member classification, the late entry's record and notice, and the deleted
/// event; this owner only calls them.</summary>
internal sealed class LocalFolderEffects(ExecutorContext _context, ActionExecutor _actions,
    Func<string, FolderRemovalState> _read, Func<FolderRemovalState, Listed, bool> _frozen,
    Func<FolderRemovalState, Listed, CancellationToken, Task> _late, Action<string> _deleted)
{
    /// <summary>Runs every member, makes the submission Terminal with their results, then reports the clean folders.</summary>
    internal async Task RemoveAsync(FolderSubmission submission, IReadOnlyList<FolderRemovalEffect> members,
        CancellationToken ct)
    {
        var results = new List<FolderMemberResult>(members.Count);
        var clean = new List<string>();
        foreach (var effect in members) results.Add(await RemoveAsync(effect, results.Count, clean, ct));
        _context.Store.AdvanceFolderSubmission(submission, submission with
        {
            Generation = checked(submission.Generation + 1),
            Phase = FolderSubmission.SubmissionPhase.Terminal
        }, results);
        // The deleted event follows the durable receipt, so no interruption can follow a reported deletion.
        foreach (var path in clean) _deleted(path);
    }

    private async Task<FolderMemberResult> RemoveAsync(FolderRemovalEffect effect, int ordinal, List<string> clean,
        CancellationToken ct)
    {
        var expected = effect.Expected!;
        try
        {
            RemovalSelection? selection;
            try { selection = await SelectionAsync(effect, ct); }
            catch (ItemFailedException error)
            {
                return new FolderMemberResult.KnownNoEffect(ordinal, error.Cause);
            }
            var result = await _actions.RemoveAsync(Side.Local, expected, selection, StateChange.Empty, ct,
                ownedFolderEffect: true);
            // shortcut: only the test seam reaches this; production folder recycles always carry a
            // receipt (LocalBin.cs Classify). Upgrade: give SendOverride a receipt.
            if (result.Outcome == RemovalOutcome.Removed && expected.IsFolder) clean.Add(expected.Path);
            return result.Outcome switch
            {
                RemovalOutcome.Removed => new FolderMemberResult.Receipt(ordinal, expected),
                RemovalOutcome.NoEffect => new FolderMemberResult.KnownNoEffect(ordinal, result.Cause),
                _ => new FolderMemberResult.Unknown(ordinal, result.Cause)
            };
        }
        catch (ChangedUnderneathException)
        {
            return new FolderMemberResult.KnownNoEffect(ordinal, TextKey.FailChanged);
        }
        catch (ItemFailedException error)
        {
            return new FolderMemberResult.Unknown(ordinal, error.Cause);
        }
    }

    // The shell recycles whatever the folder holds when it acts. An entry outside the frozen
    // inventory was added or changed after the re-listing.
    private async Task<FolderMemberResult> RecycledAsync(FolderRemovalEffect effect, int ordinal,
        IAsyncEnumerable<Listed> recycled, List<string> clean, CancellationToken ct)
    {
        var operation = _read(effect.OperationId);
        var late = false;
        try
        {
            await foreach (var listed in recycled.WithCancellation(ct))
            {
                if (_frozen(operation, listed)) continue;
                late = true;
                await _late(operation, listed, ct);
            }
        }
        catch (Exception error) when (FolderDeleteRunner.IsProviderFailure(error))
        {
            // Every inspection failure has one honest cause: the receipt could not be checked.
            return new FolderMemberResult.Unknown(ordinal, TextKey.RecycleUnchecked);
        }
        if (!late) clean.Add(effect.Expected!.Path);
        return new FolderMemberResult.Receipt(ordinal, effect.Expected!);
    }

    private async Task<RemovalSelection?> SelectionAsync(FolderRemovalEffect effect, CancellationToken ct)
    {
        if (effect.Kind != FolderEffectKind.ConditionalFile) return null;
        var expected = effect.Expected!;
        var item = _context.Store.FolderRemovalItems(_context.Pair.PairId, effect.OperationId,
            checked(effect.ItemOrdinal - 1), StateSchema.OneRow).Single();
        return await _actions.SelectRemovalAsync(Row.ForOneSide(Side.Local, PathNames.Key(expected.Path), expected, item.Saved,
            [], null, _read(effect.OperationId).RootContext), Side.Local, expected, ct);
    }
}
