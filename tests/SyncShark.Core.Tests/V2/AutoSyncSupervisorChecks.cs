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
        ZeroExitWithStopDoesNotRelaunch();
        EventsAreLogged();
        BackoffIsUnchanged();
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

    private static void ZeroExitWithStopDoesNotRelaunch()
    {
        var root = NewRoot();
        try
        {
            var starts = 0;
            var supervisor = NewSupervisor(root,
                () => new FakeChild(200 + Interlocked.Increment(ref starts), exitCode: 0),
                wait => wait > TimeSpan.Zero);
            Require(supervisor.Run() == 0, "an explicit stop ends supervision cleanly");
            Require(starts == 1, "a zero child exit with the stop event does not relaunch");
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
