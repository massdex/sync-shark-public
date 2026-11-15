using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>Windows process/startup edge. Registration is explicitly opt-in.</summary>
public static class AutoSyncProcess
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const uint WaitTimeout = 258; // Windows WAIT_TIMEOUT: the pinned process is still running.
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    // The child's arguments are exact singletons, so its inherited handle number travels here.
    private const string ReadinessVariable = "SYNCSHARK_ENGINE_READINESS_HANDLE";
    internal const string ValueName = "Sync Shark AutoSync";
    internal const string LegacyValueName = "Sync Shark";
    // All registry edges share this fixture seam, including GUI startup and settings reads.
    internal static string? StartupKeyPathForTest { get; set; }
    // Failure/order injection at the real registry write; only startup regression checks use it.
    internal static Action? BeforeStartupWriteForTest { get; set; }

    private static RegistryKey? OpenStartupKey(bool writable, bool create = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = StartupKeyPathForTest ?? RunKey;
        return create ? Registry.CurrentUser.CreateSubKey(path, writable: true) :
            Registry.CurrentUser.OpenSubKey(path, writable);
    }
    public const string HostArguments = "--core v2 --background-host";
    public const string SupervisorArguments = "--core v2 --autosync-supervisor";
    public const string LegacySupervisorArguments = "--core v2 --legacy-autosync-supervisor";
    public const string EngineHostArguments = "--core v2 --engine-host";
    public const string EngineSupervisorArguments = "--core v2 --engine-supervisor";
    public const string EngineStartupArguments = "--cli engine host start --yes";
    internal const string LegacyArguments = "--background";

    public static string StartupCommand(string executable)
    {
        if (!IsStartupExecutable(executable))
            throw new ArgumentException(EdgeMessages.Text("startup-executable-invalid"), nameof(executable));
        return $"\"{Path.GetFullPath(executable)}\" {EngineStartupArguments}";
    }

    /// <summary>Selects the stable installed copy when it is the same bound build, unless explicitly overridden.</summary>
    public static string SelectStartupExecutable(string running, bool thisBuild = false, Action<string>? warning = null)
    {
        var installed = SyncSharkRuntimePaths.InstalledExecutablePath;
        var executable = running;
        if (!thisBuild && File.Exists(installed))
        {
            var currentVersion = FileVersionInfo.GetVersionInfo(running).ProductVersion;
            var installedVersion = FileVersionInfo.GetVersionInfo(installed).ProductVersion;
            executable = SelectStartupExecutable(running, currentVersion, installed, installedVersion, thisBuild);
            if (!string.Equals(currentVersion, installedVersion, StringComparison.Ordinal))
                ProgressGuard.Report(warning, EdgeMessages.Text("startup-versions-differ",
                    $"{running} ({currentVersion}) / {installed} ({installedVersion})"));
        }
        if (string.Equals(executable, running, StringComparison.OrdinalIgnoreCase))
            ProgressGuard.Report(warning, EdgeMessages.Text("startup-running-build"));
        return executable;
    }

    public static string SelectStartupExecutable(string running, string? runningVersion,
        string installed, string? installedVersion, bool thisBuild)
    {
        if (thisBuild) return running;
        // A larger product version alone says nothing about shared-host support. Only an
        // identical, commit-bound build is safe without an explicit capability declaration.
        static bool Bound(string? value) => value is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(value, @"\+[0-9a-fA-F]{40}$");
        return Bound(runningVersion) && Bound(installedVersion) &&
            string.Equals(runningVersion, installedVersion, StringComparison.Ordinal)
                ? installed : running;
    }

    public static bool IsExistingOwnerExit(int exitCode) => exitCode == (int)SyncCliExitCode.Busy;

    /// <summary>The one startup notice file read by start/status and written by the supervisor.</summary>
    public static string ExistingOwnerNoticePath => ExistingOwnerNoticePathFor(SyncSharkRuntimePaths.ProductRoot);

    internal static string ExistingOwnerNoticePathFor(string productRoot) =>
        Path.Combine(productRoot, "autosync-owner-notice.txt");

    /// <summary>The terminal notice for a persistent startup refusal. The product data root may be unusable
    /// (an invalid location pointer), so the notice lives next to the installation instead.</summary>
    public static string PersistentStartupNoticePath =>
        Path.Combine(SyncSharkRuntimePaths.InstallationUserRoot, "autosync-startup-refused-notice.txt");

    public static void RecordExistingOwnerNotice(string path, string messageKey = "startup-owner-notice")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One overwrite-only spool notice: the other owner holds the database, so never open it here.
        File.WriteAllText(path, EdgeMessages.Text(messageKey));
    }

    /// <summary>The retained persistent-startup notice text, or null when none is retained.</summary>
    public static string? ReadPersistentStartupNotice()
    {
        var path = PersistentStartupNoticePath;
        return File.Exists(path) && new FileInfo(path).Length > 0 ? File.ReadAllText(path) : null;
    }

    public static void ClearExistingOwnerNotice(string path)
    {
        if (File.Exists(path)) File.WriteAllText(path, string.Empty);
    }

    /// <summary>Clears each retained startup notice independently, so an inaccessible notice never blocks the
    /// other and a healthy host keeps running; a failed clear is logged once.</summary>
    public static void ClearStartupNotices(string productRoot)
    {
        foreach (var path in new[] { ExistingOwnerNoticePathFor(productRoot), PersistentStartupNoticePath })
        {
            try { ClearExistingOwnerNotice(path); }
            catch (Exception error) { AutoSyncLog.Failure(productRoot, error); }
        }
    }

    /// <summary>Ownership follows the product command, never its deployment directory.</summary>
    public static bool IsProductStartupCommand(string? command) =>
        IsStartupCommand(command, SupervisorArguments) || IsStartupCommand(command, EngineSupervisorArguments) ||
        IsStartupCommand(command, EngineStartupArguments);

    /// <summary>Recognizes only the owned GUI startup or its canonical replacement for this executable.</summary>
    public static bool IsEngineStartupInvocation(string executable)
    {
        if (!OperatingSystem.IsWindows() || !IsStartupExecutable(executable)) return false;
        using var key = OpenStartupKey(writable: false);
        return TargetsExecutable(key?.GetValue(LegacyValueName) as string, LegacyArguments, executable) ||
            TargetsExecutable(key?.GetValue(ValueName) as string, EngineSupervisorArguments, executable) ||
            TargetsExecutable(key?.GetValue(ValueName) as string, EngineStartupArguments, executable);
    }

    private static bool TargetsExecutable(string? command, string arguments, string executable) =>
        IsStartupCommand(command, arguments) && string.Equals(
            Path.GetFullPath(command[1..command.IndexOf('"', 1)]), Path.GetFullPath(executable),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsStartupCommand([NotNullWhen(true)] string? command, string arguments)
    {
        if (command is null || !command.StartsWith('"')) return false;
        var end = command.IndexOf('"', 1);
        if (end < 2 || command[(end + 1)..] != " " + arguments) return false;
        var path = command[1..end];
        return IsStartupExecutable(path);
    }

    private static bool IsStartupExecutable(string path) =>
        Path.IsPathFullyQualified(path) && !path.Contains('"') &&
        string.Equals(Path.GetFileName(path), "SyncShark.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reports registration fact without creating or repairing anything.</summary>
    public static bool ReadStartup()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = OpenStartupKey(writable: false);
        return IsProductStartupCommand(key?.GetValue(ValueName) as string);
    }

    /// <summary>Enables or disables the single supervisor registration; foreign values are preserved.</summary>
    public static string RegisterStartup(string executable, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var command = enabled ? StartupCommand(executable) : null;
        using var key = OpenStartupKey(writable: true, create: enabled);
        var current = key?.GetValue(ValueName);
        var foreign = current is not null && !IsProductStartupCommand(current as string);
        if (enabled && foreign)
            throw new InvalidOperationException(EdgeMessages.Text("startup-foreign"));
        if (!enabled)
        {
            if (!foreign) key?.DeleteValue(ValueName, throwOnMissingValue: false);
            RemoveLegacyStartup(key);
            return EdgeMessages.Text(foreign ? "startup-foreign" : "startup-removed");
        }
        if (key is null) throw new IOException(EdgeMessages.Text("startup-key-unavailable"));
        WriteStartup(key, command!);
        return EdgeMessages.Text("startup-registered");
    }

    [SupportedOSPlatform("windows")]
    private static bool WriteStartup(RegistryKey key, string command, bool automatic = false,
        string? expected = null, string? expectedLegacy = null)
    {
        BeforeStartupWriteForTest?.Invoke();
        var predecessor = key.GetValue(ValueName);
        // Only a string can be the owned legacy value; a foreign binary value must not stall migration.
        if (automatic && (!Equals(predecessor, expected) ||
            (key.GetValue(LegacyValueName) as string) != expectedLegacy)) return false;
        if (predecessor is not null && !IsProductStartupCommand(predecessor as string))
            throw new InvalidOperationException(EdgeMessages.Text("startup-foreign"));
        try
        {
            key.SetValue(ValueName, command, RegistryValueKind.String);
            if (!Equals(key.GetValue(ValueName), command) || key.GetValueKind(ValueName) != RegistryValueKind.String)
                throw new IOException(EdgeMessages.Text("startup-verification-failed"));
        }
        catch
        {
            // Never undo a concurrent foreign replacement or disable while restoring our own failed write.
            if (Equals(key.GetValue(ValueName), command))
            {
                if (predecessor is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                else key.SetValue(ValueName, predecessor, RegistryValueKind.String);
            }
            throw;
        }
        RemoveLegacyStartup(key);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveLegacyStartup(RegistryKey? key)
    {
        if (IsStartupCommand(key?.GetValue(LegacyValueName) as string, LegacyArguments))
            key!.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    /// <summary>Migrates legacy startup in place and repairs missing targets; absent and foreign entries stay untouched.</summary>
    public static void RepairStartup(string executable, Action<string> warning)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var key = OpenStartupKey(writable: true);
            var value = key?.GetValue(ValueName);
            // A legacy launcher is existing opt-in, not permission inferred from a stored flag.
            var legacy = key?.GetValue(LegacyValueName);
            if (value is null ? !IsStartupCommand(legacy as string, LegacyArguments) : !IsProductStartupCommand(value as string)) return;
            var current = (value as string ?? legacy as string)!;
            var registered = current[1..current.IndexOf('"', 1)];
            var exists = File.Exists(registered);
            if (IsStartupCommand(value as string, EngineStartupArguments) && exists)
            {
                RemoveLegacyStartup(key);
                return;
            }
            // Old binaries are not Engine-capable merely because their registered file still exists.
            var command = StartupCommand(SelectStartupExecutable(executable));
            if (WriteStartup(key!, command, automatic: true, expected: value as string, expectedLegacy: legacy as string))
                ProgressGuard.Report(warning, EdgeMessages.Text("startup-repointed"));
        }
        catch (Exception error)
        {
            ProgressGuard.Report(warning, EdgeMessages.Text("startup-repair-failed", error.Message));
        }
    }

    public static string SupervisorStopName(bool engine) => @"Local\" +
        AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) +
        (engine ? ".stop-engine-supervisor" : ".stop-supervisor");

    /// <summary>Signals only the selected supervisor generation; absence does not start one.</summary>
    public static void StopSupervisor(bool engine = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        try
        {
            using var stop = EventWaitHandle.OpenExisting(SupervisorStopName(engine));
            stop.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    /// <summary>Starts the legacy host or its supervisor, retaining the legacy API's meaning.</summary>
    public static ChildProcess StartDetached(string executable, bool supervisor, Action<string>? warning = null) =>
        StartNative(executable, supervisor ? LegacySupervisorArguments : HostArguments, warning);

    /// <summary>Starts the Engine host or its supervisor using the same owned native launch path.</summary>
    /// <param name="executable">The absolute product executable.</param>
    /// <param name="supervisor">True to start the supervisor rather than the host.</param>
    /// <param name="warning">Receives launch limitations.</param>
    /// <param name="readiness">
    /// An inheritable pipe write handle that the child, and no other handle, inherits; the child claims it
    /// with <see cref="TakeReadinessPipe"/>.
    /// </param>
    public static ChildProcess StartEngineDetached(string executable, bool supervisor, Action<string>? warning = null,
        SafePipeHandle? readiness = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var arguments = supervisor ? EngineSupervisorArguments : EngineHostArguments;
        return readiness is null ? StartNative(executable, arguments, warning)
            : StartWithReadiness(executable, arguments, warning, readiness);
    }

    /// <summary>
    /// Claims the readiness pipe this process's launcher handed over, or null when there was none. The variable
    /// is cleared so a supervisor's later children cannot read a handle number that names nothing in them.
    /// </summary>
    public static AnonymousPipeClientStream? TakeReadinessPipe()
    {
        var handle = Environment.GetEnvironmentVariable(ReadinessVariable);
        if (handle is null) return null;
        Environment.SetEnvironmentVariable(ReadinessVariable, null);
        return new AnonymousPipeClientStream(PipeDirection.Out, handle);
    }

    private static ChildProcess StartNative(string executable, string arguments, Action<string>? warning)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() } };
        return Launch(executable, arguments, warning, ref startup, inherit: false, flags: 0, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    private static ChildProcess StartWithReadiness(string executable, string arguments, Action<string>? warning,
        SafePipeHandle readiness)
    {
        // Plain inheritance would also pass a caller's redirected stdout, holding its reader open for the host's life.
        IntPtr attributes = IntPtr.Zero, inherited = IntPtr.Zero, environment = IntPtr.Zero;
        var initialized = false;
        try
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // fails by design, reporting the size
            attributes = Marshal.AllocHGlobal((nint)size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            inherited = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(inherited, readiness.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, inherited, (nuint)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            environment = Marshal.StringToHGlobalUni(EnvironmentWithReadiness(readiness));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributes,
            };
            return Launch(executable, arguments, warning, ref startup, inherit: true,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environment);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(environment);
        }
    }

    private static string EnvironmentWithReadiness(SafePipeHandle readiness)
    {
        // CreateProcess requires names sorted case-insensitively and a final extra NUL.
        var variables = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            variables[(string)variable.Key] = (string?)variable.Value;
        variables[ReadinessVariable] = readiness.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture);
        return string.Concat(variables.Select(variable => $"{variable.Key}={variable.Value}\0")) + "\0";
    }

    private static ChildProcess Launch(string executable, string arguments, Action<string>? warning,
        ref StartupInfoEx startup, bool inherit, uint flags, IntPtr environment)
    {
        var text = $"\"{Path.GetFullPath(executable)}\" {arguments}";
        if (!CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, inherit, 0x01000008 | flags,
                environment, Path.GetDirectoryName(executable), ref startup, out var process))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 5) throw new Win32Exception(error);
            // Some launchers prohibit breakaway. Still start a console-independent host rather than refusing.
            // Windows may retain the caller's job in this fallback; explain that limitation explicitly.
            ProgressGuard.Report(warning, EdgeMessages.Text("startup-job-breakaway"));
            if (!CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, inherit, 0x00000008 | flags,
                    environment, Path.GetDirectoryName(executable), ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        CloseHandle(process.Thread);
        return new ChildProcess(new SafeProcessHandle(process.Process, ownsHandle: true), process.ProcessId);
    }

    /// <summary>Pins the authenticated live pipe server for lifetime observation without owning its termination.</summary>
    public static ChildProcess ObserveTrustedPeer(PipeStream pipe)
    {
        if (!LocalPipePeer.Trusted(pipe, server: false)) throw new UnauthorizedAccessException();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        const uint synchronizeAndQueryLimitedInformation = 0x00100000 | 0x1000;
        var handle = OpenProcess(synchronizeAndQueryLimitedInformation, false, processId);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!LocalPipePeer.Trusted(pipe, server: false) ||
                !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var confirmed) || confirmed != processId ||
                WaitForSingleObject(handle, 0) != WaitTimeout)
                throw new UnauthorizedAccessException();
            return new ChildProcess(handle, checked((int)processId));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Owns a launched or observed process handle; disposal never terminates the process.</summary>
    public sealed class ChildProcess : AutoSyncSupervisor.IChild
    {
        private readonly SafeProcessHandle _handle;
        internal ChildProcess(SafeProcessHandle handle, int processId) { _handle = handle; ProcessId = processId; }
        public int ProcessId { get; }
        public bool HasExited => WaitForSingleObject(_handle, 0) == 0;
        public void WaitForExit()
        {
            if (WaitForSingleObject(_handle, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public int ExitCode => GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : throw new Win32Exception(Marshal.GetLastWin32Error());
        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value,
        nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
