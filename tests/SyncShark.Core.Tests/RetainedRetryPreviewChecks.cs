using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Measure the complete retry-preview composition, not just its membership query.
// The supplied closed checkpoint stays immutable. Runtime initialization is confined
// to one disposable copy; provider and credential access always fail closed.
internal static class RetainedRetryPreviewChecks
{
    // Isolate the observed inventory-loading stall without another live apply,
    // provider request, health bypass for effects, database clone or migration.
    // Invoke the exact production read methods using a native read-only handle.
    internal static async Task ReadPlanningProfileAsync()
    {
        var stage = Path.Combine(DiagnosticPaths.RequireRoot(), "build", "root682-cloned-state-live",
            "real-provider-state-capacity");
        const string pair = "f00d0000000000000028";
        var path = Path.Combine(stage, "vNext", "state", "syncshark.db");
        using var originalOwner = PilotInstanceLock.AcquireForMaintenance(SyncSharkRuntimePaths.InstanceLockFile);
        using var stageOwner = PilotInstanceLock.Acquire(Path.Combine(stage, "state", SyncSharkRuntimePaths.InstanceLockFileName));
        for (var parent = new FileInfo(path) as FileSystemInfo; parent is not null;
             parent = parent is FileInfo file ? file.Directory : ((DirectoryInfo)parent).Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The retained read profile must not follow reparse targets.");
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        var db = new SyncAuthorityDatabase(path);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Cache = SqliteCacheMode.Private }.ToString());
        await connection.OpenAsync();
        using (var guard = connection.CreateCommand())
        {
            guard.CommandText = "PRAGMA query_only=ON;";
            guard.ExecuteNonQuery();
        }
        Require(SQLitePCL.raw.sqlite3_db_readonly(connection.Handle!, "main") == 1,
            "Native handle is read-only; no health or effect-admission claim is made");
        async Task<string> Latest(string side)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side=$side AND status='complete' ORDER BY generation DESC LIMIT 1;";
            command.Parameters.AddWithValue("$pair", pair);
            command.Parameters.AddWithValue("$side", side);
            return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidDataException("Missing retained complete scan."));
        }
        var local = await Latest("local");
        var remote = await Latest("dropbox");
        async Task Read(string label, string methodName, params object?[] prefix)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 1000,
                static state => ((CancellationToken)state!).IsCancellationRequested ? 1 : 0, deadline.Token);
            var watch = Stopwatch.StartNew();
            Console.WriteLine("BEGIN exact production " + label + "; native read-only45s deadline");
            try
            {
                var method = typeof(SyncAuthorityDatabase).GetMethods(System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                    .Single(item => item.Name == methodName && item.GetParameters().Length == prefix.Length + 1);
                var pending = (Task)method.Invoke(method.IsStatic ? null : db, [..prefix, deadline.Token])!;
                await pending;
                var result = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
                Console.WriteLine(JsonSerializer.Serialize(new { phase = label, complete = true,
                    seconds = watch.Elapsed.TotalSeconds, count = result.GetType().GetProperty("Count")!.GetValue(result) }));
            }
            catch (Exception error) when (deadline.IsCancellationRequested)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { phase = label, complete = false,
                    seconds = watch.Elapsed.TotalSeconds, reason = "native read deadline", errorType = error.GetType().Name }));
            }
            finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 0, null, null); }
        }
        await Read("local observation rows", "LoadCompleteObservationAsync", connection, null, pair, "local", local);
        await Read("remote observation rows", "LoadCompleteObservationAsync", connection, null, pair, "dropbox", remote);
        await Read("local uncertainties", "LoadCompleteUncertainPathsAsync", connection, null, pair, "local", local);
        await Read("remote uncertainties", "LoadCompleteUncertainPathsAsync", connection, null, pair, "dropbox", remote);
        await Read("excluded paths", "LoadCompleteExcludedPathsAsync", connection, null, local, remote);
        await Read("synchronization points", "LoadLatestSynchronizationPointsAsync", connection, null, pair);
        var after = new FileInfo(path);
        Require(after.Length == length && after.LastWriteTimeUtc == modified,
            "Retained database metadata unchanged; read-only handle, no provider or user-file effects");
    }

    internal static async Task RunPlanningProfileAsync(string source, string root, string runId, string pairId)
    {
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build");
        root = Path.GetFullPath(root);
        if (!root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new IOException("Use one fresh isolated Sync Shark build fixture.");
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The planning fixture must not traverse a reparse directory.");
            if (string.Equals(parent.FullName, allowed, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length != 0)
            throw new IOException("Supply a closed retained checkpoint without an active WAL or reparse target.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var sourceHash = await HashAsync(source);
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "authority.db");
        File.Copy(source, databasePath, false);
        Require(await HashAsync(databasePath) == sourceHash, "The sole disposable planning database is byte-exact before initialization");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var db = new SyncAuthorityDatabase(databasePath);
        using var http = new HttpClient(new NoNetwork());
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority), new NoCredentials(), db);
        var watch = Stopwatch.StartNew();
        var pair = (await host.LoadPairsAsync(token)).Pairs.Single(item => item.Id == pairId);
        Measure("Cold native initialization", watch);
        watch.Restart();
        await db.EnsureReviewedRunCanStartAsync(pairId, token, message => Console.WriteLine("ADMISSION " + message));
        Measure("Current real admission", watch);
        watch.Restart();
        var preview = await host.LoadRunRetryAsync(runId, token, pairId, message => Console.WriteLine("PREVIEW " + message));
        Measure("Complete production retry eligibility", watch);
        Require(preview.UnavailableReason is null && preview.PairId == pairId, "The actual runtime retains full current retry eligibility");
        Require(preview.Items.All(item => item.State != "Verify first"), "No uncertain effect is admitted for profiling");
        var retainedRun = await db.LoadRunByIdAsync(runId, token) ?? throw new InvalidDataException("Missing run");
        using var request = JsonDocument.Parse(retainedRun.RequestJson);
        var original = request.RootElement.GetProperty("reviewedPlan").Deserialize<SyncBootstrapActionPlan>()
            ?? throw new InvalidDataException("This fixture requires the exact retained first-sync approval");
        var selected = preview.Items.Where(item => item.CanRetry).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedKinds = original.Choices.Where(item => selected.Contains(item.RelativePath))
            .ToDictionary(item => item.RelativePath, item => SyncBootstrapActions.TransferKind(item.ExpectedGroup), StringComparer.OrdinalIgnoreCase);
        Require(selected.Count > 0 && expectedKinds.Count == selected.Count &&
                expectedKinds.Values.All(kind => kind == SyncAuthorityOperationKind.CreateLocal),
            "The complete unfinished Child selection uses only its original cloud-to-local approval; no replacement or upload is inferred");
        async Task<long> Count(string table)
        {
            await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await read.OpenAsync(token);
            await using var command = read.CreateCommand();
            command.CommandText = "SELECT count(*) FROM " + table + ";";
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }
        async Task<string> Scalar(string sql, string parameter)
        {
            await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await read.OpenAsync(token);
            await using var command = read.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", parameter);
            return Convert.ToString(await command.ExecuteScalarAsync(token)) ?? "";
        }
        var attempts = await Count("operation_attempt");
        var receipts = await Count("effect_receipt");
        var localControl = await Scalar("SELECT control_namespace_id FROM pair_control_namespace WHERE pair_id=$value AND side='local' AND status='active' ORDER BY binding_generation DESC LIMIT 1;", pairId);
        var remoteControl = await Scalar("SELECT recovery_root_binding_id FROM recovery_root_binding WHERE json_extract(identity_json,'$.folderId')=$value AND side='dropbox' AND status='active';", pair.RecoveryRoot?.RootFolderId ?? "");
        Require(localControl.Length > 0 && remoteControl.Length > 0, "Existing retained control/recovery authorities are used without opening either live endpoint");
        var group = "bootstrap-" + Guid.NewGuid().ToString("N");
        await db.RegisterOperationGroupAsync(group, "compound", JsonSerializer.Serialize(new { purpose = "reviewed_first_sync", pairId, reviewedPathCount = selected.Count }), token);
        await db.SuspendOperationGroupForPlanningAsync(group, token);
        var timings = new System.Collections.Concurrent.ConcurrentDictionary<string, (long Count, double Milliseconds)>();
        var batches = new List<int>();
        SyncAuthorityDatabase.TimingForTest = (phase, milliseconds) => timings.AddOrUpdate(phase, (1, milliseconds),
            (_, prior) => (prior.Count + 1, prior.Milliseconds + milliseconds));
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = count => { lock (batches) batches.Add(count); };
        watch.Restart();
        IReadOnlyList<SyncAuthorityPlannerOutcome> outcomes;
        double planningMilliseconds;
        try
        {
            var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pairId, null, null,
                "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            outcomes = await new SyncAuthorityReconciliationPlanner(db,
                new(pair.PolicyRevision, host.CapabilityProfileIdForTest, localControl, remoteControl,
                    EnableReplacementEffects: true, EnableDeletionEffects: pair.PropagateDeletions,
                    EnableMoveEffects: true, Direction: SyncEndpointEngineLayout.EngineDirection(pair)),
                effectPathFilter: selected, operationGroupId: group, expectedOperationKinds: expectedKinds,
                onComparison: progress => { Console.WriteLine($"STEP {watch.Elapsed.TotalMilliseconds:F1} ms: {progress.Phase}; {progress.RelativePath}"); return Task.CompletedTask; },
                endpointContext: SyncEndpointContext.Capture(pair), planningConcurrency: 16, admission: admission).PlanAsync(pairId, token);
        }
        finally
        {
            planningMilliseconds = watch.Elapsed.TotalMilliseconds;
            SyncAuthorityDatabase.TimingForTest = null;
            SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null;
            Console.WriteLine(JsonSerializer.Serialize(new { planningMilliseconds,
                note = "Database timings can be nested and must not be summed into wall time; the comparison callback writes to stdout only, not the production progress store",
                timings = timings.OrderBy(item => item.Key).ToDictionary(item => item.Key,
                    item => new { item.Value.Count, item.Value.Milliseconds }), batches }));
        }
        Require(outcomes.Count > 0, "The actual planner produced retained decisions");
        foreach (var blocked in outcomes.Where(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Blocked))
            Console.WriteLine(JsonSerializer.Serialize(new { result = "blocked", blocked.RelativePath, blocked.Reason }));
        Require(await Count("operation_attempt") == attempts && await Count("effect_receipt") == receipts,
            "No attempt, effect receipt, provider request or user-file operation was issued");
        Require(await HashAsync(source) == sourceHash, "The source database remains byte-exact");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "profiled", sourceSha256 = sourceHash,
            selected = selected.Count, planningMilliseconds, planningAndAssertionsMilliseconds = watch.Elapsed.TotalMilliseconds,
            planned = outcomes.Count(item => item.OperationId is not null), blocked = outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Blocked),
            sourceMutation = false, providerEffects = false }));
    }

    internal static async Task RunAsync(string source, string root, string runId, string pairId)
    {
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new IOException("Use one fresh isolated Sync Shark build fixture.");
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length != 0)
            throw new IOException("Supply a closed retained checkpoint without an active WAL or reparse target.");
        var sourceHash = await HashAsync(source);
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "authority.db");
        File.Copy(source, databasePath, false);
        Require(await HashAsync(databasePath) == sourceHash, "The disposable database is byte-exact before initialization");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var db = new SyncAuthorityDatabase(databasePath);
        using var http = new HttpClient(new NoNetwork());
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority), new NoCredentials(), db);
        var watch = Stopwatch.StartNew();
        Require(await db.LoadInstallationIdAsync() is { Length: > 0 }, "The retained installation passes native startup health");
        Measure("Cold native health", watch);
        watch.Restart();
        Require((await host.LoadPairsAsync()).Pairs.Any(pair => pair.Id == pairId), "The runtime retains the exact requested pair");
        Measure("Runtime initialization after native health", watch);
        watch.Restart();
        var ids = await db.LoadRunOperationIdsAsync(runId, pairId);
        Measure("Exact run operation membership", watch);
        Require(ids.Count > 100, "The retained run exercises more than one hundred operations");
        watch.Restart();
        foreach (var id in ids.Take(10))
            Require((await db.LoadOperationAsync(id)).OperationId == id, "Sampled operation snapshot retains its exact identity");
        Measure("Ten complete operation snapshots including members, allocations and attempts", watch);
        var output = new StringWriter();
        var errors = new StringWriter();
        watch.Restart();
        var exit = await SyncCli.ExecuteAsync(
            ["--cli", "run", "retry-plan", "--run", runId, "--pair", pairId, "--limit", "5", "--json"],
            output, errors, () => new PreviewOperations(host, authority));
        var elapsed = watch.Elapsed;
        Measure("Full production CLI retry-plan after initialization", watch);
        Require(exit == 0, "The actual CLI parser, dispatcher and runtime preview succeed: " + errors.ToString().Trim());
        Require(errors.ToString().Contains("Reading the retained approval for run " + runId) &&
                errors.ToString().Contains("Checking retained paths, operation states, leases and effect receipts") &&
                errors.ToString().Contains("Retry preview ready:"),
            "The actual CLI reports each real read phase before its final JSON response");
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Require(data.GetProperty("runId").GetString() == runId && data.GetProperty("pairId").GetString() == pairId,
            "The CLI response preserves exact run and pair identity");
        Require(data.GetProperty("totalItems").GetInt32() > 100 && data.GetProperty("items").GetArrayLength() == 5 &&
                data.GetProperty("hasMore").GetBoolean(), "Paging keeps the complete retry totals and five-item response");
        watch.Restart();
        string? admissionRefusal = null;
        try
        {
            await db.EnsureReviewedRunCanStartAsync(pairId, CancellationToken.None,
                message => Console.WriteLine($"ADMISSION {watch.Elapsed.TotalMilliseconds:F1} ms: {message}"));
        }
        catch (InvalidOperationException error) { admissionRefusal = error.Message; }
        var admissionElapsed = watch.Elapsed;
        Measure("Real retained reviewed-run admission across all unfinished operations", watch);
        Require(data.GetProperty("needsVerification").GetInt32() == 0
                ? admissionRefusal is null
                : admissionRefusal?.Contains("Verify the retained outcome before retrying:") == true,
            "Admission preserves the retained uncertain-effect rejection instead of accepting it for speed");
        Require(await HashAsync(source) == sourceHash, "The source checkpoint is unchanged");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "measured", sourceSha256 = sourceHash,
            operationCount = ids.Count, previewMilliseconds = elapsed.TotalMilliseconds,
            admissionMilliseconds = admissionElapsed.TotalMilliseconds, data = data.Clone() }));
        Require(elapsed < TimeSpan.FromSeconds(20), "The complete warm retry action meets the twenty-second retained-profile bound");
        Require(admissionElapsed < TimeSpan.FromSeconds(20), "The complete warm retry admission meets the twenty-second retained-profile bound");
    }

    private static void Measure(string phase, Stopwatch watch) =>
        Console.WriteLine($"MEASURE {phase}: {watch.Elapsed.TotalSeconds:F3} seconds");

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class PreviewOperations(SyncAuthorityRuntimeHost host, PilotStateAuthority authority) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => authority;
        public override string Mode => "retained-retry-preview-fixture";
    }

    private sealed class NoCredentials : IDropboxRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => this;
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A retry preview must not read credentials.");
        public Task WriteAsync(string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A retry preview must not write credentials.");
        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A retry preview must not delete credentials.");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A retry preview must not contact a provider.");
    }
}
