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
        return found && SyncCliBridgeTransport.IsTrustedPeer(pid, out _);
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
}
