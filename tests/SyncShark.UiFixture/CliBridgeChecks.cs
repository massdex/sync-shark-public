using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Synthetic operations surface for the two-process fixture. All command semantics come from
// SyncCliOperationsBase, so this host exercises the same production dispatch the application uses.
internal sealed class FixtureCliOperations : SyncCliOperationsBase
{
    private readonly SyncAuthorityRuntimeHost _host;
    private readonly PilotStateAuthority _stateAuthority;
    private readonly string _root;

    internal FixtureCliOperations(SyncAuthorityRuntimeHost host, PilotStateAuthority stateAuthority, string root)
    {
        _host = host;
        _stateAuthority = stateAuthority;
        _root = root;
    }

    protected override SyncAuthorityRuntimeHost Host => _host;
    protected override PilotStateAuthority StateAuthority => _stateAuthority;
    protected override bool ApplicationOwnsRuntime => true;
    // The dispatcher's run-control answers come from the real host this owner holds, so a run begun in the
    // live owner is visible to 'run pause', 'run resume', 'jobs status' and the retained views.
    protected override bool HostRunActive => _host.HasActiveRun;
    protected override bool HostRunPaused => _host.ActiveRun?.Paused == true;
    public override string Mode => "running-application";

    protected override Task ShowApplicationCoreAsync(CancellationToken cancellationToken)
    {
        // The gate engages only when the lifecycle proof requests it, so ordinary app-show cases keep their
        // previous immediate behaviour.
        if (!File.Exists(Path.Combine(_root, "app-show-gate"))) return Task.CompletedTask;
        // Bounded fixture gate used only by the bridge lifecycle proof: signal that the accepted action is in
        // flight, block until the parent releases it, then record how many times it actually ran. This is not
        // window-show behaviour and proves nothing about the real UI.
        File.WriteAllText(Path.Combine(_root, "app-show-inflight"), "1");
        var release = Path.Combine(_root, "app-show-release");
        var gateDeadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(release) && DateTime.UtcNow < gateDeadline) Thread.Sleep(50);
        var count = Path.Combine(_root, "app-show-count");
        var previous = File.Exists(count) && int.TryParse(File.ReadAllText(count).Trim(), out var parsed)
            ? parsed
            : 0;
        File.WriteAllText(count, (previous + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Delete(Path.Combine(_root, "app-show-inflight"));
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override async Task ExitApplicationCoreAsync(CancellationToken cancellationToken)
    {
        // Only the checkpoint is substituted: the real bridge must deliver its result before the
        // process stops, and the real client must observe that exit rather than time out.
        File.AppendAllText(Path.Combine(_root, "exit-accepted"), "accepted\n");
        var limit = DateTime.UtcNow.AddMinutes(4);
        while (File.Exists(Path.Combine(_root, "exit-block")) &&
               !File.Exists(Path.Combine(_root, "exit-release")))
        {
            if (DateTime.UtcNow >= limit) throw new TimeoutException("Fixture checkpoint remained blocked.");
            await Task.Delay(50, cancellationToken);
        }
        var delayFile = Path.Combine(_root, "exit-delay-ms");
        var delay = File.Exists(delayFile) ? int.Parse(File.ReadAllText(delayFile)) : 250;
        if (delay is < 0 or > 20000) throw new InvalidDataException("Fixture exit delay is out of bounds.");
        await Task.Delay(delay, cancellationToken);
        File.WriteAllText(Path.Combine(_root, "exit-checkpoint"), "complete");
    }
}

// Real two-process proof of the running-application bridge: one process hosts the bridge over isolated
// synthetic authority state, the other drives it as a client. No production state, provider, credential
// or installed runtime is touched.
internal static class CliBridgeChecks
{
    internal static int RunExitDispositionChecks(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh exit fixture root.");
        Directory.CreateDirectory(root);
        foreach (var scenario in new[] { "slow", "cancel-wait", "pending" })
        {
            var hostRoot = Directory.CreateDirectory(Path.Combine(root, scenario)).FullName;
            if (scenario == "slow") File.WriteAllText(Path.Combine(hostRoot, "exit-delay-ms"), "17000");
            else File.WriteAllText(Path.Combine(hostRoot, "exit-block"), "block");
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SyncShark.UiFixture.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--cli-bridge-host"); start.ArgumentList.Add(hostRoot); start.ArgumentList.Add("initial");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("No isolated exit host.");
            try
            {
                var readyUntil = DateTime.UtcNow.AddSeconds(40);
                while (FindBridgeWindowForProcess(child.Id) == IntPtr.Zero && !child.HasExited && DateTime.UtcNow < readyUntil)
                    Thread.Sleep(25);
                if (child.HasExited || FindBridgeWindowForProcess(child.Id) == IntPtr.Zero)
                    throw new InvalidDataException("Exit fixture owner did not expose its own receiver.");
                using var client = new SyncCliBridgeClient();
                using var cancellation = new CancellationTokenSource();
                var diagnostic = new StringWriter();
                var elapsed = Stopwatch.StartNew();
                var invoke = Task.Run(() =>
                {
                    var ok = client.TryInvoke(["--cli", "app", "exit", "--yes", "--json"], diagnostic,
                        TimeSpan.FromSeconds(15), cancellation.Token, out var code, out var text, out _);
                    return (ok, code, text);
                });
                var acceptedUntil = DateTime.UtcNow.AddSeconds(30);
                while (!File.Exists(Path.Combine(hostRoot, "exit-accepted")) && !invoke.IsCompleted && DateTime.UtcNow < acceptedUntil)
                    Thread.Sleep(25);
                if (!File.Exists(Path.Combine(hostRoot, "exit-accepted")))
                    throw new InvalidDataException("The exact fixture owner did not receive the exit request.");
                if (scenario == "cancel-wait") cancellation.Cancel();
                if (scenario == "slow")
                {
                    var result = invoke.GetAwaiter().GetResult();
                    if (!result.ok || result.code != 0 || !child.HasExited || elapsed.Elapsed < TimeSpan.FromSeconds(17) ||
                        !diagnostic.ToString().Contains("Exit accepted"))
                        throw new InvalidDataException("Slow exit did not return truthful completed process state.");
                }
                else
                {
                    var matched = false;
                    try { invoke.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (scenario == "cancel-wait") { matched = true; }
                    catch (SyncCliBusyException error) when (scenario == "pending")
                    { matched = error.Message.Contains("still pending") && error.Message.Contains("Request:"); }
                    if (!matched || child.HasExited || File.Exists(Path.Combine(hostRoot, "exit-checkpoint")))
                        throw new InvalidDataException("Cancellation/non-exit was confused with completed or failed shutdown.");
                    File.WriteAllText(Path.Combine(hostRoot, "exit-release"), "release original request");
                    if (!child.WaitForExit(15000)) throw new TimeoutException("The original request did not finish after its checkpoint released.");
                }
                if (File.ReadAllLines(Path.Combine(hostRoot, "exit-accepted")).Length != 1 ||
                    File.ReadAllText(Path.Combine(hostRoot, "exit-checkpoint")) != "complete")
                    throw new InvalidDataException("The original request was replayed or its checkpoint was lost.");
                Console.WriteLine("PASS real two-process exit " + scenario + ": one accepted request, truthful disposition, eventual graceful exit; " + elapsed.Elapsed);
            }
            finally
            {
                File.WriteAllText(Path.Combine(hostRoot, "exit-release"), "release fixture checkpoint");
                if (!child.HasExited)
                {
                    File.WriteAllText(Path.Combine(hostRoot, "stop"), "fixture teardown");
                    if (!child.WaitForExit(15000)) throw new TimeoutException("Fixture owner did not stop; no force termination attempted.");
                }
            }
        }
        return 0;
    }

    internal static int RunWindowHost(string root, bool googleAccounts = false)
    {
        // Real App/MainWindow/CLI adapter, but isolated state and suppressed network-loaded startup.
        // No installed state, credential or provider is used; the owner exercises the visible window.
        Directory.CreateDirectory(root);
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
            SuppressProductStartup = true };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        if (googleAccounts)
        {
            // Retained account metadata only: no credentials, consent or network requests.
            // The actual MainWindow handles account switching and disconnected/error states.
            var accounts = new GoogleDriveConnectionStore(instance.StateAuthority);
            foreach (var name in new[] { "primary", "secondary" })
            {
                var permissionId = "ui-fixture-" + name;
                var binding = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
                    GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId,
                    name + "@example.invalid", "Google " + name, [GoogleDriveProviderConfiguration.DriveScope]);
                Task.Run(() => accounts.SaveAsync(binding)).GetAwaiter().GetResult();
            }
        }
        var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        var window = new MainWindow(instance.StateAuthority, theme);
        void Set(string field, object value) => typeof(App).GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, value);
        Set("_singleInstance", instance);
        Set("_themeManager", theme);
        Set("_mainWindow", window);
        typeof(App).GetMethod("StartCliBridge", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(app, null);
        return app.Run(window);
    }

    internal static int RunWindowClient(string[] arguments) => CliEntry.Execute(arguments);

    internal static int RunTwoProcess(string root)
    {
        Directory.CreateDirectory(root);
        var hostRoot = Path.Combine(root, "host");
        // UI5: the owner records every presentation-hook invocation, so the parent can prove which commands
        // reach that hook through the real bridge instead of inferring it from a command label.
        var refreshLog = Path.Combine(hostRoot, "presentation-refresh-log.txt");
        int PresentationRefreshCount() => File.Exists(refreshLog) ? File.ReadAllLines(refreshLog).Length : 0;
        // Launch the fixture apphost from this assembly's own output directory. When the parent was
        // started through 'dotnet <dll>', Environment.ProcessPath is dotnet.exe and cannot be re-used to
        // start a second fixture process with application arguments.
        var appHost = Path.Combine(AppContext.BaseDirectory, "SyncShark.UiFixture.exe");
        var executable = File.Exists(appHost)
            ? appHost
            : Environment.ProcessPath ?? throw new InvalidOperationException(
                "The fixture executable path is unavailable.");
        using var child = Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = "--cli-bridge-host \"" + hostRoot + "\" initial",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The fixture host process could not be started.");
        var checks = 0;
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("CLI bridge check failed: " + label);
            checks++;
            Console.WriteLine("PASS " + label);
        }
        // Retained-view state captured inside the first owner's scope so the second owner's restart proof
        // can compare exact parsed content rather than row counts.
        string? runsBeforeData = null;
        var queuedSeeded = false;
        var queuedSeedDetail = string.Empty;
        string? queueBeforeData = null;
        string? queueReturnedBefore = null;
        RetainedQueueIdentity? queueIdentityBefore = null;
        SyncAuthorityOperationSnapshot? operationBefore = null;
        var retainedAuthority = new SyncAuthorityDatabase(
            Path.Combine(hostRoot, "vNext", "state", "syncshark.db"));
        try
        {
            // R477-2: the unchanged production client must address THIS isolated owner. Wait for the child's
            // own receiver window, identified by its exact process id, before the production probe runs; a
            // window that merely shares the product title belongs to another process and is never a candidate.
            var ownReceiver = IntPtr.Zero;
            var ownReceiverDeadline = DateTime.UtcNow.AddSeconds(30);
            while (ownReceiver == IntPtr.Zero && DateTime.UtcNow < ownReceiverDeadline)
            {
                if (child.HasExited)
                    throw new InvalidOperationException("The fixture host exited early with code " +
                        child.ExitCode + HostDiagnostic(hostRoot));
                ownReceiver = FindBridgeWindowForProcess(child.Id);
                if (ownReceiver == IntPtr.Zero) Thread.Sleep(50);
            }
            Require(ownReceiver != IntPtr.Zero,
                "the isolated owner published its own bridge window addressed by its exact process id");
            // The host creates its receiver window asynchronously; retry until it answers or times out.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var connected = false;
            while (DateTime.UtcNow < deadline && !connected)
            {
                using var probe = new SyncCliBridgeClient();
                if (probe.TryInvoke(["--cli", "status", "--json"], TextWriter.Null, TimeSpan.FromSeconds(30),
                        out var probeExit, out var probeText, out _) && probeExit == 0 &&
                    StatusMode(probeText) == "running-application")
                    connected = true;
                if (child.HasExited)
                    throw new InvalidOperationException("The fixture host exited early with code " +
                        child.ExitCode + HostDiagnostic(hostRoot));
            }
            Require(connected, "a second process executed status inside the running host and saw the running-application mode");

            using (var client = new SyncCliBridgeClient())
            {
                var ok = client.TryInvoke(["--cli", "pairs", "list", "--json"], TextWriter.Null,
                    TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                Require(ok && exit == 0 && text.Contains("\"ok\":true", StringComparison.Ordinal),
                    "the bridge returned the real result of a read command");
            }

            // UI5: every read-only State command executes and returns without invoking the presentation hook.
            foreach (var readOnly in new[]
                     {
                         new[] { "--cli", "status", "--json" },
                         new[] { "--cli", "jobs", "status", "--json" },
                         new[] { "--cli", "activity", "list", "--json" },
                         new[] { "--cli", "runs", "list", "--json" },
                         new[] { "--cli", "queue", "list", "--json" },
                         new[] { "--cli", "pairs", "list", "--json" },
                         new[] { "--cli", "settings", "show", "--json" },
                         new[] { "--cli", "help", "--json" },
                         new[] { "--cli", "version", "--json" },
                         new[] { "--cli", "capabilities", "--json" }
                     })
            {
                var readOnlyResult = Invoke(readOnly);
                Require(readOnlyResult.ok && PresentationRefreshCount() == 0,
                    "the read-only command '" + string.Join(' ', readOnly.Skip(1)) +
                    "' returned exit " + readOnlyResult.exit + " without invoking the presentation hook");
            }
            // UI5: a genuine mutation still invokes the hook, and a hook exception never turns the committed
            // command into a failed or retryable CLI effect.
            File.WriteAllText(Path.Combine(hostRoot, "presentation-fails"), "fail");
            var refreshFailure = Invoke(["--cli", "run", "pause", "--yes", "--json"]);
            Require(refreshFailure.ok && refreshFailure.exit == 0 && PresentationRefreshCount() == 1,
                "a genuine mutation invoked the presentation hook even though the hook threw: count " +
                PresentationRefreshCount() + " exit " + refreshFailure.exit);
            File.Delete(Path.Combine(hostRoot, "presentation-fails"));

            // Concurrent clients must both be served without disturbing each other.
            var first = Task.Run(() => Invoke(["--cli", "queue", "list", "--json"]));
            var second = Task.Run(() => Invoke(["--cli", "attention", "list", "--json"]));
            Task.WaitAll(first, second);
            Require(first.Result.ok && second.Result.ok && first.Result.text != second.Result.text,
                "two concurrent clients received their own correlated results");

            using (var client = new SyncCliBridgeClient())
            {
                var ok = client.TryInvoke(["--cli", "cancel", "--key", "no-such-run", "--yes", "--json"],
                    TextWriter.Null, TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                Require(ok && exit == 4 && text.Contains("missing_target", StringComparison.Ordinal),
                    "a mutating command executed inside the running host returned the real exit code");
            }

            using (var client = new SyncCliBridgeClient())
            {
                var ok = client.TryInvoke(["--cli", "app", "show", "--yes", "--json"], TextWriter.Null,
                    TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                Require(ok && exit == 0 && text.Contains("app show", StringComparison.Ordinal),
                    "app show ran as a real action inside the running owner");
            }

            // A stale/hostile request vector is refused by the production dispatcher inside the host.
            using (var client = new SyncCliBridgeClient())
            {
                var ok = client.TryInvoke(["--cli", "settings", "set", "--yes", "--json", "--settings", "{\"unknown\":1}"],
                    TextWriter.Null, TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                Require(ok && exit == 2 && text.Contains("usage", StringComparison.Ordinal),
                    "an invalid mutation was rejected inside the running host with the usage exit code");
            }
            // Hostile and lifecycle cases through the real internal transport. The production validation
            // surface stays internal; this fixture is a friend assembly for exactly this purpose.
            using (var raw = new RawBridgeClient(ownReceiver, child.Id))
            {
                raw.Send(raw.Request(Guid.NewGuid().ToString("N"), "not json at all"));
                Require(raw.Drain(2000).Count == 0, "a malformed payload is dropped with no reply");
                raw.Send(new string('x', 200_000));
                Require(raw.Drain(2000).Count == 0, "an oversize payload is dropped with no reply");
                var replay = raw.Request(Guid.NewGuid().ToString("N"), null);
                raw.Send(replay);
                var firstReplies = raw.Drain(4000);
                raw.Send(replay);
                var replayReplies = raw.Drain(3000);
                // A refusal may legitimately be delivered as a rejection result; what must hold is that the
                // duplicate is refused by protocol, never that the peer stays silent.
                Require(firstReplies.Count > 0 &&
                        replayReplies.Any(reply => reply.Contains("duplicate", StringComparison.Ordinal)),
                    "a replayed request correlation is refused as duplicate so the effect cannot repeat");
                raw.Send(raw.Request(Guid.NewGuid().ToString("N"), null), unauthenticatedSender: true);
                Require(raw.Drain(2000).Count == 0,
                    "a request whose sender window is not this product process is refused");
            }
            // Rejected traffic must not have damaged the live owner: a valid request still runs.
            using (var afterRejects = new SyncCliBridgeClient())
            {
                var ok = afterRejects.TryInvoke(["--cli", "status", "--json"], TextWriter.Null,
                    TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                Require(ok && exit == 0 && StatusMode(text) == "running-application",
                    "the live owner still serves a valid request after the rejected traffic");
            }

            // A successful provider-free mutation through the real bridge, read back from that owner's own
            // retained state, with the replayed request refused before it can run a second time.
            var accountId = File.ReadAllText(Path.Combine(hostRoot, "connection-id.txt")).Trim();
            var pauseArgs = new[] { "--cli", "pause", "--account", accountId, "--minutes", "30", "--yes", "--json" };
            var refreshesBeforePause = PresentationRefreshCount();
            using (var client = new SyncCliBridgeClient())
            {
                var mutated = client.TryInvoke(pauseArgs, TextWriter.Null, TimeSpan.FromSeconds(30), out var exit,
                    out var text, out _);
                Require(mutated && exit == 0 && text.Contains("\"paused\":true", StringComparison.Ordinal) &&
                        PresentationRefreshCount() == refreshesBeforePause + 1,
                    "a supported mutation through the bridge changed isolated owner state and invoked the " +
                    "presentation hook exactly once");
            }
            using (var client = new SyncCliBridgeClient())
            {
                var readBack = client.TryInvoke(["--cli", "accounts", "list", "--json"], TextWriter.Null,
                    TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                Require(readBack && exit == 0 && text.Contains("\"isPaused\":true", StringComparison.Ordinal),
                    "the owner read the mutation back from its own retained state");
            }
            using (var raw = new RawBridgeClient(ownReceiver, child.Id))
            {
                var replay = raw.RequestFor(Guid.NewGuid().ToString("N"), pauseArgs);
                raw.Send(replay);
                Require(raw.Drain(4000).Count > 0, "the mutation executed through the raw transport");
                raw.Send(replay);
                Require(raw.Drain(3000).Any(reply => reply.Contains("duplicate", StringComparison.Ordinal)),
                    "the replayed mutation was refused as duplicate before it could run again");
            }
            using (var client = new SyncCliBridgeClient())
            {
                var resumed = client.TryInvoke(["--cli", "resume", "--account", accountId, "--yes", "--json"],
                    TextWriter.Null, TimeSpan.FromSeconds(30), out var exit, out _, out _);
                Require(resumed && exit == 0, "the fixture returned the account to its unpaused state");
            }

            // Bridge lifecycle proof (NOT a window-show proof): the owner's action is held in flight by a
            // bounded fixture gate, the client's wait is cancelled, and the accepted job is neither cancelled
            // nor replayed - proven with the ORIGINAL correlation value.
            string[] showArgs = ["--cli", "app", "show", "--yes", "--json"];
            var cancelClient = new SyncCliBridgeClient();
            var cancelledNonce = cancelClient.Nonce;
            try
            {
                File.WriteAllText(Path.Combine(hostRoot, "app-show-gate"), "1");
                File.Delete(Path.Combine(hostRoot, "app-show-count"));
                using (var cts = new CancellationTokenSource())
                {
                    var started = DateTime.UtcNow;
                    var waiting = Task.Run(() => cancelClient.TryInvoke(showArgs, TextWriter.Null,
                        TimeSpan.FromSeconds(60), cts.Token, out _, out _, out _));
                    var inFlight = Path.Combine(hostRoot, "app-show-inflight");
                    var proofDeadline = DateTime.UtcNow.AddSeconds(15);
                    while (!File.Exists(inFlight) && DateTime.UtcNow < proofDeadline) Thread.Sleep(50);
                    Require(File.Exists(inFlight), "the owner signalled the accepted action as in flight");
                    var clientWindow = (IntPtr)(typeof(SyncCliBridgeClient).GetField("_window",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(cancelClient) ?? IntPtr.Zero);
                    // The forged traffic is addressed to the isolated owner's own exact window, never to a
                    // title-only lookup that could reach an unrelated installed build.
                    var serverWindow = ownReceiver;
                    var forgedReply = SyncCliBridgeProtocol.EncodeReply(new SyncCliBridgeReply("result",
                        cancelledNonce, true, 0, "forged-success", null));
                    SyncCliBridgeTransport.TrySendCopyData(clientWindow, serverWindow, forgedReply, 2_000, out _);
                    Require(!waiting.IsCompleted,
                        "a forged result with the correct nonce and alleged server HWND is not accepted");
                    var forgedRequest = SyncCliBridgeProtocol.EncodeRequest(new SyncCliBridgeRequest("invoke",
                        cancelledNonce, DateTimeOffset.UtcNow, pauseArgs, clientWindow.ToInt64().ToString("x")));
                    SyncCliBridgeTransport.TrySendCopyData(serverWindow, clientWindow, forgedRequest, 2_000, out _);
                    Thread.Sleep(200);
                    using (var readBack = new SyncCliBridgeClient())
                    {
                        var read = readBack.TryInvoke(["--cli", "accounts", "list", "--json"], TextWriter.Null,
                            TimeSpan.FromSeconds(10), out var readExit, out var readText, out _);
                        Require(read && readExit == 0 && readText.Contains("\"isPaused\":false", StringComparison.Ordinal)
                                && !waiting.IsCompleted,
                            "borrowing a genuine client HWND cannot inject another command or fail its current request");
                    }
                    cts.Cancel();
                    var cancelled = false;
                    try { waiting.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled && DateTime.UtcNow - started < TimeSpan.FromSeconds(15),
                        "the token-taking client returned by cancellation, not by waiting out the timeout");
                    File.WriteAllText(Path.Combine(hostRoot, "app-show-release"), "release");
                }
                var countFile = Path.Combine(hostRoot, "app-show-count");
                var completed = false;
                for (var attempt = 0; attempt < 60 && !completed; attempt++)
                {
                    Thread.Sleep(100);
                    if (File.Exists(countFile) && File.ReadAllText(countFile).Trim() == "1") completed = true;
                }
                Require(completed, "the released owner action completed exactly once");
                using (var client = new SyncCliBridgeClient())
                {
                    var ok = client.TryInvoke(["--cli", "status", "--json"], TextWriter.Null,
                        TimeSpan.FromSeconds(30), out var exit, out var text, out _);
                    Require(ok && exit == 0 && StatusMode(text) == "running-application",
                        "the same host stayed available after the cancelled wait");
                }
                using (var raw = new RawBridgeClient(ownReceiver, child.Id))
                {
                    var replay = raw.RequestFor(cancelledNonce, showArgs);
                    raw.Send(replay);
                    Require(raw.Drain(3000).Any(reply => reply.Contains("duplicate", StringComparison.Ordinal)) &&
                            File.ReadAllText(countFile).Trim() == "1",
                        "replaying the original correlation did not execute the action a second time");
                }
            }
            finally
            {
                cancelClient.Dispose();
            }

            // Dispatcher-level run control against a REAL active run inside the live owner, plus a real
            // retained queued operation for the process-boundary proof. The run is synthetic; the pause,
            // resume, cancel and job answers all come from the production dispatcher and the owner's own run
            // control. The pair is authored through the production path and its pending effect is never
            // dispatched.
            var leftDir = Path.Combine(hostRoot, "fixture-left");
            var rightDir = Path.Combine(hostRoot, "fixture-right");
            Directory.CreateDirectory(leftDir);
            Directory.CreateDirectory(rightDir);
            var endpointDocument = "{\"left\":{\"provider\":\"local\",\"path\":" +
                System.Text.Json.JsonSerializer.Serialize(leftDir) + "},\"right\":{\"provider\":\"local\",\"path\":" +
                System.Text.Json.JsonSerializer.Serialize(rightDir) + "},\"direction\":\"TwoWay\"}";
            var authored = Invoke(["--cli", "pairs", "create", "--settings", endpointDocument, "--yes", "--json"]);
            Require(authored.ok && authored.exit == 0,
                "the owner authored one real provider-free fixture pair for the retention proof: " + authored.text);

            File.WriteAllText(Path.Combine(hostRoot, "seed-queued-operation"), "1");
            var seedDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(hostRoot, "queued-operation-seeded")) &&
                   !File.Exists(Path.Combine(hostRoot, "queued-operation-error")) && DateTime.UtcNow < seedDeadline)
                Thread.Sleep(50);
            // A refused seed is recorded as an exact UNEXECUTED gap and the rest of this route still runs,
            // so the dispatcher run-control evidence is never hidden behind one unresolved fixture API.
            queuedSeeded = !File.Exists(Path.Combine(hostRoot, "queued-operation-error")) &&
                            File.Exists(Path.Combine(hostRoot, "queued-operation-seeded"));
            queuedSeedDetail = File.Exists(Path.Combine(hostRoot, "queued-operation-error"))
                ? File.ReadAllText(Path.Combine(hostRoot, "queued-operation-error"))
                : File.ReadAllText(Path.Combine(hostRoot, "queued-operation-seeded"));
            Console.WriteLine((queuedSeeded ? "EVIDENCE " : "UNEXECUTED ") +
                "the retained queued operation before the boundary: " + queuedSeedDetail);

            var activeRunId = "command-8b1d7c40e29f4a5d9c3e6b7a10f2d4e8";
            File.WriteAllText(Path.Combine(hostRoot, "start-active-run"), activeRunId);
            var runDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(hostRoot, "active-run-id")) && DateTime.UtcNow < runDeadline)
                Thread.Sleep(50);
            Require(File.Exists(Path.Combine(hostRoot, "active-run-id")) &&
                    File.ReadAllText(Path.Combine(hostRoot, "active-run-id")).Trim() == activeRunId,
                "the live owner began one exact synthetic active run");

            var pause = Invoke(["--cli", "run", "pause", "--yes", "--json"]);
            var pausedJob = Invoke(["--cli", "jobs", "status", "--json"]);
            Require(pause.ok && pause.exit == 0 && DataBool(pause.text, "accepted") &&
                    DataBool(pausedJob.text, "active") && DataPath(pausedJob.text, "job.id") == activeRunId &&
                    DataPath(pausedJob.text, "job.paused") == "True",
                "run pause reached the owner's real active run and its own job reports that same id paused: " +
                pausedJob.text);

            var resume = Invoke(["--cli", "run", "resume", "--yes", "--json"]);
            var resumedJob = Invoke(["--cli", "jobs", "status", "--json"]);
            Require(resume.ok && resume.exit == 0 && DataBool(resume.text, "accepted") &&
                    DataPath(resumedJob.text, "job.id") == activeRunId &&
                    DataPath(resumedJob.text, "job.paused") == "False",
                "run resume reached the same run and its own job reports it running again: " + resumedJob.text);

            var refreshesBeforeFailedMutation = PresentationRefreshCount();
            var wrongTarget = Invoke(["--cli", "cancel", "--key",
                "command-00000000000000000000000000000000", "--yes", "--json"]);
            Require(wrongTarget.ok && wrongTarget.exit == 4 &&
                    ErrorMember(wrongTarget.text, "code") == "missing_target" &&
                    PresentationRefreshCount() == refreshesBeforeFailedMutation + 1,
                "a partially failed mutation (missing_target, exit 4) still invoked the presentation hook once: " +
                PresentationRefreshCount() + " vs " + refreshesBeforeFailedMutation);
            var afterWrong = Invoke(["--cli", "jobs", "status", "--json"]);
            Require(DataBool(afterWrong.text, "active") && DataPath(afterWrong.text, "job.id") == activeRunId &&
                    PresentationRefreshCount() == refreshesBeforeFailedMutation + 1 &&
                    !File.Exists(Path.Combine(hostRoot, "stop-requested")),
                "cancel refusing a different id left the same exact run active and untouched after a read-only " +
                "jobs status call added no refresh: " + afterWrong.text);

            var cancel = Invoke(["--cli", "cancel", "--key", activeRunId, "--yes", "--json"]);
            var endedDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(Path.Combine(hostRoot, "active-run-ended")) && DateTime.UtcNow < endedDeadline)
                Thread.Sleep(50);
            var endedJob = Invoke(["--cli", "jobs", "status", "--json"]);
            Require(cancel.ok && cancel.exit == 0 && DataBool(cancel.text, "accepted") &&
                    File.ReadAllText(Path.Combine(hostRoot, "stop-requested")).Trim() == "1" &&
                    File.Exists(Path.Combine(hostRoot, "active-run-ended")) && !DataBool(endedJob.text, "active"),
                "cancel by the exact active run id stopped that run, reached its checkpoint and cleared the job");

            var idlePause = Invoke(["--cli", "run", "pause", "--yes", "--json"]);
            Require(idlePause.ok && idlePause.exit == 0 && !DataBool(idlePause.text, "accepted"),
                "run pause stops claiming acceptance once that run has ended");

            var runsBefore = Invoke(["--cli", "runs", "list", "--json"]);
            var queueBefore = Invoke(["--cli", "queue", "list", "--json"]);
            runsBeforeData = DataRaw(runsBefore.text);
            queueBeforeData = DataRaw(queueBefore.text);
            queueReturnedBefore = DataMember(queueBefore.text, "returned");
            Require(runsBefore.ok && runsBefore.exit == 0 && DataRaw(runsBefore.text) is { } runsData &&
                    File.Exists(Path.Combine(hostRoot, "retained-run-id")) &&
                    File.ReadAllText(Path.Combine(hostRoot, "retained-run-id")).Trim() is { Length: > 0 } retainedRunId &&
                    runsData.Contains(retainedRunId, StringComparison.Ordinal),
                "runs list publishes the owner's retained run row before the process boundary");
            if (queuedSeeded)
            {
                queueIdentityBefore = ReadRetainedQueueIdentity(queueBefore.text);
                var seededPairId = queuedSeedDetail.Split('|').ElementAtOrDefault(1);
                Require(queueBefore.ok && queueBefore.exit == 0 && queueIdentityBefore is { } item &&
                        item.OperationId == RetainedOperationId && item.PairId == seededPairId &&
                        !string.IsNullOrWhiteSpace(item.PairId) && item.RelativePath == "<operation>" &&
                        item.Operation == "reconcile" && item.State == "Queued" &&
                        item.ItemKind == "Folder" && item.FileType == "Folder" && item.Priority == 1000,
                    "queue list publishes exactly one structurally verified retained operation before restart: " +
                    System.Text.Json.JsonSerializer.Serialize(queueIdentityBefore));
                operationBefore = retainedAuthority.LoadOperationAsync(RetainedOperationId).GetAwaiter().GetResult();
                Require(operationBefore.OperationId == RetainedOperationId &&
                        operationBefore.PairId == seededPairId && operationBefore.ObjectId == RetainedObjectId &&
                        operationBefore.Kind == SyncAuthorityOperationKind.Reconcile &&
                        operationBefore.Status == SyncAuthorityOperationStatus.Ready &&
                        operationBefore.IdempotencyKey == RetainedOperationKey &&
                        operationBefore.Members.Count == 1 &&
                        operationBefore.Members[0] is { } member && member.MemberId == RetainedMemberId &&
                        member.PairId == seededPairId && member.ObjectId == RetainedObjectId &&
                        member.Role == "lock" && !string.IsNullOrWhiteSpace(member.RootBindingId),
                    "the authority exposes the exact ready operation, object and member before restart: " +
                    System.Text.Json.JsonSerializer.Serialize(operationBefore));
            }

            using (var exitClient = new SyncCliBridgeClient())
            {
                var exited = exitClient.TryInvoke(["--cli", "app", "exit", "--yes", "--json"], TextWriter.Null,
                    TimeSpan.FromSeconds(15), out var exitCode, out var exitText, out _);
                Require(exited && exitCode == 0 && child.HasExited &&
                    File.ReadAllText(Path.Combine(hostRoot, "exit-checkpoint")) == "complete" &&
                    exitText.Contains("checkpoint completed", StringComparison.Ordinal),
                    "exit returns its completed checkpoint result and observes graceful owner termination");
            }

        }
        finally
        {
            // T6: stop this fixture's own child with the normal stop signal it already honours, never by
            // terminating it. A child that ignores the signal is reported for root, not killed here.
            if (!child.HasExited)
            {
                Directory.CreateDirectory(hostRoot);
                File.WriteAllText(Path.Combine(hostRoot, "stop"), "parent-stop");
                child.WaitForExit(15_000);
                if (!child.HasExited)
                    Console.WriteLine("EVIDENCE the isolated fixture host process " + child.Id +
                        " did not stop within 15s of its normal stop signal; it is left running for root " +
                        "and was never terminated by this fixture");
            }
        }

#if false // superseded post-teardown cancellation block; the live-host bridge lifecycle proof is inside the try above
            // Wait-cancellation proof: the client stops waiting promptly, the owner keeps the accepted job and
            // applies its effect exactly once, the same host stays available, and the correlation is never
            // replayed. Acceptance is established by the observed effect below, not by a sleep guess.
            var cancelAccountId = File.ReadAllText(Path.Combine(hostRoot, "connection-id.txt")).Trim();
            string[] cancelArgs = { "--cli", "pause", "--account", cancelAccountId, "--minutes", "45", "--yes",
                "--json" };
            string[] accountArgs = { "--cli", "accounts", "list", "--json" };
            using (var cts = new CancellationTokenSource())
            using (var client = new SyncCliBridgeClient())
            {
                var started = DateTime.UtcNow;
                var waiting = Task.Run(() => client.TryInvoke(cancelArgs, TextWriter.Null, TimeSpan.FromSeconds(60),
                    cts.Token, out _, out _, out _));
                Thread.Sleep(20);
                cts.Cancel();
                var cancelledPromptly = false;
                var completedBeforeCancel = false;
                try { completedBeforeCancel = waiting.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelledPromptly = true; }
                // A provider-free command can finish before the token is observed, so the honest invariant is
                // that the client returned promptly - by cancellation or by completing - never by waiting out
                // the full timeout, and that the owner's job was neither cancelled nor replayed (checked below).
                Require((cancelledPromptly || completedBeforeCancel) &&
                        DateTime.UtcNow - started < TimeSpan.FromSeconds(15),
                    "a cancelled wait returned promptly (cancelled=" + cancelledPromptly + ", completed=" +
                    completedBeforeCancel + ")");
            }
            var applied = false;
            for (var attempt = 0; attempt < 20 && !applied; attempt++)
            {
                Thread.Sleep(250);
                using var client = new SyncCliBridgeClient();
                if (client.TryInvoke(accountArgs, TextWriter.Null, TimeSpan.FromSeconds(30), out var exit,
                        out var text, out _) && exit == 0 &&
                    text.Contains("\"isPaused\":true", StringComparison.Ordinal))
                    applied = true;
            }
            Require(applied, "the accepted command kept running in the owner and applied its effect exactly once");
            using (var client = new SyncCliBridgeClient())
            {
                string[] resumeArgs = { "--cli", "resume", "--account", cancelAccountId, "--yes", "--json" };
                var ok = client.TryInvoke(resumeArgs, TextWriter.Null, TimeSpan.FromSeconds(30), out var exit,
                    out _, out _);
                Require(ok && exit == 0, "the same host served a later request and returned the account to unpaused");
            }
            using (var raw = new RawBridgeClient())
            {
                var replay = raw.RequestFor(Guid.NewGuid().ToString("N"), cancelArgs);
                raw.Send(replay);
                Require(raw.Drain(4000).Count > 0, "the post-cancellation command executed through the raw transport");
                raw.Send(replay);
                Require(raw.Drain(3000).Any(reply => reply.Contains("duplicate", StringComparison.Ordinal)),
                    "the post-cancellation correlation was refused as duplicate, so nothing was replayed");
            }

#endif
            // With the owner gone, this fixture's own receiver window is absent. The unchanged production
            // client must not answer through it: with no eligible peer it reports false, and when another
            // process merely shares the window title it reports its exact trust refusal instead. Neither
            // claims that no window of that title exists anywhere on the desktop.
            Thread.Sleep(500);
            Require(FindBridgeWindowForProcess(child.Id) == IntPtr.Zero,
                "the isolated owner's own receiver window is gone after it exits");
            var answered = false;
            string? foreignRefusal = null;
            try
            {
                using var client = new SyncCliBridgeClient();
                answered = client.TryInvoke(["--cli", "status", "--json"], TextWriter.Null,
                    TimeSpan.FromSeconds(5), out _, out _, out _);
            }
            catch (SyncCliOperationException refusal) { foreignRefusal = refusal.Message; }
            Require(!answered, "after the owner exits the bridge reports no running application");
            Require(foreignRefusal is null || foreignRefusal.Contains(
                    "is not a trusted Sync Shark runtime", StringComparison.Ordinal),
                "a foreign-only receiver window is reported as an exact trust refusal instead of a running " +
                "owner: " + (foreignRefusal ?? "no refusal was raised"));
        // Retained views across a REAL process boundary. The second owner is started WITHOUT initial setup,
        // so it cannot recreate either record: whatever it publishes is what the first owner durably
        // retained. Both views are compared by parsed content, not by formatting and not by row counts alone.
        File.Delete(Path.Combine(hostRoot, "stop"));
        File.Delete(Path.Combine(hostRoot, "start-active-run"));
        File.Delete(Path.Combine(hostRoot, "seed-queued-operation"));
        using (var restarted = Process.Start(new ProcessStartInfo(executable)
               {
                   Arguments = "--cli-bridge-host \"" + hostRoot + "\"",
                   UseShellExecute = false,
                   CreateNoWindow = true
               }) ?? throw new InvalidOperationException("The restarted fixture host could not be started."))
        {
            // R477-2: the restarted owner publishes its own receiver window as well; wait for that exact
            // process's window before the production probe runs, exactly as for the first owner.
            var restartedReceiver = IntPtr.Zero;
            var restartedReceiverDeadline = DateTime.UtcNow.AddSeconds(30);
            while (restartedReceiver == IntPtr.Zero && DateTime.UtcNow < restartedReceiverDeadline)
            {
                if (restarted.HasExited)
                    throw new InvalidOperationException("The restarted fixture host exited early with code " +
                        restarted.ExitCode + HostDiagnostic(hostRoot));
                restartedReceiver = FindBridgeWindowForProcess(restarted.Id);
                if (restartedReceiver == IntPtr.Zero) Thread.Sleep(50);
            }
            Require(restartedReceiver != IntPtr.Zero,
                "the restarted owner published its own bridge window addressed by its exact process id");
            var reconnectDeadline = DateTime.UtcNow.AddSeconds(30);
            var reconnected = false;
            while (DateTime.UtcNow < reconnectDeadline && !reconnected)
            {
                using var probe = new SyncCliBridgeClient();
                if (probe.TryInvoke(["--cli", "status", "--json"], TextWriter.Null, TimeSpan.FromSeconds(30),
                        out var probeExit, out var probeText, out _) && probeExit == 0 &&
                    StatusMode(probeText) == "running-application")
                    reconnected = true;
                if (restarted.HasExited)
                    throw new InvalidOperationException("The restarted fixture host exited early with code " +
                        restarted.ExitCode + HostDiagnostic(hostRoot));
            }
            Require(reconnected, "a second owner process served the same isolated root");
            Require(File.Exists(Path.Combine(hostRoot, "restart-read-only")),
                "the second owner verified existing state without entering initial setup or seeding");
            var runsAfter = Invoke(["--cli", "runs", "list", "--json"]);
            var queueAfter = Invoke(["--cli", "queue", "list", "--json"]);
            Require(runsAfter.ok && runsAfter.exit == 0 && DataRaw(runsAfter.text) == runsBeforeData,
                "the retained run view across the boundary is exactly the first owner's durable view");
            var queueIdentityAfter = ReadRetainedQueueIdentity(queueAfter.text);
            Require(queueAfter.ok && queueAfter.exit == 0 && queueIdentityBefore is not null &&
                    queueIdentityAfter == queueIdentityBefore && DataRaw(queueAfter.text) == queueBeforeData,
                "the retained queue view across the boundary is exactly the first owner's durable view, its real " +
                "operation identity included: " + (DataRaw(queueAfter.text) ?? string.Empty));
            Require(queueReturnedBefore == "1" && DataMember(queueAfter.text, "returned") == "1" &&
                    queueIdentityAfter is not null,
                "the second owner neither duplicated nor recreated the retained queued operation");
            var operationAfter = retainedAuthority.LoadOperationAsync(RetainedOperationId).GetAwaiter().GetResult();
            Require(operationBefore is not null &&
                    System.Text.Json.JsonSerializer.Serialize(operationAfter) ==
                    System.Text.Json.JsonSerializer.Serialize(operationBefore),
                "the authority retained identical operation, pair, object, status, member and binding facts across restart");
            if (!queuedSeeded)
                Console.WriteLine("UNEXECUTED the retained queued operation across the process boundary: " +
                    queuedSeedDetail);
            File.WriteAllText(Path.Combine(hostRoot, "stop"), "parent-stop");
            restarted.WaitForExit(15_000);
            Require(restarted.HasExited, "the restarted owner stopped cleanly at the parent's request");
        }

        // Root sshark:391-r1/r3: an unproven queue must terminate the route as FAILURE with a nonzero exit.
        Require(queuedSeeded, "a real queued operation was retained for the required persistence proof: " +
            queuedSeedDetail);
        Console.WriteLine(Json("PASS", checks));
        return 0;

        static (bool ok, int exit, string text) Invoke(string[] args)
        {
            using var client = new SyncCliBridgeClient();
            var ok = client.TryInvoke(args, TextWriter.Null, TimeSpan.FromSeconds(30), out var exit, out var text,
                out _);
            return (ok, exit, text);
        }
    }

    // UI5 proof: the isolated fixture host is addressed by its exact process id instead of the shared bridge
    // window title, so a real product application that happens to be running cannot be selected instead of the
    // owner under test. Every command below travels the production transport, is executed by the production
    // SyncCliBridgeServer and its real registry classification, and the presentation hook it may or may not
    // call is the host's own recorded hook.
    internal static int RunPresentationHookChecks(string root)
    {
        Directory.CreateDirectory(root);
        var hostRoot = Path.Combine(root, "host");
        var appHost = Path.Combine(AppContext.BaseDirectory, "SyncShark.UiFixture.exe");
        var executable = File.Exists(appHost)
            ? appHost
            : throw new InvalidOperationException(
                "This check must run through the fixture apphost so the host and the caller are the same " +
                "product executable: " + appHost);
        using var child = Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = "--cli-bridge-host \"" + hostRoot + "\" initial",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The fixture host process could not be started.");
        var checks = 0;
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("CLI bridge presentation check failed: " + label);
            checks++;
            Console.WriteLine("PASS " + label);
        }
        var refreshLog = Path.Combine(hostRoot, "presentation-refresh-log.txt");
        int RefreshCount() => File.Exists(refreshLog) ? File.ReadAllLines(refreshLog).Length : 0;
        try
        {
            var target = IntPtr.Zero;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (target == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                if (child.HasExited)
                    throw new InvalidOperationException("The fixture host exited early with code " +
                        child.ExitCode + HostDiagnostic(hostRoot));
                target = FindBridgeWindowForProcess(child.Id);
                if (target == IntPtr.Zero) Thread.Sleep(50);
            }
            Require(target != IntPtr.Zero,
                "the isolated owner exposes its own bridge window addressed by its exact process id");
            var readyDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(hostRoot, "connection-id.txt")) && DateTime.UtcNow < readyDeadline)
                Thread.Sleep(50);
            var accountId = File.ReadAllText(Path.Combine(hostRoot, "connection-id.txt")).Trim();
            Require(accountId.Length > 0, "the isolated owner finished its provider-free setup");
            using var client = new RawBridgeClient(target, child.Id);
            Require(RefreshCount() == 0,
                "no presentation refresh is recorded before the first command reaches the owner");
            foreach (var readOnly in new[]
                     {
                         new[] { "--cli", "status", "--json" },
                         new[] { "--cli", "jobs", "status", "--json" },
                         new[] { "--cli", "activity", "list", "--json" },
                         new[] { "--cli", "runs", "list", "--json" },
                         new[] { "--cli", "queue", "list", "--json" },
                         new[] { "--cli", "pairs", "list", "--json" },
                         new[] { "--cli", "settings", "show", "--json" },
                         new[] { "--cli", "help", "--json" },
                         new[] { "--cli", "version", "--json" },
                         new[] { "--cli", "capabilities", "--json" }
                     })
            {
                var readOnlyReply = client.Invoke(readOnly);
                Require(readOnlyReply.Kind == "result" && readOnlyReply.ExitCode == 0 && RefreshCount() == 0,
                    "the real read-only command '" + string.Join(' ', readOnly.Skip(1)) + "' returned kind '" +
                    readOnlyReply.Kind + "' exit " + readOnlyReply.ExitCode +
                    " and invoked the presentation hook " + RefreshCount() + " time(s)");
            }
            var mutation = client.Invoke(["--cli", "run", "pause", "--yes", "--json"]);
            Require(mutation.Kind == "result" && mutation.ExitCode == 0 && RefreshCount() == 1,
                "a genuine mutation invoked the presentation hook exactly once (exit " + mutation.ExitCode +
                ", count " + RefreshCount() + ")");
            File.WriteAllText(Path.Combine(hostRoot, "presentation-fails"), "fail");
            var withFailingHook = client.Invoke(["--cli", "pause", "--account", accountId,
                "--minutes", "30", "--yes", "--json"]);
            Require(withFailingHook.Kind == "result" && withFailingHook.ExitCode == 0 && RefreshCount() == 2,
                "a committed mutation still succeeded and refreshed while the presentation hook threw (exit " +
                withFailingHook.ExitCode + ", count " + RefreshCount() + ")");
            File.Delete(Path.Combine(hostRoot, "presentation-fails"));
            var beforeFailed = RefreshCount();
            var failedMutation = client.Invoke(["--cli", "cancel", "--key",
                "command-00000000000000000000000000000000", "--yes", "--json"]);
            Require(failedMutation.Kind == "result" && failedMutation.ExitCode == 4 &&
                    failedMutation.Text.Contains("missing_target", StringComparison.Ordinal) &&
                    RefreshCount() == beforeFailed + 1,
                "a partially failed genuine mutation still invoked the presentation hook exactly once (exit " +
                failedMutation.ExitCode + ", count " + RefreshCount() + ")");
            var beforeResume = RefreshCount();
            var readOnlyAfterMutation = client.Invoke(["--cli", "runs", "list", "--json"]);
            Require(readOnlyAfterMutation.Kind == "result" && RefreshCount() == beforeResume,
                "a later read-only command still invokes no presentation refresh (count " + RefreshCount() + ")");
            var ownerExit = client.Invoke(["--cli", "app", "exit", "--yes", "--json"]);
            Require(ownerExit.Kind == "result" && ownerExit.ExitCode == 0 && RefreshCount() == beforeResume,
                "a completed owner exit returns without refreshing a closed window or reloading its history");
            Require(child.WaitForExit(15_000) && child.ExitCode == 0,
                "the exact fixture owner exits after delivering its completed checkpoint result");
            Console.WriteLine(Json("PASS", checks));
            return 0;
        }
        finally
        {
            File.WriteAllText(Path.Combine(hostRoot, "stop"), "parent-stop");
            child.WaitForExit(15_000);
        }
    }


    // Host mode: isolated authority state plus the production bridge server, blocking until killed.
    internal static int RunHost(string root, bool initialSetup)
    {
        Directory.CreateDirectory(root);
        try
        {
            var lockPath = Path.Combine(root, "state", "instance.lock");
            using var instance = PilotInstanceLock.Acquire(lockPath);
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority,
                new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
            host.LoadPairsAsync().GetAwaiter().GetResult();
            // A retained fixture account so provider-free mutations have an exact target inside the isolated
            // owner. No real provider, credential or production state is involved.
            var connections = new DropboxConnectionStore(instance.StateAuthority);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:fixture-host", "fixture-root", "fixture-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            if (initialSetup)
                connections.SaveNewAsync(binding).GetAwaiter().GetResult();
            else
            {
                var retained = connections.Load(binding.ConnectionId)
                    ?? throw new InvalidDataException("The restarted owner has no retained fixture connection.");
                if (retained.ConnectionId != binding.ConnectionId)
                    throw new InvalidDataException("The restarted owner loaded a different fixture connection.");
                File.WriteAllText(Path.Combine(root, "restart-read-only"), "verified without reseeding");
            }
            File.WriteAllText(Path.Combine(root, "connection-id.txt"), binding.ConnectionId);
            var operations = new FixtureCliOperations(host, instance.StateAuthority, root);
            var stop = Path.Combine(root, "stop");
            // Retained state is seeded ONLY at initial setup: a second owner on the same root must show
            // what the first owner durably retained, never recreate it.
            if (initialSetup) SeedRetainedHistory(host, root);
            using var server = new SyncCliBridgeServer(operations, () => File.WriteAllText(stop, "exit"),
                refreshPresentation: () =>
                {
                    File.WriteAllText(Path.Combine(root, "presentation-refreshed"), "refreshed");
                    File.AppendAllText(Path.Combine(root, "presentation-refresh-log.txt"), "refresh\n");
                    if (File.Exists(Path.Combine(root, "presentation-fails")))
                        throw new InvalidOperationException("Controlled presentation failure");
                    return Task.CompletedTask;
                });
            server.Start();
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (!File.Exists(stop) && DateTime.UtcNow < deadline)
            {
                if (initialSetup)
                {
                    StartActiveRunIfRequested(root, host);
                    SeedQueuedOperationIfRequested(root, host);
                }
                Thread.Sleep(50);
            }
            return 0;
        }
        catch (Exception error)
        {
            // Bounded exception diagnostics under the isolated fixture root: with no foreground console
            // this is how the parent observes the exact startup failure instead of guessing.
            File.WriteAllText(Path.Combine(root, "host-error.txt"), error.GetType().Name + ": " + error.Message);
            return 2;
        }
    }

    private static string Json(string result, int checks) =>
        "{\"result\":\"" + result + "\",\"checks\":" + checks +
        ",\"note\":\"two real processes over synthetic isolated state; no provider, credential or installed runtime\"}";

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, System.Text.StringBuilder text, int maximum);

    // The isolated owner is identified by its own process id and the product's exact bridge title, so an
    // unrelated running application that merely shares that title is never addressed instead of it.
    private static IntPtr FindBridgeWindowForProcess(int processId)
    {
        var title = SyncCliBridgeProtocol.ReceiverWindowTitle;
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId) return true;
            var text = new System.Text.StringBuilder(256);
            GetWindowTextW(window, text, 256);
            if (!string.Equals(text.ToString(), title, StringComparison.Ordinal)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // Semantic JSON parse: never rely on formatting-sensitive substrings for protocol assertions.
    // One durable retained run-history row and one real retained queued operation, both created through
    // the real authority API - never hand-written rows. The queued operation is reseeded only when the
    // parent explicitly asks for it, and it is never dispatched, so its pending effect never executes.

    private const string RetainedOperationId = "operation-9d2f6c1a4b8e4703a5c9e1f7b3d80642";
    private const string RetainedOperationKey = "idempotency-cli-retention-9d2f6c1a4b8e4703";
    private const string RetainedObjectId = "object-5e8b2d7f4a1c4936b8e2f0d9a7c31485";
    private const string RetainedMemberId = "member-3a7c1e5f9b2d4806a1c3e5f7b9d02468";
    private const string RetainedRootBinding = "root-binding-fixture-4c8e1a6f2b9d3705";


    private static void SeedRetainedHistory(SyncAuthorityRuntimeHost host, string root)
    {
        var identityPath = Path.Combine(root, "retained-run-id");
        if (File.Exists(identityPath))
        {
            var retainedId = File.ReadAllText(identityPath).Trim();
            if (!host.Authority.LoadRunHistoryAsync().GetAwaiter().GetResult().Any(run => run.Id == retainedId))
                throw new InvalidDataException("The retained fixture command identity has no matching history.");
            return;
        }
        var admission = host.Authority.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null,
            "{\"pairIds\":[]}", SyncAuthorityCommandPurpose.NormalEffect).GetAwaiter().GetResult();
        host.Authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing").GetAwaiter().GetResult();
        host.Authority.TransitionCommandRequestAsync(admission.CommandRequestId, "completed", "{\"completedPairs\":1}")
            .GetAwaiter().GetResult();
        File.WriteAllText(identityPath, admission.CommandRequestId);
    }

    // The parent asks the live owner to begin one synthetic active run by creating a control file. The
    // watcher owns its lifetime: it ends once that exact run has been asked to stop and has reached its
    // checkpoint, and it is a background thread that dies with the owner.
    private static void StartActiveRunIfRequested(string root, SyncAuthorityRuntimeHost host)
    {
        var request = Path.Combine(root, "start-active-run");
        if (!File.Exists(request) || File.Exists(Path.Combine(root, "active-run-id"))) return;
        var id = File.ReadAllText(request).Trim();
        if (id.Length == 0) return;
        var control = host.BeginRunForTestAsync(commandId: id).GetAwaiter().GetResult();
        var watcher = new Thread(() =>
        {
            while (!control.IsStopRequested) Thread.Sleep(25);
            File.WriteAllText(Path.Combine(root, "stop-requested"), "1");
            Thread.Sleep(100);
            host.EndRunForTest(control);
            File.WriteAllText(Path.Combine(root, "active-run-ended"), "1");
        }) { IsBackground = true, Name = "Synthetic active run" };
        watcher.Start();
        File.WriteAllText(Path.Combine(root, "active-run-id"), id);
    }

    // The queued operation is created for the real pair the parent authored through the production
    // dispatcher, so the queue view retains a genuine operation identity rather than an empty row set.
    // Any refusal is recorded verbatim as the exact fact instead of being waived.
    private static void SeedQueuedOperationIfRequested(string root, SyncAuthorityRuntimeHost host)
    {
        var request = Path.Combine(root, "seed-queued-operation");
        if (!File.Exists(request) || File.Exists(Path.Combine(root, "queued-operation-seeded"))) return;
        try
        {
            var pairSet = host.LoadPairsAsync().GetAwaiter().GetResult();
            var pair = pairSet.Pairs.SingleOrDefault();
            if (pair is null)
            {
                File.WriteAllText(Path.Combine(root, "queued-operation-error"),
                    "expected exactly one retained fixture pair, saw " + pairSet.Pairs.Count);
                return;
            }
            host.Authority.RegisterObjectAsync(RetainedObjectId, "directory").GetAwaiter().GetResult();
            // The operation member needs the pair's REAL retained root binding: a synthetic identifier
            // fails the authority's own foreign-key integrity, which is the exact fact worth recording.
            var rootBindingId = host.Authority.LoadActiveRootBindingIdAsync(pair.Id, "local").GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(rootBindingId))
            {
                File.WriteAllText(Path.Combine(root, "queued-operation-error"),
                    "the authored fixture pair retained no active local root binding");
                return;
            }
            var fixtureAdmission = host.Authority.CreateAcceptedCommandAdmissionAsync(null, "reconcile", pair.Id, null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect).GetAwaiter().GetResult();
            host.Authority.CreateOperationAsync(new SyncAuthorityOperationPlan(RetainedOperationId,
                RetainedOperationKey, SyncAuthorityOperationKind.Reconcile, pair.Id, RetainedObjectId,
                1, host.CapabilityProfileIdForTest, "{}", 1000,
                [new SyncAuthorityMember(RetainedMemberId, pair.Id, RetainedObjectId, "lock",
                    rootBindingId, null, null, null, null, "{}")], [], []), fixtureAdmission)
                .GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "queued-operation-seeded"),
                RetainedOperationId + "|" + pair.Id);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "queued-operation-error"),
                error.GetType().Name + ": " + error.Message);
        }
    }

    private sealed record RetainedQueueIdentity(string? OperationId, string? PairId, string? RelativePath,
        string? Operation, string? State, string? ItemKind, string? FileType, int Priority);

    private static RetainedQueueIdentity? ReadRetainedQueueIdentity(string envelope)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            var data = document.RootElement.GetProperty("data");
            var items = data.GetProperty("items");
            if (data.GetProperty("returned").GetInt32() != 1 || items.GetArrayLength() != 1) return null;
            var item = items[0];
            return new RetainedQueueIdentity(item.GetProperty("operationId").GetString(),
                item.GetProperty("pairId").GetString(), item.GetProperty("relativePath").GetString(),
                item.GetProperty("operation").GetString(), item.GetProperty("state").GetString(),
                item.GetProperty("itemKind").GetString(), item.GetProperty("fileType").GetString(),
                item.GetProperty("priority").GetInt32());
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException
                                     or KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    // Structural JSON reads: the retained views are compared by their parsed content, never by formatting.
    private static string? DataRaw(string envelope)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("data").GetRawText();
        }
        catch (Exception) { return null; }
    }

    private static string? DataMember(string envelope, string name)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("data").GetProperty(name).ToString();
        }
        catch (Exception) { return null; }
    }

    private static bool DataBool(string envelope, string name) => DataMember(envelope, name) == "True";

    // Dotted structural read for nested members such as the owner's job identity.
    private static string? DataPath(string envelope, string dottedName)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            var element = document.RootElement.GetProperty("data");
            foreach (var step in dottedName.Split('.')) element = element.GetProperty(step);
            return element.ToString();
        }
        catch (Exception) { return null; }
    }


    // Refusals publish their code under the envelope's error member, not under data.
    private static string? ErrorMember(string envelope, string name)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("error").GetProperty(name).ToString();
        }
        catch (Exception) { return null; }
    }
    private static string? StatusMode(string envelope)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("data").GetProperty("mode").GetString();
        }
        catch (Exception) { return null; }
    }

    private static string HostDiagnostic(string hostRoot)
    {
        var error = Path.Combine(hostRoot, "host-error.txt");
        return File.Exists(error) ? " Host reported: " + File.ReadAllText(error) : string.Empty;
    }

    // Raw transport client used only for hostile and replay cases: it owns a real reply window, sends
    // arbitrary payloads through the production transport and records every reply it receives.
    private sealed class RawBridgeClient : IDisposable
    {
        private readonly SyncCliBridgeTransport.WindowProcedure _procedure;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _replies = new();
        private readonly Thread _thread;
        private readonly IntPtr _target;
        private readonly int _expectedProcessId;
        private IntPtr _window;
        private uint _threadId;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _requests = new();

        // A raw client always addresses one explicit window owned by one explicit fixture process: a
        // default or title-only target could silently reach an unrelated installed build.
        internal RawBridgeClient(IntPtr target, int expectedProcessId)
        {
            if (target == IntPtr.Zero || expectedProcessId <= 0)
                throw new InvalidOperationException(
                    "A raw bridge client requires the exact target window and its owning process id.");
            _target = target;
            _expectedProcessId = expectedProcessId;
            _procedure = HandleMessage;
            _thread = new Thread(Run) { IsBackground = true, Name = "Raw CLI bridge reply window" };
            if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_window == IntPtr.Zero && DateTime.UtcNow < deadline) Thread.Sleep(20);
            if (_window == IntPtr.Zero) throw new InvalidOperationException("Raw reply window unavailable.");
        }

        internal string Request(string nonce, string? malformedBody) =>
            "{\"kind\":\"invoke\",\"nonce\":\"" + nonce + "\",\"issuedUtc\":\"" +
            DateTimeOffset.UtcNow.ToString("O") + "\",\"arguments\":[" +
            (malformedBody is null ? "\"--cli\",\"status\",\"--json\"" : "\"--cli\"") +
            "],\"replyWindow\":\"" + _window.ToInt64().ToString("x") + "\"}" +
            (malformedBody is null ? string.Empty : malformedBody);

        // Fixture arguments contain no quotes or backslashes, so a plain quoted join is the payload.
        internal string RequestFor(string nonce, IReadOnlyList<string> arguments) =>
            "{\"kind\":\"invoke\",\"nonce\":\"" + nonce + "\",\"issuedUtc\":\"" +
            DateTimeOffset.UtcNow.ToString("O") + "\",\"arguments\":[" +
            string.Join(",", arguments.Select(argument => "\"" + argument + "\"")) +
            "],\"replyWindow\":\"" + _window.ToInt64().ToString("x") + "\"}";

        internal void Send(string payload, bool unauthenticatedSender = false)
        {
            if (!unauthenticatedSender) _requests.TryAdd(payload, 0);
            // The target must still belong to the isolated owner process: a recycled HWND would otherwise
            // let this hostile-traffic fixture address a different process.
            GetWindowThreadProcessId(_target, out var owner);
            if (owner != (uint)_expectedProcessId)
                throw new InvalidOperationException("The raw bridge target window is owned by process " +
                    owner + ", not the isolated owner process " + _expectedProcessId + ".");
            SyncCliBridgeTransport.TrySendCopyData(_target, unauthenticatedSender ? IntPtr.Zero : _window, payload,
                5_000, out _);
        }

        // One real request through the production transport, waiting for this exact correlation's result reply.
        internal SyncCliBridgeReply Invoke(IReadOnlyList<string> arguments)
        {
            var nonce = Guid.NewGuid().ToString("N");
            Send(RequestFor(nonce, arguments));
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var payload in Drain(100))
                {
                    SyncCliBridgeReply reply;
                    try { reply = SyncCliBridgeProtocol.ParseReply(payload, nonce); }
                    catch (SyncCliBridgeException) { continue; }
                    if (reply.Kind == "result") return reply;
                }
            }
            throw new TimeoutException("The isolated owner did not return a result for the real command '" +
                string.Join(' ', arguments.Skip(1)) + "'.");
        }

        internal List<string> Drain(int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until) Thread.Sleep(50);
            var result = new List<string>();
            while (_replies.TryDequeue(out var reply)) result.Add(reply);
            return result;
        }

        private void Run()
        {
            var windowClass = new SyncCliBridgeTransport.WindowClass();
            SyncCliBridgeTransport.WindowProcedure procedure = _procedure;
            windowClass.Size = (uint)Marshal.SizeOf<SyncCliBridgeTransport.WindowClass>();
            windowClass.Procedure = Marshal.GetFunctionPointerForDelegate(procedure);
            windowClass.Instance = SyncCliBridgeTransport.GetModuleHandle(null);
            windowClass.ClassName = "SyncSharkRawReply" + Guid.NewGuid().ToString("N");
            if (SyncCliBridgeTransport.RegisterClassEx(ref windowClass) == 0) return;
            _window = SyncCliBridgeTransport.CreateWindowEx(0, windowClass.ClassName!, "Sync Shark raw reply", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) return;
            _threadId = SyncCliBridgeTransport.GetCurrentThreadId();
            while (SyncCliBridgeTransport.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                SyncCliBridgeTransport.TranslateMessage(ref message);
                SyncCliBridgeTransport.DispatchMessage(ref message);
            }
            SyncCliBridgeTransport.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }

        private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message != SyncCliBridgeTransport.CopyDataMessage)
                return SyncCliBridgeTransport.DefWindowProc(window, message, wParam, lParam);
            var proof = SyncCliBridgeTransport.TryReadCopyData(lParam, SyncCliBridgeTransport.VerifyRequestKind);
            if (proof is not null) return _requests.ContainsKey(proof) ? new IntPtr(1) : IntPtr.Zero;
            var payload = SyncCliBridgeTransport.TryReadCopyData(lParam);
            if (payload is not null) _replies.Enqueue(payload);
            return new IntPtr(1);
        }

        public void Dispose()
        {
            if (_window == IntPtr.Zero) return;
            SyncCliBridgeTransport.PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
