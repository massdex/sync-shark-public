namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Only retry recording sees this filtered view. Planning retains the original unknown-root
    // sentinel; existing read retries are not retired by a synthetic replay-pending observation.
    private BothScan ScanRetryFailures(PairDef pair, BothScan scan, string path)
    {
        if (path.Length != 0 || _store is not SqliteStateStore sql) return scan;
        SideScan WithoutReplayPending(RootDef root, SideScan side)
        {
            if (!sql.PendingGapInventory(root.RootId) || ((ILocalGapStore)sql).GapEpoch(root.RootId) is null ||
                side.InventoryFailure is not null || side.IncompleteReasons.GetValueOrDefault("") !=
                    MessageCatalog.Default.Template("reconciliation-replay-pending")) return side;
            return side with
            {
                IncompleteSubtrees = side.IncompleteSubtrees.Where(scope => scope.Length != 0).ToArray(),
                IncompleteReasons = side.IncompleteReasons.Where(entry => entry.Key.Length != 0)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            };
        }
        return scan with { A = WithoutReplayPending(pair.A, scan.A), B = WithoutReplayPending(pair.B, scan.B) };
    }

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
