using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Headless single-owner IPC host. The supplied engine and authority lease are created once, never per client.
/// A disconnect abandons the reply, not admitted work; clients must inspect status instead of replaying a mutation.
/// </summary>
public sealed class AutoSyncBackgroundHost : IAsyncDisposable
{
    private const int MaximumConnections = 32;
    private const int MaximumOperations = 8;
    private readonly string _pipeName;
    private readonly IDisposable _authority;
    private readonly IAutoSyncHostEngine _engine;
    private readonly Func<PipeStream, bool> _trustedPeer;
    private readonly CancellationTokenSource _acceptStop = new();
    private readonly CancellationTokenSource _workStop = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _quiescent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _shutdown;
    private int _active;
    private bool _engineClosed;
    private long _connectionId;
    private readonly Task _listener;

    private AutoSyncBackgroundHost(string pipeName, NamedPipeServerStream first, IDisposable authority,
        IAutoSyncHostEngine engine, Func<PipeStream, bool> trustedPeer)
    {
        _pipeName = pipeName;
        _authority = authority;
        _engine = engine;
        _trustedPeer = trustedPeer;
        Identity = new AutoSyncHostIdentity(Guid.NewGuid().ToString("N"), Environment.ProcessId, DateTimeOffset.UtcNow);
        _listener = Task.Run(() => ListenAsync(first));
    }

    public AutoSyncHostIdentity Identity { get; }
    public Task Completion => _listener;

    /// <summary>
    /// Reserves the address, then takes the existing product authority, then constructs the engine.
    /// A second process cannot run either factory while this address is owned. Contention is not permission to
    /// bypass the lock: attach with AutoSyncHostClient, or report the existing GUI/maintenance owner.
    /// </summary>
    public static AutoSyncBackgroundHost Start(string pipeName, Func<IDisposable> acquireAuthority,
        Func<IAutoSyncHostEngine> createEngine) => Start(pipeName, acquireAuthority, createEngine,
            pipe => AutoSyncHostProtocol.TrustedPeer(pipe, server: true));

    internal static AutoSyncBackgroundHost Start(string pipeName, Func<IDisposable> acquireAuthority,
        Func<IAutoSyncHostEngine> createEngine, Func<PipeStream, bool> trustedPeer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(acquireAuthority);
        ArgumentNullException.ThrowIfNull(createEngine);
        ArgumentNullException.ThrowIfNull(trustedPeer);
        var first = NewListener(pipeName, first: true);
        IDisposable? authority = null;
        try
        {
            authority = acquireAuthority();
            var engine = createEngine();
            return new AutoSyncBackgroundHost(pipeName, first, authority, engine, trustedPeer);
        }
        catch
        {
            authority?.Dispose();
            first.Dispose();
            throw;
        }
    }

