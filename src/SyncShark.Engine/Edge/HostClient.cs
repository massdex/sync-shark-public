using System.Buffers.Binary;
using System.IO.Pipes;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Sends one command to the authenticated host. Never replays a request.</summary>
public sealed class HostClient
{
    /// <summary>Launch the sibling host only after definite absence and wait for its private readiness response.</summary>
    public static Task<int> StartAsync(CancellationToken ct) =>
        StartAsync(Path.Combine(AppContext.BaseDirectory, ResourceNames.HostExecutable), HostServer.ReadyArgument, ct);

    /// <summary>Inherit only the readiness pipe so the caller's readers can observe EOF.</summary>
    public static async Task<int> StartAsync(string exe, string arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var ready = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var host = Launch(exe, arguments, ready.ClientSafePipeHandle);
        ready.DisposeLocalCopyOfClientHandle();
        using var reader = new StreamReader(ready);
        var line = await reader.ReadLineAsync(ct);
        if (line == "1") return 0;
        if (line != null) throw new InvalidDataException(nameof(HostServer.Ready));
        WaitForSingleObject(host, Infinite);
        return GetExitCodeProcess(host, out var code) && code != 0 ? unchecked((int)code) : 1;
    }

    private const int HandleList = 0x00020002, UseStdHandles = 0x100;
    private const uint ExtendedStartupInfo = 0x00080000, NoWindow = 0x08000000, Infinite = uint.MaxValue;

    private static SafeProcessHandle Launch(string exe, string arguments, SafePipeHandle stdout)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        nint handles = 0;
        bool initialized = false;
        try {
            initialized = InitializeProcThreadAttributeList(list, 1, 0, ref size);
            if (!initialized) throw new Win32Exception();
            handles = Marshal.AllocHGlobal(IntPtr.Size);
            var output = stdout.DangerousGetHandle();
            Marshal.WriteIntPtr(handles, output);
            if (!UpdateProcThreadAttribute(list, 0, HandleList, handles, IntPtr.Size, 0, 0)) throw new Win32Exception();
            var info = new StartupInfoEx { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = UseStdHandles,
                hStdOutput = output, lpAttributeList = list };
            if (!CreateProcessW(exe, new StringBuilder($"\"{exe}\" {arguments}"), 0, 0, true,
                    ExtendedStartupInfo | NoWindow, 0, null, ref info, out var process)) throw new Win32Exception();
            CloseHandle(process.hThread);
            return new SafeProcessHandle(process.hProcess, true);
        } finally {
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(list);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError, lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, int flags, nint attribute, nint value, nint size, nint previous, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string exe, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx info, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>Null exit means definite pre-send absence; ReceivedExit distinguishes a command failure from a broken exchange.</summary>
    public async Task<(int? ExitCode, bool ReceivedExit)> SendAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct, bool waitForHostExit = false)
    {
        var name = HostServer.PipeName;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(300, ct); } // Predecessor connection deadline; a busy pipe is not absence.
        catch (TimeoutException) {
            if (!WaitNamedPipe(@"\\.\pipe\" + name, 1) && Marshal.GetLastWin32Error() == 2) return (null, false);
            throw;
        }
        using var peer = HostServer.AuthenticatedPeer(pipe) ?? throw new UnauthorizedAccessException();
        var result = await ExchangeAsync(pipe, args, output, error, ct);
        pipe.Dispose();
        if (waitForHostExit && result is (0, true)) await peer.WaitForExitAsync(ct);
        return result;
    }

    /// <summary>Run the one-command protocol on an already authenticated stream.</summary>
    private static async Task<(int? ExitCode, bool ReceivedExit)> ExchangeAsync(Stream pipe, string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        byte[] request = [PipeFrame.Arguments, ..JsonSerializer.SerializeToUtf8Bytes(args)];
        try {
            await PipeFrame.WriteAsync(pipe, request, ct);
            if ((await PipeFrame.ReadAsync(pipe, ct))[0] != PipeFrame.Accepted) throw new InvalidDataException(nameof(PipeFrame.Accepted));
            while (true) {
                var reply = await PipeFrame.ReadAsync(pipe, ct);
                if (reply[0] == PipeFrame.Exit) return (BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1)), true);
                var writer = reply[0] switch { PipeFrame.Output => output, PipeFrame.Error => error, _ => throw new InvalidDataException(nameof(reply)) };
                await writer.WriteAsync(Encoding.UTF8.GetString(reply, 1, reply.Length - 1));
            }
        } catch (IOException) { return (1, false); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string path, uint timeout);
}
