namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // The user rule: logging and output at every stage. Live, a planning phase ran eighteen minutes
    // between the "comparison scope/join" line and the phase boundary line with no trace at all.
    // This reads the autosync .jsonl a real RunLog writes, so it proves the lines reach the run log.
    private static async Task PlanningSubStageTracesAsync()
    {
        var folder = FixtureFolder("planning-traces");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("traced.txt", "1");
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);
            fx.A.Seed("traced.txt", "2");
            string jsonPath;
            using (var log = RunLog.Open(folder, fx.Pair.PairId, RunLog.AutoSyncCommand, verbose: false))
            {
                Require(SpinUntil(() => log.FileAvailable, TimeSpan.FromSeconds(2)), "the planning log opens");
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                jsonPath = log.JsonPath!;
            }
            var lines = File.ReadAllLines(jsonPath);
            bool Has(params string[] parts) => lines.Any(line =>
                parts.All(part => line.Contains(part, StringComparison.Ordinal)));
            int IndexOf(params string[] parts) => Array.FindIndex(lines, line =>
                parts.All(part => line.Contains(part, StringComparison.Ordinal)));
            // A stall must name the stage in progress, so every stage logs its start before its done.
            void RequireStartThenDone(string what, string start, string done)
            {
                var startAt = IndexOf(start);
                var doneAt = IndexOf(done);
                Require(startAt >= 0, $"{what} reports its start in the run log");
                Require(doneAt >= 0, $"{what} reports its completion in the run log");
                Require(startAt < doneAt, $"{what}'s start comes before its completion");
            }
            Require(Has("\"kind\":\"trace\"", "planning decide: start;", "rows="),
                "the planning phase's first sub-stage reports its start in the run log");
            Require(Has("planning decide done:", "elapsed-ms=", "actions="),
                "the decide stage reports its end with elapsed time and counts");
            Require(Has("planning filter decide:", "actions=", "blocked="),
                "a safety-filter pass reports its action count");
            Require(Has("planning filter retries:") && Has("planning filter notices:"),
                "every safety-filter pass in the phase is traced, not just the first");
            RequireStartThenDone("the occupancy expansion", "planning occupancy: start;", "planning occupancy: elapsed-ms=");
            RequireStartThenDone("the decide safety filter", "planning filter decide: start;", "planning filter decide: elapsed-ms=");
            RequireStartThenDone("the retries safety filter", "planning filter retries: start;", "planning filter retries: elapsed-ms=");
            RequireStartThenDone("the notices safety filter", "planning filter notices: start;", "planning filter notices: elapsed-ms=");
            Require(Has("planning retries: start;") && Has("planning retries done:", "elapsed-ms="),
                "the reviewed-retry stage reports its start and end");
            Require(Has("planning settle: start;", "capacity=") || Has("planning settle: skipped;"),
                "the settle lane reports whether it runs, with its start counts");
            Require(!Has("planning settle: start;") || Has("planning settle done:", "candidates="),
                "a running settle lane reports its end with its counts");
        }
        finally { DeleteFixture(folder); }
        Console.WriteLine("PASS PlanningSubStageTraces");
    }
}
