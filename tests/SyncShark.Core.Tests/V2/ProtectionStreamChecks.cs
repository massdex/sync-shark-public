using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class ProtectionStreamChecks
{
    internal static void Run(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        ItemState File(string path, int id, ItemFlags flags = ItemFlags.None) =>
            new(path, ItemKind.File, $"object-{id:D3}", null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, flags);
        var items = new[] { File("Parent/below", 1), File("Parent/pending", 2), File("Parent/above", 8),
            File("Other/flag", 3, (ItemFlags)128), File("Linked", 4, ItemFlags.Link) with { Kind = ItemKind.Folder },
            File("Empty", 5) with { Kind = ItemKind.Folder }, File("Other/rehash", 6), File("￿/above", 9) };
        var gen = store.BeginGeneration("local", "fixture");
        store.WriteSnapshot(new("local", Side.A, gen, "cursor", items, []));
        store.CompleteGeneration(gen, "cursor", true);
        store.RequireRehash("local", items[6], "synthetic flag hold");
        var cloud = File("CloudOnly/file", 20, ItemFlags.Placeholder);
        gen = store.BeginGeneration("cloud", "fixture");
        store.WriteSnapshot(new("cloud", Side.B, gen, "cursor", [cloud], []));
        store.CompleteGeneration(gen, "cursor", true);
        ((ILocalGapStore)store).BeginGap("local", "cursor", 1, false, "synthetic gap");
        using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(folder)};Pooling=False");
        connection.Open();
        StoreSchema.Execute(connection, null, "PRAGMA temp_store=FILE; PRAGMA temp.cache_size=-2048;");
        var a = Convert.ToInt64(StoreSchema.Scalar(connection, null, "SELECT root_no FROM root WHERE root_id='local';"));
        var b = Convert.ToInt64(StoreSchema.Scalar(connection, null, "SELECT root_no FROM root WHERE root_id='cloud';"));
        StoreSchema.Execute(connection, null, """
            UPDATE local_gap SET after_object='object-006';
            INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json)
              SELECT root_no,'object-002',epoch,'{}' FROM local_gap;
            """);
        var paths = new[] { "", "Parent", "Parent/below", "Parent/pending", "Parent/above", "Parent/absent",
            "Linked", "Linked/absent", "Empty", "Other", "Other/flag", "Other/rehash", "CloudOnly/file",
            "CloudOnly", "CloudOnly/absent", "￿", "￿/above", "parent/ABOVE", "Absent" };
        foreach (var flags in new[] { ItemFlags.NeedsRehash, ItemFlags.Link | ItemFlags.Placeholder | (ItemFlags)128,
            ItemFlags.NeedsRehash | ItemFlags.Link | ItemFlags.Placeholder | (ItemFlags)128 })
        {
            var left = store.ProtectionBatch("local", flags, paths);
            var right = store.ProtectionBatch("cloud", flags, paths);
            LocalChecks.Require(left.SequenceEqual(LegacyProtectionOracle.Read(connection, "local", flags, paths)) &&
                right.SequenceEqual(LegacyProtectionOracle.Read(connection, "cloud", flags, paths)), "old SQL oracle is exact");
            var expected = left.Zip(right, (x, y) => new ProtectionMatch(x.Covered || y.Covered, x.Contains || y.Contains)).ToArray();
            using var tx = connection.BeginTransaction();
            using var stream = new SqliteStateStore.ProtectionStream(connection, tx, a, b, flags, false);
            var actual = paths.Chunk(3).SelectMany(chunk => stream.Read(chunk)).ToArray();
            LocalChecks.Require(actual.SequenceEqual(expected),
                "set-based streamed partial gap equals old SQL for both roots, folders, absent paths, independent flags and Unicode");
        }
        using (var tx = connection.BeginTransaction())
        using (var stream = new SqliteStateStore.ProtectionStream(connection, tx, a, b, ItemFlags.NeedsRehash, false))
        {
            StoreSchema.Execute(connection, tx, "DROP TABLE protection_held;");
            var expected = store.ProtectionBatch("local", ItemFlags.NeedsRehash, paths)
                .Zip(store.ProtectionBatch("cloud", ItemFlags.NeedsRehash, paths),
                    (x, y) => new ProtectionMatch(x.Covered || y.Covered, x.Contains || y.Contains)).ToArray();
            LocalChecks.Require(stream.Read(paths).SequenceEqual(expected),
                "SQL failure in the held index falls back to the exact old batch reader");
            LocalChecks.Require(stream.Read(paths).SequenceEqual(expected),
                "subsequent batches retain exact fallback semantics");
        }
        store.EnableStorage();
        using (var tx = connection.BeginTransaction())
        using (var stream = new SqliteStateStore.ProtectionStream(connection, tx, a, b,
            ItemFlags.NeedsRehash | ItemFlags.Placeholder, true))
        {
            var flags = ItemFlags.NeedsRehash | ItemFlags.Placeholder;
            var expected = store.ProtectionBatch("local", flags, paths)
                .Zip(store.ProtectionBatch("cloud", flags, paths),
                    (x, y) => new ProtectionMatch(x.Covered || y.Covered, x.Contains || y.Contains));
            LocalChecks.Require(stream.Read(paths).SequenceEqual(expected),
                "FoD-enabled held index remains equivalent to exact storage-aware SQL");
        }
        var many = Enumerable.Range(0, 2049).Select(n => $"Parent/above/child-{n}");
        using (var tx = connection.BeginTransaction())
        using (var stream = new SqliteStateStore.ProtectionStream(connection, tx, a, b,
            ItemFlags.NeedsRehash, false))
        {
            var count = 0;
            foreach (var batch in many.Chunk(1024))
            {
                var result = stream.Read(batch);
                LocalChecks.Require(result.All(hit => hit == new ProtectionMatch(true, false)),
                    "stream preserves ancestor-only coverage across result batch boundaries");
                count += result.Count;
            }
            LocalChecks.Require(count == 2049 && stream.SourcePasses == 3 && stream.ResultQueries == 3 &&
                stream.MaximumInputBatch == 1024,
                "three bounded result batches use three indexed source passes and one query per batch");
        }
    }
}
