namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Real ScanSide/AdvanceCursors, SQLite and directory IO; all journal reads are injected.
    internal static async Task GapRecoveryChecksAsync(string folder)
    {
        foreach (var cause in new[] { "overflow", "retained", "replaced", "ahead", "untrusted", "replace-pending" })
            await GapRecoveryAsync(Path.Combine(folder, cause), cause);
    }

    private sealed class GapWalkInterrupted : Exception { }

    private static async Task GapRecoveryAsync(string folder, string cause)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "file.txt", "old");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var start = new NtfsUsnCheckpoint(1, 0, cause == "ahead" ? 900 : 100, 0);
        var checkpoint = new NtfsUsnCheckpoint(cause == "replaced" ? 2UL : 1UL,
            cause == "retained" ? 150 : 0, 200, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System)
        { WalkCheckpointForTest = () => (start, false) };
        var gen = store.BeginGeneration(root.RootId, "full");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, default);
        var original = snapshot.Items.Single() with { Hash = DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("old")) };
        store.WriteSnapshot(snapshot with { Items = [original] });
        store.CompleteGeneration(gen, start.Cursor, true);
        var gaps = (ILocalGapStore)store;
        if (cause == "untrusted") gaps.BeginGap(root.RootId, "", 0, false);
        if (cause == "replace-pending")
        {
            gaps.BeginGap(root.RootId, checkpoint.Cursor, 100, true);
            checkpoint = new NtfsUsnCheckpoint(2, 0, 250, 0);
        }
        inventory.WalkCheckpointForTest = () => (checkpoint, false);
        var overflow = cause is "overflow" or "untrusted";
        var reads = new List<long>();
        var emitWrite = false;
        inventory.JournalReadForTest = ReadJournal;
        bool ReadJournal(string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct)
        {
            reads.Add(from.NextUsn);
            changes = [];
            failure = null;
            kind = NtfsUsnReadFailure.OverLimit;
            if (overflow) { overflow = false; failure = "synthetic overflow"; return false; }
            if (emitWrite && from.NextUsn <= 260 && through.NextUsn > 260)
            {
                var identity = LocalItems.ParseObjectId(original.ObjectId);
                changes = [new(identity.FileId, LocalItems.ParseObjectId(root.RootObjectId).FileId,
                    260, 1, FileAttributes.Normal, original.Path)];
            }
            return true;
        }
        inventory.DirectoryCompletedForTest = _ => throw new GapWalkInterrupted();
        fx.Provider.AddInventory(root, inventory);
        try { await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default); }
        catch (GapWalkInterrupted) { }
        var epoch = gaps.GapEpoch(root.RootId)!;
        Require(epoch is not null && store.GapInventoryPending(root.RootId) && store.Cursor(root.RootId) == checkpoint.Cursor,
            cause + ": interrupted run publishes checkpoint and retains full obligation");
        Require(epoch!.TrustFileUsn == (cause is "overflow" or "retained"), cause + ": correct trust after gap routing");
        var refresh = store.BeginGeneration(root.RootId, "quarantine-refresh");
        store.CompleteGeneration(refresh, checkpoint.Cursor, true);
        Require(store.GapInventoryPending(root.RootId), cause + ": targeted refresh cannot clear obligation");
        if (cause == "overflow")
        {
            gaps.GapDiscovered(root.RootId, epoch, original, false);
            gaps.GapDiscoveryDone(root.RootId, epoch);
        }
        // Discard only this fixture's resumable walk to model a crash before a page checkpoint.
        store.ForgetLocalWalk(root.RootId);
        LocalChecks.Seed(directory, "file.txt", "new");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "file.txt"), original.Mtime.UtcDateTime);
        checkpoint = checkpoint with { NextUsn = 300 };
        inventory.DirectoryCompletedForTest = null;
        emitWrite = true;
        reads.Clear();
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(gaps.GapEpoch(root.RootId)!.Epoch == epoch.Epoch, cause + ": next run preserves interrupted epoch");
        Require(reads.Count == 1 && reads[0] == (cause == "replace-pending" ? 250 : 200),
            cause + ": run catch-up replays advanced checkpoint, not newer walk start");
        Require(store.ByObject(root.RootId, original.ObjectId)?.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("new")),
            cause + ": same-size same-mtime crash-window write is rehashed by the run path");
        Require(!store.GapInventoryPending(root.RootId) && store.Cursor(root.RootId) == checkpoint.Cursor,
            cause + ": completed full side settles obligation and commits fresh cursor");
    }
}
