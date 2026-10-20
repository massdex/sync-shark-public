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
        if (CliEntry.IsCliInvocation(args)) return CliEntry.Execute(args);
        var selected = CoreSwitch.Select(args, Environment.GetEnvironmentVariable(CoreSwitch.EnvironmentVariable));
        if (selected.Args is ["--background-host"] or ["--autosync-supervisor"])
        {
            if (selected.Error is not null || !selected.UseV2) return (int)SyncCliExitCode.Usage;
            try
            {
                return selected.Args[0] == "--background-host"
                    ? BackgroundHostEntry.RunAsync().GetAwaiter().GetResult()
                    : Supervise();
            }
            catch (Exception) { return (int)SyncCliExitCode.Failed; }
        }
        return RunWindow();
    }

    private static int Supervise()
    {
        // Only one relauncher per store/user/session. A second starter observes the existing host.
        using var mutex = new Mutex(false, @"Local\" + AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + ".supervisor");
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, SupervisorStopName);
        var delay = TimeSpan.FromSeconds(5);
        try
        {
            while (!stop.WaitOne(0))
            {
                try
                {
                    using var child = AutoSyncProcess.StartDetached(Environment.ProcessPath ?? throw new IOException("Executable unavailable."), supervisor: false);
                    child.WaitForExit();
                    if (child.ExitCode == 0) return 0; // explicit safe stop/attach is not a crash
                }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                {
                    // A temporarily unavailable executable or resource retries with the same bounded policy.
                }
                if (stop.WaitOne(delay)) return 0;
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
            return 0;
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
