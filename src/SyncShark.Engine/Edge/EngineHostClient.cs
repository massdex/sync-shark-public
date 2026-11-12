using System.IO.Pipes;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Attaches to the running engine host over its one-command pipe (design 8.8, V3 C16): one Arguments
/// request, one Accepted acknowledgment, streamed standard output and error, then one Exit. It also hands the
/// authenticated host process to the supervisor for lifetime observation. An absent host reports no attached peer
/// before anything is sent; after sending, a lost transport is a failure and is never replayed or performed locally.</summary>
public sealed class EngineHostClient
{
    // The pipe options need the local server name; the engine text rule allows no string literal here, so the
    // dot that names the local computer is built from a character.
    private static readonly string LocalServerName = '.'.ToString();

    private readonly string _pipeName;
    private readonly TimeSpan _connectionTimeout;

    /// <summary>Creates a client for one host pipe. The caller owns the connection deadline; the client adds no
    /// wait of its own.</summary>
    public EngineHostClient(string pipeName, TimeSpan connectionTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (connectionTimeout <= TimeSpan.Zero ||
            connectionTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        _pipeName = pipeName;
        _connectionTimeout = connectionTimeout;
    }

    /// <summary>Executes one command and returns the host's exit code, or null when no host answered before the
    /// connection deadline. Once the request may have reached the host, a lost transport returns the ordinary
    /// failure exit code; the command is never resent and never falls back to direct mutation.</summary>
    public async Task<int?> TryExecuteAsync(string[] arguments, TextWriter stdout, TextWriter stderr,
        CancellationToken ct) => (await TryExecuteResultAsync(arguments, stdout, stderr, ct).ConfigureAwait(false)).ExitCode;

    /// <summary>Distinguishes a received terminal Exit from loss after a request may have been sent.</summary>
    public async Task<(int? ExitCode, bool ReceivedExit)> TryExecuteResultAsync(string[] arguments,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0) throw new ArgumentException(null, nameof(arguments));
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var repair = IsRepair(arguments);
        var started = TimeProvider.System.GetTimestamp();
        await using var monitor = repair ? new Run.Monitor(TimeProvider.System) : null;
        if (monitor is not null)
        {
            await stderr.WriteLineAsync(Run.RunLog.StageDetail(TextKey.StageHostWait)).ConfigureAwait(false);
            monitor.Observe(() =>
            {
                stderr.WriteLine(Text.Get(TextKey.StatusStillWaiting,
                    (TextArg.Reason, Text.Get(TextKey.StageHostWait))));
                return true;
            });
            monitor.Start(quietOnly: true);
        }
        using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        if (pipe is null)
        {
            if (repair) await stderr.WriteLineAsync(Run.RunLog.StageDetail(TextKey.StageHostWait,
                ended: true, elapsed: TimeProvider.System.GetElapsedTime(started))).ConfigureAwait(false);
            return (null, false);
        }
        if (!LocalPipePeer.Trusted(pipe, server: false)) throw new UnauthorizedAccessException();
        try
        {
            await EngineHostFrame.WriteAsync(pipe, EngineHostFrame.CreateArguments(arguments), ct)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ((int)SyncCliExitCode.Failed, false);
        }
        var accepted = await ReadFrameAsync(pipe, ct).ConfigureAwait(false);
        if (accepted is null) return ((int)SyncCliExitCode.Failed, false);
        if (accepted.Value.Kind != EngineHostFrameKind.Accepted)
            throw new InvalidDataException(nameof(arguments));
        while (true)
        {
            var frame = await ReadFrameAsync(pipe, ct).ConfigureAwait(false);
            if (frame is null) return ((int)SyncCliExitCode.Failed, false);
            if (repair)
            {
                await monitor!.StopAsync().ConfigureAwait(false);
                await stderr.WriteLineAsync(Run.RunLog.StageDetail(TextKey.StageHostWait,
                    ended: true, count: Run.RunLog.CompletedOperation,
                    elapsed: TimeProvider.System.GetElapsedTime(started))).ConfigureAwait(false);
                repair = false;
            }
            switch (frame.Value.Kind)
            {
                case EngineHostFrameKind.StandardOutput:
                    await stdout.WriteAsync(frame.Value.DecodeText()).ConfigureAwait(false);
                    break;
                case EngineHostFrameKind.StandardError:
                    await stderr.WriteAsync(frame.Value.DecodeText()).ConfigureAwait(false);
                    break;
                case EngineHostFrameKind.Exit:
                    return (frame.Value.DecodeExitCode(), true);
                default:
                    throw new InvalidDataException(nameof(arguments));
            }
        }
    }

