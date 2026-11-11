using System.Diagnostics;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static partial class CallBudgetChecks
{
    /// <summary>Measures first-scan storage with the existing cost fixture's path distribution and synthetic listings.</summary>
    public static void RunReadSpeed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "readspeed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Console.WriteLine("READSPEED fixture=" + folder);
            using var store = StateStore.Open(Path.Combine(folder, "state.db"));
            var pair = SourceFixture.Bound(new PairConfig("readspeed", "readspeed", folder, "/Fixture",
                "fixture", [], [], FirstRunPolicy.NewestWins, true, null));
            store.SavePair(pair);
            SourceFixture.AcquireJournal(store, pair);
            var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
            store.BeginScan(pair.PairId, generation, includeDeleted: true);
            var divisor = int.Parse(Environment.GetEnvironmentVariable("READSPEED_DIVISOR") ?? "10");
            Check.Require(divisor >= 1, "positive workload divisor");
            var shape = new Shape("readspeed", 964796 / divisor, 196);
            using var process = Process.GetCurrentProcess();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var cpu = process.TotalProcessorTime;
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var statements = new Dictionary<string, (long Count, long Nanoseconds)>();
            Console.WriteLine($"READSPEED divisor={divisor} profile={Environment.GetEnvironmentVariable("READSPEED_PROFILE") ?? "off"}");
            if (Environment.GetEnvironmentVariable("READSPEED_PROFILE") == "1")
            SQLitePCL.raw.sqlite3_profile(connection.Handle, (SQLitePCL.delegate_profile)((_, text, elapsed) =>
            {
                var sql = text.utf8_to_string();
                var group = sql.StartsWith("INSERT INTO scan_state.deleted_name", StringComparison.Ordinal) ? "deleted-name" :
                    sql.StartsWith("DELETE FROM scan_state.scan", StringComparison.Ordinal) ?
                        sql.Contains("RETURNING", StringComparison.Ordinal) ? "delete-self" : "delete-descendants" :
                    sql.StartsWith("INSERT", StringComparison.Ordinal) ? "upsert" : sql.Split(' ')[0];
                statements.TryGetValue(group, out var before);
                statements[group] = (before.Count + 1, before.Nanoseconds + (long)elapsed);
            }), null);
            SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
            SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_WRITE, out _, out _, 1);
            var total = Stopwatch.StartNew();
            ReadSide(Side.Local, 964796 / divisor, 0);
            ReadSide(Side.Dropbox, 945515 / divisor, 2643129 / divisor);
            Console.WriteLine($"READSPEED total_ms={total.Elapsed.TotalMilliseconds:F3} cpu_ms={(process.TotalProcessorTime-cpu).TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true)-allocated} peak_working_set={process.PeakWorkingSet64}");

            SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out var misses, out _, 0);
            SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_WRITE, out var writes, out _, 0);
            Console.WriteLine($"READSPEED cache_misses={misses} cache_page_writes={writes} fixture_bytes={Directory.EnumerateFiles(folder).Sum(path => new FileInfo(path).Length)}");
            SQLitePCL.raw.sqlite3_profile(connection.Handle, (SQLitePCL.delegate_profile)null!, null);
            foreach (var entry in statements)
                Console.WriteLine($"READSPEED sql={entry.Key} executions={entry.Value.Count} profile_ms={entry.Value.Nanoseconds / 1e6:F3}");
            using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT path_key FROM scan_state.deleted_name ORDER BY pair_id,path_key;";
                using var rows = verify.ExecuteReader();
                using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                long names = 0;
                while (rows.Read())
                {
                    digest.AppendData(System.Text.Encoding.UTF8.GetBytes(rows.GetString(0) + "\n"));
                    names++;
                }
                Check.Require(names == 2643129 / divisor, "every distinct deleted name remains stored");
                Console.WriteLine($"READSPEED verified_names={names} digest={Convert.ToHexString(digest.GetHashAndReset())}");
            }

            void ReadSide(Side side, int live, int deleted)
            {
                var timer = Stopwatch.StartNew();
                var sink = store.OpenScan(pair.PairId, side, ScanTestSettings.BatchRows);
                for (var index = 0; index < live; index++)
                {
                    var path = PathOf(index, shape);
                    sink.AddAsync(new Listed(path, new Entry(path, false, 1, BaseTime,
                        "id-" + index, "rev", "", null)), default).GetAwaiter().GetResult();
                }
                var producedLive = timer.Elapsed.TotalMilliseconds;
                for (var index = 0; index < deleted; index++)
                    sink.AddAsync(new Listed(PathNames.Child("Deleted", PathOf(index, shape)), null), default)
                        .GetAwaiter().GetResult();
                var produced = timer.Elapsed.TotalMilliseconds;
                sink.CompleteAsync().GetAwaiter().GetResult();
                Console.WriteLine($"READSPEED side={side} live={live} deleted={deleted} live_produced_ms={producedLive:F3} all_produced_ms={produced:F3} complete_ms={timer.Elapsed.TotalMilliseconds:F3}");
            }
        }
        finally
        {
            // Probe-only synthetic fixture: remove the directory this run created.
            Directory.Delete(folder, true);
        }
    }
}
