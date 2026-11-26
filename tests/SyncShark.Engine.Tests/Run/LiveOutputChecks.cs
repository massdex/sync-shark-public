using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for the live run view: what a waiting command sees while its run writes its log.</summary>
public static class LiveOutputChecks
{
    // Stage, wait and failure blocks reach the waiting command in order; a per-file effect stays in the log only.
    public static Task StreamsRecordsAsync()
    {
        var output = new StringWriter();
        using (var view = new LiveOutput(output, TimeProvider.System)) using (var log = new RunLog(new HostChecks.MemoryLogs(), "live", ItemChecks.Settings, view))
        {
            log.Write(new RunEvent(RunEventKind.Stage, nameof(TextKey.StageLocalWalk), string.Empty, string.Empty, string.Empty));
            log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha.txt", string.Empty, nameof(Side.Dropbox)) { Action = ActionKind.Upload });
            log.Write(new RunEvent(RunEventKind.Wait, nameof(Repair), string.Empty, "waiting", string.Empty));
        }
        var text = output.ToString();
        var stage = text.IndexOf(Text.Get(TextKey.StageLocalWalk), StringComparison.Ordinal);
        Check.Require(stage >= 0 && text.IndexOf(Text.Get(TextKey.HumanWaitLabel), StringComparison.Ordinal) > stage, "the stage and wait lines did not reach the live output in order");
        Check.Require(!text.Contains("alpha.txt", StringComparison.Ordinal), "a per-file effect was shown without the switch");
        return Task.CompletedTask;
    }

    // A quiet period shows the newest open phase with its file and count once, then only that it is still working;
    // the per-item records never reach the log file.
    public static Task QuietProgressAsync()
    {
        var output = new StringWriter(); var clock = new Ticks(); var logs = new HostChecks.MemoryLogs { Capture = new StringWriter() };
        using (var view = new LiveOutput(output, clock)) using (var log = new RunLog(logs, "live", ItemChecks.Settings, view))
        {
            log.Write(new RunEvent(RunEventKind.Stage, nameof(TextKey.StageRows), string.Empty, string.Empty, string.Empty));
            log.Write(new RunEvent(RunEventKind.Stage, nameof(TextKey.StageRows), "folder/current.txt", string.Empty, RunLog.InProgressOutcome) { Count = 7 });
            clock.Fire(); // the stage start was shown in this period
            clock.Fire();
            log.Write(new RunEvent(RunEventKind.Stage, nameof(TextKey.StageRows), string.Empty, string.Empty, RunLog.EndedOutcome) { Ended = true });
            clock.Fire(); // the stage end was shown in this period
            clock.Fire();
        }
        var text = output.ToString();
        Check.Require(clock.Period == TimeSpan.FromSeconds(30), "the quiet period is not the owner's 30 seconds");
        Check.Require(text.Split("folder/current.txt").Length == 2 && text.Contains(Text.Get(TextKey.StatusStillQuiet), StringComparison.Ordinal),
            "a quiet period did not show the current file once, then still working");
        Check.Require(!logs.Capture.ToString().Contains("folder/current.txt", StringComparison.Ordinal), "a per-item progress record reached the log file");
        return Task.CompletedTask;
    }

    // The waiting command sees the run's completion as the finished banner with its applied count.
    public static Task CompletionLineAsync()
    {
        var output = new StringWriter();
        using (var view = new LiveOutput(output, TimeProvider.System)) using (var log = new RunLog(new HostChecks.MemoryLogs(), "live", ItemChecks.Settings, view))
            log.Write(new RunEvent(RunEventKind.Stage, nameof(TextKey.ResultRunCompleted), string.Empty,
                Repair.StageDetail(TextKey.ResultRunCompleted, true, 3, TimeSpan.FromSeconds(2)), string.Empty) { Ended = true, Count = 3 });
        var text = output.ToString();
        Check.Require(text.Contains(Text.Get(TextKey.HumanFinished, (TextArg.Name, Text.Get(TextKey.HumanTitleRepair)),
            (TextArg.Status, Text.Get(TextKey.HumanFinishedComplete)), (TextArg.Suffix, string.Empty)), StringComparison.Ordinal) &&
            text.Contains(Repair.StageDetail(TextKey.ResultRunCompleted, true, 3, TimeSpan.FromSeconds(2)), StringComparison.Ordinal),
            "the completion line did not show the finished banner with its applied count");
        return Task.CompletedTask;
    }

    // repair --show-files reaches the run's live view, which then shows each per-file effect.
    public static Task ShowFilesAsync()
    {
        var invocation = Engine.Edge.Cli.Parse(["repair", "--pair", "p1", "--show-files"]);
        Check.Require(invocation?.Flag("ShowFiles") == true, "repair did not accept --show-files");
        var output = new StringWriter();
        using (var view = new LiveOutput(output, TimeProvider.System, perFile: true)) using (var log = new RunLog(new HostChecks.MemoryLogs(), "live", ItemChecks.Settings, view))
            log.Write(new RunEvent(RunEventKind.Effect, "Uploaded", "alpha.txt", string.Empty, nameof(Side.Dropbox)) { Action = ActionKind.Upload });
        Check.Require(output.ToString().Contains("alpha.txt", StringComparison.Ordinal), "a per-file effect was not shown with the switch");
        return Task.CompletedTask;
    }

    // The live view opens with the header naming the pair, the mode with per-file detail off, and the run id.
    public static Task HeaderAsync()
    {
        var output = new StringWriter();
        new LiveOutput(output, TimeProvider.System, header: ("pair-1", "run-1")).Dispose();
        var text = output.ToString();
        Check.Require(text.StartsWith(Text.Get(TextKey.HumanHeaderTitle, (TextArg.Name, Text.Get(TextKey.HumanTitleRepair))), StringComparison.Ordinal) &&
            text.Contains(Text.Get(TextKey.HumanHeaderPair, (TextArg.Pair, "pair-1")), StringComparison.Ordinal) &&
            text.Contains(Text.Get(TextKey.HumanHeaderRun, (TextArg.Run, "run-1")), StringComparison.Ordinal) &&
            text.Contains(Text.Get(TextKey.HumanPerFileOff), StringComparison.Ordinal), "the live header did not name the pair, mode and run");
        return Task.CompletedTask;
    }

    private sealed class Ticks : TimeProvider
    {
        private TimerCallback? _tick;
        internal TimeSpan Period { get; private set; }
        internal void Fire() => _tick!(null);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            (_tick, Period) = (callback, period);
            return System.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
