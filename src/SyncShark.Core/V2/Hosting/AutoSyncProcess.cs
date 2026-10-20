using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>Windows process/startup edge. Registration is explicitly opt-in.</summary>
public static class AutoSyncProcess
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sync Shark AutoSync";
    public const string HostArguments = "--core v2 --background-host";
    public const string SupervisorArguments = "--core v2 --autosync-supervisor";

    public static string StartupCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"'))
            throw new ArgumentException("An absolute executable path without quotes is required.", nameof(executable));
        return $"\"{Path.GetFullPath(executable)}\" {SupervisorArguments}";
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

    public static bool StopsForExistingOwner(int exitCode) => exitCode == (int)SyncCliExitCode.Busy;

    public static void RecordExistingOwnerNotice(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One overwrite-only spool notice: the other owner holds the database, so never open it here.
        File.WriteAllText(path, "AutoSync startup is blocked because the GUI or another maintenance owner holds the product lock. " +
            "Automatic relaunch has stopped. Close that owner safely, then Retry with autosync start. " +
            "Ignore by leaving AutoSync stopped; disable GUI Start With Windows to avoid competing logon owners.");
    }

    public static string RegisterStartup(string executable, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("The per-user startup key could not be opened.");
        // Uninstall is idempotent regardless of which version/build registered this product value.
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return "Removed AutoSync logon startup; GUI startup and running processes are unchanged.";
        }
        var command = StartupCommand(executable);
        var warning = key.GetValue(ValueName) is string current && !string.Equals(current, command, StringComparison.OrdinalIgnoreCase)
            ? "Replaced a stale AutoSync startup entry. " : string.Empty;
        if (key.GetValue("Sync Shark") is not null)
            warning += "GUI logon startup and its setting were left unchanged. Disable GUI Start With Windows in settings to avoid competing owners. ";
        key.SetValue(ValueName, command, RegistryValueKind.String);
        return warning + "Registered per-user AutoSync logon startup with crash supervision.";
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
            warning?.Invoke("This launcher prohibits job breakaway. Starting without a console; its job may still stop AutoSync when the launcher closes. Per-user logon startup avoids this launcher's job.");
            if (!CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, false, 0x00000008,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        CloseHandle(process.Thread);
        return new ChildProcess(new SafeProcessHandle(process.Process, ownsHandle: true));
    }

    public sealed class ChildProcess : IDisposable
    {
        private readonly SafeProcessHandle _handle;
        internal ChildProcess(SafeProcessHandle handle) => _handle = handle;
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
