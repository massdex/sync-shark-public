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
            Require(Has("\"kind\":\"trace\"", "planning decide: start;", "rows="),
                "the planning phase's first sub-stage reports its start in the run log");
            Require(Has("planning decide done:", "elapsed-ms=", "actions="),
                "the decide stage reports its end with elapsed time and counts");
            Require(Has("planning filter decide:", "actions=", "blocked="),
                "a safety-filter pass reports its action count");
            Require(Has("planning filter retries:") && Has("planning filter notices:"),
                "every safety-filter pass in the phase is traced, not just the first");
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
