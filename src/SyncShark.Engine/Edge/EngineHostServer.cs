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

    /// <summary>Accepts bounded connections until stopped or faulted, then ends unread intake, starts the owner's
    /// safe shutdown and joins admitted exchanges. A probe that closes before accept recycles only its pipe;
    /// unexpected connection faults end the listener and are observed during its drain.</summary>
    public async Task ListenAsync(NamedPipeServerStream first, Func<NamedPipeServerStream> createListener,
        int maximumConnections, CancellationToken acceptStop, Func<ValueTask> shutdown)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(createListener);
        ArgumentNullException.ThrowIfNull(shutdown);
        using var intake = CancellationTokenSource.CreateLinkedTokenSource(acceptStop);
        var connections = new Dictionary<long, Task>();
        var waiting = first;
        long id = 0;
        try
        {
            while (!intake.IsCancellationRequested)
            {
                var accept = await LocalPipePeer.AcceptAsync(waiting, createListener, intake.Token)
                    .ConfigureAwait(false);
                waiting = accept.Waiting;
                if (!accept.Accepted) continue;
                var connected = waiting;
                // Retain an instance while replacing the listener; there is no free-name handover window.
                waiting = createListener();
                foreach (var finished in connections.Where(pair => pair.Value.IsCompletedSuccessfully).Select(pair => pair.Key)
                    .ToArray())
                    connections.Remove(finished);
                if (connections.Count >= maximumConnections)
                {
                    // Bounded load shedding; an unread request is closed rather than queued.
                    connected.Dispose();
                    continue;
                }
                connections[++id] = ServeConnectionAsync(connected);
            }
        }
        catch (OperationCanceledException) when (intake.IsCancellationRequested) { }
        finally
        {
            waiting.Dispose();
            // Shutdown completes status sources and drains host work before authority can be released.
            try
            {
                await intake.CancelAsync().ConfigureAwait(false);
                await shutdown().ConfigureAwait(false);
            }
            finally { await Task.WhenAll(connections.Values).ConfigureAwait(false); }
        }

        async Task ServeConnectionAsync(PipeStream connected)
        {
            try { await ServeAsync(connected, intake.Token).ConfigureAwait(false); }
            catch
            {
                throw;
            }
        }
    }

    // One connection exchange: frame read, accepted ack, output streaming and exit are one ordered sequence on one pipe.
    /// <summary>Serves one trusted connection. Intake bounds only the unread request; an accepted command,
    /// its output, its Exit and a status subscription are never cancelled by it.</summary>
    public async Task ServeAsync(PipeStream pipe, CancellationToken intake)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using (pipe)
        {
            try
            {
                if (!LocalPipePeer.Trusted(pipe, server: true)) return;
                EngineHostFrame? frame;
                string[]? arguments = null;
                try
                {
                    // A clean end of stream before any header is an observation attach, not a command.
                    frame = await EngineHostFrame.ReadAsync(pipe, intake).ConfigureAwait(false);
                    if (frame is { Kind: EngineHostFrameKind.Arguments }) arguments = frame.Value.DecodeArguments();
                }
                catch (OperationCanceledException) when (intake.IsCancellationRequested) { return; }
                catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
                if (frame is { Kind: EngineHostFrameKind.SubscribeStatus } && _subscribe is not null)
                {
                    await ServeStatusAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                if (arguments is null) return;
                await EngineHostFrame.WriteAsync(pipe,
                    new EngineHostFrame(EngineHostFrameKind.Accepted, ReadOnlyMemory<byte>.Empty), CancellationToken.None)
                    .ConfigureAwait(false);
                using var stdout = new EnginePipeWriter(pipe, EngineHostFrameKind.StandardOutput);
                using var stderr = new EnginePipeWriter(pipe, EngineHostFrameKind.StandardError);
                var exitCode = await _execute(arguments, stdout, stderr, CancellationToken.None).ConfigureAwait(false);
                await EngineHostFrame.WriteAsync(pipe, EngineHostFrame.CreateExit(exitCode), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (IOException error) when (LocalPipePeer.Disconnected(error))
            {
                // A missing Exit is the client's uncertainty signal; the command is never replayed.
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
            await Task.WhenAll(JoinAsync(disconnected), JoinAsync(sending)).ConfigureAwait(false);
        }

        async Task JoinAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException error) when (LocalPipePeer.Disconnected(error)) { }
        }

        async Task SendAsync()
        {
            await foreach (var status in _subscribe!(lifetime.Token).WithCancellation(lifetime.Token).ConfigureAwait(false))
                await EngineHostFrame.WriteAsync(pipe, EngineHostFrame.CreateStatus(status), lifetime.Token).ConfigureAwait(false);
        }
    }
}
