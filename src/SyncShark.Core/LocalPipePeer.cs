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

    // Microsoft Windows system error codes 0-499 (https://learn.microsoft.com/windows/win32/debug/system-error-codes--0-499-):
    // ERROR_BROKEN_PIPE (109), ERROR_NO_DATA (232), ERROR_PIPE_NOT_CONNECTED (233). These invalidate the one
    // connection, not the listener's lifetime.
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const int ErrorPipeNotConnected = 233;

    /// <summary>True when the error means one pipe connection ended, not that the listener itself failed.</summary>
    public static bool Disconnected(IOException error) =>
        (error.HResult & 0xffff) is ErrorBrokenPipe or ErrorNoData or ErrorPipeNotConnected;

    /// <summary>Waits for one connection, or recycles the listener when a probe connects and closes before the
    /// accept begins. <c>Accepted</c> is false when the abandoned instance was replaced; <c>Waiting</c> is then the
    /// replacement the caller must accept on. The replacement is created before the abandoned instance is disposed,
    /// so the pipe name is never left unowned.</summary>
    public static async Task<(bool Accepted, NamedPipeServerStream Waiting)> AcceptAsync(
        NamedPipeServerStream waiting, Func<NamedPipeServerStream> createListener, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(createListener);
        try
        {
            await waiting.WaitForConnectionAsync(token).ConfigureAwait(false);
            return (true, waiting);
        }
        catch (IOException error) when (Disconnected(error))
        {
            var abandoned = waiting;
            var replacement = createListener();
            abandoned.Dispose();
            return (false, replacement);
        }
    }

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
