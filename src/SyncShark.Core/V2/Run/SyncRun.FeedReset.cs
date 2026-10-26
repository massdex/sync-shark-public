namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<Delta> ReplayAfterGapAsync(PairDef pair, RootDef root, IInventorySource inventory,
        Delta invalid, Func<string, bool> include, CancellationToken ct)
    {
        if (RehearsalEndpoints.Unwrap(inventory) is not LocalInventory local ||
            local.JournalCheckpoint() is null || _store is not SqliteStateStore sql ||
            !sql.PendingGapInventory(root.RootId) || ((ILocalGapStore)sql).GapEpoch(root.RootId) is not { } gap)
            return invalid;
        RequestReconciliation(pair.PairId, StateMonitor.FromGap(gap) with { RootId = root.RootId }, null, ct);
        var cursor = _store.Cursor(root.RootId);
        if (string.IsNullOrEmpty(cursor)) return invalid;
        var replay = await inventory.ChangesSinceAsync(cursor, invalid.Gen, include, ct);
        if (!replay.CursorInvalid) return replay;
        // A second discontinuity is still repair debt, not authority for an AutoSync full walk.
        return replay with
        {
            CursorInvalid = false, CursorAfter = cursor, Changes = [],
            IncompleteSubtrees = [string.Empty],
            IncompleteReasons = new Dictionary<string, string>
                { [string.Empty] = MessageCatalog.Default.Template("reconciliation-replay-pending") }
        };
    }
}
