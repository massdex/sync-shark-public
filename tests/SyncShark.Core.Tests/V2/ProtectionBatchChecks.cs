using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Synthetic-only checks; the caller chooses the isolated fixture directory.</summary>
internal static class ProtectionBatchChecks
{
    internal static void Run(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        const string root = "gap-root";
        var paths = new[] { "Parent/Child/held", "Parent/clear", "Parent-Other/held", "Case/held", "case/other", "￿/held", "pending/leaf", "visited/leaf" };
        var items = paths.Select((path, n) => new ItemState(path, ItemKind.File, SyncLocalEndpointStore.ObjectId(1, (ulong)n + 10), null,
            new string('a', 64), 1, DateTimeOffset.UnixEpoch,
            n == 0 || n == 2 || n == 5 ? ItemFlags.NeedsRehash : ItemFlags.None)).ToArray();
        var gen = store.BeginGeneration(root, "fixture");
        store.WriteSnapshot(new Snapshot(root, Side.A, gen, "", items, []));
        store.CompleteGeneration(gen, "", true);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        var queries = new[] { "", "Parent", "Parent/Child", "parent/child/HELD", "Parent/Child/held/deep", "Parent/clear", "Paren", "Parent-Other", "Case", "case/other", "￿", "pending", "visited", "missing" };
        Check(items.Where(i => i.Flags != ItemFlags.None).Select(i => i.Path));
        var gap = (ILocalGapStore)store;
        gap.BeginGap(root, "synthetic-cursor", 1, false, "synthetic gap");
        Check(items.Select(i => i.Path));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_gap SET after_object=$pending;" +
                "INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json) " +
                "SELECT root_no,$pending,epoch,'{}' FROM local_gap;";
            command.Parameters.AddWithValue("$pending", items[6].ObjectId);
            command.ExecuteNonQuery();
        }
        Check(items.Where(i => i.Flags != ItemFlags.None || i.ObjectId == items[6].ObjectId || i.ObjectId == items[7].ObjectId).Select(i => i.Path));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_gap SET discovery_done=1;";
            command.ExecuteNonQuery();
        }
        Check(items.Where(i => i.Flags != ItemFlags.None || i.ObjectId == items[6].ObjectId).Select(i => i.Path));
        // Historical root containment is deliberately conservative even if a pending
        // entity now has folder metadata; non-root coverage still requires kind=file.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE file_state SET flags=0; UPDATE file_state SET kind=1 WHERE object_id=$pending;";
            command.Parameters.AddWithValue("$pending", items[6].ObjectId);
            command.ExecuteNonQuery();
        }
        var folderDebt = store.ProtectionBatch(root, ItemFlags.NeedsRehash, ["", "pending", "pending/leaf"]);
        LocalChecks.Require(folderDebt.SequenceEqual(new[] { new ProtectionMatch(false, true), new ProtectionMatch(false, false), new ProtectionMatch(false, false) }),
            "pending folder retains historical conservative root hold without file-only descendant hold");
        // A newly opened batch sees committed writes, rather than caching the previous snapshot.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM local_gap_item; UPDATE file_state SET flags=256 WHERE object_id=$held;";
            command.Parameters.AddWithValue("$held", items[1].ObjectId);
            command.ExecuteNonQuery();
        }
        Check(["Parent/clear"]);
        {
            // Model a million-row root for the planner without constructing a million-row
            // fixture. These statistics belong solely to this test-owned synthetic database.
            using var command = connection.CreateCommand();
            command.CommandText = """
                ANALYZE;
                UPDATE sqlite_stat1 SET stat='1000000 1000000 1000 1 1' WHERE idx='file_state';
                UPDATE sqlite_stat1 SET stat='1000000 1000000 1' WHERE idx='fs_obj';
                UPDATE sqlite_stat1 SET stat='100000 100000' WHERE idx='fs_flagged';
                UPDATE sqlite_stat1 SET stat='100000 1' WHERE idx='dir_key';
                INSERT INTO sqlite_stat1(tbl,idx,stat)
                  SELECT 'local_gap_item','local_gap_item','100000 100000 1'
                  WHERE NOT EXISTS(SELECT 1 FROM sqlite_stat1 WHERE idx='local_gap_item');
                UPDATE sqlite_stat1 SET stat='100000 100000 1' WHERE idx='local_gap_item';
                ANALYZE sqlite_schema;
                """;
            command.ExecuteNonQuery();
        }
        Plan(SqliteStateStore.ProtectionDirectSql, true);
        Plan(SqliteStateStore.ProtectionDirectoriesSql, false);

        void Check(IEnumerable<string> held)
        {
            var index = PathProtectionIndex.TryCreate(held)!;
            var actual = store.ProtectionBatch(root, ItemFlags.NeedsRehash, queries);
            for (var n = 0; n < queries.Length; n++)
                LocalChecks.Require(actual[n] == new ProtectionMatch(index.Touches(queries[n], false), index.Touches(queries[n], true)),
                    "batch equals old protected-path semantics for gap/non-gap, nested and case-folded paths");
        }

        void Plan(string sql, bool direct)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$r", 1);
            command.Parameters.AddWithValue("$flags", 256);
            command.Parameters.AddWithValue("$gap", 1);
            command.Parameters.AddWithValue("$done", 0);
            command.Parameters.AddWithValue("$after", items[6].ObjectId);
            command.Parameters.AddWithValue("$a", "[{\"n\":0,\"dir\":\"PARENT\",\"name\":\"CLEAR\",\"self\":1}]");
            command.Parameters.AddWithValue("$ranges", "[\"PARENT\",\"MISSING\"]");
            var details = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) details.Add(reader.GetString(3));
            LocalChecks.Require(details.Any(d => d.Contains("dir_key", StringComparison.Ordinal)) &&
                details.Any(d => d.Contains("SEARCH f USING PRIMARY KEY (root_no=? AND dir_no=?", StringComparison.Ordinal)) &&
                details.Any(d => d.Contains("SEARCH gi USING PRIMARY KEY (root_no=? AND object_id=?)", StringComparison.Ordinal)) &&
                !details.Any(d => d.StartsWith("SCAN d", StringComparison.Ordinal) || d.StartsWith("SCAN f", StringComparison.Ordinal)),
                "protection probes indexed directories, file PK and gap entity PK without whole-table scan");
            if (!direct) LocalChecks.Require(details.Any(d => d.Contains("path_key>? AND path_key<?", StringComparison.Ordinal)),
                "folder containment uses explicit indexed range rather than per-candidate correlated subtree");
        }
    }
}
