namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    private static async Task RunTimingLogAsync()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT") ?? Path.GetTempPath(),
            "autosync-timing-" + Guid.NewGuid().ToString("N"));
        var logs = new System.Collections.Concurrent.ConcurrentQueue<RunLog>();
        await using (var rig = new Rig(["p1"], beginRunLog: (pair, cause) =>
        {
            var log = RunLog.Open(root, pair, "autosync", false);
            log.Summary("wake-cause=" + cause);
            using (new RunBoundaryTiming("fixture", true)) { }
            logs.Enqueue(log);
            return log;
        }))
        {
            await rig.StartAsync();
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(logs.Count == 1, "automatic startup opens exactly one bounded per-run log");
        }
        var captured = logs.Single();
        await captured.Completion;
        var text = File.ReadAllText(captured.Path!);
        Require(text.Contains("wake-cause=startup") && text.Contains("run-boundary phase=fixture"),
            "automatic run scope retains wake cause and existing phase timing lines");
        Require(RunLog.MaximumFiles == 50 && RunLog.MaximumRunBytes > 0, "automatic diagnostics reuse the bounded rotating log owner");
        Pass("RunTimingLog");
    }
}
