using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>The Engine process edge (design 8.8, RC05-B/D/S/X): starts or attaches to the one trusted engine host,
/// runs the host process itself, and routes engine commands and the two typed preference operations through the
/// shared parser/transport/renderer. Once bytes may have been sent, absence, an untrusted peer, a failed reply or a
/// missing Exit is never permission for local authority, direct Repair, a replay or a restart; only
/// <see cref="RunAsync"/> acquires the product lock, composes the engine resources and binds the one platform wake
/// source.</summary>
internal static class EngineHostEntry
{
    // Private process-start results (RC05-V5-X); never public CLI exit codes or IPC frame kinds.
    internal const int StartupReleaseRecoveryRequired = 20;
    internal const int StartupInvalidDataLocation = 21;
    internal const int StartupLegacyFootprintRefused = 22;

    internal static int CheckStartupAdmission(string? userSid = null)
    {
        var productRoot = SyncSharkRuntimePaths.ProductRoot;
        try
        {
            _ = EngineStartupAdmission.Require(productRoot, userSid);
            return (int)SyncCliExitCode.Success;
        }
        catch (EngineStartupRefusedException refusal)
        {
            AutoSyncLog.StartupRefused(productRoot, refusal);
            return StartupLegacyFootprintRefused;
        }
    }

    // Private readiness frame: one marker byte, and a four-byte signed result for the first child's outcome.
    private const byte ReadyFrame = 0;
    private const byte ResultFrame = 1;
    private const int SharingViolationWin32 = 32;
    private const int LockViolationWin32 = 33;
    private const string EnginePipeSuffix = ".engine";
    // The existing host status probe's caller-owned connection deadline; a client connect completes as soon as the
    // listener instance exists, so this only bounds an absent or foreign address.
    private const int HostConnectionDeadlineMilliseconds = 300;
    // The existing background host's bounded connection count; load shedding closes the pipe rather than queueing.
    private const int MaximumConnections = 32;

    private static readonly TimeSpan HostConnectionDeadline =
        TimeSpan.FromMilliseconds(HostConnectionDeadlineMilliseconds);

    private static string EngineHostPipeName =>
        AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + EnginePipeSuffix;

    /// <summary>Starts or attaches to the trusted engine host and returns 0 once it is ready, otherwise the exact
    /// private child result. An already-ready host is attached and never stopped; a failed readiness is reported as
    /// a non-zero failure, and zero without readiness is itself a failure.</summary>
    internal static async Task<int> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var executable = Executable();
        if (await ObserveReadyHostAsync(ct).ConfigureAwait(false) is { } existing)
        {
            existing.Dispose();
            return (int)SyncCliExitCode.Success;
        }
        ct.ThrowIfCancellationRequested();
        using var readiness = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var child = AutoSyncProcess.StartEngineDetached(executable, supervisor: true,
            warning: Console.Error.WriteLine, readiness: readiness.ClientSafePipeHandle);
        readiness.DisposeLocalCopyOfClientHandle();
        return await ReadReadinessAsync(readiness, child, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the Engine host process: claims the launcher's readiness pipe once, takes the one product
    /// authority, admits its metadata footprint, then composes one host, one wake source and the
    /// one-command pipe until an admitted stop drains them. Only this method owns those resources.</summary>
    internal static async Task<int> RunAsync(CancellationToken ct, Action? stopSupervisor = null)
    {
        using var readiness = AutoSyncProcess.TakeReadinessPipe();
        string productRoot;
        try
        {
            productRoot = SyncSharkRuntimePaths.ProductRoot;
        }
        catch (InvalidDataException)
        {
            return StartupInvalidDataLocation;
        }
        PilotInstanceLock? authority;
        try
        {
            authority = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        }
        catch (PilotReleaseRecoveryRequiredException)
        {
            return StartupReleaseRecoveryRequired;
        }
        catch (IOException error) when (error.InnerException is Win32Exception
            { NativeErrorCode: SharingViolationWin32 or LockViolationWin32 })
        {
            return (int)SyncCliExitCode.Busy;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (int)SyncCliExitCode.Canceled;
        }
        catch (Exception error)
        {
            AutoSyncLog.Failure(productRoot, error);
            return (int)SyncCliExitCode.Failed;
        }
        try
        {
            var admission = EngineStartupAdmission.Require(productRoot);
            // One clear attempt per acquired host lifetime; a failed write leaves the host healthy and logged.
            try { AutoSyncProcess.ClearExistingOwnerNotice(AutoSyncProcess.ExistingOwnerNoticePath); }
            catch (Exception noticeError) { AutoSyncLog.Failure(productRoot, noticeError); }
            return await RunOwnedAsync(productRoot, authority.StateAuthority, admission, readiness, ct).ConfigureAwait(false);
        }
        catch (EngineStartupRefusedException refusal)
        {
            AutoSyncLog.StartupRefused(productRoot, refusal);
            stopSupervisor ??= () => AutoSyncProcess.StopSupervisor(engine: true);
            try { stopSupervisor(); }
            catch (Exception cleanupError) { AutoSyncLog.Failure(productRoot, cleanupError); }
            return StartupLegacyFootprintRefused;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (int)SyncCliExitCode.Canceled;
        }
        catch (Exception error)
        {
            AutoSyncLog.Failure(productRoot, error);
            return (int)SyncCliExitCode.Failed;
        }
        finally
        {
            authority.Dispose();
        }
    }

    /// <summary>The four-argument entry retains the existing standalone behavior for pre-existing commands; the five-
    /// argument overload decides whether a trusted running host is required. An invalid invocation is never
    /// forwarded, and a request that may have been sent is never replayed or performed locally.</summary>
    internal static Task<int?> TryCliAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct) =>
        TryCliAsync(args, stdout, stderr, ct, requireRunningHost: false);

