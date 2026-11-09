namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Completion of a selection whose queued work lies wholly outside the pair's exclusions.</summary>
internal sealed partial class ChangeTable
{
    // B-133: the executor asks this after its ancestor scheduling and before observing, so excluded work does not
    // spend a provider read only to settle as out of scope.
    /// <summary>Completes the selection through CompleteWork when every member names only excluded paths
    /// (PathScope.ExcludesChange) and owes nothing else, and returns whether it did. The caller has already checked
    /// that the anchor key is excluded. Rename custody, residuals, discovery, Local dirty evidence and a refused
    /// completion return false and keep the ordinary path.</summary>
    internal bool CompleteOutOfScope(WorkSelection selection, PathScope scope)
    {
        // Cheapest evidence first: each member's own flags, then the identity baseline (one read), then the walk.
        Dictionary<(Side, string), SyncedEntry?>? saved = null;
        var baselinePending = selection.Identity is not null;
        foreach (var member in ReadWork(selection))
        {
            if (member.IsResidual || member.DiscoveryRequired) return false;
            if (baselinePending)
            {
                if (!scope.ExcludesBinding(SelectionBaseline(null, selection))) return false;
                baselinePending = false;
            }
            var state = (Owner: this, selection.PairId, Saved: saved ??= new());
            if (!scope.ExcludesBinding(member.Before) || !scope.ExcludesChange(member.Notice, state, SavedOnce))
                return false;
        }
        if (ReadRenameContinuation(selection) is not null) return false;
        if (selection.Identity is not null)
            return (!baselinePending || scope.ExcludesBinding(SelectionBaseline(null, selection))) &&
                CompleteWork(selection, null);
        // A path selection's baseline is its anchor's binding by key, which the walk has usually read already.
        var baseline = saved is not null && saved.TryGetValue((selection.Side, selection.Key), out var known)
            ? known : SelectionBaseline(null, selection);
        return scope.ExcludesBinding(baseline) && CompleteWork(selection, null);
    }

    private static SyncedEntry? SavedOnce(
        (ChangeTable Owner, string PairId, Dictionary<(Side, string), SyncedEntry?> Saved) state, Side side, string key)
    {
        if (!state.Saved.TryGetValue((side, key), out var entry))
            state.Saved[(side, key)] = entry = SavedByKey((state.Owner, state.PairId), side, key);
        return entry;
    }

    private static SyncedEntry? SavedByKey((ChangeTable Owner, string PairId) state, Side side, string key) =>
        state.Owner.Read(null, side == Side.Local ? StateSchema.SelectSyncedByLocalKey
                : StateSchema.SelectSyncedByDropboxKey, ReadSaved, (StateSchema.ParamPair, state.PairId),
            (StateSchema.ParamTargetKey, key)).FirstOrDefault();
}
