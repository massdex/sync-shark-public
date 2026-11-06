using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>Dispatch headless modes before constructing or JIT-loading a WPF Application.</summary>
internal static class ProcessEntry
{
    private const string LocalPrefix = @"Local\";
    private const string SupervisorMutexSuffix = ".supervisor";
    // Private whole-process switches; after the core switch is stripped each one is a singleton argument.
    private const string InitializeStoreSwitch = "--initialize-store";
    private const string EngineHostSwitch = "--engine-host";
    private const string EngineSupervisorSwitch = "--engine-supervisor";
    private const string AutoSyncSupervisorSwitch = "--autosync-supervisor";
    private const string LegacySupervisorSwitch = "--legacy-autosync-supervisor";
    private const string BackgroundHostSwitch = "--background-host";
    private const string BackgroundSwitch = "--background";
    // The existing supervisor relaunch policy; Engine reuses it unchanged.
    private const int SupervisorInitialDelaySeconds = 5;
    private const int SupervisorMaximumDelayMinutes = 5;
    private const int SupervisorStableAfterMinutes = 10;

    [STAThread]
    public static int Main(string[] args)
    {
        // Process-wide and before any product file I/O: Windows otherwise disguises the placeholder
        // attributes this process must see. A missing API is reported, never thrown.
        WindowsPlaceholderCompatibility.Expose();
        if (CliEntry.IsCliInvocation(args)) return CliEntry.Execute(args);
        var selected = CoreSwitch.Select(args, Environment.GetEnvironmentVariable(CoreSwitch.EnvironmentVariable));
        if (selected.Args.FirstOrDefault() is "--setup-validate-data" or "--setup-startup-command" or InitializeStoreSwitch)
        {
            if (selected.Error is not null || !selected.UseV2 || selected.Args.Length != 2)
                return (int)SyncCliExitCode.Usage;
            try
            {
                if (selected.Args[0] == InitializeStoreSwitch)
                {
                    DataLocationSetup.Initialize(selected.Args[1]);
                    return 0;
                }
                using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                if (selected.Args[0] == "--setup-validate-data")
                {
                    var root = SyncDataLocation.ValidateDirectory(selected.Args[1]);
                    output.WriteLine(root);
                    output.WriteLine("store: " + SyncDataLocation.StoreState(root));
                }
                else output.WriteLine(AutoSyncProcess.StartupCommand(selected.Args[1]));
                return 0;
            }
            catch (Exception) { return (int)SyncCliExitCode.Failed; }
        }
        if (selected.Error is null && !selected.UseV2)
        {
            try { Console.Error.WriteLine(CoreSwitch.LegacyNotice); } catch (IOException) { }
        }
        // A private switch is an exact singleton: a malformed combination or v1 selection is a usage error before
        // any resource or effect, and no arbitrary argument route becomes new startup consent.
        if (IsPrivateSwitch(selected.Args) &&
            (selected.Error is not null || !selected.UseV2 || selected.Args.Length != 1))
            return (int)SyncCliExitCode.Usage;
        if (selected.Args is [BackgroundHostSwitch] && (selected.Error is not null || !selected.UseV2))
            return (int)SyncCliExitCode.Usage;
        if (selected.Error is null && selected.UseV2)
        {
            switch (selected.Args)
            {
                case [EngineHostSwitch]:
                    return RunEngineHost();
                case [EngineSupervisorSwitch]:
                case [AutoSyncSupervisorSwitch]:
                    return Supervise(engine: true);
                case [LegacySupervisorSwitch]:
                    return Supervise(engine: false);
                case [BackgroundHostSwitch]:
                    return RunBackgroundHost();
                // The narrow cutover exception for the exact still-owned legacy GUI startup value.
                case [BackgroundSwitch] when BackgroundEngineStartup():
                    return RunEngineBackground();
            }
        }
        return RunWindow();
    }

    private static bool IsPrivateSwitch(string[] args) => args.Any(arg =>
        arg is InitializeStoreSwitch or EngineHostSwitch or EngineSupervisorSwitch or AutoSyncSupervisorSwitch or LegacySupervisorSwitch);

