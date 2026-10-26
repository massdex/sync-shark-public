using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Offline transport/ownership checks. No product paths, credentials, providers or production engine.</summary>
internal static class AutoSyncHostChecks
{
    public static async Task RunAsync()
    {
        await QueuedItemChoiceAsync(partialClose: false);
        await QueuedItemChoiceAsync(partialClose: true);
        await InvalidItemChoicesAsync();
        await SingleOwnerAndSharedClientsAsync();
        await ReadsNeverLaunchAsync();
        await GuiAdapterSharesHostAsync();
        await ProductionWiringWithEmptyTempStoreAsync();
        await RunsListDispatchesWhileAutoSyncRunsAsync();
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
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0+" + new string('a', 40), "installed", "2.0.0+" + new string('a', 40), false) == "installed", "exact commit-bound version uses installed build");
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0", "installed", "3.0.0", false) == "running", "newer version alone does not declare AutoSync support");
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0+new", "installed", "2.0.0+old", false) == "running", "unknown revision order uses running build");
        Require(AutoSyncProcess.SelectStartupExecutable("running", "2.0.0+UNBOUND", "installed", "2.0.0+UNBOUND", false) == "running", "UNBOUND versions are not comparable");
        Require(AutoSyncProcess.IsExistingOwnerExit((int)SyncCliExitCode.Busy) &&
            !AutoSyncProcess.IsExistingOwnerExit((int)SyncCliExitCode.Failed), "owner contention is distinguished from ordinary crashes");
        var ownerNotice = Path.Combine(Path.GetTempPath(), "supervisor-notice-" + Guid.NewGuid().ToString("N"), "notice.txt");
        AutoSyncProcess.RecordExistingOwnerNotice(ownerNotice);
        AutoSyncProcess.RecordExistingOwnerNotice(ownerNotice);
        Require(File.ReadAllText(ownerNotice).Contains("Retry", StringComparison.Ordinal) && Directory.GetFiles(Path.GetDirectoryName(ownerNotice)!).Length == 1,
            "owner contention records one actionable deduplicated notice without opening the store");
        Require(File.ReadAllText(ownerNotice).Contains("Automatic relaunch will retry", StringComparison.Ordinal),
            "owner notice describes ongoing capped-backoff retries");
        AutoSyncProcess.ClearExistingOwnerNotice(ownerNotice);
        Require(File.ReadAllText(ownerNotice).Length == 0, "successful ownership clears the stale notice");
        Require(AutoSyncProcess.StartupCommand(Path.Combine(Path.GetTempPath(), "Product", "SyncShark.exe"))
            .EndsWith("--core v2 --autosync-supervisor", StringComparison.Ordinal), "startup explicitly selects v2 and supervises crashes");
        if (OperatingSystem.IsWindows()) await AutoSyncProcessChecks.RunAsync();
        Console.WriteLine("AutoSync host checks passed (fake transport peers and isolated empty temp composition only).");
    }

