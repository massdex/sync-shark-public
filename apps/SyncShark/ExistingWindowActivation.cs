using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark;

internal static class ExistingWindowActivation
{
    // Process-local window message; no new listener, pipe, file, credential or service.
    internal const int ShowMessage = 0x8000 + 113;
    internal static bool TryRaise(ExplorerRequest? request = null)
    {
        var ownPath = Environment.ProcessPath;
        if (ownPath is null) return false;
        try { ownPath = ResolveExecutable(ownPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        foreach (var process in Process.GetProcessesByName("SyncShark"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var otherPath = process.MainModule?.FileName;
                    if (otherPath is null || !string.Equals(ResolveExecutable(otherPath), ownPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var raised = false;
                    AllowSetForegroundWindow((uint)process.Id);
                    EnumWindows((window, _) =>
                    {
                        GetWindowThreadProcessId(window, out var owner);
                        if (owner != process.Id) return true;
                        var title = new System.Text.StringBuilder(100);
                        GetWindowText(window, title, title.Capacity);
                        if (title.ToString() != "Sync Shark") return true;
                        raised = request is null ? PostMessage(window, ShowMessage, IntPtr.Zero, IntPtr.Zero)
                            : SendRequest(window, request);
                        return !raised;
                    }, IntPtr.Zero);
                    if (raised) return true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException) { }
            }
        }
        return false;
    }
    private static bool SendRequest(IntPtr target, ExplorerRequest request)
    {
        // WM_COPYDATA copies a bounded payload synchronously; the receiver queues work only after
        // checking this sender's actual executable. No path-valued shell command is evaluated.
        using var sender = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Sync Shark")
            { Width = 0, Height = 0, WindowStyle = 0 });
        var text = Marshal.StringToHGlobalUni(request.Serialize());
        try
        {
            var payload = new CopyData { Kind = (IntPtr)ShowMessage, Bytes = checked((request.Serialize().Length + 1) * 2), Data = text };
            return SendMessageTimeout(target, 0x004A, sender.Handle, ref payload, 2, 2000, out var result) != IntPtr.Zero && result != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(text); }
    }

    internal static ExplorerRequest? ReadRequest(IntPtr sender, IntPtr payload)
    {
        if (sender == IntPtr.Zero || payload == IntPtr.Zero) return null;
        try
        {
            GetWindowThreadProcessId(sender, out var pid);
            using var process = Process.GetProcessById(checked((int)pid));
            if (process.MainModule?.FileName is not { } path || Environment.ProcessPath is not { } own ||
                !string.Equals(ResolveExecutable(path), ResolveExecutable(own), StringComparison.OrdinalIgnoreCase)) return null;
            var copy = Marshal.PtrToStructure<CopyData>(payload);
            if (copy.Kind != (IntPtr)ShowMessage || copy.Bytes is < 2 or > 131072 || copy.Bytes % 2 != 0 || copy.Data == IntPtr.Zero) return null;
            var value = Marshal.PtrToStringUni(copy.Data, copy.Bytes / 2) ?? "";
            if (!value.EndsWith('\0') || value[..^1].Contains('\0')) return null;
            return ExplorerRequest.Deserialize(value[..^1]);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CopyData { public IntPtr Kind; public int Bytes; public IntPtr Data; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr target, uint message,
        IntPtr sender, ref CopyData data, uint flags, uint timeout, out IntPtr result);
    // One shared resolver: the handle-based implementation now lives with the installed-runtime
    // admission in SyncShark.Core, and this project keeps its existing internal surface and
    // every native window and IPC check unchanged.
    internal static string ResolveExecutable(string path) => SyncSharkRuntimePaths.ResolveExecutablePath(path);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
}
