using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Versioned, store-scoped local protocol. This is a transport boundary, not an alternative state authority.
/// Production must supply the existing product-lock lease before constructing an engine.
/// </summary>
public static class AutoSyncHostProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>No state is opened or created when deriving the address.</summary>
    public static string PipeName(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        if (!Path.IsPathFullyQualified(lockPath)) throw new ArgumentException("An absolute authority lock path is required.", nameof(lockPath));
        var path = Path.GetFullPath(lockPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        // The same store has one authority per user/session, matching the existing GUI bridge's peer boundary.
        var scope = path + "\n" + Environment.UserDomainName + "\\" + Environment.UserName + "\n" +
            Process.GetCurrentProcess().SessionId;
        return "SyncShark.AutoSync.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("The host response exceeds the bounded IPC frame; request a smaller page.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Invalid or oversized host frame.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("An empty host frame is not a request.");
    }

    // Reuse the existing bridge's same-user, same-session, exact-executable checks. CurrentUserOnly also
    // restricts the pipe's ACL. Tests inject a peer verifier; production never accepts an arbitrary caller PID.
    internal static bool TrustedPeer(PipeStream pipe, bool server)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (server)
        {
            var computer = new StringBuilder(256);
            // ERROR_PIPE_LOCAL is the documented local-transport result. A successful lookup is
            // an SMB client, including loopback SMB; reject it even when it names this machine.
            if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, computer, (uint)computer.Capacity) ||
                Marshal.GetLastWin32Error() != 229) return false;
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
