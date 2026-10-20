using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Massdex.SyncShark.V2;

/// <summary>Windows process/startup edge. Building commands is pure; registration is explicitly opt-in.</summary>
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

    public static void RegisterStartup(string executable, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var command = StartupCommand(executable);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("The per-user startup key could not be opened.");
        if (key.GetValue(ValueName) is string current && !string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The AutoSync startup entry points elsewhere. Inspect it before replacing it; nothing changed.");
        if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>No inherited handles/console. Breakaway failure is explicit, never silently attached to a kill job.</summary>
    public static Process StartDetached(string executable, bool supervisor)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var command = new StringBuilder($"\"{Path.GetFullPath(executable)}\" {(supervisor ? SupervisorArguments : HostArguments)}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS. No redirected pipes that could fill or die with the caller.
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x01000008,
                IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not detach AutoSync from this launcher's job. Start it from Explorer or install per-user startup; no attached host was started.");
        try { return Process.GetProcessById(process.ProcessId); }
        finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
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
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
