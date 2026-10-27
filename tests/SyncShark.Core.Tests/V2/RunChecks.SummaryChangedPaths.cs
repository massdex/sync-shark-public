namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // M2: an automatic summary must not sell completed effects as observed changes. One changed path whose upload
    // failed is still one changed path, while the effect counters report zero.
    private static async Task SummaryNamesChangedPathsAsync()
    {
        var fx = Build();
        fx.A.Seed("same.txt", "same");
        fx.B.Seed("same.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("new.txt", "made later");
        fx.B.FailNextWrites = 1;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Failed == 1 && report.Uploaded == 0,
            $"one injected upload failure leaves no completed effect (failed {report.Failed}, uploaded {report.Uploaded})");
        Require(report.ChangedPaths == 1,
            "the run reports its one observed changed path, not the zero completed effects: " + report.ChangedPaths);
        var line = RunLog.RunSummaryLine(report);
        Require(line.Contains("effects=0", StringComparison.Ordinal) &&
            line.Contains("changedPaths=1", StringComparison.Ordinal),
            "the summary distinguishes completed effects from changed paths: " + line);
    }
}
