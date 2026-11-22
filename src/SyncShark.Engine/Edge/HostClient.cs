using System.Buffers.Binary;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Sends one command to the authenticated host. Never replays a request.</summary>
public sealed class HostClient(string? pipeName = null)
{
    /// <summary>Launch the sibling host only after definite absence and wait for its private readiness response.</summary>
    public static async Task<int> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, ResourceNames.HostExecutable)) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add(HostServer.ReadyArgument);
        using var child = Process.Start(start) ?? throw new IOException(nameof(Process.Start));
        var line = await child.StandardOutput.ReadLineAsync(ct);
        if (line == "1") return 0;
        if (line != null) throw new InvalidDataException(nameof(HostServer.Ready));
        await child.WaitForExitAsync(ct);
        return child.ExitCode == 0 ? 1 : child.ExitCode;
    }

    /// <summary>Legacy one-writer entry; the two-writer command path retains terminal versus uncertain outcomes.</summary>
    public async Task<int> SendAsync(string[] args, TextWriter output, CancellationToken ct) =>
        (await SendAsync(args, output, Console.Error, ct)).ExitCode ?? 1;

    /// <summary>Null exit means definite pre-send absence; ReceivedExit distinguishes a command failure from a broken exchange.</summary>
    public async Task<(int? ExitCode, bool ReceivedExit)> SendAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var name = pipeName ?? HostServer.PipeName;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(300, ct); } // Predecessor connection deadline; a busy pipe is not absence.
        catch (TimeoutException) {
            if (!WaitNamedPipe(@"\\.\pipe\" + name, 1) && Marshal.GetLastWin32Error() == 2) return (null, false);
            throw;
        }
        if (!HostServer.Trusted(pipe)) throw new UnauthorizedAccessException();
        return await ExchangeAsync(pipe, args, output, error, ct);
    }

    /// <summary>Run the one-command protocol on an already authenticated stream.</summary>
    public static async Task<(int? ExitCode, bool ReceivedExit)> ExchangeAsync(Stream pipe, string[] args, TextWriter output, TextWriter error, CancellationToken ct)
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
