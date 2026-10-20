namespace Massdex.SyncShark.V2;

/// <summary>Fresh identity AND path absence evidence; null means absence could not be proved.</summary>
public interface ISourceAbsenceProbe
{
    /// <summary>True only when the historical identity (if supplied) and exact path are both absent.</summary>
    Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct);

    /// <summary>Fresh metadata-only source observation for a planned replacement.</summary>
    Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct);

    /// <summary>True only when the same historical object still exists; null means unknown.</summary>
    Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) => Task.FromResult<bool?>(null);
}

/// <summary>Checks the missing side before any absence-propagating delete or Recovery move.</summary>
internal static class DeleteSourceGuard
{
    internal const string ReasonKey = "delete-source-unconfirmed";
    internal static string NoticeId(string pairId, string path) => ShortId.Make("n", pairId, "0", PathKey.Of(path), ReasonKey);

    // Replacement/winner deletes are prerequisites to a copy, not propagation of an absent source.
    internal static bool Applies(SyncAction action) => action.Kind == ActionKind.Delete;

    internal static IReadOnlyDictionary<(Side, int, string), ItemState> Replacements(IReadOnlyList<SyncAction> actions) =>
        actions.Where(next => next.Rule is 26 or 32 && next.Source is not null &&
            next.Kind is ActionKind.CopyNew or ActionKind.MkDir)
            .GroupBy(next => (next.Target, next.Rule, PathKey.Of(next.Path)))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Source!);

    internal static ItemState? Replacement(SyncAction action, IReadOnlyDictionary<(Side, int, string), ItemState> replacements)
    {
        if (action.Kind != ActionKind.Delete || action.Rule is not (26 or 32)) return null;
        for (var path = PathKey.Of(action.Path); path.Length > 0;)
        {
            if (replacements.TryGetValue((action.Target, action.Rule, path), out var source)) return source;
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
        return null;
    }

    internal static async Task<ActionResult?> CheckAsync(SyncAction action, IEndpointWriter source,
        StoreCommitter committer, string pairId, CancellationToken ct, ItemState? replacement = null)
    {
        if (!Applies(action)) return null;
        string? detail;
        try
        {
            if (action.Rule is 26 or 32)
            {
                var current = replacement is not null && source is ISourceAbsenceProbe metadata
                    ? await metadata.ObserveSourceMetadataAsync(replacement.Path, ct).ConfigureAwait(false) : null;
                if (current is not null && replacement is not null && current.Kind == replacement.Kind &&
                    string.Equals(current.ObjectId, replacement.ObjectId, StringComparison.Ordinal)) return null;
                detail = "The expected replacement source could not be verified with its planned identity and kind. Nothing was deleted; Retry checks it again next run.";
            }
            else if (committer.DeleteRestorePending(action))
            {
                detail = "This item's missing copy is queued for restoration after an interrupted delete. Nothing was deleted; Retry restores the missing copy instead.";
            }
            else
            {
                var historical = committer.DeleteSource(action);
                var absent = source is ISourceAbsenceProbe probe
                    ? await probe.IsSourceAbsentAsync(historical, action.Path, ct).ConfigureAwait(false)
                    : null;
                if (absent == true) return null;
                detail = absent == false
                    ? "Deletion was expected, but the source item still exists. Nothing was deleted; the item will be checked and replanned next run."
                    : "Deletion was expected, but the source item's absence could not be verified. Nothing was deleted; Retry checks it again next run.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            detail = "Deletion was expected, but checking the source failed. Nothing was deleted; Retry checks it again next run. " + error.Message;
        }
        detail = $"Source '{action.Path}' on side {(action.Target == Side.A ? Side.B : Side.A)}: " + detail;
        await committer.AddNoticeAsync(new Notice(pairId, 0, NoticeKind.Info, action.Path,
            detail, ReasonKey, NoticeId(pairId, action.Path))).ConfigureAwait(false);
        return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, detail) { DeleteSourceUnconfirmed = true };
    }
}
