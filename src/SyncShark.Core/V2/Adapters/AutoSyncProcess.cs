using System.ComponentModel;
using System.Diagnostics;
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
    internal const string LegacyArguments = "--background";

    public static string StartupCommand(string executable)
    {
        if (!IsStartupExecutable(executable))
            throw new ArgumentException(EdgeMessages.Text("startup-executable-invalid"), nameof(executable));
        return $"\"{Path.GetFullPath(executable)}\" {SupervisorArguments}";
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
    public static string ExistingOwnerNoticePath => ExistingOwnerNoticePathFor(SyncDataLocation.ProductRoot);

    internal static string ExistingOwnerNoticePathFor(string productRoot) =>
        Path.Combine(productRoot, "autosync-owner-notice.txt");

    public static void RecordExistingOwnerNotice(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One overwrite-only spool notice: the other owner holds the database, so never open it here.
        File.WriteAllText(path, "AutoSync startup is blocked because the GUI or another maintenance owner holds the product lock. " +
            "Automatic relaunch will retry with backoff (up to five minutes). Let maintenance finish or close the owner safely. " +
            "Retry immediately with autosync stop then autosync start, or Ignore by running autosync stop.");
    }

    public static void ClearExistingOwnerNotice(string path)
    {
        if (File.Exists(path)) File.WriteAllText(path, string.Empty);
    }

    /// <summary>Ownership follows the product command, never its deployment directory.</summary>
    public static bool IsProductStartupCommand(string? command) => IsStartupCommand(command, SupervisorArguments);

    private static bool IsStartupCommand(string? command, string arguments)
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
    private static void WriteStartup(RegistryKey key, string command)
    {
        BeforeStartupWriteForTest?.Invoke();
        key.SetValue(ValueName, command, RegistryValueKind.String);
        // Retire the legacy launcher only after the supervisor registration succeeds.
        RemoveLegacyStartup(key);
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
            var legacy = key?.GetValue(LegacyValueName) as string;
            if (value is null ? !IsStartupCommand(legacy, LegacyArguments) : !IsProductStartupCommand(value as string)) return;
            var current = (value as string ?? legacy)!;
            var registered = current[1..current.IndexOf('"', 1)];
            var exists = File.Exists(registered);
            if (value is not null && exists)
            {
                RemoveLegacyStartup(key); // The replacement supervisor is already registered and available.
                return;
            }
            // A working legacy launcher migrates in place, never to the build opening the GUI.
            var command = StartupCommand(exists ? registered : SelectStartupExecutable(executable));
            WriteStartup(key!, command);
            ProgressGuard.Report(warning, EdgeMessages.Text("startup-repointed"));
        }
        catch (Exception error)
        {
            ProgressGuard.Report(warning, EdgeMessages.Text("startup-repair-failed", error.Message));
        }
    }

    /// <summary>Owns the original kernel handle even if the child exits before this method returns.</summary>
    public static ChildProcess StartDetached(string executable, bool supervisor, Action<string>? warning = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var text = $"\"{Path.GetFullPath(executable)}\" {(supervisor ? SupervisorArguments : HostArguments)}";
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, false, 0x01000008,
                IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var process))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 5) throw new Win32Exception(error);
            // Some launchers prohibit breakaway. Still start a console-independent host rather than refusing.
            // Windows may retain the caller's job in this fallback; explain that limitation explicitly.
            ProgressGuard.Report(warning, "This launcher prohibits job breakaway. Starting without a console; its job may still stop AutoSync when the launcher closes. Per-user logon startup avoids this launcher's job.");
            if (!CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, false, 0x00000008,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        CloseHandle(process.Thread);
        return new ChildProcess(new SafeProcessHandle(process.Process, ownsHandle: true), process.ProcessId);
    }

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
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
