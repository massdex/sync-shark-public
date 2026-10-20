using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    public static void NameMappingMigration(string root)
    {
        var data = Path.Combine(root, "mapping-v6");
        using (var store = SqliteStateStore.Open(data, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            var gen = store.BeginGeneration("rb", "snapshot");
            var files = Enumerable.Range(0, 600).Select(i => F($"dir:{i}/file", "id-" + i, "11"))
                .Append(F("ordinary/file", "ordinary", "11")).ToArray();
            store.WriteSnapshot(new("rb", Side.B, gen, "c", files, []));
            store.CompleteGeneration(gen, "c", true);
        }
        // Construct the pre-change v6 fixture; all entity rows and paths remain untouched.
        using (var connection = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(data)))
        {
            connection.Open();
            StoreSchema.Execute(connection, null,
                "DROP INDEX dir_mapped_key; DROP INDEX fs_refused; ALTER TABLE dir DROP COLUMN mapped_key; PRAGMA user_version=6;");
        }
        using (var store = SqliteStateStore.Open(data, Fast, new TestClock()))
        {
            Require(store.Live("rb").Count == 601, "v7 additive migration preserves every v6 entity");
            Require(store.DirectorySpellings("rb", "dir_599").Single() == "dir:599",
                "mapped directory backfill crosses multiple bounded batches");
        }
        using (var connection = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(data)))
        {
            connection.Open();
            Require((long)StoreSchema.Scalar(connection, null, "PRAGMA user_version;")! == StoreSchema.Version,
                "directory mapping v7 followed by refused index v8 uses current coordinated schema");
            Require((long)StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='fs_refused';")! == 1,
                "refused index follows directory mapping migration");
            Require((long)StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM dir WHERE path='ordinary' AND mapped_key IS NULL;")! == 1,
                "ordinary directories keep NULL mapped keys");
            Require(StoreSchema.QueryPlan(connection, "mapped", "SELECT path FROM dir WHERE mapped_key=$key;", "$key")
                .Any(p => p.Contains("dir_mapped_key", StringComparison.Ordinal)), "mapped ancestor point query uses partial index");
        }
        Console.WriteLine("PASS mapping migration v6 to v7, preservation, sparse keys, indexed lookup");
    }
}
