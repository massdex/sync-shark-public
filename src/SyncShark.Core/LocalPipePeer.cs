using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

/// <summary>
/// Shared trust boundary for local named-pipe peers, used by both the legacy AutoSync protocol and the
/// Engine host protocol instead of each protocol repeating the native peer checks.
/// </summary>
public static class LocalPipePeer
{
    // Windows ERROR_PIPE_LOCAL is the only documented result that proves a local transport. A successful
    // lookup is an SMB client, including loopback SMB, and is rejected even when it names this machine.
    private const int ErrorPipeLocal = 229;

    /// <summary>
    /// True when the connected pipe's peer is this product executable, in the same Windows session and
    /// under the same user token as this process.
    /// </summary>
    /// <param name="pipe">A connected named-pipe stream.</param>
    /// <param name="server">True when verifying a client on a server stream; false when verifying the server on a client stream.</param>
    public static bool Trusted(PipeStream pipe, bool server)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows()) return false;
        if (server)
        {
            var computer = new StringBuilder(256);
            // A successful lookup means an SMB client; only ERROR_PIPE_LOCAL proves a local transport.
            if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, computer, (uint)computer.Capacity) ||
                Marshal.GetLastWin32Error() != ErrorPipeLocal)
                return false;
        }
        uint pid;
        var found = server
            ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        return found && TrustedProcess(pid);
    }

    // The peer must be the same product executable, in the same Windows session, under the same user
    // token as this process. A claimed pid or a process name alone is never enough.
    private static bool TrustedProcess(uint processId)
    {
        if (processId == 0 || processId == (uint)Environment.ProcessId) return false;
        try
        {
            using var peer = Process.GetProcessById((int)processId);
            if (peer.SessionId != Process.GetCurrentProcess().SessionId) return false;
            var peerPath = peer.MainModule?.FileName;
            var ownPath = Environment.ProcessPath;
            if (peerPath is null || ownPath is null ||
                !string.Equals(ResolveExecutable(peerPath), ResolveExecutable(ownPath),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return SameUser(peer, Process.GetCurrentProcess());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException or IOException or
                                          UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveExecutable(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return PilotDirectoryLease.FinalPath(handle);
    }

    private static bool SameUser(Process first, Process second)
    {
        if (!OpenProcessToken(first.Handle, TokenQuery, out var firstToken)) return false;
        try
        {
            if (!OpenProcessToken(second.Handle, TokenQuery, out var secondToken)) return false;
            try
            {
                return EqualTokenUser(firstToken, secondToken);
            }
            finally { CloseHandle(secondToken); }
        }
        finally { CloseHandle(firstToken); }
    }

    private static bool EqualTokenUser(IntPtr first, IntPtr second)
    {
        GetTokenInformation(first, TokenUser, IntPtr.Zero, 0, out var firstLength);
        GetTokenInformation(second, TokenUser, IntPtr.Zero, 0, out var secondLength);
        if (firstLength <= 0 || secondLength <= 0) return false;
        var firstBuffer = Marshal.AllocHGlobal(firstLength);
        var secondBuffer = Marshal.AllocHGlobal(secondLength);
        try
        {
            if (!GetTokenInformation(first, TokenUser, firstBuffer, firstLength, out _)) return false;
            if (!GetTokenInformation(second, TokenUser, secondBuffer, secondLength, out _)) return false;
            var firstSid = Marshal.ReadIntPtr(firstBuffer);
            var secondSid = Marshal.ReadIntPtr(secondBuffer);
            return firstSid != IntPtr.Zero && secondSid != IntPtr.Zero && EqualSid(firstSid, secondSid);
        }
        finally
        {
            Marshal.FreeHGlobal(firstBuffer);
            Marshal.FreeHGlobal(secondBuffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder name, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
        int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern bool EqualSid(IntPtr first, IntPtr second);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // OpenProcessToken takes TOKEN_* access rights, not PROCESS_* rights: TOKEN_QUERY (0x0008) is the
    // minimum access needed for GetTokenInformation(TokenUser).
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
}
