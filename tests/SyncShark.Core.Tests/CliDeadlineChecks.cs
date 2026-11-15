using System.Diagnostics;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Input work is delayed; parsing, dispatch, deadline ownership, exit mapping and cleanup
// are the production CLI. No provider access or user product state is opened here.
internal static class CliDeadlineChecks
{
    internal static async Task RunAsync()
    {
        foreach (var behavior in new[] { "retry-only", "retry-conflict", "retry-blocked" })
        {
            var runtime = new DeadlineRuntime(behavior);
            var result = await Execute(["--cli", "run", "--pair", "fixture", "--yes", "--json"], () => runtime);
            // E2 / run.md Terminal boundary decisions: live Partial means a completed
            // attempt, not automatic manual review. Preserve each unresolved counter.
            Console.WriteLine($"CLI partial {behavior}: exit={result.Exit}, error={result.Error}, data={result.Data}");
            Require(result.Exit == 0 && result.Error is null &&
                result.Data.GetProperty("outcome").GetString() == "partial" &&
                result.Data.GetProperty("failed").GetInt32() == 1 &&
                result.Data.GetProperty("conflicts").GetInt32() == (behavior == "retry-conflict" ? 1 : 0) &&
                result.Data.GetProperty("blocked").GetInt32() == (behavior == "retry-blocked" ? 1 : 0),
                "live partial completes without review while retaining failed, conflict and blocked counts");
        }
        var factoryCalls = 0;
        ISyncCliRuntime NeverOpen()
        {
            factoryCalls++;
            throw new InvalidOperationException("Invalid deadline opened a runtime.");
        }
        foreach (var value in new[] { "0", "-1", "0.0009", "86401", "NaN", "Infinity", "1e999", "bad", "1,2" })
        {
            var result = await Execute(["--cli", "run", "--pair", "fixture", "--yes", "--json",
                "--timeout-seconds", value], NeverOpen);
            Require(result.Exit == 2 && result.Error == "usage", "reject invalid deadline " + value);
        }
        foreach (var args in new[]
        {
            new[] { "--cli", "run", "--pair", "fixture", "--yes", "--json", "--timeout-seconds" },
            new[] { "--cli", "run", "--pair", "fixture", "--yes", "--json", "--timeout-seconds", "1", "--timeout-seconds", "2" },
            new[] { "--cli", "status", "--json", "--timeout-seconds", "1" },
            new[] { "--cli", "help", "--json", "--timeout-seconds", "1" }
        })
            Require((await Execute(args, NeverOpen)).Exit == 2, "reject missing/duplicate/unsupported deadline");
        Require(factoryCalls == 0, "invalid options cannot open state");

        var supported = new HashSet<string> { "scan", "first-sync prepare", "first-sync apply", "first-sync repair-moves",
            "run", "run batch", "run retry", "run verify", "index run", "conflicts resolve", "conflicts resolve-selection" };
        foreach (var row in SyncCliRegistry.Rows)
            Require(row.Options.Split(',').Contains("--timeout-seconds") == supported.Contains(row.Command),
                "deadline registry scope: " + row.Command);
        Require(SyncCli.HelpText().Contains("--timeout-seconds", StringComparison.Ordinal), "help exposes deadline");
        var indexDeadline = SyncCliRegistry.Parse(["--cli", "index", "run", "--root", "r00000000000000aa",
            "--yes", "--timeout-seconds", "30"]);
        Require(indexDeadline.Command == "index run" && indexDeadline.Value("--timeout-seconds") == "30",
            "independent indexing accepts the explicit command deadline without opening state");

        foreach (var bounded in new[] { false, true })
        {
            var runtime = new DeadlineRuntime("success");
            var args = new List<string> { "--cli", "run", "--pair", "fixture", "--yes", "--json" };
            if (bounded) args.AddRange(["--timeout-seconds", "30"]);
            var result = await Execute(args.ToArray(), () => runtime);
            Require(result.Exit == 0 && runtime.Dispatches == 1 && runtime.DisposeCount == 1 && runtime.Drained,
                "successful bounded/unbounded work completes cleanup once");
        }
        foreach (var behavior in new[] { "throw", "stopped-result", "translated-error" })
        {
            var runtime = new DeadlineRuntime(behavior);
            var watch = Stopwatch.StartNew();
            var result = await Execute(["--cli", "run", "--pair", "fixture", "--yes", "--json",
                "--timeout-seconds", "0.1"], () => runtime);
            Require(result.Exit == 130 && result.Error == "timed_out", "expired " + behavior + " is never success");
            if (behavior != "throw")
                Require(result.Data.ValueKind == JsonValueKind.Object &&
                    result.Data.GetProperty("uploaded").GetInt32() == 1 &&
                    result.Data.GetProperty("stopped").GetBoolean(),
                    "Timeout must preserve the actual partial result for " + behavior);
            Require(runtime.Dispatches == 1 && runtime.CancellationObserved && runtime.CompletedFiles == 1 &&
                    runtime.DisposeCount == 1 && runtime.Drained && watch.Elapsed < TimeSpan.FromSeconds(5),
                "expiry reaches actual work, retains completed input and awaits safe drain without retry");
            Require(result.Progress.Contains("Command deadline: 0.1 seconds", StringComparison.Ordinal),
                "deadline is announced in timestamped CLI progress");
        }
        foreach (var behavior in new[] { "returned-stop", "returned-review" })
        {
            var runtime = new DeadlineRuntime(behavior);
            var result = await Execute(["--cli", "run", "--pair", "fixture", "--yes", "--json"], () => runtime);
            Require(result.Exit == (behavior == "returned-stop" ? 130 : 5) &&
                result.Error == (behavior == "returned-stop" ? "canceled" : "review_required") &&
                result.Data.ValueKind == JsonValueKind.Object && result.Data.GetProperty("uploaded").GetInt32() == 1 &&
                runtime.DisposeCount == 1 && runtime.Drained,
                "Ordinary stopped/review results retain completed totals, not only timed-out commands.");
        }
        using var external = new CancellationTokenSource();
        external.Cancel();
        var preCanceled = await Execute(["--cli", "run", "--pair", "fixture", "--yes", "--json",
            "--timeout-seconds", "30"], NeverOpen, external.Token);
        Require(preCanceled.Exit == 130 && preCanceled.Error == "canceled" && factoryCalls == 0,
            "external cancellation is distinguished and prevents runtime admission");
        await CheckNativeReadCancellationAsync();
        Console.WriteLine("PASS CLI deadline success, invalid input, safe timeout, stopped-result, native SQLite cancellation and no-retry checks");
    }

