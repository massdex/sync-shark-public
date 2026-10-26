using System.IO.Pipes;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Shared CLI/GUI client. It never opens a store or constructs an engine. No transport failure replays a command.
/// An unavailable observation returns null; a failure after connection is explicitly uncertain.
/// </summary>
public sealed class AutoSyncHostClient
{
    private readonly string _pipeName;
    private readonly Func<PipeStream, bool> _trustedPeer;

    public AutoSyncHostClient(string pipeName) : this(pipeName,
        pipe => AutoSyncHostProtocol.TrustedPeer(pipe, server: false)) { }

    internal AutoSyncHostClient(string pipeName, Func<PipeStream, bool> trustedPeer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(trustedPeer);
        _pipeName = pipeName;
        _trustedPeer = trustedPeer;
    }

    /// <summary>
    /// Null means the connection deadline expired before any bytes were sent. Cancellation after sending only
    /// stops this client's wait; the authority retains the admitted work. Read requests never launch a process.
    /// </summary>
    public async Task<AutoSyncHostReply?> TrySendAsync(AutoSyncHostRequest request,
        TimeSpan connectionTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (connectionTimeout <= TimeSpan.Zero || connectionTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(connectionTimeout);
            try { await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
        if (!_trustedPeer(pipe)) throw new UnauthorizedAccessException("The AutoSync authority is not the same trusted product executable, user and session.");
        try
        {
            await AutoSyncHostProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            var reply = await AutoSyncHostProtocol.ReadAsync<AutoSyncHostReply>(pipe, cancellationToken).ConfigureAwait(false);
            if (reply.Version != AutoSyncHostProtocol.Version || reply.RequestId != request.RequestId)
                throw new InvalidDataException("The AutoSync reply does not match this request and protocol.");
            return reply;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            throw new AutoSyncHostUncertainException(request.RequestId, error);
        }
    }

    /// <summary>Ask the host to stop first; only an absent host permits cancelling relaunch backoff here.</summary>
    public async Task<AutoSyncHostReply?> StopAsync(TimeSpan connectionTimeout, Action stopSupervisorIfAbsent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stopSupervisorIfAbsent);
        var reply = await TrySendAsync(Request("stop"), connectionTimeout, cancellationToken).ConfigureAwait(false);
        // Failed, rejected and uncertain sends never cancel supervision from the client.
        if (reply is null) stopSupervisorIfAbsent();
        return reply;
    }

    public static AutoSyncHostRequest Request(string command, string? pairId = null, bool all = false,
        int? minutes = null, int limit = 50, long after = 0) =>
        new(AutoSyncHostProtocol.Version, Guid.NewGuid().ToString("N"), command, pairId, all, minutes, limit, after);
}
