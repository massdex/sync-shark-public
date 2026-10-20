using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Offline transport/ownership checks. No product paths, credentials, providers or production engine.</summary>
internal static class AutoSyncHostChecks
{
    public static async Task RunAsync()
    {
        await SingleOwnerAndSharedClientsAsync();
        await ReadsNeverLaunchAsync();
        await GuiAdapterSharesHostAsync();
        await ProductionWiringWithEmptyTempStoreAsync();
        await ReadsBypassMutationSlotsAsync();
        await SafeStopAsync();
        await DisconnectDoesNotReplayAsync();
        await MalformedFrameDoesNotKillHostAsync();
        await FailedConstructionReleasesAuthorityAsync();
        ValidateRequests();
        using (var writer = new HostCliWriter(10))
        {
            writer.Write("0123456789"); writer.Write("abcdefghijk");
            Require(writer.Truncated && writer.ToString() == "bcdefghijk", "bounded output retains the tail");
        }
        using (var writer = new HostCliWriter(1000))
        {
            writer.Write(new string('a', 900)); writer.Write(new string('b', 300));
            Require(writer.Truncated && writer.ToString() == new string('a', 700) + new string('b', 300), "lazy buffer growth preserves ordered tail");
        }
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0", "installed", "1.0.0", false) == "running", "older installed build is never selected");
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0", "installed", "2.0.0", false) == "installed", "same version uses installed build");
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0", "installed", "3.0.0", false) == "installed", "newer installed build is selected");
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0+new", "installed", "2.0.0+old", false) == "running", "unknown revision order uses running build");
        Require(AutoSyncProcess.StartupCommand(Path.Combine(Path.GetTempPath(), "Product", "SyncShark.exe"))
            .EndsWith("--core v2 --autosync-supervisor", StringComparison.Ordinal), "startup explicitly selects v2 and supervises crashes");
        if (OperatingSystem.IsWindows()) await AutoSyncProcessChecks.RunAsync();
        Console.WriteLine("AutoSync host checks passed (fake transport peers and isolated empty temp composition only).");
    }

    private static string Address() => "SyncShark.HostCheck." + Guid.NewGuid().ToString("N");
    private static AutoSyncHostClient Client(string address) => new(address, _ => true);
    private static AutoSyncBackgroundHost Start(string address, Lease lease, FakeEngine engine) =>
        AutoSyncBackgroundHost.Start(address, () => lease, () => engine, _ => true);

