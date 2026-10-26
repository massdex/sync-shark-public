namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task MissingCursorReconciliationAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "1", "old");
        LocalChecks.Seed(directory, "2", "old");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = LocalChecks.RootOf(directory, "missing-cursor-root");
        var through = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast,
            TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage)
            { WalkCheckpointForTest = () => (through, false) };
        var generation = store.BeginGeneration(root.RootId, "full");
        var snapshot = await inventory.FullAsync(generation, _ => true, null!, default);
        var rows = snapshot.Items.OrderBy(item => item.Path).Select(item => item with
            { Hash = DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("old")) }).ToArray();
        store.WriteSnapshot(new(root.RootId, Side.A, generation, "", rows, []));
        inventory.BeginFullGap(root.RootId, through.Cursor, journalDeleted: false);
        var epoch = ((ILocalGapStore)store).GapEpoch(root.RootId)!;
        Require(epoch is { TrustFileUsn: false } && store.Cursor(root.RootId) is null &&
            store.RehashPendingCount(root.RootId) == 0,
            "missing saved cursor requests untrusted reconciliation without pre-seeded content debt");
        await SavedCursorReconciliationAsync(store, root, inventory, rows, epoch, through, folder, untrusted: true);
    }

    private static void CheckUntrustedPending(SqliteStateStore store, RootDef root, ItemState[] rows,
        BoundaryPlanCapture executor, CountingWriter writer)
    {
        Require(executor.Actions.Count == 0 && writer.Calls == 0 && store.GapInventoryPending(root.RootId),
            "untrusted retained files cannot plan or apply any effect before content proof");
        Require(store.RehashPendingCount(root.RootId) == rows.Length && rows.All(item =>
            store.ByObject(root.RootId, item.ObjectId) is { Hash: null } pending &&
                pending.Flags.HasFlag(ItemFlags.NeedsRehash)),
            "untrusted reconciliation invalidates every retained cached hash despite unchanged metadata");
    }

    private static void CheckUntrustedSettled(SqliteStateStore store, RootDef root, ItemState[] rows,
        LocalGapEpoch epoch, CursorBoundaryIo io, RunReport report)
    {
        Require(io.Probes == 0 && rows.All(item => io.ReadsFor(item.Path) == 1) &&
            report.Failed == 0 && report.Uncertain == 0,
            "untrusted reconciliation reads every retained file once and trusts no file USN");
        Require(store.RehashPendingCount(root.RootId) == 0 && !store.GapInventoryPending(root.RootId) &&
            !((ILocalGapStore)store).HasGapProtection(root.RootId) &&
            ((ILocalGapStore)store).GapEpoch(root.RootId)!.Epoch == epoch.Epoch &&
            store.ByObject(root.RootId, rows[0].ObjectId)!.Hash == rows[0].Hash,
            "unchanged content keeps its hash and the untrusted epoch clears only after all rows are acknowledged");
    }
}
