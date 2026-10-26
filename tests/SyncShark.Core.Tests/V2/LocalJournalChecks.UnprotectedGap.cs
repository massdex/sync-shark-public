namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static void BeginGapUnprotected(string folder)
    {
        var product = Path.Combine(folder, "unprotected-gap-" + Guid.NewGuid().ToString("N"));
        const string root = "inventory-root";
        var row = new ItemState("file.txt", ItemKind.File, "item", null, new string('a', 64),
            3, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var cursor = new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor;
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var gen = store.BeginGeneration(root, "full");
            store.WriteSnapshot(new(root, Side.A, gen, "", [row], []));
            store.CompleteGeneration(gen, "", true);
            ((ILocalGapStore)store).BeginGap(root, cursor, 100, true, protect: false);
            LocalChecks.Require(store.PendingGapInventories().SequenceEqual([root]) && store.Cursor(root) == cursor,
                "unprotected gap atomically records its restart obligation and covered cursor");
        }
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var gaps = (ILocalGapStore)store;
            LocalChecks.Require(store.PendingGapInventories().SequenceEqual([root]) &&
                gaps.GapEpoch(root) is { DiscoveryDone: true } && !gaps.HasGapProtection(root) &&
                !store.ProtectionBatch(root, ItemFlags.NeedsRehash, [row.Path])[0].Covered,
                "unprotected inventory survives restart without legacy per-file protection");
            store.CompleteGapInventory(root, gaps.GapEpoch(root)!.Epoch);
            LocalChecks.Require(store.PendingGapInventories().Count == 0,
                "exact completion removes the pending inventory from monitor intake");
            gaps.BeginGap(root, new NtfsUsnCheckpoint(1, 0, 300, 0).Cursor, 200, true);
            LocalChecks.Require(gaps.HasGapProtection(root), "default gap creation retains legacy protection");
            gaps.BeginGap(root, new NtfsUsnCheckpoint(1, 0, 400, 0).Cursor, 300, true, protect: false);
            LocalChecks.Require(gaps.HasGapProtection(root) && store.GapInventoryPending(root),
                "monitor gap creation cannot discharge an existing legacy coverage obligation");
        }
    }
}
