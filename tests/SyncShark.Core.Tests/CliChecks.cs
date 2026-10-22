using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Focused real checks for the --cli pilot: real parser, real dispatcher, real exit-code mapping and
// the real SyncCliRuntime adapter over isolated fixture state. Fakes supply bounded input only
// (fixture files, a fixture remote inventory and fixture connection metadata); every decision under
// test stays production code. No user database, settings file, credential or provider call is used.
internal static partial class CliChecks
{
    internal static int RunNativeBridgeClientProbe(string testCase)
    {
        if (testCase is "revocations" or "revocations-too-large")
        {
            var selection = File.ReadAllText(Path.Combine(SyncSharkRuntimePaths.ProductRoot, "cli-revoke-selection.json"));
            using var observer = new SyncCliBridgeClient();
            if (!observer.TryInvoke(["--cli", "sharing", "revoke-selected", "--settings", selection, "--yes", "--json"],
                    Console.Error, TimeSpan.FromSeconds(20), CancellationToken.None,
                    out var observedExit, out var observedOutput, out var observedError))
                throw new InvalidOperationException("Native revocation probe did not find its verified receiver.");
            if (observedError is not null || observedExit != (testCase == "revocations" ? 0 : 5))
            {
                var code = "absent";
                try
                {
                    using var failure = JsonDocument.Parse(observedOutput);
                    var candidate = failure.RootElement.GetProperty("error").GetProperty("code").GetString();
                    if (candidate is "usage" or "review_required" or "operation_failed" or "unsupported" or "busy" or "missing_target")
                        code = candidate;
                }
                catch (Exception) { /* Diagnostic classification only; no raw receipt/error disclosure. */ }
                Console.Error.WriteLine($"NATIVE-REVOKE exit={observedExit}; error={(observedError is null ? "none" : "present")}; code={code}");
                throw new InvalidOperationException("Native revocation transport did not preserve the command's outcome: " + observedError);
            }
            using var receipt = new StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            receipt.Write(observedOutput);
            return 0;
        }
        if (testCase.StartsWith("jobs-", StringComparison.Ordinal))
        {
            var watchKey = testCase.StartsWith("jobs-watch:", StringComparison.Ordinal)
                ? testCase["jobs-watch:".Length..] : null;
            string[] arguments = watchKey is not null
                ? ["--cli", "jobs", "watch", "--key", watchKey, "--seconds", "1", "--json"]
                : testCase == "jobs-missing"
                    ? ["--cli", "jobs", "watch", "--key", "absent-observation-job", "--seconds", "1", "--json"]
                    : testCase == "jobs-human"
                        ? ["--cli", "jobs", "status"] : ["--cli", "jobs", "status", "--json"];
            using var observer = new SyncCliBridgeClient();
            if (!observer.TryInvoke(arguments, Console.Error, TimeSpan.FromSeconds(10), CancellationToken.None,
                    out var observedExit, out var observedOutput, out var observedError))
                throw new InvalidOperationException("Native job observer did not find the actual verified receiver.");
            if (observedError is not null || (testCase == "jobs-missing" ? observedExit == 0 : observedExit != 0))
                throw new InvalidOperationException("Native job transport did not preserve the expected status/watch result: " + observedError);
            // A probe forwards the actual one-object native receipt. Its own
            // assertions must not inject PASS lines into the JSON being tested.
            // Match the production CliEntry's explicitly UTF-8 output stream,
            // rather than this console test executable's Windows code page.
            using var receipt = new StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            receipt.Write(observedOutput);
            return 0;
        }
        var settings = testCase == "success"
            ? "{\"transfer\":{\"resourceMode\":\"Performance\",\"maximumConcurrentFiles\":32}}"
            : "{\"transfer\":{\"resourceMode\":\"invalid-mode\"}}";
        using var client = new SyncCliBridgeClient();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Require(client.TryInvoke(["--cli", "settings", "set", "--settings", settings, "--yes", "--json"],
                Console.Error, TimeSpan.FromSeconds(3), CancellationToken.None,
                out var exit, out var output, out var error), "native client found the actual verified receiver");
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(2),
            "the durable command result did not wait for a deliberately held presentation refresh");
        Require(error is null && (testCase == "success" ? exit == 0 : exit != 0),
            "native transport preserved the success or safe-rejection exit code");
        Console.Write(output);
        return 0;
    }

    internal static async Task RunNativeBridgeChecksAsync(string root)
    {
        Require(OperatingSystem.IsWindows(), "native bridge checks require Windows");
        await BridgeProgressBatchChecksAsync();
        await NativeConcurrentJobChecksAsync(Path.Combine(root, "concurrent-jobs"));
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "product.lock"));
        using var http = new HttpClient();
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http);
        var operations = new BorrowedScanRuntime(host, instance.StateAuthority);
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCalls = 0;
        using var server = new SyncCliBridgeServer(operations, refreshPresentation: async () =>
        {
            Interlocked.Increment(ref refreshCalls);
            refreshEntered.TrySetResult();
            try { await refreshRelease.Task; }
            finally { refreshFinished.TrySetResult(); }
        });
        server.Start();
        try
        {
            using (var competingReceiver = new SyncCliBridgeServer(operations))
            {
                var refused = false;
                try { competingReceiver.Start(); }
                catch (InvalidOperationException) { refused = true; }
                Require(refused, "a second live receiver fails safely without unregistering the original receiver's class");
            }
            var success = await NativeProbeAsync("success");
            await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!refreshFinished.Task.IsCompleted && success.Contains("\"ok\":true", StringComparison.Ordinal),
                "a genuine saved-settings result arrived while the UI refresh was still pending");
            refreshRelease.TrySetResult();
            await refreshFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var rejected = await NativeProbeAsync("rejected");
            Require(rejected.Contains("\"ok\":false", StringComparison.Ordinal),
                "the same real transport returned the rejected settings outcome, not success");
            var readback = await RunCliAsync(["--cli", "settings", "show", "--json"], () => operations);
            var data = JsonData(readback, "native bridge retained settings", "settings show");
            Require(data["transfer"]!["resourceMode"]!.GetValue<string>() == "Performance" &&
                    data["transfer"]!["maximumConcurrentFiles"]!.GetValue<int>() == 32,
                "native success persisted settings and rejection left those exact settings intact");
            Console.WriteLine("PASS native bridge: trusted separate processes; result before pending refresh; success/rejection retained; lossless bounded live progress");
        }
        finally { refreshRelease.TrySetResult(); }
    }

    internal static async Task<string> NativeProbeAsync(string testCase)
    {
        var executable = Environment.ProcessPath!;
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false) };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--cli-bridge-client-probe");
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        start.ArgumentList.Add(testCase);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var result = await stdout;
        var errorText = await stderr;
        if (process.ExitCode != 0)
        {
            var outcome = System.Text.RegularExpressions.Regex.Match(errorText,
                @"NATIVE-REVOKE exit=[0-9]+; error=(?:none|present); code=(?:absent|usage|review_required|operation_failed|unsupported|busy|missing_target)");
            Console.WriteLine("DIAGNOSTIC native child: exit=" + process.ExitCode +
                (outcome.Success ? "; " + outcome.Value : "; no known receipt classification"));
        }
        Require(process.ExitCode == 0, "real separate-process bridge probe passed: " + errorText);
        return result;
    }

    // Synthetic provider input holds 128 genuine downloads. The real host,
    // scheduler, CLI dispatcher and separate-process native bridge own every
    // decision and projection; no active-job snapshot is injected by the test.
    private static async Task NativeConcurrentJobChecksAsync(string root)
    {
        const int files = 128;
        // Program already owns the one fresh isolated product-root override;
        // native child probes inherit that exact root through their arguments.
        using var concurrency = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(files);
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        remote.BeforeDownloadForTest = async token =>
        {
            if (Interlocked.Increment(ref held) == files) entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var payload = new byte[] { 11, 23, 37, 41 };
        var names = Enumerable.Range(0, files).Select(index =>
            index.ToString("D3") + "-" + string.Concat(Enumerable.Repeat("日本語", 40)) + ".bin").ToArray();
        foreach (var name in names) remote.PutBytes(name, payload);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var connections = new DropboxConnectionStore(authority);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:native-jobs", "native-jobs-root", "native-jobs-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
            database, (_, _) => remote);
        await host.LoadPairsAsync();
        using var lease = PilotDirectoryLease.Open(local);
        var pair = SyncPair.FromPilot(new PilotSettings(local, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/NativeJobs", "id:native-jobs"), "Native job observation",
            binding.RootNamespaceId) with { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new BorrowedScanRuntime(host, authority);
        var scan = await RunCliAsync(["--cli", "scan", "--pair", pair.Id, "--yes", "--json"], () => operations);
        Expect("real concurrent-job inventory scan", scan, 0);
        var scanned = JsonData(scan, "concurrent-job inventory", "scan");
        var choices = names.ToDictionary(name => name, _ => (string?)"download", StringComparer.Ordinal);
        choices["__plan"] = scanned["localScanGenerationId"]!.GetValue<string>();
        choices["__remote_plan"] = scanned["dropboxScanGenerationId"]!.GetValue<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var apply = RunCliAsync(["--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json",
            "--choices", JsonSerializer.Serialize(choices)], () => operations, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var snapshot = host.ActiveRun ?? throw new InvalidOperationException("Actual held downloads have no active job.");
            var batch = snapshot.Progress?.Action?.Batch ??
                throw new InvalidOperationException("Actual held downloads have no transfer batch.");
            Require(host.HasActiveRun && batch.InFlight == files && batch.Active.Count == files,
                "the genuine scheduler exposes all 128 held files, not an injected job fixture");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var unfitted = JsonSerializer.Serialize(new SyncCliEnvelope(SyncCli.SchemaVersion, "jobs status", true,
                new SyncCliJobData(snapshot, true), null), options) + Environment.NewLine;
            var rawReply = SyncCliBridgeProtocol.EncodeReply(new("result", new string('0', 32), true, 0, unfitted, null));
            Require(rawReply.Length >= SyncCliBridgeProtocol.MaximumPayloadBytes,
                "the real concurrent snapshot actually exceeds the native reply limit before projection");
            using var server = new SyncCliBridgeServer(operations);
            server.Start();
            foreach (var testCase in new[] { "jobs-status", "jobs-watch:" + snapshot.Id })
            {
                var nativeReceipt = await NativeProbeAsync(testCase);
                await File.WriteAllTextAsync(Path.Combine(root, testCase.StartsWith("jobs-watch:", StringComparison.Ordinal)
                    ? "native-watch.json" : "native-status.json"), nativeReceipt);
                using var observed = JsonDocument.Parse(nativeReceipt);
                var data = observed.RootElement.GetProperty("data");
                var count = data.GetProperty("activeRowsReturned").GetInt32();
                var observedJob = data.GetProperty("job");
                var observedBatch = observedJob.GetProperty("progress").GetProperty("action").GetProperty("batch");
                Require(data.GetProperty("active").GetBoolean() &&
                        data.GetProperty("activeRowsTotal").GetInt32() == files && count is > 0 and < files &&
                        data.GetProperty("activeRowsHasMore").GetBoolean() &&
                        observedJob.GetProperty("id").GetString() == snapshot.Id &&
                        observedBatch.GetProperty("inFlight").GetInt32() == files &&
                        observedBatch.GetProperty("failed").GetInt32() == 0 &&
                        observedBatch.GetProperty("active").GetArrayLength() == count,
                    "actual native status/watch preserves exact job and aggregate counters with an honest bounded active sample");
                var paths = observedBatch.GetProperty("active").EnumerateArray()
                    .Select(row => row.GetProperty("relativePath").GetString()).ToArray();
                Require(paths.SequenceEqual(batch.Active.Take(count).Select(row => row.RelativePath)),
                    "native observation preserves every returned ordered Unicode path without truncation or substitution");
            }
            var human = await NativeProbeAsync("jobs-human");
            Require(human.Contains(snapshot.Id, StringComparison.Ordinal) &&
                    human.Contains("128", StringComparison.Ordinal) &&
                    human.Contains("Retained operation log: activity list --log --run " + snapshot.Id, StringComparison.Ordinal),
                "actual human native status fits and links the exact run's detailed retained log");
            using var missing = JsonDocument.Parse(await NativeProbeAsync("jobs-missing"));
            Require(!missing.RootElement.GetProperty("ok").GetBoolean() && host.HasActiveRun &&
                    host.ActiveRun?.Id == snapshot.Id && held == files,
                "wrong-job native watch rejects safely without cancelling or replacing the real active run");
        }
        finally
        {
            release.TrySetResult();
            await apply; // Drain real effects even after an observation assertion fails.
        }
        var completed = await apply;
        await File.WriteAllTextAsync(Path.Combine(root, "first-sync.stdout.json"), completed.StandardOutput);
        await File.WriteAllTextAsync(Path.Combine(root, "first-sync.progress.log"), completed.StandardError);
        Expect("all held concurrent files finish through actual first-sync CLI", completed, 0);
        Require(!host.HasActiveRun && remote.DownloadCount == files,
            "observation neither duplicates transfers nor leaves the real run active");
        foreach (var name in names)
            Require((await File.ReadAllBytesAsync(Path.Combine(local, name))).SequenceEqual(payload),
                "every real native-observed download installs exact fixture bytes");
        Require(completed.StandardError.Contains("Reading retained operation ", StringComparison.Ordinal) &&
                completed.StandardError.Contains("operation records loaded", StringComparison.Ordinal) &&
                completed.StandardError.Contains("Revalidating the bound local roots and provider account", StringComparison.Ordinal) &&
                completed.StandardError.Contains("Committing the reviewed pair's approval", StringComparison.Ordinal) &&
                completed.StandardError.Contains("0 of 2 approval steps completed", StringComparison.Ordinal) &&
                completed.StandardError.Contains("1 of 2 approval steps completed", StringComparison.Ordinal) &&
                completed.StandardError.Contains("No file transfer is running", StringComparison.Ordinal),
            "real first-sync finishing feedback names the current database operation and approval work, not a fictitious transfer");
        await GroupReaderChecksAsync(database, pair.Id);
        Console.WriteLine("PASS native concurrent jobs: 128 real held transfers; oversized source; status/watch JSON and human sample; exact-target rejection; exact bytes and drain");
    }

    private static async Task GroupReaderChecksAsync(SyncAuthorityDatabase database, string pairId)
    {
        var groups = new List<string>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT operation_group_id FROM operation WHERE pair_id=$pair AND operation_group_id IS NOT NULL ORDER BY operation_group_id;";
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) groups.Add(reader.GetString(0));
        }
        Require(groups.Count > 0, "actual first sync retained real operation groups");
        foreach (var group in groups)
        {
            var observed = new List<(int Loaded, int Total, string Id)>();
            var batch = await database.ListOperationsForGroupAsync(group, onRead: (loaded, total, id) =>
            { observed.Add((loaded, total, id)); return Task.CompletedTask; });
            Require(batch.Count > 0 && observed.Count == batch.Count + 2 &&
                    observed[0] == (0, batch.Count, "") && observed[^1] == (batch.Count, batch.Count, "") &&
                    observed.Skip(1).Take(batch.Count).Select(row => row.Id).SequenceEqual(batch.Select(row => row.OperationId)) &&
                    observed.Skip(1).Take(batch.Count).Select(row => row.Loaded).SequenceEqual(Enumerable.Range(0, batch.Count)),
                "the batch group reader preserves exact operation order and reports actual loaded counts/identities");
            foreach (var operation in batch)
            {
                var original = await database.LoadOperationAsync(operation.OperationId);
                Require(JsonSerializer.Serialize(operation) == JsonSerializer.Serialize(original),
                    "batch group snapshot equals the original reader across status, authority, members, dependencies, allocations and attempts");
            }
            using var stop = new CancellationTokenSource();
            try
            {
                await database.ListOperationsForGroupAsync(group, stop.Token, (_, _, id) =>
                { if (id.Length > 0) stop.Cancel(); return Task.CompletedTask; });
                throw new InvalidOperationException("Cancelled group read was accepted.");
            }
            catch (OperationCanceledException) { }
            var afterCancellation = await database.ListOperationsForGroupAsync(group);
            Require(JsonSerializer.Serialize(batch) == JsonSerializer.Serialize(afterCancellation),
                "cancellation propagates without modifying or completing any retained operation");
        }
        Require((await database.ListOperationsForGroupAsync("absent-fixture-operation-group")).Count == 0,
            "an absent operation group remains an empty read, not fabricated success");
    }

    private static async Task BridgeProgressBatchChecksAsync()
    {
        var frames = new List<string>();
        using var writer = new SyncCliBridgeProgressWriter(text =>
        {
            var envelope = SyncCliBridgeProtocol.EncodeReply(new("progress", new string('a', 32), true, 0, text, null));
            Require(System.Text.Encoding.Unicode.GetByteCount(envelope + "\0") < SyncCliBridgeProtocol.MaximumPayloadBytes,
                "every progress batch, including escaped text, fits the actual UTF-16 transport limit");
            Require(SyncCliBridgeProtocol.ParseReply(envelope, new string('a', 32)).Text == text,
                "the real progress protocol retained the complete batch");
            frames.Add(text);
        });
        var expected = string.Concat(Enumerable.Range(0, 2_000).Select(index =>
            $"Sync Shark: [2026-10-02T00:00:00Z] file {index} inspected\n"));
        foreach (var line in expected.Split('\n', StringSplitOptions.RemoveEmptyEntries)) writer.WriteLine(line);
        var longLine = new string('\u0001', 20_000);
        writer.Write(longLine);
        await writer.CompleteAsync();
        Require(string.Concat(frames) == expected + longLine, "all file events, order, timestamps and long partial lines survived batching");
        Require(frames.Count < 100, "thousands of lines were not delivered as thousands of blocking peer handshakes");
        var busyLines = 0;
        using var busyWriter = new SyncCliBridgeProgressWriter(text =>
            Interlocked.Add(ref busyLines, text.Count(character => character == '\n')));
        var busyStarted = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() =>
        {
            for (var index = 0; index < 20_000; index++) busyWriter.WriteLine(new string('x', 200));
        }).WaitAsync(TimeSpan.FromSeconds(2));
        await busyWriter.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Require(busyLines == 20_000 && busyStarted.Elapsed < TimeSpan.FromSeconds(2),
            "a sustained backlog is lossless and never sleeps 100ms for each 256 queued lines");
        Console.WriteLine($"PASS busy progress: {busyLines:N0} lines drained in {busyStarted.Elapsed.TotalSeconds:F3}s; sparse coalescing is not a backlog throttle");
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var liveWriter = new SyncCliBridgeProgressWriter(_ => live.TrySetResult());
        liveWriter.WriteLine("one live event");
        await live.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await liveWriter.CompleteAsync();
        var failed = new SyncCliBridgeProgressWriter(_ => throw new IOException("fixture delivery refused"));
        failed.WriteLine("failure");
        try { await failed.CompleteAsync(); throw new Exception("delivery failure was hidden"); }
        catch (IOException) { }
        finally { try { failed.Dispose(); } catch (IOException) { /* Same observed delivery failure. */ } }
        var pressureFailure = new SyncCliBridgeProgressWriter(_ => throw new IOException("fixture pressure refusal"));
        var producer = Task.Run(() =>
        {
            for (var index = 0; index < 2_000; index++) pressureFailure.WriteLine("queued file " + index);
        });
        try { await producer.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("blocked producer failure was hidden"); }
        catch (System.Threading.Channels.ChannelClosedException) { }
        finally { try { pressureFailure.Dispose(); } catch (IOException) { } }
        Console.WriteLine($"PASS progress batching: 2000 complete lines plus long escaped tail in {frames.Count} verified bounded frames; live low-volume delivery; refusal surfaced");
    }

    // Called by the isolated adaptive CLI fixture: exercise the actual parser, dispatcher,
    // settings store and controller, not a direct SaveSettingsAsync shortcut.
    internal static async Task ResourceSettingsChecksAsync(SyncAuthorityRuntimeHost host,
        PilotStateAuthority authority, int ceiling)
    {
        ISyncCliRuntime Factory() => new BorrowedScanRuntime(host, authority);
        foreach (var mode in new[] { "Performance", "Low impact", "Auto" })
        {
            var settings = JsonSerializer.Serialize(new { transfer = new {
                resourceMode = mode, maximumConcurrentFiles = ceiling,
                uploadKiBPerSecond = 0, downloadKiBPerSecond = 0 } });
            var saved = await RunCliAsync(["--cli", "settings", "set", "--settings", settings, "--yes", "--json"], Factory);
            Expect("resource settings CLI saves " + mode, saved, 0);
            var readback = await RunCliAsync(["--cli", "settings", "show", "--json"], Factory);
            Expect("resource settings CLI readback succeeds", readback, 0);
            var data = JsonData(readback, "resource settings readback", "settings show");
            Require(data["transfer"]!["resourceMode"]!.GetValue<string>() == mode &&
                    data["transfer"]!["maximumConcurrentFiles"]!.GetValue<int>() == ceiling &&
                    data["resources"]!["maximumFiles"]!.GetValue<int>() == ceiling,
                "CLI mode and ceiling reach persisted settings and the shared controller");
        }
        foreach (var invalid in new[] {
            "{\"transfer\":{\"resourceMode\":\"Fastish\"}}",
            "{\"transfer\":{\"maximumConcurrentFiles\":1025}}",
            "{\"transfer\":{\"maximumConcurrentFiles\":-1}}" })
        {
            var failed = await RunCliAsync(["--cli", "settings", "set", "--settings", invalid, "--yes", "--json"], Factory);
            Require(failed.ExitCode != 0 && !Envelope(failed, "invalid resource settings")["ok"]!.GetValue<bool>(),
                "invalid CLI resource setting fails with an explicit error");
            var current = await RunCliAsync(["--cli", "settings", "show", "--json"], Factory);
            var data = JsonData(current, "unchanged resource settings", "settings show");
            Require(data["transfer"]!["resourceMode"]!.GetValue<string>() == "Auto" &&
                    data["transfer"]!["maximumConcurrentFiles"]!.GetValue<int>() == ceiling,
                "rejected CLI setting does not replace accepted mode or ceiling");
        }
        var help = await RunCliAsync(["--cli", "capabilities", "--json"], Factory);
        Require(help.ExitCode == 0 && help.StandardOutput.Contains("transfer.resourceMode", StringComparison.Ordinal) &&
                help.StandardOutput.Contains("transfer.maximumConcurrentFiles", StringComparison.Ordinal) &&
                help.StandardOutput.Contains("Performance uses available-memory headroom", StringComparison.Ordinal),
            "CLI capability help documents adaptive modes and adjustable ceiling");
    }

    internal static async Task RunAsync(string root, string? capabilitiesPath = null)
    {
        var rehearsal = SyncCliRegistry.Parse(["--cli", "run", "--pair", "fixture", "--rehearse",
            "--data-root", Path.Combine(root, "isolated"), "--export", Path.Combine(root, "effects.csv"), "--runs", "2",
            "--plan-provenance", Path.Combine(root, "provenance.csv")]);
        Require(rehearsal.Flag("--rehearse") && rehearsal.Value("--runs") == "2" &&
            !SyncCliRegistry.MutatesProductState(rehearsal), "rehearsal parses without live mutation or confirmation");
        Require(SyncCliRegistry.Rows.Single(row => row.Command == "run").Usage.Contains("--rehearse", StringComparison.Ordinal),
            "rehearsal help is in the single capability registry");
        foreach (var command in new[] { "install-startup", "uninstall-startup" })
        {
            var invocation = SyncCliRegistry.Parse(["--cli", "autosync", command]);
            Require(invocation.Command == "autosync " + command, "startup capability parses without opening state or changing registration");
        }
        Require(SyncCliRegistry.Rows.Single(row => row.Command == "queue list").Effect.Contains(
                    "current pairs", StringComparison.Ordinal) &&
                SyncCliRegistry.Rows.Single(row => row.Command == "queue list").Effect.Contains(
                    "retired-pair history stays in Activity", StringComparison.Ordinal) &&
                SyncCliRegistry.Rows.Single(row => row.Command == "attention list").Effect.Contains(
                    "current pending count", StringComparison.Ordinal),
            "The one help/capability registry distinguishes current work from preserved retired history.");
        Require(!SyncCliRegistry.Rows.Single(row => row.Command == "dates report-mismatch").Mutating,
            "date mismatch export is read-only");
        var dates = SyncCliRegistry.Parse(["--cli", "dates", "repair", "--pair", "test", "--dry-run",
            "--from", "2026-10-17T14:00", "--to", "2026-10-17T17:00", "--exclude-list", "exclude.txt"]);
        Require(dates.Value("--from") == "2026-10-17T14:00" && dates.Value("--to") == "2026-10-17T17:00" && dates.Value("--exclude-list") == "exclude.txt",
            "dates repair exposes inclusive local date range");
        PairRemoveRegistryChecks();
        RecoverySelectionRegistryChecks();
        await CliDeadlineChecks.RunAsync();
        Require(SyncCliRegistry.Rows.Single(row => row.Command == "pairs edit").Effect.Contains(
                    "publishes atomically", StringComparison.Ordinal) &&
                SyncCliRegistry.Rows.Single(row => row.Command == "pairs edit").Effect.Contains(
                    "preserves the old pair on refusal", StringComparison.Ordinal),
            "CLI help/capabilities explain the same transactional replacement used by the pair editor.");
        var createdRoot = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        try
        {
            await RouteAndStateFreeChecksAsync();
            RunPresentationChecks();
            await ByteProgressChecksAsync();
            await DetailedFailureChecksAsync();
            var fixture = await BuildReadFixtureAsync(Path.Combine(root, "read"));
            await HelpAndVersionChecksAsync(fixture);
            await StatusChecksAsync(fixture);
            var cleanup = new CleanupFailingRuntime(fixture.LockPath);
            var cleanupResult = await RunCliAsync(["--cli", "status", "--json"], () => cleanup);
            Require(cleanupResult.ExitCode == 6 && cleanup.DisposeCount == 1 &&
                    cleanupResult.StandardOutput.Contains("\"ok\":false", StringComparison.Ordinal) &&
                    !cleanupResult.StandardOutput.Contains("\"ok\":true", StringComparison.Ordinal),
                "a cleanup failure is not hidden behind success and disposal is not replayed");
            await AccountChecksAsync(fixture);
            await PairChecksAsync(fixture);
            await EndpointAuthoringChecksAsync(Path.Combine(root, "endpoint-authoring"));
            if (OperatingSystem.IsWindows())
                await CloudPublicPairAuthoringChecks.RunCliCommandChecksAsync(Path.Combine(root, "cloud-endpoint-authoring"));
            var inventory = await fixture.RunAsync("--cli", "storage", "inventory", "--account", fixture.PrimaryConnectionId, "--json");
            Expect("storage inventory uses the shared retained authority", inventory, 0);
            var inspectedUsage = await fixture.RunAsync("--cli", "storage", "inspect", "--pair", "missing", "--local", @"C:\fixture", "--json");
            Expect("storage inspect refuses a pair without its exact account", inspectedUsage, 2);
            await ActivityChecksAsync(fixture);
            await SettingsChecksAsync(fixture);
            await PlanChecksAsync(fixture);
            await PlanPagingChecksAsync(Path.Combine(root, "plan-paging"));
            await ActivityBytePageChecksAsync(Path.Combine(root, "activity-byte-paging"));
            await UsageChecksAsync(fixture);
            await LocalFolderChecksAsync(fixture, root);
            await UnsupportedFamilyChecksAsync(fixture);
            foreach (var selection in new[] { "[]", "[null]", "[\"duplicate\",\"duplicate\"]", "not-json" })
            {
                var rejected = await fixture.RunAsync("--cli", "run", "batch", "--selection", selection, "--yes", "--json");
                Expect("invalid batch selection rejected before any provider work", rejected, 2);
            }
            var unknownBatch = await fixture.RunAsync("--cli", "run", "batch", "--selection", "[\"no-such-pair\"]", "--yes", "--json");
            Expect("exact missing batch target rejected", unknownBatch, 4);
            await BusyLockChecksAsync(Path.Combine(root, "busy"));
            var runFixture = await BuildRunFixtureAsync(Path.Combine(root, "run"));
            await RunGuardChecksAsync(runFixture);
            await CancellationChecksAsync(runFixture);
            await DiagnosticsCommandChecksAsync(root);
            await FullCliChecksAsync(fixture, capabilitiesPath);
            SyncCliBridgeProtocolChecks();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks = Passed,
                note = "native console host, real WPF startup routing, installed runtime and provider acceptance are not exercised here"
            }));
        }
        finally
        {
            if (createdRoot && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int Passed;

    internal static async Task CheckCompletedMoveRepairAsync(SyncAuthorityRuntimeHost host,
        PilotStateAuthority state, string pairId, bool expectSuccess, bool expectCandidates = true)
    {
        var args = new[] { "--cli", "first-sync", "repair-moves", "--pair", pairId, "--json" };
        var missingConfirmation = await RunCliAsync(args, () => new BorrowedScanRuntime(host, state));
        Expect("move repair requires explicit confirmation before touching files", missingConfirmation, 2);
        var result = await RunCliAsync([.. args, "--yes"], () => new BorrowedScanRuntime(host, state));
        Expect("move repair exposes the real shared recovery outcome", result, expectSuccess ? 0 : 6);
        if (expectCandidates)
            Require(result.StandardOutput.Contains("old.txt", StringComparison.Ordinal) &&
                    result.StandardOutput.Contains("moved.txt", StringComparison.Ordinal) &&
                    result.StandardError.Contains("physical file", StringComparison.Ordinal),
                "move repair retains exact per-file paths and streams actual work, not only a generic status");
        else
        {
            using var response = JsonDocument.Parse(result.StandardOutput);
            Require(response.RootElement.GetProperty("data").GetProperty("items").GetArrayLength() == 0 &&
                    !result.StandardError.Contains("Starting: Verifying physical file", StringComparison.Ordinal),
                "a retired exact candidate produces no new file action or reconstructed history on repeat");
        }
    }

    internal static async Task CheckRetainedScanDispatchAsync(SyncAuthorityRuntimeHost host,
        PilotStateAuthority state, string pairId, bool expectSuccess)
    {
        var result = await RunCliAsync(["--cli", "scan", "--pair", pairId, "--yes", "--json"],
            () => new BorrowedScanRuntime(host, state));
        Expect("scan uses the shared host with a truthful terminal exit", result, expectSuccess ? 0 : 6);
        if (expectSuccess)
        {
            var before = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pairId);
            var prepared = await RunCliAsync(["--cli", "first-sync", "prepare", "--pair", pairId,
                    "--yes", "--json"], () => new BorrowedScanRuntime(host, state));
            Expect("effect-free preparation uses the real shared CLI and complete comparison", prepared, 0);
            var data = JsonData(prepared, "first-sync preparation", "first-sync prepare");
            Require(!data["fileEffectsAuthorized"]!.GetValue<bool>() &&
                    !data["firstSyncApproved"]!.GetValue<bool>() &&
                    data["localScanGenerationId"]!.GetValue<string>().Length > 0 &&
                    data["dropboxScanGenerationId"]!.GetValue<string>().Length > 0 &&
                    data["phases"]!.AsArray().Count > 0 &&
                    prepared.StandardError.Contains("Complete first-sync comparison", StringComparison.Ordinal),
                "preparation reports real phase timings and both immutable inventories without effect authority");
            Require(prepared.StandardError.Contains("Starting: Verifying the selected Dropbox account", StringComparison.Ordinal) &&
                    prepared.StandardError.Contains("Completed: Verifying the selected Dropbox account", StringComparison.Ordinal) &&
                    prepared.StandardError.Contains("Validating the current database schema, receipts", StringComparison.Ordinal) &&
                    prepared.StandardError.Contains("checking duplicate/overlapping roots", StringComparison.Ordinal) &&
                    prepared.StandardError.Contains("feedback delivery is excluded", StringComparison.Ordinal),
                "the real CLI streams specific binding/provider/registration work and separately timed completion before inventory reads");
            var after = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pairId);
            Require(after.InitialReviewApproved == before.InitialReviewApproved &&
                    after.Enabled == before.Enabled && after.PolicyRevision == before.PolicyRevision,
                "preparation does not approve, enable or change the pair policy");
            var confirmationMissing = await RunCliAsync(["--cli", "first-sync", "prepare", "--pair", pairId,
                "--json"], () => new BorrowedScanRuntime(host, state));
            Expect("preparation requires explicit retained-state refresh confirmation", confirmationMissing, 2);
        }
        if (!expectSuccess)
        {
            var problem = (await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pairId);
            Require(problem.Code == "first_sync_scan_failed" && problem.CommandId.Length > 0,
                "CLI scan failure remains retrievable through the same retained Problems projection as the GUI");
            var retained = await RunCliAsync(["--cli", "activity", "list", "--log", "--run", problem.CommandId, "--json"],
                () => new BorrowedScanRuntime(host, state));
            Expect("the failed scan can be retraced through the real CLI retained log", retained, 0);
            Require(retained.StandardOutput.Contains("Controlled scan preparation failure") &&
                    retained.StandardOutput.Contains("HTTP 503") &&
                    !retained.StandardOutput.Contains("scan-fixture-secret") &&
                    !retained.StandardOutput.Contains("scan-fixture-state"),
                "CLI historical scan output preserves the nested failure while redacting synthetic credentials");
        }
    }

    internal static async Task CheckApprovedScanDispatchAsync(SyncAuthorityRuntimeHost host,
        PilotStateAuthority state, string pairId, bool expectSuccess)
    {
        var before = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pairId);
        Require(before.InitialReviewApproved == true, "approved scan proof starts with retained approval");
        var result = await RunCliAsync(["--cli", "scan", "--pair", pairId, "--yes", "--json"],
            () => new BorrowedScanRuntime(host, state));
        Expect("approved pair inventory scan reaches the real effect-free host", result, expectSuccess ? 0 : 6);
        if (expectSuccess)
        {
            var data = JsonData(result, "approved inventory scan", "scan");
            Require(data["localScanGenerationId"]!.GetValue<string>() != "none" &&
                    data["dropboxScanGenerationId"]!.GetValue<string>() != "none" &&
                    result.StandardError.Contains("Preparing to read both endpoint inventories", StringComparison.Ordinal),
                "approved scan returns both retained inventory identities and concrete live preparation detail");
            var refused = await RunCliAsync(["--cli", "first-sync", "prepare", "--pair", pairId, "--yes", "--json"],
                () => new BorrowedScanRuntime(host, state));
            Expect("inventory scan does not reopen first-sync review", refused, 6);
        }
        var after = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pairId);
        Require(JsonSerializer.Serialize(after) == JsonSerializer.Serialize(before) &&
                !(await host.LoadRetainedFirstSyncProblemsAsync()).Any(item => item.PairId == pairId),
            "approved scan success or failure preserves exact approval/policy and never becomes another first-sync review problem");
    }

    internal static async Task CheckBoundedRunDispatchAsync(SyncAuthorityRuntimeHost host,
        PilotStateAuthority state, string pairId, int expectedDownloads)
    {
        var result = await RunCliAsync(["--cli", "run", "--pair", pairId, "--yes", "--json"],
            () => new BorrowedScanRuntime(host, state));
        Expect("bounded observed work reaches the real run dispatcher and host", result, 0);
        var data = JsonData(result, "bounded observed run", "run");
        Require(data["downloaded"]!.GetValue<int>() == expectedDownloads &&
                data["uploaded"]!.GetValue<int>() == 0 && !data["stopped"]!.GetValue<bool>() &&
                data["failed"]!.GetValue<int>() == 0 && data["conflicts"]!.GetValue<int>() == 0 &&
                data["blocked"]!.GetValue<int>() == 0,
            "a changing producer does not convert verified observed files into a whole-run failure");
        Require(SyncCliRegistry.Rows.Single(row => row.Command == "run").Effect.Contains(
                "later arrivals wait for the next sync", StringComparison.Ordinal),
            "CLI help/capabilities document bounded job completion without weakening move freshness");
    }

    internal static async Task<(int Exit, string Output, string Progress)> CheckRetainedRunVerificationDispatchAsync(
        SyncAuthorityRuntimeHost host, PilotStateAuthority state, string runId,
        CancellationToken cancellationToken = default)
    {
        var result = await RunCliAsync(["--cli", "run", "verify", "--run", runId, "--yes", "--json"],
            () => new BorrowedScanRuntime(host, state), cancellationToken);
        Require(Envelope(result, "cold retained-run verification")["command"]!.GetValue<string>() == "run verify",
            "cold verification is dispatched through the actual CLI registry and returns its own command envelope");
        Require(result.StandardError.Contains("Recovery: opening retained state for run " + runId, StringComparison.Ordinal),
            "the CLI streams concrete retained-origin work before recovery admission, including safe failures");
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    internal static async Task CheckRetainedCommandAttentionDispatchAsync(SyncAuthorityRuntimeHost host,
        PilotStateAuthority state, string pairId, string commandId, string cause, bool expected)
    {
        var result = await RunCliAsync(["--cli", "attention", "list", "--json"],
            () => new BorrowedScanRuntime(host, state));
        Expect("retained command failure is read through the real CLI registry", result, 0);
        var items = JsonData(result, "retained command attention", "attention list")["items"]!.AsArray();
        var matches = items.Where(item => item?["pairId"]?.GetValue<string>() == pairId &&
                                         item?["runId"]?.GetValue<string>() == commandId).ToArray();
        Require(expected
                ? matches.Length == 1 && matches[0]!["isProblem"]!.GetValue<bool>() &&
                  matches[0]!["description"]!.GetValue<string>().Contains(cause, StringComparison.Ordinal)
                : matches.Length == 0,
            "Direct CLI attention preserves the exact current cause/run or removes only its resolved issue.");
    }

    private sealed class BorrowedScanRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority state)
        : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => state;
        public override string Mode => "scan-contract-fixture";
        // The enclosing retention fixture owns the host and exercises restart separately.
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UpdateInputRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority state,
        Func<HttpMessageHandler> input) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => state;
        protected override SyncPublicUpdates CreatePublicUpdateClient() => new(input());
        public override string Mode => "scan-contract-fixture";
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UpdateInputHandler(Func<HttpRequestMessage, HttpResponseMessage> input)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(input(request));
        }
    }

    private static async Task ByteProgressChecksAsync()
    {
        var timestamp = new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);
        Require(SyncCliRegistry.FormatProgress("Writing inventory batch 2 of 3", timestamp) ==
                "Sync Shark: [2026-09-29T15:00:00.0000000+00:00] Writing inventory batch 2 of 3",
            "CLI human progress has an exact UTC producer timestamp without changing its detail");
        var events = new List<SyncByteProgress>();
        var bytes = System.Text.Encoding.UTF8.GetBytes("real download and hash readback");
        using var context = SyncByteProgressStream.Begin("fixture.txt", item =>
        { events.Add(item); return Task.CompletedTask; });
        using var destination = new MemoryStream();
        await using (var progress = new SyncByteProgressStream(destination, "Downloading to verified staging",
            bytes.Length, readBackPhase: "Verifying downloaded content"))
        {
            await progress.StartedAsync();
            await progress.WriteAsync(bytes);
            progress.Position = 0;
            var digest = await DropboxContentHash.ComputeAsync(progress);
            Require(!string.IsNullOrEmpty(digest), "download readback computes a real content digest");
        }
        Require(events.Any(item => item.Phase == "Downloading to verified staging" && item.Bytes == bytes.Length) &&
                events.Any(item => item.Phase == "Verifying downloaded content" && item.Bytes == bytes.Length) &&
                events.All(item => item.Bytes <= bytes.Length && item.Path == "fixture.txt") && destination.CanRead,
            "download and hash readback have separate accurate byte counters; progress never closes the owned stream");

        var batch = new SyncTransferBatchTracker();
        batch.Register(["a", "b"]);
        SyncAuthorityLiveAction Action(string id, SyncActivityPhase phase, string stage = "Reading bytes for upload") =>
            new(id, id + ".txt", "Upload", stage, "Exact file detail", DateTimeOffset.UtcNow, "File", "TXT",
                SyncAuthorityOperationKind.CreateRemote, phase);
        batch.Observe(Action("a", SyncActivityPhase.Running) with { Progress = new(10, 10, "bytes processed", 1) });
        var running = batch.Observe(Action("b", SyncActivityPhase.Running));
        Require(running.InFlight == 2 && running.Queued == 0 && running.Active.Count == 2 &&
            running.Describe().Contains("a.txt") && running.Describe().Contains("b.txt"),
            "concurrent progress names every active file separately from queued counts");
        batch.Observe(Action("a", SyncActivityPhase.Completed, "Completed"));
        batch.Observe(Action("a", SyncActivityPhase.Completed, "Completed"));
        var terminal = batch.Observe(Action("b", SyncActivityPhase.Blocked, "Not attempted"));
        Require(terminal is { Completed: 1, CompletedFiles: 1, Deferred: 1, Failed: 0, InFlight: 0,
                PayloadBytesProcessed: 10 } && terminal.Describe(0).Contains("payload processed"),
            "duplicate completion never double-counts and an unattempted dependency is deferred, not a failed transfer");
    }

    internal static void RunPresentationChecks()
    {
        CheckActionLifetimePresentation(new SyncActivityRetentionWindow(true,365,1000000,1073741824,7,33554432,
            0,0,0,0,0,0,0,null,null,false,false) {
            Actions=new SyncActionJournalStatus(3,4096,2,3072,false,true,1,null) });
        EncodedResponseChecks();
        var normalReceipt = new SyncCliRunData("receipt-pair", "Receipt fixture", 1, 2, 3, 0, 1, false)
        { RunId = "retained-receipt-run", Failed = 1, NotAttempted = 4, NeedsVerification = 2 };
        var renderedReceipt = SyncCli.RenderHuman(normalReceipt);
        Require(renderedReceipt.Contains("failed=1 not-attempted=4 needs-verification=2", StringComparison.Ordinal) &&
                renderedReceipt.Contains("activity list --log --run retained-receipt-run", StringComparison.Ordinal),
            "normal human receipt preserves unresolved counters and the exact retained operation-log command");
        Require(!SyncCli.RenderHuman(normalReceipt with { RunId = null }).Contains("Retained run:", StringComparison.Ordinal),
            "a pre-admission receipt without a run does not invent a retained run identity");
        var allOutcomes = Enum.GetValues<SyncDecisionKind>()
            .Select(kind => new SyncDecision(kind.ToString(), kind, "Counter fixture")).ToArray();
        var appliedCount = new SyncRunResult("counter-fixture", allOutcomes);
        Require(appliedCount.AppliedItems == 5 && appliedCount.Uploaded == 1 && appliedCount.Downloaded == 1,
            "applied count includes transfers, moves/recovery, archives and online-only actions, not unchanged, failed or uncertain outcomes");
        Require(new SyncRunResult("counter-fixture",
            [new("deferred", SyncDecisionKind.Recovered, "Not attempted") { NotAttempted = true }]).AppliedItems == 0,
            "an unattempted action is not counted as applied");
        Require((appliedCount with { TransferTotals = new SyncRunTransferTotals(["a", "b"], ["c", "d"]) }).AppliedItems == 7,
            "applied count preserves coordinated transfer totals as well as successful non-transfer actions");
        Require(!JsonSerializer.Serialize(appliedCount).Contains("\"AppliedItems\"", StringComparison.Ordinal),
            "shared applied projection does not change the retained run serialization");
        var largeRetry = new SyncRunRetryPlan("large-retained-run", "pair", null,
            Enumerable.Range(0, 5_127).Select(index => new SyncRunRetryItem("folder/file-" + index,
                "Not attempted", new string('x', 4096), true)).ToArray());
        var pageOffset = 0;
        var retainedPaths = new HashSet<string>();
        while (pageOffset < largeRetry.Items.Count)
        {
            var page = SyncCliRunRetryPlanData.Page(largeRetry, 200, pageOffset);
            if (page.Items.Count == 0 || !page.Items.All(item => retainedPaths.Add(item.Path)) ||
                    page.TotalItems != 5_127 || page.NotAttempted != 5_127 ||
                    page.HasMore != (pageOffset + page.Returned < 5_127))
                throw new InvalidDataException("Retry page lost progress, counts or exact membership.");
            var envelope = JsonSerializer.Serialize(new SyncCliEnvelope(SyncCli.SchemaVersion, "run retry-plan", true, page, null));
            var bridgeBytes = JsonSerializer.SerializeToUtf8Bytes(new { standardOutput = envelope, standardError = "", exitCode = 0 });
            if (bridgeBytes.Length >= SyncCliBridgeProtocol.MaximumPayloadBytes)
                throw new InvalidDataException("Byte-bounded retry page exceeded the real nested transport limit.");
            pageOffset += page.Returned;
        }
        Require(retainedPaths.Count == largeRetry.Items.Count,
            "all 5,127 long-detail retry records remain reachable once, with complete counts, within the unchanged nested transport cap");
        var invocation = SyncCliParser.Parse(["--cli", "pairs", "list"]);
        var legacy = new SyncCliPairData("pair-text", "Text fixture", "Dropbox", "account-d",
            @"C:\fixture-left", "/legacy-right", false, 0, "TwoWay", 1, false, true, false, 0, 0, false);
        var local = new SyncEndpointRecord(SyncEndpointKind.Local, @"C:\fixture-left", 1, 11);
        var localRight = new SyncEndpointRecord(SyncEndpointKind.Local, @"C:\fixture-right", 1, 12);
        var drive = new SyncEndpointRecord(SyncEndpointKind.GoogleDrive, Provider: SyncProviders.GoogleDrive,
            ConnectionId: "account-g", NamespaceId: "namespace-g", FolderId: "drive-root", FolderPath: "/drive-root");
        var dropbox = new SyncEndpointRecord(SyncEndpointKind.Dropbox, Provider: "Dropbox",
            ConnectionId: "account-d", NamespaceId: "namespace-d", FolderId: "id:dropbox-root", FolderPath: "/dropbox-root");
        SyncCliPairData Explicit(SyncEndpointRecord left, SyncEndpointRecord right) => legacy with
        { LocalRoot = "DO-NOT-USE-LEGACY-LOCAL", DropboxRoot = "DO-NOT-USE-LEGACY-CLOUD",
            EndpointDocument = new(SyncEndpointSchema.SymmetricEndpoints, left, right) };
        var cases = new[]
        {
            (legacy, "left: Local: C:\\fixture-left", "right: Dropbox: /legacy-right", "account=account-d"),
            (legacy with { Provider = SyncProviders.GoogleDrive, ConnectionId = "account-g" },
                "left: Local: C:\\fixture-left", "right: Google Drive: /legacy-right", "account=account-g"),
            (Explicit(local, localRight), "left: Local: C:\\fixture-left", "right: Local: C:\\fixture-right", ""),
            (Explicit(drive, localRight), "left: Google Drive: /drive-root", "right: Local: C:\\fixture-right", "account=account-g"),
            (Explicit(dropbox, drive), "left: Dropbox: /dropbox-root", "right: Google Drive: /drive-root", "account=account-g"),
            (Explicit(drive, drive with { ConnectionId = "account-g2", NamespaceId = "namespace-g2",
                FolderId = "drive-root-2", FolderPath = "/drive-root-2" }),
                "left: Google Drive: /drive-root", "right: Google Drive: /drive-root-2", "account=account-g2")
        };
        foreach (var (pair, left, right, account) in cases)
        foreach (var value in new object[] { pair, new SyncCliPairListData(1, [pair]) })
        {
            var output = SyncCli.RenderHuman(invocation, value);
            Require(output.Contains(left, StringComparison.Ordinal) && output.Contains(right, StringComparison.Ordinal) &&
                    output.IndexOf(left, StringComparison.Ordinal) < output.IndexOf(right, StringComparison.Ordinal) &&
                    output.Contains(account, StringComparison.Ordinal) && !output.Contains("DO-NOT-USE", StringComparison.Ordinal),
                "list and show render the same ordered endpoints and actual account, never stale compatibility roots");
            if (pair.EndpointDocument is { Left.Kind: SyncEndpointKind.GoogleDrive, Right.Kind: SyncEndpointKind.GoogleDrive })
                Require(output.Contains("account=account-g ", StringComparison.Ordinal) ||
                        output.Contains("account=account-g" + Environment.NewLine, StringComparison.Ordinal),
                    "same-provider pairs retain the first account as well as the second");
        }
        foreach (var invalid in new[]
        {
            new SyncEndpointDocument(999, local, drive),
            new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints, null, drive),
            new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints, local with { LocalRootFileId = null }, drive),
            new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints, local, drive with { Provider = "Dropbox" }),
            new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints, local, local)
        })
        foreach (var value in new object[] { legacy with { EndpointDocument = invalid },
                     new SyncCliPairListData(1, [legacy with { EndpointDocument = invalid }]) })
        {
            var output = SyncCli.RenderHuman(invocation, value);
            Require(output.Contains("endpoints unavailable:", StringComparison.Ordinal) &&
                    !output.Contains("/legacy-right", StringComparison.Ordinal),
                "malformed explicit endpoint metadata never falls back to a different legacy pair");
        }
        var context = new SyncEndpointContext("pair-text", "Text fixture",
            new(SyncPairSide.Left, SyncEndpointKind.GoogleDrive, SyncAuthorityCloudSides.LogicalRight, SyncProviders.GoogleDrive, "account-g"),
            new(SyncPairSide.Right, SyncEndpointKind.Local, SyncAuthorityCloudSides.LogicalLeft, LocalPath: @"C:\fixture-right"));
        var uploadEffect = "Copy to " + SyncEndpointPresentation.PortTitle(context, SyncAuthorityCloudSides.LogicalRight);
        var downloadEffect = "Copy to " + SyncEndpointPresentation.PortTitle(context, SyncAuthorityCloudSides.LogicalLeft);
        var plan = new SyncCliPlanData("pair-text", "Text fixture", true, true, 5, 2, 3, 0, 2, false,
            [new("upload.txt", "Local only", "File", "TXT", "Upload", uploadEffect, 1, null, null, null),
             new("download.txt", "Dropbox only", "File", "TXT", "Download", downloadEffect, null, 1, null, null)])
            { PresentationContext = context };
        var renderedPlan = SyncCli.RenderHuman(invocation, plan);
        Require(renderedPlan.Contains("only on Right — This computer=2", StringComparison.Ordinal) &&
                renderedPlan.Contains("only on Left — Google Drive=3", StringComparison.Ordinal) &&
                renderedPlan.Contains("Copy to Left — Google Drive  upload.txt", StringComparison.Ordinal) &&
                renderedPlan.Contains("Copy to Right — This computer  download.txt", StringComparison.Ordinal) &&
                !renderedPlan.Contains("Dropbox", StringComparison.Ordinal),
            "reversed plans map physical inventory counts and actions to the correct ordered endpoint without swapping effects");
        var wire = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var wireItems = JsonNode.Parse(wire)!["items"]!.AsArray();
        Require(wire.Contains("\"localOnlyItems\":2", StringComparison.Ordinal) &&
                wire.Contains("\"dropboxOnlyItems\":3", StringComparison.Ordinal) &&
                !wire.Contains("presentationContext", StringComparison.Ordinal) &&
                wireItems[0]!["effect"]!.GetValue<string>() == uploadEffect &&
                wireItems[1]!["effect"]!.GetValue<string>() == downloadEffect,
            "human presentation leaves established JSON keys, values and recommendation payload unchanged");
        Require(SyncCli.RenderHuman(invocation, plan with { HasCompleteInventory = false }).Contains(
                "inventory of both endpoints", StringComparison.Ordinal), "unscanned plans do not claim a local/Dropbox pair");
        var movePlan = plan with { Items = [new("new.txt", "Dropbox only", "File", "TXT", "MoveLocal",
            "old.txt → new.txt", null, 1, null, null)] };
        var moveText = SyncCli.RenderHuman(invocation, movePlan);
        Require(moveText.Contains("Move on Right — This computer", StringComparison.Ordinal) &&
                moveText.Contains("old.txt → new.txt", StringComparison.Ordinal),
            "human CLI move review shows the actual destination endpoint and old/new paths");
        var updatePlan = plan with
        {
            LocalUpdatedItems = 1, DropboxUpdatedItems = 2,
            Items = [new("upload-update.txt", SyncBootstrapActions.LocalUpdatedGroup, "File", "TXT", "Upload",
                         "Update on remote", 2, 1, null, null),
                     new("download-update.txt", SyncBootstrapActions.RemoteUpdatedGroup, "File", "TXT", "Download",
                         "Update on local", 1, 2, null, null)]
        };
        var updateText = SyncCli.RenderHuman(invocation, updatePlan);
        Require(updateText.Contains("updated on Right — This computer=1", StringComparison.Ordinal) &&
                updateText.Contains("updated on Left — Google Drive=2", StringComparison.Ordinal) &&
                updateText.Contains("Update on Left — Google Drive  upload-update.txt", StringComparison.Ordinal) &&
                updateText.Contains("Update on Right — This computer  download-update.txt", StringComparison.Ordinal) &&
                !updateText.Contains("Dropbox", StringComparison.Ordinal),
            "baseline updates retain their source counts and actual destination in reversed endpoint plans");
        var updateWire = JsonSerializer.Serialize(updatePlan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(updateWire.Contains("\"localUpdatedItems\":1", StringComparison.Ordinal) &&
                updateWire.Contains("\"dropboxUpdatedItems\":2", StringComparison.Ordinal) &&
                updateWire.Contains("\"localOnlyItems\":2", StringComparison.Ordinal) &&
                updateWire.Contains("\"dropboxOnlyItems\":3", StringComparison.Ordinal),
            "baseline update counts extend the CLI contract without relabeling one-sided-only counts");
        Require(SyncBootstrapActions.TransferKind(SyncBootstrapActions.LocalUpdatedGroup) == SyncAuthorityOperationKind.UpdateRemote &&
                SyncBootstrapActions.TransferKind(SyncBootstrapActions.RemoteUpdatedGroup) == SyncAuthorityOperationKind.UpdateLocal &&
                !SyncBootstrapActions.IsActionable("Local newer") &&
                !SyncBootstrapActions.IsActionable("Approximately equal time"),
            "shared baseline action policy admits updates but not timestamp-only conflicts");
        Console.WriteLine("PASS CLI ordered endpoint presentation success/rejection and unchanged JSON contract");
    }

    private static void EncodedResponseChecks()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        bool Fits(object value, string command)
        {
            var json = JsonSerializer.Serialize(new SyncCliEnvelope(SyncCli.SchemaVersion, command, true, value, null),
                options) + Environment.NewLine;
            return TextFits(json) && TextFits(SyncCli.RenderHuman(value).TrimEnd('\r', '\n') + Environment.NewLine);
        }
        static bool TextFits(string text)
        {
            var reply = SyncCliBridgeProtocol.EncodeReply(new SyncCliBridgeReply("result", new string('0', 32), true, 0,
                text, null));
            return reply.Length < SyncCliBridgeProtocol.MaximumPayloadBytes &&
                System.Text.Encoding.Unicode.GetByteCount(reply + "\0") <= SyncCliBridgeProtocol.MaximumPayloadBytes * 2;
        }
        var escaped = string.Concat(Enumerable.Repeat("日本語\"\\\n", 120));
        var rows = Enumerable.Range(0, 200).Select(index => new SyncCliPlanItemData(
            $"folder/{index:D3}-{escaped}.txt", "Dropbox only", "File", "TXT", "Download", escaped,
            null, 1, null, null)).ToArray();
        SyncCliPlanData Plan(SyncCliPlanItemData[] items) => new("exact-pair", "Encoded fixture", true, true,
            rows.Length, 0, rows.Length, 0, items.Length, items.Length < rows.Length, items) { Limit = 200 };
        var fitted = SyncCli.FitResponseItems(rows, Plan, "plan");
        Require(fitted.Returned is > 0 and < 200 && fitted.HasMore && fitted.TotalItems == 200 &&
                fitted.Items.SequenceEqual(rows.Take(fitted.Returned)) && Fits(fitted, "plan") &&
                !Fits(Plan(rows.Take(fitted.Returned + 1).ToArray()), "plan"),
            "escaped Unicode/quotes/backslashes return the largest exact ordered prefix within both actual JSON and human replies");
        var oversizedRow = rows[0] with { RelativePath = new string('界', 70_000) };
        var rowRefused = false;
        try { SyncCli.FitResponseItems(new[] { oversizedRow }, Plan, "plan"); }
        catch (SyncCliOperationException error) { rowRefused = error.Message.Contains("No path was truncated"); }
        Require(rowRefused, "one unrepresentable plan row explicitly refuses rather than truncating a path or returning an empty continuation loop");

        var pair = SyncPair.FromPilot(new PilotSettings(@"C:\receipt-fixture", 1, 2, "/Receipt", "id:receipt"),
            "Encoded receipt", "namespace-fixture");
        var decisions = Enumerable.Range(0, 250).Select(index => new SyncDecision($"exact/{index:D3}.txt",
            index < 240 ? SyncDecisionKind.Downloaded : SyncDecisionKind.Blocked, escaped + escaped)).ToArray();
        var source = new SyncBootstrapActionResult(pair, new SyncRunResult(pair.Id, decisions), 240, 0, false);
        var receipt = SyncCliOperationsBase.CompactFirstSyncResult(source, "exact-retained-run");
        Require(receipt.RunId == "exact-retained-run" && receipt.PairId == pair.Id && receipt.AppliedItems == 240 &&
                receipt.Downloaded == 240 && receipt.Blocked == 10 && !receipt.FirstSyncComplete &&
                receipt.TotalFiles == 250 && receipt.Returned is > 0 and < SyncCli.ActivityDefaultLimit &&
                receipt.Returned == receipt.Files!.Count && receipt.HasMore &&
                receipt.Files.SequenceEqual(decisions.OrderByDescending(item => item.Kind == SyncDecisionKind.Blocked)
                    .Take(receipt.Returned)) && receipt.Failed == 10 && receipt.NotAttempted == 0 &&
                Fits(receipt, "first-sync apply"),
            "production terminal compaction preserves exact run/pair/all counters and unmodified sample rows despite nested escaping expansion; " +
            $"applied={receipt.AppliedItems}, downloaded={receipt.Downloaded}, blocked={receipt.Blocked}, total={receipt.TotalFiles}, returned={receipt.Returned}, limit={SyncCli.ActivityDefaultLimit}, fits={Fits(receipt, "first-sync apply")}");
        var noSample = SyncCliOperationsBase.CompactFirstSyncResult(source with
        { Result = new SyncRunResult(pair.Id, [new("exact/oversized.txt", SyncDecisionKind.Downloaded, new string('界', 70_000))]), AppliedItems = 1 },
            "exact-retained-run");
        Require(noSample.RunId == "exact-retained-run" && noSample.AppliedItems == 1 && noSample.Downloaded == 1 &&
                noSample.TotalFiles == 1 && noSample.Returned == 0 && noSample.HasMore && Fits(noSample, "first-sync apply"),
            "a post-effect receipt can omit an oversized optional sample without losing successful effects or inventing a transport failure");
        var longName = SyncCliOperationsBase.CompactFirstSyncResult(source with { Pair = pair with { Name = new string('界', 70_000) } },
            "exact-retained-run");
        Require(longName.RunId == "exact-retained-run" && longName.PairId == pair.Id && longName.AppliedItems == 240 &&
                longName.PairName == "[display name omitted; open the retained run]" && Fits(longName, "first-sync apply"),
            "unrepresentable optional display name never displaces a completed effect's exact retained identity and counters");
        Require(source.Result.Decisions.Count == 250 && source.Result.Decisions.SequenceEqual(decisions),
            "presentation bounds never mutate the full retained outcome source");
        var activeRows = Enumerable.Range(0, 256).Select(index => new SyncActiveTransfer(
            $"operation-{index:D3}", $"exact/{index:D3}-{escaped}.txt", "Downloading", escaped)).ToArray();
        var batch = new SyncTransferBatchProgress(1601, 256, 6339, 0, 0, 0, 6100, 123456789, 10000, activeRows);
        var action = new SyncAuthorityLiveAction("current-operation", "exact/current.txt", "Download",
            "Downloading", "Reading the current verified response", DateTimeOffset.UtcNow, Batch: batch);
        var active = new SyncActiveRunSnapshot("exact-active-run", "run_pair", pair.Id,
            DateTimeOffset.UtcNow, false, false, new(pair, "Running", null, null, null, action));
        foreach (var command in new[] { "jobs status", "jobs watch" })
        {
            var job = SyncCliJobData.Project(active, true, command);
            var shown = job.Job!.Progress!.Action!.Batch!;
            Require(job.ActiveRowsTotal == 256 && job.ActiveRowsReturned is > 0 and < 256 &&
                    job.ActiveRowsHasMore && shown.Active.SequenceEqual(activeRows.Take(job.ActiveRowsReturned)) &&
                    (shown with { Active = activeRows }) == batch && job.Job.Id == active.Id &&
                    job.Job.Progress.Action.RelativePath == action.RelativePath && Fits(job, command),
                command + " preserves current identity/path and every aggregate while fitting an explicitly counted exact active prefix");
            var oneMore = job with { ActiveRowsReturned = job.ActiveRowsReturned + 1,
                Job = job.Job with { Progress = job.Job.Progress with {
                    Action = job.Job.Progress.Action with { Batch = shown with {
                        Active = activeRows.Take(job.ActiveRowsReturned + 1).ToArray() } } } } };
            Require(!Fits(oneMore, command), command + " returns the largest prefix, not an arbitrary three-file sample");
            var terminal = SyncCliJobData.Project(active with { Progress = active.Progress! with {
                Result = source.Result } }, false, command, requestedJobFinished: true);
            Require(terminal.RequestedJobFinished && terminal.ResultSummary is {
                    DecisionCount: 250, Downloaded: 240, Failed: 10 } &&
                    terminal.Job!.Progress!.Result is null && Fits(terminal, command) &&
                    source.Result.Decisions.Count == 250 && batch.Active.Count == 256,
                command + " terminal projection retains all outcome counts without changing the complete source result or scheduler active set");
            var terminalText = SyncCli.RenderHuman(terminal);
            Require(terminalText.Contains("last observed snapshot, not proof of its terminal outcome") &&
                    terminalText.Contains("activity list --log --run " + active.Id),
                command + " distinguishes ended observation from final outcome and links the exact retained run log");
            var optionalHuge = SyncCliJobData.Project(active with { Progress = active.Progress! with {
                Action = action with { Batch = batch with { Active = [activeRows[0] with {
                    RelativePath = new string('界', 70000) }] } } } }, true, command);
            Require(optionalHuge.ActiveRowsTotal == 1 && optionalHuge.ActiveRowsReturned == 0 &&
                    optionalHuge.ActiveRowsHasMore && Fits(optionalHuge, command),
                command + " explicitly omits an unrepresentable optional active row rather than losing the current job");
            var mandatoryRefused = false;
            try { SyncCliJobData.Project(active with { Progress = active.Progress! with {
                Action = action with { RelativePath = new string('界', 70000) } } }, true, command); }
            catch (SyncCliOperationException error) { mandatoryRefused = error.Message.Contains("No identity or path was truncated"); }
            Require(mandatoryRefused, command + " refuses oversized mandatory current context without identity truncation");
        }
        var partialRefused = false;
        try { SyncCliOperationsBase.CheckedFirstSyncResult(source, "exact-retained-run"); }
        catch (SyncCliReviewException error)
        {
            partialRefused = error.ResultData is SyncCliFirstSyncResultData partial &&
                partial.RunId == "exact-retained-run" && partial.AppliedItems == 240 && partial.Failed == 10;
        }
        Require(partialRefused, "actual CLI result boundary rejects partial success while preserving exact completed counts and failure receipt");
        var complete = source with { FirstSyncComplete = true, Result = new SyncRunResult(pair.Id,
            [new("exact/complete.txt", SyncDecisionKind.Downloaded, "Verified")]), AppliedItems = 1 };
        Require(SyncCliOperationsBase.CheckedFirstSyncResult(complete, "complete-run").Downloaded == 1,
            "the same CLI result boundary accepts fully completed work");
    }

    private static async Task DetailedFailureChecksAsync()
    {
        const string privateTransport = "unlabelled-private-provider-fixture";
        foreach (var providerFailure in new Exception[]
                 {
                     new DropboxEffectUncertainException("Dropbox outcome is uncertain. Refresh before retrying.",
                         new IOException(privateTransport, new HttpRequestException(privateTransport))),
                     new GoogleDriveEffectUncertainException("Google outcome is uncertain. Refresh before retrying.",
                         new InvalidDataException(privateTransport, new HttpRequestException(privateTransport))),
                     new HttpRequestException(privateTransport, new IOException(privateTransport,
                         new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)),
                         HttpStatusCode.BadGateway)
                 })
        {
            var safe = SyncCli.DescribeException(providerFailure);
            Require(!safe.Contains(privateTransport, StringComparison.Ordinal) &&
                    safe.Contains("HttpRequestException", StringComparison.Ordinal) &&
                    (providerFailure is HttpRequestException ? safe.Contains("HTTP 502", StringComparison.Ordinal) &&
                        safe.Contains("HostNotFound", StringComparison.Ordinal) :
                        safe.Contains("Refresh before retrying", StringComparison.Ordinal)),
                "provider transport details never cross CLI/GUI diagnostics; safe cause types/status and action remain");
        }
        foreach (var apiFailure in new HttpRequestException[]
                 {
                     new DropboxApiException(HttpStatusCode.Conflict,
                         "This file is locked by another editor. access_token=fixture-api-private-value"),
                     new GoogleDriveApiException(HttpStatusCode.Forbidden,
                         "This file is locked by another editor. access_token=fixture-api-private-value",
                         "insufficientFilePermissions")
                 })
        {
            var safe = SyncCli.DescribeException(apiFailure);
            Require(safe.Contains("This file is locked by another editor.", StringComparison.Ordinal) &&
                    safe.Contains($"HTTP {(int)apiFailure.StatusCode!.Value}", StringComparison.Ordinal) &&
                    !safe.Contains("fixture-api-private-value", StringComparison.Ordinal) &&
                    !safe.Contains("transport Unknown", StringComparison.Ordinal),
                "typed safe provider causes remain actionable without credentials or a false transport label");
            var privateInner = SyncCli.DescribeException(new DropboxEffectUncertainException(
                "The outcome is uncertain. Refresh before retrying.", apiFailure));
            Require(!privateInner.Contains("This file is locked by another editor.", StringComparison.Ordinal),
                "a typed API cause inside a private uncertain-effect boundary remains withheld");
        }
        var failure = new IOException("Outer cause " + new string('x', 700), new InvalidDataException(
            "Nested retained cause password=fixture-private-value Bearer fixture-bearer-value " +
            "https://example.test/operation?access_token=fixture-query-value&state=fixture-state-value"));
        var described = SyncCli.DescribeException(failure);
        Require(described.Length > 700 && described.Contains("Nested retained cause") &&
                described.Contains("InvalidDataException") && described.Contains("https://example.test/operation") &&
                !described.Contains("fixture-private-value") && !described.Contains("fixture-bearer-value") &&
                !described.Contains("fixture-query-value") && !described.Contains("fixture-state-value") &&
                !described.Contains('?'),
            "the shared safe diagnostic retains nested cause beyond character400 without secrets, redirects or raw stacks");
        foreach (var json in new[] { false, true })
        {
            var result = await RunCliAsync(json ? ["--cli", "status", "--json"] : ["--cli", "status"],
                () => throw failure);
            Expect("actual dispatcher preserves detailed safe failure", result, 6);
            Require(result.StandardError.Contains("Nested retained cause") &&
                    !result.StandardError.Contains("fixture-private-value") &&
                    !result.StandardError.Contains("fixture-bearer-value") &&
                    !result.StandardError.Contains("fixture-query-value"),
                "actual human/JSON failure stderr does not reclip the safe cause or expose credentials");
            if (json) Require(Envelope(result, "detailed safe failure")["error"]!["message"]!
                    .GetValue<string>().Contains("Nested retained cause"),
                "the one terminal JSON failure receipt retains the same detailed cause");
            else Require(result.StandardOutput.Length == 0, "human failure preserves clean stdout");
        }
    }

    private sealed class CleanupFailingRuntime : SyncCliOperationsBase
    {
        private readonly PilotInstanceLock _instance;
        private readonly HttpClient _http = new(new NoNetworkHandler());
        private readonly SyncAuthorityRuntimeHost _host;
        internal CleanupFailingRuntime(string lockPath)
        {
            _instance = PilotInstanceLock.Acquire(lockPath);
            _host = new SyncAuthorityRuntimeHost(_instance.StateAuthority, _http);
        }
        protected override SyncAuthorityRuntimeHost Host => _host;
        protected override PilotStateAuthority StateAuthority => _instance.StateAuthority;
        public override string Mode => "exclusive-product-lock";
        internal int DisposeCount { get; private set; }
        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            try { await base.DisposeAsync(); }
            finally { _http.Dispose(); _instance.Dispose(); }
            throw new IOException("Fixture cleanup failure after the real runtime released its state.");
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("CLI check failed: " + label);
        Passed++;
        Console.WriteLine("PASS " + label);
    }

    private static void RequireRuntimeAnnouncementOnly(CliResult result, string command)
    {
        using var lines = new StringReader(result.StandardError);
        var line = lines.ReadLine() ?? "";
        const string prefix = "Sync Shark: [";
        var end = line.IndexOf(']');
        var hasTimestamp = line.StartsWith(prefix, StringComparison.Ordinal) && end > prefix.Length &&
            DateTimeOffset.TryParseExact(line[prefix.Length..end], "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timestamp) && timestamp.Offset == TimeSpan.Zero;
        Require(hasTimestamp && line.EndsWith(
                $"] Execution runtime: direct; authority process {Environment.ProcessId}.",
                StringComparison.Ordinal) && lines.ReadLine() is null,
            command + " emits only its timestamped exact runtime/process announcement on standard error");
    }

    private static async Task<CliResult> RunCliAsync(string[] args, Func<ISyncCliRuntime> factory,
        CancellationToken cancellationToken = default)
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var exitCode = await SyncCli.ExecuteAsync(args, standardOutput, standardError, factory,
            cancellationToken);
        return new CliResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static void Expect(string label, CliResult result, int expectedExitCode)
    {
        Require(result.ExitCode == expectedExitCode,
            label + " [expected exit " + expectedExitCode + ", actual " + result.ExitCode + ": " +
            result.StandardError.Trim() + "]");
    }

    private static JsonObject Envelope(CliResult result, string label)
    {
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(lines.Length == 1, label + ": standard output is exactly one JSON object");
        var envelope = JsonNode.Parse(lines[0])!.AsObject();
        Require(envelope["schemaVersion"]!.GetValue<int>() == 1, label + ": schemaVersion is 1");
        return envelope;
    }

    private static JsonObject JsonData(CliResult result, string label, string command)
    {
        var envelope = Envelope(result, label);
        Require(envelope["command"]!.GetValue<string>() == command, label + ": command name is " + command);
        Require(envelope["ok"]!.GetValue<bool>(), label + ": ok is true");
        Require(envelope["error"] is null, label + ": error is null on success");
        return envelope["data"]!.AsObject();
    }

    private static string JsonErrorCode(CliResult result, string label, string command, bool hasReceipt = false)
    {
        var envelope = Envelope(result, label);
        Require(envelope["command"]!.GetValue<string>() == command, label + ": command name is " + command);
        Require(envelope["ok"]!.GetValue<bool>() == false, label + ": ok is false on failure");
        Require(hasReceipt ? envelope["data"] is JsonObject : envelope["data"] is null,
            label + (hasReceipt ? ": retained failure receipt is present" : ": pre-admission failure carries no result"));
        var error = envelope["error"]!.AsObject();
        var code = error["code"]!.GetValue<string>();
        Require(error["message"]!.GetValue<string>().Length > 0, label + ": failure carries a human message");
        return code;
    }

    private static async Task RouteAndStateFreeChecksAsync()
    {
        var repair = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--path", "Folder/Item", "--choice", Massdex.SyncShark.V2.NoticeChoices.RepairHelp,
            "--key", "notice-a", "--generation", "0", "--yes"]);
        Require(repair.Command == "conflicts resolve", "repair help uses the shared conflicts action with exact notice identity");
        var rename = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--path", "Folder/Item", "--choice", Massdex.SyncShark.V2.NoticeChoices.Rename,
            "--name", "Safe.txt", "--key", "notice-a", "--generation", "0", "--yes"]);
        Require(rename.Name == "Safe.txt", "rename carries the user supplied name through the registry");
        var children = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--path", "Folder", "--choice", Massdex.SyncShark.V2.NoticeChoices.ShowRemaining,
            "--offset", "50", "--key", "notice-a", "--generation", "0", "--yes"]);
        Require(children.Offset == 50, "child inspection carries its bounded page offset");
        var heldPage = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--choice", Massdex.SyncShark.V2.NoticeChoices.ShowRemaining, "--name", "123:50",
            "--key", "held-summary", "--generation", "0", "--yes"]);
        Require(heldPage.Name == "123:50", "pair summary remaining-items inspection carries its keyset cursor without an item path");
        foreach (var choice in new[] { Massdex.SyncShark.V2.NoticeChoices.ShowTarget,
            Massdex.SyncShark.V2.NoticeChoices.SyncTargetSeparately, Massdex.SyncShark.V2.NoticeChoices.CopyLink,
            Massdex.SyncShark.V2.NoticeChoices.CopyLinkAgain, Massdex.SyncShark.V2.NoticeChoices.RemoveLinkStaging,
            Massdex.SyncShark.V2.NoticeChoices.CopyLinkOutsideRoot, Massdex.SyncShark.V2.NoticeChoices.MakeAvailableOffline,
            Massdex.SyncShark.V2.NoticeChoices.ExportDocx, Massdex.SyncShark.V2.NoticeChoices.ExportMarkdown,
            Massdex.SyncShark.V2.NoticeChoices.ExportPdf, Massdex.SyncShark.V2.NoticeChoices.CheckHistoryAgain,
            Massdex.SyncShark.V2.NoticeChoices.ReviewDeletion, Massdex.SyncShark.V2.NoticeChoices.OpenStorage,
            Massdex.SyncShark.V2.NoticeChoices.ReviewReserve,
            Massdex.SyncShark.V2.NoticeChoices.PreviewMatches, Massdex.SyncShark.V2.NoticeChoices.ChooseFolder,
            Massdex.SyncShark.V2.NoticeChoices.RemoveRule, Massdex.SyncShark.V2.NoticeChoices.KeepRule })
        {
            var action = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
                "--path", "Folder/Item", "--choice", choice, "--key", "notice-a", "--generation", "0", "--yes"]);
            Require(action.Choice == choice && Massdex.SyncShark.V2.NoticeChoices.IsRetryChoice(choice),
                "offline, export and rebuildable-rule actions share the exact-target CLI notice route");
        }
        var confirmation = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--path", "link", "--choice", Massdex.SyncShark.V2.NoticeChoices.CopyLinkOutsideRoot,
            "--name", "D:/Target", "--key", "notice-a", "--generation", "0", "--yes"]);
        Require(confirmation.Name == "D:/Target", "link confirmation carries the exact target path");
        var chooseFolder = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--path", "typo", "--choice", Massdex.SyncShark.V2.NoticeChoices.ChooseFolder,
            "--name", "Folder/Sub", "--key", "notice-a", "--generation", "0", "--yes"]);
        Require(chooseFolder.Name == "Folder/Sub",
            "choose folder carries the user supplied pair-relative folder path");
        // F1: a key-only pair notice carries an empty path. Windows PowerShell 5.1 drops a "" argument
        // to a native exe, so dispatch must not refuse before the v2 runtime's own item-notice check
        // (which still demands --path for item notices) can see the exact notice.
        var pairNotice = new PairNoticeDispatchRuntime();
        var pairNoticeResult = await RunCliAsync(["--cli", "conflicts", "resolve", "--pair", "pair-a",
            "--choice", Massdex.SyncShark.V2.NoticeChoices.RetryNow, "--key", "notice-a",
            "--generation", "0", "--yes", "--json"], () => pairNotice);
        Require(pairNoticeResult.ExitCode == 0 && pairNotice.NoticePath == string.Empty &&
                pairNotice.NoticeChoice == Massdex.SyncShark.V2.NoticeChoices.RetryNow &&
                pairNotice.DisposeCount == 1,
            "a key-only pair notice resolves with --key and --generation and no --path");
        await LocalPathArgumentChecksAsync();
        foreach (var choice in new[] { "review-removal", "review-removal-a", "review-removal-b", "confirm-removal", "removal-next-page", "removal-previous-page" })
        {
            var removal = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "pair", "--path", "folder",
                "--key", "exact-notice", "--generation", "0", "--choice", choice, "--yes"]);
            Require(removal.Command == "conflicts resolve" && removal.Generation == 0 && removal.Choice == choice,
                "removal review CLI choices retain exact notice targeting and generation zero");
        }
        var ordinaryReplyWait = TimeSpan.FromMinutes(30);
        foreach (var seconds in new[] { "1800", "4500", "86400" })
        {
            var timedApply = SyncCliRegistry.Parse(["--cli", "first-sync", "apply", "--pair", "pair",
                "--yes", "--allow-recommended", "--timeout-seconds", seconds]);
            Require(SyncCliBridgeClient.ResolveReplyWaitTimeout(timedApply, ordinaryReplyWait) ==
                    TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture) + 120),
                "real bridge wait policy covers the authority deadline plus bounded checkpoint time: " + seconds);
        }
        var shortRun = SyncCliRegistry.Parse(["--cli", "run", "--pair", "pair", "--yes",
            "--timeout-seconds", "0.001"]);
        Require(SyncCliBridgeClient.ResolveReplyWaitTimeout(shortRun, ordinaryReplyWait) == ordinaryReplyWait,
            "a short authority deadline does not replace the existing client wait or cancellation contract");
        Require(SyncCliBridgeClient.ResolveReplyWaitTimeout(SyncCliRegistry.Parse(["--cli", "status"]),
                    TimeSpan.FromSeconds(15)) == TimeSpan.FromSeconds(15) &&
                SyncCliBridgeClient.ResolveReplyWaitTimeout(SyncCliRegistry.Parse(["--cli", "app", "exit", "--yes"]),
                    TimeSpan.FromSeconds(15)) == TimeSpan.FromMinutes(2),
            "ordinary reads keep their bound and graceful exit keeps its existing safe-checkpoint interval");
        foreach (var options in new string[][]
                 {
                     ["--yes"], ["--key", "session", "--yes"],
                     ["--key", "session", "--source", "local-scan", "--yes"],
                     ["--key", "session", "--source", "local-scan", "--destination", "remote-scan"]
                 })
            Expect("Rewind apply requires all reviewed identities and consent before opening state",
                await RunCliAsync(["--cli", "rewind", "apply", "--pair", "pair", .. options, "--json"], NeverOpensState), 2);
        var rewind = SyncCliRegistry.Parse(["--cli", "rewind", "apply", "--pair", "pair", "--key", "session",
            "--source", "local-scan", "--destination", "remote-scan", "--yes"]);
        Require(rewind.Key == "session" && rewind.Value("--source") == "local-scan" &&
                rewind.Value("--destination") == "remote-scan",
            "real Rewind parser preserves the exact session and both preview generations");
        var rewindView = new SyncCliControlData("rewind preview", true, "reviewed")
        {
            RewindConfirmation = new("session", "local-scan", "remote-scan")
        };
        var viewOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var rewindJson = JsonSerializer.Serialize(rewindView, viewOptions);
        Require(rewindJson.Contains("\"rewindConfirmation\"", StringComparison.Ordinal) &&
                rewindJson.Contains("\"localPreviewScanId\":\"local-scan\"", StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(new SyncCliControlData("other action", true, "unchanged"),
                    viewOptions).Contains("rewindConfirmation", StringComparison.Ordinal),
            "machine-readable preview carries exact consent without changing unrelated CLI control envelopes");
        Require(SyncCli.RenderHuman(rewindView).Contains("local-preview=local-scan", StringComparison.Ordinal) &&
                SyncCli.RenderHuman(rewindView).Contains("Dropbox-preview=remote-scan", StringComparison.Ordinal),
            "the actual human CLI renderer exposes both reviewed generations rather than an opaque success label");
        Require(SyncCli.IsCliInvocation(["--cli", "status"]), "--cli selects the CLI startup route");
        Require(SyncCli.IsCliInvocation(["--CLI", "status"]), "the --cli route is matched case-insensitively");
        Require(!SyncCli.IsCliInvocation([]), "no arguments keep the ordinary product startup");
        Require(!SyncCli.IsCliInvocation(["--background"]), "--background keeps the ordinary hidden startup");
        Require(!SyncCli.IsCliInvocation(["--check-rollback-ready"]), "maintenance startup is unchanged");
        Require(!SyncCli.IsCliInvocation(["/select,C:\\folder"]), "Explorer activation is unchanged");
        Require(SyncCliRuntime.ProductionLockPath == SyncSharkRuntimePaths.InstanceLockFile,
            "the CLI takes the registered production state lock");
        var unknownProvider = await RunCliAsync(["--cli", "accounts", "connect", "--provider", "unknown", "--yes", "--json"], NeverOpensState);
        Expect("unknown account provider rejected without opening state", unknownProvider, 2);
        foreach (var args in new string[][] {
            ["--yes"], ["--account", "google-one", "--yes"],
            ["--account", "google-one", "--enable"],
            ["--account", "google-one", "--enable", "--disable", "--yes"] })
            Expect("shared-drive opt-in requires exact target, one choice and confirmation",
                await RunCliAsync(["--cli", "accounts", "shared-drives", .. args, "--json"], NeverOpensState), 2);
        foreach (var choice in new[] { "--enable", "--disable" })
            Require(SyncCliRegistry.Parse(["--cli", "accounts", "shared-drives", "--account", "google-one", choice, "--yes"])
                .Flag(choice), "shared-drive explicit choice retained");
        await Task.CompletedTask;
    }

    internal static async Task LocalPathArgumentChecksAsync()
    {
        string[][] Routes(string path) =>
        [
            ["--cli", "storage", "inspect", "--local", path, "--json"],
            ["--cli", "folders", "create-local", "--local", path, "--name", "child", "--yes", "--json"],
            ["--cli", "pairs", "create", "--account", "fixture-account", "--path", "/fixture",
                "--local", path, "--yes", "--json"]
        ];

        var relativePaths = new List<string> { "relative-folder", "../relative-folder", " " };
        if (OperatingSystem.IsWindows())
            relativePaths.AddRange([@"\relative-folder", @"C:relative-folder"]);
        foreach (var path in relativePaths)
            foreach (var route in Routes(path))
            {
                // This factory throws if either direct/bridged dispatch gets as far as state.
                var rejected = await RunCliAsync(route, NeverOpensState);
                Expect("relative local path is rejected before opening state", rejected, 2);
                var error = Envelope(rejected, "relative local path rejection")["error"]!.AsObject();
                Require(error["message"]!.GetValue<string>().Contains("absolute folder path", StringComparison.Ordinal),
                    "local-path refusal explains that an absolute folder path is required");
            }

        var absolutePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sync-shark-path-fixture"));
        foreach (var route in Routes(absolutePath))
        {
            var accepted = SyncCliRegistry.Parse(route);
            Require(accepted.LocalRoot == absolutePath,
                "the shared parser preserves the exact absolute path without filesystem or provider work");
        }
    }

    private static async Task HelpAndVersionChecksAsync(CliFixture fixture)
    {
        // The factory throws: help and version must not open product state or any file at all.
        var help = await RunCliAsync(["--cli", "help"], NeverOpensState);
        Expect("help exits 0 without opening state", help, 0);
        Require(help.StandardOutput.Contains("--cli", StringComparison.Ordinal) &&
                help.StandardOutput.Contains("run --pair <exact-id> --yes", StringComparison.Ordinal) &&
                help.StandardOutput.Contains("exclusive product lock", StringComparison.Ordinal) &&
                help.StandardOutput.Contains("existing runtime", StringComparison.Ordinal) &&
                !help.StandardOutput.Contains("close it and run", StringComparison.Ordinal),
            "help documents the running-owner route and exclusive fallback without telling users to close the app");
        Require(help.StandardError.Length == 0, "help writes nothing to standard error");
        Require(Massdex.SyncShark.V2.NoticeChoices.IsExplicitNoticeChoice("restore-as-copy") &&
            help.StandardOutput.Contains("restore-as-copy", StringComparison.Ordinal),
            "Restore as a copy is a shared explicit notice choice advertised by CLI help");

        var helpJson = await RunCliAsync(["--cli", "help", "--json"], NeverOpensState);
        Expect("help --json exits 0 without opening state", helpJson, 0);
        var helpData = JsonData(helpJson, "help --json", "help");
        var helpCommands = helpData["commands"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Require(helpCommands.SequenceEqual(SyncCliRegistry.Rows.Select(row => row.Usage)),
            "help --json lists every registered command in registry order, not just live or app-required commands");
        Require(ReferenceEquals(SyncCli.ExitCodeHelp, SyncCliRegistry.ExitCodes),
            "human and JSON help use the same exit-code table");
        var capabilities = await RunCliAsync(["--cli", "capabilities", "--json"], NeverOpensState);
        var capabilityData = JsonData(capabilities, "capabilities --json", "capabilities");
        Require(capabilities.StandardOutput.Contains("{id,prefix}", StringComparison.Ordinal),
            "first-sync capabilities document scoped rebuildable choices");
        Require(capabilityData["commands"]!.AsArray().Select(item => item!["usage"]!.GetValue<string>())
                .SequenceEqual(helpCommands),
            "actual state-free capability and JSON help outputs have identical complete command inventories");
        foreach (var command in new[] { "show", "reauthorize" })
            Expect("account-specific actions reject missing selectors before opening state",
                await RunCliAsync(["--cli", "accounts", command, .. (command == "reauthorize"
                    ? new[] { "--yes" } : Array.Empty<string>()), "--json"], NeverOpensState), 2);
        var exactReauthorization = SyncCliRegistry.Parse(["--cli", "accounts", "reauthorize", "--account", "google-exact", "--yes"]);
        Require(exactReauthorization.AccountId == "google-exact",
            "reauthorization preserves the exact requested account rather than selecting a primary account");
        Expect("an update download without confirmation rejects before opening state",
            await RunCliAsync(["--cli", "updates", "check", "--download", "--json"], NeverOpensState), 2);
        var updateCheck = SyncCliRegistry.Parse(["--cli", "updates", "check"]);
        var updateDownload = SyncCliRegistry.Parse(["--cli", "updates", "check", "--download", "--yes"]);
        Require(!SyncCliRegistry.MutatesProductState(updateCheck) && SyncCliRegistry.MutatesProductState(updateDownload),
            "the shared registry classifies update discovery as read-only and confirmed downloading as a mutation");
        var batchSummary = SyncCliOperationsBase.CompactBatchReceipt(Enumerable.Range(0, 200)
            .Select(index => (object)new { pairId = "pair-" + index, failure = new string('界', 1000) }).ToArray(),
            "retained-run", false, "run batch");
        var summaryJson = JsonSerializer.SerializeToNode(batchSummary)!;
        Require(summaryJson["count"]!.GetValue<int>() == 200 &&
                summaryJson["returned"]!.GetValue<int>() < 200 &&
                summaryJson["omitted"]!.GetValue<int>() + summaryJson["returned"]!.GetValue<int>() == 200 &&
                summaryJson["runId"]!.GetValue<string>() == "retained-run",
            "oversized completed batch summary preserves full count, retained run identity and explicit omissions");
        var batchWire = SyncCliBridgeProtocol.EncodeReply(new("result", new string('a', 32), true, 0,
            SyncCli.RenderHuman(batchSummary), null));
        Require(SyncCliBridgeProtocol.ParseReply(batchWire, new string('a', 32)) is { Kind: "result", Ok: true, ExitCode: 0 },
            "the actual bounded batch renderer and bridge preserve a successful terminal receipt");
        var untransportableSample = JsonSerializer.SerializeToNode(SyncCliOperationsBase.CompactBatchReceipt(
            [new { pairId = "exact-pair", failure = new string('界', 70_000) }], "retained-run", false, "run batch"))!;
        Require(untransportableSample["returned"]!.GetValue<int>() == 0 &&
                untransportableSample["omitted"]!.GetValue<int>() == 1 &&
                untransportableSample["count"]!.GetValue<int>() == 1 &&
                untransportableSample["runId"]!.GetValue<string>() == "retained-run",
            "an optional post-effect sample too large to transport never retracts completion or loses the exact retained run");
        var largeProgress = SyncCliBridgeProtocol.ParseReply(SyncCliBridgeProtocol.EncodeProgressReply(
            new("progress", new string('a', 32), true, 0, new string('界', 70_000), null)), new string('a', 32));
        Require(largeProgress is { Kind: "progress", Ok: true, ExitCode: 0 } && largeProgress.Text.Contains("still running"),
            "an oversized progress entry never masquerades as a terminal result or a failed committed command");
        using (var fixtureLock = PilotInstanceLock.Acquire(fixture.LockPath))
        {
            var preferences = new UiPreferencesStore(fixtureLock.StateAuthority);
            var original = preferences.Load();
            var absent = Path.Combine(fixture.Root, "absent-failed-update");
            await preferences.SaveAsync(original with { DownloadedUpdateFolder = absent });
            await SyncCliOperationsBase.ClearAbsentFailedUpdateAsync(preferences, absent);
            Require(preferences.Load().DownloadedUpdateFolder is null,
                "a cancelled or failed download's exact absent allocation no longer blocks the next download");
            await preferences.SaveAsync(original with { DownloadedUpdateFolder = fixture.Root });
            await SyncCliOperationsBase.ClearAbsentFailedUpdateAsync(preferences, fixture.Root);
            Require(preferences.Load().DownloadedUpdateFolder == fixture.Root,
                "a retained allocation is not cleared when cleanup has not proved absence");
            await SyncCliOperationsBase.ClearAbsentFailedUpdateAsync(preferences, absent);
            Require(preferences.Load().DownloadedUpdateFolder == fixture.Root,
                "another retained download is never cleared by an old failed allocation's reconciliation");
            await preferences.SaveAsync(original);
        }
        await UpdateDownloadLifecycleChecksAsync(fixture);
        Require(helpData["requiresRunningApplication"]!.AsArray().Count >= 3,
            "help --json lists the families that need the running application");
        Require(helpData["exitCodes"]!.AsObject()["130"] is not null, "help --json documents exit code 130");

        var version = await RunCliAsync(["--cli", "version", "--json"], NeverOpensState);
        Expect("version --json exits 0 without opening state", version, 0);
        var versionData = JsonData(version, "version --json", "version");
        Require(versionData["version"]!.GetValue<string>().Length > 0, "version reports the product version");
        Require(versionData["sourceRevision"]!.GetValue<string>().Length > 0,
            "version reports the bound source revision");

        // The same commands succeed while the exclusive product lock is held, which is only possible
        // because they never take the lock.
        using (var held = PilotInstanceLock.Acquire(fixture.LockPath))
        {
            var heldHelp = await fixture.RunAsync("--cli", "help", "--json");
            Expect("help succeeds while the product lock is held", heldHelp, 0);
            var heldVersion = await fixture.RunAsync("--cli", "version");
            Expect("version succeeds while the product lock is held", heldVersion, 0);
        }
    }

    private static async Task UpdateDownloadLifecycleChecksAsync(CliFixture fixture)
    {
        Require(SyncSharkRuntimePaths.HasTestProductRoot,
            "download lifecycle uses the isolated test product root, never installed user staging");
        var staging = SyncSharkRuntimePaths.ReplacementStagingRoot;
        Directory.CreateDirectory(staging);
        var before = Entries(staging);
        // The synthetic installer must offer a version strictly NEWER than this build's own semantic
        // release, so the update lifecycle really exercises its download and failure input instead of
        // reporting the current release as already up to date.
        var releaseParts = BuildIdentity.InformationalVersion.Split('+')[0].Split('.');
        var offeredVersion = string.Join('.', releaseParts[..^1].Append(
            (int.Parse(releaseParts[^1], System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var installer = System.Text.Encoding.UTF8.GetBytes("# SyncSharkUpdateContract: " +
            JsonSerializer.Serialize(new { product = "Massdex.SyncShark", version = offeredVersion,
                previousVersion = BuildIdentity.InformationalVersion }) +
            "\nthrow 'Synthetic fixture installer must NEVER execute'\n");
        byte[] archive;
        using (var output = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(output,
                System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, byte[] bytes)
                { using var member = zip.CreateEntry(name).Open(); member.Write(bytes); }
                Add("scripts/Install-SyncShark.ps1", installer);
                Add("scripts/SyncShark.Release.psm1", "# synthetic module, never execute"u8.ToArray());
                Add("package/unsigned-candidate-framework-manifest.json", "{}"u8.ToArray());
                Add("package/signed-candidate/SyncShark.exe", "not an executable"u8.ToArray());
            }
            archive = output.ToArray();
        }
        string Asset(string name) => "https://github.com/" + SyncPublicUpdates.Repository +
            "/releases/download/v" + offeredVersion + "/" + name;
        var archiveUri = Asset("SyncShark-v" + offeredVersion + "-win-x64.zip");
        var release = JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "v" + offeredVersion,
            draft = false, prerelease = false, assets = new object[] {
                new { name = "Install-SyncShark.ps1", state = "uploaded", size = installer.Length,
                    browser_download_url = Asset("Install-SyncShark.ps1") },
                new { name = "SyncShark-v" + offeredVersion + "-win-x64.zip", state = "uploaded",
                    size = archive.Length, browser_download_url = archiveUri, digest = "sha256:" +
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)).ToLowerInvariant() }
            } } });
        using var instance = PilotInstanceLock.Acquire(fixture.LockPath);
        using var http = new HttpClient(new NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http);
        var preferences = new UiPreferencesStore(instance.StateAuthority);
        var original = preferences.Load();
        Require(original.DownloadedUpdateFolder is null,
            "isolated download lifecycle starts without a retained foreign package");
        var archiveRequests = 0;
        var allocations = new List<string>();
        var credentialsAbsent = true;
        HttpResponseMessage Input(HttpRequestMessage request)
        {
            credentialsAbsent &= request.Headers.Authorization is null && !request.Headers.Contains("Cookie");
            var uri = request.RequestUri!.AbsoluteUri;
            byte[] body;
            if (uri == "https://api.github.com/repos/" + SyncPublicUpdates.Repository +
                       "/releases?per_page=100&page=1") body = release;
            else if (uri == Asset("Install-SyncShark.ps1")) body = installer;
            else if (uri == archiveUri)
            {
                var allocated = preferences.Load().DownloadedUpdateFolder;
                Require(allocated is not null && Directory.Exists(allocated),
                    "actual downloader persists its exact leased allocation before requesting archive bytes");
                allocations.Add(allocated!);
                if (++archiveRequests == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                body = archive;
            }
            else throw new InvalidOperationException("Unexpected fixture update request: " + uri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
        ISyncCliRuntime Factory() => new UpdateInputRuntime(host, instance.StateAuthority,
            () => new UpdateInputHandler(Input));
        string? retained = null;
        try
        {
            var check = await RunCliAsync(["--cli", "updates", "check", "--json"], Factory);
            Expect("actual update discovery succeeds without downloading", check, 0);
            Require(archiveRequests == 0 && preferences.Load().DownloadedUpdateFolder is null,
                "read-only update check leaves staging and retained download authority untouched");
            var failed = await RunCliAsync(["--cli", "updates", "check", "--download", "--yes", "--json"], Factory);
            if (archiveRequests == 0)
            {
                // The real product refuses before requesting any archive bytes when the installation drive's
                // AvailableFreeSpace is below 10 GiB plus the archive and expanded maxima, so the refusal and
                // its absence of effects are the decisive facts on this machine. The positive download
                // subcases stay explicitly UNVERIFIED rather than being reported as passing.
                Expect("the real low-free-space update download refusal exits 6", failed, 6);
                Require(failed.StandardError.Contains(
                        "Free at least 10.4 GB on the installation drive before downloading an update.",
                        StringComparison.Ordinal),
                    "the zero-request refusal is the actual disk-capacity guard, not an unrelated early failure");
                Require(preferences.Load().DownloadedUpdateFolder is null && allocations.Count == 0 && credentialsAbsent,
                    "the capacity-refused update download retains no allocation, requests no archive bytes and " +
                    "sends no provider credentials");
                Require(!Directory.EnumerateFileSystemEntries(staging).Any(item =>
                        Path.GetFileName(item).Contains("SyncShark-", StringComparison.OrdinalIgnoreCase)),
                    "the capacity-refused update download writes no synthetic package into the fixture staging root");
                Console.WriteLine("UNVERIFIED update download success, failure, retry and cleanup positive " +
                    "subcases: the real installation-drive free-space guard refused before any archive request " +
                    "on this machine. The above-guard branch below is unchanged and must run where space allows.");
            }
            else
            {
                Expect("actual confirmed download reports the fixture server failure", failed, 6);
                Require(archiveRequests == 1 && allocations.Count == 1 && !Directory.Exists(allocations[0]) &&
                        preferences.Load().DownloadedUpdateFolder is null,
                    "real download cleanup and CLI adapter reconcile the absent failed allocation, permitting retry");
                var retry = await RunCliAsync(["--cli", "updates", "check", "--download", "--yes", "--json"], Factory);
                Expect("actual confirmed download can retry after the exact server failure", retry, 0);
                retained = preferences.Load().DownloadedUpdateFolder;
                Require(JsonData(retry, "retry update receipt", "updates check")["downloaded"]!.GetValue<bool>() &&
                        archiveRequests == 2 && retained == allocations[1] && Directory.Exists(retained) &&
                        File.ReadAllBytes(Path.Combine(retained!, "scripts", "Install-SyncShark.ps1"))
                            .SequenceEqual(installer),
                    "actual retry verifies and retains exact synthetic package bytes without executing an installer");
                var duplicate = await RunCliAsync(["--cli", "updates", "check", "--download", "--yes", "--json"], Factory);
                Expect("a still-retained package prevents another download", duplicate, 6);
                Require(archiveRequests == 2 && preferences.Load().DownloadedUpdateFolder == retained && credentialsAbsent,
                    "retained download refusal neither repeats archive effects nor sends provider credentials");
            }
        }
        finally
        {
            if (retained is not null) SyncDownloadedUpdate.CleanupRetained(retained);
            await preferences.SaveAsync(original);
        }
        Require(Entries(staging).SequenceEqual(before) && allocations.All(path => !Directory.Exists(path)),
            "actual registered cleanup removes only these generated fixture allocations and restores preferences");
    }

    private static async Task StatusChecksAsync(CliFixture fixture)
    {
        var result = await fixture.RunAsync("--cli", "status", "--json");
        Expect("status exits 0", result, 0);
        RequireRuntimeAnnouncementOnly(result, "status");
        var data = JsonData(result, "status --json", "status");
        Require(data["stateAccess"]!.GetValue<string>() == "exclusive-product-lock",
            "status reports the exclusive product lock requirement");
        Require(data["productRoot"]!.GetValue<string>() == fixture.Root,
            "status reports the exact product root it read");
        Require(data["authorityStateExists"]!.GetValue<bool>(),
            "status distinguishes retained authority bookkeeping from an empty state");
        Require(data["accountCount"]!.GetValue<int>() == 2, "status counts both retained accounts");
        Require(data["pairs"]!["total"]!.GetValue<int>() == 2 &&
                data["pairs"]!["enabled"]!.GetValue<int>() == 2,
            "status counts the retained pairs");
        Require(data["guiRequirement"]!.GetValue<string>().Length > 40 &&
                data["mode"]!.GetValue<string>() == "direct",
            "status states the state-access requirement and the mode it is running in");
        Require(data["lastSuccessfulRunUtc"] is null, "status reports last success as null until a run completes");

        var human = await fixture.RunAsync("--cli", "status");
        Expect("status human mode exits 0", human, 0);
        Require(human.StandardOutput.Contains("Sync Shark ", StringComparison.Ordinal) &&
                human.StandardOutput.Contains(fixture.Root, StringComparison.Ordinal),
            "status human output is plain readable text");
        RequireRuntimeAnnouncementOnly(human, "status human mode");
    }

    private static async Task AccountChecksAsync(CliFixture fixture)
    {
        var result = await fixture.RunAsync("--cli", "accounts", "list", "--json");
        Expect("accounts list exits 0", result, 0);
        var data = JsonData(result, "accounts list --json", "accounts list");
        Require(data["count"]!.GetValue<int>() == 2, "accounts list reports both accounts");
        var accounts = data["accounts"]!.AsArray();
        Require(accounts.Any(account => account!["connectionId"]!.GetValue<string>() == fixture.PrimaryConnectionId &&
                                        account!["isPrimary"]!.GetValue<bool>()),
            "accounts list identifies the primary account by exact connection id");
        Require(accounts.Any(account => account!["connectionId"]!.GetValue<string>() == fixture.SecondaryConnectionId &&
                                        account!["isPrimary"]!.GetValue<bool>() == false),
            "accounts list identifies the additional account by exact connection id");
        Require(accounts.All(account => account!["provider"]!.GetValue<string>() == "dropbox" &&
                                        account!["pairCount"]!.GetValue<int>() == 1),
            "accounts list reports each account's provider and dependent pair count");
        Require(!ContainsAny(result), "accounts list discloses no credential reference or token");
    }

    private static async Task PairChecksAsync(CliFixture fixture)
    {
        var list = await fixture.RunAsync("--cli", "pairs", "list", "--json");
        Expect("pairs list exits 0", list, 0);
        var data = JsonData(list, "pairs list --json", "pairs list");
        Require(data["count"]!.GetValue<int>() == 2, "pairs list reports both retained pairs");
        var pairs = data["pairs"]!.AsArray();
        var pairA = pairs.Single(pair => pair!["id"]!.GetValue<string>() == fixture.PairAId)!;
        Require(pairA["localRoot"]!.GetValue<string>() == fixture.LocalRootA,
            "pairs list preserves the exact Unicode and space-bearing local root");
        Require(pairA["isSelected"]!.GetValue<bool>(), "pairs list marks the retained selected pair");
        Require(pairA["firstSyncReviewRequired"]!.GetValue<bool>() == false,
            "pairs list reports an approved pair without a first-sync review requirement");
        var pairB = pairs.Single(pair => pair!["id"]!.GetValue<string>() == fixture.PairBId)!;
        Require(pairB["connectionId"]!.GetValue<string>() == fixture.SecondaryConnectionId,
            "pairs list retains the second pair's exact account routing");
        Require(pairB["dropboxRoot"]!.GetValue<string>() == "/CLI B",
            "pairs list reports the exact remote root");

        var human = await fixture.RunAsync("--cli", "pairs", "list");
        Expect("pairs list human mode exits 0", human, 0);
        Require(human.StandardOutput.Contains(fixture.LocalRootA, StringComparison.Ordinal),
            "pairs list human output shows the Unicode path verbatim");

        var show = await fixture.RunAsync("--cli", "pairs", "show", "--pair", fixture.PairAId, "--json");
        Expect("pairs show exits 0 for an exact id", show, 0);
        var shown = JsonData(show, "pairs show --json", "pairs show");
        Require(shown["id"]!.GetValue<string>() == fixture.PairAId &&
                shown["name"]!.GetValue<string>().Length > 0,
            "pairs show returns the exact named pair");
        Require(shown["selectionRuleCount"] is not null && shown["ignoreRuleCount"] is not null &&
                shown["policyRevision"]!.GetValue<long>() >= 1,
            "pairs show reports policy revision and rule counts");

        var unknown = await fixture.RunAsync("--cli", "pairs", "show", "--pair", "pair-not-retained", "--json");
        Expect("pairs show fails 4 for an unknown pair id", unknown, 4);
        Require(JsonErrorCode(unknown, "pairs show unknown", "pairs show") == "missing_target",
            "an unknown pair id reports missing_target instead of falling back to another pair");

        var missing = await fixture.RunAsync("--cli", "pairs", "show");
        Expect("pairs show without --pair fails 2", missing, 2);
        Require(!ContainsAny(missing), "a usage failure discloses no credential reference or token");
    }

    private static async Task ActivityChecksAsync(CliFixture fixture)
    {
        var filtered = await fixture.RunAsync("--cli", "activity", "list", "--account", fixture.PrimaryConnectionId,
            "--categories", "Sync log", "--run", fixture.LogRunId, "--order", "oldest", "--json");
        Expect("activity structured filters exit 0", filtered, 0);
        var typed = JsonData(filtered, "typed activity", "activity list")["items"]!.AsArray();
        Require(typed.Count == 2 && typed[0]!["outcome"]!.GetValue<string>() == "Sync started" &&
                typed[1]!["outcome"]!.GetValue<string>() == "Sync finished" &&
                typed.All(item => item!["eventId"] is not null && item["sequence"] is not null &&
                    item["accountId"]!.GetValue<string>() == fixture.PrimaryConnectionId),
            "CLI uses the same retained chronological events and exact account filtering");
        var log = await fixture.RunAsync("--cli", "activity", "list", "--log", "--run", fixture.LogRunId, "--limit", "1", "--json");
        var firstLog = JsonData(log, "detailed log cursor", "activity list");
        Require(firstLog["returned"]!.GetValue<int>() == 1 && firstLog["hasMore"]!.GetValue<bool>(), "detailed CLI log retains cursor pagination");
        var sequence = firstLog["nextSequence"]!.GetValue<long>();
        var newest = await fixture.RunAsync("--cli", "activity", "list", "--log", "--run", fixture.LogRunId,
            "--order", "newest", "--limit", "1", "--json");
        var newestLog = JsonData(newest, "newest retained log", "activity list");
        var backward = newestLog["previousSequence"]!.GetValue<long>();
        Require(newest.ExitCode == 0 && newestLog["items"]!.AsArray().Single()!["outcome"]!.GetValue<string>() == "Sync finished" &&
                backward > sequence && newestLog["nextSequence"]!.GetValue<long>() == backward && newestLog["hasMore"]!.GetValue<bool>(),
            "CLI newest log opens the real retained tail immediately and distinguishes backward history from forward live cursors");
        var earlierLog = await fixture.RunAsync("--cli", "activity", "list", "--log", "--run", fixture.LogRunId,
            "--order", "newest", "--before", backward.ToString(), "--limit", "1", "--json");
        var earlierData = JsonData(earlierLog, "earlier retained log", "activity list");
        Require(earlierLog.ExitCode == 0 && earlierData["items"]!.AsArray().Single()!["outcome"]!.GetValue<string>() == "Sync started" &&
                earlierData["previousSequence"]!.GetValue<long>() == sequence && !earlierData["hasMore"]!.GetValue<bool>(),
            "CLI earlier-history cursor returns the preceding exact run event once and reports the retained end truthfully");
        var newestText = await fixture.RunAsync("--cli", "activity", "list", "--log", "--run", fixture.LogRunId,
            "--order", "newest", "--limit", "1");
        Require(newestText.StandardOutput.Contains("--order newest --before " + backward) &&
                newestText.StandardOutput.Contains("activity follow --seconds 1 --after " + backward),
            "Human CLI output explains both earlier history and forward live following rather than silently reversing a cursor");
        var follow = await fixture.RunAsync("--cli", "activity", "follow", "--run", fixture.LogRunId, "--after", sequence.ToString(), "--seconds", "1", "--json");
        Require(follow.ExitCode == 0 && JsonData(follow, "follow log", "activity follow")["eventsEmitted"]!.GetValue<int>() == 1 &&
            follow.StandardError.Contains("Sync finished") && !follow.StandardError.Contains("Sync started"),
            "CLI follow streams exactly the newly retained event and does not replay an earlier cursor");
        var liveRecord = follow.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Sync Shark: {", StringComparison.Ordinal))["Sync Shark: ".Length..];
        using (var parsedLiveRecord = JsonDocument.Parse(liveRecord))
            Require(liveRecord.Length > 400 && parsedLiveRecord.RootElement.GetProperty("Reason").GetString()?
                    .Contains("retained-tail-cause") == true && !liveRecord.Contains("long-log-fixture-secret"),
                "CLI live log emits complete parseable event JSON and the long retained cause, with secret redaction");
        foreach (var invalid in new[] {
            new[] { "activity", "list", "--after", "1" },
            new[] { "activity", "list", "--log", "--after", "-1" },
            new[] { "activity", "list", "--log", "--offset", "1" },
            new[] { "activity", "list", "--before", "1", "--order", "newest" },
            new[] { "activity", "list", "--log", "--before", "1" },
            new[] { "activity", "list", "--log", "--before", "0", "--order", "newest" },
            new[] { "activity", "list", "--log", "--before", "-1", "--order", "newest" },
            new[] { "activity", "list", "--log", "--after", "1", "--order", "newest" },
            new[] { "activity", "follow", "--seconds", "0" },
            new[] { "activity", "follow", "--seconds", "61" },
            new[] { "run", "retry", "--yes" },
            new[] { "run", "retry", "--run", "fixture", "--choice", "everything", "--yes" },
            new[] { "run", "verify", "--run", "fixture" } })
            Expect("new log/retry usage rejects before state access",
                await RunCliAsync(new[] { "--cli" }.Concat(invalid).ToArray(), NeverOpensState), 2);
        var severityOnly = await fixture.RunAsync("--cli", "activity", "list", "--categories", "Sync log,Diagnostics",
            "--severity", "Success", "--entity", "Folder pair", "--run", fixture.LogRunId, "--json");
        Require(JsonData(severityOnly, "severity activity", "activity list")["items"]!.AsArray().Count == 1,
            "CLI category union, severity and entity filters share one nonduplicated event projection");
        Expect("invalid activity category rejects before state access",
            await RunCliAsync(["--cli", "activity", "list", "--categories", "not-a-category"], NeverOpensState), 2);
        Expect("invalid activity order rejects before state access",
            await RunCliAsync(["--cli", "activity", "list", "--order", "random"], NeverOpensState), 2);
        var all = await fixture.RunAsync("--cli", "activity", "list", "--limit", "200", "--json");
        Expect("activity list exits 0", all, 0);
        var data = JsonData(all, "activity list --json", "activity list");
        var items = data["items"]!.AsArray();
        Require(data["returned"]!.GetValue<int>() == items.Count,
            "activity list reports the exact number of returned rows");
        Require(items.Any(item => item!["pairId"]!.GetValue<string>() == fixture.PairAId) &&
                items.Any(item => item!["pairId"]!.GetValue<string>() == fixture.PairBId),
            "activity list returns retained rows for both pairs");
        Require(items.All(item => item!["relativePath"] is not null && item!["outcome"] is not null),
            "activity list projects explicit safe fields for every row");

        var primary = await fixture.RunAsync("--cli", "activity", "list", "--account", fixture.PrimaryConnectionId, "--json");
        Expect("activity list --account primary exits 0", primary, 0);
        var primaryData = JsonData(primary, "activity list --account primary", "activity list");
        var primaryItems = primaryData["items"]!.AsArray();
        Require(primaryItems.Count >= 1 && primaryItems.All(item =>
                    item!["pairId"]!.GetValue<string>() == fixture.PairAId),
            "the account filter returns only the exact account's pair rows");

        var secondary = await fixture.RunAsync("--cli", "activity", "list", "--account", fixture.SecondaryConnectionId, "--json");
        Expect("activity list --account secondary exits 0", secondary, 0);
        var secondaryData = JsonData(secondary, "activity list --account secondary", "activity list");
        var secondaryItems = secondaryData["items"]!.AsArray();
        Require(secondaryItems.Count >= 1 && secondaryItems.All(item =>
                    item!["pairId"]!.GetValue<string>() == fixture.PairBId),
            "a second account's filter is isolated from the first account's pairs");
        Require(secondaryItems.Any(item => item!["relativePath"]!.GetValue<string>() == "account-scoped-event.txt"),
            "the second account sees an event carrying its explicitly recorded account identity");
        Require(items.Any(item => item!["pairId"]!.GetValue<string>() == fixture.PairBId &&
                                  item["relativePath"]!.GetValue<string>().StartsWith("item-", StringComparison.Ordinal)) &&
                secondaryItems.All(item => !item!["relativePath"]!.GetValue<string>().StartsWith("item-", StringComparison.Ordinal)),
            "legacy observations without recorded account identity remain in All Accounts and are not attributed from the pair's current binding");

        var unknownAccount = await fixture.RunAsync("--cli", "activity", "list", "--account", "connection-unknown-fixture");
        Expect("activity list --account unknown fails 4", unknownAccount, 4);

        var limited = await fixture.RunAsync("--cli", "activity", "list", "--limit", "1", "--json");
        Expect("activity list --limit 1 exits 0", limited, 0);
        var limitedData = JsonData(limited, "activity list --limit 1", "activity list");
        Require(limitedData["returned"]!.GetValue<int>() == 1 && limitedData["hasMore"]!.GetValue<bool>(),
            "a bounded page reports one row and that more retained rows exist");

        var offset = await fixture.RunAsync("--cli", "activity", "list", "--limit", "200", "--offset", "1", "--json");
        Expect("activity list --offset 1 exits 0", offset, 0);
        var offsetData = JsonData(offset, "activity list --offset 1", "activity list");
        Require(offsetData["offset"]!.GetValue<int>() == 1, "activity list reports the applied offset");

        var human = await fixture.RunAsync("--cli", "activity", "list", "--limit", "3");
        Expect("activity list human mode exits 0", human, 0);
        Require(human.StandardOutput.Contains("row(s) from offset", StringComparison.Ordinal),
            "activity human output is plain readable text");
        RequireRuntimeAnnouncementOnly(human, "activity list");
        Require(!ContainsAny(human, all, primary, secondary), "activity output discloses no credential reference or token");
    }

    private static async Task ActivityBytePageChecksAsync(string root)
    {
        var fixture = await BuildReadFixtureAsync(root);
        // A live append between pages belongs to one admitted run in one installation generation.
        // Borrow the same host for reads rather than reopening the installation under its writer.
        using var instance = PilotInstanceLock.Acquire(fixture.LockPath);
        using var http = new HttpClient(new NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http);
        await host.LoadPairsAsync();
        var admission = await AdmitActivityFixtureAsync(host.Authority, fixture.PairAId);
        var writer = await host.Authority.OpenRunActivityWriterAsync(admission);
        Task<CliResult> RunAsync(params string[] args) =>
            RunCliAsync(args, () => new BorrowedScanRuntime(host, instance.StateAuthority));
        var now = DateTimeOffset.UtcNow;
        var message = "Byte page " + new string('x', 3500) +
            " retained-tail-cause access_token=byte-page-private-value";
        var safeReason = SyncActivityText.Safe(message);
        var records = Enumerable.Range(0, 32).Select(index => new SyncActivityEvent(
            "", now, [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics],
            "Error", "File", "Download failed", message, fixture.PrimaryConnectionId, fixture.PairAId,
            RelativePath: $"byte-page/{index:D3}/日本語  quoted\"back\\slash.txt")).ToArray();
        async Task<SyncActivityEvent[]> AppendAsync(IReadOnlyList<SyncActivityEvent> input, bool separateRun = false)
        {
            var currentWriter = separateRun
                ? await host.Authority.OpenRunActivityWriterAsync(
                    await AdmitActivityFixtureAsync(host.Authority, fixture.PairAId))
                : writer;
            var pending = input.Select(item => currentWriter.Enqueue(item with
                { EventId = "", RunId = currentWriter.CommandId })).ToArray();
            await host.Authority.AppendRunActivityAsync(pending);
            if (separateRun) await host.Authority.CloseRunActivityWriterAsync(currentWriter);
            return pending.Select(item => item.Event).ToArray();
        }
        records = await AppendAsync(records);
        var runId = writer.CommandId;
        var expected = records.ToDictionary(item => "history:" + item.EventId, item => item.RelativePath, StringComparer.Ordinal);
        static bool Fits(CliResult result) => System.Text.Encoding.Unicode.GetByteCount(
            SyncCliBridgeProtocol.EncodeReply(new("result", new string('0', 32), true, result.ExitCode,
                result.StandardOutput, null)) + "\0") <= SyncCliBridgeProtocol.MaximumPayloadBytes * 2;
        async Task<CliResult> ReadAsync(bool newest, long cursor)
        {
            var args = new List<string> { "--cli", "activity", "list", "--log", "--run", runId,
                "--pair", fixture.PairAId, "--account", fixture.PrimaryConnectionId,
                "--categories", "Sync log", "--severity", "Error", "--entity", "File", "--limit", "30", "--json" };
            if (newest) { args.AddRange(["--order", "newest"]); if (cursor > 0) args.AddRange(["--before", cursor.ToString()]); }
            else if (cursor > 0) args.AddRange(["--after", cursor.ToString()]);
            return await RunAsync(args.ToArray());
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long next = 0, firstSequence = 0;
        var appended = false;
        do
        {
            var result = await ReadAsync(false, next);
            Expect("real detailed byte-bounded forward page", result, 0);
            var data = JsonData(result, "byte-bounded forward", "activity list");
            var rows = data["items"]!.AsArray();
            Require(rows.Count > 0 && data["returned"]!.GetValue<int>() == rows.Count && Fits(result),
                "actual detailed dispatcher returns exact counts within the unchanged nested reply limit");
            if (next == 0) Require(rows.Count < 30 && data["hasMore"]!.GetValue<bool>(),
                "long escaped rows reduce the actual30-row window by bytes with honest continuation");
            foreach (var row in rows)
            {
                var id = row!["eventId"]!.GetValue<string>();
                var sequence = row["sequence"]!.GetValue<long>();
                if (firstSequence == 0) firstSequence = sequence;
                Require(sequence > next && seen.Add(id) && expected[id] == row["relativePath"]!.GetValue<string>() &&
                        row["reason"]!.GetValue<string>() == safeReason &&
                        row["accountId"]!.GetValue<string>() == fixture.PrimaryConnectionId,
                    "forward continuation preserves every exact event, escaped space-bearing path, safe cause and filter once");
                next = sequence;
            }
            Require(data["nextSequence"]!.GetValue<long>() == next, "forward cursor binds the last emitted event, not an omitted row");
            if (!appended)
            {
                var tail = (await AppendAsync([records[0] with
                    { RelativePath = "byte-page/新しい  tail.txt" }])).Single();
                expected.Add("history:" + tail.EventId, tail.RelativePath);
                await host.Authority.CloseRunActivityWriterAsync(writer);
                appended = true;
            }
            if (!data["hasMore"]!.GetValue<bool>()) break;
        } while (true);
        Require(seen.SetEquals(expected.Keys), "an append between bounded pages remains reachable exactly once without gaps");
        seen.Clear();
        long before = 0;
        do
        {
            var result = await ReadAsync(true, before);
            Expect("real detailed byte-bounded backward page", result, 0);
            var data = JsonData(result, "byte-bounded backward", "activity list");
            var rows = data["items"]!.AsArray();
            Require(rows.Count > 0 && Fits(result), "newest detailed page fits the actual transport");
            long previous = before == 0 ? long.MaxValue : before;
            foreach (var row in rows)
            {
                var sequence = row!["sequence"]!.GetValue<long>();
                Require(sequence < previous && seen.Add(row["eventId"]!.GetValue<string>()) &&
                        row["reason"]!.GetValue<string>() == safeReason,
                    "earlier-history pages return strict descending exact retained events without repeats or clipped causes");
                previous = sequence;
            }
            before = data["previousSequence"]!.GetValue<long>();
            Require(before == previous && data["nextSequence"]!.GetValue<long>() ==
                    rows[0]!["sequence"]!.GetValue<long>(), "backward and live-forward cursors both bind emitted rows");
            if (!data["hasMore"]!.GetValue<bool>()) break;
        } while (true);
        Require(seen.SetEquals(expected.Keys), "every long retained event remains reachable from newest history");
        var follow = await RunAsync("--cli", "activity", "follow", "--run", runId,
            "--after", firstSequence.ToString(), "--seconds", "2", "--json");
        Expect("actual long detailed follow succeeds", follow, 0);
        var emitted = follow.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("Sync Shark: {", StringComparison.Ordinal)).ToArray();
        Require(emitted.Length == expected.Count - 1 && JsonData(follow, "long follow", "activity follow")
                ["eventsEmitted"]!.GetValue<int>() == emitted.Length,
            "actual follow drains multiple byte-bounded pages and receipts the exact emitted count");
        foreach (var line in emitted)
        {
            using var parsed = JsonDocument.Parse(line["Sync Shark: ".Length..]);
            var eventId = parsed.RootElement.GetProperty("EventId").GetString()!;
            Require(parsed.RootElement.GetProperty("RelativePath").GetString() == expected[eventId] &&
                    parsed.RootElement.GetProperty("Reason").GetString() == safeReason,
                "complete JSON event lines preserve escaped paths and the entire safe diagnostic beyond character400");
        }
        var oversized = (await AppendAsync([records[0] with
            { RelativePath = new string('界', 32700) }], separateRun: true)).Single();
        var rejected = await RunAsync("--cli", "activity", "list", "--log", "--run",
            oversized.RunId!, "--limit", "1", "--json");
        Expect("one unrepresentable detailed row explicitly refuses", rejected, 6);
        Require(rejected.StandardError.Contains("No path was truncated") && fixture.RemoteEffects == 0,
            "oversized detail cannot become a truncated path, empty continuation loop or sync permission");
    }

    private static async Task SettingsChecksAsync(CliFixture fixture)
    {
        var result = await fixture.RunAsync("--cli", "settings", "show", "--json");
        Expect("settings show exits 0", result, 0);
        var data = JsonData(result, "settings show --json", "settings show");
        Require(data["automation"]!["enabled"]!.GetValue<bool>() &&
                data["automation"]!["intervalMinutes"]!.GetValue<int>() == 15,
            "settings show reports the retained automatic sync defaults");
        Require(data["transfer"]!["maximumConcurrentPairs"]!.GetValue<int>() == 2 &&
                data["transfer"]!["uploadKiBPerSecond"]!.GetValue<int>() == 0,
            "settings show reports the retained transfer settings without provider configuration");
        Require(data["interface"]!["theme"]!.GetValue<string>().Length > 0,
            "settings show reports the retained interface theme");
        var human = await fixture.RunAsync("--cli", "settings", "show");
        Expect("settings show human mode exits 0", human, 0);
        Require(human.StandardOutput.Contains("Automatic syncing", StringComparison.Ordinal),
            "settings human output is plain readable text");
    }

    private static async Task PlanChecksAsync(CliFixture fixture)
    {
        var result = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId, "--json");
        Expect("plan exits 0 for a pair with retained inventories", result, 0);
        var data = JsonData(result, "plan --json", "plan");
        Require(data["pairId"]!.GetValue<string>() == fixture.PairAId, "plan routes the exact named pair");
        Require(data["firstSyncReviewRequired"]!.GetValue<bool>() == false,
            "plan reports the pair's first-sync review state");
        Require(data["hasCompleteInventory"]!.GetValue<bool>(),
            "plan reads the retained complete inventory without refreshing it");
        var items = data["items"]!.AsArray();
        Require(data["returned"]!.GetValue<int>() == items.Count,
            "plan reports the exact number of returned plan rows");
        Require(items.All(item => item!["relativePath"]!.GetValue<string>().Length > 0 &&
                                  item!["group"]!.GetValue<string>().Length > 0 &&
                                  item!["recommendation"]!.GetValue<string>().Length > 0),
            "every retained plan row carries an explicit path, group and recommendation");
        Require(data["localOnlyItems"]!.GetValue<int>() + data["dropboxOnlyItems"]!.GetValue<int>() +
                data["conflictItems"]!.GetValue<int>() <= data["totalItems"]!.GetValue<int>(),
            "plan group counts are consistent with the retained total");
        var human = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId);
        Expect("plan human mode exits 0", human, 0);
        Require(human.StandardOutput.Contains(fixture.PairAId, StringComparison.Ordinal),
            "plan human output names the exact pair");

        var unknown = await fixture.RunAsync("--cli", "plan", "--pair", "pair-also-not-retained", "--json");
        Expect("plan fails 4 for an unknown pair id", unknown, 4);
        Require(JsonErrorCode(unknown, "plan unknown pair", "plan") == "missing_target",
            "plan never falls back to the selected or default pair");

        // No fixture account has a protected credential, so a provider authentication attempt would
        // fail; every read-only command above succeeded, and the fixture remote recorded no effect.
        Require(fixture.RemoteEffects == 0,
            "status, plan, activity, pairs and settings made zero provider effects");
    }

    private static async Task PlanPagingChecksAsync(string root)
    {
        var fixture = await BuildReadFixtureAsync(root, additionalRemoteRows: 225);
        var first = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId, "--json");
        Expect("large retained plan default page succeeds", first, 0);
        var firstData = JsonData(first, "large plan default", "plan");
        Require(firstData["returned"]!.GetValue<int>() == SyncCli.ActivityDefaultLimit &&
                firstData["limit"]!.GetValue<int>() == SyncCli.ActivityDefaultLimit &&
                firstData["offset"]!.GetValue<int>() == 0 && firstData["hasMore"]!.GetValue<bool>(),
            "large plans default to a bounded first page with explicit continuation state");
        var nonce = new string('0', 32);
        static int ReplyBytes(CliResult result, string nonce) => System.Text.Encoding.Unicode.GetByteCount(
            SyncCliBridgeProtocol.EncodeReply(new SyncCliBridgeReply("result", nonce, true, 0,
                result.StandardOutput, null)) + "\0");
        Require(ReplyBytes(first, nonce) <= SyncCliBridgeProtocol.MaximumPayloadBytes * 2,
            "the default real plan result fits the unchanged running-app reply boundary");
        var oldWindow = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId,
            "--limit", "200", "--json");
        Expect("explicit maximum plan window still uses the real retained adapter", oldWindow, 0);
        var maximumData = JsonData(oldWindow, "large plan maximum", "plan");
        Require(ReplyBytes(oldWindow, nonce) <= SyncCliBridgeProtocol.MaximumPayloadBytes * 2 &&
                maximumData["returned"]!.GetValue<int>() is > 0 and < 200 &&
                maximumData["hasMore"]!.GetValue<bool>(),
            "the actual 200-row request returns a fitting ordered prefix with honest continuation rather than transport failure");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var total = firstData["totalItems"]!.GetValue<int>();
        while (true)
        {
            var page = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId,
                "--limit", "25", "--offset", offset.ToString(), "--json");
            Expect("retained plan continuation succeeds at " + offset, page, 0);
            var data = JsonData(page, "plan continuation", "plan");
            var items = data["items"]!.AsArray();
            Require(data["offset"]!.GetValue<int>() == offset && data["limit"]!.GetValue<int>() == 25 &&
                    data["returned"]!.GetValue<int>() == items.Count &&
                    data["totalItems"]!.GetValue<int>() == total &&
                    ReplyBytes(page, nonce) <= SyncCliBridgeProtocol.MaximumPayloadBytes * 2,
                "each real page binds its offset, counts and an in-bound reply");
            foreach (var item in items)
                Require(seen.Add(item!["relativePath"]!.GetValue<string>()), "continuations never repeat a retained path");
            offset += items.Count;
            if (!data["hasMore"]!.GetValue<bool>()) break;
            Require(items.Count > 0 && offset < total, "continuation makes bounded forward progress");
        }
        Require(total == 229 && seen.Count == total, "all229 retained rows are reachable once, including beyond200");
        var end = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId,
            "--limit", "25", "--offset", offset.ToString(), "--json");
        Expect("plan exact end is a successful empty page", end, 0);
        var endData = JsonData(end, "plan end", "plan");
        Require(endData["returned"]!.GetValue<int>() == 0 && !endData["hasMore"]!.GetValue<bool>(),
            "the exact end does not invent another page");
        var human = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairAId, "--limit", "1");
        Require(human.StandardOutput.Contains($"plan --pair {fixture.PairAId} --limit 1 --offset 1"),
            "human output provides the executable next-page command");
        foreach (var invalid in new[] { new[] { "--limit", "0" }, new[] { "--limit", "201" }, new[] { "--offset", "-1" } })
        {
            var refused = await fixture.RunAsync(["--cli", "plan", "--pair", fixture.PairAId, .. invalid, "--json"]);
            Expect("invalid plan paging rejected before state: " + string.Join(' ', invalid), refused, 2);
        }
        Require(fixture.RemoteEffects == 0, "plan pagination never transfers or approves any file");
    }

    private static async Task UsageChecksAsync(CliFixture fixture)
    {
        async Task UsageAsync(string label, bool jsonRequested, params string[] args)
        {
            var result = await fixture.RunAsync(args);
            Expect(label, result, 2);
            Require(result.StandardError.Length > 0, label + ": the error is visible on standard error");
            if (!jsonRequested)
            {
                Require(result.StandardOutput.Length == 0, label + ": human mode keeps standard output clean");
                return;
            }
            var envelope = Envelope(result, label);
            Require(envelope["ok"]!.GetValue<bool>() == false &&
                    envelope["error"]!["code"]!.GetValue<string>() == "usage",
                label + ": JSON mode still prints exactly one usage envelope");
        }

        await UsageAsync("an unknown command fails 2", false, "--cli", "frobnicate");
        await UsageAsync("an unfinished command family fails 2", false, "--cli", "pairs");
        await UsageAsync("a third positional argument fails 2", false, "--cli", "pairs", "list", "extra");
        await UsageAsync("a duplicate --json fails 2", true, "--cli", "status", "--json", "--json");
        await UsageAsync("an unknown option fails 2", false, "--cli", "status", "--bogus");
        await UsageAsync("an option without a value fails 2", false, "--cli", "pairs", "show", "--pair");
        await UsageAsync("an empty pair id fails 2", false, "--cli", "pairs", "show", "--pair", "");
        await UsageAsync("a whitespace pair id fails 2", false, "--cli", "pairs", "show", "--pair", "pair a");
        await UsageAsync("an option that the command does not accept fails 2", false, "--cli", "status", "--pair", "x");
        await UsageAsync("a duplicate --pair fails 2", false, "--cli", "pairs", "show", "--pair", "a", "--pair", "b");
        await UsageAsync("a non-integer --limit fails 2", false, "--cli", "activity", "list", "--limit", "abc");
        await UsageAsync("--limit 0 fails 2", false, "--cli", "activity", "list", "--limit", "0");
        await UsageAsync("--limit above the ceiling fails 2", false, "--cli", "activity", "list", "--limit", "201");
        await UsageAsync("a negative --offset fails 2", false, "--cli", "activity", "list", "--offset", "-1");
        await UsageAsync("paging on a non-paged command fails 2", false, "--cli", "status", "--limit", "5");
        await UsageAsync("--account on a non-paged command fails 2", false, "--cli", "status", "--account", "x");
        await UsageAsync("run without --yes fails 2", false, "--cli", "run", "--pair", fixture.PairAId);
        await UsageAsync("run without --pair fails 2", false, "--cli", "run", "--yes");

        var usageJson = await fixture.RunAsync("--cli", "frobnicate", "--json");
        Expect("a usage failure with --json exits 2", usageJson, 2);
        Require(JsonErrorCode(usageJson, "usage --json", "frobnicate") == "usage",
            "a usage failure reports the usage error code as one JSON object");

        // The bare route and the JSON-only route both fail closed without touching state.
        var bare = await RunCliAsync(["--cli"], NeverOpensState);
        Expect("a bare --cli fails 2 without opening state", bare, 2);
        var jsonOnly = await RunCliAsync(["--cli", "--json"], NeverOpensState);
        Expect("--cli --json fails 2 without opening state", jsonOnly, 2);
    }

    private static async Task UnsupportedFamilyChecksAsync(CliFixture fixture)
    {
        // The first-pass placeholders are real commands now, so these checks keep the confirmation and
        // exact-target rules honest rather than asserting that the families are missing.
        var cancelWithoutKey = await fixture.RunAsync("--cli", "cancel", "--yes", "--json");
        Expect("cancel without a job key fails 2", cancelWithoutKey, 2);
        var settingsWithoutValue = await fixture.RunAsync("--cli", "settings", "set", "--yes", "--json");
        Expect("settings set without --settings fails 2", settingsWithoutValue, 2);
        var pairEnableWithoutConfirmation = await fixture.RunAsync("--cli", "pairs", "enable", "--pair",
            fixture.PairAId, "--json");
        Expect("a pair change without --yes fails 2", pairEnableWithoutConfirmation, 2);
        var unknownAccountFamily = await fixture.RunAsync("--cli", "account", "connect");
        Expect("an unknown account command family fails 2", unknownAccountFamily, 2);
        Require(!ContainsAny(cancelWithoutKey, settingsWithoutValue, pairEnableWithoutConfirmation,
                unknownAccountFamily), "confirmation failures disclose no protected value");
    }

    private static async Task BusyLockChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "state", "instance.lock");
        using (var held = PilotInstanceLock.Acquire(lockPath))
        {
            var factory = () => SyncCliRuntime.Acquire(lockPath);
            var result = await RunCliAsync(["--cli", "status", "--json"], factory);
            Expect("a state command fails 3 while the product lock is held", result, 3);
            Require(JsonErrorCode(result, "busy lock", "status") == "busy",
                "a held product lock reports the busy error code");
            Require(result.StandardError.Contains("Use the running application", StringComparison.Ordinal),
                "the busy error directs the caller to the running application instead of forcing it");
            var run = await RunCliAsync(["--cli", "run", "--pair", "pair-any", "--yes"], factory);
            Expect("a state-changing command fails 3 while the product lock is held", run, 3);
            var remove = await RunCliAsync(["--cli", "pairs", "remove", "--pair", "pair-any",
                "--archive", Path.Combine(root, "archive"), "--yes", "--json"], factory);
            Expect("pair removal fails 3 while the product lock is held", remove, 3);
            Require(remove.StandardError.Contains("Close Sync Shark and retry", StringComparison.Ordinal) &&
                    !remove.StandardError.Contains("Use the running application", StringComparison.Ordinal),
                "pair removal offers the supported close-and-retry route rather than the running app");
            Require(held.StateAuthority.LockPath == lockPath, "the test still owns the exact lock path");
        }
        using var released = PilotInstanceLock.Acquire(lockPath);
        Require(released.StateAuthority.LockPath == lockPath, "the busy command released nothing it did not own");
    }

    private static async Task RunGuardChecksAsync(CliFixture fixture)
    {
        var accounts = await fixture.RunAsync("--cli", "accounts", "list", "--json");
        Expect("accounts list exits 0 in the run fixture", accounts, 0);
        var accountData = JsonData(accounts, "run fixture accounts", "accounts list");
        Require(accountData["accounts"]!.AsArray().Any(account =>
                    account!["connectionId"]!.GetValue<string>() == fixture.PrimaryConnectionId &&
                    account!["pairCount"]!.GetValue<int>() == 1),
            "an empty pair connection id is attributed to the primary account");
        Require(accountData["accounts"]!.AsArray().Any(account =>
                    account!["connectionId"]!.GetValue<string>() == fixture.SecondaryConnectionId &&
                    account!["pairCount"]!.GetValue<int>() == 1),
            "an explicit pair connection id is attributed to that exact account");
        var historyBefore = await RunHistoryCountAsync(fixture.Root);
        var reviewPair = fixture.PairCId;
        var review = await fixture.RunAsync("--cli", "run", "--pair", reviewPair, "--yes", "--json");
        Expect("run refuses an unreviewed first sync with 5", review, 5);
        Require(JsonErrorCode(review, "run first-sync guard", "run") == "review_required",
            "the first-sync guard reports review_required");
        Require(review.StandardError.Contains("first sync", StringComparison.OrdinalIgnoreCase),
            "the first-sync guard explains the required in-app review");
        Require(await RunHistoryCountAsync(fixture.Root) == historyBefore,
            "a refused first sync starts no run and records no command request");

        var humanReview = await fixture.RunAsync("--cli", "run", "--pair", reviewPair, "--yes");
        Expect("the first-sync guard also fails 5 in human mode", humanReview, 5);
        Require(humanReview.StandardOutput.Length == 0, "a refused run keeps standard output clean");

        var noApproval = await fixture.RunAsync("--cli", "run", "--pair", fixture.PairDId);
        Expect("run without --yes fails 2", noApproval, 2);
        Require(await RunHistoryCountAsync(fixture.Root) == historyBefore,
            "a run without --yes starts no run");

        var unknown = await fixture.RunAsync("--cli", "run", "--pair", "pair-unknown-run", "--yes", "--json");
        Expect("run fails 4 for an unknown pair id", unknown, 4);
        Require(JsonErrorCode(unknown, "run unknown pair", "run") == "missing_target",
            "run never substitutes another pair for an unknown exact id");
        Require(await RunHistoryCountAsync(fixture.Root) == historyBefore,
            "an unknown run target records no command request");

        // Real operational failure: the pair's account has no protected credential in this environment,
        // so the existing runtime fails before any provider effect. A dispatch that started work and
        // failed must not report success.
        var failure = await fixture.RunAsync("--cli", "run", "--pair", fixture.PairDId, "--yes", "--json");
        Expect("a failed operation exits 6", failure, 6);
        Require(JsonErrorCode(failure, "run operation failure", "run", hasReceipt: true) == "operation_failed",
            "a failed operation reports operation_failed");
        Require(failure.StandardError.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "a failed operation explains the failure on standard error");
        var historyAfter = await RunHistoryCountAsync(fixture.Root);
        var failureData = Envelope(failure, "run operation failure receipt")["data"];
        Require(failureData?["pairId"]?.GetValue<string>() == fixture.PairDId &&
                (historyAfter == historyBefore || failureData?["runId"]?.GetValue<string>() is { Length: > 0 }),
            "real normal failure retains its exact pair and accepted run identity when run history was created");
        Require(historyAfter > historyBefore || failure.StandardError.Contains("credential", StringComparison.OrdinalIgnoreCase),
            "a real run attempt is retained in run history or fails before a run lease is taken");
        Require(!ContainsAny(review, failure, noApproval, unknown),
            "run failures disclose no credential reference or token");

        var planForUnscannedPair = await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairDId, "--json");
        Expect("plan exits 0 for a pair without retained inventories", planForUnscannedPair, 0);
        var planData = JsonData(planForUnscannedPair, "plan without inventories", "plan");
        Require(planData["hasCompleteInventory"]!.GetValue<bool>() == false &&
                planData["returned"]!.GetValue<int>() == 0,
            "plan reports missing retained inventories instead of refreshing them");
    }

    // R477/T3: the undecided-review requirement proven on its own fresh isolated root through the real
    // observation mechanisms. The pair is unapproved and genuinely actionable, so the refusal under test is
    // the adapter's undecided-items requirement rather than the already-reviewed guard, and no effect of any
    // kind may occur: every item stays pending and the pair stays unapproved.
    private static async Task FirstSyncUndecidedChoiceChecksAsync(string root)
    {
        var fixture = await BuildRunFixtureAsync(root);
        var localRoot = Path.Combine(root, "review root");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "undecided-local-only.txt"), "undecided local payload");
        var remote = new FakeSyncRemoteStore();
        remote.Put("undecided-dropbox-only.txt", "undecided remote payload");
        var pair = Pair("pair-cli-c", "CLI review pair", localRoot, "/CLI C", "id:cli-c", string.Empty)
            with { InitialReviewApproved = false };
        using (var instance = PilotInstanceLock.Acquire(fixture.LockPath))
        using (var host = new SyncAuthorityRuntimeHost(instance.StateAuthority,
                   new HttpClient(new NoNetworkHandler())))
        {
            await host.LoadPairsAsync();
            await ObserveAsync(host.Authority, pair, remote, host.CapabilityProfileIdForTest);
        }
        var historyBefore = await RunHistoryCountAsync(fixture.Root);
        var planBefore = JsonData(await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairCId, "--json"),
            "undecided plan", "plan");
        Require(planBefore["hasCompleteInventory"]!.GetValue<bool>() &&
                planBefore["localOnlyItems"]!.GetValue<int>() == 1 &&
                planBefore["dropboxOnlyItems"]!.GetValue<int>() == 1,
            "the isolated unapproved pair retains a genuinely actionable first-sync plan: " +
            JsonSerializer.Serialize(planBefore));
        var localFilesBefore = Directory.GetFiles(localRoot).OrderBy(name => name).ToArray();
        var undecided = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairCId,
            "--yes", "--json");
        Expect("first-sync apply without explicit choices fails 5 for an actionable unapproved plan", undecided, 5);
        Require(JsonErrorCode(undecided, "first-sync apply undecided", "first-sync apply") == "review_required",
            "first-sync apply names the undecided items as a review requirement");
        var planAfter = JsonData(await fixture.RunAsync("--cli", "plan", "--pair", fixture.PairCId, "--json"),
            "undecided plan after the refusal", "plan");
        Require(JsonSerializer.Serialize(planAfter) == JsonSerializer.Serialize(planBefore),
            "the refused apply left every actionable item pending in the retained plan");
        Require(Directory.GetFiles(localRoot).OrderBy(name => name).SequenceEqual(localFilesBefore) &&
                !File.Exists(Path.Combine(localRoot, "undecided-dropbox-only.txt")),
            "the refused apply produced no local-file effect");
        Require(remote.UploadCount == 0 && remote.DownloadCount == 0 && remote.ArchiveCount == 0,
            "the refused apply reached no content transfer in the fixture's bound remote store");
        Require(await RunHistoryCountAsync(fixture.Root) == historyBefore,
            "a refused undecided apply starts no run and records no command request");
        var stillUnapproved = JsonData(await fixture.RunAsync("--cli", "pairs", "show", "--pair", fixture.PairCId,
            "--json"), "undecided pair state", "pairs show");
        Require(stillUnapproved["firstSyncReviewRequired"]!.GetValue<bool>(),
            "the refused apply left the pair unapproved for its first sync");
    }

    private static async Task CancellationChecksAsync(CliFixture fixture)
    {
        var historyBefore = await RunHistoryCountAsync(fixture.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await fixture.RunAsync(cancellation.Token, "--cli", "run", "--pair", fixture.PairDId, "--yes", "--json");
        Expect("a canceled run exits 130", result, 130);
        Require(JsonErrorCode(result, "canceled run", "run") == "canceled",
            "a canceled run reports the canceled error code");
        Require(await RunHistoryCountAsync(fixture.Root) == historyBefore,
            "a canceled run never dispatched the operation");
        using var released = PilotInstanceLock.Acquire(fixture.LockPath);
        Require(released.StateAuthority.LockPath == fixture.LockPath,
            "a canceled run releases the exclusive product lock");
    }

    // ---- the shared local Create Folder action through the real parser, dispatcher and exit codes ---
    private static async Task LocalFolderChecksAsync(CliFixture fixture, string root)
    {
        var parent = Path.Combine(root, "local-create", "Parent");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, "Existing"));
        File.WriteAllText(Path.Combine(parent, "Blocked.txt"), "existing file content");
        var before = Entries(parent);

        var noYes = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
            "--name", "Refused", "--json");
        Expect("create-local without --yes is refused with 2", noYes, 2);
        var noName = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent, "--yes", "--json");
        Expect("create-local without --name is refused with 2", noName, 2);
        var noLocal = await fixture.RunAsync("--cli", "folders", "create-local", "--name", "Refused", "--yes", "--json");
        Expect("create-local without --local is refused with 2", noLocal, 2);
        Require(Entries(parent).SequenceEqual(before),
            "a missing confirmation or a missing exact input created nothing");

        var refusedParents = new (string Label, string Value, int Exit)[]
        {
            ("a relative parent", "relative" + Path.DirectorySeparatorChar + "Parent", 2),
            ("a device parent", @"\\?\C:\Parent", 2),
            ("a UNC network parent", @"\\server\share\Parent", 2),
            ("the volume root", Path.GetPathRoot(parent)!, 2),
            ("a file instead of a folder", Path.Combine(parent, "Blocked.txt"), 4),
            ("a parent that does not exist", Path.Combine(parent, "Missing"), 4)
        };
        foreach (var refused in refusedParents)
        {
            var refusedParent = await fixture.RunAsync("--cli", "folders", "create-local", "--local",
                refused.Value, "--name", "Refused Folder", "--yes", "--json");
            Expect("create-local refuses " + refused.Label + " with " + refused.Exit, refusedParent, refused.Exit);
            Require(!refusedParent.StandardOutput.Contains("\"ok\":true", StringComparison.Ordinal),
                "create-local refuses " + refused.Label + " without a success envelope");
            Require(Entries(parent).SequenceEqual(before),
                "create-local refuses " + refused.Label + " without touching the real parent folder");
        }

        var reservedName = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
            "--name", "CON", "--yes", "--json");
        Expect("create-local refuses a reserved device name with 2", reservedName, 2);
        Require(reservedName.StandardError.Contains(LocalFolderActions.NameRule, StringComparison.Ordinal),
            "the CLI refusal is the shared LocalFolderActions name rule itself, so both surfaces cannot drift");
        Require(JsonErrorCode(reservedName, "refused reserved name", "folders create-local") == "usage",
            "a refused name carries the typed usage failure code");

        foreach (var name in new[] { "Sub/Name", "Sub" + Path.DirectorySeparatorChar + "Name", "Bad:Name",
                     "prn.txt", "Name.", "Name ", "   ", "/Name", "Name/", ".", "..", "", new string('x', 256) })
        {
            var refusedName = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
                "--name", name, "--yes", "--json");
            Expect("create-local refuses the name '" + name + "' with 2", refusedName, 2);
        }
        Require(Entries(parent).SequenceEqual(before), "no refused or silently altered name created anything");

        var occupiedFolder = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
            "--name", "Existing", "--yes", "--json");
        Expect("create-local refuses an existing folder name with 6", occupiedFolder, 6);
        Require(occupiedFolder.StandardError.Contains("already exists", StringComparison.Ordinal),
            "the refused collision explains that the exact name is already taken");
        var occupiedFile = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
            "--name", "Blocked.txt", "--yes", "--json");
        Expect("create-local refuses an existing file name with 6", occupiedFile, 6);
        Require(File.ReadAllText(Path.Combine(parent, "Blocked.txt")) == "existing file content" &&
                Directory.Exists(Path.Combine(parent, "Existing")),
            "the refused collisions left the existing folder and file exactly as they were");
        Require(Entries(parent).SequenceEqual(before), "no collision attempt added or removed anything");

        var success = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
            "--name", "Created By CLI", "--yes", "--json");
        Expect("create-local exits 0", success, 0);
        var data = JsonData(success, "create-local", "folders create-local");
        var created = Path.Combine(parent, "Created By CLI");
        Require(data["parentPath"]!.GetValue<string>() == parent &&
                data["name"]!.GetValue<string>() == "Created By CLI" &&
                data["path"]!.GetValue<string>() == created,
            "create-local returns the exact parent, the exact single name and the actual created path");
        Require(Directory.Exists(created), "the exact requested folder exists on disk after the CLI success");
        Require(Entries(parent).SequenceEqual(before.Append(created).OrderBy(item => item,
                StringComparer.OrdinalIgnoreCase)),
            "the CLI success created exactly that one folder and changed nothing else in the parent");

        var human = await fixture.RunAsync("--cli", "folders", "create-local", "--local", parent,
            "--name", "Human Render", "--yes");
        Expect("create-local human output exits 0", human, 0);
        Require(human.StandardOutput.Contains("Created " + Path.Combine(parent, "Human Render"),
                    StringComparison.Ordinal) &&
                human.StandardOutput.Contains("pair, account and sync state unchanged", StringComparison.Ordinal),
            "the human output names the exact created path and states that no pair, account or sync state changed");

        var productState = Throws<ArgumentException>(() =>
            LocalFolderActions.RequireExistingLocalParent(SyncSharkRuntimePaths.StateRoot),
            "the shared parent rule must refuse Sync Shark product state");
        Require(productState.Message.Contains("product or state storage", StringComparison.Ordinal),
            "the shared parent rule refuses Sync Shark product state before any creation is attempted");
        Throws<ArgumentException>(() => LocalFolderActions.RequireExactFolderName("Sub/Name"),
            "the shared name rule must refuse a separator");
        Throws<ArgumentException>(() => LocalFolderActions.RequireExistingLocalParent(@"\\server\share"),
            "the shared parent rule must refuse a network path");
        Require(LocalFolderActions.Create(parent, "Shared Action Folder") ==
                Path.Combine(parent, "Shared Action Folder") &&
                Directory.Exists(Path.Combine(parent, "Shared Action Folder")),
            "the shared action itself creates exactly one folder and returns its exact path");
    }

    private static string[] Entries(string folder) => Directory.EnumerateFileSystemEntries(folder)
        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();

    private static TException Throws<TException>(Action action, string label) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(label + ": threw " + error.GetType().Name + " instead of " +
                                                typeof(TException).Name + ": " + error.Message);
        }
        throw new InvalidOperationException(label + ": no " + typeof(TException).Name + " was thrown.");
    }

    // ---- full CLI coverage and the executable parity check ---------------------------------------
    private static async Task FullCliChecksAsync(CliFixture fixture, string? capabilitiesPath)
    {
            var capabilities = await fixture.RunAsync("--cli", "capabilities", "--json");
            Expect("capabilities exits 0", capabilities, 0);
            var capabilityData = JsonData(capabilities, "capabilities", "capabilities");
            Require(capabilityData["commands"]!.AsArray().Count == SyncCliRegistry.Rows.Count,
                "capabilities publishes every registered command exactly once");
            Require(SyncCliRegistry.Rows.Single(row => row.Command == "run").Effect.Contains(
                    "retained completed-file results if a later scan is cancelled", StringComparison.Ordinal),
                "The shared CLI capability contract states that cancelling a later scan preserves completed file outcomes.");
            foreach (var command in new[] { "conflicts resolve", "conflicts resolve-selection" })
                Require(SyncCliRegistry.Rows.Single(row => row.Command == command).Usage.Contains("--timeout-seconds") &&
                        SyncCliRegistry.Rows.Single(row => row.Command == command).Options.Contains("--timeout-seconds"),
                    "Explicit conflict resolution supports the shared bounded deadline: " + command);
            foreach (var choice in new[] { Massdex.SyncShark.V2.JournalSizing.Enlarge, Massdex.SyncShark.V2.JournalSizing.NotNow, Massdex.SyncShark.V2.JournalSizing.Never })
            {
                var journal = SyncCliRegistry.Parse(["--cli", "conflicts", "resolve", "--pair", "p1", "--path", "C:",
                    "--key", "n1", "--generation", "0", "--choice", choice, "--yes"]);
                Require(journal.Choice == choice && journal.Generation == 0 && journal.Path == "C:", "Journal choices preserve exact target and notice guard");
            }
            // Structural inventory parity: the runtime blocked set must equal the registry's blocked set, no
            // implemented command may appear as not implemented, and every blocked entry must be published.
            var notImplemented = capabilityData["notImplemented"]!.AsArray();
            Require(notImplemented.Count == SyncCliRegistry.Blocked.Count,
                "capabilities publishes exactly the registry's blocked entries (" + notImplemented.Count + ")");
            Require(SyncCliRegistry.Rows.All(row => !SyncCliRegistry.Blocked.Any(entry =>
                    entry.Key.Contains(row.Command, StringComparison.Ordinal))),
                "no implemented command is published as not implemented");
            Require(SyncCliRegistry.Blocked.All(entry => notImplemented.Any(item =>
                    item!.GetValue<string>().StartsWith(entry.Key, StringComparison.Ordinal))),
                "every blocked family is published with its exact reason");

            var stateFree = await RunCliAsync(["--cli", "capabilities"], NeverOpensState);
            Expect("capabilities never opens product state", stateFree, 0);
            Require(SyncCli.IsStateFreeInvocation(["--cli", "capabilities"]) &&
                    SyncCli.IsStateFreeInvocation(["--cli", "help"]) &&
                    SyncCli.IsStateFreeInvocation(["--cli", "status", "--bogus"]) &&
                    !SyncCli.IsStateFreeInvocation(["--cli", "status"]),
                "only state-free and invalid invocations skip the runtime");

            foreach (var row in SyncCliRegistry.Rows)
            {
                Require(row.Usage.StartsWith(row.Command, StringComparison.Ordinal),
                    "the registry usage text starts with its command: " + row.Command);
                Require(row.Effect.Length > 0 && row.Requires.Length > 0 && row.Mode.Length > 0,
                    "the registry documents effect, prerequisite and mode: " + row.Command);
                if (row.RequiresYes)
                    Require(row.Usage.Contains("--yes", StringComparison.Ordinal),
                        "a mutating command documents its confirmation: " + row.Command);
            }
            Require(SyncCliRegistry.Rows.Select(row => row.Command).Distinct(StringComparer.Ordinal).Count() ==
                    SyncCliRegistry.Rows.Count, "the command registry has no duplicate command");
            var duplicateRow = SyncCliRegistry.Rows.Single(row => row.Command == "pairs duplicate");
            Require(duplicateRow.Usage.Contains("--settings", StringComparison.Ordinal) &&
                duplicateRow.Options.Contains("--settings", StringComparison.Ordinal),
                "the duplicate command exposes the shared --settings policy option in its usage and allowed options");
            Require(duplicateRow.Mutating && duplicateRow.RequiresYes && duplicateRow.RequiresPair &&
                duplicateRow.Usage.Contains("--account", StringComparison.Ordinal) &&
                duplicateRow.Usage.Contains("--generation", StringComparison.Ordinal),
                "the duplicate command keeps its account, generation, pair, mutation and confirmation gates with the settings option");
            var help = SyncCli.HelpText();
            var bulkRevoke = await RunCliAsync(["--cli", "sharing", "revoke-selected", "--settings", "{\"links\":[]}", "--json"], NeverOpensState);
            Expect("selected link revocation requires confirmation before opening state", bulkRevoke, 2);
            Require(SyncCliRegistry.Rows.All(row => help.Contains(row.Usage, StringComparison.Ordinal)),
                "help lists every registered command from the same registry");
            if (capabilitiesPath is not null)
            {
                Require(File.Exists(capabilitiesPath), "the capability inventory file exists: " + capabilitiesPath);
                var document = await File.ReadAllTextAsync(capabilitiesPath);
                var missing = SyncCliRegistry.Rows
                    .Where(row => !document.Contains("| `" + row.Command + "` |", StringComparison.Ordinal))
                    .Select(row => row.Command).ToArray();
                Require(missing.Length == 0, "every registry command has a capability row: " +
                    (missing.Length == 0 ? "complete" : string.Join(", ", missing)));
            }

            var settingsSet = await fixture.RunAsync("--cli", "settings", "set", "--yes", "--json", "--settings",
                "{\"automation\":{\"intervalMinutes\":7},\"transfer\":{\"maximumConcurrentPairs\":3},\"recoveryMaxBytes\":21474836480}");
            Expect("settings set exits 0", settingsSet, 0);
            var settingsData = JsonData(settingsSet, "settings set", "settings set");
            Require(settingsData["automation"]!["intervalMinutes"]!.GetValue<int>() == 7 &&
                    settingsData["transfer"]!["maximumConcurrentPairs"]!.GetValue<int>() == 3 &&
                    settingsData["recoveryMaxBytes"]!.GetValue<long>() == 21474836480L,
                "settings set persists the stated values, including the 64-bit recovery cap, and returns the retained readback");
            Require(!string.IsNullOrWhiteSpace(settingsData["dataLocation"]!.GetValue<string>()),
                "settings readback includes the effective data location");
            var mixedLocation = await fixture.RunAsync("--cli", "settings", "set", "--yes", "--settings",
                "{\"dataLocation\":\"F:\\\\SyncSharkData\",\"recoveryMaxBytes\":1}");
            Expect("data location must be changed separately", mixedLocation, 2);
            var unknownSection = await fixture.RunAsync("--cli", "settings", "set", "--yes", "--settings",
                "{\"unknown\":{}}");
            Expect("an unknown settings section fails 2", unknownSection, 2);
            var notAnObject = await fixture.RunAsync("--cli", "settings", "set", "--yes", "--settings", "[1]");
            Expect("a non-object settings value fails 2", notAnObject, 2);

            var ignoresSet = await fixture.RunAsync("--cli", "ignores", "set", "--pair", fixture.PairAId, "--yes",
                "--json", "--rules", "FileExtension:tmp;FileName:desktop.ini");
            Expect("ignores set exits 0", ignoresSet, 0);
            var ignores = JsonData(ignoresSet, "ignores set", "ignores set");
            Require(ignores["count"]!.GetValue<int>() == 2,
                "ignores set validates and retains both friendly rules");
            var ignoresList = await fixture.RunAsync("--cli", "ignores", "list", "--pair", fixture.PairAId, "--json");
            Expect("ignores list exits 0", ignoresList, 0);
            Require(JsonData(ignoresList, "ignores list", "ignores list")["count"]!.GetValue<int>() == 2 &&
                JsonData(ignoresList, "ignores list", "ignores list")["rules"]!.ToJsonString() ==
                    ignores["rules"]!.ToJsonString(),
                "ignores list reads the exact normalized rules back");
            var badKind = await fixture.RunAsync("--cli", "ignores", "set", "--pair", fixture.PairAId, "--yes",
                "--rules", "NotAKind:x");
            Expect("an unknown ignore rule kind fails 2", badKind, 2);
            var cleared = await fixture.RunAsync("--cli", "ignores", "set", "--pair", fixture.PairAId, "--yes",
                "--rules", "none", "--json");
            Expect("ignores set none exits 0", cleared, 0);
            Require(JsonData(cleared, "ignores cleared", "ignores set")["count"]!.GetValue<int>() == 0,
                "ignores set none clears the retained rules");

            var disabled = await fixture.RunAsync("--cli", "pairs", "disable", "--pair", fixture.PairAId, "--yes",
                "--json");
            Expect("pairs disable exits 0", disabled, 0);
            Require(!JsonData(disabled, "pairs disable", "pairs disable")["enabled"]!.GetValue<bool>(),
                "pairs disable persists the pair state");
            Require(disabled.StandardError.Contains("Startup: checking the retained database schema", StringComparison.Ordinal) &&
                    !disabled.StandardError.Contains("SQLite PRAGMA quick_check", StringComparison.Ordinal) &&
                    disabled.StandardError.Contains("Pair " + fixture.PairAId + ": checking retained file effects and live leases", StringComparison.Ordinal) &&
                    disabled.StandardError.Contains("Pair " + fixture.PairAId + ": saved enabled=False", StringComparison.Ordinal) &&
                    !disabled.StandardOutput.Contains("Startup:", StringComparison.Ordinal),
                "cold pair pause streams actual database admission, exact target safety check and persisted completion on stderr only");
            var enabled = await fixture.RunAsync("--cli", "pairs", "enable", "--pair", fixture.PairAId, "--yes",
                "--json");
            Expect("pairs enable exits 0", enabled, 0);
            Require(JsonData(enabled, "pairs enable", "pairs enable")["enabled"]!.GetValue<bool>(),
                "pairs enable persists the pair state");

            var pause = await fixture.RunAsync("--cli", "pause", "--yes", "--json");
            Expect("global pause exits 0", pause, 0);
            var pauseData = JsonData(pause, "global pause", "pause");
            Require(pauseData["scope"]!.GetValue<string>() == "global" &&
                    pauseData["paused"]!.GetValue<bool>(),
                "global pause records the exact scope");
            var resume = await fixture.RunAsync("--cli", "resume", "--yes", "--json");
            Expect("global resume exits 0", resume, 0);
            Require(!JsonData(resume, "global resume", "resume")["paused"]!.GetValue<bool>(),
                "global resume clears the pause");
            var accountPause = await fixture.RunAsync("--cli", "pause", "--account", fixture.SecondaryConnectionId,
                "--minutes", "30", "--yes", "--json");
            Expect("account pause exits 0", accountPause, 0);
            var accountPauseData = JsonData(accountPause, "account pause", "pause");
            Require(accountPauseData["scope"]!.GetValue<string>() == "account" &&
                    accountPauseData["untilUtc"] is not null,
                "a timed account pause records its exact account and expiry");
            var accountRun = await fixture.RunAsync("--cli", "run", "--pair", fixture.PairBId, "--yes", "--json");
            Expect("a paused account refuses an otherwise runnable pair with 5", accountRun, 5);
            var accountResume = await fixture.RunAsync("--cli", "resume", "--account",
                fixture.SecondaryConnectionId, "--yes");
            Expect("account resume exits 0", accountResume, 0);

            var queue = await fixture.RunAsync("--cli", "queue", "list", "--json");
            Expect("queue list exits 0", queue, 0);
            Require(JsonData(queue, "queue list", "queue list")["returned"] is not null,
                "queue list reports the exact returned row count");
            var attention = await fixture.RunAsync("--cli", "attention", "list", "--json");
            Expect("attention list exits 0", attention, 0);
            Require(JsonData(attention, "attention list", "attention list")["pairIds"] is not null,
                "attention list reports needing-attention pairs and monitor rows");
            foreach(var monitor in (JsonArray)JsonData(attention,"attention list","attention list")["monitors"]!)
                Require(monitor!["pendingDirtyItems"] is not null && monitor["pendingEvents"] is null,
                    "monitor pending counts are labelled as coalesced dirty items in CLI JSON");
            Require(JsonData(attention, "attention list", "attention list")["items"] is JsonArray,
                "attention list includes explicit issue titles, descriptions and exact pair identities, not only pending operations");
            var runs = await fixture.RunAsync("--cli", "runs", "list", "--limit", "5", "--json");
            Expect("runs list exits 0", runs, 0);
            Require(JsonData(runs, "runs list", "runs list")["runs"] is not null,
                "runs list reports retained run status rows");
            var conflicts = await fixture.RunAsync("--cli", "conflicts", "list", "--pair", fixture.PairAId, "--json");
            Expect("conflicts list exits 0", conflicts, 0);
            Require(JsonData(conflicts, "conflicts list", "conflicts list")["items"] is not null,
                "conflicts list reports the retained projection for the exact pair");
            var recovery = await fixture.RunAsync("--cli", "recovery", "list", "--pair", fixture.PairAId, "--json");
            Expect("recovery list exits 0", recovery, 0);
            Require(JsonData(recovery, "recovery list", "recovery list")["itemCount"] is not null,
                "recovery list reports usage and the exact pair's catalog");
            var folders = await fixture.RunAsync("--cli", "folders", "tree", "--pair", fixture.PairAId, "--json");
            Expect("folders tree exits 0", folders, 0);
            Require(JsonData(folders, "folders tree", "folders tree")["folders"] is not null,
                "folders tree reports the pair's selection tree");
            var explorer = await fixture.RunAsync("--cli", "explorer", "status", "--json");
            Expect("explorer status exits 0", explorer, 0);
            var explorerStatus = JsonData(explorer, "explorer status", "explorer status");
            Require(explorerStatus["installedProcess"] is not null &&
                    !explorerStatus["installedProcess"]!.GetValue<bool>() &&
                    explorerStatus["pairCount"]!.GetValue<int>() == 2,
                "explorer status reports the shared installed-process admission (false for this isolated test host) and pair count");
            var approve = await fixture.RunAsync("--cli", "first-sync", "approve", "--pair", fixture.PairAId,
                "--yes", "--json");
            Expect("first-sync approve exits 0 for a retained reviewed plan", approve, 0);
            Require(!JsonData(approve, "first-sync approve", "first-sync approve")["firstSyncReviewRequired"]!
                    .GetValue<bool>(), "first-sync approve records the reviewed decision");
            // R477/T3: the approve immediately above already reviewed this pair, so a later apply without
            // choices is an already-reviewed refusal (exit 6) rather than the undecided review requirement.
            // Pair A's retained identifiers are identical on both sides, which under R473-5 legitimately need
            // no decision. Whatever the wording, the refusal may produce no effect at all.
            var approvedEffectsBefore = fixture.RemoteEffects;
            var approvedFilesBefore = Directory.GetFiles(fixture.LocalRootA).OrderBy(name => name).ToArray();
            var alreadyReviewed = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairAId,
                "--yes", "--json");
            Expect("first-sync apply on an already-reviewed pair fails 6", alreadyReviewed, 6);
            Require(JsonErrorCode(alreadyReviewed, "first-sync apply already reviewed", "first-sync apply") ==
                    "operation_failed",
                "an apply on an already-reviewed pair reports operation_failed");
            Require(fixture.RemoteEffects == approvedEffectsBefore &&
                    Directory.GetFiles(fixture.LocalRootA).OrderBy(name => name).SequenceEqual(approvedFilesBefore),
                "an apply on an already-reviewed pair produced no provider or local-file effect");
            var badChoice = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairAId,
                "--yes", "--choices", "{\"note-00.txt\":\"sideways\"}");
            Expect("an unknown per-item choice fails 2", badChoice, 2);
            var duplicateChoice = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairAId,
                "--yes", "--choices", "{\"note-00.txt\":\"no-change\",\"note-00.txt\":\"download\"}");
            Expect("duplicate choices fail 2 instead of replacing earlier consent", duplicateChoice, 2);
            var stalePlan = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairAId,
                "--yes", "--choices", "{\"__plan\":\"not-the-reviewed-generation\"}");
            Expect("a stale reviewed-plan identity fails 5", stalePlan, 5);
            var staleRevision = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairAId,
                "--yes", "--generation", "999", "--allow-recommended");
            Expect("a changed pair policy revision fails 5", staleRevision, 5);
            var recommended = await fixture.RunAsync("--cli", "first-sync", "apply", "--pair", fixture.PairAId,
                "--yes", "--allow-recommended", "--json");
            Require(recommended.ExitCode is 0 or 6,
                "accepting recommendations reaches the guarded executor (provider outcome may be 6): " +
                recommended.ExitCode);
            // R477/T3: explicit recommendation acceptance must stay a real execution path, never a review
            // refusal and never a silent success that does not name the pair it acted on.
            if (recommended.ExitCode == 6)
                Require(JsonErrorCode(recommended, "first-sync recommended execution", "first-sync apply") ==
                        "operation_failed",
                    "explicitly accepting recommendations reached the guarded executor and failed there, not at review");
            else
                Require(JsonData(recommended, "first-sync recommended execution", "first-sync apply")["pairId"]!
                        .GetValue<string>() == fixture.PairAId,
                    "explicitly accepting recommendations executed a real reviewed first-sync apply");
            // R477/T3: the undecided-review oracle on its own fresh isolated root, with a genuinely
            // actionable unapproved plan, so the refusal under test is the undecided-items requirement.
            await FirstSyncUndecidedChoiceChecksAsync(Path.Combine(fixture.Root, "first-sync-undecided"));
            var runPause = await fixture.RunAsync("--cli", "run", "pause", "--yes", "--json");
            Expect("run pause exits 0 with a not-accepted result when no run is active", runPause, 0);
            var appExit = await fixture.RunAsync("--cli", "app", "exit", "--yes", "--json");
            Expect("app exit fails 5 when no application owns the runtime", appExit, 5);
            Require(JsonErrorCode(appExit, "app exit direct mode", "app exit") == "unsupported",
                "app exit never terminates a process it does not own");
            Require(!JsonData(runPause, "run pause", "run pause")["accepted"]!.GetValue<bool>(),
                "run pause never claims to have paused a run that does not exist");
            var unknownCancel = await fixture.RunAsync("--cli", "cancel", "--key", "no-such-run", "--yes", "--json");
            Expect("cancel with an unknown job id fails 4", unknownCancel, 4);
            var appShow = await fixture.RunAsync("--cli", "app", "show", "--yes", "--json");
            Expect("app show fails 5 when no application owns the runtime", appShow, 5);
            var badAvailability = await fixture.RunAsync("--cli", "availability", "set", "--pair", fixture.PairAId,
                "--path", "note-00.txt", "--availability", "sometimes", "--yes");
            Expect("an invalid availability value fails 2", badAvailability, 2);
        }

    private static void SyncCliBridgeProtocolChecks()
    {
            var now = DateTimeOffset.UtcNow;
            var nonce = Guid.NewGuid().ToString("N");
            var encoded = SyncCliBridgeProtocol.EncodeRequest(new SyncCliBridgeRequest("invoke", nonce, now,
                ["--cli", "status", "--json"], 4096.ToString("x")));
            var parsed = SyncCliBridgeProtocol.ParseRequest(encoded, now);
            Require(parsed.Nonce == nonce && parsed.Arguments.Count == 3 && parsed.Arguments[0] == "--cli",
                "the bridge accepts a well-formed bounded --cli request");
            var future = SyncCliBridgeProtocol.EncodeRequest(new SyncCliBridgeRequest("invoke", nonce,
                now + SyncCliBridgeProtocol.MaximumFutureClockSkew, ["--cli", "status"], "1000"));
            Require(SyncCliBridgeProtocol.ParseRequest(future, now).Nonce == nonce,
                "bounded forward clock skew remains usable");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(SyncCliBridgeProtocol.EncodeRequest(
                new SyncCliBridgeRequest("invoke", nonce, now.AddMinutes(5), ["--cli", "status"], "1000")), now), "expired"),
                "future dating cannot extend a request beyond replay retention");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(future,
                now + SyncCliBridgeProtocol.RequestRetention + TimeSpan.FromSeconds(1)), "expired"),
                "the latest initially valid request expires before its replay record can be evicted");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(new string('x', 70_000), now), "oversize"),
                "an oversize bridge payload is rejected before it is parsed");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest("{not json", now), "malformed"),
                "a malformed bridge payload is rejected");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(
                    encoded.Replace("\"kind\"", "\"kind\",\"extra\":1"), now), "malformed"),
                "an unknown bridge member is rejected");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(SyncCliBridgeProtocol.EncodeRequest(
                    new SyncCliBridgeRequest("invoke", nonce, now.AddMinutes(-10), ["--cli", "status"],
                        4096.ToString("x"))), now), "expired"),
                "an expired bridge request is rejected");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(SyncCliBridgeProtocol.EncodeRequest(
                    new SyncCliBridgeRequest("invoke", nonce, now, ["status"], 4096.ToString("x"))), now),
                    "malformed"),
                "a request that is not a --cli invocation is rejected");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseRequest(SyncCliBridgeProtocol.EncodeRequest(
                    new SyncCliBridgeRequest("shell", nonce, now, ["--cli", "status"], 4096.ToString("x"))), now),
                    "unsupported"),
                "a request kind outside the allowlist is rejected");
            var cache = new SyncCliBridgeReplayCache();
            Require(cache.TryAccept(nonce, now) && !cache.TryAccept(nonce, now),
                "a replayed correlation value is refused so an effect cannot repeat");
            var concurrentCache = new SyncCliBridgeReplayCache();
            var accepted = 0;
            Parallel.For(0, 128, _ =>
            {
                if (concurrentCache.TryAccept(nonce, now)) Interlocked.Increment(ref accepted);
            });
            Require(accepted == 1 && concurrentCache.Count == 1,
                "concurrent requests with one correlation are accepted exactly once");
            var reply = SyncCliBridgeProtocol.EncodeReply(new SyncCliBridgeReply("result", nonce, true, 0, "{}",
                null));
            Require(SyncCliBridgeProtocol.ParseReply(reply, nonce).Ok, "a matching reply is accepted");
            Require(Rejects(() => SyncCliBridgeProtocol.ParseReply(reply, Guid.NewGuid().ToString("N")),
                    "mismatch"), "a reply for another correlation value is ignored");
        }

    private static bool Rejects(Action action, string expectedCode)
    {
        try { action(); }
        catch (SyncCliBridgeException error) { return error.Code == expectedCode; }
        return false;
    }

    private static async Task<int> RunHistoryCountAsync(string productRoot)
    {

        var databasePath = Path.Combine(productRoot, "vNext", "state", "syncshark.db");
        if (!File.Exists(databasePath)) return 0;
        var rows = await new SyncAuthorityDatabase(databasePath).LoadRunHistoryAsync();
        return rows.Count;
    }

    private static bool ContainsAny(params CliResult[] results)
    {
        foreach (var result in results)
        {
            var text = result.StandardOutput + result.StandardError;
            if (text.Contains("account-access", StringComparison.Ordinal) ||
                text.Contains("refresh-token", StringComparison.Ordinal) ||
                text.Contains("Bearer ", StringComparison.Ordinal) ||
                text.Contains(WindowsDropboxRefreshTokenStore.RegisteredTarget, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // F1: records the exact arguments dispatch hands to the notice route. The v2 runtime keeps its own
    // "--path is required for item notices" refusal; this fake only proves dispatch never refuses first.
    private sealed class PairNoticeDispatchRuntime : SyncCliOperationsBase, ISyncCliRuntime, ISyncCliOperations
    {
        protected override SyncAuthorityRuntimeHost Host => throw new InvalidOperationException("No host needed.");
        protected override PilotStateAuthority StateAuthority => throw new InvalidOperationException("No state needed.");
        public override string Mode => "fixture";
        internal string? NoticePath { get; private set; }
        internal string? NoticeChoice { get; private set; }
        internal int DisposeCount { get; private set; }

        Task<SyncCliRunData> ISyncCliOperations.ResolveNoticeAsync(string pairId, string relativePath,
            string choice, string? conflictId, long? generation, string? accountId,
            CancellationToken cancellationToken, NoticeActionInput input)
        {
            NoticePath = relativePath;
            NoticeChoice = choice;
            return Task.FromResult(new SyncCliRunData(pairId, "Pair A", 0, 0, 0, 0, 0, false));
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static ISyncCliRuntime NeverOpensState() =>
        throw new InvalidOperationException("This command must not open product state or any file.");

    // DIAG354-4: the diagnostics command is one registry read command over the one shared in-app
    // diagnostic action. These checks enter the real parser, the real registry, the real dispatcher and the
    // real exit-code mapping; no provider, credential, signing or installed effect is exercised.
    private static async Task DiagnosticsCommandChecksAsync(string root)
    {
        var row = SyncCliRegistry.Rows.Single(candidate => candidate.Command == "diagnostics run");
        Require(row.Kind == SyncCliRequestKind.State && row.NeedsApplication && !row.Mutating &&
                !row.RequiresYes && !row.RequiresPair && row.Options.Length == 0 && row.Approval == "none" &&
                row.Mode == "running-application",
            "diagnostics run is one registry read command requiring the running application, no confirmation and no options");
        Require(SyncCli.RunningApplicationCommands.Contains("diagnostics run") &&
                SyncCli.SupportedCommands.Contains("diagnostics run") &&
                SyncCli.HelpText().Contains(row.Usage, StringComparison.Ordinal),
            "the published help and running-application inventory carry the diagnostics command from the one registry");
        Require(SyncCliRegistry.RequiresRunningApplication("diagnostics run") &&
                !SyncCliRegistry.RequiresRunningApplication("diagnostics") &&
                !SyncCliRegistry.RequiresRunningApplication("status"),
            "only the exact registered diagnostics command is classified as needing the running application");
        Require(SyncCliRegistry.RequiresStateFreeApplicationRefusal("diagnostics run") &&
                SyncCliRegistry.Rows.Where(row => SyncCliRegistry.RequiresStateFreeApplicationRefusal(row.Command))
                    .Select(row => row.Command).SequenceEqual(new[] { "diagnostics run" }),
            "only the diagnostics command declares the state-free no-application refusal, so no other command's order changes");
        Require(!SyncCli.IsStateFreeInvocation(["--cli", "diagnostics", "run"]) &&
                SyncCli.IsStateFreeInvocation(["--cli", "diagnostics", "run", "--bogus"]),
            "a well-formed diagnostics request is state-bearing while an invalid one is refused state-free");

        // An unsupported, unknown or duplicate option, and a stray argument, are refused before any runtime.
        foreach (var invalid in new[]
                 {
                     new[] { "--cli", "diagnostics", "run", "--yes" },
                     new[] { "--cli", "diagnostics", "run", "--pair", "anything" },
                     new[] { "--cli", "diagnostics", "run", "--bogus" },
                     new[] { "--cli", "diagnostics", "run", "--json", "--json" },
                     new[] { "--cli", "diagnostics", "run", "extra" }
                 })
        {
            var calls = 0;
            var invalidResult = await RunCliAsync(invalid, () => { calls++; return NeverOpensState(); });
            Expect("invalid diagnostics input fails 2 before any runtime: " + string.Join(' ', invalid),
                invalidResult, 2);
            Require(calls == 0, "an invalid diagnostics option never creates a runtime or opens product state");
        }
        var invalidJson = await RunCliAsync(["--cli", "diagnostics", "run", "--yes", "--json"],
            () => NeverOpensState());
        Expect("an unsupported diagnostics option fails 2 with --json", invalidJson, 2);
        Require(Envelope(invalidJson, "invalid diagnostics option")["error"]!["code"]!.GetValue<string>() == "usage",
            "the invalid diagnostics refusal is published as a usage error");

        // DIAG354-4/5: with no running application the command refuses through the existing
        // NeedsApplication gate before the runtime factory runs, so no product state, lock or file is created.
        var refusalRoot = Path.Combine(root, "diagnostics-refusal");
        var refusalLock = Path.Combine(refusalRoot, "state", "instance.lock");
        var factoryCalls = 0;
        var refused = await RunCliAsync(["--cli", "diagnostics", "run", "--json"],
            () => { factoryCalls++; return SyncCliRuntime.Acquire(refusalLock); });
        Expect("diagnostics run fails 5 when no application owns the runtime", refused, 5);
        Require(JsonErrorCode(refused, "diagnostics run with no application", "diagnostics run") == "unsupported",
            "the no-application refusal uses the existing registry NeedsApplication gate");
        Require(refused.StandardError.Contains(SyncCliRegistry.NeedsApplicationRefusal, StringComparison.Ordinal),
            "the no-application refusal states the exact running-application requirement");
        Require(factoryCalls == 0 && !File.Exists(refusalLock) && !Directory.Exists(refusalRoot),
            "the no-application refusal never creates the runtime, the lock file or any product state");
        Require(!File.Exists(Path.Combine(root, "diagnostics-report.txt")),
            "the diagnostics command creates no file");
    }

    private static Task<SyncAuthorityCommandAdmission> AdmitActivityFixtureAsync(
        SyncAuthorityDatabase authority, string pairId) =>
        authority.CreateAcceptedCommandAdmissionAsync(SyncAuthorityCommandReservation.Mint(),
            "run_pair", pairId, null, null, JsonSerializer.Serialize(new { pairIds = new[] { pairId } }),
            SyncAuthorityCommandPurpose.NormalEffect);

    private static async Task<CliFixture> BuildReadFixtureAsync(string root, int additionalRemoteRows = 0)
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "state", "instance.lock");
        var localRootA = Path.Combine(root, "local \u0394 docs", "pair one");
        var localRootB = Path.Combine(root, "local b");
        Directory.CreateDirectory(localRootA);
        Directory.CreateDirectory(localRootB);
        var remoteA = new FakeSyncRemoteStore();
        var remoteB = new FakeSyncRemoteStore();
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(localRootA, $"note-{index:00}.txt"), $"cli fixture {index}");
            remoteA.Put($"note-{index:00}.txt", $"cli fixture {index}");
            await File.WriteAllTextAsync(Path.Combine(localRootB, $"item-{index:00}.txt"), $"cli fixture b {index}");
            remoteB.Put($"item-{index:00}.txt", $"cli fixture b {index}");
        }
        for (var index = 0; index < additionalRemoteRows; index++)
            remoteA.Put($"retained-plan-{index:000}-{new string('x', 96)}.txt", "retained input only");
        using var instance = PilotInstanceLock.Acquire(lockPath);
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, new HttpClient(new NoNetworkHandler()));
        await host.LoadPairsAsync();
        var authority = host.Authority;
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var primary = PrimaryBinding();
        await connections.SaveNewAsync(primary);
        var secondary = SecondaryBinding();
        await connections.SaveAsync(secondary);
        // Explicit account routing is required before any planning authority can exist; the primary
        // pair carries the exact primary connection id rather than an implicit blank.
        var pairA = Pair("pair-cli-a", "CLI pair A", localRootA, "/CLI A", "id:cli-a", primary.ConnectionId);
        var pairB = Pair("pair-cli-b", "CLI pair B", localRootB, "/CLI B", "id:cli-b", secondary.ConnectionId);
        await RetainAsync(authority, pairA, select: true);
        await RetainAsync(authority, pairB, select: false);
        await ObserveAsync(authority, pairA, remoteA, host.CapabilityProfileIdForTest);
        await ObserveAsync(authority, pairB, remoteB, host.CapabilityProfileIdForTest);
        // Matching observations only establish baselines; no-op planning does not retain activity.
        // Seed a genuine legacy decision without endpoint/account context so the account filter
        // must not attribute it from pair B's current binding.
        var legacyLocal = (await authority.LoadLatestCompleteObservationAsync(pairB.Id, "local"))
            .Single(item => item.RelativePath == "item-00.txt");
        var legacyRemote = (await authority.LoadLatestCompleteObservationAsync(pairB.Id, "dropbox"))
            .Single(item => item.RelativePath == legacyLocal.RelativePath);
        await authority.RecordReconciliationDecisionAsync("cli-legacy-observation", pairB.Id,
            legacyLocal.ObjectId, legacyLocal.ScanGenerationId, legacyRemote.ScanGenerationId, null,
            "initial_observation", JsonSerializer.Serialize(new
            {
                path = legacyLocal.RelativePath,
                reason = "Legacy observation without recorded account identity"
            }));
        var logTime = DateTimeOffset.UtcNow;
        // Fresh history requires an admitted writer; raw identities are exact-replay-only.
        var logAdmission = await AdmitActivityFixtureAsync(authority, pairA.Id);
        var logWriter = await authority.OpenRunActivityWriterAsync(logAdmission);
        await authority.AppendRunActivityAsync([
            logWriter.Enqueue(new SyncActivityEvent("", logTime,
                [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
                primary.ConnectionId, pairA.Id, logWriter.CommandId)),
            logWriter.Enqueue(new SyncActivityEvent("", logTime,
                [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics], "Success", "Folder pair", "Sync finished",
                "Finished " + new string('x', 700) + " retained-tail-cause access_token=long-log-fixture-secret",
                primary.ConnectionId, pairA.Id, logWriter.CommandId))]);
        await authority.CloseRunActivityWriterAsync(logWriter);
        // Account-scoped history is attributed by identity retained on the event, never inferred
        // from today's pair binding. Keep the older unscoped observations above as rejection input.
        await authority.AppendDiagnosticActivityAsync(new SyncActivityEvent("", logTime,
            [SyncActivityCategories.Files], "Success", "File", "Downloaded", "Fixture file received",
            secondary.ConnectionId, pairB.Id, RelativePath: "account-scoped-event.txt"));
        var fixture = new CliFixture(root, lockPath, primary.ConnectionId, secondary.ConnectionId, pairA.Id, pairB.Id,
            null, null, localRootA, remoteA, remoteB) { LogRunId = logWriter.CommandId };
        return fixture;
    }

    private static async Task<CliFixture> BuildRunFixtureAsync(string root)
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "state", "instance.lock");
        var localRootC = Path.Combine(root, "review root");
        var localRootD = Path.Combine(root, "run root");
        Directory.CreateDirectory(localRootC);
        Directory.CreateDirectory(localRootD);
        using var instance = PilotInstanceLock.Acquire(lockPath);
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, new HttpClient(new NoNetworkHandler()));
        await host.LoadPairsAsync();
        var authority = host.Authority;
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var primary = PrimaryBinding();
        await connections.SaveNewAsync(primary);
        var secondary = SecondaryBinding();
        await connections.SaveAsync(secondary);
        // Both pairs are bound to the fixture's additional account, whose credential target is derived
        // from a fixture account id: no real Windows credential target is ever read by these checks.
        // An empty connection id is the retained legacy form of "the primary account": the review pair
        // exercises that routing form, and it is never executed, so the primary credential is never read.
        var pairC = Pair("pair-cli-c", "CLI review pair", localRootC, "/CLI C", "id:cli-c", string.Empty)
            with { InitialReviewApproved = false };
        var pairD = Pair("pair-cli-d", "CLI run pair", localRootD, "/CLI D", "id:cli-d", secondary.ConnectionId);
        await RetainAsync(authority, pairC, select: true);
        await RetainAsync(authority, pairD, select: false);
        return new CliFixture(root, lockPath, primary.ConnectionId, secondary.ConnectionId, pairC.Id, pairD.Id,
            pairC.Id, pairD.Id, localRootC, null, null);
    }

    private static DropboxConnectionBinding PrimaryBinding() =>
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "dbid:cli-primary", "cli-root-primary",
            "cli-home-primary", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();

    private static DropboxConnectionBinding SecondaryBinding()
    {
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:cli-secondary", "cli-root-secondary", "cli-home-secondary",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        return binding with { CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(binding.ConnectionId) };
    }

    private static SyncPair Pair(string id, string name, string localRoot, string dropboxRoot, string folderId,
        string connectionId)
    {
        using var lease = PilotDirectoryLease.Open(localRoot);
        return new SyncPair(id, name, localRoot, lease.Identity.VolumeSerialNumber, lease.Identity.FileId, dropboxRoot,
            folderId, ConnectionId: connectionId);
    }

    private static async Task RetainAsync(SyncAuthorityDatabase authority, SyncPair pair, bool select)
    {
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await authority.RetainPairDefinitionAsync(pair, select);
    }

    // The planner's capability-profile id is a real foreign key on every created operation, so the fixture
    // must plan with the identity the host actually retained instead of a fabricated literal.
    private static async Task ObserveAsync(SyncAuthorityDatabase authority, SyncPair pair,
        FakeSyncRemoteStore remote, string capabilityProfileId)
    {
        var localBinding = "cli-local-" + pair.Id;
        var dropboxBinding = "cli-dropbox-" + pair.Id;
        await authority.RegisterRootBindingAsync(localBinding, pair.Id, "local", 1,
            JsonSerializer.Serialize(new { pair.LocalVolumeSerialNumber, pair.LocalRootFileId }), pair.LocalRoot);
        await authority.RegisterRootBindingAsync(dropboxBinding, pair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = pair.DropboxFolderId }), pair.DropboxRoot);
        await new SyncAuthorityObservationCoordinator(pair, remote, authority, localBinding, dropboxBinding)
            .ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await new SyncAuthorityReconciliationPlanner(authority, new SyncAuthorityPlannerOptions(pair.PolicyRevision,
            capabilityProfileId, "cli-pilot-control", "cli-pilot-control"), admission: admission).PlanAsync(pair.Id);
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class CliFixture(string root, string lockPath, string primaryConnectionId,
        string secondaryConnectionId, string pairAId, string pairBId, string? pairCId, string? pairDId,
        string localRootA, FakeSyncRemoteStore? remoteA, FakeSyncRemoteStore? remoteB)
    {
        internal string LogRunId { get; init; } = string.Empty;
        internal string Root { get; } = root;
        internal string LockPath { get; } = lockPath;
        internal string PrimaryConnectionId { get; } = primaryConnectionId;
        internal string SecondaryConnectionId { get; } = secondaryConnectionId;
        internal string PairAId { get; } = pairAId;
        internal string PairBId { get; } = pairBId;
        internal string PairCId { get; } = pairCId ?? string.Empty;
        internal string PairDId { get; } = pairDId ?? string.Empty;
        internal string LocalRootA { get; } = localRootA;
        private readonly FakeSyncRemoteStore? _remoteA = remoteA;
        private readonly FakeSyncRemoteStore? _remoteB = remoteB;

        internal int RemoteEffects => (_remoteA?.UploadCount ?? 0) + (_remoteA?.DownloadCount ?? 0) +
            (_remoteA?.ArchiveCount ?? 0) + (_remoteB?.UploadCount ?? 0) + (_remoteB?.DownloadCount ?? 0) +
            (_remoteB?.ArchiveCount ?? 0);

        internal Func<ISyncCliRuntime> RuntimeFactory => () => SyncCliRuntime.Acquire(LockPath);

        internal Task<CliResult> RunAsync(params string[] args) => RunCliAsync(args, RuntimeFactory);

        internal Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args) =>
            RunCliAsync(args, RuntimeFactory, cancellationToken);
    }

    // ---- shared endpoint authoring: pairs create/edit --settings ------------------------------------

    /// <summary>
    /// The endpoint-authoring form is exercised through the real parser, the real dispatcher, the real
    /// directory lease and the real shared endpoint-binding boundary, on isolated fixture state. Fixture
    /// inputs stay inputs: the accepted document shape, the exact account and provider match, the verified
    /// namespace, the exact path match and the binding decision are all production code. Two local folders
    /// need no account at all, and the cloud resolution below is given folder inventory through the
    /// account-bound browser contract instead of a provider transport, so no provider request is made here.
    /// </summary>
    private static async Task EndpointAuthoringChecksAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Endpoint authoring checks need Windows NTFS folder identities.");
        var localA = Path.Combine(root, "left \u0394 endpoint");
        var localB = Path.Combine(root, "right endpoint");
        Directory.CreateDirectory(localA);
        Directory.CreateDirectory(localB);
        Directory.CreateDirectory(Path.Combine(localA, "child"));

        static string Quote(string value) => JsonSerializer.Serialize(value);
        static string Member(string name, string value) => Quote(name) + ":" + Quote(value);
        static string Local(string path) => "{" + Member("provider", "local") + "," + Member("path", path) + "}";
        static string LocalWithAccount(string path, string account) => "{" + Member("provider", "local") + "," +
            Member("path", path) + "," + Member("account", account) + "}";
        static string Endpoint(string provider, string path, string? account) => "{" +
            Member("provider", provider) + "," + Member("path", path) +
            (account is null ? string.Empty : "," + Member("account", account)) + "}";
        static string RawDocument(string left, string right, string rawDirection, string extra = "") => "{" +
            Quote("left") + ":" + left + "," + Quote("right") + ":" + right + extra + "," +
            Quote("direction") + ":" + rawDirection + "}";
        static string Document(string left, string right, string direction, string extra = "") =>
            RawDocument(left, right, Quote(direction), extra);
        async Task RefusesAsync<TException>(string label, Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException) { Require(true, label); return; }
            Require(false, label + " [no refusal was raised]");
        }

        // A malformed, mixed or unsupported document is refused by the parser before product state is
        // opened at all: NeverOpensState would throw if the runtime factory were ever reached.
        var refusals = new (string Label, string Settings)[]
        {
            ("malformed JSON", "{" + Quote("left") + ":"),
            ("a JSON array instead of an object", "[1]"),
            ("a null endpoint member", RawDocument("null", Local(localB), Quote("TwoWay"))),
            ("left without right", "{" + Quote("left") + ":" + Local(localA) + "," + Quote("direction") + ":" +
                Quote("TwoWay") + "}"),
            ("right without left", "{" + Quote("right") + ":" + Local(localB) + "," + Quote("direction") + ":" +
                Quote("TwoWay") + "}"),
            ("an unknown document member", Document(Local(localA), Local(localB), "TwoWay", "," +
                Member("extra", "1"))),
            ("a duplicate document member", Document(Local(localA), Local(localB), "TwoWay", "," +
                Quote("left") + ":" + Local(localB))),
            ("a missing direction", "{" + Quote("left") + ":" + Local(localA) + "," + Quote("right") + ":" +
                Local(localB) + "}"),
            ("a numeric direction", RawDocument(Local(localA), Local(localB), "2")),
            ("a null direction", RawDocument(Local(localA), Local(localB), "null")),
            ("an unknown direction", Document(Local(localA), Local(localB), "Sideways")),
            ("an unknown provider", Endpoint("gdrive", localA, null)),
            ("a non-string provider", "{" + Quote("provider") + ":7," + Quote("path") + ":" + Quote(localA) + "}"),
            ("a relative local path", Local("folder")),
            ("an empty local path", Local(string.Empty)),
            ("a local endpoint naming an account", LocalWithAccount(localA, "conn-1")),
            ("a local endpoint naming an empty account", LocalWithAccount(localA, string.Empty)),
            ("a cloud endpoint without an account", Endpoint("dropbox", "/Photos", null)),
            ("a cloud endpoint without a folder path", Endpoint("dropbox", string.Empty, "conn-1")),
            ("an unknown endpoint member", "{" + Member("provider", "local") + "," + Member("path", localA) + "," +
                Member("folder", "/Photos") + "}")
        };
        foreach (var (label, settings) in refusals)
        {
            var refused = await RunCliAsync(["--cli", "pairs", "create", "--settings", settings, "--yes", "--json"],
                NeverOpensState);
            Expect("an endpoint document is refused before state is opened: " + label, refused, 2);
            Require(JsonErrorCode(refused, "endpoint refusal " + label, "pairs create") == "usage",
                "an endpoint document refusal is one usage error: " + label);
        }
        var valid = Document(Local(localA), Local(localB), "TwoWay");
        var withoutConfirmation = await RunCliAsync(["--cli", "pairs", "create", "--settings", valid, "--json"],
            NeverOpensState);
        Expect("endpoint create without --yes is refused before state is opened", withoutConfirmation, 2);
        foreach (var (label, mixed) in new (string Label, string[] Extra)[]
        {
            ("--account", new[] { "--account", "dbid:any" }),
            ("--path", new[] { "--path", "/Photos" }),
            ("--local", new[] { "--local", localB })
        })
        {
            var mixedResult = await RunCliAsync(
                ["--cli", "pairs", "create", "--settings", valid, .. mixed, "--yes", "--json"], NeverOpensState);
            Expect("an endpoint document mixed with " + label + " is refused before state is opened", mixedResult, 2);
        }
        // The accepted document is bound on isolated fixture state through the one shared boundary.
        var fixtureRoot = Path.Combine(root, "fixture");
        var fixture = await BuildReadFixtureAsync(fixtureRoot);
        var unknownPair = await fixture.RunAsync("--cli", "pairs", "edit", "--pair", "no-such-pair", "--settings",
            valid, "--yes", "--json");
        Expect("an endpoint edit of an unknown pair fails 4", unknownPair, 4);
        Require(JsonErrorCode(unknownPair, "unknown endpoint edit pair", "pairs edit") == "missing_target",
            "an endpoint edit of an unknown pair reports a missing target instead of creating a pair");
        var created = JsonData(await fixture.RunAsync("--cli", "pairs", "create", "--settings", valid, "--name",
            "Endpoint pair", "--priority", "7", "--enable", "--yes", "--json"), "endpoint create", "pairs create");
        var pairId = created["id"]!.GetValue<string>();
        Require(SameFolder(created["localRoot"]!.GetValue<string>(), localA) &&
                SameFolder(created["dropboxRoot"]!.GetValue<string>(), localB) &&
                created["provider"]!.GetValue<string>() == SyncProviders.Local &&
                created["connectionId"]!.GetValue<string>().Length == 0 &&
                created["enabled"]!.GetValue<bool>() && created["priority"]!.GetValue<int>() == 7 &&
                created["policyRevision"]!.GetValue<long>() == 1 &&
                created["firstSyncReviewRequired"]!.GetValue<bool>(),
            "the endpoint pair retains both exact local roots and no account, and stays unapproved for its first sync");

        var revision = created["policyRevision"]!.GetValue<long>();
        const string directionPolicy = "{\"direction\":\"UploadOnly\"}";
        var policyEdit = SyncCliRegistry.Parse(["--cli", "pairs", "edit", "--pair", "legacy-pair",
            "--account", "legacy-account", "--generation", "1", "--settings", directionPolicy, "--yes"]);
        Require(policyEdit.Settings == directionPolicy && !SyncCliEndpointAuthoring.IsEndpointDocument(policyEdit.Settings),
            "a legacy direction-only edit accepts its account/generation and uses policy editing, not endpoint rebinding");
        var edited = JsonData(await fixture.RunAsync("--cli", "pairs", "edit", "--pair", pairId, "--settings",
            Document(Local(localA), Local(localB), "DownloadOnly"), "--generation", revision.ToString(), "--name",
            "Endpoint pair renamed", "--yes", "--json"), "endpoint edit", "pairs edit");
        Require(edited["id"]!.GetValue<string>() == pairId &&
                edited["direction"]!.GetValue<string>() == "DownloadOnly" &&
                edited["name"]!.GetValue<string>() == "Endpoint pair renamed" &&
                edited["policyRevision"]!.GetValue<long>() > revision,
            "the same two endpoints are edited in place: identity kept, direction and name changed, revision advanced");
        var stale = await fixture.RunAsync("--cli", "pairs", "edit", "--pair", pairId, "--settings",
            Document(Local(localA), Local(localB), "UploadOnly"), "--generation", revision.ToString(), "--yes",
            "--json");
        Expect("a stale endpoint edit revision fails 5", stale, 5);
        Require(JsonErrorCode(stale, "stale endpoint edit revision", "pairs edit") == "review_required",
            "a stale endpoint edit revision is reported as a review requirement, not a failure");
        Require(JsonData(await fixture.RunAsync("--cli", "pairs", "show", "--pair", pairId, "--json"),
                "pair after refusals", "pairs show")["direction"]!.GetValue<string>() == "DownloadOnly",
            "a refused endpoint document changes nothing");

        var same = await fixture.RunAsync("--cli", "pairs", "create", "--settings",
            Document(Local(localA), Local(localA), "TwoWay"), "--name", "Same endpoints", "--yes", "--json");
        Expect("the same folder on both sides fails 5", same, 5);
        var nested = await fixture.RunAsync("--cli", "pairs", "create", "--settings",
            Document(Local(localA), Local(Path.Combine(localA, "child")), "TwoWay"), "--name", "Nested endpoints",
            "--yes", "--json");
        Expect("a local folder nested in the other side fails 5", nested, 5);
        var reused = await fixture.RunAsync("--cli", "pairs", "create", "--settings", valid, "--name",
            "Reused endpoints", "--yes", "--json");
        Expect("the shared binding refuses an endpoint another pair already owns without a second pair", reused, 6);
        var containsAnother = await fixture.RunAsync("--cli", "pairs", "create", "--settings",
            Document(Local(fixtureRoot), Local(localB), "TwoWay"), "--name", "Contains another pair", "--yes",
            "--json");
        Expect("a folder that contains another retained pair's folder fails 5", containsAnother, 5);
        var missing = await fixture.RunAsync("--cli", "pairs", "create", "--settings",
            Document(Local(localA), Local(Path.Combine(root, "not there")), "TwoWay"), "--name", "Missing endpoint",
            "--yes", "--json");
        Expect("a local endpoint folder that does not exist fails 4", missing, 4);
        Require(JsonErrorCode(missing, "missing local endpoint folder", "pairs create") == "missing_target",
            "a missing local endpoint folder is reported as a missing target, never resolved to a near match");
        Require(JsonData(await fixture.RunAsync("--cli", "pairs", "list", "--json"), "pairs after endpoint refusals",
                "pairs list")["count"]!.GetValue<int>() == 3,
            "the endpoint pair is retained once and no refused document retained a pair");

        // Cloud resolution: the exact retained account, its verified namespace and the exact provider
        // folder, through the same account-bound browser contract the pair editor uses.
        var inventory = new EndpointBrowserInventory();
        inventory.Folders.Add(new DropboxFolderEntry("Photos", "/Photos", "id:photos"));
        inventory.Folders.Add(new DropboxFolderEntry("Archive", "/Archive", "id:archive"));
        SyncCliOperationsBase.EndpointBrowserFactoryForTest = (connectionId, _) =>
        {
            inventory.ConnectionIds.Add(connectionId);
            return Task.FromResult<IDropboxFolderBrowserService>(new EndpointFixtureBrowser(inventory));
        };
        try
        {
            await using var runtime = SyncCliRuntime.Acquire(fixture.LockPath);
            using (var lease = PilotDirectoryLease.Open(localA))
            {
                var local = runtime.ResolveLocalEndpoint(new SyncCliEndpointRequest(SyncEndpointKind.Local, localA, null));
                Require(local.IsLocal && local.LocalIdentity is { IsComplete: true } identity &&
                        identity.VolumeSerialNumber == lease.Identity.VolumeSerialNumber &&
                        identity.RootFileId == lease.Identity.FileId && SameFolder(local.LocalPath!, localA),
                    "a local endpoint carries the exact volume and root file id read through the real directory lease");
            }
            var cloud = await runtime.ResolveCloudEndpointAsync(new SyncCliEndpointRequest(SyncEndpointKind.Dropbox,
                "/Photos", fixture.PrimaryConnectionId), default);
            Require(cloud.IsCloud && cloud.Kind == SyncEndpointKind.Dropbox &&
                    cloud.ConnectionId == fixture.PrimaryConnectionId && cloud.NamespaceId == "cli-root-primary" &&
                    cloud.FolderId == "id:photos" && cloud.FolderPath == "/Photos" &&
                    inventory.ConnectionIds.SequenceEqual(new[] { fixture.PrimaryConnectionId }),
                "a cloud endpoint resolves through the exact retained account, its verified namespace and the exact folder identity");
            Require(inventory.Queries.SequenceEqual(new[] { "Photos" }),
                "cloud resolution asks the account-bound browser for the exact path's own name");
            var consulted = inventory.ConnectionIds.Count;
            await RefusesAsync<SyncCliTargetException>("an account that is not retained fails 4", () =>
                runtime.ResolveCloudEndpointAsync(new SyncCliEndpointRequest(SyncEndpointKind.Dropbox, "/Photos",
                    "dbid:not-retained"), default));
            await RefusesAsync<SyncCliTargetException>("an account of another provider fails 4", () =>
                runtime.ResolveCloudEndpointAsync(new SyncCliEndpointRequest(SyncEndpointKind.GoogleDrive,
                    "/My Drive", fixture.PrimaryConnectionId), default));
            Require(inventory.ConnectionIds.Count == consulted,
                "an unknown or foreign endpoint account is refused before its browser transport is consulted");
            await RefusesAsync<SyncCliTargetException>("a folder path with no exact match fails 4", () =>
                runtime.ResolveCloudEndpointAsync(new SyncCliEndpointRequest(SyncEndpointKind.Dropbox, "/photos/sub",
                    fixture.PrimaryConnectionId), default));
            inventory.Folders.Add(new DropboxFolderEntry("Photos", "/Photos", "id:photos-other"));
            await RefusesAsync<SyncCliAmbiguousTargetException>("two exact folder matches fail 4", () =>
                runtime.ResolveCloudEndpointAsync(new SyncCliEndpointRequest(SyncEndpointKind.Dropbox, "/Photos",
                    fixture.PrimaryConnectionId), default));
        }
        finally
        {
            SyncCliOperationsBase.EndpointBrowserFactoryForTest = null;
        }
    }

    private static bool SameFolder(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private sealed class EndpointBrowserInventory
    {
        internal List<DropboxFolderEntry> Folders { get; } = [];
        internal List<string> Queries { get; } = [];
        internal List<string> ConnectionIds { get; } = [];
    }

    /// <summary>
    /// The account-bound provider browser the endpoint checks inject: it answers the shared browser
    /// contract with fixed folder inventory and records what production resolution asked it for. It never
    /// substitutes a parser, a provider match, an identity check or a binding decision.
    /// </summary>
    private sealed class EndpointFixtureBrowser(EndpointBrowserInventory inventory) : IDropboxFolderBrowserService
    {
        public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage(inventory.Folders.ToArray(), null));

        public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage(Array.Empty<DropboxFolderEntry>(), null));

        public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
            CancellationToken cancellationToken = default)
        {
            inventory.Queries.Add(query);
            return Task.FromResult(new DropboxFolderPage(inventory.Folders.ToArray(), null));
        }

        public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage(Array.Empty<DropboxFolderEntry>(), null));

        public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var folder in inventory.Folders) yield return folder;
            await Task.CompletedTask;
        }

        public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The endpoint checks never create a provider folder.");
    }

    // Any provider request from these checks would be a defect: the fixture never supplies a real
    // credential, so a live Dropbox call is neither possible nor permitted here.
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "CLI fixture checks must not make a provider request: " + request.RequestUri);
    }
}
