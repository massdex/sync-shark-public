using System.IO.Pipes;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>One trusted connection of the one-command engine host pipe (design 8.8, V3 C16). It verifies the peer
/// with the shared LocalPipePeer check, reads exactly one Arguments frame, acknowledges it with exactly one Accepted
/// frame before any output, streams the command's standard output and error through EnginePipeWriter, then sends
/// exactly one Exit frame and closes. A clean end of stream before any header is the supervisor's observation-only
/// attach and ends silently; a malformed or untrusted request closes the connection without a frame, so the client
/// reports its existing failure and never replays the command. The command delegate is the host's own dispatcher:
/// it executes the registered arguments through the shared registry and EngineCommands against the host's
/// already-held authority, and never attaches recursively, opens a second product lock or falls back to a direct
/// runtime. Each connection has its own pipe writers and runs on one command path, so the two output streams are
/// never written concurrently. This transport holds no request history and has no uncertainty exception path of
/// its own.</summary>
public sealed class EngineHostServer
{
    private readonly Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> _execute;

    /// <summary>Creates the server over the host's command dispatcher.</summary>
    private readonly Func<CancellationToken, IAsyncEnumerable<HostStatusUpdate>>? _subscribe;

    public EngineHostServer(Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> execute,
        Func<CancellationToken, IAsyncEnumerable<HostStatusUpdate>>? subscribe = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _subscribe = subscribe;
    }

    /// <summary>Serves exactly one command on one connected trusted pipe and disposes the connection.</summary>
    public async Task ServeAsync(PipeStream pipe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using (pipe)
        {
            if (!LocalPipePeer.Trusted(pipe, server: true)) return;
            string[] arguments;
            try
            {
                // A clean end of stream before any header is an observation attach, not a command.
                var frame = await EngineHostFrame.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (frame is { Kind: EngineHostFrameKind.SubscribeStatus } && _subscribe is not null)
                {
                    await ServeStatusAsync(pipe, ct).ConfigureAwait(false);
                    return;
                }
                if (frame is null || frame.Value.Kind != EngineHostFrameKind.Arguments) return;
                arguments = frame.Value.DecodeArguments();
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                return;
            }
            try
            {
                await EngineHostFrame.WriteAsync(pipe,
                    new EngineHostFrame(EngineHostFrameKind.Accepted, ReadOnlyMemory<byte>.Empty), ct)
                    .ConfigureAwait(false);
                using var stdout = new EnginePipeWriter(pipe, EngineHostFrameKind.StandardOutput);
                using var stderr = new EnginePipeWriter(pipe, EngineHostFrameKind.StandardError);
                var exitCode = await _execute(arguments, stdout, stderr, ct).ConfigureAwait(false);
                await EngineHostFrame.WriteAsync(pipe, EngineHostFrame.CreateExit(exitCode), ct)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client left after the request may have been accepted; a missing Exit is its uncertainty
                // signal, and the command is never replayed.
            }
        }
    }
    private async Task ServeStatusAsync(PipeStream pipe, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await EngineHostFrame.WriteAsync(pipe,
            new EngineHostFrame(EngineHostFrameKind.Accepted, ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
        var disconnected = EngineHostFrame.ReadAsync(pipe, lifetime.Token);
        var sending = SendAsync();
        try
        {
            await Task.WhenAny(disconnected, sending).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(disconnected, sending).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) { }
        }

        async Task SendAsync()
        {
            await foreach (var status in _subscribe!(lifetime.Token).WithCancellation(lifetime.Token).ConfigureAwait(false))
                await EngineHostFrame.WriteAsync(pipe, EngineHostFrame.CreateStatus(status), lifetime.Token).ConfigureAwait(false);
        }
    }
}