    private static bool IsRepair(string[] arguments)
    {
        try
        {
            var invocation = SyncCliRegistry.Parse(arguments);
            return invocation.Kind == SyncCliRequestKind.State && EngineCliCatalog.Owns(invocation.Command) &&
                EngineCliCatalog.CommandKey(invocation.Command) == EngineCommandKey.Repair;
        }
        catch (SyncCliUsageException) { return false; }
    }

    /// <summary>Attaches to a running host and returns its pinned process handle for the supervisor's lifetime
    /// observation, or null when no host answered before the connection deadline. The caller owns and disposes the
    /// returned handle. The shared observation owner rechecks the trusted peer and the process before returning it,
    /// and disposes its own handle on every refused, unverified or not-ready path.</summary>
    public async Task<AutoSyncProcess.ChildProcess?> TryObserveAsync(CancellationToken ct)
    {
        using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        return pipe is null ? null : AutoSyncProcess.ObserveTrustedPeer(pipe);
    }

    /// <summary>Streams latest host observations until disconnect. Cancellation closes this subscription;
    /// absence or transport loss throws IOException. It never starts a host or reconnects implicitly.</summary>
    public async IAsyncEnumerable<HostStatusUpdate> SubscribeStatusAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var pipe = await ConnectAsync(ct).ConfigureAwait(false)
            ?? throw new IOException(nameof(SubscribeStatusAsync));
        if (!LocalPipePeer.Trusted(pipe, server: false)) throw new UnauthorizedAccessException();
        await EngineHostFrame.WriteAsync(pipe,
            new EngineHostFrame(EngineHostFrameKind.SubscribeStatus, ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
        var accepted = await EngineHostFrame.ReadAsync(pipe, ct).ConfigureAwait(false);
        if (accepted is not { Kind: EngineHostFrameKind.Accepted })
            throw new InvalidDataException(nameof(SubscribeStatusAsync));
        while (true)
        {
            var frame = await EngineHostFrame.ReadAsync(pipe, ct).ConfigureAwait(false)
                ?? throw new IOException(nameof(SubscribeStatusAsync));
            yield return frame.DecodeStatus();
        }
    }

    /// <summary>Builds the one CLI argument vector for a catalog command from caller-known option values.</summary>
    internal static string[] CommandRequest(EngineCommandKey key,
        IReadOnlyList<(EngineOptionKey Key, string Value)> options, bool json = true)
    {
        var arguments = new List<string> { SyncCli.CliSwitch };
        arguments.AddRange(CommandWord(key).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var option in options)
        {
            var flag = EngineCliCatalog.Flag(option.Key);
            arguments.Add(flag);
            if (EngineCliCatalog.TakesValue(flag)) arguments.Add(option.Value);
        }
        if (json) arguments.Add(SyncCli.JsonSwitch);
        return arguments.ToArray();
    }

    /// <summary>Decodes one host reply with the writer's own format; a malformed or mismatched envelope is refused.</summary>
    internal static T ReadResult<T>(string text, EngineCommandKey key)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var envelope = document.RootElement.Deserialize<SyncCliEnvelope>(SyncCli.WireJson) ??
                throw new InvalidDataException(nameof(text));
            if (!envelope.Ok || envelope.Data is not JsonElement payload ||
                !string.Equals(envelope.Command, CommandWord(key), StringComparison.Ordinal))
                throw new InvalidDataException(nameof(text));
            return payload.Deserialize<T>(SyncCli.WireJson) ?? throw new InvalidDataException(nameof(text));
        }
        catch (JsonException)
        {
            throw new InvalidDataException(nameof(text));
        }
    }

    /// <summary>The one catalog command word for a key.</summary>
    internal static string CommandWord(EngineCommandKey key) =>
        EngineCliCatalog.Rows.Single(row => EngineCliCatalog.CommandKey(row.Command) == key).Command;

    private async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(LocalServerName, _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_connectionTimeout);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the caller's own token can make absence ambiguous; the deadline means no host answered.
            pipe.Dispose();
            return null;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task<EngineHostFrame?> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            return await EngineHostFrame.ReadAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The request may have reached the host; report the failure result rather than retry.
            return null;
        }
    }
}
