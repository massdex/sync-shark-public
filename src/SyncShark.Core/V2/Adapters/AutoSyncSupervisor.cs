namespace Massdex.SyncShark.V2;

/// <summary>
/// Owns the AutoSync relaunch policy: one durable event per start, exit, stop and delay. A transient child
/// exit is never itself a reason to stop, and only the supervisor stop event ends supervision; a persistent
/// startup refusal is terminal, so it is reported once and never relaunched. Process start, the stop wait,
/// the clock and the backoff policy are injected by the app edge, so the policy runs without real processes
/// or real time.</summary>
public sealed class AutoSyncSupervisor
{
    /// <summary>A started host observed until it exits. AutoSyncProcess.ChildProcess is the production form.</summary>
    public interface IChild : IDisposable
    {
        int ProcessId { get; }
        void WaitForExit();
        int ExitCode { get; }
    }

    private readonly string _productRoot;
    private readonly Func<IChild> _start;
    private readonly Func<TimeSpan, bool> _waitForStop;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly TimeSpan _stableAfter;

    public AutoSyncSupervisor(string productRoot, Func<IChild> start, Func<TimeSpan, bool> waitForStop,
        TimeSpan initialDelay, TimeSpan maximumDelay, TimeSpan stableAfter, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot);
        _productRoot = productRoot;
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _waitForStop = waitForStop ?? throw new ArgumentNullException(nameof(waitForStop));
        _initialDelay = initialDelay;
        _maximumDelay = maximumDelay;
        _stableAfter = stableAfter;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Supervises children until the stop event is set or a persistent startup refusal ends the run.</summary>
    public int Run()
    {
        var delay = _initialDelay;
        Trace("supervisor start");
        while (true)
        {
            if (Stopped(TimeSpan.Zero)) return 0;
            try
            {
                using var child = _start();
                var pid = child.ProcessId;
                var started = _utcNow();
                Trace("child start pid=" + pid);
                child.WaitForExit();
                var exit = child.ExitCode;
                var uptime = _utcNow() - started;
                Trace("child exit pid=" + pid + " exit=" + exit + " uptime=" + (long)uptime.TotalSeconds + "s");
                if (uptime >= _stableAfter) delay = _initialDelay;
                if (AutoSyncProcess.IsExistingOwnerExit(exit)) RecordExistingOwnerNotice();
                if (SyncCli.IsPersistentStartupRefusal(exit))
                {
                    Trace(EdgeMessages.Text("startup-refused-log",
                        exit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    RecordStartupRefusalNotice(exit);
                }
            }
            catch (Exception error)
            {
                AutoSyncLog.Failure(_productRoot, error);
            }
            Trace("relaunch delay=" + (long)delay.TotalSeconds + "s");
            if (Stopped(delay)) return 0;
            delay = NextDelay(delay);
        }
    }

    private bool Stopped(TimeSpan wait)
    {
        if (!_waitForStop(wait)) return false;
        Trace("stop event set");
        return true;
    }

    private TimeSpan NextDelay(TimeSpan delay)
    {
        var doubled = delay + delay;
        return doubled > _maximumDelay ? _maximumDelay : doubled;
    }

    private void RecordExistingOwnerNotice()
    {
        try { AutoSyncProcess.RecordExistingOwnerNotice(AutoSyncProcess.ExistingOwnerNoticePathFor(_productRoot)); }
        catch (Exception error) { AutoSyncLog.Failure(_productRoot, error); }
    }

    // Written once per supervisor lifetime: the caller returns immediately after this call.
    private void RecordStartupRefusalNotice(int exit)
    {
        try
        {
            AutoSyncProcess.RecordExistingOwnerNotice(AutoSyncProcess.PersistentStartupNoticePath,
                SyncCli.PersistentStartupRefusalNoticeKey(exit));
        }
        catch (Exception error) { AutoSyncLog.Failure(_productRoot, error); }
    }

    private void Trace(string message) => AutoSyncLog.Append(_productRoot, message);
}
