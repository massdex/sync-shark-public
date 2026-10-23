using System.Diagnostics;
using System.Security.Cryptography;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// A retained, checkpointed owner fixture is copied once. Exercise the real projection
// entry points with their real endpoint validator, never a substitute SQL/UDF. This
// measures the whole read including schema validation and materialization at real scale.
internal static class RetainedActivityScalingChecks
{
    internal static async Task RunAsync(string source, string root)
    {
        var slowPhases = new List<string>();
        async Task<T> MeasureAsync<T>(string label, Func<Task<T>> action)
        {
            var watch = Stopwatch.StartNew();
            var result = await action();
            Console.WriteLine($"MEASURE {label}: {watch.Elapsed.TotalSeconds:F3} seconds");
            var message = label + " finishes within the conservative real-profile responsiveness bound";
            if (watch.Elapsed >= TimeSpan.FromSeconds(20))
            {
                slowPhases.Add(label + $": {watch.Elapsed.TotalSeconds:F3} seconds");
                Console.WriteLine("FAIL " + message);
            }
            else Console.WriteLine("PASS " + message);
            return result;
        }

        if (Directory.Exists(root)) throw new IOException("The scaling fixture requires a fresh isolated root.");
        if (!File.Exists(source) || File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length != 0)
            throw new IOException("Supply a retained checkpoint without an active WAL, never a live database.");
        var sourceHash = await HashAsync(source);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "activity.db");
        File.Copy(source, path, overwrite: false);
        Require(await HashAsync(path) == sourceHash && await HashAsync(source) == sourceHash,
            "The isolated retained snapshot is byte-exact and its source did not change during intake");
        SQLitePCL.Batteries_V2.Init();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            foreach (var (table, minimum) in new[] { ("history_event", 20_000), ("operation_member", 10_000) })
            {
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT count(*) FROM " + table;
                var rows = (long)(await count.ExecuteScalarAsync())!;
                Require(rows >= minimum, $"Representative retained {table}: {rows:N0} rows");
            }
        }
        var db = new SyncAuthorityDatabase(path);
        var startupHealthChecks = 0;
        SyncAuthorityDatabase.HealthCheckCompletedForTest = checkedPath =>
        {
            if (Path.GetFullPath(checkedPath).Equals(path, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref startupHealthChecks);
        };
        try
        {
            var watch = Stopwatch.StartNew();
            Require(!string.IsNullOrWhiteSpace(await db.LoadInstallationIdAsync()),
                "The retained profile passes schema and installation identity checks without a retained-data scan");
            Console.WriteLine($"MEASURE Native retained-profile cold startup schema guards: {watch.Elapsed.TotalSeconds:F3} seconds");
            await MeasureAsync("First pending count after startup", () => db.LoadPendingQueueCountAsync());
            Require(startupHealthChecks == 0,
                "Neither startup nor the first authority read scans the full retained database at scale");
        }
        finally { SyncAuthorityDatabase.HealthCheckCompletedForTest = null; }
        var recent = await MeasureAsync("Activity Changes page", () =>
            db.LoadActivityProjectionPageAsync(251, filter: "Changes"));
        Require(recent.Count == 251 && recent.Select(row => row.EventId).Distinct().Count() == recent.Count,
            "The real merged activity page retains distinct decisions, lifecycle and presentation records");
        var files = await MeasureAsync("Home file-activity slice", () =>
            db.LoadActivityProjectionPageAsync(40, filter: "Changes",
                query: new SyncActivityQuery([SyncActivityCategories.Files])));
        Require(files.Count > 0 && files.All(row => row.Categories?.Contains(SyncActivityCategories.Files) == true),
            "File filtering still occurs before the bounded Home page");
        var newest = await MeasureAsync("Newest detailed operation log", () =>
            db.LoadActivityProjectionPageAsync(200, query: new SyncActivityQuery(DetailedLog: true, NewestLogFirst: true)));
        Require(newest.Count == 200 && newest.All(row => row.Sequence is not null) &&
                newest.Select(row => row.Sequence).SequenceEqual(newest.Select(row => row.Sequence).OrderDescending()),
            "The real detailed log opens the newest retained rows with descending durable cursors");
        var earlier = await MeasureAsync("Earlier detailed log page", () =>
            db.LoadActivityProjectionPageAsync(200, query: new SyncActivityQuery(DetailedLog: true,
                NewestLogFirst: true, ThroughSequence: newest[^1].Sequence - 1)));
        Require(earlier.Count > 0 && earlier.All(row => row.Sequence < newest[^1].Sequence) &&
                !earlier.Select(row => row.EventId).Intersect(newest.Select(row => row.EventId)).Any(),
            "Earlier retained history remains accessible without page overlap or event replay");
        var errors = await MeasureAsync("Retained exact failures", () =>
            db.LoadActivityProjectionPageAsync(200, query: new SyncActivityQuery(Severity: "Error", DetailedLog: true,
                NewestLogFirst: true)));
        Require(errors.Count > 0 && errors.All(row => row.Severity == "Error" && !string.IsNullOrWhiteSpace(row.Reason)),
            "Filtering retains the actual failure explanations rather than hiding historical errors");
        var scopedRun = errors.First(row => !string.IsNullOrEmpty(row.RunId) && !string.IsNullOrEmpty(row.PairId));
        var scopedLog = await MeasureAsync("Exact retained run: 500 newest rows", () =>
            db.LoadActivityProjectionPageAsync(500, query: new SyncActivityQuery(RunId: scopedRun.RunId,
                DetailedLog: true, NewestLogFirst: true)));
        Require(scopedLog.Count > 0 && scopedLog.All(row => row.RunId == scopedRun.RunId),
            "Run filtering keeps exact membership at the actual GUI page size");
        var operationIds = await MeasureAsync("Exact retained run operation membership for retry", () =>
            db.LoadRunOperationIdsAsync(scopedRun.RunId!, scopedRun.PairId));
        var expectedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT json_extract(detail_json,'$.OperationId')
                FROM history_event WHERE event_kind='presentation'
                  AND json_extract(detail_json,'$.RunId')=$run
                  AND json_extract(detail_json,'$.PairId')=$pair
                  AND coalesce(json_extract(detail_json,'$.OperationId'),'')<>'';
                """;
            command.Parameters.AddWithValue("$run", scopedRun.RunId!);
            command.Parameters.AddWithValue("$pair", scopedRun.PairId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) expectedOperationIds.Add(reader.GetString(0));
        }
        Require(expectedOperationIds.Count > 1 && expectedOperationIds.SetEquals(operationIds) &&
                operationIds.Distinct().Count() == operationIds.Count,
            "Retry preserves every exact operation identity in the retained real-profile run, without alias grouping collapse");
        var pending = await MeasureAsync("Pending operations page", () => db.LoadQueueProjectionPageAsync(251));
        Require(pending.Count == 251 && pending.Select(row => row.OperationId).Distinct().Count() == pending.Count,
            "Pending work remains independently paged by exact operation identity");
        Require(await HashAsync(source) == sourceHash && await HashAsync(path) == sourceHash,
            "All projection reads leave the retained authority, schema and history bytes unchanged");
        // A slow phase must fail the route, but must not hide the independent
        // cursor/filter/scope/pending proofs that follow it in the same batch.
        Require(slowPhases.Count == 0, "Every phase meets the unchanged twenty-second bound: " +
            string.Join("; ", slowPhases));
        Console.WriteLine("PASS retained activity scaling: real production read paths, no provider or live-profile effects");
    }

    // No database clone, initialization, migration, provider or product mutation.
    // Measure the actual retained store through the production page reader. A
    // test-only query_only/native deadline bounds this read-only diagnostic;
    // it does not replace attribution, filtering, pagination or materialization.
    internal static async Task ReadCurrentAsync(string path, string runId, bool verifyHealth = true)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Supply the exact existing database and retained run identity.");
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var db = new SyncAuthorityDatabase(path);
        var health = Stopwatch.StartNew();
        if (verifyHealth)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await db.VerifyHealthAsync(deadline.Token, detail =>
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    { Phase = "retained-database-health", Detail = detail, ElapsedSeconds = health.Elapsed.TotalSeconds })));
            Console.WriteLine($"MEASURE current retained health: {health.Elapsed.TotalSeconds:F3} seconds; cache state not claimed cold");
        }
        else Console.WriteLine("SCOPE read-only production activity pages only; startup/command health admission is NOT tested or claimed healthy.");

        var failures = new List<string>();
        async Task<IReadOnlyList<SyncAuthorityActivityProjection>> PageAsync(string label, int limit,
            SyncActivityQuery query, string filter = "All")
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            SyncAuthorityDatabase.ActivityReadConnectionForTest = connection =>
            {
                Require(SQLitePCL.raw.sqlite3_db_readonly(connection.Handle!, "main") == 1,
                    "The actual production activity page uses a native read-only database connection");
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA query_only=ON;";
                command.ExecuteNonQuery();
                SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 1000,
                    static state => ((CancellationToken)state!).IsCancellationRequested ? 1 : 0, deadline.Token);
            };
            var started = Stopwatch.StartNew();
            Console.WriteLine($"BEGIN {label}; exact production projection, limit {limit}, 20-second native read-only diagnostic bound");
            try
            {
                var rows = await db.LoadActivityProjectionPageAsync(limit, filter: filter,
                    cancellationToken: deadline.Token, query: query);
                if (deadline.IsCancellationRequested) failures.Add(label + ": exceeded20s");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    { Phase = label, ElapsedSeconds = started.Elapsed.TotalSeconds, Count = rows.Count,
                        FirstSequence = rows.FirstOrDefault()?.Sequence, LastSequence = rows.LastOrDefault()?.Sequence }));
                Require(rows.Select(row => row.EventId).Distinct().Count() == rows.Count,
                    label + " retains distinct exact event identities");
                return rows;
            }
            catch (Exception error) when (deadline.IsCancellationRequested &&
                (error is OperationCanceledException || error is SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_INTERRUPT }))
            {
                failures.Add(label + ": exceeded20s");
                Console.WriteLine($"FAIL {label}: native read-only deadline; elapsed {started.Elapsed.TotalSeconds:F3}s; no provider/effect operation issued");
                return [];
            }
            finally { SyncAuthorityDatabase.ActivityReadConnectionForTest = null; }
        }
        var newest = await PageAsync("current detailed log", 200,
            new SyncActivityQuery(DetailedLog: true, NewestLogFirst: true));
        if (newest.Count > 0)
        {
            Require(newest.All(row => row.Sequence is not null) && newest.Select(row => row.Sequence)
                    .SequenceEqual(newest.Select(row => row.Sequence).OrderDescending()),
                "Current detailed page preserves newest-first durable cursors");
            var earlier = await PageAsync("current earlier detailed log", 200,
                new SyncActivityQuery(DetailedLog: true, NewestLogFirst: true,
                    ThroughSequence: newest[^1].Sequence - 1));
            Require(!earlier.Select(row => row.EventId).Intersect(newest.Select(row => row.EventId)).Any(),
                "Current backward cursor does not overlap or replay events");
        }
        var run = await PageAsync("current exact Child run log", 500,
            new SyncActivityQuery(RunId: runId, DetailedLog: true, NewestLogFirst: true));
        Require(run.All(row => row.RunId == runId), "Current run log contains only the exact retained Child run");
        var files = await PageAsync("current Home file activity", 40,
            new SyncActivityQuery([SyncActivityCategories.Files]), "Changes");
        Require(files.All(row => row.Categories?.Contains(SyncActivityCategories.Files) == true),
            "Current Home slice preserves file-only attribution before pagination");
        var errors = await PageAsync("current retained errors", 200,
            new SyncActivityQuery(Severity: "Error", DetailedLog: true, NewestLogFirst: true));
        Require(errors.All(row => row.Severity == "Error" && !string.IsNullOrWhiteSpace(row.Reason)),
            "Current error filter preserves exact explanations");
        var after = new FileInfo(path);
        Require(after.Length == length && after.LastWriteTimeUtc == modified,
            "Retained database size and modification time unchanged; this is not a byte-hash claim");
        Require(failures.Count == 0 && newest.Count > 0 && run.Count > 0,
            "Every current retained projection meets the unchanged20s bound with actual retained output: " +
            string.Join("; ", failures));
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }
}