    private static async Task<AutoSyncHostReply> SendAsync(AutoSyncHostClient client, string command,
        string? pair = null)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await client.TrySendAsync(AutoSyncHostClient.Request(command, pair), TimeSpan.FromSeconds(2), bound.Token)
            ?? throw new InvalidOperationException("The test host did not accept the client.");
    }

    private static async Task SingleOwnerAndSharedClientsAsync()
    {
        var address = Address();
        var lease = new Lease();
        var engine = new FakeEngine();
        await using var host = Start(address, lease, engine);
        var secondFactoryCalled = false;
        try
        {
            await using var second = AutoSyncBackgroundHost.Start(address,
                () => { secondFactoryCalled = true; return new Lease(); }, () => new FakeEngine(), _ => true);
            throw new InvalidOperationException("A second listener was admitted.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Require(!secondFactoryCalled, "second start never creates an authority or engine");
        var cli = Client(address);
        var gui = Client(address);
        var first = await SendAsync(cli, "status");
        var secondReply = await SendAsync(gui, "status");
        Require(first.Ok && secondReply.Ok && first.Host == secondReply.Host && first.Host == host.Identity,
            "CLI and GUI adapters share the same authority identity");
        Require(first.Data!.Value.GetProperty("pairs")[0].GetProperty("state").GetString() == "idle", "status data round trips");
        var history = await SendAsync(cli, "history", "pair-1");
        Require(history.Ok && history.Data!.Value.GetProperty("runs")[0].GetProperty("durationSeconds").GetInt32() == 3,
            "bounded history data round trips");
        var failed = await SendAsync(cli, "now", "pair-fails");
        Require(!failed.Ok && failed.Error?.Code == "operation_failed", "failed pair returns an error, not process death");
        Require((await SendAsync(gui, "status")).Ok, "host survives a failed pair command");
        Require(!lease.Disposed, "clients never release the shared authority");
    }

    private static async Task ProductionWiringWithEmptyTempStoreAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "autosync-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "state", "instance.lock");
        using var authority = PilotInstanceLock.Acquire(lockPath);
        await using var engine = new ProductionAutoSyncEngine(authority, root);
        var status = JsonSerializer.SerializeToElement(await engine.ExecuteAsync(AutoSyncHostClient.Request("status"), default));
        Require(status.GetProperty("running").GetBoolean(), "production engine opens only the explicit temp store");
        await engine.ExecuteAsync(AutoSyncHostClient.Request("interval", minutes: 7), default);
        Require(new SyncAutomationStore(authority.StateAuthority).Load().IntervalMinutes == 7, "host interval persists");
        await engine.ExecuteAsync(AutoSyncHostClient.Request("cli") with
        { CliArgs = ["--cli", "pause", "--yes", "--until-resumed"] }, default);
        Require(PauseStateProjection.GlobalPaused(new UiPreferencesStore(authority.StateAuthority).Load(), DateTimeOffset.UtcNow),
            "CLI pause reaches owner preferences");
        await engine.ExecuteAsync(AutoSyncHostClient.Request("cli") with
        { CliArgs = ["--cli", "resume", "--yes"] }, default);
        Require(!PauseStateProjection.GlobalPaused(new UiPreferencesStore(authority.StateAuthority).Load(), DateTimeOffset.UtcNow),
            "CLI resume reaches owner preferences");
        await engine.ExecuteAsync(AutoSyncHostClient.Request("cli") with
        { CliArgs = ["--cli", "settings", "set", "--settings", "{\"automation\":{\"intervalMinutes\":9}}", "--yes"] }, default);
        Require(new SyncAutomationStore(authority.StateAuthority).Load().IntervalMinutes == 9, "CLI settings shares host authority");
        await engine.StopAsync();
    }

    private static async Task ReadsNeverLaunchAsync()
    {
        var client = Client(Address());
        foreach (var command in new[] { "status", "history", "logs" })
        {
            var reply = await client.TrySendAsync(AutoSyncHostClient.Request(command,
                command == "history" ? "pair-1" : null), TimeSpan.FromMilliseconds(50));
            Require(reply is null, "a read against an absent host does not create an owner");
        }
    }

    private static async Task GuiAdapterSharesHostAsync()
    {
        var address = Address();
        var lease = new Lease();
        await using var host = Start(address, lease, new FakeEngine());
        var gui = new AutoSyncGuiClient(Client(address));
        var cli = Client(address);
        var status = await gui.StatusAsync();
        var interval = await gui.SetIntervalAsync(7);
        var cliStatus = await SendAsync(cli, "status");
        Require(status?.Host == cliStatus.Host && interval?.Host == cliStatus.Host,
            "GUI preferences and CLI reads address one host identity");
        Require(!lease.Disposed, "GUI client does not dispose the owner's lease");
        foreach (var command in new[] { "start", "stop", "status", "enable", "disable", "interval", "history", "now", "logs" })
            Require(SyncCliRegistry.Rows.Any(row => row.Command == "autosync " + command), "AutoSync command is registered: " + command);
    }

    private static async Task ReadsBypassMutationSlotsAsync()
    {
        var address = Address();
        var engine = new FakeEngine { BlockCommand = true };
        await using var host = Start(address, new Lease(), engine);
        var requests = Enumerable.Range(0, 8).Select(_ => SendAsync(Client(address), "now", "pair-1")).ToArray();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref engine.NowCalls) != 8) await Task.Delay(10, deadline.Token);
            foreach (var command in new[] { "status", "history", "logs" })
                Require((await SendAsync(Client(address), command, command == "history" ? "pair-1" : null)).Ok,
                    command + " bypasses saturated mutation admission");
        }
        finally { engine.CommandRelease.TrySetResult(); await Task.WhenAll(requests); }
    }

    private static async Task SafeStopAsync()
    {
        var address = Address();
        var lease = new Lease();
        var engine = new FakeEngine { WaitAtSafePoint = true };
        await using var host = Start(address, lease, engine);
        var stop = SendAsync(Client(address), "stop");
        await engine.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!stop.IsCompleted && !lease.Disposed && !engine.Disposed, "stop waits for the safe point with the authority held");
        var status = await SendAsync(Client(address), "status");
        Require(status.Ok && status.State == "stopping", "status stays available while draining");
        var rejected = await SendAsync(Client(address), "now", "pair-1");
        Require(!rejected.Ok && rejected.Error?.Code == "stopping", "no new work starts during drain");
        engine.SafePoint.TrySetResult();
        var reply = await stop;
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require(reply.Ok && reply.State == "stopped" && engine.Disposed && lease.Disposed,
            "stop replies only after safe drain; engine closes before authority release");
        Require(engine.StopCalls == 1, "stop is idempotent");
    }

    private static async Task DisconnectDoesNotReplayAsync()
    {
        var address = Address();
        var engine = new FakeEngine { BlockCommand = true };
        await using var host = Start(address, new Lease(), engine);
        using var cancelled = new CancellationTokenSource();
        var request = Client(address).TrySendAsync(AutoSyncHostClient.Request("now", "pair-1"),
            TimeSpan.FromSeconds(2), cancelled.Token);
        await engine.CommandEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        try { await request; throw new InvalidOperationException("The disconnected client should report uncertainty."); }
        catch (AutoSyncHostUncertainException) { }
        Require(!engine.CommandFinished.Task.IsCompleted, "client cancellation does not cancel admitted work");
        engine.CommandRelease.TrySetResult();
        await engine.CommandFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(engine.NowCalls == 1, "a disconnected mutation is not replayed");
        Require((await SendAsync(Client(address), "status")).Ok, "other clients still attach");
    }

    private static async Task MalformedFrameDoesNotKillHostAsync()
    {
        var address = Address();
        await using var host = Start(address, new Lease(), new FakeEngine());
        using (var pipe = new NamedPipeClientStream(".", address, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(2000);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, AutoSyncHostProtocol.MaximumFrameBytes + 1);
            await pipe.WriteAsync(header);
            await pipe.FlushAsync();
        }
        Require((await SendAsync(Client(address), "status")).Ok, "oversized frame does not kill the host");
        using var stream = new MemoryStream();
        await AutoSyncHostProtocol.WriteAsync(stream, new { value = "unicode שלום" }, CancellationToken.None);
        stream.Position = 0;
        var decoded = await AutoSyncHostProtocol.ReadAsync<JsonElement>(stream, CancellationToken.None);
        Require(decoded.GetProperty("value").GetString() == "unicode שלום", "UTF-8 frame round trips");
    }

    private static async Task FailedConstructionReleasesAuthorityAsync()
    {
        var address = Address();
        var lease = new Lease();
        try
        {
            await using var failed = AutoSyncBackgroundHost.Start(address, () => lease,
                () => throw new InvalidDataException("fixture failure"), _ => true);
            throw new InvalidOperationException("The fixture should fail construction.");
        }
        catch (InvalidDataException) { }
        Require(lease.Disposed, "failed engine construction releases the authority");
        await using var replacement = Start(address, new Lease(), new FakeEngine());
        Require((await SendAsync(Client(address), "status")).Ok, "failed construction releases the pipe address");
    }

    private static void ValidateRequests()
    {
        foreach (var request in new[]
        {
            AutoSyncHostClient.Request("enable"),
            AutoSyncHostClient.Request("disable", "pair-1", all: true),
            AutoSyncHostClient.Request("interval", minutes: 0),
            AutoSyncHostClient.Request("interval", minutes: 1441),
            AutoSyncHostClient.Request("history"),
            AutoSyncHostClient.Request("now"),
            AutoSyncHostClient.Request("logs", limit: 201),
            AutoSyncHostClient.Request("logs", after: -1),
            AutoSyncHostClient.Request("status", "pair-1"),
            AutoSyncHostClient.Request("status") with { Version = 2 }
        })
        {
            try { request.Validate(); throw new InvalidOperationException("Invalid request accepted: " + request); }
            catch (InvalidDataException) { }
        }
        AutoSyncHostClient.Request("enable", all: true).Validate();
        AutoSyncHostClient.Request("enable", "pair-1").Validate();
        AutoSyncHostClient.Request("interval", minutes: 15).Validate();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("AutoSync host check: " + message);
    }

    private sealed class Lease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEngine : IAutoSyncHostEngine
    {
        public bool WaitAtSafePoint { get; init; }
        public bool BlockCommand { get; init; }
        public bool Disposed { get; private set; }
        public int StopCalls;
        public int NowCalls;
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SafePoint { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommandEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommandRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommandFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken cancellationToken)
        {
            if (request.Command == "now")
            {
                Interlocked.Increment(ref NowCalls);
                if (request.PairId == "pair-fails") throw new IOException("isolated pair failure");
                CommandEntered.TrySetResult();
                if (BlockCommand) await CommandRelease.Task.WaitAsync(cancellationToken);
                CommandFinished.TrySetResult();
                return new { queued = true };
            }
            if (request.Command == "history")
                return new { runs = new[] { new { outcome = "complete", uploaded = 2, downloaded = 1, durationSeconds = 3 } } };
            return new { running = true, intervalMinutes = 15, pairs = new[] { new { pairId = "pair-1", state = "idle" } } };
        }

        public async Task StopAsync()
        {
            Interlocked.Increment(ref StopCalls);
            StopEntered.TrySetResult();
            if (WaitAtSafePoint) await SafePoint.Task;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