    private static async Task QueuedItemChoiceAsync(bool partialClose)
    {
        var root = Path.Combine(Path.GetTempPath(), "queued-choice-" + Guid.NewGuid().ToString("N"));
        using var store = OpenChoiceStore(root);
        store.AddNotices([new Notice("pair-1", 0, NoticeKind.Info, "file.txt", "Item failed because access was denied. Fix permissions, then Retry.", "sync-retry"),
            new Notice("pair-1", 0, NoticeKind.Info, "", "outage", "autosync-failed")]);
        var notice = store.OpenNotices("pair-1").Single(n => n.Path == "file.txt");
        var outage = store.OpenNotices("pair-1").Single(n => n.Path == "");
        store.CloseNotice(outage.NoticeId!, NoticeChoices.Ignore);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        var queue = new HostNoticeChoiceQueue(store, async (_, ct) =>
        {
            workToken = ct;
            entered.SetResult();
            await release.Task;
            if (partialClose) store.CloseNotice(notice.NoticeId!, NoticeChoices.Ignore); // partial failure after closing
            return new HostCliOutput(6, "", "simulated settings write failure");
        });
        var engine = new ChoiceEngine(queue);
        var address = Address();
        await using var host = AutoSyncBackgroundHost.Start(address, () => new Lease(), () => engine, _ => true);
        var request = AutoSyncHostClient.Request("cli") with { CliArgs =
            ["--cli", "conflicts", "resolve", "--pair", "pair-1", "--path", "file.txt", "--key", notice.NoticeId!,
                "--generation", "0", "--choice", NoticeChoices.Exclude, "--yes", "--json"] };
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // E9: only explicit Exclude is queued. Always release the fake operation before host
        // disposal, including failed transport/assertion paths (round17 measured a cleanup deadlock).
        try
        {
            var reply = await Client(address).TrySendAsync(request, TimeSpan.FromSeconds(2), bound.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(reply?.Data is not null, "queued exclusion receives a host reply");
            var output = reply!.Data!.Value.Deserialize<HostCliOutput>(AutoSyncHostProtocol.Json)!;
            Require(JsonDocument.Parse(output.Stdout).RootElement.GetProperty("queued").GetBoolean(), "host returns queued before the item choice finishes");
            bound.Cancel();
            Require(!workToken.CanBeCanceled, "accepted queued work is independent of client cancellation");
            var stopped = queue.StopAsync(() => throw new IOException("simulated AutoSync stop failure"));
            Require(!stopped.IsCompleted, "even a failing AutoSync stop drains accepted choices");
            release.TrySetResult();
            try { await stopped.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("stop failure was swallowed"); }
            catch (IOException) { }
        }
        finally { release.TrySetResult(); }
        Require(store.NoticeById("pair-1", notice.NoticeId!) is { Closed: false }, "failed choice reopens its own item notice even after partial Ignore");
        Require(store.NoticeById("pair-1", outage.NoticeId!) is { Closed: false } &&
            store.OpenNotices("pair-1").All(n => n.NoticeId != outage.NoticeId),
            "failed item choice leaves ignored pair outage suppressed and actionable");
        var recovered = store.NoticeById("pair-1", notice.NoticeId!)!.Value.Notice;
        Require(recovered.Detail.StartsWith(notice.Detail + "\n\n", StringComparison.Ordinal) &&
            recovered.Detail.Contains("simulated settings write failure", StringComparison.Ordinal),
            "SQLite recovery retains original why/fix text and appends the failure");
        using var reopened = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        Require(reopened.NoticeById("pair-1", notice.NoticeId!) is { Closed: false } saved &&
            saved.Notice.Detail == recovered.Detail, "recovery is durable in the real SQLite store");
    }

    private static SqliteStateStore OpenChoiceStore(string root)
    {
        var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(new PairDef("pair-1", "Pair A",
            new RootDef("ra", "Local", null, Path.Combine(root, "a"), "local-a"),
            new RootDef("rb", "Dropbox", "account-fixture", "/b", "remote-b"), [], [], true));
        return store;
    }

    private static async Task InvalidItemChoicesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "invalid-choice-" + Guid.NewGuid().ToString("N"));
        using var store = OpenChoiceStore(root);
        var item = new ItemState("retained.txt", ItemKind.File, "item-fixture", "v1", "hash", 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        store.AddNotices([
            new Notice("pair-1", 0, NoticeKind.Info, "retry.txt", "Original why and fix", "sync-retry"),
            new Notice("pair-1", 0, NoticeKind.Info, "ordinary.txt", "Original information", "ordinary"),
            new Notice("pair-1", 0, NoticeKind.Info, "retained.txt", "Original version explanation", "retained")
                { RetainedVersion = new RetainedNoticeVersion("action-fixture", Side.B, item, item) },
            new Notice("pair-1", NoticeChoices.DeletionReviewRestoreOnlyRule, NoticeKind.Info,
                "review.txt", "Original deletion explanation", "deletion-review")]);
        var retry = store.OpenNotices("pair-1").Single(n => n.Path == "retry.txt");
        var retained = store.OpenNotices("pair-1").Single(n => n.Path == "retained.txt");
        foreach (var test in new[] {
            (Notice: retry, Path: "wrong.txt", Choice: NoticeChoices.Ignore),
            (Notice: retry, Path: "retry.txt", Choice: NoticeChoices.DeleteReviewed),
            (Notice: retained, Path: "retained.txt", Choice: NoticeChoices.Ignore),
            (Notice: store.OpenNotices("pair-1").Single(n => n.Path == "ordinary.txt"), Path: "ordinary.txt", Choice: NoticeChoices.Ignore),
            // E9: round20 measured Exclude is valid and queued; restore-only Delete remains forbidden.
            (Notice: store.OpenNotices("pair-1").Single(n => n.Path == "review.txt"), Path: "review.txt", Choice: NoticeChoices.DeleteReviewed) })
        {
            var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new CancellationTokenSource();
            CancellationToken observed = default;
            var queue = new HostNoticeChoiceQueue(store, (_, ct) => { observed = ct; return release.Task; });
            var request = AutoSyncHostClient.Request("cli") with { CliArgs =
                ["--cli", "conflicts", "resolve", "--pair", "pair-1", "--path", test.Path,
                    "--key", test.Notice.NoticeId!, "--generation", "0", "--choice", test.Choice, "--yes"] };
            var pending = queue.ExecuteAsync(request, client.Token);
            Console.WriteLine($"NOTICE-MEASURE invalid-choice path={test.Path} reason={test.Notice.ReasonKey} choice={test.Choice} completed={pending.IsCompleted} tokenMatches={observed == client.Token} options={string.Join(",", NoticeOptions.Options(test.Notice).Select(o => o.Choice))}");
            Require(!pending.IsCompleted && observed == client.Token, "invalid choice executes directly, never returns queued");
            release.SetException(new SyncCliReviewException("Choice not accepted"));
            try { await pending; throw new InvalidOperationException("Expected direct refusal"); }
            catch (SyncCliReviewException) { }
            await queue.StopAsync(() => Task.CompletedTask);
            Require(store.NoticeById("pair-1", test.Notice.NoticeId!) is { Closed: false } unchanged &&
                unchanged.Notice == test.Notice, "invalid choice leaves the original SQLite notice untouched");
        }
        // Defensive recovery also handles a retained-version row without relying on Add's no-op semantics.
        store.CloseNotice(retained.NoticeId!, "partial-choice");
        store.RecordNoticeChoiceFailure(retained, "simulated failure");
        Require(store.NoticeById("pair-1", retained.NoticeId!) is { Closed: false } restored &&
            restored.Notice.RetainedVersion == retained.RetainedVersion &&
            restored.Notice.Detail.StartsWith(retained.Detail, StringComparison.Ordinal),
            "atomic recovery cannot permanently close or strip retained-version evidence");
    }

