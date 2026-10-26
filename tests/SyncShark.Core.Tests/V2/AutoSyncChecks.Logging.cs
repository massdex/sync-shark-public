namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    // d2: every automatic run writes one bounded summary line naming its wake cause, changed paths, actions by kind
    // and outcome, beside the existing per-run progress trace.
    private static async Task RunLogNamesWakeAndResultAsync()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT") ?? Path.GetTempPath(),
            "autosync-result-" + Guid.NewGuid().ToString("N"));
        var logs = new System.Collections.Concurrent.ConcurrentQueue<RunLog>();
        await using (var rig = new Rig(["p1"], beginRunLog: (pair, cause) =>
        {
            var log = RunLog.Open(root, pair, RunLog.AutoSyncCommand, false);
            log.Summary("wake-cause=" + cause);
            logs.Enqueue(log);
            return log;
        }))
        {
            rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with { Uploaded = 2, Downloaded = 1 });
            await rig.StartAsync();
            await rig.ParkedAsync(AutoStatus.Idle);
        }
        var captured = logs.Single();
        await captured.Completion;
        var text = File.ReadAllText(captured.Path!);
        Require(text.Contains("wake-cause=startup", StringComparison.Ordinal) &&
            text.Contains("outcome=" + RunOutcomes.Complete, StringComparison.Ordinal) &&
            text.Contains("uploaded=2", StringComparison.Ordinal) &&
            text.Contains("downloaded=1", StringComparison.Ordinal) &&
            text.Contains("changed=3", StringComparison.Ordinal),
            "the automatic run log names its wake cause, changed paths, actions by kind and outcome");
        Pass("RunLogNamesWakeAndResult");
    }
}