    private static bool BackgroundEngineStartup()
    {
        var executable = Environment.ProcessPath;
        return executable is not null && AutoSyncProcess.IsEngineStartupInvocation(executable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunEngineHost() => RunOwned(() => EngineHostEntry.RunAsync(CancellationToken.None));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunEngineBackground() => RunOwned(() => EngineHostEntry.StartAsync(CancellationToken.None));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunBackgroundHost() => RunOwned(BackgroundHostEntry.RunAsync);

    private static int RunOwned(Func<Task<int>> run)
    {
        try { return run().GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            LogFailure(error);
            return (int)SyncCliExitCode.Failed;
        }
    }

    // One relauncher per store/user/session, shared by both supervisor generations. Engine claims the launcher's
    // readiness pipe once, hands it to the first host child only, and relays that child's exit result so an initial
    // startup failure reaches the waiting launcher while supervision continues.
    private static int Supervise(bool engine)
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return (int)SyncCliExitCode.Failed;
        using var readiness = engine ? AutoSyncProcess.TakeReadinessPipe() : null;
        using var mutex = new Mutex(false, LocalPrefix +
            AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + SupervisorMutexSuffix);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset,
            AutoSyncProcess.SupervisorStopName(engine));
        if (!TakeOwnership(mutex, stop)) return (int)SyncCliExitCode.Success;
        stop.Reset(); // this owner begins a new supervisor generation
        var firstLaunch = true;
        AutoSyncSupervisor.IChild StartChild()
        {
            if (!engine)
                return AutoSyncProcess.StartDetached(executable, supervisor: false, warning: Console.Error.WriteLine);
            if (EngineHostEntry.ObserveReadyHost() is { } observed)
            {
                // An already-ready host is observed, never duplicated into a zero-exit relaunch loop.
                if (firstLaunch)
                {
                    firstLaunch = false;
                    EngineHostEntry.WriteReady(readiness);
                    readiness?.Dispose();
                }
                return observed;
            }
            var handle = firstLaunch ? readiness?.SafePipeHandle : null;
            firstLaunch = false;
            if (handle is null)
                return AutoSyncProcess.StartEngineDetached(executable, supervisor: false,
                    warning: Console.Error.WriteLine);
            try
            {
                return new ReadinessChild(AutoSyncProcess.StartEngineDetached(executable, supervisor: false,
                    warning: Console.Error.WriteLine, readiness: handle), readiness!);
            }
            catch (Exception)
            {
                // The first child never started; the launcher must not wait for a readiness that cannot come.
                EngineHostEntry.WriteStartupResult(readiness, (int)SyncCliExitCode.Failed);
                readiness?.Dispose();
                throw;
            }
        }
        try
        {
            return new AutoSyncSupervisor(SyncSharkRuntimePaths.ProductRoot, StartChild,
                wait => stop.WaitOne(wait),
                TimeSpan.FromSeconds(SupervisorInitialDelaySeconds),
                TimeSpan.FromMinutes(SupervisorMaximumDelayMinutes),
                TimeSpan.FromMinutes(SupervisorStableAfterMinutes)).Run();
        }
        catch (Exception error)
        {
            LogFailure(error);
            return (int)SyncCliExitCode.Failed;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool TakeOwnership(Mutex mutex, EventWaitHandle stop)
    {
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (owned) return true;
        // A healthy supervisor already owns this generation; only a stopping generation may be inherited.
        if (!stop.WaitOne(0)) return false;
        try { return WaitHandle.WaitAny([mutex, stop]) == 0; }
        catch (AbandonedMutexException) { return true; }
    }

    internal static void StopSupervisor() => AutoSyncProcess.StopSupervisor();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWindow()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void LogFailure(Exception error)
    {
        try { AutoSyncLog.Failure(SyncSharkRuntimePaths.ProductRoot, error); } catch (Exception) { }
    }

    /// <summary>Relays the first supervised child's exit result through the launcher's readiness pipe exactly once,
    /// then releases both owned handles; disposal never terminates the observed process.</summary>
    private sealed class ReadinessChild(AutoSyncProcess.ChildProcess child, AnonymousPipeClientStream readiness)
        : AutoSyncSupervisor.IChild
    {
        private bool _reported;

        public int ProcessId => child.ProcessId;
        public int ExitCode => child.ExitCode;

        public void WaitForExit()
        {
            child.WaitForExit();
            if (_reported) return;
            _reported = true;
            EngineHostEntry.WriteStartupResult(readiness, child.ExitCode);
        }

        public void Dispose()
        {
            child.Dispose();
            readiness.Dispose();
        }
    }
}
