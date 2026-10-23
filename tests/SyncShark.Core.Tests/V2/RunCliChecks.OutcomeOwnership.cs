namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    internal static async Task OutcomeOwnershipChecksAsync()
    {
        var fx = Build();
        fx.A.Seed("both.txt", "same");
        fx.B.Seed("both.txt", "same");
        await FirstSyncAsync(fx);
        fx.A.Seed("both.txt", "version A");
        fx.B.Seed("both.txt", "version B");
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider),
            Path.Combine(Path.GetTempPath(), "outcome-cli"));
        var result = (SyncCliRunData)await DispatchAsync(runtime, ["--cli", "run", "--pair", fx.Pair.PairId, "--yes"]);
        Require(result.Outcome == RunOutcomes.Complete && result.Conflicts == 2,
            "successful keep-both copies finish CLI dispatch without review_required");
        Require(result.Pending == 0 && result.Deferred == 0,
            "CLI presents the report's pending and deferred counts explicitly");
        SyncCliRegistry.CheckRunOutcome(RunOutcomes.Partial, new { Failed = 5, Pending = 5 });
        foreach (var (outcome, code) in new[]
        {
            (RunOutcomes.Paused, "paused"), (RunOutcomes.Stopped, "canceled"), (RunOutcomes.Failed, "run_failed"),
            (RunOutcomes.PlanBoundExceeded, "plan_bound_exceeded")
        })
        {
            SyncCliException? error = null;
            try { SyncCliRegistry.CheckRunOutcome(outcome, new { Outcome = outcome }); }
            catch (SyncCliException caught) { error = caught; }
            Require(error?.Code == code, "one CLI mapping preserves " + outcome);
        }
        Console.WriteLine("PASS CLI outcome ownership");
    }
}
