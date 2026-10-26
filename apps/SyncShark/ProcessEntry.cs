using System.IO;
using System.Runtime.CompilerServices;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>Dispatch headless modes before constructing or JIT-loading a WPF Application.</summary>
internal static class ProcessEntry
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Process-wide and before any product file I/O: Windows otherwise disguises the placeholder
        // attributes this process must see. A missing API is reported, never thrown.
        WindowsPlaceholderCompatibility.Expose();
        if (CliEntry.IsCliInvocation(args)) return CliEntry.Execute(args);
        var selected = CoreSwitch.Select(args, Environment.GetEnvironmentVariable(CoreSwitch.EnvironmentVariable));
        if (selected.Error is null && !selected.UseV2)
        {
            try { Console.Error.WriteLine(CoreSwitch.LegacyNotice); } catch (IOException) { }
        }
        if (selected.Args is ["--background-host"] or ["--autosync-supervisor"])
        {
            if (selected.Error is not null || !selected.UseV2) return (int)SyncCliExitCode.Usage;
            try
            {
                return selected.Args[0] == "--background-host"
                    ? BackgroundHostEntry.RunAsync().GetAwaiter().GetResult()
                    : Supervise();
            }
            catch (Exception error)
            {
                // A host failure is already recorded by BackgroundHostEntry; a supervisor failure must not vanish.
                if (selected.Args[0] == "--autosync-supervisor")
                    try { AutoSyncLog.Failure(SyncDataLocation.ProductRoot, error); }
                    catch (Exception) { }
                return (int)SyncCliExitCode.Failed;
            }
        }
        return RunWindow();
    }

    private static int Supervise()
    {
        // Only one relauncher per store/user/session. A second starter observes the existing host.
        using var mutex = new Mutex(false, @"Local\" + AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + ".supervisor");
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, SupervisorStopName);
        if (!owned)
        {
            if (!stop.WaitOne(0)) return 0; // a healthy supervisor already owns this generation
            // A quick restart must inherit ownership after the stopping generation drains.
            while (!owned)
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned && !stop.WaitOne(0)) return 0; // another starter already took over
            }
        }
        stop.Reset(); // this owner begins a new supervisor generation
        try
        {
            return new AutoSyncSupervisor(
                SyncDataLocation.ProductRoot,
                () => AutoSyncProcess.StartDetached(Environment.ProcessPath ?? throw new IOException("Executable unavailable."),
                    supervisor: false, warning: Console.Error.WriteLine),
                wait => stop.WaitOne(wait),
                initialDelay: TimeSpan.FromSeconds(5),
                maximumDelay: TimeSpan.FromSeconds(300),
                stableAfter: TimeSpan.FromMinutes(10)).Run();
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string SupervisorStopName => @"Local\" +
        AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + ".stop-supervisor";

    internal static void StopSupervisor()
    {
        try { using var stop = EventWaitHandle.OpenExisting(SupervisorStopName); stop.Set(); }
        catch (WaitHandleCannotBeOpenedException) { } // no relauncher is running
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWindow()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