    private sealed class ChoiceEngine(HostNoticeChoiceQueue queue) : IAutoSyncHostEngine
    {
        public Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken ct) => queue.ExecuteAsync(request, ct);
        public Task StopAsync() => queue.StopAsync(() => Task.CompletedTask);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        catch (AutoSyncHostReservationException) { }
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
        Require(status.TryGetProperty("storeStale", out _) && status.TryGetProperty("storeAsOf", out _),
            "production status always states whether its store fields are stale and when they were read");
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

    // H1: a read-only run-history command must be dispatched by the running host, not refused by its allowlist.
    private static async Task RunsListDispatchesWhileAutoSyncRunsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "autosync-runslist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var authority = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        await using var engine = new ProductionAutoSyncEngine(authority, root);
        var composition = (EngineComposition)typeof(ProductionAutoSyncEngine)
            .GetField("_composition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(engine)!;
        // Retention prunes rows older than the settings horizon, so seed them at the current time.
        var started = DateTimeOffset.UtcNow;
        composition.Store.RecordRunSummary(new RunSummary(0, "reconcile-fixture", "pair-fixture", started,
            started.AddSeconds(1), RunOutcomes.Complete, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        composition.Store.RecordRunSummary(new RunSummary(0, "autosync-fixture", "pair-fixture", started,
            started.AddSeconds(2), RunOutcomes.Complete, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var before = composition.Store.ReadRunHistory(null, 50).Select(run => run.RunId).ToArray();
        var statusBefore = JsonSerializer.SerializeToElement(
            await engine.ExecuteAsync(AutoSyncHostClient.Request("status"), default));
        Require(statusBefore.GetProperty("running").GetBoolean(), "AutoSync is running before the host read");
        var output = (HostCliOutput)await engine.ExecuteAsync(AutoSyncHostClient.Request("cli") with
            { CliArgs = ["--cli", "runs", "list", "--json"] }, default);
        Require(output.ExitCode == 0, "runs list is dispatched through the running host: " + output.Stderr);
        using var parsed = JsonDocument.Parse(output.Stdout);
        var kinds = parsed.RootElement.GetProperty("data").GetProperty("runs").EnumerateArray()
            .Select(run => run.GetProperty("kind").GetString()).ToArray();
        Require(kinds.Contains("reconciliation") && kinds.Contains("autosync"),
            "runs list returns the retained run kinds through the host: " + output.Stdout);
        var after = composition.Store.ReadRunHistory(null, 50).Select(run => run.RunId).ToArray();
        Require(before.SequenceEqual(after), "the host read leaves retained run history unchanged");
        var statusAfter = JsonSerializer.SerializeToElement(
            await engine.ExecuteAsync(AutoSyncHostClient.Request("status"), default));
        Require(statusAfter.GetProperty("running").GetBoolean(), "AutoSync is still running after the host read");
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
