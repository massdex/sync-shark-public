using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static void EvidenceRevisionMigration(string folder)
    {
        const string root = "revision-root";
        var item = new ItemState("file.txt", ItemKind.File, "revision-item", null, new string('a', 64), 3,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        long oldRevision;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var gen = store.BeginGeneration(root, "seed");
            store.WriteSnapshot(new(root, Side.A, gen, "saved-cursor", [item], []));
            store.CompleteGeneration(gen, "saved-cursor", true);
            store.RequireRehash(root, item, "existing v26 obligation");
            oldRevision = store.RehashWatermark(root);
        }
        // Construct the actual previous shape, not merely a lower stamp with the new column present.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE local_rehash DROP COLUMN evidence_revision; PRAGMA user_version=26;";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('local_rehash') WHERE name='evidence_revision';";
            LocalChecks.Require(Convert.ToInt64(command.ExecuteScalar()) == 0, "upgrade fixture has genuine v26 rehash shape with existing rows");
        }
        long nextRevision;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            LocalChecks.Require(store.RehashRevision(root, item.ObjectId) == oldRevision && store.Cursor(root) == "saved-cursor" &&
                store.RehashReasons(root, 1).Single().Contains("existing v26 obligation"),
                "v26 to v27 backfills captured evidence token without losing obligation, reason or cursor");
            var beforeOrder = store.RehashWatermark(root);
            store.RehashCandidates(root, 1, beforeOrder, true);
            LocalChecks.Require(store.RehashWatermark(root) > beforeOrder && store.RehashRevision(root, item.ObjectId) == oldRevision,
                "selection rotates scheduling only, leaving in-flight evidence valid");
            LocalChecks.Require(store.CompleteIndexingRehash(root, item, new string('b', 64), oldRevision),
                "captured pre-upgrade proof can complete after migrated queue selection");
            LocalChecks.Require(store.RehashRevision(root, item.ObjectId) == oldRevision,
                "successful publication does not invalidate content evidence");
            store.RequireRehash(root, item, "new authoritative write");
            nextRevision = store.RehashRevision(root, item.ObjectId);
            LocalChecks.Require(nextRevision != oldRevision && !store.CompleteIndexingRehash(root, item, new string('b', 64), oldRevision),
                "actual newer write changes evidence revision and rejects older proof");
        }
        using (var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
            LocalChecks.Require(reopened.RehashRevision(root, item.ObjectId) == nextRevision && reopened.HasPendingRehashVerification(root),
                "repeated compatible open leaves migrated and subsequently updated evidence unchanged");
    }
}
