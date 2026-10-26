namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    // d5: the v2 `runs list` surface reads the retained summaries and names each run's kind and status, so a
    // reconciliation run (bounded `reconcile-` run id) is distinguishable from an automatic sync run.
    private static async Task Cli_RunsList_ShowsReconciliationKindAsync()
    {
        var fx = Build();
        await FirstSyncAsync(fx);
        var report = new RunReport(fx.Pair.PairId, RunOutcomes.Complete, 0, 0, 0, 0, 0, 0, 0, 0, []);
        fx.Store.RecordRunSummary(RunSummary.Of("reconcile-" + Guid.NewGuid().ToString("N"), report,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow));
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var runs = await runtime.LoadRunsAsync(5, CancellationToken.None);
        Require(runs.Count >= 1 && runs.Runs[0].Kind == RunSummaryReconciliationKind &&
            runs.Runs[0].Status == "completed",
            "runs list exposes the v2 run kind and status");
        Console.WriteLine("PASS Cli_RunsList_ShowsReconciliationKind");
    }

    private const string RunSummaryReconciliationKind = "reconciliation";
}
