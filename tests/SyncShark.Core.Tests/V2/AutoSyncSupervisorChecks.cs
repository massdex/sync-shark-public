namespace Massdex.SyncShark.V2;

/// <summary>
/// Supervisor relaunch policy and its durable log, over fake children and an injected clock. No real
/// process is started, so a zero exit is reproduced exactly instead of being raced on the machine.
/// </summary>
internal static class AutoSyncSupervisorChecks
{
    public static void Run()
    {
        ZeroExitWithoutStopRelaunches();
        BackoffResetsAfterStableUptime();
        EventsAreLogged();
        BackoffIsUnchanged();
        FailedRotationDoesNotGrow();
        ConcurrentWritersStayBounded();
        PrivateDetailsAreOmitted();
        Console.WriteLine("AutoSync supervisor checks passed.");
    }

    private static void ZeroExitWithoutStopRelaunches()
    {
        var root = NewRoot();
        try
        {
            var starts = 0;
            var supervisor = NewSupervisor(root,
                () => new FakeChild(100 + Interlocked.Increment(ref starts), exitCode: 0),
                wait => starts >= 2 && wait > TimeSpan.Zero);
            Require(supervisor.Run() == 0, "supervision ends at the stop event, not at a child exit");
            Require(starts == 2, "a zero child exit without a stop event relaunches");
        }
        finally { Remove(root); }
    }

    private static void BackoffResetsAfterStableUptime()
    {
        var root = NewRoot();
        try
        {
            var starts = 0;
            var clock = new FakeClock();
            var delays = new List<int>();
            var supervisor = NewSupervisor(root,
                () => new FakeChild(++starts, 0, () => clock.Advance(
                    starts == 3 ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(1))),
                wait =>
                {
                    if (wait == TimeSpan.Zero) return false;
                    Require(File.ReadAllLines(AutoSyncLog.FilePath(root)).Last().Contains(
                        "relaunch delay=" + (int)wait.TotalSeconds + "s", StringComparison.Ordinal),
                        "relaunch delay is durable before waiting");
                    delays.Add((int)wait.TotalSeconds);
                    return delays.Count == 4;
                }, () => clock.Now);
            supervisor.Run();
            Require(delays.SequenceEqual(new[] { 5, 10, 5, 10 }), "stable uptime resets backoff before the next wait");
        }
        finally { Remove(root); }
    }

    private static void EventsAreLogged()
    {
        var root = NewRoot();
        try
        {
            var starts = 0;
            var clock = new FakeClock();
            var supervisor = NewSupervisor(root,
                () => new FakeChild(300 + Interlocked.Increment(ref starts), exitCode: 0,
                    onWait: () => clock.Advance(TimeSpan.FromSeconds(3))),
                wait => starts >= 2 && wait > TimeSpan.Zero,
                () => clock.Now);
            supervisor.Run();
            var lines = File.ReadAllLines(AutoSyncLog.FilePath(root));
            Require(lines.Any(line => line.Contains("supervisor start", StringComparison.Ordinal)),
                "supervisor start is logged");
            Require(lines.Any(line => line.Contains("child start pid=301", StringComparison.Ordinal)),
                "child start pid is logged");
            Require(lines.Any(line => line.Contains("child exit pid=301 exit=0 uptime=3s", StringComparison.Ordinal)),
                "child exit code and uptime are logged");
            Require(lines.Any(line => line.Contains("relaunch delay=5s", StringComparison.Ordinal)),
                "relaunch delay is logged");
            Require(lines.Any(line => line.Contains("stop event set", StringComparison.Ordinal)),
                "the stop event is logged");
        }
        finally { Remove(root); }
    }

    private static void BackoffIsUnchanged()
    {
        var root = NewRoot();
        try
        {
            var starts = 0;
            var delays = new List<TimeSpan>();
            var supervisor = NewSupervisor(root,
                () => new FakeChild(400 + Interlocked.Increment(ref starts), exitCode: (int)SyncCliExitCode.Failed),
                wait =>
                {
                    if (wait == TimeSpan.Zero) return false;
                    delays.Add(wait);
                    return delays.Count >= 7;
                });
            supervisor.Run();
            Require(delays.Select(delay => (int)delay.TotalSeconds).SequenceEqual(new[] { 5, 10, 20, 40, 80, 160, 300 }),
                "backoff doubles from five seconds and caps at five minutes");
        }
        finally { Remove(root); }
    }

