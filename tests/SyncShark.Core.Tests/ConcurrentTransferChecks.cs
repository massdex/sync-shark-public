using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ConcurrentTransferChecks
{
    internal static async Task RunDirectorySchedulerChecksAsync()
    {
        static SyncAuthorityOperationSnapshot Folder(string id, string path,
            SyncAuthorityOperationKind kind = SyncAuthorityOperationKind.CreateLocal) => Snapshot(id, path) with
        {
            Kind = kind,
            Members =
            [new("source-" + id, "pair", "object-" + id, "source", "remote", null, null, null, path,
                "{\"objectKind\":\"directory\"}"),
             new("target-" + id, "pair", "object-" + id, "destination", "local", null, null, null, path,
                "{\"present\":false}")]
        };

        static bool Independent(SyncAuthorityOperationSnapshot left, SyncAuthorityOperationSnapshot right) =>
            SyncAuthorityOperationScheduler.Independent(left, right, allowDirectoryCreates: true);
        var a = Folder("folder-a", "a");
        var b = Folder("folder-b", "b");
        var entered = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = SyncAuthorityOperationScheduler.RunAsync(new[] { a, b }, item => item, async item =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothStarted.TrySetResult();
            await release.Task;
            return item.OperationId;
        }, (_, _) => Task.FromResult(true), CancellationToken.None, maximumInFlight: 2, allowDirectoryCreates: true);
        var overlapped = false;
        try { await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); overlapped = true; }
        catch (TimeoutException) { }
        finally { release.TrySetResult(); await work; }
        Require(overlapped, "independent directory creates reach two actual workers before either finishes");

        Require(!SyncAuthorityOperationScheduler.Independent(a, b), "default recovery/Rewind directory barrier unchanged");
        Require(Independent(a, b), "disjoint local directory creates overlap");
        Require(Independent(a, Folder("remote-b", "b", SyncAuthorityOperationKind.CreateRemote)),
            "disjoint remote directory creates use the same scheduler");
        Require(Independent(a, Snapshot("file-b", "b/file.txt")),
            "a disjoint file need not wait for an unrelated directory create");
        Require(!Independent(a, Folder("child", "a/child")), "directory ancestor fence");
        Require(!Independent(a, Snapshot("child-file", "a/file.txt")), "child file waits for its parent");
        Require(!Independent(a, b with { Members = a.Members }), "directory identity alias fence");
        Require(!Independent(a, b with { Dependencies = [new(a.OperationId, "must_complete")] }),
            "directory dependency fence");
        Require(!Independent(a, b with { SubtreeReservations = [new("pair", "local", "b")] }),
            "retained subtree reservation remains a barrier");
        foreach (var kind in new[] { SyncAuthorityOperationKind.MoveLocal, SyncAuthorityOperationKind.MoveRemote,
                     SyncAuthorityOperationKind.DeleteLocal, SyncAuthorityOperationKind.DeleteRemote,
                     SyncAuthorityOperationKind.UpdateLocal, SyncAuthorityOperationKind.UpdateRemote })
            Require(!Independent(a, b with { Kind = kind }), kind + " directory effect stays serial");
        Require(!Independent(a, b with { Status = SyncAuthorityOperationStatus.EffectIssuing }),
            "interrupted directory effect remains a recovery barrier");
        Require(!Independent(a, b with
        { Members = b.Members.Select(member => member with { ExpectedStateJson = "invalid-json" }).ToArray() }),
            "malformed directory authority fails closed");

        // Exercise the real pump's ordering, rather than only its classification helper.
        var parentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childEntered = false;
        var ordered = SyncAuthorityOperationScheduler.RunAsync(new[] { a, Folder("child", "a/child") }, item => item,
            async item =>
            {
                if (item.OperationId == a.OperationId) { parentEntered.TrySetResult(); await parentRelease.Task; }
                else childEntered = true;
                return item.OperationId;
            }, (_, _) => Task.FromResult(true), CancellationToken.None, maximumInFlight: 2, allowDirectoryCreates: true);
        try
        {
            await parentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!childEntered, "real child worker is not admitted while its parent is held");
        }
        finally { parentRelease.TrySetResult(); await ordered; }
        Require(childEntered, "real child worker runs after the parent completes");
        Console.WriteLine("PASS independent directory creates overlap; parent/child, identity, dependency, reservation, mutation and recovery barriers retained");
    }

    internal static async Task RunConnectionReuseChecksAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh connection-reuse fixture root.");
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("connection-reuse", "connection-reuse", "connection-reuse", "{}");
        var startupChecks = 0;
        var readOnlyHealthConnections = 0;
        SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest = connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only;";
            Require(Convert.ToInt64(command.ExecuteScalar()) == 1 &&
                    SQLitePCL.raw.sqlite3_db_readonly(connection.Handle!, "main") == 1,
                "health IO tuning remains confined to a native read-only, query-only connection");
            command.CommandText = "PRAGMA mmap_size;";
            var mapping = Convert.ToInt64(command.ExecuteScalar());
            Require(mapping >= 0 && mapping <= 2L * 1024 * 1024 * 1024,
                "health mapping is bounded; unsupported engines may retain buffered reads");
            command.CommandText = "PRAGMA cache_size;";
            Require(Convert.ToInt64(command.ExecuteScalar()) == -65536,
                "health tuning retains the bounded private cache rather than preloading the database");
            readOnlyHealthConnections++;
        };
        SyncAuthorityDatabase.HealthCheckCompletedForTest = path =>
        {
            if (Path.GetFullPath(path) == Path.GetFullPath(db.DatabasePath)) startupChecks++;
        };
        try
        {
            var startupProgress = new List<string>();
            Require(await db.LoadInstallationIdAsync(startupProgress: startupProgress.Add) == "connection-reuse" &&
                    startupChecks == 0 && readOnlyHealthConnections == 0 &&
                    !startupProgress.Any(value => value.Contains("PRAGMA quick_check", StringComparison.Ordinal) ||
                                                  value.Contains("PRAGMA foreign_key_check", StringComparison.Ordinal)),
                "ordinary cold startup validates identity without a full native health scan");
            await db.VerifyHealthAsync(startupProgress: startupProgress.Add);
            var foreignStarted = startupProgress.FindIndex(value => value.Contains("running SQLite PRAGMA foreign_key_check", StringComparison.Ordinal));
            var foreignCompleted = startupProgress.FindIndex(value => value.Contains("SQLite foreign_key_check completed in", StringComparison.Ordinal));
            var quickStarted = startupProgress.FindIndex(value => value.Contains("running SQLite PRAGMA quick_check", StringComparison.Ordinal));
            var quickCompleted = startupProgress.FindIndex(value => value.Contains("SQLite quick_check completed in", StringComparison.Ordinal));
            Require(quickStarted >= 0 && quickCompleted > quickStarted && foreignStarted > quickCompleted &&
                    foreignCompleted > foreignStarted,
                "cold admission checks structure first, then every relationship, with measured completion for both");
            var expectedTables = new List<string>();
            await using (var metadata = new SqliteConnection(new SqliteConnectionStringBuilder
                         { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                await metadata.OpenAsync();
                await using var command = metadata.CreateCommand();
                command.CommandText = "SELECT name FROM main.sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) expectedTables.Add(reader.GetString(0));
            }
            Require(expectedTables.Count > 0 && expectedTables.All(table =>
                    startupProgress.Count(value => value.StartsWith($"Startup: checking saved relationships in {table} (", StringComparison.Ordinal)) == 1 &&
                    startupProgress.Count(value => value.StartsWith($"Startup: checked saved relationships in {table} (", StringComparison.Ordinal)) == 1),
                "actual startup names and times every retained table, including historical data, exactly once");
            using var tableCancellation = new CancellationTokenSource();
            var tableCancelled = false;
            var cancelledTable = expectedTables[expectedTables.Count / 2];
            var cancelledProgress = new List<string>();
            try
            {
                await db.VerifyHealthAsync(tableCancellation.Token, value =>
                {
                    cancelledProgress.Add(value);
                    if (value.StartsWith($"Startup: checking saved relationships in {cancelledTable} (", StringComparison.Ordinal))
                        tableCancellation.Cancel();
                });
            }
            catch (OperationCanceledException) { tableCancelled = true; }
            Require(tableCancelled && startupChecks == 1 &&
                    !cancelledProgress.Any(value => value.StartsWith($"Startup: checked saved relationships in {cancelledTable} (", StringComparison.Ordinal)) &&
                    !cancelledProgress.Any(value => value.Contains("SQLite foreign_key_check completed", StringComparison.Ordinal)),
                "mid-table cancellation releases the read snapshot and never reports unfinished checks as completed");
            using var cancelledHealth = new CancellationTokenSource();
            startupProgress.Clear();
            var healthCancelled = false;
            try
            {
                await db.VerifyHealthAsync(cancelledHealth.Token, value =>
                {
                    startupProgress.Add(value);
                    if (value.Contains("running SQLite PRAGMA foreign_key_check", StringComparison.Ordinal))
                        cancelledHealth.Cancel();
                });
            }
            catch (OperationCanceledException) { healthCancelled = true; }
            Require(healthCancelled && startupChecks == 1 &&
                    startupProgress.Any(value => value.Contains("SQLite quick_check completed in", StringComparison.Ordinal)) &&
                    !startupProgress.Any(value => value.Contains("SQLite foreign_key_check completed in", StringComparison.Ordinal)),
                "cancellation after the structural pass never reports relationship completion or a healthy database");
            using var cancelledStructure = new CancellationTokenSource();
            var structureProgress = new List<string>();
            var structureCancelled = false;
            try
            {
                await db.VerifyHealthAsync(cancelledStructure.Token, value =>
                {
                    structureProgress.Add(value);
                    if (value.Contains("running SQLite PRAGMA quick_check", StringComparison.Ordinal))
                        cancelledStructure.Cancel();
                });
            }
            catch (OperationCanceledException) { structureCancelled = true; }
            Require(structureCancelled && startupChecks == 1 &&
                    !structureProgress.Any(value => value.Contains("completed in", StringComparison.Ordinal)) &&
                    !structureProgress.Any(value => value.Contains("running SQLite PRAGMA foreign_key_check", StringComparison.Ordinal)),
                "cancellation during the first structural phase prevents relationship checks and a healthy verdict");
            await SchemaInventoryChecksAsync(db, async () =>
                await db.LoadInstallationIdAsync() == "connection-reuse");
            Require(startupChecks == 1, "restored cold startup preserves schema guards without repeating full diagnostics");
            Require(readOnlyHealthConnections >= startupChecks,
                "actual cold-admission connections exercise the read-only IO configuration");
        }
        finally
        {
            SyncAuthorityDatabase.HealthCheckCompletedForTest = null;
            SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest = null;
        }
        await using (var ordinary = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = db.DatabasePath, Pooling = false }.ToString()))
        {
            await ordinary.OpenAsync();
            await using var command = ordinary.CreateCommand();
            command.CommandText = "PRAGMA mmap_size;";
            Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0,
                "a closed health check does not persist its mapping into an ordinary write connection");
        }
        Console.WriteLine("PASS cold read-only admission: full health and missing/substituted/extra schema rejection");
        await db.RegisterPairAsync("reuse-pair", 1);
        await db.SetPairStateAsync("reuse-pair", "active");
        async Task<bool> IsActive() => await db.LoadPairStateAsync("reuse-pair") == "active";
        var groupedCommits = 0;
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = _ => groupedCommits++;
        try
        {
            await using (db.BeginConnectionReuseScope())
            {
                await db.SetPairStateAsync("reuse-pair", "paused");
                await db.SetPairStateAsync("reuse-pair", "active");
                Require(await IsActive() && groupedCommits == 0,
                    "connection reuse preserves individual non-batched durable writes");
                await SchemaInventoryChecksAsync(db, IsActive);
                await using (db.BeginExecutionConnectionScope())
                    await db.SetPairStateAsync("reuse-pair", "active");
                Require(groupedCommits == 1 && await IsActive(),
                    "nested execution writes drain and return to the outer connection scope");
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var cancelled = false;
                try { await db.SetPairStateAsync("reuse-pair", "paused", cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && await IsActive(), "cancelled outer-scope write leaves durable state unchanged");
                var replacementRefused = false;
                try { File.Move(db.DatabasePath, db.DatabasePath + ".displaced"); }
                catch (IOException) { replacementRefused = true; }
                finally
                {
                    if (File.Exists(db.DatabasePath + ".displaced"))
                        File.Move(db.DatabasePath + ".displaced", db.DatabasePath);
                }
                Require(replacementRefused, "outer scope still prevents database replacement after nested scope disposal");
            }
        }
        finally { SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null; }
        File.Move(db.DatabasePath, db.DatabasePath + ".released");
        File.Move(db.DatabasePath + ".released", db.DatabasePath);
        Require(await IsActive(), "outer disposal releases pooled file handles and retains exact durable state");
        Console.WriteLine("PASS command connection reuse: unchanged schema guards, durable writes, nesting, cancellation and release");
        var cliRoot = Path.Combine(root, "cold-cli");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(cliRoot, "instance.lock"));
        var state = instance.StateAuthority;
        var coldDatabase = new SyncAuthorityDatabase(Path.Combine(cliRoot, "authority.db"));
        await coldDatabase.InitializeAsync(Guid.NewGuid().ToString("N"), "startup-check", "startup-check", "{}");
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(state, http, new SyncPairStore(state),
            new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
            new FirstSyncEffectChecks.FixtureCredentialStore(), coldDatabase);
        var operations = new FirstSyncEffectChecks.FixtureOperations(host, state);
        using var output = new StringWriter();
        using var cliProgress = new StringWriter();
        var exit = await SyncCli.ExecuteAsync(["--cli", "scan", "--pair", "absent-fixture", "--yes", "--json",
            "--timeout-seconds", "5"], output, cliProgress, () => operations);
        var emitted = cliProgress.ToString();
        Require(exit == 4 && !emitted.Contains("running SQLite PRAGMA foreign_key_check", StringComparison.Ordinal) &&
                !emitted.Contains("SQLite quick_check completed in", StringComparison.Ordinal) &&
                emitted.Contains("legacy-state reconciliation completed in", StringComparison.Ordinal) &&
                emitted.Contains("Startup: loaded 0 folder-pair definitions.", StringComparison.Ordinal),
            "the real CLI scan reports cold startup through its timestamped sink before rejecting an absent pair");
        Require(!emitted.Contains("Preparing to read both endpoint inventories", StringComparison.Ordinal),
            "startup rejection never claims a folder scan or begins provider work");
        Console.WriteLine("PASS actual CLI cold-start phase output and missing-pair no-provider rejection");

        // A late table must not escape the split check merely because earlier
        // tables were healthy. The native whole-database check is the oracle.
        var corruptDatabase = new SyncAuthorityDatabase(Path.Combine(root, "corrupt-health", "authority.db"));
        await corruptDatabase.InitializeAsync("corrupt-health", "corrupt-health", "corrupt-health", "{}");
        await using (var corrupt = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = corruptDatabase.DatabasePath, ForeignKeys = false, Pooling = false }.ToString()))
        {
            await corrupt.OpenAsync();
            await using var command = corrupt.CreateCommand();
            command.CommandText = """
                INSERT INTO volume(volume_id,installation_id,volume_serial_hex,filesystem,first_seen_utc,last_seen_utc,
                    supports_file_id_128,supports_usn,supports_atomic_rename,status)
                VALUES('orphan-volume','missing-installation','00','NTFS','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',1,1,1,'online');
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA foreign_key_check;";
            Require(string.Equals(await command.ExecuteScalarAsync() as string, "volume", StringComparison.Ordinal),
                "native whole-database oracle detects the planted late-table foreign-key violation");
        }
        var corruptProgress = new List<string>();
        var rejectedCorruption = false;
        try { await corruptDatabase.VerifyHealthAsync(startupProgress: corruptProgress.Add); }
        catch (InvalidDataException error) when (error.Message.Contains("table volume", StringComparison.Ordinal))
        { rejectedCorruption = true; }
        Require(rejectedCorruption &&
                corruptProgress.Any(value => value.StartsWith("Startup: checking saved relationships in volume (", StringComparison.Ordinal)) &&
                !corruptProgress.Any(value => value.Contains("SQLite foreign_key_check completed", StringComparison.Ordinal)),
            "real full admission rejects the same late-table corruption with its actual failing table and no false success");
    }

    // Unlike the direct-runner timing below, this includes the production CLI,
    // first-sync preparation, retained run history and every detailed log event.
    internal static async Task RunCliAsync(string root, int filesPerSide = 16, int maximumInFlight = 16,
        bool adaptive = false, string resourceMode = "Auto", TimeSpan batchCommitLatency = default)
    {
        ValidateBenchmarkInput(filesPerSide, maximumInFlight);
        using var concurrency = adaptive ? null : SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(maximumInFlight);
        if (Directory.Exists(root)) throw new IOException("Use a fresh concurrent CLI fixture root.");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var remote = new ConcurrentRemote { BatchCommitLatency = batchCommitLatency };
        var payload = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        var payloadBytes = checked(filesPerSide * 2L * payload.Length);
        const double minimumPayloadBytesPerSecond = 20_000_000d / 8;
        const double startupAllowanceSeconds = 30;
        var commandBudget = TimeSpan.FromSeconds(startupAllowanceSeconds);
        var choices = new Dictionary<string, string?>();
        for (var i = 0; i < filesPerSide; i++)
        {
            var upload = $"upload-{i:D3}.bin";
            var download = $"download-{i:D3}.bin";
            await File.WriteAllBytesAsync(Path.Combine(local, upload), payload);
            remote.Put(download, payload);
            choices.Add(upload, "upload");
            choices.Add(download, "download");
        }
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var connections = new DropboxConnectionStore(authority);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:concurrent-cli", "concurrent-cli-root", "concurrent-cli-home",
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
            lease.Identity.FileId, "/ConcurrentCli", "id:concurrent-cli"), "Concurrent CLI pair",
            binding.RootNamespaceId) with { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FirstSyncEffectChecks.FixtureOperations(host, authority);
        async Task<(JsonDocument Result, string Progress)> Cli(params string[] args)
        {
            using var output = new StringWriter();
            using var progress = new StringWriter();
            using var deadline = new CancellationTokenSource(commandBudget);
            var elapsed = Stopwatch.StartNew();
            var databaseTiming = new ConcurrentDictionary<string, (long Count, double TotalMilliseconds)>();
            var priorTiming = SyncAuthorityDatabase.TimingForTest;
            var healthChecks = 0;
            var priorHealth = SyncAuthorityDatabase.HealthCheckCompletedForTest;
            SyncAuthorityDatabase.HealthCheckCompletedForTest = path =>
            {
                if (Path.GetFullPath(path).Equals(Path.GetFullPath(database.DatabasePath),
                        StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref healthChecks);
                priorHealth?.Invoke(path);
            };
            SyncAuthorityDatabase.TimingForTest = (phase, milliseconds) => databaseTiming.AddOrUpdate(phase,
                (1, milliseconds), (_, previous) => (previous.Count + 1, previous.TotalMilliseconds + milliseconds));
            int exit;
            try { exit = await SyncCli.ExecuteAsync(args, output, progress, () => operations, deadline.Token); }
            finally
            {
                SyncAuthorityDatabase.TimingForTest = priorTiming;
                SyncAuthorityDatabase.HealthCheckCompletedForTest = priorHealth;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                cliTiming = args[1], exit, elapsedSeconds = elapsed.Elapsed.TotalSeconds, healthChecks,
                // Nested/concurrent measurements overlap: these totals are
                // diagnostic call costs, not additive wall-clock phases.
                aggregateDatabaseMilliseconds = databaseTiming.OrderBy(item => item.Key).ToDictionary(
                    item => item.Key, item => new { count = item.Value.Count, milliseconds = item.Value.TotalMilliseconds })
            }));
            if (exit != 0)
            {
                // Preserve failure output before the assertion, including cancellation.
                // The normal product cancellation path drains safely; never kill a mutation.
                await File.WriteAllTextAsync(Path.Combine(root, "cli-failure.stdout.json"), output.ToString());
                await File.WriteAllTextAsync(Path.Combine(root, "cli-failure.stderr.log"), progress.ToString());
                await File.WriteAllTextAsync(Path.Combine(root, "cli-failure-measurement.json"),
                    JsonSerializer.Serialize(new { exit, resourceMode, payloadBytes,
                        budgetSeconds = commandBudget.TotalSeconds, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                        deadlineExceeded = deadline.IsCancellationRequested, peak = remote.MaximumActive,
                        transferCalls = remote.TransferCalls, syntheticProvider = true }));
            }
            Require(exit == 0, "real CLI command succeeded: " + progress);
            if (args[1] == "scan")
                Require(healthChecks == 0,
                    "an already initialized inventory-only scan does not repeat full database health checks");
            if (args[1] == "first-sync" && args[2] == "apply")
                Require(healthChecks == 0 &&
                        !progress.ToString().Contains("running SQLite PRAGMA foreign_key_check", StringComparison.Ordinal) &&
                        !progress.ToString().Contains("SQLite quick_check completed in", StringComparison.Ordinal),
                    "file-changing first sync checks command authority without full retained-data scans");
            return (JsonDocument.Parse(output.ToString()), progress.ToString());
        }
        if (adaptive)
        {
            await CliChecks.ResourceSettingsChecksAsync(host, authority, maximumInFlight);
            // The settings lifecycle check ends in Auto. Select and verify the mode
            // used by this run explicitly, instead of mislabelling its measurement.
            var settings = JsonSerializer.Serialize(new { transfer = new {
                resourceMode, maximumConcurrentFiles = maximumInFlight,
                uploadKiBPerSecond = 0, downloadKiBPerSecond = 0 } });
            var selected = await Cli("--cli", "settings", "set", "--settings", settings, "--yes", "--json");
            selected.Result.Dispose();
            var readback = await Cli("--cli", "settings", "show", "--json");
            using (readback.Result)
                Require(readback.Result.RootElement.GetProperty("data").GetProperty("transfer")
                        .GetProperty("resourceMode").GetString() == resourceMode &&
                    host.TransferResourceStatus.Mode == resourceMode &&
                    host.TransferResourceStatus.MaximumFiles == maximumInFlight,
                    "actual CLI benchmark runs the explicitly selected mode and ceiling");
        }
        var scanWatch = Stopwatch.StartNew();
        var scan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json",
            "--timeout-seconds", commandBudget.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        scanWatch.Stop();
        using (scan.Result)
        {
            var data = scan.Result.RootElement.GetProperty("data");
            choices["__plan"] = data.GetProperty("localScanGenerationId").GetString();
            choices["__remote_plan"] = data.GetProperty("dropboxScanGenerationId").GetString();
        }
        var activityBatches = new ConcurrentBag<int>();
        database.ActivityAppendBoundaryForTest = (boundary, items) =>
        {
            if (boundary == "before") activityBatches.Add(items.Count);
            return Task.CompletedTask;
        };
        using var process = Process.GetCurrentProcess();
        var cpuStarted = process.TotalProcessorTime;
        commandBudget = TimeSpan.FromSeconds(startupAllowanceSeconds + payloadBytes / minimumPayloadBytesPerSecond) +
            batchCommitLatency * filesPerSide;
        var watch = Stopwatch.StartNew();
        var applied = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
            "--yes", "--json", "--choices", JsonSerializer.Serialize(choices),
            "--timeout-seconds", commandBudget.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        watch.Stop();
        Console.WriteLine($"MEASURE first-sync apply: {filesPerSide * 2} files in {watch.Elapsed.TotalSeconds:F1} s = " +
            $"{filesPerSide * 2 / watch.Elapsed.TotalSeconds:F2} files/s; batch commit {batchCommitLatency.TotalMilliseconds:F0} ms");
        await File.WriteAllTextAsync(Path.Combine(root, "cli-progress.log"), applied.Progress);
        process.Refresh();
        var applyCpuSeconds = (process.TotalProcessorTime - cpuStarted).TotalSeconds;
        var peakWorkingSetBytes = process.PeakWorkingSet64;
        using (applied.Result)
            Require(applied.Result.RootElement.GetProperty("data").GetProperty("firstSyncComplete").GetBoolean(),
                "real first-sync CLI completed all reviewed files");
        var expectedFiles = filesPerSide * 2;
        var progressLines = applied.Progress.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("Sync Shark: ", StringComparison.Ordinal)).ToArray();
        Require(progressLines.Length > 0 && progressLines.All(line =>
                line.StartsWith("Sync Shark: [", StringComparison.Ordinal) && line.IndexOf(']') > 13 &&
                DateTimeOffset.TryParse(line[13..line.IndexOf(']')], out var occurred) && occurred.Offset == TimeSpan.Zero),
            "every actual first-sync CLI progress line includes its parseable UTC producer time");
        Require(remote.TransferCalls == expectedFiles && remote.MaximumActive >= 2 && remote.MaximumActive <= maximumInFlight,
            "the full CLI/logging path overlaps provider transfers without duplicates");
        var run = (await database.LoadRunHistoryAsync(20)).Single(item =>
            item.ResultJson?.Contains("firstSyncComplete", StringComparison.OrdinalIgnoreCase) == true);
        var loggedPaths = new HashSet<string>(StringComparer.Ordinal);
        var logRows = 0;
        var logComplete = false;
        var resourceRows = 0;
        DateTimeOffset? transferStarted = null, transferFinished = null;
        for (var offset = 0; offset < Math.Max(10000, expectedFiles * 32); offset += 500)
        {
            var page = await database.LoadActivityProjectionPageAsync(500, offset, pairId: pair.Id,
                query: new SyncActivityQuery(RunId: run.Id, DetailedLog: true));
            logRows += page.Count;
            resourceRows += page.Count(item => item.Outcome.Contains("Transfer resources", StringComparison.Ordinal));
            foreach (var item in page.Where(item => item.OperationId is not null && item.RunId == run.Id))
            {
                loggedPaths.Add(item.RelativePath);
                if (transferStarted is null || item.OccurredUtc < transferStarted) transferStarted = item.OccurredUtc;
                if (transferFinished is null || item.OccurredUtc > transferFinished) transferFinished = item.OccurredUtc;
            }
            if (page.Count < 500) { logComplete = true; break; }
        }
        Require(logComplete, "the bounded benchmark reads the complete retained run log, including its final page");
        for (var i = 0; i < filesPerSide; i++)
        {
            var upload = $"upload-{i:D3}.bin";
            var download = $"download-{i:D3}.bin";
            Require(loggedPaths.Contains(upload) && loggedPaths.Contains(download) &&
                applied.Progress.Contains(upload) && applied.Progress.Contains(download),
                "both CLI output and retained run-linked detailed log identify every file");
            Require((await File.ReadAllBytesAsync(Path.Combine(local, download))).SequenceEqual(payload),
                "full CLI download matches exact source bytes");
            Require((await remote.ReadBytesAsync(upload)).SequenceEqual(payload),
                "full CLI upload matches exact destination bytes");
        }
        Require(!host.ActivityLogIncomplete, "all detailed events were durably retained");
        if (adaptive) Require(resourceRows > 0 && host.TransferResourceStatus.ActiveFiles == 0,
            "real adaptive scheduling emits run-linked resource decisions and drains its shared file allowance");
        if (adaptive) Require(host.TransferResourceStatus.Mode == resourceMode &&
            applied.Progress.Contains(resourceMode + ":", StringComparison.Ordinal) &&
            applied.Progress.Contains("files active", StringComparison.Ordinal) &&
            applied.Progress.Contains("completed files/s", StringComparison.Ordinal),
            "the actual CLI callback shows live adaptive decisions, not just the retained database log");
        var transferSeconds = (transferFinished!.Value - transferStarted!.Value).TotalSeconds;
        var measurement = new { files = expectedFiles, payloadBytes, maximumInFlight, adaptive,
            resourceMode = adaptive ? resourceMode : "fixed test override", resourceRows,
            budgetSeconds = commandBudget.TotalSeconds, startupAllowanceSeconds,
            minimumPayloadMbps = minimumPayloadBytesPerSecond * 8 / 1_000_000,
            transferMbps = payloadBytes * 8 / transferSeconds / 1_000_000,
            liveProviderPerformanceVerified = false,
            scanSeconds = scanWatch.Elapsed.TotalSeconds,
            seconds = watch.Elapsed.TotalSeconds, filesPerSecond = expectedFiles / watch.Elapsed.TotalSeconds,
            transferSeconds, transferFilesPerSecond = expectedFiles / transferSeconds,
            peak = remote.MaximumActive, logRows, runId = run.Id,
            activityCommitRequests = activityBatches.Count,
            largestActivityBatch = activityBatches.DefaultIfEmpty(0).Max(),
            activityEventsSubmitted = activityBatches.Sum(),
            applyCpuSeconds, peakWorkingSetBytes,
            provider = "synthetic input; real CLI, runtime host, detailed logs and file/database effects" };
        await File.WriteAllTextAsync(Path.Combine(root, "throughput.json"), JsonSerializer.Serialize(measurement));
        Console.WriteLine("PASS full CLI functionality within the size-based deadline " + JsonSerializer.Serialize(measurement));
        if (measurement.transferMbps < measurement.minimumPayloadMbps)
            Console.WriteLine("PERFORMANCE TARGET NOT MET: measured payload throughput is below 20 Mbps; this functional result is not speed acceptance.");
    }

    internal static async Task RunAsync(string root, int filesPerSide = 16, int maximumInFlight = 16)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The real file-identity fixture requires Windows.");
        ValidateBenchmarkInput(filesPerSide, maximumInFlight);
        using var concurrency = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(maximumInFlight);
        if (Directory.Exists(root)) throw new IOException("Use a fresh concurrent-transfer fixture root.");
        await SchedulerChecksAsync(maximumInFlight);
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var remote = new ConcurrentRemote();
        var payload = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        for (var i = 0; i < filesPerSide; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(local, $"upload-{i:D3}.bin"), payload);
            remote.Put($"download-{i:D3}.bin", payload);
        }
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
        const string installation = "concurrency-fixture", controlId = "concurrency-control", profile = "concurrency-profile";
        SyncLocalControlNamespaceBinding control;
        using (var lease = PilotDirectoryLease.Open(local, identity))
            control = SyncControlNamespace.BindLocalForTest(lease, installation, controlId);
        var pair = new SyncPair("concurrency-pair", "Tiny files", local, identity.VolumeSerialNumber,
            identity.FileId, "/Concurrency", "id:concurrency",
            ControlNamespace: new SyncControlNamespaceBinding(installation, controlId, control));
        using (var rootLease = PilotDirectoryLease.Open(local, identity))
        using (var first = SyncControlNamespace.OpenLocal(pair.ControlNamespace!, rootLease))
        using (var second = SyncControlNamespace.OpenLocal(pair.ControlNamespace!, rootLease))
        {
            Require(first.Identity == second.Identity, "concurrent namespace anchors retain the same exact identity");
            var refused = false;
            try { using var deletion = PilotDirectoryLease.OpenOwnedDirectory(first.Path, first.Identity); }
            catch (IOException) { refused = true; }
            Require(refused, "shared namespace anchors still exclude deletion authority");
        }
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync(installation, profile, "concurrency-fixture", "{}");
        await BatchWriteChecksAsync(db);
        await db.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await db.RegisterRootBindingAsync("concurrency-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { volumeSerial = identity.VolumeSerialNumber.ToString("x8"),
                fileId = identity.FileId.ToString("x16") }), local);
        await db.RegisterRootBindingAsync("concurrency-remote", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:concurrency\"}", "/Concurrency");
        var options = new SyncAuthorityPlannerOptions(1, profile, controlId, controlId,
            EnableDeletionEffects: true, EnableMoveEffects: true, DeletionMinimumInterval: TimeSpan.Zero);
        var actions = new List<SyncAuthorityLiveAction>();
        Task Capture(SyncAuthorityLiveAction action) { actions.Add(action); return Task.CompletedTask; }
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        SyncAuthorityPairRunner Runner() => new(pair, remote, db, "concurrency-local", "concurrency-remote",
            options, new PilotRunControl(), admission, onExecution: Capture);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "concurrency-local", "concurrency-remote");
        var observed = await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        await db.SetPairStateAsync(pair.Id, "active");
        var set = await db.LoadPlanningObservationSetAsync(pair.Id, 1, "concurrency-local", "concurrency-remote",
            observed.LocalScan.ScanGenerationId, observed.DropboxScan.ScanGenerationId);
        var runner = Runner();
        var plan = await runner.PlanBoundAsync(set, "concurrency-observation");
        var scoped = plan.Operations[0].Operation.PlanningAuthorities.First(item => item.ObjectId is not null);
        await using (db.BeginExecutionConnectionScope())
        {
            await SchemaInventoryChecksAsync(db, () => db.IsPlanningAuthorityCurrentAsync(scoped));
            var refused = false;
            try { File.Move(db.DatabasePath, db.DatabasePath + ".displaced"); }
            catch (IOException) { refused = true; }
            finally
            {
                if (File.Exists(db.DatabasePath + ".displaced"))
                    File.Move(db.DatabasePath + ".displaced", db.DatabasePath);
            }
            Require(refused, "execution scope excludes database replacement while connections are pooled");
        }
        File.Move(db.DatabasePath, db.DatabasePath + ".released");
        File.Move(db.DatabasePath + ".released", db.DatabasePath);
        Require(File.Exists(db.DatabasePath), "scoped pool releases every idle file handle before returning");
        Require(await db.IsPlanningAuthorityCurrentAsync(scoped), "exact object authority is current before transfer");
        Require(!await db.IsPlanningAuthorityCurrentAsync(scoped with { SynchronizationPointId = "foreign-point" }),
            "an incorrect per-object synchronization point is rejected");
        var participant = scoped with { ObjectId = null, SynchronizationPointId = null };
        Require(await db.IsPlanningAuthorityCurrentAsync(participant), "participant-wide authority is current before transfer");
        var databaseTiming = new ConcurrentDictionary<string, (long Count, double TotalMilliseconds)>();
        var writeGroups = new ConcurrentQueue<int>();
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = size => writeGroups.Enqueue(size);
        SyncAuthorityDatabase.TimingForTest = (phase, milliseconds) => databaseTiming.AddOrUpdate(phase,
            (1, milliseconds), (_, previous) => (previous.Count + 1, previous.TotalMilliseconds + milliseconds));
        var watch = Stopwatch.StartNew();
        SyncAuthorityPairExecutionResult result;
        try { result = await runner.ExecuteBoundAsync(plan, onAction: Capture); }
        finally
        {
            SyncAuthorityDatabase.TimingForTest = null;
            SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null;
        }
        watch.Stop();
        await File.WriteAllTextAsync(Path.Combine(root, "coordinated-result.json"), JsonSerializer.Serialize(result));
        var expectedFiles = filesPerSide * 2;
        Require(result.CompletedEffects == expectedFiles, $"real coordinated runner completed {result.CompletedEffects}/{expectedFiles} tiny effects; see coordinated-result.json");
        Require(writeGroups.Any(size => size > 1) && writeGroups.All(size => size <= 16),
            "real independent operations share bounded durable commits");
        Require(remote.MaximumActive >= 2 && remote.MaximumActive <= maximumInFlight,
            "real provider calls overlapped within the bounded worker limit");
        Require(actions.Any(action => action.Batch?.InFlight >= 2), "progress reports multiple active operations");
        Require(actions.Last().Batch is { InFlight: 0, Queued: 0 } final && final.Completed == expectedFiles &&
            final.CompletedFiles == expectedFiles, "terminal counts are exact");
        Require(actions.Last().Batch!.PayloadBytesProcessed == expectedFiles * 1024, "payload bytes exclude verification/hash readback");
        Require(!await db.IsPlanningAuthorityCurrentAsync(scoped), "a changed own synchronization point invalidates old authority");
        Require(!await db.IsPlanningAuthorityCurrentAsync(participant), "a changed synchronization set invalidates participant-wide authority");
        foreach (var action in actions.Where(action => action.Progress is not null))
            Require(plan.Operations.Single(item => item.Operation.OperationId == action.OperationId).RelativePath == action.RelativePath,
                "byte progress keeps the correct operation/path context");
        for (var i = 0; i < filesPerSide; i++)
            Require((await File.ReadAllBytesAsync(Path.Combine(local, $"download-{i:D3}.bin"))).SequenceEqual(payload), "exact downloaded bytes");
        var measurement = new { files = expectedFiles, payloadBytes = expectedFiles * 1024, maximumInFlight,
            seconds = watch.Elapsed.TotalSeconds, filesPerSecond = expectedFiles / watch.Elapsed.TotalSeconds,
            peak = remote.MaximumActive, provider = "synthetic input; real production runner and file/database effects",
            durableWriteGroups = writeGroups.Count, maximumWritesPerCommit = writeGroups.DefaultIfEmpty().Max(),
            databaseTiming = databaseTiming.OrderBy(item => item.Key).ToDictionary(item => item.Key,
                item => new { count = item.Value.Count, totalMilliseconds = item.Value.TotalMilliseconds }) };
        await File.WriteAllTextAsync(Path.Combine(root, "throughput.json"), JsonSerializer.Serialize(measurement));
        Console.WriteLine("MEASURE " + JsonSerializer.Serialize(measurement));
        var completedCalls = remote.TransferCalls;
        await Runner().RunAsync("Manual sync");
        Require(remote.TransferCalls == completedCalls, "unchanged real rescan does not replay completed transfers");
        var mixedPayload = new byte[8 * 1024 * 1024];
        Array.Fill(mixedPayload, (byte)0x6b);
        remote.HoldPath = "000-mixed-large.bin";
        remote.Put(remote.HoldPath, mixedPayload);
        var smallPaths = Enumerable.Range(0, 16).Select(i => $"mixed-small-{i:D3}.bin").ToArray();
        foreach (var path in smallPaths) remote.Put(path, payload);
        var mixedTask = Runner().RunAsync("Mixed-size sync");
        try
        {
            await remote.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await remote.SmallDownloadsReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var deadline = Stopwatch.StartNew();
            while (!smallPaths.All(path => File.Exists(Path.Combine(local, path))) && deadline.Elapsed.TotalSeconds < 30)
                await Task.Delay(10);
            Require(smallPaths.All(path => File.Exists(Path.Combine(local, path))) &&
                    !File.Exists(Path.Combine(local, remote.HoldPath)),
                "16 small files finish through the real executor while the larger transfer is still in flight");
        }
        finally
        {
            remote.ReleaseHeld.TrySetResult();
            await mixedTask; // Drain even when an assertion or bounded wait fails.
        }
        var mixed = await mixedTask;
        Require(mixed.Downloaded == 17 && (await File.ReadAllBytesAsync(Path.Combine(local, remote.HoldPath)))
                .SequenceEqual(mixedPayload), "mixed-size batch completes every exact payload");
        Console.WriteLine("PASS mixed-size fairness: 16 small files finish while the 8 MiB download is held");
        for (var i = 0; i < 16; i++) remote.Put($"reviewed-{i:D3}.bin", payload);
        remote.FailPath = "reviewed-005.bin";
        await db.RegisterOperationGroupAsync("concurrency-reviewed", "compound", "{}");
        var paths = Enumerable.Range(0, 16).Select(i => $"reviewed-{i:D3}.bin").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewed = await Runner().RunReviewedAsync("Reviewed first sync", paths, "concurrency-reviewed",
            _ => Task.CompletedTask, onAction: Capture, continueAfterBlockedPaths: true);
        Require(reviewed.Downloaded == 15 && reviewed.Blocked == 1, "reviewed runner isolates one failure and completes 15 peers");
        Require(!File.Exists(Path.Combine(local, remote.FailPath)) && paths.Where(path => path != remote.FailPath)
            .All(path => File.Exists(Path.Combine(local, path))), "failed download never installs partial bytes");
        await File.WriteAllTextAsync(Path.Combine(root, "proof.json"), JsonSerializer.Serialize(new
        { coordinated = measurement, reviewedDownloaded = reviewed.Downloaded, reviewedBlocked = reviewed.Blocked,
            noReplay = true, directoryDeletionRefused = true, exactBytes = true, authorityInvalidation = true,
            schedulerCancellationAndExceptionDrain = true, mixedSizeFairness = true }));
        Console.WriteLine("PASS concurrent transfer checks");
    }

    private static async Task SchemaInventoryChecksAsync(SyncAuthorityDatabase db, Func<Task<bool>> readCurrent)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        string name, definition;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='index' AND name NOT LIKE 'sqlite_%' AND sql IS NOT NULL ORDER BY name LIMIT 1;";
            await using var reader = await command.ExecuteReaderAsync();
            Require(await reader.ReadAsync(), "fixture has a named production index");
            name = reader.GetString(0); definition = reader.GetString(1);
        }
        async Task Sql(string sql)
        { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        async Task Rejected(string reason)
        {
            var rejected = false;
            try { await readCurrent(); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, reason);
        }
        var identifier = "\"" + name.Replace("\"", "\"\"") + "\"";
        try
        {
            await Sql("DROP INDEX " + identifier + ";");
            await Rejected("missing required schema object fails before authority use");
            await Sql("CREATE INDEX fixture_unexpected_index ON sync_pair(state);");
            await Rejected("same-cardinality schema substitution is not accepted");
            await Sql("DROP INDEX fixture_unexpected_index;");
            await Sql(definition);
            await Sql("CREATE INDEX fixture_unexpected_index ON sync_pair(state);");
            await Rejected("an additional schema object is not accepted");
        }
        finally
        {
            await Sql("DROP INDEX IF EXISTS fixture_unexpected_index;");
            await Sql("DROP INDEX IF EXISTS " + identifier + ";");
            await Sql(definition);
        }
        Require(await readCurrent(), "exact restored schema and authority are accepted");
        Console.WriteLine("PASS exact schema inventory: missing, substituted, extra and restored objects");
    }

    private static async Task BatchWriteChecksAsync(SyncAuthorityDatabase db)
    {
        const string pair = "write-batch-fixture";
        await db.RegisterPairAsync(pair, 1);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sizes = new ConcurrentQueue<int>();
        var firstBoundary = 0;
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = size =>
        {
            sizes.Enqueue(size);
            if (Interlocked.Increment(ref firstBoundary) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Fixture commit was not released.");
        };
        var pending = new List<Task>();
        await using (db.BeginExecutionConnectionScope())
        {
            try
            {
                var first = db.SetPairStateAsync(pair, "active");
                pending.Add(first);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Require(!first.IsCompleted && await ReadStateAsync() == "discovery_required",
                    "a write is not acknowledged or visible before its FULL durable commit");
                var rejected = db.SetPairStateAsync("write-batch-missing", "active");
                pending.Add(rejected);
                var good = Enumerable.Range(0, 12).Select(_ => db.SetPairStateAsync(pair, "active")).ToArray();
                pending.AddRange(good);
                var cancelled = db.SetPairStateAsync(pair, "paused", cancellation.Token);
                pending.Add(cancelled);
                cancellation.Cancel();
                Require(!cancelled.IsCompleted, "an admitted cancelled write drains before returning");
                release.Set();
                await first;
                await Task.WhenAll(good);
                var refused = false;
                try { await rejected; }
                catch (InvalidOperationException error) when (error.Message.Contains("not updated", StringComparison.Ordinal))
                { refused = true; }
                Require(refused, "one real authority rejection remains an exact independent failure");
                var cancelledSafely = false;
                try { await cancelled; }
                catch (OperationCanceledException) { cancelledSafely = true; }
                Require(cancelledSafely && await ReadStateAsync() == "active" && sizes.Any(size => size > 1),
                    "a rejected and cancelled request do not roll back valid peers or change the cancelled pair");
            }
            finally
            {
                release.Set();
                try { await Task.WhenAll(pending); } catch { /* All expected failures were observed above. */ }
                SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null;
            }
        }
        // The writer must include ready requests that arrive while the first
        // body is executing, not freeze a one-item batch before disk work.
        // Control arrival, not elapsed time: old code commits 1 + 16 + 15;
        // the bounded rolling drain commits 16 + 16, acknowledging only then.
        using var releaseBody = new ManualResetEventSlim();
        var bodyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyCount = 0;
        var rollingSizes = new ConcurrentQueue<int>();
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase != "write-body" || Interlocked.Increment(ref bodyCount) != 1) return;
            bodyEntered.TrySetResult();
            if (!releaseBody.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Fixture write body was not released.");
        };
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = rollingSizes.Enqueue;
        var rollingWrites = new List<Task>();
        await using (db.BeginExecutionConnectionScope(planningReservations: true))
        {
            try
            {
                rollingWrites.Add(db.SetPairStateAsync(pair, "active"));
                await bodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                rollingWrites.AddRange(Enumerable.Range(0, 31)
                    .Select(_ => db.SetPairStateAsync(pair, "active")));
                Require(rollingWrites.All(write => !write.IsCompleted),
                    "ready writes remain unacknowledged while the first transaction is open");
                releaseBody.Set();
                await Task.WhenAll(rollingWrites);
                Require(rollingSizes.SequenceEqual(new[] { 16, 16 }),
                    "arrivals during write bodies fill two bounded durable commits, not three partial commits");
            }
            finally
            {
                releaseBody.Set();
                try { await Task.WhenAll(rollingWrites); } catch { }
                SyncAuthorityDatabase.TimingForTest = null;
                SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null;
            }
        }
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = _ => throw new IOException("Injected pre-commit failure.");
        await using (db.BeginExecutionConnectionScope())
        {
            try
            {
                var writes = Enumerable.Range(0, 12).Select(_ => db.SetPairStateAsync(pair, "paused")).ToArray();
                foreach (var write in writes)
                {
                    var refused = false;
                    try { await write; } catch (IOException) { refused = true; }
                    Require(refused && !write.IsCompletedSuccessfully, "a failed group acknowledges no successful write");
                }
                Require(await ReadStateAsync() == "active", "a failed commit group leaves the prior durable state intact");
            }
            finally { SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null; }
        }
        Console.WriteLine("PASS durable write batching: commit visibility, bounded grouping, isolated rejection/cancellation and failed-group rollback");

        async Task<string?> ReadStateAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM sync_pair WHERE pair_id=$pair;";
            command.Parameters.AddWithValue("$pair", pair);
            return (string?)await command.ExecuteScalarAsync();
        }
    }

    private static async Task SchedulerChecksAsync(int maximumInFlight)
    {
        var a = Snapshot("a", "a.bin"); var b = Snapshot("b", "b.bin");
        Require(SyncAuthorityOperationScheduler.Independent(a, b), "siblings overlap");
        Require(!SyncAuthorityOperationScheduler.Independent(a, b with { Members = a.Members }), "same identity/path fenced");
        Require(!SyncAuthorityOperationScheduler.Independent(a, Snapshot("b", "a.bin/child")), "ancestor path fenced");
        Require(!SyncAuthorityOperationScheduler.Independent(a, b with { Dependencies = [new(a.OperationId, "parent")] }), "dependency fenced");
        Require(!SyncAuthorityOperationScheduler.Independent(a, b with { Status = SyncAuthorityOperationStatus.Committed }), "uncertain recovery barrier");
        Require(!SyncAuthorityOperationScheduler.Independent(a, b with { Kind = SyncAuthorityOperationKind.MoveLocal }), "move barrier");
        foreach (var invalid in new[] { 0, SyncAuthorityOperationScheduler.MaximumBenchmarkInFlight + 1 })
        {
            var rejected = false;
            try { using var invalidScope = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "out-of-range benchmark limits are rejected before work");
        }
        var active = 0; var peak = 0; var accepted = 0;
        var items = Enumerable.Range(0, 2048).Select(i => Snapshot("file-" + i, $"file-{i:D4}.bin")).ToArray();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var saturation = SyncAuthorityOperationScheduler.RunAsync(items.Take(maximumInFlight * 2).ToArray(), item => item,
            async item =>
            {
                if (Interlocked.Increment(ref admitted) == maximumInFlight) filled.TrySetResult();
                await release.Task;
                return item.OperationId;
            }, (_, _) => Task.FromResult(true), CancellationToken.None);
        try
        {
            await filled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Require(Volatile.Read(ref admitted) == maximumInFlight,
                "the requested limit is reached without admitting a surplus worker");
        }
        finally
        {
            release.TrySetResult();
            await saturation;
        }
        Console.WriteLine($"PASS scheduler actual simultaneous workers: {maximumInFlight}");
        await SyncAuthorityOperationScheduler.RunAsync(items, item => item, async item =>
        {
            var count = Interlocked.Increment(ref active);
            int old;
            do { old = Volatile.Read(ref peak); } while (old < count && Interlocked.CompareExchange(ref peak, count, old) != old);
            try { await Task.Delay(2); return item.OperationId; }
            finally { Interlocked.Decrement(ref active); }
        }, (_, _) => { accepted++; return Task.FromResult(true); }, CancellationToken.None);
        Require(accepted == items.Length && peak > 1 && peak <= maximumInFlight && active == 0,
            "2048-item pump bounded and drained");
        accepted = 0;
        var stopped = await SyncAuthorityOperationScheduler.RunAsync(items, item => item,
            async item => { await Task.Delay(2); return item.OperationId; },
            (_, _) => { accepted++; return Task.FromResult(false); }, CancellationToken.None);
        Require(!stopped && accepted > 0 && accepted <= maximumInFlight, "stop drains admitted effects only");
        foreach (var mode in new[] { "admission", "worker", "cancellation" })
        {
            active = 0; accepted = 0;
            var finished = 0;
            using var cancellation = new CancellationTokenSource();
            var sentinel = new IOException("sentinel-" + mode);
            Exception? observed = null;
            try
            {
                await SyncAuthorityOperationScheduler.RunAsync(items, item =>
                {
                    if (mode == "admission" && item.OperationId == "file-5") throw sentinel;
                    return item;
                }, async item =>
                {
                    Interlocked.Increment(ref active);
                    try
                    {
                        if (mode == "cancellation") cancellation.Cancel();
                        await Task.Delay(30); // Simulates an already-issued effect that must finish safely.
                        if (mode == "worker" && item.OperationId == "file-0") throw sentinel;
                        return item.OperationId;
                    }
                    finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref finished); }
                }, (_, _) => { accepted++; return Task.FromResult(true); }, cancellation.Token);
            }
            catch (Exception error) { observed = error; }
            Require(mode == "cancellation" ? observed is OperationCanceledException : ReferenceEquals(observed, sentinel),
                mode + " retains the precise failure/cancellation");
            Require(active == 0 && finished > 0 && accepted == finished - (mode == "worker" ? 1 : 0),
                mode + " observes and drains every admitted effect before returning");
        }
        Console.WriteLine("PASS scheduler overlap, footprint fences, stop, cancellation, exceptions and 2048-item queue");
    }

    private static SyncAuthorityOperationSnapshot Snapshot(string id, string path) => new(id,
        SyncAuthorityOperationKind.CreateLocal, SyncAuthorityOperationStatus.Ready, "pair", "object-" + id,
        1, "profile", "idempotency-" + id, "{}", null, null,
        [new("source-" + id, "pair", "object-" + id, "source", "remote", null, null, null, path, "{\"objectKind\":\"file\"}"),
         new("target-" + id, "pair", "object-" + id, "destination", "local", null, null, null, path, "{\"present\":false}")],
        [], [], [], [], []);

    private static void ValidateBenchmarkInput(int filesPerSide, int maximumInFlight)
    {
        if (filesPerSide < 16 || filesPerSide > SyncAuthorityOperationScheduler.MaximumBenchmarkInFlight / 2)
            throw new ArgumentOutOfRangeException(nameof(filesPerSide));
        if (maximumInFlight < 16 || maximumInFlight > SyncAuthorityOperationScheduler.MaximumBenchmarkInFlight)
            throw new ArgumentOutOfRangeException(nameof(maximumInFlight));
    }

    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }

    // Only external input is simulated. Scheduling, planning, receipts, file
    // identity and failure reconciliation use the actual product above.
    private sealed class ConcurrentRemote : ISyncRemoteStore
    {
        private readonly FakeSyncRemoteStore _inner = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly TaskCompletionSource _overlap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active, _peak, _calls;
        public string? FailPath { get; set; }
        public string? HoldPath { get; set; }
        internal TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SmallDownloadsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _smallDownloads;
        public int MaximumActive => Volatile.Read(ref _peak);
        public int TransferCalls => Volatile.Read(ref _calls);
        public void Put(string path, byte[] bytes) => _inner.PutBytes(path, bytes);
        internal async Task<byte[]> ReadBytesAsync(string path)
        {
            var item = (await _inner.ListEntriesAsync()).Single(entry => entry.RelativePath == path);
            using var bytes = new MemoryStream();
            await _inner.DownloadAsync(item, bytes);
            return bytes.ToArray();
        }
        private async Task<T> Locked<T>(Func<Task<T>> call)
        { await _gate.WaitAsync(); try { return await call(); } finally { _gate.Release(); } }
        private async Task Locked(Func<Task> call)
        { await _gate.WaitAsync(); try { await call(); } finally { _gate.Release(); } }
        private async Task Enter(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int old;
            do { old = Volatile.Read(ref _peak); } while (old < active && Interlocked.CompareExchange(ref _peak, active, old) != old);
            if (active >= 2) _overlap.TrySetResult();
            await _overlap.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Task.Delay(40, token);
        }
        public Task VerifyRootIdentityAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken token = default) => Locked(() => _inner.ListEntriesAsync(token));
        public Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installation, string ns, CancellationToken token = default) => _inner.VerifyControlNamespaceAsync(binding, installation, ns, token);
        public Task<PilotUploadResult> UploadCreateOnlyAsync(string path, Stream source, string hash, CancellationToken token = default) => UploadAsync(path, source, hash, null, token);
        public async Task<PilotUploadResult> UploadAsync(string path, Stream source, string hash, string? revision, CancellationToken token = default)
        {
            PilotUploadResult result;
            try { await Enter(token); result = await Locked(() => _inner.UploadAsync(path, source, hash, revision, token)); }
            finally { Interlocked.Decrement(ref _active); }
            if (BatchCommitLatency > TimeSpan.Zero) await JoinBatchCommitAsync();
            return result;
        }

        // Models DropboxPilotClient's small-file batch: every file waiting at that moment shares one
        // commit, sent 200 ms after the last arrival.
        internal TimeSpan BatchCommitLatency { get; init; }
        private readonly object _batchGate = new();
        private Task? _batch;
        private long _batchArrivals;
        private Task JoinBatchCommitAsync()
        {
            lock (_batchGate)
            {
                _batchArrivals++;
                return _batch ??= CommitWhenQuietAsync();
            }
        }
        private async Task CommitWhenQuietAsync()
        {
            while (true)
            {
                long seen;
                lock (_batchGate) seen = _batchArrivals;
                await Task.Delay(200);
                lock (_batchGate)
                {
                    if (_batchArrivals != seen) continue;
                    _batch = null;
                }
                await Task.Delay(BatchCommitLatency);
                return;
            }
        }
        public async Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination, CancellationToken token = default)
        {
            try
            {
                await Enter(token);
                if (expected.RelativePath == FailPath) throw new IOException("Injected isolated tiny-file failure: " + FailPath);
                if (expected.RelativePath == HoldPath)
                {
                    HeldStarted.TrySetResult();
                    await ReleaseHeld.Task.WaitAsync(token);
                }
                var result = await Locked(() => _inner.DownloadAsync(expected, destination, token));
                if (expected.RelativePath.StartsWith("mixed-small-", StringComparison.Ordinal) &&
                    Interlocked.Increment(ref _smallDownloads) == 16) SmallDownloadsReady.TrySetResult();
                return result;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string path, CancellationToken token = default) => Locked(() => _inner.CopyToRecoveryAsync(expected, path, token));
        public Task<SyncArchiveResult?> GetRecoveryAsync(string path, CancellationToken token = default) => Locked(() => _inner.GetRecoveryAsync(path, token));
        public Task DeleteAsync(PilotRemoteEntry expected, CancellationToken token = default) => Locked(() => _inner.DeleteAsync(expected, token));
    }
}
