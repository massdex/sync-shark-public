using System.Text.Json;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

internal static class StorageQueuePlanChecks
{
    private const string FirstObject = "id:local:00000001:0000000000000002";
    private const string SecondObject = "id:local:00000001:00000000000000ae";

    internal static Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh disposable queue-plan fixture directory.");
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        {
            var local = new RootDef("local", SyncProviders.Local, null, root, "id:local:00000001:0000000000000001");
            var remote = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
            store.SavePair(new("pair", "Pair A", local, remote, [], [], true));
            store.EnableStorage();
            using var connection = Open(root);
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT sqlite_version();";
                Console.WriteLine("Storage queue checks SQLite runtime: " + version.ExecuteScalar());
            }
            RequestedIndexesMatchSentinel(connection);
            QueueQueriesUseSearchRanges(connection);
            SeedQueues(connection);
            PagesPrioritizeWithoutDuplicates(store);
        }
        LegacyDueMigrationCreatesRequestedIndexes(root);
        Console.WriteLine("PASS storage queue plans: three partial-index Retry searches, three normal range searches, bounded deduplicated pages and lazy due migration");
        return Task.CompletedTask;
    }

    private static void RequestedIndexesMatchSentinel(SqliteConnection connection)
    {
        Require(StorageDue.UserRequested == -1, "partial-index/query literals must match the named Retry sentinel");
        foreach (var name in new[] { "storage_pending_requested", "storage_hydration_requested", "storage_refresh_requested" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";
            command.Parameters.AddWithValue("$name", name);
            Require(command.ExecuteScalar() is string ddl && ddl.Contains("WHERE due=-1", StringComparison.Ordinal),
                name + ": durable partial index exists and selects only Retry rows");
            Require(FilesOnDemandSchema.Names.Contains(name), name + ": schema verification accepts the lazy index");
        }
    }

    private static void QueueQueriesUseSearchRanges(SqliteConnection connection)
    {
        // These are the product statements, not test copies. Scanning json_each($roots), the small
        // active-pair catalog, is expected; scanning any queue or receipt table is not.
        var queries = new[]
        {
            (SqliteStateStore.StorageSettlementRequestedSql, "sw", "storage_pending_requested", "root_id=?"),
            (SqliteStateStore.StorageSettlementRangeSql, "sw", "storage_pending_root", "job_id>?"),
            (SqliteStateStore.StorageHydrationRequestedSql, "h", "storage_hydration_requested", "root_id=?"),
            (SqliteStateStore.StorageHydrationRangeSql, "h", "storage_hydration_root", "job_id>?"),
            (SqliteStateStore.StoragePointerRequestedSql, "rq", "storage_refresh_requested", "root_id=?"),
            (SqliteStateStore.StoragePointerRangeSql, "rq", "storage_refresh_root", "due>?")
        };
        foreach (var (sql, alias, index, range) in queries)
        {
            Require(!sql.Contains(" OR ", StringComparison.OrdinalIgnoreCase) &&
                !sql.Contains("CASE", StringComparison.OrdinalIgnoreCase), index + ": priority cannot alter range/order SQL");
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$roots", "[\"local\"]");
            command.Parameters.AddWithValue("$now", 1000L);
            command.Parameters.AddWithValue("$after", 500L);
            command.Parameters.AddWithValue("$r", "local");
            command.Parameters.AddWithValue("$o", FirstObject);
            command.Parameters.AddWithValue("$b", "remote");
            command.Parameters.AddWithValue("$limit", 128);
            var details = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) details.Add(reader.GetString(3));
            var detail = string.Join(" | ", details);
            Require(details.Any(line => line.StartsWith("SEARCH " + alias + " ", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(index, StringComparison.Ordinal) && line.Contains(range, StringComparison.Ordinal)),
                index + ": indexed SEARCH with " + range + " required: " + detail);
            Require(!details.Any(line => line.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("SCAN roots ", StringComparison.OrdinalIgnoreCase)),
                index + ": no queue/receipt scan is allowed: " + detail);
            // job_id is INTEGER PRIMARY KEY, so it is the rowid alias. SQLite may surface the
            // candidate equality through a named index as job_id=? (settlement range uses
            // storage_pending(state,job_id)) instead of INTEGER PRIMARY KEY (rowid=?); both are
            // the same single-row point lookup, so accept either spelling.
            Require(details.Any(line => line.StartsWith("SEARCH ", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("rowid=?", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("job_id=?", StringComparison.OrdinalIgnoreCase))),
                index + ": bounded candidates use a job-key point lookup: " + detail);
        }
    }

    private static void SeedQueues(SqliteConnection connection)
    {
        var receipt = JsonSerializer.Serialize(new PilotRemoteEntry("file.bin", PilotEntryKind.File,
            "remote-file", "revision-1", 4, new string('a', 64)));
        foreach (var item in new[] { FirstObject, SecondObject })
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO storage_settlement(root_id,object_id,remote_binding,remote_json) VALUES('local',$o,'remote',$j);
                INSERT INTO storage_hydration(root_id,object_id) VALUES('local',$o);
                INSERT INTO storage_item(root_id,object_id,remote_binding,remote_json,availability,pinned,in_sync)
                  VALUES('local',$o,'remote',$j,'OnlineOnly',0,1);
                INSERT INTO storage_refresh(root_id,object_id,remote_binding,due) VALUES('local',$o,'remote',0);
                """;
            command.Parameters.AddWithValue("$o", item);
            command.Parameters.AddWithValue("$j", receipt);
            command.ExecuteNonQuery();
        }
    }

    private static void PagesPrioritizeWithoutDuplicates(SqliteStateStore store)
    {
        var settlement = store.StorageSettlementPage().Single(job => job.ObjectId == FirstObject);
        var hydration = store.StorageHydrationPage().Single(job => job.ObjectId == FirstObject);
        var pointer = store.ChangedStoragePointers().Single(job => job.ObjectId == FirstObject);
        store.RequestStorageSettlement(settlement);
        store.RequestStorageHydration(hydration);
        store.RequestStoragePointer(pointer);
        Require(store.StorageSettlementPage(limit: 2).Select(job => job.ObjectId).SequenceEqual(new[] { FirstObject, SecondObject }),
            "settlement: requested row present in both queries is returned once, then normal work");
        Require(store.StorageHydrationPage(limit: 2).Select(job => job.ObjectId).SequenceEqual(new[] { FirstObject, SecondObject }),
            "hydration: requested row present in both queries is returned once, then normal work");
        Require(store.ChangedStoragePointers(limit: 2).Select(job => job.ObjectId).SequenceEqual(new[] { FirstObject, SecondObject }),
            "refresh: requested row present in both queries is returned once, then normal work");
        Require(store.StorageSettlementPage(long.MaxValue, 1).Single().ObjectId == FirstObject &&
            store.StorageHydrationPage(long.MaxValue, 1).Single().ObjectId == FirstObject &&
            store.ChangedStoragePointers(SqliteStateStore.StoragePointerCursor(pointer), 1).Single().ObjectId == FirstObject,
            "all three requested queries bypass an advanced cursor and respect a one-row limit");
        store.SetStorageRootBackoff("local", TimeSpan.FromHours(1));
        Require(store.StorageSettlementPage().Single().ObjectId == FirstObject &&
            store.StorageHydrationPage().Single().ObjectId == FirstObject &&
            store.ChangedStoragePointers().Single().ObjectId == FirstObject,
            "all three normal queries retain root backoff while explicit requests bypass it");
        Require(store.StorageSettlementPage(activeRoots: []).Count == 0 &&
            store.StorageHydrationPage(activeRoots: []).Count == 0 &&
            store.ChangedStoragePointers(activeRoots: []).Count == 0,
            "all three requested queries still respect active-root filtering");
    }

    private static void LegacyDueMigrationCreatesRequestedIndexes(string root)
    {
        // Only this test's disposable database is changed. Reproduce an opted-in schema predating due.
        using (var connection = Open(root))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX storage_pending_requested;
                DROP INDEX storage_hydration_requested;
                DROP INDEX storage_refresh_requested;
                ALTER TABLE storage_settlement DROP COLUMN due;
                """;
            command.ExecuteNonQuery();
        }
        using var reopened = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        using var check = Open(root);
        RequestedIndexesMatchSentinel(check);
        QueueQueriesUseSearchRanges(check);
        Require(reopened.StorageHydrationPage(limit: 1).Single().ObjectId == FirstObject,
            "opening a legacy opted-in store preserves durable Retry rows");
    }

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SqliteStateStore.DatabasePath(root), Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
