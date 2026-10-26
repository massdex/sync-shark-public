namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    // d2: every automatic run writes one bounded summary line naming its wake cause, completed effects, observed
    // changed paths (when its owner knows them), actions by kind and outcome, beside the existing per-run progress trace.
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
            text.Contains("effects=3", StringComparison.Ordinal),
            "the automatic run log names its wake cause, completed effects, actions by kind and outcome");
        Pass("RunLogNamesWakeAndResult");
    }

    // M1: the run log's scope must outlive the failure handlers, or the failure summary is written after disposal.
    private static async Task FailedRunKeepsSummaryAsync()
    {
        var root = Root("autosync-failure-summary");
        var logs = new System.Collections.Concurrent.ConcurrentQueue<RunLog>();
        await using (var rig = new Rig(["p1"], beginRunLog: (pair, _) =>
        {
            var log = RunLog.Open(root, pair, RunLog.AutoSyncCommand, false);
            logs.Enqueue(log);
            return log;
        }))
        {
            rig.Run.Then(new IOException("synthetic run failure"));
            await rig.StartAsync();
            await Until(() => logs.Count == 1, "the throwing run opened its log");
        }
        await RequireFailureSummaryAsync(logs, "synthetic run failure", "FailedRunKeepsSummary");
    }

    // M1: a failure before the incremental run starts (the monitor pass) takes the same path.
    private static async Task MonitorFailureKeepsSummaryAsync()
    {
        var root = Root("autosync-monitor-failure-summary");
        var logs = new System.Collections.Concurrent.ConcurrentQueue<RunLog>();
        await using (var rig = new Rig(["p1"], beginRunLog: (pair, _) =>
        {
            var log = RunLog.Open(root, pair, RunLog.AutoSyncCommand, false);
            logs.Enqueue(log);
            return log;
        }))
        {
            rig.Run.MonitorThrow = new IOException("synthetic monitor failure");
            await rig.StartAsync();
            await Until(() => logs.Count == 1, "the failing monitor pass opened its log");
        }
        await RequireFailureSummaryAsync(logs, "synthetic monitor failure", "MonitorFailureKeepsSummary");
    }

    private static string Root(string prefix) => Path.Combine(
        Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT") ?? Path.GetTempPath(),
        prefix + "-" + Guid.NewGuid().ToString("N"));

    private static async Task RequireFailureSummaryAsync(
        System.Collections.Concurrent.ConcurrentQueue<RunLog> logs, string reason, string label)
    {
        var captured = logs.Single();
        await captured.Completion;
        var text = File.ReadAllText(captured.Path!);
        Require(text.Contains("outcome=failed reason=" + reason, StringComparison.Ordinal),
            $"the failed run keeps its log scope through the failure handler ({label}): " + text);
        Pass(label);
    }
}