    private static NamedPipeServerStream NewListener(string name, bool first = false) => new(name,
        PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        4096, 4096);

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? waiting = first;
        try
        {
            while (!_acceptStop.IsCancellationRequested)
            {
                await waiting.WaitForConnectionAsync(_acceptStop.Token).ConfigureAwait(false);
                var connected = waiting;
                // Retain an instance while replacing the listener; there is no free-name handover window.
                waiting = NewListener(_pipeName);
                foreach (var entry in _connections.Where(entry => entry.Value.IsCompleted))
                    _connections.TryRemove(entry.Key, out _);
                if (_connections.Count >= MaximumConnections)
                {
                    // Bounded load shedding; no unbounded task or command queue. Close rather than interpret an
                    // unauthenticated/unread request. The client reports an uncertain transport, never retries it.
                    connected.Dispose();
                    continue;
                }
                var id = Interlocked.Increment(ref _connectionId);
                _connections[id] = ServeAsync(connected);
            }
        }
        catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested) { }
        finally
        {
            waiting?.Dispose();
            try
            {
                await BeginStop().ConfigureAwait(false);
                await Task.WhenAll(_connections.Values).ConfigureAwait(false);
            }
            finally
            {
                // Failed disposal is not proof that background work released the store. Keep the lease in
                // that case; the launcher must report failed shutdown, never start a competing engine.
                if (_engineClosed) _authority.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            var stopAfterReply = false;
            AutoSyncHostRequest? request = null;
            try
            {
                if (!_trustedPeer(pipe)) return;
                using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                request = await AutoSyncHostProtocol.ReadAsync<AutoSyncHostRequest>(pipe, readDeadline.Token).ConfigureAwait(false);
                request.Validate();
                AutoSyncHostReply reply;
                if (request.Command == "stop")
                {
                    stopAfterReply = true;
                    await BeginStop().ConfigureAwait(false);
                    reply = Reply(request, "stopped", new { safePointReached = true });
                }
                else
                {
                    var admission = Admit();
                    if (admission is not null)
                        reply = admission == "stopping" && request.Command == "status"
                            ? Reply(request, "stopping", new { running = true, acceptingWork = false })
                            : Failure(request, admission, "The host is " + admission +
                                ". Wait for the current safe point and inspect status; no command was queued or replayed.");
                    else
                    {
                        try
                        {
                            var result = await _engine.ExecuteAsync(request, _workStop.Token).ConfigureAwait(false);
                            reply = Reply(request, "running", result);
                        }
                        finally { Release(); }
                    }
                }
                using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AutoSyncHostProtocol.WriteAsync(pipe, reply, writeDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A bad request, failed pair or abandoned reply cannot kill the listener or another command.
                if (request is not null)
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        var code = error is OperationCanceledException ? "canceled" :
                            error is InvalidDataException ? "invalid_request" : "operation_failed";
                        await AutoSyncHostProtocol.WriteAsync(pipe, Failure(request, code,
                            SyncCli.DescribeException(error)), deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { /* Client left, or the stream already contains an incomplete reply. */ }
                }
            }
            finally
            {
                // A failed stop reply must not leave an already drained process owning the store indefinitely.
                if (stopAfterReply) _acceptStop.Cancel();
            }
        }
    }

    private string? Admit()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return "stopping";
            if (_active >= MaximumOperations) return "busy";
            _active++;
            return null;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            if (_shutdown is not null && _active == 0) _quiescent.TrySetResult();
        }
    }

    private Task BeginStop()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return _shutdown;
            if (_active == 0) _quiescent.TrySetResult();
            // Run outside the admission lock: cancellation callbacks can finish commands synchronously.
            _shutdown = Task.Run(async () =>
            {
                try
                {
                    try { _workStop.Cancel(); }
                    finally { await _engine.StopAsync().ConfigureAwait(false); }
                }
                finally
                {
                    // Even a cancellation callback or engine stop failure cannot release its store while
                    // admitted commands use it. Disposal still runs after the last command leaves.
                    await _quiescent.Task.ConfigureAwait(false);
                    await _engine.DisposeAsync().ConfigureAwait(false);
                    _engineClosed = true;
                }
            });
            return _shutdown;
        }
    }

    private AutoSyncHostReply Reply(AutoSyncHostRequest request, string state, object data) =>
        new(AutoSyncHostProtocol.Version, request.RequestId, Identity, state, true,
            JsonSerializer.SerializeToElement(data, AutoSyncHostProtocol.Json), null);

    private AutoSyncHostReply Failure(AutoSyncHostRequest request, string code, string message) =>
        new(AutoSyncHostProtocol.Version, request.RequestId, Identity, "unknown", false, null,
            new SyncCliError(code, message, code == "busy", code == "busy" ? 1 : null));

    public async ValueTask DisposeAsync()
    {
        try { await BeginStop().ConfigureAwait(false); }
        finally
        {
            _acceptStop.Cancel();
            // Join even when stop failed: the listener's finally owns the authority lease.
            await _listener.ConfigureAwait(false);
        }
    }
}