    private static void PrivateDetailsAreOmitted()
    {
        var root = NewRoot();
        try
        {
            AutoSyncLog.HostStarted(root, new Version(1, 2, 3, 4));
            try { throw new IOException("synthetic private file content Parent/Child.txt"); }
            catch (Exception error) { AutoSyncLog.Failure(root, error); }
            AutoSyncLog.HostShutdown(root, "autosync-stop-requested");
            var log = File.ReadAllText(AutoSyncLog.FilePath(root));
            Require(!log.Contains("synthetic private") && !log.Contains("Parent/Child"),
                "exception messages never enter lifecycle evidence");
            Require(log.Contains("type=System.IO.IOException") && log.Contains("hresult=") &&
                log.Contains("method=PrivateDetailsAreOmitted") && log.Contains("host start version=1.2.3.4") &&
                log.Contains("host shutdown: stop requested"), "typed lifecycle metadata remains available");
        }
        finally { Remove(root); }
    }

    private static void FailedRotationDoesNotGrow()
    {
        var root = NewRoot();
        try
        {
            AutoSyncLog.Append(root, "initial");
            var path = AutoSyncLog.FilePath(root);
            using (var full = File.OpenWrite(path)) full.SetLength(AutoSyncLog.FileBound);
            using (var blocked = new FileStream(path + ".previous", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                AutoSyncLog.Append(root, "must not grow");
                Require(new FileInfo(path).Length == AutoSyncLog.FileBound, "failed rotation skips the append");
            }
            AutoSyncLog.Append(root, "rotation recovered");
            Require(File.ReadAllText(path).Contains("dropped=1 "), "next successful write reports the dropped line");
            Require(new FileInfo(path).Length < AutoSyncLog.FileBound &&
                new FileInfo(path + ".previous").Length == AutoSyncLog.FileBound,
                "rotation recovers with one bounded previous generation");
        }
        finally { Remove(root); }
    }

    private static void ConcurrentWritersStayBounded()
    {
        var root = NewRoot();
        try
        {
            AutoSyncLog.Append(root, "initial");
            var path = AutoSyncLog.FilePath(root);
            using (var full = File.OpenWrite(path)) full.SetLength(AutoSyncLog.FileBound);
            using var start = new Barrier(2);
            Task Writer(string label) => Task.Run(() =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(5));
                for (var n = 0; n < 100; n++) AutoSyncLog.Append(root, label);
            });
            Task.WhenAll(Writer("writer-a"), Writer("writer-b")).GetAwaiter().GetResult();
            Require(new FileInfo(path).Length <= AutoSyncLog.FileBound &&
                new FileInfo(path + ".previous").Length <= AutoSyncLog.FileBound,
                "concurrent writers keep both generations within budget");
            var lines = File.ReadAllLines(path);
            Require(lines.Length > 0 && lines.All(line => line.EndsWith("writer-a", StringComparison.Ordinal) ||
                line.EndsWith("writer-b", StringComparison.Ordinal)), "concurrent appends retain complete lines");
        }
        finally { Remove(root); }
    }

    private static AutoSyncSupervisor NewSupervisor(string root, Func<AutoSyncSupervisor.IChild> start,
        Func<TimeSpan, bool> wait, Func<DateTimeOffset>? clock = null) =>
        new(root, start, wait, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(300), TimeSpan.FromMinutes(10), clock);

    private sealed class FakeChild(int pid, int exitCode, Action? onWait = null) : AutoSyncSupervisor.IChild
    {
        public int ProcessId { get; } = pid;
        public void WaitForExit() => onWait?.Invoke();
        public int ExitCode { get; } = exitCode;
        public void Dispose() { }
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal DateTimeOffset Now => _now;
        internal void Advance(TimeSpan by) => _now += by;
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "supervisor-checks-" + Guid.NewGuid().ToString("N"));

    private static void Remove(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception) { /* the fixture may not have been created */ }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