    /// <summary>Routes one engine invocation or native Dropbox verb to the verified running host. Null means this
    /// entry did not handle the request (not an engine or Dropbox command, or a state-free/refused invocation the
    /// caller renders); a host-only command or a required host that did not answer returns its truthful unavailable
    /// result. A Dropbox verb that no host answered before the request was sent returns null, which permits only the
    /// caller's explicit standalone EngineCliRuntime path and never a replay.</summary>
    internal static async Task<int?> TryCliAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        bool requireRunningHost)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        SyncCliInvocation invocation;
        try { invocation = SyncCliRegistry.Parse(args); }
        catch (SyncCliException) { return null; }
        if (invocation.Kind != SyncCliRequestKind.State) return null;
        // 1A: the legacy AutoSync start/stop commands run on the current Engine host surface.
        if (invocation.Command is "autosync start" or "autosync stop")
        {
            var start = invocation.Command == "autosync start";
            string[] currentArgs = invocation.Json
                ? ["engine", "host", start ? "start" : "stop", "--yes", "--json"]
                : ["engine", "host", start ? "start" : "stop", "--yes"];
            var current = await ExecuteOnHostResultAsync(currentArgs, stdout, stderr, ct).ConfigureAwait(false);
            if (current.ExitCode is { } currentExit) return currentExit;
            if (start) return await StartHostCommandAsync(currentArgs, stdout, stderr, ct).ConfigureAwait(false);
            // Only an authoritatively absent Engine permits ending its owned relaunch backoff.
            AutoSyncProcess.StopSupervisor(engine: true);
            return (int)SyncCliExitCode.Success;
        }
        // #115: the native Dropbox group uses the same verified-host surface.
        var dropbox = DropboxCliCatalog.Owns(invocation.Command);
        if (!dropbox && !EngineCliCatalog.Owns(invocation.Command)) return null;
        var key = dropbox ? (EngineCommandKey?)null : EngineCliCatalog.CommandKey(invocation.Command);
        // The read-only log view is one local file read: it must not pass through the bounded host frame, and the
        // client's own terminal supplies its width (R1 review 2/8).
        if (key is EngineCommandKey.Logs) return null;
        var result = await ExecuteOnHostResultAsync(args, stdout, stderr, ct).ConfigureAwait(false);
        if (requireRunningHost && result.ExitCode is not null && !result.ReceivedExit)
            throw new AutoSyncHostUncertainException(invocation.Command,
                new IOException(EngineCliCatalog.Message(EngineMessageKey.NeedsEngine)));
        var exit = result.ExitCode;
        if (exit is not null) return exit;
        if (dropbox) return null;
        if (requireRunningHost || IsHostOnly(key!.Value)) return Unavailable(stderr);
        if (key == EngineCommandKey.HostStart)
            return await StartHostCommandAsync(args, stdout, stderr, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Routes one host-owned read command to the verified running Engine host over its command pipe. Null
    /// means this entry handled nothing (not one of the four reads, or no host answered before anything was sent);
    /// the caller then keeps the existing direct path. Once the request may have been sent, a lost reply is reported
    /// as the failed exit and is never replayed here.</summary>
    internal static async Task<int?> TryReadAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        SyncCliInvocation invocation;
        try { invocation = SyncCliRegistry.Parse(args); }
        catch (SyncCliException) { return null; }
        if (invocation.Kind != SyncCliRequestKind.State || !EngineHostReadCatalog.Owns(invocation.Command)) return null;
        return (await ExecuteOnHostResultAsync(args, stdout, stderr, ct).ConfigureAwait(false)).ExitCode;
    }

    /// <summary>Reads the saved presentation preferences through the shared registered command and the verified
    /// host; a null host, unsupported output, a malformed or partial envelope or a missing Exit is refused, never
    /// replaced with defaults.</summary>
    internal static async Task<UiPreferences> ReadUiPreferencesAsync(CancellationToken ct) =>
        (await RunPreferenceAsync(EngineHostClient.CommandRequest(EngineCommandKey.PreferencesShow, []),
            EngineCommandKey.PreferencesShow, ct).ConfigureAwait(false)).Preferences;

    /// <summary>Saves the exact changed-field patch through the shared registered command and the verified host and
    /// returns the committed merged preferences. A failure after the request may have been sent is reported as
    /// uncertain rather than as an unchanged store.</summary>
    internal static async Task<UiPreferences> UpdateUiPreferencesAsync(UiPreferencesUpdate update,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        return (await RunPreferenceAsync(EngineHostClient.CommandRequest(EngineCommandKey.PreferencesSet,
                [(EngineOptionKey.Settings, update.ToJson()), (EngineOptionKey.Yes, string.Empty)]),
            EngineCommandKey.PreferencesSet, ct).ConfigureAwait(false)).Preferences;
    }

    /// <summary>Observes a trusted ready host and returns its pinned process handle for the supervisor's lifetime
    /// observation, or null when none answered before the caller-owned deadline. The caller owns the handle.</summary>
    internal static async Task<AutoSyncProcess.ChildProcess?> ObserveReadyHostAsync(CancellationToken ct)
    {
        var client = new EngineHostClient(EngineHostPipeName, HostConnectionDeadline);
        try { return await client.TryObserveAsync(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // No trusted ready host answered; starting a host is the caller's decision, never a retry here.
            return null;
        }
    }

    /// <summary>The supervisor child factory is synchronous, so its one bounded observation blocks on the same
    /// deadline rather than polling.</summary>
    internal static AutoSyncProcess.ChildProcess? ObserveReadyHost() =>
        ObserveReadyHostAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Writes the ready marker so a waiting launcher observes verified readiness.</summary>
    internal static void WriteReady(Stream? readiness)
    {
        if (readiness is null) return;
        Span<byte> frame = stackalloc byte[sizeof(byte)];
        frame[0] = ReadyFrame;
        TryWrite(readiness, frame);
    }

    /// <summary>Relays the first supervised child's exit result through the readiness pipe, so a startup failure
    /// reaches the launcher while the supervisor continues its own relaunch policy.</summary>
    internal static void WriteStartupResult(Stream? readiness, int exitCode)
    {
        if (readiness is null) return;
        Span<byte> frame = stackalloc byte[sizeof(byte) + sizeof(int)];
        frame[0] = ResultFrame;
        BinaryPrimitives.WriteInt32LittleEndian(frame[sizeof(byte)..], exitCode);
        TryWrite(readiness, frame);
    }

    private static void TryWrite(Stream readiness, ReadOnlySpan<byte> frame)
    {
        try
        {
            readiness.Write(frame);
            readiness.Flush();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The launcher already read the ready marker and closed its end; the host stays running.
        }
    }

    private static async Task<int> ReadReadinessAsync(Stream readiness, AutoSyncProcess.ChildProcess child,
        CancellationToken ct)
    {
        var marker = new byte[sizeof(byte)];
        int read;
        try { read = await readiness.ReadAsync(marker, ct).ConfigureAwait(false); }
        catch (IOException) { read = 0; }
        if (read == 0) return ChildResult(child);
        if (marker[0] == ReadyFrame) return (int)SyncCliExitCode.Success;
        var code = new byte[sizeof(int)];
        var got = 0;
        while (got < code.Length)
        {
            var part = await readiness.ReadAsync(code.AsMemory(got), ct).ConfigureAwait(false);
            if (part == 0) break;
            got += part;
        }
        return got == code.Length ? WithoutReady(BinaryPrimitives.ReadInt32LittleEndian(code)) :
            (int)SyncCliExitCode.Failed;
    }

    // A clean end of stream with no ready marker is a result only when the launcher's own child exited.
    private static int ChildResult(AutoSyncProcess.ChildProcess child) =>
        child.HasExited ? WithoutReady(child.ExitCode) : (int)SyncCliExitCode.Failed;

    // A child that exits successfully without verifying readiness did not become ready.
    private static int WithoutReady(int exitCode) =>
        exitCode == (int)SyncCliExitCode.Success ? (int)SyncCliExitCode.Failed : exitCode;

    private static async Task<int> RunOwnedAsync(string productRoot, PilotStateAuthority stateAuthority,
        EngineStartupAdmission admission, Stream? readiness, CancellationToken ct)
    {
        // The platform wake source registers its signals in its constructor, before any current-state inspection.
        using var wake = new SystemWakeSource();
        await using var host = EngineHost.Compose(productRoot, IgnoreProgress, stateAuthority,
            EngineHost.Mode.Persistent, admission);
        using var acceptStop = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
        host.BindStopSupervision(() =>
        {
            // The admitted safe stop ends supervision first, then this host drains and releases its own resources.
            AutoSyncProcess.StopSupervisor(engine: true);
            acceptStop.Cancel();
        });
        host.BindPlatformWake(wake);
        await host.StartAsync(ct).ConfigureAwait(false);
        var first = CreateListener(first: true);
        WriteReady(readiness);
        var server = new EngineHostServer((arguments, stdout, stderr, commandToken) =>
            SyncCli.ExecuteAsync(arguments, stdout, stderr,
                () => new EngineCliRuntime(host.Operations, host.Authority), commandToken),
            host.SubscribeStatusAsync);
        await server.ListenAsync(first, () => CreateListener(first: false), MaximumConnections, acceptStop.Token)
            .ConfigureAwait(false);
        // A caller cancellation ends the listener silently; it must not be reported as a clean stop.
        ct.ThrowIfCancellationRequested();
        return (int)SyncCliExitCode.Success;
    }

    private static NamedPipeServerStream CreateListener(bool first) => new(EngineHostPipeName,
        PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
        (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private static string Executable() =>
        Environment.ProcessPath ?? throw new IOException(nameof(Environment.ProcessPath));

    internal static IAsyncEnumerable<HostStatusUpdate> SubscribeStatusAsync(CancellationToken ct) =>
        new EngineHostClient(EngineHostPipeName, HostConnectionDeadline).SubscribeStatusAsync(ct);

    private static void IgnoreProgress(string message) { }

    private static async Task<int?> ExecuteOnHostAsync(string[] args, TextWriter stdout, TextWriter stderr,
        CancellationToken ct)
    {
        return (await ExecuteOnHostResultAsync(args, stdout, stderr, ct).ConfigureAwait(false)).ExitCode;
    }

    private static Task<(int? ExitCode, bool ReceivedExit)> ExecuteOnHostResultAsync(string[] args,
        TextWriter stdout, TextWriter stderr, CancellationToken ct) =>
        new EngineHostClient(EngineHostPipeName, HostConnectionDeadline).TryExecuteResultAsync(args, stdout, stderr, ct);

    private static async Task<int> StartHostCommandAsync(string[] args, TextWriter stdout, TextWriter stderr,
        CancellationToken ct)
    {
        var exit = await StartAsync(ct).ConfigureAwait(false);
        if (exit != (int)SyncCliExitCode.Success)
        {
            if (exit == StartupLegacyFootprintRefused)
                stderr.WriteLine(EngineStartupAdmission.RefusalMessage);
            return TranslateStartExit(exit);
        }
        // The approved start command reports the host it just made ready through the shared renderer.
        return await ExecuteOnHostAsync(args, stdout, stderr, ct).ConfigureAwait(false) ??
            (int)SyncCliExitCode.Failed;
    }

    private static int TranslateStartExit(int exit) => exit switch
    {
        StartupReleaseRecoveryRequired or StartupLegacyFootprintRefused => (int)SyncCliExitCode.NeedsReview,
        StartupInvalidDataLocation => (int)SyncCliExitCode.Failed,
        _ => exit,
    };

    private static int Unavailable(TextWriter stderr)
    {
        stderr.WriteLine(EngineCliCatalog.Message(EngineMessageKey.NeedsEngine));
        return (int)SyncCliExitCode.NeedsReview;
    }

    private static bool IsHostOnly(EngineCommandKey key) => key is
        EngineCommandKey.PreferencesShow or EngineCommandKey.PreferencesSet or
        EngineCommandKey.HostPause or EngineCommandKey.HostResume or EngineCommandKey.HostStop;

    private static async Task<UiPreferencesResult> RunPreferenceAsync(string[] arguments, EngineCommandKey key,
        CancellationToken ct)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await TryCliAsync(arguments, output, error, ct, requireRunningHost: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (exit is null)
            throw new SyncCliUnsupportedException(EngineCliCatalog.Message(EngineMessageKey.NeedsEngine));
        var text = output.ToString();
        if (exit != (int)SyncCliExitCode.Success) throw PreferenceFailure(key, exit.Value, error.ToString());
        try
        {
            if (text.Length > AutoSyncHostProtocol.MaximumFrameBytes) throw new InvalidDataException(nameof(arguments));
            var result = EngineHostClient.ReadResult<UiPreferencesResult>(text, key);
            ArgumentNullException.ThrowIfNull(result.Preferences);
            ArgumentNullException.ThrowIfNull(result.Preferences.Theme);
            var validation = new UiPreferencesUpdate
            {
                Theme = result.Preferences.Theme,
                StatusIconStyle = result.Preferences.StatusIconStyle,
                PageViewModes = result.Preferences.PageViewModes is { Count: > 0 } pages ? pages : null
            };
            UiPreferencesUpdate.Parse(validation.ToJson());
            return result;
        }
        catch (Exception malformed) when (key == EngineCommandKey.PreferencesSet &&
            malformed is InvalidDataException or ArgumentException or SyncCliException)
        {
            throw new AutoSyncHostUncertainException(EngineHostClient.CommandWord(key), malformed);
        }
    }

    private static Exception PreferenceFailure(EngineCommandKey key, int exit, string text)
    {
        var detail = Detail(text);
        // No trusted host answered before sending, so a save definitely did not reach storage.
        if (exit == (int)SyncCliExitCode.NeedsReview) return new SyncCliUnsupportedException(detail);
        // Missing Exit throws at the transport boundary; this result is a received refusal.
        return new SyncCliOperationException(detail);
    }

    private static string Detail(string error)
    {
        var text = error.Trim();
        return text.Length > 0 ? text : EngineCliCatalog.Message(EngineMessageKey.NeedsEngine);
    }

}
