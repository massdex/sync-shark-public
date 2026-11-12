using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static partial class CallBudgetChecks
{
    /// <summary>Isolates real Dropbox listing decoding and spelling with generated, paged HTTP responses.</summary>
    public static void RunReadProviderSpeed()
    {
        var divisor = decimal.Parse(Environment.GetEnvironmentVariable("READSPEED_DIVISOR") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        Check.Require(divisor >= 1, "positive workload divisor");
        var shape = new Shape("readspeed", (int)(945515 / divisor), 196);
        var deleted = (int)(2643129 / divisor);
        var delayMs = int.Parse(Environment.GetEnvironmentVariable("READSPEED_PAGE_DELAY_MS") ?? "0");
        Check.Require(delayMs >= 0, "nonnegative modeled page latency");
        var knownParents = Environment.GetEnvironmentVariable("READSPEED_KNOWN_HISTORY_PARENTS") == "1";
        using var handler = new ReadPages(shape, deleted, delayMs, knownParents);
        using var http = new HttpClient(handler);
        var network = new FakeNetworkSignal();
        network.SetAvailable(true);
        var retry = new FailureRetryOwner.BackoffSettings(0, 1, 0, 0);
        var limiter = new AdaptiveLimiter(1, 1, TimeProvider.System, network, _ => { }, retry);
        var api = new DropboxApi(http, new ReadTokens(), "fixture", limiter,
            _ => new ValueTask<Stream>(new MemoryStream()), retry, TimeProvider.System,
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var save = Environment.GetEnvironmentVariable("READSPEED_PIPELINE") == "1";
        var folder = Path.Combine(Path.GetTempPath(), "readprovider-" + Guid.NewGuid().ToString("N"));
        if (save) Directory.CreateDirectory(folder);
        try
        {
            using var store = save ? StateStore.Open(Path.Combine(folder, "state.db")) : null;
            var pair = SourceFixture.Bound(new PairConfig("readspeed", "readspeed", folder, "/Fixture", "fixture",
                [], [], FirstRunPolicy.NewestWins, true, null));
            IScanSink? sink = null;
            Microsoft.Data.Sqlite.SqliteConnection? scanConnection = null;
            var profile = Environment.GetEnvironmentVariable("READSPEED_PROFILE") == "1";
            var sqlCosts = new Dictionary<string, (long Calls, long Nanoseconds, long CacheMisses)>();
            var previousMisses = 0;
            long peakManagedHeap = 0;
            var firstDeleteInBatch = true;
            Func<int>? queuedCount = null;
            if (store is not null)
            {
                store.SavePair(pair);
                SourceFixture.AcquireJournal(store, pair);
                store.BeginScan(pair.PairId, store.ReadPairExecution(pair.PairId)!.Value.Generation, includeDeleted: true);
                scanConnection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
                if (Environment.GetEnvironmentVariable("READSPEED_EXPLAIN") == "1")
                {
                    foreach (var sql in new[] { "SELECT sqlite_version();", "PRAGMA scan_state.page_size;", "PRAGMA scan_state.cache_size;",
                        "EXPLAIN QUERY PLAN " + StateSchema.ScanDeleteSelf, "EXPLAIN QUERY PLAN " + StateSchema.ScanDeleteDescendants })
                    {
                        using var command = scanConnection.CreateCommand();
                        command.CommandText = sql;
                        command.Parameters.AddWithValue(StateSchema.ParamPair, pair.PairId);
                        command.Parameters.AddWithValue(StateSchema.ParamSide, (int)Side.Dropbox);
                        command.Parameters.AddWithValue(StateSchema.ParamTargetKey, PathNames.Key("Fixture/Child"));
                        using var rows = command.ExecuteReader();
                        while (rows.Read())
                            Console.WriteLine("READPLAN " + sql + " => " + string.Join(" | ", Enumerable.Range(0, rows.FieldCount).Select(rows.GetValue)));
                    }
                }
                if (profile)
                {
                    SQLitePCL.raw.sqlite3_db_status(scanConnection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1);
                    SQLitePCL.raw.sqlite3_db_status(scanConnection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_WRITE, out _, out _, 1);
                    SQLitePCL.raw.sqlite3_profile(scanConnection.Handle, (SQLitePCL.delegate_profile)((state, text, elapsed) =>
                    {
                        var sql = text.utf8_to_string();
                        sqlCosts.TryGetValue(sql, out var before);
                        SQLitePCL.raw.sqlite3_db_status(scanConnection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out var misses, out _, 0);
                        sqlCosts[sql] = (before.Calls + 1, before.Nanoseconds + (long)elapsed, before.CacheMisses + misses - previousMisses);
                        previousMisses = misses;
                        if (sql.StartsWith("BEGIN", StringComparison.Ordinal)) firstDeleteInBatch = true;
                        if (sql == "COMMIT;" || (firstDeleteInBatch && sql == StateSchema.ScanDeleteSelf))
                        {
                            SampleHeap();
                            if (sql == StateSchema.ScanDeleteSelf) firstDeleteInBatch = false;
                        }
                    }), null);
                }
                sink = store.OpenScan(pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows);
                var channel = (System.Threading.Channels.Channel<Listed>)typeof(ScanWriter).GetField("_queued",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sink)!;
                // The single-reader channel does not expose Count; inspect its queue only in this cost probe.
                var items = channel.GetType().GetField("_items",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
                queuedCount = items.GetType().GetProperty("Count")!.GetMethod!.CreateDelegate<Func<int>>(items);
                Console.WriteLine("READPROVIDER fixture=" + folder);
            }
            var listing = new DropboxListing(api, pair, new PathScope(pair, [pair]));
            var context = new ListingReadContext(() => new RequestAttempt(new object(), AttemptOwner.Request),
                (_, _) => Task.FromResult(false), () => ValueTask.CompletedTask);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = handler.Timeline;
            timer.Start();
            long seen = 0;
            long tombstones = 0;
            var peakQueued = 0;
            long peakWaiting = 0;
            long heapAtPeakWaiting = 0;
            handler.BeforePage = () =>
            {
                SampleHeap();
                peakQueued = Math.Max(peakQueued, queuedCount?.Invoke() ?? 0);
                // At the next fetch, the previous page is decoded; unyielded entries remain in Spelling.
                var waiting = handler.GeneratedEntries - seen;
                if (waiting <= peakWaiting) return;
                peakWaiting = waiting;
                heapAtPeakWaiting = GC.GetTotalMemory(false);
            };
            var producedMs = 0d;
            var queuedAtEnd = 0;
            try
            {
                Consume().GetAwaiter().GetResult();
                producedMs = timer.Elapsed.TotalMilliseconds;
                queuedAtEnd = queuedCount?.Invoke() ?? 0;
                peakQueued = Math.Max(peakQueued, queuedAtEnd);
            }
            finally { sink?.CompleteAsync().GetAwaiter().GetResult(); }
            var completeMs = timer.Elapsed.TotalMilliseconds;
            if (profile && scanConnection is not null)
            {
                SQLitePCL.raw.sqlite3_profile(scanConnection.Handle, (SQLitePCL.delegate_profile)null!, null);
                SQLitePCL.raw.sqlite3_db_status(scanConnection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out var misses, out _, 0);
                SQLitePCL.raw.sqlite3_db_status(scanConnection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_WRITE, out var writes, out _, 0);
                Console.WriteLine($"READPROVIDER cache_misses={misses} cache_page_writes={writes}");
                foreach (var (sql, cost) in sqlCosts)
                    Console.WriteLine($"READPROVIDER sql_calls={cost.Calls} native_ms={cost.Nanoseconds/1e6:F3} cache_misses={cost.CacheMisses} sql={sql}");
                SQLitePCL.raw.sqlite3_db_status(scanConnection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_USED, out var cacheUsed, out _, 0);
                Console.WriteLine($"READPROVIDER sqlite_cache_used={cacheUsed}");
            }
            Console.WriteLine($"READPROVIDER cpu_ms={(process.TotalProcessorTime-cpu).TotalMilliseconds:F3} peak_working_set={process.PeakWorkingSet64}");
            Check.Require(seen == shape.Items + deleted && tombstones == deleted,
                "the adapter preserves all generated live and deleted entries");
            Console.WriteLine($"READPROVIDER live={shape.Items} deleted={deleted} pages={handler.Pages} response_bytes={handler.Bytes} elapsed_ms={completeMs:F3} response_generation_ms={handler.GenerationMs:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true)-allocated}");
            Console.WriteLine($"READPROVIDER pipeline={save} modeled_page_delay_ms={delayMs} measured_delay_ms={handler.DelayMs:F3} produced_ms={producedMs:F3} final_drain_ms={completeMs-producedMs:F3} sampled_peak_channel={peakQueued} channel_at_producer_end={queuedAtEnd}");
            Console.WriteLine($"READPROVIDER known_history_parents={knownParents} last_http_response_ms={handler.LastResponseMs:F3} peak_waiting_entries={peakWaiting} process_managed_heap_at_peak_waiting={heapAtPeakWaiting}");
            Check.Require(peakWaiting == 0, "both history shapes stream tombstones without a spelling backlog");
            // Channel counts exclude the batch already removed by SaveLoopAsync for writing.
            if (store is not null)
            {
                var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
                using var verify = connection.CreateCommand();
                verify.CommandText = "SELECT COUNT(*) FROM scan_state.deleted_name;";
                Check.Require(Convert.ToInt64(verify.ExecuteScalar()) == deleted, "all delayed-provider deleted names are stored");
                verify.CommandText = "SELECT COUNT(*) FROM scan_state.scan;";
                var scanRows = Convert.ToInt64(verify.ExecuteScalar());
                Check.Require(scanRows == shape.Items, "each synthetic live entry has one scan_own index entry");
                Console.WriteLine($"READPROVIDER scan_own_rows={scanRows}");
            }
            SampleHeap();
            Console.WriteLine($"READPROVIDER sampled_managed_heap={peakManagedHeap} completed_managed_heap={GC.GetTotalMemory(false)} retained_after_completion_gc={GC.GetTotalMemory(true)}");

            void SampleHeap()
            {
                var heap = GC.GetTotalMemory(false);
                lock (sqlCosts) peakManagedHeap = Math.Max(peakManagedHeap, heap);
            }

            async Task Consume()
            {
                await foreach (var item in listing.ReadAll("", new ListOptions(true, false, IncludeDeleted: true), context, default))
                {
                    if (sink is { } queued) await queued.AddAsync(item, default);
                    seen++;
                    if (item.Entry is null) tombstones++;
                }
            }
        }
        finally
        {
            // Probe-only synthetic fixture: remove the directory this run created.
            if (save) Directory.Delete(folder, true);
        }
    }

    private sealed class ReadTokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-token");
    }

    private sealed class ReadPages(Shape shape, int deleted, int delayMs, bool knownParents) : HttpMessageHandler
    {
        private readonly IEnumerator<(string Path, string Kind)> _items = Items(shape, deleted, knownParents).GetEnumerator();
        public Stopwatch Timeline { get; } = new();
        public Action? BeforePage { get; set; }
        public long GeneratedEntries { get; private set; }
        public double LastResponseMs { get; private set; }
        public int Pages { get; private set; }
        public long Bytes { get; private set; }
        public double GenerationMs { get; private set; }
        public double DelayMs { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforePage?.Invoke();
            var timer = Stopwatch.StartNew();
            if (delayMs != 0) await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            DelayMs += timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var entries = new List<Dictionary<string, object>>();
            while (entries.Count < DropboxProtocol.ListPageMaximum && _items.MoveNext())
            {
                var item = _items.Current;
                var value = new Dictionary<string, object>
                {
                    [DropboxProtocol.Tag] = item.Kind,
                    [DropboxProtocol.PathDisplay] = "/Fixture/" + item.Path
                };
                if (item.Kind != DropboxProtocol.TagDeleted) value[DropboxProtocol.Id] = "id:" + item.Path;
                if (item.Kind == DropboxProtocol.TagFile)
                {
                    value[DropboxProtocol.Rev] = "rev";
                    value[DropboxProtocol.Size] = 1L;
                    value[DropboxProtocol.ClientModified] = "2026-01-01T00:00:00Z";
                    value[DropboxProtocol.ContentHash] = new string('a', 64);
                    value[DropboxProtocol.IsDownloadable] = true;
                }
                entries.Add(value);
            }
            Pages++;
            GeneratedEntries += entries.Count;
            // An empty terminal page exposes the complete pre-Flush spelling backlog to the probe.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { entries, cursor = "page-" + Pages,
                has_more = entries.Count != 0 });
            Bytes += bytes.Length;
            GenerationMs += timer.Elapsed.TotalMilliseconds;
            LastResponseMs = Timeline.Elapsed.TotalMilliseconds;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _items.Dispose();
            base.Dispose(disposing);
        }

        private static IEnumerable<(string, string)> Items(Shape shape, int deleted, bool knownParents)
        {
            foreach (var path in FolderPaths(shape)) yield return (path, DropboxProtocol.TagFolder);
            for (var index = 0; index < shape.Files; index++) yield return (PathOf(index, shape), DropboxProtocol.TagFile);
            for (var index = 0; index < deleted; index++)
                yield return (knownParents ? PathOf(index, shape) + ".deleted" :
                    PathNames.Child("Deleted", PathOf(index, shape)), DropboxProtocol.TagDeleted);
        }
    }
}
