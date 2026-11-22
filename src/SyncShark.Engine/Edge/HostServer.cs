using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Owns the one local host address through shutdown and the last admitted command.</summary>
public sealed class HostServer(string? pipeName = null) : IDisposable
{
    internal static string PipeName {
        get {
            using var identity = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess();
            return $"{nameof(HostServer)}.{ResourceNames.HostExecutable}.{identity.User!.Value}.{process.SessionId}";
        }
    }
    private NamedPipeServerStream? waiting;
    private NamedPipeServerStream Listen(bool first) => new(pipeName ?? PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    /// <summary>Claim before opening product state; authenticate any already-running instance.</summary>
    public async Task<bool> ClaimAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { waiting = Listen(true); return true; }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xffff) is 5 or 231) {
            using var probe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await probe.ConnectAsync(300, ct);
            if (!Trusted(probe)) throw new UnauthorizedAccessException();
            return false;
        }
    }
    /// <summary>Serve the claimed pipe; stop intake and drain before releasing its name.</summary>
    public async Task ServeAsync(Run.Host host, CancellationToken ct)
    {
        var commands = new Commands();
        await ServeAsync((args, output, error, token) => commands.ExecuteAsync(args, host, output, token), host.StopAsync, ct);
    }
    /// <summary>Serve one authenticated command per connection; the dispatcher owns command semantics.</summary>
    public async Task ServeAsync(Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> execute, Func<CancellationToken,Task> shutdown, CancellationToken ct)
    {
        if (waiting == null) throw new InvalidOperationException(nameof(ClaimAsync));
        var active = new List<Task>(); using var intake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try {
            while (!intake.IsCancellationRequested) {
                var connected = waiting;
                try { await connected.WaitForConnectionAsync(intake.Token); }
                catch (IOException e) when ((e.HResult & 0xffff) is 109 or 232 or 233) { waiting = Listen(false); connected.Dispose(); continue; }
                waiting = Listen(false);
                foreach (var task in active.Where(t => t.IsCompleted).ToArray()) { await task; active.Remove(task); }
                if (active.Count >= 32) { connected.Dispose(); continue; } // Predecessor concurrent-command limit.
                active.Add(HandleAsync(connected));
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally {
            try { await intake.CancelAsync(); await shutdown(CancellationToken.None); }
            finally { try { await Task.WhenAll(active); } finally { Dispose(); } }
        }
        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            using (pipe) try {
                if (!Trusted(pipe)) return;
                var request = await PipeFrame.ReadAsync(pipe, intake.Token);
                if (request[0] != PipeFrame.Arguments) return;
                var args = JsonSerializer.Deserialize<string[]>(request.AsSpan(1));
                if (args == null || args.Any(a => a == null)) return;
                await PipeFrame.WriteAsync(pipe, new byte[] {PipeFrame.Accepted}, default);
                using var gate = new SemaphoreSlim(1);
                using var output = new Writer(pipe, PipeFrame.Output, gate); using var error = new Writer(pipe, PipeFrame.Error, gate);
                var code = await execute(args, output, error, CancellationToken.None);
                var exit = new byte[1 + sizeof(int)]; exit[0] = PipeFrame.Exit;
                BinaryPrimitives.WriteInt32LittleEndian(exit.AsSpan(1), code); await PipeFrame.WriteAsync(pipe, exit, default);
            } catch (Exception e) when (e is JsonException or InvalidDataException or EndOfStreamException || e is OperationCanceledException && intake.IsCancellationRequested || e is IOException && (e.HResult & 0xffff) is 109 or 232 or 233) { /* End this connection without replay. */ }
        }
    }
    /// <summary>Authenticate a local peer by SID, session and kernel image path, not its claimed name.</summary>
    public static bool Trusted(PipeStream pipe)
    {
        if (!pipe.IsConnected) return false;
        try {
            uint pid;
            if (pipe is NamedPipeServerStream) {
                var machine = new StringBuilder(256);
                if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, machine, machine.Capacity) || Marshal.GetLastWin32Error() != 229 || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)) return false;
            } else if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)) return false;
            using var peer = Process.GetProcessById(checked((int)pid)); using var own = Process.GetCurrentProcess();
            if (pid == own.Id || peer.SessionId != own.SessionId || !OpenProcessToken(peer.SafeHandle, 8, out var token)) return false;
            using (token) using (var other = new WindowsIdentity(token.DangerousGetHandle())) using (var me = WindowsIdentity.GetCurrent()) {
                if (me.User is not { } selfSid || other.User is not { } peerSid || !selfSid.Equals(peerSid)) return false;
            }
            var actual = Image(peer); var local = Image(own);
            return actual != null && local != null && string.Equals(Path.GetDirectoryName(actual), Path.GetDirectoryName(local), StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(Path.GetFileName(actual), ResourceNames.HostExecutable, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(actual), ResourceNames.CliExecutable, StringComparison.OrdinalIgnoreCase));
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException) { return false; }
    }
    // PROCESS_NAME_NATIVE identifies the loaded image without following a mutable active junction.
    private static string? Image(Process process) {
        int length = 32768; var path = new StringBuilder(length);
        return QueryFullProcessImageName(process.SafeHandle, 1, path, ref length) ? path.ToString() : null;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder name, int length);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle process, int rights, out SafeAccessTokenHandle token);
    public void Dispose() { waiting?.Dispose(); waiting = null; }
    private sealed class Writer(Stream pipe, byte kind, SemaphoreSlim gate) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value) => WriteAsync(value).GetAwaiter().GetResult();
        public override Task WriteLineAsync(string? value) => WriteAsync(value + NewLine);
        public override async Task WriteAsync(string? value) {
            if (string.IsNullOrEmpty(value)) return;
            await gate.WaitAsync();
            try {
                int offset = 0;
                while (offset < value.Length) {
                    int chars = Math.Min(value.Length - offset, PipeFrame.MaximumPayload / 3);
                    if (offset + chars < value.Length && char.IsHighSurrogate(value[offset + chars - 1])) chars--;
                    var message = new byte[1 + Encoding.GetByteCount(value.AsSpan(offset,chars))]; message[0] = kind;
                    Encoding.GetBytes(value.AsSpan(offset,chars), message.AsSpan(1));
                    await PipeFrame.WriteAsync(pipe, message, default); offset += chars;
                }
            } finally { gate.Release(); }
        }
    }
}