    private static async Task CheckNativeReadCancellationAsync()
    {
        SQLitePCL.Batteries_V2.Init();
        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await connection.OpenAsync();
        await using var slow = connection.CreateCommand();
        slow.CommandText = "WITH RECURSIVE n(x) AS (VALUES(start_deadline()) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n;";
        using var deadline = new CancellationTokenSource();
        var watchdogUsed = 0;
        var enteredSql = false;
        // Test containment only: a broken native hook must fail within seconds, not
        // leave a billion-row fixture running. This watchdog cannot satisfy the proof.
        await using var watchdog = new Timer(_ =>
        {
            Interlocked.Exchange(ref watchdogUsed, 1);
            SQLitePCL.raw.sqlite3_interrupt(connection.Handle!);
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        connection.CreateFunction("start_deadline", () =>
        {
            enteredSql = true;
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            watchdog.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            return 0;
        });
        var watch = Stopwatch.StartNew();
        var cancelled = false;
        try
        {
            await Task.Run(() => SyncAuthorityDatabase.ReadWithNativeCancellationAsync(connection,
                () => slow.ExecuteScalarAsync(CancellationToken.None), deadline.Token));
        }
        catch (OperationCanceledException error) when (error.CancellationToken == deadline.Token)
        {
            cancelled = true;
        }
        await watchdog.DisposeAsync();
        Require(enteredSql && cancelled && Volatile.Read(ref watchdogUsed) == 0 && watch.Elapsed < TimeSpan.FromSeconds(2),
            "the production native callback interrupts actual SQLite work, not merely a delayed fixture");
        await using var subsequent = connection.CreateCommand();
        subsequent.CommandText = "WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<2000) SELECT sum(x) FROM n;";
        Require(Convert.ToInt64(await subsequent.ExecuteScalarAsync()) == 2001000,
            "cancelled native callback is removed before the next read");
        Require(Convert.ToInt64(await SyncAuthorityDatabase.ReadWithNativeCancellationAsync(connection,
            () => subsequent.ExecuteScalarAsync(), CancellationToken.None)) == 2001000,
            "uncancelled real SQLite reads retain their exact result");
        var entered = false;
        try
        {
            await SyncAuthorityDatabase.ReadWithNativeCancellationAsync(connection, () =>
            {
                entered = true;
                return subsequent.ExecuteScalarAsync();
            }, deadline.Token);
            throw new InvalidOperationException("Pre-cancelled SQLite read was accepted.");
        }
        catch (OperationCanceledException) { }
        Require(!entered, "pre-cancelled reads never execute SQL");
        using var valid = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        subsequent.CommandText = "SELECT missing_column;";
        var rejected = false;
        try
        {
            await SyncAuthorityDatabase.ReadWithNativeCancellationAsync(connection,
                () => subsequent.ExecuteScalarAsync(), valid.Token);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == SQLitePCL.raw.SQLITE_ERROR)
        {
            rejected = true;
        }
        Require(rejected, "real SQLite errors are not hidden as cancellation");
    }

    private static async Task<(int Exit, string? Error, string Progress, JsonElement Data)> Execute(string[] args,
        Func<ISyncCliRuntime> factory, CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        using var progress = new StringWriter();
        var exit = await SyncCli.ExecuteAsync(args, output, progress, factory, cancellationToken);
        using var json = JsonDocument.Parse(output.ToString());
        var error = json.RootElement.GetProperty("error");
        return (exit, error.ValueKind == JsonValueKind.Null ? null : error.GetProperty("code").GetString(),
            progress.ToString(), json.RootElement.GetProperty("data").Clone());
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("CLI deadline check failed: " + label);
    }

    private sealed class DeadlineRuntime(string behavior) : SyncCliOperationsBase, ISyncCliRuntime
    {
        protected override SyncAuthorityRuntimeHost Host => throw new InvalidOperationException("No host needed.");
        protected override PilotStateAuthority StateAuthority => throw new InvalidOperationException("No state needed.");
        public override string Mode => "fixture";
        internal int Dispatches { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int CompletedFiles { get; private set; }
        internal bool CancellationObserved { get; private set; }
        internal bool Drained { get; private set; }

        async Task<SyncCliRunData> ISyncCliRuntime.RunPairAsync(string pairId, Action<string> progress,
            CancellationToken cancellationToken)
        {
            Dispatches++;
            CompletedFiles = 1;
            if (behavior.StartsWith("retry-", StringComparison.Ordinal))
                return new(pairId, "Fixture", 1, 0, 0, behavior == "retry-conflict" ? 1 : 0,
                    behavior == "retry-blocked" ? 1 : 0, false)
                { Failed = 1, Outcome = "partial" };
            if (behavior == "success") return new(pairId, "Fixture", 1, 0, 0, 0, 0, false);
            if (behavior == "returned-stop") return new(pairId, "Fixture", 1, 0, 0, 0, 0, true);
            if (behavior == "returned-review") return new(pairId, "Fixture", 1, 0, 0, 1, 0, false);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                if (behavior == "throw") throw;
                if (behavior == "translated-error") throw new SyncCliException("operation_failed",
                    "Fixture work stopped.", SyncCliExitCode.Failed,
                    data: new SyncCliRunData(pairId, "Fixture", 1, 0, 0, 0, 0, true));
            }
            return new(pairId, "Fixture", 1, 0, 0, 0, 0, true);
        }

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await Task.Delay(25);
            Drained = true;
        }
    }
}
