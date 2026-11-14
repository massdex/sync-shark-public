using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Transport checks for the one-command Engine host pipe that carries the preference commands (design
/// 3.11, RC05-T3/T5/T7/T8): the real EngineHostClient, EngineHostFrame codec and EnginePipeWriter. The shared trust
/// rule refuses a same-process peer, so a trusted peer is supplied by a second process of this same executable; the
/// fixture role is selected by environment variables and only that child runs it.</summary>
public static class EngineHostTransportChecks
{
    private const string LocalServer = ".";
    private const string PairFlag = "--pair";
    private const string SettingsPatch = "{\"theme\":\"Dark\",\"pageViewModes\":{\"activity\":\"Detailed\"}}";
    private const string ErrorReply = "the preference save was refused";
    private const byte UnknownFrameKind = 9;
    private const int FrameHeaderBytes = sizeof(byte) + sizeof(uint);
    private const int WriterCapacity = 256;
    private const int AbsenceDeadlineMilliseconds = 150;
    private const int FixtureConnectionDeadlineMilliseconds = 15000;
    private const int FixtureChildTimeoutMilliseconds = 20000;
    private const string ExitOutcome = "exit";
    private const string AbsentOutcome = "absent";
    private const string ExceptionOutcome = "exception";
    private const string FixturePipeVariable = "SYNCSHARK_TRANSPORT_FIXTURE_PIPE";
    private const string FixtureArgumentsVariable = "SYNCSHARK_TRANSPORT_FIXTURE_ARGUMENTS";
    private const string FixtureReceiptVariable = "SYNCSHARK_TRANSPORT_FIXTURE_RECEIPT";
    private const string FixtureConnectionDeadlineVariable = "SYNCSHARK_TRANSPORT_FIXTURE_DEADLINE";
    private const string FixtureCancelEventVariable = "SYNCSHARK_TRANSPORT_FIXTURE_CANCEL_EVENT";
    private const string FixtureStatusVariable = "SYNCSHARK_TRANSPORT_FIXTURE_STATUS";
    private const string FixtureAutoSyncVariable = "SYNCSHARK_TRANSPORT_FIXTURE_AUTOSYNC";

    private static readonly string[] ShowArguments = ["--cli", "engine", "preferences", "show", "--json"];
    private static readonly string[] SetArguments =
        ["--cli", "engine", "preferences", "set", "--settings", SettingsPatch, "--yes", "--json"];

    private static readonly UiPreferences SavedPreferences = new(
        Theme: UiPreferencesStore.DarkTheme,
        CompactRows: true,
        NotifyCompleted: true,
        NotificationSound: true,
        PageViewModes: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["activity"] = UiPreferencesStore.DetailedView,
            ["runs"] = UiPreferencesStore.SimpleView,
        });

    private static readonly HostStatusUpdate[] StreamedStatuses =
    [
        new(AutoStatus.Running, "phase-one", "first update"),
        new(AutoStatus.Waiting, "provider", "second update"),
        new(AutoStatus.Idle, null, "third update"),
    ];

    /// <summary>Runs every transport case for the preference commands.</summary>
    public static void Run()
    {
        if (RunFixtureClient()) return;
        foreach (var code in new[] { 109, 232, 233 })
            Check.Require(LocalPipePeer.Disconnected(new IOException("closed", unchecked((int)0x80070000) | code)),
                "a disconnected pipe is recoverable at accept");
        Check.Require(!LocalPipePeer.Disconnected(new IOException("denied", unchecked((int)0x80070005))),
            "unrelated listener IO failures are not hidden");
        EarlyDisconnectDoesNotStopListener();
        foreach (var replacement in new[] { false, true })
            foreach (var request in new[] { "HungRequest", "DrainStatus", "Command" })
                ListenerFailureDrainsOpenConnection(replacement, request);
        foreach (var status in new[] { false, true })
        {
            foreach (var code in new[] { 109, 232, 233, 5 })
                ConnectionFailureIsObserved(status, new IOException("fixture failure", unchecked((int)0x80070000) | code),
                    expectedDisconnect: code != 5);
            ConnectionFailureIsObserved(status, new IOException("fixture failure"), false);
            ConnectionFailureIsObserved(status, new InvalidOperationException("fixture failure"), false);
            ConnectionFailureIsObserved(status, new InvalidDataException("fixture failure"), false);
            ConnectionFailureIsObserved(status, new JsonException("fixture failure"), false);
        }
        ClientLeavesBeforeReplyKeepsServing();
        LegacyAutoSyncListenerSurvivesEarlyDisconnect();
        SameProcessPeerIsNeverTrusted();
        AbsentHostIsAbsenceBeforeSending();
        RepairReportsHostWait();
        DelayedAcceptedReportsQuietWait();
        BusyHostIsAbsenceBeforeSending();
        UntrustedPeerIsRefusedBeforeSending();
        CancellationIsNotAbsence();
        PreferenceArgumentsCrossOneRequestFrame();
        OneResponseIsBoundedByTheRetainedPreferenceSize();
        MalformedFramesAreRefusedLoudly();
        HostOutputIsStreamedInBoundedFrames();
        TrustedHostServesOnePreferenceRead();
        TrustedHostCarriesOnlyTheChangedPreferencePatch();
        AcceptedThenLostFailsWithoutReplay();
        MissingExitFailsWithoutReplay();
        MalformedResponseIsRefusedWithoutReplay();
        OversizedResponseIsRefusedWithoutReadingItsPayload();
        CancelledWaitIsNotAbsenceAndSendsNoReplay();
        StatusProjectionRoundTripsThroughTheFrame();
        TrustedSubscribeStreamsStatus();
        EofAndDisconnectUnsubscribeAndJoinTheSender();
        CancelledSubscriptionUnsubscribesAndJoinsTheSender();
        SlowSubscriberGetsOnlyTheLatestState();
        IntakeStopClosesHungRequest();
        IntakeStopPreservesRunningCommand();
        StopCommandReportsSuccess();
    }

    private static void EarlyDisconnectDoesNotStopListener()
    {
        var pipeName = NewPipeName();
        var work = Directory.CreateTempSubdirectory("sync-shark-early-disconnect-").FullName;
        var receiptPath = Path.Combine(work, "receipt.json");
        using var stop = new CancellationTokenSource();
        NamedPipeServerStream? first = null;
        var transferred = false;
        Task? listening = null;
        try
        {
            var instances = 0;
            var recycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NamedPipeServerStream Create()
            {
                var listener = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
                    (Interlocked.Increment(ref instances) == 1 ? PipeOptions.FirstPipeInstance : PipeOptions.None));
                if (Volatile.Read(ref instances) >= 2) recycled.TrySetResult();
                return listener;
            }
            first = Create();
            // The probe is disposed before the listener is asked to accept: the next wait observes ERROR_NO_DATA.
            var probe = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { Await(probe.ConnectAsync(FixtureConnectionDeadlineMilliseconds)); }
            finally { probe.Dispose(); }
            var requests = 0;
            var server = new EngineHostServer(async (_, stdout, _, _) =>
            {
                Interlocked.Increment(ref requests);
                await stdout.WriteAsync(PreferenceReply());
                return (int)SyncCliExitCode.Success;
            });
            listening = server.ListenAsync(first, Create, 32, stop.Token, () => ValueTask.CompletedTask);
            transferred = true; // ListenAsync's finally owns and disposes the first instance from here.
            Check.Require(recycled.Task.Wait(TimeSpan.FromSeconds(5)),
                "a connect-close before accept recycles the listener instead of ending it");
            using var child = StartFixtureChild(pipeName, ShowArguments, receiptPath, null, work);
            Environment.FailFast("H5 synthetic transport crash");
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(FixtureChildTimeoutMilliseconds))
            {
                child.Kill(entireProcessTree: true);
                throw new InvalidOperationException("the next client was not served after the disconnected probe");
            }
            Await(output);
            Await(errors);
            var receipt = JsonSerializer.Deserialize<FixtureReceipt>(File.ReadAllText(receiptPath))!;
            Check.Require(receipt.ExitCode == (int)SyncCliExitCode.Success && receipt.Stdout == PreferenceReply() &&
                requests == 1 && !listening.IsCompleted,
                "a connect-close before accept recycles the pipe and the host serves the next trusted client");
        }
        finally
        {
            stop.Cancel();
            try
            {
                if (listening is not null) Await(listening.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            catch (IOException) { } // the unfixed listener faults with the very disconnect under test
            finally
            {
                if (!transferred) first?.Dispose();
                TryDeleteDirectory(work);
            }
        }
    }

    private static void ListenerFailureDrainsOpenConnection(bool replacement, string request)
    {
        var pipeName = NewPipeName();
        var work = Directory.CreateTempSubdirectory("sync-shark-listener-failure-").FullName;
        using var productRoot = OverrideProductRoot(work);
        using var authority = PilotInstanceLock.Acquire(Path.Combine(work, "state", "instance.lock"));
        var host = EngineHost.Compose(authority.StateAuthority.ProductRoot, _ => { }, authority.StateAuthority,
            StatusHostSettings, StatusHostHttp, TimeProvider.System, _ => null, _ => new InMemoryStateStore(),
            EngineHost.Mode.Persistent);
        using var stop = new CancellationTokenSource();
        using var bound = new CancellationTokenSource(FixtureChildTimeoutMilliseconds);
        using var sent = new EventWaitHandle(false, EventResetMode.ManualReset, pipeName + ".Sent");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource<NamedPipeServerStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failListener = false;
        var instances = 0;
        NamedPipeServerStream Create()
        {
            if (Volatile.Read(ref failListener) && replacement)
                throw new IOException("fixture listener replacement failure");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
                (++instances == 1 ? PipeOptions.FirstPipeInstance : PipeOptions.None));
            if (instances == 2) waiting.TrySetResult(pipe);
            // Close before accept starts; disposing an already-pending Windows accept need not complete it.
            if (Volatile.Read(ref failListener)) pipe.Dispose();
            return pipe;
        }
        var server = new EngineHostServer(async (_, _, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(bound.Token);
            Check.Require(!token.IsCancellationRequested, "listener failure does not cancel the admitted command token");
            return (int)SyncCliExitCode.Success;
        }, host.SubscribeStatusAsync);
        async ValueTask Shutdown()
        {
            var draining = host.DisposeAsync();
            stopped.TrySetResult();
            await draining;
        }
        var listening = server.ListenAsync(Create(), Create, 32, stop.Token, Shutdown);
        var receiptPath = Path.Combine(work, "receipt.json");
        using var child = StartFixtureChild(pipeName, ShowArguments, receiptPath, null, work,
            request == "Command" ? null : request);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            Await(waiting.Task.WaitAsync(bound.Token));
            if (request == "Command") Await(entered.Task.WaitAsync(bound.Token));
            else Check.Require(sent.WaitOne(FixtureChildTimeoutMilliseconds), "the trusted connection is open before failure");
            Volatile.Write(ref failListener, true);
            using (var probe = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                Await(probe.ConnectAsync(FixtureConnectionDeadlineMilliseconds));
            Await(stopped.Task.WaitAsync(bound.Token));
            Check.Require(host.Status().Status == AutoStatus.Off, "fatal listener failure starts the existing host shutdown");
            if (request == "Command") release.TrySetResult();
            Exception? failure = null;
            try { Await(listening.WaitAsync(bound.Token)); }
            catch (Exception error) when (error is not OperationCanceledException || !bound.IsCancellationRequested)
            { failure = error; }
            Check.Require(failure is not null, "fatal listener failure remains observable after the drain");
            Check.Require(child.WaitForExit(FixtureChildTimeoutMilliseconds), "shutdown closes the open trusted connection");
            Check.Require(File.Exists(receiptPath), "the child wrote a drain receipt: " + Await(output) + Await(errors));
            if (request == "DrainStatus")
            {
                var receipt = JsonSerializer.Deserialize<StatusReceipt>(File.ReadAllText(receiptPath))!;
                Check.Require(receipt.Error == typeof(IOException).FullName && receipt.Updates[^1].Status == AutoStatus.Off,
                    "host shutdown publishes Off and the client reports the closed subscription");
            }
            else
            {
                var receipt = JsonSerializer.Deserialize<FixtureReceipt>(File.ReadAllText(receiptPath))!;
                Check.Require(receipt.Outcome == ExitOutcome && receipt.ExitCode ==
                    (request == "HungRequest" ? -1 : (int)SyncCliExitCode.Success),
                    "shutdown closes unread intake and preserves the admitted command Exit");
            }
        }
        finally
        {
            release.TrySetResult();
            stop.Cancel();
            Await(host.DisposeAsync().AsTask());
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            try { Await(listening.WaitAsync(TimeSpan.FromMilliseconds(FixtureChildTimeoutMilliseconds))); }
            catch (Exception) { } // Join the injected failure without replacing the assertion failure.
            authority.Dispose();
            TryDeleteDirectory(work);
        }
    }

    private static void ConnectionFailureIsObserved(bool status, Exception failure, bool expectedDisconnect)
    {
        var pipeName = NewPipeName();
        var work = Directory.CreateTempSubdirectory("sync-shark-connection-failure-").FullName;
        using var stop = new CancellationTokenSource();
        using var bound = new CancellationTokenSource(FixtureChildTimeoutMilliseconds);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sent = new EventWaitHandle(false, EventResetMode.ManualReset, pipeName + ".Sent");
        var calls = 0;
        var create = ListenerFactory(pipeName);
        async IAsyncEnumerable<HostStatusUpdate> Statuses([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return StreamedStatuses[0];
            throw failure;
        }
        var server = new EngineHostServer((_, _, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1 && !status) throw failure;
            return Task.FromResult((int)SyncCliExitCode.Success);
        }, Statuses);
        var listening = server.ListenAsync(create(), create, 32, stop.Token, () =>
        {
            stopped.TrySetResult();
            return ValueTask.CompletedTask;
        });
        using var child = StartFixtureChild(pipeName, ShowArguments, Path.Combine(work, "fault.json"), null, work,
            status ? "DrainStatus" : null);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            Check.Require(child.WaitForExit(FixtureChildTimeoutMilliseconds), "the faulting exchange closes without a replay");
            Await(output);
            Await(errors);
            if (expectedDisconnect)
            {
                Check.Require(!listening.IsCompleted && !stopped.Task.IsCompleted,
                    "an expected peer disconnect does not stop the listener");
                var receipt = ServeNextClient(pipeName, work, "next.json",
                    "the next request is served after disconnect");
                Check.Require(receipt.ExitCode == (int)SyncCliExitCode.Success,
                    "expected disconnect is consumed before the next accept prunes the finished task");
                stop.Cancel();
                Await(listening.WaitAsync(bound.Token));
            }
            else
            {
                Await(stopped.Task.WaitAsync(bound.Token));
                Exception? observed = null;
                try { Await(listening.WaitAsync(bound.Token)); }
                catch (Exception error) { observed = error; }
                Check.Require(ReferenceEquals(observed, failure),
                    "a genuine connection fault starts shutdown and is observed without needing another accept");
            }
        }
        finally
        {
            stop.Cancel();
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            try { Await(listening.WaitAsync(TimeSpan.FromMilliseconds(FixtureChildTimeoutMilliseconds))); }
            catch (Exception) { } // The assertion above owns the injected fault's outcome.
            TryDeleteDirectory(work);
        }
    }

    // B-189: a client that leaves mid-command makes the command's next write fail, and the catch-all's write of
    // the failure then raises .NET's message-only IOException (COR_E_IO) from CheckWriteOperations. It must end
    // only that connection: the listener keeps accepting and the host is not stopped.
    private static void ClientLeavesBeforeReplyKeepsServing()
    {
        var pipeName = NewPipeName();
        var work = Directory.CreateTempSubdirectory("sync-shark-client-leaves-").FullName;
        using var stop = new CancellationTokenSource();
        using var bound = new CancellationTokenSource(FixtureChildTimeoutMilliseconds);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var create = ListenerFactory(pipeName);
        var server = new EngineHostServer(async (_, stdout, _, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(bound.Token);
            try { await stdout.WriteAsync(PreferenceReply()); }
            catch (IOException) { await stdout.WriteAsync(PreferenceReply()); } // SyncCli.cs:425-429 writes the failure to the same broken pipe
            return (int)SyncCliExitCode.Success;
        });
        var listening = server.ListenAsync(create(), create, 32, stop.Token, () =>
        {
            stopped.TrySetResult();
            return ValueTask.CompletedTask;
        });
        using var first = StartFixtureChild(pipeName, ShowArguments, Path.Combine(work, "first.json"), null, work);
        try
        {
            Await(entered.Task.WaitAsync(bound.Token));
            if (!first.HasExited) first.Kill(entireProcessTree: true);
            first.WaitForExit();
            release.TrySetResult();
            var receipt = ServeNextClient(pipeName, work, "next.json",
                "the listener serves the next request after a client left mid-command");
            Check.Require(receipt.Outcome == ExitOutcome && receipt.ExitCode == (int)SyncCliExitCode.Success &&
                !listening.IsCompleted && !stopped.Task.IsCompleted,
                "a client that left before the reply ends only its connection");
            stop.Cancel();
            Await(listening.WaitAsync(bound.Token));
        }
        finally
        {
            release.TrySetResult();
            stop.Cancel();
            if (!first.HasExited) first.Kill(entireProcessTree: true);
            first.WaitForExit();
            try { Await(listening.WaitAsync(TimeSpan.FromMilliseconds(FixtureChildTimeoutMilliseconds))); }
            catch (Exception) { } // The assertion above owns the listener's outcome.
            TryDeleteDirectory(work);
        }
    }

    // The legacy AutoSync host owns a second listener. Its engine factory blocks here so the probe can connect and
    // close before any accept starts; the listener must recycle and then serve a trusted request.
    private static void LegacyAutoSyncListenerSurvivesEarlyDisconnect()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = NewPipeName();
        var work = Directory.CreateTempSubdirectory("sync-shark-legacy-disconnect-").FullName;
        var receiptPath = Path.Combine(work, "receipt.json");
        using var listening = new ManualResetEventSlim();
        using var compose = new ManualResetEventSlim();
        AutoSyncBackgroundHost? host = null;
        var starting = Task.Run(() =>
        {
            host = AutoSyncBackgroundHost.Start(pipeName, () => new Lease(), () =>
            {
                listening.Set();
                // A setup deadline fails the fixture instead of starting the listener before the probe is gone.
                if (!compose.Wait(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException(
                        "the early-disconnect probe was not disposed before the listener started");
                return new ProofEngine();
            });
        });
        try
        {
            Check.Require(listening.Wait(TimeSpan.FromSeconds(5)),
                "the legacy host listener exists before its engine is composed");
            var probe = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { Await(probe.ConnectAsync(FixtureConnectionDeadlineMilliseconds)); }
            finally { probe.Dispose(); }
            compose.Set(); // released only after the probe was disposed
            Await(starting.WaitAsync(TimeSpan.FromSeconds(5)));
            using var child = StartFixtureChild(pipeName, ShowArguments, receiptPath, null, work,
                autoSyncPipe: pipeName);
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(FixtureChildTimeoutMilliseconds))
            {
                child.Kill(entireProcessTree: true);
                throw new InvalidOperationException(
                    "the legacy host did not answer the trusted request after the disconnected probe");
            }
            Await(output);
            Await(errors);
            var receipt = JsonSerializer.Deserialize<FixtureReceipt>(File.ReadAllText(receiptPath))!;
            Check.Require(receipt.Outcome == ExitOutcome &&
                receipt.Stdout.Contains("legacy-listener-served", StringComparison.Ordinal),
                "a connect-close before accept leaves the legacy AutoSync listener running and serving the next trusted request");
        }
        finally
        {
            compose.Set();
            try { Await(starting); }
            catch (Exception) { } // Preserve the original failure after joining startup, even when setup timed out.
            if (host is not null)
            {
                try { Await(host.DisposeAsync().AsTask()); }
                catch (IOException) { } // the unfixed listener faults with the very disconnect under test
            }
            TryDeleteDirectory(work);
        }
    }

    // The shared trust owner refuses a peer that is this process, so no production same-process bypass exists and
    // the trusted cases below need a peer in another process of the same executable image.
    private static void SameProcessPeerIsNeverTrusted()
    {
        var pipeName = NewPipeName();
        using var server = NewListener(pipeName);
        var accepting = server.WaitForConnectionAsync();
        using var probe = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Await(probe.ConnectAsync());
        Check.Require(accepting.Wait(TimeSpan.FromSeconds(5)), "the same-process probe connected");
        Check.Require(!LocalPipePeer.Trusted(server, server: true),
            "the shared trust rule refuses a same-process client instead of admitting it");
        Check.Require(!LocalPipePeer.Trusted(probe, server: false),
            "the shared trust rule refuses a same-process server instead of admitting it");
    }

    // No host answered before anything was sent: absence is not a command failure and creates no local state.
    private static void AbsentHostIsAbsenceBeforeSending()
    {
        var work = Directory.CreateTempSubdirectory("sync-shark-transport-absent-").FullName;
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = Await(new EngineHostClient(NewPipeName(), TimeSpan.FromMilliseconds(
                AbsenceDeadlineMilliseconds)).TryExecuteAsync(ShowArguments, stdout, stderr,
                CancellationToken.None));
            Check.Require(exit is null, "an absent host is reported as no host, not as a failed command");
            Check.Require(stdout.ToString().Length == 0 && stderr.ToString().Length == 0,
                "an absent host receives no command and produces no output");
            Check.Require(!Directory.EnumerateFileSystemEntries(work).Any(),
                "a preference read against an absent host writes no local state");
        }
        finally { TryDeleteDirectory(work); }
    }

    private static void RepairReportsHostWait()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var executing = new EngineHostClient(NewPipeName(), TimeSpan.FromMilliseconds(AbsenceDeadlineMilliseconds))
            .TryExecuteAsync(["--cli", "engine", "repair", "--pair", "pair-a", "--yes"], stdout, stderr,
                CancellationToken.None);
        Check.Require(stderr.ToString().Contains(Text.Get(TextKey.StageHostWait), StringComparison.Ordinal),
            "Repair reports the host wait before the connection attempt completes");
        Check.Require(Await(executing) is null && stdout.ToString().Length == 0,
            "host-wait progress preserves absence and keeps result stdout untouched");
        Check.Require(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "an absent host closes the wait stage");
    }

    private static void DelayedAcceptedReportsQuietWait() => WithFixture(
        ["--cli", "engine", "repair", "--pair", "pair-a", "--yes"], FixtureScript.DelayedAccepted, run =>
        {
            Check.Require(run.Receipt.ExitCode == (int)SyncCliExitCode.Success &&
                run.Receipt.Stderr.Contains(Text.Get(TextKey.StatusStillWaiting,
                    (TextArg.Reason, Text.Get(TextKey.StageHostWait)))),
                "connected Repair emits a quiet heartbeat before the delayed Accepted acknowledgment");
        });

    // Every server instance is already attached, so no instance can admit this command before the deadline: the
    // caller derives Busy from its own authority, and nothing is sent or started here.
    private static void BusyHostIsAbsenceBeforeSending()
    {
        var pipeName = NewPipeName();
        using var server = NewListener(pipeName);
        using var occupant = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Await(occupant.ConnectAsync());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Await(new EngineHostClient(pipeName,
            TimeSpan.FromMilliseconds(AbsenceDeadlineMilliseconds)).TryExecuteAsync(ShowArguments, stdout, stderr,
            CancellationToken.None));
        Check.Require(exit is null, "a fully occupied host admits no command and is not reported as a failure");
        Check.Require(stdout.ToString().Length == 0 && stderr.ToString().Length == 0,
            "a busy host receives no request bytes from this client");
    }

    // An untrusted peer is refused before any request byte, and the client has no local fallback or replay.
    private static void UntrustedPeerIsRefusedBeforeSending()
    {
        var pipeName = NewPipeName();
        using var server = NewListener(pipeName);
        var accepting = server.WaitForConnectionAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Await(new EngineHostClient(pipeName, TimeSpan.FromSeconds(5))
                .TryExecuteAsync(ShowArguments, stdout, stderr, CancellationToken.None));
            Check.Require(false, "the transport accepted a same-process peer as trusted");
        }
        catch (UnauthorizedAccessException) { }
        Check.Require(accepting.Wait(TimeSpan.FromSeconds(5)), "the untrusted peer still connected");
        var received = server.ReadAsync(new byte[1], CancellationToken.None).AsTask();
        Check.Require(received.Wait(TimeSpan.FromSeconds(5)), "the refused peer closed its connection");
        Check.Require(received.Result == 0, "an untrusted peer receives no request bytes, so nothing was sent");
        Check.Require(stdout.ToString().Length == 0 && stderr.ToString().Length == 0,
            "a refused peer produces no output or local fallback");
    }

    // The caller's own cancellation is distinct from an absent host and never becomes a command failure.
    private static void CancellationIsNotAbsence()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Await(new EngineHostClient(NewPipeName(), TimeSpan.FromSeconds(5))
                .TryExecuteAsync(ShowArguments, stdout, stderr, cancellation.Token));
            Check.Require(false, "a cancelled preference command was reported as an absent host");
        }
        catch (OperationCanceledException) { }
        Check.Require(stdout.ToString().Length == 0 && stderr.ToString().Length == 0,
            "a cancelled preference command writes no output");
    }

    // One request frame carries the exact preference command; a save carries only the changed patch and --yes.
    private static void PreferenceArgumentsCrossOneRequestFrame()
    {
        var show = RoundTrip(EngineHostFrame.CreateArguments(ShowArguments));
        Check.Require(show.Kind == EngineHostFrameKind.Arguments &&
                      show.DecodeArguments().SequenceEqual(ShowArguments),
            "the preference show command crosses one request frame unchanged");
        var save = RoundTrip(EngineHostFrame.CreateArguments(SetArguments)).DecodeArguments();
        Check.Require(save.SequenceEqual(SetArguments),
            "the changed-field patch and its confirmation cross one request frame unchanged");
        Check.Require(!save.Contains(PairFlag), "a preference save never names a pair target");
        Check.Require(RoundTrip(EngineHostFrame.CreateExit((int)SyncCliExitCode.Success)).DecodeExitCode() ==
                      (int)SyncCliExitCode.Success, "a completed command returns its success exit code");
        Check.Require(RoundTrip(EngineHostFrame.CreateExit((int)SyncCliExitCode.Failed)).DecodeExitCode() ==
                      (int)SyncCliExitCode.Failed, "a failed command returns the existing failure exit code");
    }

    // One response frame carries a whole preference result, bounded by the retained document's own ceiling.
    private static void OneResponseIsBoundedByTheRetainedPreferenceSize()
    {
        var reply = PreferenceReply();
        var payload = Encoding.UTF8.GetBytes(reply);
        Check.Require(payload.Length <= AutoSyncHostProtocol.MaximumFrameBytes,
            "the typed preference result fits one bounded transport frame");
        Check.Require(RoundTrip(new EngineHostFrame(EngineHostFrameKind.StandardOutput, payload)).DecodeText() ==
                      reply, "one response frame carries the whole typed preference result");
        Check.Require(RetainedPreferenceCeiling() == AutoSyncHostProtocol.MaximumFrameBytes,
            "one response is bounded by the retained preference document ceiling");
    }

    // Kind, length, ordering and UTF-8 are validated by the shared codec; a malformed frame is loud, never silent.
    private static void MalformedFramesAreRefusedLoudly()
    {
        Refused(Raw(UnknownFrameKind, 0, null), "an unknown frame kind");
        Refused(Raw((byte)EngineHostFrameKind.StandardOutput, (uint)AutoSyncHostProtocol.MaximumFrameBytes + 1u,
            null), "a declared length above the shared bound");
        Refused(Raw((byte)EngineHostFrameKind.StandardOutput, 4, new byte[2]), "a truncated payload");
        Refused(Raw((byte)EngineHostFrameKind.StandardOutput, 1, new byte[] { 0xff }), "invalid UTF-8 payload text");
        Refused(Raw((byte)EngineHostFrameKind.Exit, 1, new byte[1]), "an Exit frame with the wrong payload size");
        Refused(Raw((byte)EngineHostFrameKind.Accepted, 1, new byte[] { (byte)'a' }),
            "an Accepted frame carrying a payload");
        Refused(Raw((byte)EngineHostFrameKind.Arguments, 0, null), "an empty Arguments frame");
        RefusedArguments(Encoding.UTF8.GetBytes("{}"), "an Arguments frame that is not a JSON array");
        RefusedArguments(Encoding.UTF8.GetBytes("[1]"), "an Arguments frame with a non-string element");
        Refused(Raw((byte)EngineHostFrameKind.SubscribeStatus, 1, new byte[] { (byte)'x' }),
            "a SubscribeStatus frame carrying a payload");
        Refused(Raw((byte)EngineHostFrameKind.SubscribeStatus, 2, new byte[1]),
            "a truncated SubscribeStatus payload");
        Refused(Raw((byte)EngineHostFrameKind.Status, 1, new byte[] { 0xff }),
            "a Status frame with invalid UTF-8");
        Refused(new byte[FrameHeaderBytes - 2], "a truncated frame header");
        Check.Require(Await(EngineHostFrame.ReadAsync(new MemoryStream(), CancellationToken.None)) is null,
            "a clean end of stream before any header is not an error");
    }

    // The host's text is streamed frame by frame within the pipe's capacity, never accumulated, and each stream
    // keeps its own frame kind.
    private static void HostOutputIsStreamedInBoundedFrames()
    {
        var pipeName = NewPipeName();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
            WriterCapacity, WriterCapacity);
        using var client = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepting = server.WaitForConnectionAsync();
        Await(client.ConnectAsync());
        Check.Require(accepting.Wait(TimeSpan.FromSeconds(5)), "the streaming reader connected");
        var reply = PreferenceReply() + PreferenceReply() + PreferenceReply();
        var frames = new List<EngineHostFrame>();
        var reading = Task.Run(() =>
        {
            while (EngineHostFrame.ReadAsync(client, CancellationToken.None).GetAwaiter().GetResult() is { } frame)
                lock (frames) frames.Add(frame);
        });
        var standardOutput = new EnginePipeWriter(server, EngineHostFrameKind.StandardOutput);
        var standardError = new EnginePipeWriter(server, EngineHostFrameKind.StandardError);
        Await(standardOutput.WriteAsync(reply));
        Await(standardError.WriteAsync(ErrorReply));
        server.Dispose();
        Check.Require(reading.Wait(TimeSpan.FromSeconds(5)), "the last frame arrived and the writer closed");
        EngineHostFrame[] output;
        lock (frames) output = frames.ToArray();
        var stdoutFrames = output.Where(frame => frame.Kind == EngineHostFrameKind.StandardOutput).ToArray();
        var stderrFrames = output.Where(frame => frame.Kind == EngineHostFrameKind.StandardError).ToArray();
        Check.Require(stdoutFrames.Length > 1,
            "a long preference reply is streamed in several bounded chunks instead of one accumulated buffer");
        Check.Require(stdoutFrames.All(frame => frame.Payload.Length <= WriterCapacity),
            "every streamed chunk stays within the pipe's frame capacity");
        Check.Require(string.Concat(stdoutFrames.Select(frame => frame.DecodeText())) == reply,
            "the streamed chunks reassemble the exact preference reply");
        Check.Require(stderrFrames.Length == 1 && stderrFrames[0].DecodeText() == ErrorReply,
            "the error stream keeps its own frame kind");
        Check.Require(output.Take(stdoutFrames.Length).All(frame => frame.Kind == EngineHostFrameKind.StandardOutput),
            "the two output streams are not interleaved");
    }

    // A trusted host answers one read-only preference command with exactly one bounded response and no local state.
    private static void TrustedHostServesOnePreferenceRead()
    {
        WithFixture(ShowArguments, FixtureScript.ShowReply, run =>
        {
            Check.Require(run.Receipt.Outcome == ExitOutcome &&
                          run.Receipt.ExitCode == (int)SyncCliExitCode.Success,
                "a trusted host returns the preference read's success exit code: " + Describe(run.Receipt));
            Check.Require(run.Receipt.Stdout == PreferenceReply(),
                "the whole typed preference result arrives on standard output");
            Check.Require(run.Receipt.Stderr.Length == 0, "a successful preference read writes no standard error");
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1 &&
                          run.Trace.Requests[0].SequenceEqual(ShowArguments),
                "one command opens one connection and sends exactly one request frame");
            Check.Require(run.Trace.OutputFrames == 1 &&
                          run.Trace.LargestOutputPayload == Encoding.UTF8.GetByteCount(PreferenceReply()),
                "exactly one bounded response frame carries the retained preference result");
            Check.Require(run.Trace.Error is null,
                "the trusted peer completed its scripted response: " + run.Trace.Error);
            Check.Require(!Directory.EnumerateFileSystemEntries(run.WorkDirectory).Any(),
                "a preference read through the shared transport creates no local state or host of its own");
        });
    }

    // A trusted host receives the exact changed patch and nothing else, and one save sends one command.
    private static void TrustedHostCarriesOnlyTheChangedPreferencePatch()
    {
        WithFixture(SetArguments, FixtureScript.SetReply, run =>
        {
            Check.Require(run.Receipt.Outcome == ExitOutcome &&
                          run.Receipt.ExitCode == (int)SyncCliExitCode.Success,
                "a trusted host returns the preference save's success exit code: " + Describe(run.Receipt));
            Check.Require(run.Receipt.Stdout == PreferenceReply(),
                "the verified merged preference result arrives on standard output");
            Check.Require(run.Trace.Error is null,
                "the trusted peer completed its scripted response: " + run.Trace.Error);
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1 &&
                          run.Trace.Requests[0].SequenceEqual(SetArguments),
                "one save sends exactly one request frame carrying only the changed patch and --yes");
            Check.Require(!run.Trace.Requests[0].Contains(PairFlag),
                "a preference save never names a pair target");
        });
    }

    // A transport lost after acceptance is the existing failure result: no replay, no invented output.
    private static void AcceptedThenLostFailsWithoutReplay()
    {
        WithFixture(ShowArguments, FixtureScript.AcceptedThenClose, run =>
        {
            Check.Require(run.Receipt.Outcome == ExitOutcome &&
                          run.Receipt.ExitCode == (int)SyncCliExitCode.Failed,
                "a reply lost after acceptance is the existing failure result, never success: " +
                Describe(run.Receipt));
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1,
                "the lost reply is never replayed on a second connection or request");
            Check.Require(run.Receipt.Stdout.Length == 0,
                "no output is invented when the reply is lost after acceptance");
        });
    }

    // A missing Exit is uncertainty: the received output is kept and the command is never resent.
    private static void MissingExitFailsWithoutReplay()
    {
        WithFixture(ShowArguments, FixtureScript.ReplyThenClose, run =>
        {
            Check.Require(run.Receipt.Outcome == ExitOutcome &&
                          run.Receipt.ExitCode == (int)SyncCliExitCode.Failed,
                "a missing Exit is uncertainty, not success: " + Describe(run.Receipt));
            Check.Require(run.Receipt.Stdout == PreferenceReply(),
                "the output already streamed before the missing Exit is kept");
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1,
                "a missing Exit never resends the preference command");
        });
    }

    // A malformed or out-of-order response after acceptance is loud and is never replayed or replaced by a local command.
    private static void MalformedResponseIsRefusedWithoutReplay()
    {
        WithFixture(ShowArguments, FixtureScript.MalformedFrame, run =>
        {
            Check.Require(run.Receipt.Outcome == ExceptionOutcome &&
                          run.Receipt.Error == typeof(InvalidDataException).FullName,
                "an unknown response frame kind is a loud protocol failure: " + Describe(run.Receipt));
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1,
                "a malformed response is never replayed or fallen back to a local command");
        });
        WithFixture(ShowArguments, FixtureScript.OutputBeforeAccepted, run =>
        {
            Check.Require(run.Receipt.Outcome == ExceptionOutcome &&
                          run.Receipt.Error == typeof(InvalidDataException).FullName,
                "output before the acceptance frame is a loud protocol failure: " + Describe(run.Receipt));
            Check.Require(run.Receipt.Stdout.Length == 0,
                "no output is forwarded before the acceptance frame");
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1,
                "an out-of-order response is never replayed");
        });
    }

    // An over-bound response is refused from its header, so an oversized reply is never buffered.
    private static void OversizedResponseIsRefusedWithoutReadingItsPayload()
    {
        WithFixture(ShowArguments, FixtureScript.OversizedFrame, run =>
        {
            Check.Require(run.Receipt.Outcome == ExceptionOutcome &&
                          run.Receipt.Error == typeof(InvalidDataException).FullName,
                "an over-bound response header is a loud protocol failure: " + Describe(run.Receipt));
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1,
                "an over-bound response is refused without a second request");
        });
        var guarded = new HeaderOnlyStream(Raw((byte)EngineHostFrameKind.StandardOutput,
            (uint)AutoSyncHostProtocol.MaximumFrameBytes + 1u, null));
        try
        {
            Await(EngineHostFrame.ReadAsync(guarded, CancellationToken.None));
            Check.Require(false, "the codec accepted an over-bound declared length");
        }
        catch (InvalidDataException) { }
        Check.Require(!guarded.PayloadReadAttempted,
            "an over-bound frame is refused before its payload is read or buffered");
    }

    // A cancel while waiting is neither absence nor failure, and a cancelled wait never replays the command.
    private static void CancelledWaitIsNotAbsenceAndSendsNoReplay()
    {
        WithFixture(ShowArguments, FixtureScript.WaitForCancel, run =>
        {
            Check.Require(run.Receipt.Outcome == ExceptionOutcome && run.Receipt.Cancelled,
                "a cancel while waiting for the reply is a cancellation, not absence or failure: " +
                Describe(run.Receipt));
            Check.Require(run.Trace.Connections == 1 && run.Trace.Requests.Count == 1,
                "a cancelled wait sends its one request and never replays it");
        }, cancelAfterAccepted: true);
    }

    // The Status frame carries the transient HostStatusUpdate projection unchanged, in both directions.
    private static void StatusProjectionRoundTripsThroughTheFrame()
    {
        foreach (var status in StreamedStatuses)
        {
            var frame = EngineHostFrame.CreateStatus(status);
            Check.Require(frame.Kind == EngineHostFrameKind.Status,
                "a HostStatusUpdate is projected as a Status frame");
            Check.Require(RoundTrip(frame).DecodeStatus() == status,
                "the HostStatusUpdate projection round trips through one Status frame");
        }
        var subscribe = Await(EngineHostFrame.ReadAsync(
            new MemoryStream(Raw((byte)EngineHostFrameKind.SubscribeStatus, 0, null)), CancellationToken.None));
        Check.Require(subscribe.HasValue &&
                      subscribe.Value.Kind == EngineHostFrameKind.SubscribeStatus &&
                      subscribe.Value.Payload.Length == 0,
            "an empty SubscribeStatus frame is the valid subscription request");
        try
        {
            EngineHostFrame.CreateExit((int)SyncCliExitCode.Success).DecodeStatus();
            Check.Require(false, "a non-Status frame was decoded as a host status");
        }
        catch (InvalidDataException) { }
    }

    // A trusted subscription streams the host's status frames in order and unsubscribes when the client closes.
    private static void TrustedSubscribeStreamsStatus()
    {
        WithStatusFixture(StatusScript.Stream, run =>
        {
            Check.Require(run.Receipt.Outcome == ExitOutcome && run.Receipt.Error is null,
                "a trusted subscription completed without failure: " + run.Receipt.Outcome + " " +
                run.Receipt.Error);
            Check.Require(run.Receipt.Received == StreamedStatuses.Length &&
                          run.Receipt.Updates.SequenceEqual(StreamedStatuses),
                "a trusted subscription streams the exact HostStatusUpdate projections in order");
            Check.Require(run.SourceDisposed,
                "the host subscription unsubscribes after the trusted client stops reading");
        });
    }

    // An end of stream without cancellation unsubscribes the sender, and the server joins it before returning.
    private static void EofAndDisconnectUnsubscribeAndJoinTheSender()
    {
        WithStatusFixture(StatusScript.Disconnect, run =>
        {
            Check.Require(run.Receipt.Outcome == ExitOutcome && run.Receipt.Error is null,
                "a client disconnect without cancellation is not an error: " + run.Receipt.Outcome + " " +
                run.Receipt.Error);
            Check.Require(run.Receipt.Received == 1 && run.Receipt.Updates[0] == StreamedStatuses[0],
                "the client received the one status sent before it closed the pipe");
            Check.Require(run.SourceDisposed,
                "an end of stream unsubscribes the sender instead of leaving it running");
        });
    }

    // A cancelled subscription is reported as cancellation, never absence or a failed command, and unsubscribes.
    private static void CancelledSubscriptionUnsubscribesAndJoinsTheSender()
    {
        WithStatusFixture(StatusScript.Cancel, run =>
        {
            Check.Require(run.Receipt.Cancelled && run.Receipt.Received == 1,
                "a cancelled subscription is reported as cancellation after its first status: " +
                run.Receipt.Outcome + " " + run.Receipt.Error);
            Check.Require(run.SourceDisposed,
                "cancellation unsubscribes the sender instead of leaving it running");
        });
    }

    // A subscriber that stops reading receives the latest bounded state only: the host drops the intermediate
    // instead of growing an unbounded queue, and the subscription ends when the host stops.
    private static void SlowSubscriberGetsOnlyTheLatestState()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-status-subscribe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var productRoot = OverrideProductRoot(root);
            using var instance = PilotInstanceLock.Acquire(
                Path.Combine(root, "status-subscribe", "state", "instance.lock"));
            var store = new InMemoryStateStore();
            const string PairId = "status-subscribe-pair";
            store.SavePair(new PairConfig(PairId, PairId, @"C:\EngineHostFixture", "/EngineHostFixture",
                "connection-a", [], [], FirstRunPolicy.NewestWins, true, null));
            var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
                instance.StateAuthority, StatusHostSettings, StatusHostHttp, TimeProvider.System, _ => null,
                _ => store, EngineHost.Mode.Persistent);
            try
            {
                var enumerator = host.SubscribeStatusAsync(CancellationToken.None).GetAsyncEnumerator();
                try
                {
                    Check.Require(Await(enumerator.MoveNextAsync().AsTask()),
                        "a subscriber first receives the current host state");
                    Check.Require(enumerator.Current.Status is null,
                        "the first host state is the host's current unstarted state");
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var cancel = new CancellationTokenSource();
                    var running = host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                            return pair;
                        }, cancel.Token);
                    Await(entered.Task);
                    // The subscriber deliberately does not read while newer states are published.
                    cancel.Cancel();
                    try { Await(running); }
                    catch (OperationCanceledException) { }
                    Await(host.DisposeAsync().AsTask());
                    Check.Require(Await(enumerator.MoveNextAsync().AsTask()),
                        "a slow subscriber still receives the latest state");
                    Check.Require(enumerator.Current.Status == AutoStatus.Off,
                        "a slow subscriber gets the latest state only, not the dropped intermediate");
                    Check.Require(!Await(enumerator.MoveNextAsync().AsTask()),
                        "the bounded subscription ends without an unbounded queue");
                }
                finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // The legacy AutoSync protocol client role: the parent's host must serve this trusted request after recovering
    // from the early-disconnect probe.
    private static bool RunLegacyAutoSyncFixtureClient()
    {
        var pipe = Environment.GetEnvironmentVariable(FixtureAutoSyncVariable);
        if (string.IsNullOrEmpty(pipe)) return false;
        var receiptPath = Environment.GetEnvironmentVariable(FixtureReceiptVariable) ??
                          throw new InvalidOperationException("The transport fixture child has no receipt path.");
        FixtureReceipt receipt;
        try
        {
            var reply = Await(new AutoSyncHostClient(pipe).TrySendAsync(
                AutoSyncHostClient.Request("cli") with { CliArgs = ["--cli", "status"] },
                TimeSpan.FromMilliseconds(FixtureConnectionDeadlineMilliseconds)));
            receipt = reply is { Ok: true }
                ? new FixtureReceipt(ExitOutcome, 0, reply.Data?.GetRawText() ?? "", "", null, false)
                : new FixtureReceipt(AbsentOutcome, null, "", "", "the legacy host did not reply", false);
        }
        catch (Exception error)
        {
            receipt = new FixtureReceipt(ExceptionOutcome, null, "", "", error.GetType().FullName, false);
        }
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        return true;
    }

    // The one fixture role: the child performs the real client call and reports its outcome to the parent.
    private static bool RunFixtureClient()
    {
        if (RunLegacyAutoSyncFixtureClient()) return true;
        if (Environment.GetEnvironmentVariable(FixtureStatusVariable) == "HungRequest")
        {
            var name = Environment.GetEnvironmentVariable(FixturePipeVariable)!;
            using var client = new NamedPipeClientStream(LocalServer, name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Await(client.ConnectAsync(FixtureConnectionDeadlineMilliseconds));
            client.WriteByte((byte)EngineHostFrameKind.Arguments);
            client.Flush();
            using var sent = EventWaitHandle.OpenExisting(name + ".Sent");
            sent.Set();
            var received = client.ReadByte();
            File.WriteAllText(Environment.GetEnvironmentVariable(FixtureReceiptVariable)!,
                JsonSerializer.Serialize(new FixtureReceipt(ExitOutcome, received, "", "", null, false)));
            return true;
        }
        if (RunStatusFixtureClient()) return true;
        var pipe = Environment.GetEnvironmentVariable(FixturePipeVariable);
        if (string.IsNullOrEmpty(pipe)) return false;
        var receiptPath = Environment.GetEnvironmentVariable(FixtureReceiptVariable) ??
                          throw new InvalidOperationException("The transport fixture child has no receipt path.");
        var arguments = JsonSerializer.Deserialize<string[]>(
            Environment.GetEnvironmentVariable(FixtureArgumentsVariable) ??
            throw new InvalidOperationException("The transport fixture child has no request arguments.")) ??
            throw new InvalidOperationException("The transport fixture child received an empty request.");
        using var stdout = new StringWriter();
        using var quietSignal = new EventWaitHandle(false, EventResetMode.ManualReset, pipe + ".Quiet");
        using var stderr = new QuietWriter(quietSignal);
        using var cancellation = new CancellationTokenSource();
        using var cancelSignal =
            Environment.GetEnvironmentVariable(FixtureCancelEventVariable) is { Length: > 0 } cancelEvent
                ? new EventWaitHandle(false, EventResetMode.ManualReset, cancelEvent)
                : null;
        Task? cancelWaiter = null;
        if (cancelSignal is not null)
        {
            var signal = cancelSignal;
            cancelWaiter = Task.Run(() =>
            {
                signal.WaitOne();
                cancellation.Cancel();
            });
        }
        FixtureReceipt receipt;
        try
        {
            var exit = new EngineHostClient(pipe, TimeSpan.FromMilliseconds(FixtureConnectionDeadlineMilliseconds))
                .TryExecuteAsync(arguments, stdout, stderr, cancellation.Token).GetAwaiter().GetResult();
            receipt = new FixtureReceipt(exit is null ? AbsentOutcome : ExitOutcome, exit, stdout.ToString(),
                stderr.ToString(), null, false);
        }
        catch (OperationCanceledException error)
        {
            receipt = new FixtureReceipt(ExceptionOutcome, null, stdout.ToString(), stderr.ToString(),
                error.GetType().FullName, true);
        }
        catch (Exception error)
        {
            receipt = new FixtureReceipt(ExceptionOutcome, null, stdout.ToString(), stderr.ToString(),
                error.GetType().FullName, false);
        }
        cancelSignal?.Set();
        cancelWaiter?.Wait();
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        return true;
    }

    private sealed class QuietWriter(EventWaitHandle quiet) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value == Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, Text.Get(TextKey.StageHostWait))))
                quiet.Set();
        }
    }

    // The status fixture child runs the real SubscribeStatusAsync against the parent's real server.
    private static bool RunStatusFixtureClient()
    {
        var scenario = Environment.GetEnvironmentVariable(FixtureStatusVariable);
        var pipe = Environment.GetEnvironmentVariable(FixturePipeVariable);
        if (string.IsNullOrEmpty(scenario) || string.IsNullOrEmpty(pipe)) return false;
        var receiptPath = Environment.GetEnvironmentVariable(FixtureReceiptVariable) ??
                          throw new InvalidOperationException("The status fixture child has no receipt path.");
        var received = new List<HostStatusUpdate>();
        var cancelled = false;
        string? error = null;
        try
        {
            var client = new EngineHostClient(pipe,
                TimeSpan.FromMilliseconds(FixtureConnectionDeadlineMilliseconds));
            using var cancellation = new CancellationTokenSource();
            var enumerator = client.SubscribeStatusAsync(cancellation.Token).GetAsyncEnumerator();
            try
            {
                bool Next() => enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
                switch (scenario)
                {
                    case "DrainStatus":
                        using (var sent = EventWaitHandle.OpenExisting(pipe + ".Sent"))
                        {
                            while (Next())
                            {
                                received.Add(enumerator.Current);
                                sent.Set();
                            }
                        }
                        break;
                    case nameof(StatusScript.Stream):
                        while (received.Count < StreamedStatuses.Length)
                        {
                            Check.Require(Next(),
                                "the trusted subscription ended before every streamed status arrived");
                            received.Add(enumerator.Current);
                        }
                        break;
                    case nameof(StatusScript.Disconnect):
                        Check.Require(Next(), "the trusted subscription delivered no status before disconnect");
                        received.Add(enumerator.Current);
                        break;
                    case nameof(StatusScript.Cancel):
                        Check.Require(Next(), "the trusted subscription delivered no status before cancellation");
                        received.Add(enumerator.Current);
                        cancellation.Cancel();
                        try { Next(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        break;
                    default:
                        throw new InvalidOperationException("Unknown status fixture scenario.");
                }
            }
            finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (Exception caught) { error = caught.GetType().FullName; }
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new StatusReceipt(
            error is null ? ExitOutcome : ExceptionOutcome, received.Count, error, cancelled, received)));
        return true;
    }

    private static void WithFixture(string[] arguments, FixtureScript script, Action<FixtureRun> body,
        bool cancelAfterAccepted = false)
    {
        var pipeName = NewPipeName();
        var receiptPath = Path.Combine(Path.GetTempPath(),
            "sync-shark-transport-" + Guid.NewGuid().ToString("N") + ".json");
        var workDirectory = Directory.CreateTempSubdirectory("sync-shark-transport-child-").FullName;
        // The cancellation is causal: the fixture sets this only after it has read the request and
        // sent Accepted, so the child can never cancel before its request is sent.
        var acceptedName = cancelAfterAccepted
            ? "SyncShark.Engine.Tests.Accepted." + Guid.NewGuid().ToString("N")
            : null;
        using var accepted = acceptedName is null
            ? null
            : new EventWaitHandle(false, EventResetMode.ManualReset, acceptedName);
        using var quiet = new EventWaitHandle(false, EventResetMode.ManualReset, pipeName + ".Quiet");
        using var server = FixtureServer.Start(pipeName, script,
            script == FixtureScript.DelayedAccepted ? quiet : accepted);
        try
        {
            using var process = StartFixtureChild(pipeName, arguments, receiptPath, acceptedName,
                workDirectory);
            var childOutput = process.StandardOutput.ReadToEndAsync();
            var childError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(script == FixtureScript.DelayedAccepted
                ? FixtureChildTimeoutMilliseconds + (int)TimeSpan.FromSeconds(60).TotalMilliseconds
                : FixtureChildTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new InvalidOperationException(
                    "the transport fixture child did not finish one preference command in time");
            }
            var output = childOutput.GetAwaiter().GetResult();
            var errorOutput = childError.GetAwaiter().GetResult();
            Check.Require(File.Exists(receiptPath),
                "the transport fixture child wrote no receipt (standard output: " + output +
                "; standard error: " + errorOutput + ")");
            var receipt = JsonSerializer.Deserialize<FixtureReceipt>(File.ReadAllText(receiptPath)) ??
                          throw new InvalidOperationException("The transport fixture receipt is empty.");
            body(new FixtureRun(receipt, server.Completion.GetAwaiter().GetResult(), workDirectory));
        }
        finally
        {
            TryDeleteFile(receiptPath);
            TryDeleteDirectory(workDirectory);
        }
    }

    private static void IntakeStopClosesHungRequest() => WithIntakeFixture(hung: true, stop: false);

    private static void IntakeStopPreservesRunningCommand() => WithIntakeFixture(hung: false, stop: false);

    private static void StopCommandReportsSuccess() => WithIntakeFixture(hung: false, stop: true);

    private static void WithIntakeFixture(bool hung, bool stop)
    {
        var pipeName = NewPipeName();
        var work = Directory.CreateTempSubdirectory("sync-shark-intake-").FullName;
        var receiptPath = Path.Combine(work, "receipt.json");
        using var pipe = NewListener(pipeName);
        using var intake = new CancellationTokenSource();
        using var bound = new CancellationTokenSource(FixtureChildTimeoutMilliseconds);
        using var sent = new EventWaitHandle(false, EventResetMode.ManualReset, pipeName + ".Sent");
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken commandToken = default;
        var called = false;
        var server = new EngineHostServer(async (_, _, _, token) =>
        {
            called = true;
            commandToken = token;
            if (stop) intake.Cancel();
            else
            {
                running.SetResult();
                await release.Task.WaitAsync(bound.Token);
            }
            token.ThrowIfCancellationRequested();
            return (int)SyncCliExitCode.Success;
        });
        var accepting = pipe.WaitForConnectionAsync(bound.Token);
        using var child = StartFixtureChild(pipeName, ShowArguments, receiptPath, null, work,
            hung ? "HungRequest" : null);
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            Await(accepting);
            var serving = server.ServeAsync(pipe, intake.Token);
            if (hung)
            {
                Check.Require(sent.WaitOne(FixtureChildTimeoutMilliseconds), "the child sent a partial header");
                intake.Cancel();
            }
            else if (!stop)
            {
                Await(running.Task.WaitAsync(bound.Token));
                intake.Cancel();
                release.SetResult();
            }
            Await(serving.WaitAsync(bound.Token));
            Check.Require(child.WaitForExit(FixtureChildTimeoutMilliseconds), "the intake fixture child completed");
            Check.Require(File.Exists(receiptPath), "the intake child wrote its receipt: " + Await(output) + Await(error));
            var receipt = JsonSerializer.Deserialize<FixtureReceipt>(File.ReadAllText(receiptPath))!;
            Check.Require(receipt.Outcome == ExitOutcome && receipt.ExitCode ==
                (hung ? -1 : (int)SyncCliExitCode.Success),
                "intake stop closes an unread frame silently and preserves accepted Exit: " + Describe(receipt));
            Check.Require(hung ? !called : called && !commandToken.IsCancellationRequested,
                "intake cancellation never cancels an accepted command or executes a partial frame");
        }
        finally
        {
            release.TrySetResult();
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            TryDeleteDirectory(work);
        }
    }

    // The parent runs the real EngineHostServer; only the trusted child process runs the real client.
    private static void WithStatusFixture(StatusScript script, Action<StatusRun> body)
    {
        var pipeName = NewPipeName();
        var receiptPath = Path.Combine(Path.GetTempPath(),
            "sync-shark-status-" + Guid.NewGuid().ToString("N") + ".json");
        var workDirectory = Directory.CreateTempSubdirectory("sync-shark-status-child-").FullName;
        using var server = StatusServer.Start(pipeName, script);
        try
        {
            using var process = StartFixtureChild(pipeName, ShowArguments, receiptPath, null, workDirectory,
                script.ToString());
            var childOutput = process.StandardOutput.ReadToEndAsync();
            var childError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(FixtureChildTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new InvalidOperationException("the status fixture child did not finish in time");
            }
            var output = childOutput.GetAwaiter().GetResult();
            var errorOutput = childError.GetAwaiter().GetResult();
            Check.Require(File.Exists(receiptPath),
                "the status fixture child wrote no receipt (standard output: " + output +
                "; standard error: " + errorOutput + ")");
            var receipt = JsonSerializer.Deserialize<StatusReceipt>(File.ReadAllText(receiptPath)) ??
                          throw new InvalidOperationException("The status fixture receipt is empty.");
            Check.Require(server.Completion.Wait(TimeSpan.FromMilliseconds(FixtureChildTimeoutMilliseconds)),
                "the status server was cancelled and joined the sender after the subscription ended");
            body(new StatusRun(receipt, server.SourceDisposed));
        }
        finally
        {
            TryDeleteFile(receiptPath);
            TryDeleteDirectory(workDirectory);
        }
    }

    private static Process StartFixtureChild(string pipeName, string[] arguments, string receiptPath,
        string? cancelEventName, string workDirectory, string? statusScenario = null, string? autoSyncPipe = null)
    {
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("The test executable identity is absent.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDirectory,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            Check.Require(!string.IsNullOrEmpty(assembly), "the hosted test entry assembly identity is absent.");
            start.ArgumentList.Add(assembly!);
        }
        start.ArgumentList.Add(CheckArguments.Flag);
        start.ArgumentList.Add(CurrentGroupName());
        start.Environment[FixturePipeVariable] = pipeName;
        start.Environment[FixtureArgumentsVariable] = JsonSerializer.Serialize(arguments);
        start.Environment[FixtureReceiptVariable] = receiptPath;
        start.Environment[FixtureConnectionDeadlineVariable] =
            FixtureConnectionDeadlineMilliseconds.ToString(CultureInfo.InvariantCulture);
        if (cancelEventName is not null) start.Environment[FixtureCancelEventVariable] = cancelEventName;
        if (statusScenario is not null) start.Environment[FixtureStatusVariable] = statusScenario;
        if (autoSyncPipe is not null) start.Environment[FixtureAutoSyncVariable] = autoSyncPipe;
        return Process.Start(start) ??
               throw new InvalidOperationException("The transport fixture child did not start.");
    }

    // The first listener instance owns the pipe name; later accepted replacements do not. The counter is per
    // test, so every check gets its own first instance.
    private static Func<NamedPipeServerStream> ListenerFactory(string pipeName)
    {
        var instances = 0;
        return () => new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
            (++instances == 1 ? PipeOptions.FirstPipeInstance : PipeOptions.None));
    }

    // Starts one more trusted client on the same listener and returns its receipt. A timed-out child is killed
    // before the assertion fails, so a failing check leaves no process behind.
    private static FixtureReceipt ServeNextClient(string pipeName, string work, string receiptName, string what)
    {
        var receiptPath = Path.Combine(work, receiptName);
        using var next = StartFixtureChild(pipeName, ShowArguments, receiptPath, null, work);
        var nextOutput = next.StandardOutput.ReadToEndAsync();
        var nextErrors = next.StandardError.ReadToEndAsync();
        var served = next.WaitForExit(FixtureChildTimeoutMilliseconds);
        if (!served) next.Kill(entireProcessTree: true);
        Await(nextOutput);
        Await(nextErrors);
        Check.Require(served, what);
        return JsonSerializer.Deserialize<FixtureReceipt>(File.ReadAllText(receiptPath))!;
    }

    // The child must run this file's own registered group; the runner flag is the only source of that name.
    private static string CurrentGroupName()
    {
        var commandLine = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(commandLine, CheckArguments.Flag);
        Check.Require(index >= 0 && index + 1 < commandLine.Length,
            "the trusted-peer transport cases need the check runner's own group name to start a peer process");
        return commandLine[index + 1];
    }

    // The bounded latest-state subscription belongs to the host owner; this composition is the probe for it.
    private static readonly EngineSettings StatusHostSettings = new("{name} ({side} {time}){ext}",
        "yyyyMMddHHmmss", "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "",
        262144, ScanTestSettings.BatchRows)
    {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes,
        StoragePageSize = ScanTestSettings.StoragePageSize,
        Backoff = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120),
    };

    private static readonly HttpClient StatusHostHttp = new();

    // Core's test hook redirects the product root to a temporary root; Engine.Tests is not on Core's
    // InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;

    // A listener with one instance and no script; the busy and untrusted cases own their own connections.
    private static NamedPipeServerStream NewListener(string pipeName) => new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    private static string NewPipeName() => "SyncShark.Engine.Tests.Transport." + Guid.NewGuid().ToString("N");

    private static string PreferenceReply() => new UiPreferencesResult(SavedPreferences).RenderHuman();

    private static string Describe(FixtureReceipt receipt) =>
        receipt.Outcome + (receipt.ExitCode is null ? string.Empty : " " + receipt.ExitCode) +
        (receipt.Error is null ? string.Empty : " " + receipt.Error);

    private static int RetainedPreferenceCeiling()
    {
        var owner = typeof(UiPreferencesStore).Assembly.GetType("Massdex.SyncShark.DualSlotJsonStore`1");
        var field = owner?.GetField("MaximumBytes", BindingFlags.NonPublic | BindingFlags.Static);
        Check.Require(field is not null, "the retained preference document ceiling owner was not found");
        return (int)field!.GetRawConstantValue()!;
    }

    private static void Refused(byte[] frame, string what)
    {
        try
        {
            Await(EngineHostFrame.ReadAsync(new MemoryStream(frame), CancellationToken.None));
            Check.Require(false, "the shared frame codec accepted a malformed frame: " + what);
        }
        catch (InvalidDataException) { }
    }

    // The request's JSON shape is checked when the arguments are decoded, before the server executes them.
    private static void RefusedArguments(byte[] payload, string what)
    {
        try
        {
            var frame = Await(EngineHostFrame.ReadAsync(
                new MemoryStream(Raw((byte)EngineHostFrameKind.Arguments, (uint)payload.Length, payload)),
                CancellationToken.None)) ?? throw new InvalidDataException("the request frame is absent");
            frame.DecodeArguments();
            Check.Require(false, "the shared request frame accepted an invalid argument list: " + what);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException) { }
    }

    private static byte[] Raw(byte kind, uint length, byte[]? payload)
    {
        var bytes = new byte[FrameHeaderBytes + (payload?.Length ?? 0)];
        bytes[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(byte)), length);
        payload?.CopyTo(bytes, FrameHeaderBytes);
        return bytes;
    }

    private static EngineHostFrame RoundTrip(EngineHostFrame frame)
    {
        using var buffer = new MemoryStream();
        Await(EngineHostFrame.WriteAsync(buffer, frame, CancellationToken.None));
        buffer.Position = 0;
        return Await(EngineHostFrame.ReadAsync(buffer, CancellationToken.None)) ??
               throw new InvalidOperationException("The frame did not round trip.");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Await(Task task) => task.GetAwaiter().GetResult();

    private enum FixtureScript
    {
        ShowReply,
        DelayedAccepted,
        SetReply,
        AcceptedThenClose,
        ReplyThenClose,
        MalformedFrame,
        OutputBeforeAccepted,
        OversizedFrame,
        WaitForCancel,
    }

    private enum StatusScript
    {
        Stream,
        Disconnect,
        Cancel,
    }

    private sealed record StatusReceipt(string Outcome, int Received, string? Error, bool Cancelled,
        IReadOnlyList<HostStatusUpdate> Updates);

    private sealed record StatusRun(StatusReceipt Receipt, bool SourceDisposed);

    // The parent-side real server: it waits for the trusted child, then serves the scripted status source.
    private sealed class StatusServer : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StatusSource _source;

        private StatusServer(NamedPipeServerStream pipe, StatusScript script)
        {
            _pipe = pipe;
            _source = new StatusSource(script);
            Completion = Task.Run(ServeAsync);
        }

        public Task Completion { get; }

        public bool SourceDisposed => _source.Disposed;

        public static StatusServer Start(string pipeName, StatusScript script) => new(NewListener(pipeName), script);

        public void Dispose() => _pipe.Dispose();

        private async Task ServeAsync()
        {
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(FixtureChildTimeoutMilliseconds));
            await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            await new EngineHostServer(NoCommand, _source.Updates).ServeAsync(_pipe, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    // The server pulls one update at a time, so a slow client leaves at most the current write outstanding.
    private sealed class StatusSource
    {
        private readonly StatusScript _script;
        private volatile bool _disposed;

        public StatusSource(StatusScript script) => _script = script;

        public bool Disposed => _disposed;

        public async IAsyncEnumerable<HostStatusUpdate> Updates([EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                HostStatusUpdate[] statuses =
                    _script == StatusScript.Stream ? StreamedStatuses : new[] { StreamedStatuses[0] };
                foreach (var update in statuses) yield return update;
                // The source stays live until the client leaves, so a disconnect is observed as EOF, not a closed sender.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            finally { _disposed = true; }
        }
    }

    private static Task<int> NoCommand(string[] arguments, TextWriter stdout, TextWriter stderr,
        CancellationToken ct) => Task.FromResult(0);

    private sealed record FixtureReceipt(string Outcome, int? ExitCode, string Stdout, string Stderr,
        string? Error, bool Cancelled);

    private sealed record FixtureTrace(int Connections, IReadOnlyList<string[]> Requests, int OutputFrames,
        int LargestOutputPayload, string? Error);

    private sealed record FixtureRun(FixtureReceipt Receipt, FixtureTrace Trace, string WorkDirectory);

    // A scripted peer of the same executable image, so the shared trust rule sees a real trusted host.
    private sealed class FixtureServer : IDisposable
    {
        private static readonly TimeSpan ServingDeadline = TimeSpan.FromSeconds(30);
        private readonly NamedPipeServerStream _pipe;
        private readonly FixtureScript _script;
        private readonly EventWaitHandle? _accepted;
        private readonly List<string[]> _requests = [];
        private int _connections;
        private int _outputFrames;
        private int _largestOutputPayload;
        private string? _error;

        private FixtureServer(NamedPipeServerStream pipe, FixtureScript script, EventWaitHandle? accepted)
        {
            _pipe = pipe;
            _script = script;
            _accepted = accepted;
            Completion = Task.Run(ServeAsync);
        }

        public Task<FixtureTrace> Completion { get; }

        public static FixtureServer Start(string pipeName, FixtureScript script, EventWaitHandle? accepted = null) => new(
            new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance),
            script, accepted);

        public void Dispose() => _pipe.Dispose();

        private FixtureTrace Result =>
            new(_connections, _requests.ToArray(), _outputFrames, _largestOutputPayload, _error);

        private async Task<FixtureTrace> ServeAsync()
        {
            using var deadline = new CancellationTokenSource(_script == FixtureScript.DelayedAccepted
                ? ServingDeadline + TimeSpan.FromSeconds(60) : ServingDeadline);
            try
            {
                await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
                _connections++;
                var request = await EngineHostFrame.ReadAsync(_pipe, deadline.Token).ConfigureAwait(false);
                if (request is null)
                {
                    _error = "no request frame";
                    return Result;
                }
                _requests.Add(request.Value.DecodeArguments());
                switch (_script)
                {
                    case FixtureScript.DelayedAccepted:
                        // RQ-090 is 30 seconds; wait causally for its output, not a test sleep.
                        Check.Require(_accepted!.WaitOne(TimeSpan.FromSeconds(60)), "quiet output before Accepted");
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        await WriteAsync(EngineHostFrame.CreateExit((int)SyncCliExitCode.Success), deadline.Token)
                            .ConfigureAwait(false);
                        await DrainAsync(deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.ShowReply:
                    case FixtureScript.SetReply:
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        await WriteReplyAsync(deadline.Token).ConfigureAwait(false);
                        await WriteAsync(EngineHostFrame.CreateExit((int)SyncCliExitCode.Success),
                            deadline.Token).ConfigureAwait(false);
                        await DrainAsync(deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.AcceptedThenClose:
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.ReplyThenClose:
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.StandardOutput,
                            Encoding.UTF8.GetBytes(PreferenceReply())), deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.MalformedFrame:
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        await WriteRawAsync(UnknownFrameKind, 0, null, deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.OutputBeforeAccepted:
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.StandardOutput,
                            Encoding.UTF8.GetBytes(PreferenceReply())), deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.OversizedFrame:
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        await WriteRawAsync((byte)EngineHostFrameKind.StandardOutput,
                            (uint)AutoSyncHostProtocol.MaximumFrameBytes + 1u, null, deadline.Token)
                            .ConfigureAwait(false);
                        await DrainAsync(deadline.Token).ConfigureAwait(false);
                        break;
                    case FixtureScript.WaitForCancel:
                        // Cancellation is causal: the request is recorded above, Accepted is sent, and
                        // only then does the fixture release the child's cancel signal.
                        await WriteAsync(new EngineHostFrame(EngineHostFrameKind.Accepted,
                            ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
                        _accepted?.Set();
                        await DrainAsync(deadline.Token).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or
                                              InvalidDataException or ObjectDisposedException)
            {
                _error ??= error.GetType().Name;
            }
            finally
            {
                _pipe.Dispose();
            }
            return Result;
        }

        private Task WriteReplyAsync(CancellationToken ct) => WriteAsync(new EngineHostFrame(
            EngineHostFrameKind.StandardOutput, Encoding.UTF8.GetBytes(PreferenceReply())), ct);

        private async Task WriteAsync(EngineHostFrame frame, CancellationToken ct)
        {
            RecordOutput(frame);
            await EngineHostFrame.WriteAsync(_pipe, frame, ct).ConfigureAwait(false);
        }

        // The raw writer emits a deliberately malformed or over-bound header the codec must refuse.
        private async Task WriteRawAsync(byte kind, uint length, byte[]? payload, CancellationToken ct)
        {
            var header = new byte[FrameHeaderBytes];
            header[0] = kind;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(byte)), length);
            await _pipe.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload is not null) await _pipe.WriteAsync(payload, ct).ConfigureAwait(false);
            await _pipe.FlushAsync(ct).ConfigureAwait(false);
        }

        private void RecordOutput(EngineHostFrame frame)
        {
            if (frame.Kind is not (EngineHostFrameKind.StandardOutput or EngineHostFrameKind.StandardError)) return;
            _outputFrames++;
            _largestOutputPayload = Math.Max(_largestOutputPayload, frame.Payload.Length);
        }

        // Nothing else may arrive from the client: a further frame is a replay or a protocol violation.
        private async Task DrainAsync(CancellationToken ct)
        {
            while (await EngineHostFrame.ReadAsync(_pipe, ct).ConfigureAwait(false) is { } frame)
                _error ??= "unexpected extra client frame: " + frame.Kind;
        }
    }

    // A stream that holds only a frame header and reports any attempt to read beyond it.
    private sealed class HeaderOnlyStream(byte[] header) : Stream
    {
        private int _offset;

        public bool PayloadReadAttempted { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset >= header.Length)
            {
                PayloadReadAttempted = true;
                return 0;
            }
            var count = Math.Min(buffer.Length, header.Length - _offset);
            header.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
            if (buffer.Length == 0) return ValueTask.FromResult(0);
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class ProofEngine : IAutoSyncHostEngine
    {
        public Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { proof = "legacy-listener-served" });
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
