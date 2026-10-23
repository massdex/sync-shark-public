using System.Buffers.Binary;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    internal static void GapCursorChecks(string folder)
    {
        var product = Path.Combine(folder, "gap-cursor-" + Guid.NewGuid().ToString("N"));
        var root = new RootDef("cursor-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        var row = new ItemState("file", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 11), null,
            new string('a', 64), 3, DateTimeOffset.UtcNow, ItemFlags.None);
        var before = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var covered = new NtfsUsnCheckpoint(1, 0, 200, 0);
        string epochId;
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gen = store.BeginGeneration(root.RootId, "full");
            store.WriteSnapshot(new(root.RootId, Side.A, gen, before.Cursor, [row], []));
            store.CompleteGeneration(gen, before.Cursor, true);
            var otherGen = store.BeginGeneration("other-root", "full");
            store.WriteSnapshot(new("other-root", Side.B, otherGen, "remote-cursor", [row], []));
            store.CompleteGeneration(otherGen, "remote-cursor", true);
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(product, "v2", "state.db"));
            db.Open();
            StoreSchema.Execute(db, null, "CREATE TRIGGER fail_cursor BEFORE UPDATE OF cursor ON root BEGIN SELECT RAISE(ABORT,'injected cursor failure'); END;");
            var gaps = (ILocalGapStore)store;
            try { gaps.BeginGap(root.RootId, covered.Cursor, 100, true); throw new InvalidOperationException("expected injected failure"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            LocalChecks.Require(store.Cursor(root.RootId) == before.Cursor && gaps.GapEpoch(root.RootId) is null,
                "cursor write failure rolls back epoch publication atomically");
            StoreSchema.Execute(db, null, "DROP TRIGGER fail_cursor;");
            var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System);
            var delta = inventory.JournalReadFailed(gen, root.RootId, before.Cursor, covered, 100, "limit", NtfsUsnReadFailure.OverLimit);
            var epoch = gaps.GapEpoch(root.RootId)!;
            epochId = epoch.Epoch;
            LocalChecks.Require(delta.CursorInvalid && store.Cursor(root.RootId) == covered.Cursor && store.GapInventoryPending(root.RootId),
                "over-limit atomically advances covered cursor and retains crash-safe inventory obligation");
            gaps.GapDiscovered(root.RootId, epoch, row, true);
            gaps.StageGapHash(root.RootId, epoch, row, new string('b', 64),
                new GapCheckIo(row.Mtime, covered.Cursor).Proof(row, 100));
            gaps.GapDiscoveryDone(root.RootId, epoch);
        }
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System))
        {
            var gaps = (ILocalGapStore)store;
            gaps.PromoteGapDiscovery(root.RootId);
            LocalChecks.Require(gaps.GapEpoch(root.RootId) is { DiscoveryDone: true } current && current.Epoch == epochId &&
                gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Count == 1,
                "next-run promotion keeps epoch and staged content across interruption");
            // A write during discovery belongs to [covered, next), not the gap's consumed range.
            NtfsUsnCheckpoint.TryParseCursor(store.Cursor(root.RootId), out _, out var replayFrom);
            var record = new byte[62];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 62);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 11);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 1);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 201);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
            record[60] = (byte)'x';
            var replay = NtfsUsnJournal.ParseChanges(record, replayFrom, 300);
            LocalChecks.Require(replayFrom == 200 && replay.Count == 1 && replay[0].FileId == 11 &&
                !new NtfsUsnCheckpoint(1, 0, 300, 0).RequiresFullReconciliation(store.Cursor(root.RootId)),
                "discovery-time write remains in the next journal replay interval");
            LocalChecks.Require(store.Live(root.RootId).Single() == row &&
                store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, [row.Path])[0].Covered,
                "gap neither removes retained inventory nor admits destructive effects for unverified content");
            var gen = store.BeginGeneration(root.RootId, "full");
            store.CompleteGeneration(gen, covered.Cursor, false);
            LocalChecks.Require(store.GapInventoryPending(root.RootId), "incomplete generation retains inventory obligation");
            gen = store.BeginGeneration(root.RootId, "full");
            store.CompleteGeneration(gen, covered.Cursor, true);
            LocalChecks.Require(store.GapInventoryPending(root.RootId), "generation completion alone cannot settle inventory obligation");
            var refresh = store.BeginGeneration(root.RootId, "quarantine-refresh");
            store.CompleteGeneration(refresh, covered.Cursor, true);
            LocalChecks.Require(store.GapInventoryPending(root.RootId), "single-path quarantine refresh preserves the full-inventory obligation");
            store.SettleGapInventory(gen);
            LocalChecks.Require(!store.GapInventoryPending(root.RootId), "explicit completed full-side settlement clears inventory obligation");
            var staged = gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Single();
            LocalChecks.Require(gaps.AcceptGapResult(root.RootId, staged) && !gaps.HasGapProtection(root.RootId) &&
                store.RehashCandidates(root.RootId, 128).Any(item => item.Path == row.Path),
                "discharged gap item re-enters durable planning work even with empty next delta");
            LocalChecks.Require(store.Cursor("other-root") == "remote-cursor" &&
                store.Live("other-root").Single() == row && !store.GapInventoryPending("other-root"),
                "early cursor advancement never alters the other root cursor or inventory");
            gaps.BeginGap(root.RootId, "0000000000000001:400", 200, true);
            LocalChecks.Require(gaps.GapEpoch(root.RootId)!.Epoch != epochId && gaps.GapEpoch(root.RootId)!.Boundary == 200,
                "genuinely later overflow after discharge starts a new proof boundary");
        }
    }
}
