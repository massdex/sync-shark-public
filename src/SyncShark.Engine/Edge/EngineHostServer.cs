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
/// its own. A request still unread when the host stops accepting is closed without a frame, like a malformed one.</summary>
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

    /// <summary>Accepts bounded connections until the stop token fires. A probe that connects and closes before an
    /// accept begins disconnects only that connection: the listener recycles its pipe and keeps serving.</summary>
    public async Task ListenAsync(NamedPipeServerStream first, Func<NamedPipeServerStream> createListener,
        int maximumConnections, CancellationToken acceptStop)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(createListener);
        var connections = new Dictionary<long, Task>();
        var waiting = first;
        long id = 0;
        try
        {
            while (!acceptStop.IsCancellationRequested)
            {
                var accept = await LocalPipePeer.AcceptAsync(waiting, createListener, acceptStop)
                    .ConfigureAwait(false);
                waiting = accept.Waiting;
                if (!accept.Accepted) continue;
                var connected = waiting;
                // Retain an instance while replacing the listener; there is no free-name handover window.
                waiting = createListener();
                foreach (var finished in connections.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key)
                    .ToArray())
                    connections.Remove(finished);
                if (connections.Count >= maximumConnections)
                {
                    // Bounded load shedding; an unread request is closed rather than queued.
                    connected.Dispose();
                    continue;
                }
                connections[++id] = ServeAsync(connected, acceptStop);
            }
        }
        catch (OperationCanceledException) when (acceptStop.IsCancellationRequested) { }
        finally
        {
            waiting.Dispose();
            await Task.WhenAll(connections.Values).ConfigureAwait(false);
        }
    }

    /// <summary>Serves one trusted connection. Intake bounds only the unread request; an accepted command,
    /// its output, its Exit and a status subscription are never cancelled by it.</summary>
    public async Task ServeAsync(PipeStream pipe, CancellationToken intake)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using (pipe)
        {
            if (!LocalPipePeer.Trusted(pipe, server: true)) return;
            string[] arguments;
            try
            {
                // A clean end of stream before any header is an observation attach, not a command.
                var frame = await EngineHostFrame.ReadAsync(pipe, intake).ConfigureAwait(false);
                if (frame is { Kind: EngineHostFrameKind.SubscribeStatus } && _subscribe is not null)
                {
                    await ServeStatusAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                if (frame is null || frame.Value.Kind != EngineHostFrameKind.Arguments) return;
                arguments = frame.Value.DecodeArguments();
            }
            catch (OperationCanceledException) when (intake.IsCancellationRequested) { return; }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                return;
            }
            try
            {
                await EngineHostFrame.WriteAsync(pipe,
                    new EngineHostFrame(EngineHostFrameKind.Accepted, ReadOnlyMemory<byte>.Empty), CancellationToken.None)
                    .ConfigureAwait(false);
                using var stdout = new EnginePipeWriter(pipe, EngineHostFrameKind.StandardOutput);
                using var stderr = new EnginePipeWriter(pipe, EngineHostFrameKind.StandardError);
                var exitCode = await _execute(arguments, stdout, stderr, CancellationToken.None).ConfigureAwait(false);
                await EngineHostFrame.WriteAsync(pipe, EngineHostFrame.CreateExit(exitCode), CancellationToken.None)
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
