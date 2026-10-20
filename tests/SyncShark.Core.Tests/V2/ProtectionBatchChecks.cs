using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Synthetic-only checks; the caller chooses the isolated fixture directory.</summary>
internal static class ProtectionBatchChecks
{
    internal static void Run(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast with { StoreBatchRows = 1 }, TimeProvider.System);
        const string root = "gap-root";
        var paths = new[] { "Parent/Child/Deep/held", "Parent/clear", "Parent-Other/held", "Case/held", "case/other", "￿/held", "pending/leaf", "visited/leaf", "Parent/shallow" };
        var items = paths.Select((path, n) => new ItemState(path, ItemKind.File, SyncLocalEndpointStore.ObjectId(1, (ulong)n + 10), null,
            new string('a', 64), 1, DateTimeOffset.UnixEpoch,
            n == 0 || n == 2 || n == 5 || n == 8 ? ItemFlags.NeedsRehash : ItemFlags.None)).ToArray();
        var gen = store.BeginGeneration(root, "fixture");
        store.WriteSnapshot(new Snapshot(root, Side.A, gen, "", items, []));
        store.CompleteGeneration(gen, "", true);
        // Snapshot writes reconcile flags against durable entity debt; an incoming flag
        // alone is deliberately cleared. Establish real holds before building the oracle.
        foreach (var item in items.Where(i => (i.Flags & ItemFlags.NeedsRehash) != 0))
            store.RequireRehash(root, item, "synthetic protection hold");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        var queries = new[] { "", "Parent", "Parent/Child", "parent/child/HELD", "Parent/Child/held/deep", "Parent/clear", "Paren", "Parent-Other", "Case", "case/other", "￿", "pending", "visited", "missing", "PARENT", "parent", "Parent/Child/Deep", "Parent/Child/Deep/held" }
            .Concat(Enumerable.Range(0, 300).Select(n => $"Parent/missing-{n}")).ToArray();
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
        // The executed 940b56be SQL requires kind=file for pending gap debt, including
        // root containment. Its special root Replace never matched the raw-string indent.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE file_state SET flags=0; UPDATE file_state SET kind=1 WHERE object_id=$pending;";
            command.Parameters.AddWithValue("$pending", items[6].ObjectId);
            command.ExecuteNonQuery();
        }
        var folderDebt = store.ProtectionBatch(root, ItemFlags.NeedsRehash, ["", "pending", "pending/leaf"]);
        LocalChecks.Require(folderDebt.SequenceEqual(new[] { new ProtectionMatch(false, false), new ProtectionMatch(false, false), new ProtectionMatch(false, false) }),
            "pending folder alone has no file-only gap hold, including at root");
        LocalChecks.Require(folderDebt.SequenceEqual(LegacyProtectionOracle.Read(connection, root,
            ItemFlags.NeedsRehash, ["", "pending", "pending/leaf"])),
            "pending folder root containment equals executed 940b56be SQL");
        // A newly opened batch sees committed writes, rather than caching the previous snapshot.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM local_gap_item;";
            command.ExecuteNonQuery();
        }
        store.RequireRehash(root, items[1], "synthetic movable protection hold");
        Check(["Parent/clear"]);
        var snapshotCalls = 0;
        var moved = items[1] with { Path = "Moved/clear", Flags = ItemFlags.NeedsRehash };
        var inserted = items[7] with { Path = "Other/inserted", ObjectId = SyncLocalEndpointStore.ObjectId(1, 99) };
        var deltaGeneration = store.BeginGeneration(root, "delta");
        store.ProtectionSnapshotReadyForTest = () =>
        {
            snapshotCalls++;
            // StoreBatchRows=1 commits the unrelated upsert before the moved held
            // entity's upsert. Current move suppression preserves the old row until
            // that upsert; the snapshot guarantee must not depend on that optimization.
            // The pinned reader retains the old hold across the entire multi-commit call.
            var writer = Task.Run(() => store.WriteDelta(new Delta(root, Side.A, deltaGeneration, "", "",
            [
                new Change(ChangeKind.Delete, items[1].ObjectId, items[1].Path, null, DateTimeOffset.UnixEpoch),
                new Change(ChangeKind.Upsert, inserted.ObjectId, inserted.Path, inserted, null),
                new Change(ChangeKind.Upsert, moved.ObjectId, moved.Path, moved, null),
            ], false, [])));
            LocalChecks.Require(writer.Wait(TimeSpan.FromSeconds(10)), "snapshot releases the gate for multi-commit store writers");
        };
        try
        {
            var before = store.ProtectionBatch(root, ItemFlags.NeedsRehash, [items[1].Path, moved.Path]);
            LocalChecks.Require(snapshotCalls == 1 && before[0].Covered && !before[1].Covered,
                "pinned snapshot retains old-path hold across multi-commit move");
        }
        finally { store.ProtectionSnapshotReadyForTest = null; }
        var afterMove = store.ProtectionBatch(root, ItemFlags.NeedsRehash, [items[1].Path, moved.Path]);
        LocalChecks.Require(!afterMove[0].Covered && afterMove[1].Covered,
            "next batch sees the moved hold only at its new path");
        var failures = 0;
        store.ProtectionSnapshotReadyForTest = () => { failures++; throw new IOException("synthetic reader failure"); };
        try
        {
            LocalChecks.Require(store.ProtectionBatch(root, ItemFlags.NeedsRehash, [moved.Path])[0].Covered && failures == 1,
                "one reader failure falls back once without relaxing the hold");
        }
        finally { store.ProtectionSnapshotReadyForTest = null; }

        store.ProtectionSnapshotReadyForTest = () => throw new SqliteException("synthetic corruption", 11);
        var damaged = false;
        try { store.ProtectionBatch(root, ItemFlags.NeedsRehash, [moved.Path]); }
        catch (StateDamagedException) { damaged = true; }
        finally { store.ProtectionSnapshotReadyForTest = null; }
        LocalChecks.Require(damaged, "corruption remains a damage exception rather than reader fallback");

        // Seed one real pending fixture row so ANALYZE emits its WITHOUT ROWID PK
        // statistic. Resolve that index via pragma_index_list instead of guessing its name.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json) " +
                "SELECT root_no,$pending,epoch,'{}' FROM local_gap; ANALYZE;";
            command.Parameters.AddWithValue("$pending", items[6].ObjectId);
            command.ExecuteNonQuery();
        }
        SetStats("file_state", null, "1000000 1000000 1000 1 1");
        SetStats("file_state", "fs_obj", "1000000 1000000 1");
        SetStats("file_state", "fs_flagged", "100000 100000");
        SetStats("dir", "dir_key", "100000 1");
        SetStats("local_gap_item", null, "100000 100000 1");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ANALYZE sqlite_schema;";
            command.ExecuteNonQuery();
        }
        Plan(SqliteStateStore.ProtectionDirectSql, true);
        Plan(SqliteStateStore.ProtectionDirectoriesSql, false);
        Plan(SqliteStateStore.ProtectionRootSql, false);

        void SetStats(string table, string? index, string stats)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sqlite_stat1 SET stat=$stats WHERE tbl=$table AND " +
                "(idx=$index OR ($index IS NULL AND (idx=$table OR idx IN " +
                "(SELECT name FROM pragma_index_list($table) WHERE origin='pk'))));";
            command.Parameters.AddWithValue("$table", table);
            command.Parameters.AddWithValue("$index", (object?)index ?? DBNull.Value);
            command.Parameters.AddWithValue("$stats", stats);
            LocalChecks.Require(command.ExecuteNonQuery() == 1, "million-item planner statistic updates exactly one real index");
        }

        void Check(IEnumerable<string> held)
        {
            var index = PathProtectionIndex.TryCreate(held)!;
            var actual = store.ProtectionBatch(root, ItemFlags.NeedsRehash, queries);
            var legacy = LegacyProtectionOracle.Read(connection, root, ItemFlags.NeedsRehash, queries);
            for (var n = 0; n < queries.Length; n++)
            {
                LocalChecks.Require(actual[n] == legacy[n],
                    $"batch equals 940b56be SQL at '{queries[n]}': batch={actual[n]}, legacy={legacy[n]}");
                var expected = new ProtectionMatch(index.Touches(queries[n], false), index.Touches(queries[n], true));
                LocalChecks.Require(actual[n] == expected,
                    $"batch equals protected-path fixture at '{queries[n]}': batch={actual[n]}, expected={expected}");
            }
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
            if (sql == SqliteStateStore.ProtectionRootSql)
            {
                LocalChecks.Require(details.Any(d => d.Contains("fs_flagged", StringComparison.Ordinal)) &&
                    details.Any(d => d.Contains("fs_obj", StringComparison.Ordinal)) &&
                    details.Any(d => d.Contains("SEARCH gi USING PRIMARY KEY", StringComparison.Ordinal)) &&
                    !details.Any(d => d.StartsWith("SCAN f", StringComparison.Ordinal)),
                    "root containment uses flagged, object and pending entity indexes");
                return;
            }
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
