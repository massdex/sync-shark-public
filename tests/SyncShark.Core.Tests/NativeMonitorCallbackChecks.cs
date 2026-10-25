using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Original H6 native-origin supplement only. Root owns compilation and execution. Unlike the
// retained flood fixture, every path hint below originates in the production FileSystemWatcher.
// An observer appended to that same watcher's Created delegate proves its preceding production
// callback returned; reflection obtains the owned watcher, never invokes or replaces a handler.
// A reserved writer on this fixture's SQLite file supplies real persistence backpressure/failure.
// No static test hook, fabricated Error callback, provider, transfer, or production file is used.
internal static class NativeMonitorCallbackChecks
{
    private static readonly TimeSpan CallbackLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PumpLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FaultLimit = TimeSpan.FromSeconds(70);

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Original H6 native callback proof requires Windows.");
        if (SyncAuthorityLocalMonitor.BeforeCommitForTest is not null ||
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest is not null ||
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest is not null)
            throw new InvalidOperationException("Run this native proof serially with unmodified monitor defaults.");

        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh isolated native-monitor fixture root.");
        Directory.CreateDirectory(root);
        var localRoot = Path.Combine(root, "monitored");
        Directory.CreateDirectory(localRoot);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-native-monitor", "Original H6 native callback", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Native Callback Fixture", "id:native-monitor");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-native-monitor", "profile-native-monitor", "H6", "{}");
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        const string bindingId = "root-native-monitor-local";
        await authority.RegisterRootBindingAsync(bindingId, pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        var assertions = 0;
        var observations = new List<object>();
        var completedIds = new List<string>();
        var durableNotifications = 0;
        var healthNotifications = 0;
        var tailReturns = new ConcurrentDictionary<string, TaskCompletionSource<long>>(StringComparer.Ordinal);
        var monitor = CreateMonitor(authority);
        SyncAuthorityLocalMonitor? successor = null;
        WriterBlock? writerBlock = null;
        FileSystemWatcher? observedWatcher = null;
        FileSystemEventHandler? observer = null;
        string? retainedFailure = null;
        var nativeWrites = 0;
        bool? schemaUnchanged = null;
        long? operationAttemptReceiptCount = null;
        var readiness = "FAIL";
        try
        {
            await monitor.StartAsync();
            await WaitUntilAsync(async () =>
                (await authority.LoadMonitorCursorAsync(monitor.CursorId))?.Status == "active", PumpLimit,
                "the real startup sentinel committed before native input");
            Check(await authority.QuickCheckAsync() == "ok", "the isolated authority starts healthy");
            var schemaBefore = await SchemaAsync(authority.DatabasePath);

            observedWatcher = typeof(SyncAuthorityLocalMonitor)
                .GetField("_watcher", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(monitor) as FileSystemWatcher
                ?? throw new InvalidOperationException("The existing production watcher boundary is unavailable.");
            Check(observedWatcher.EnableRaisingEvents && observedWatcher.Path == localRoot,
                "the observer is bound to this production monitor's enabled native watcher");
            observer = (_, item) =>
            {
                if (tailReturns.TryGetValue(item.Name ?? "", out var completion))
                    completion.TrySetResult(Stopwatch.GetTimestamp());
            };
            observedWatcher.Created += observer;

            // First prove legitimate success through an actual native callback. A real writer
            // reservation prevents ANY journal commit until release. Reaching the following
            // delegate promptly while that reservation is held demonstrates callback return,
            // not merely that a separate FileSystemWatcher saw the file.
            var eventsBefore = await EventCountAsync(authority.DatabasePath, monitor.CursorId);
            var cursorBefore = await authority.LoadMonitorCursorAsync(monitor.CursorId);
            writerBlock = await WriterBlock.AcquireAsync(authority.DatabasePath);
            var firstReturnMs = await ProduceNativeAsync("native-success.txt", "native-success");
            Check(await EventCountAsync(authority.DatabasePath, monitor.CursorId) == eventsBefore,
                "the native callback performs no synchronous journal write behind the held writer");
            Check(await CursorEqualsAsync(authority.DatabasePath, monitor.CursorId, cursorBefore?.CursorValue),
                "the callback does not advance durable cursor authority before the pump commit");
            await writerBlock.DisposeAsync();
            writerBlock = null;
            await WaitUntilAsync(async () => await PathEventCountAsync(authority.DatabasePath,
                monitor.CursorId, "native-success.txt") > 0, PumpLimit,
                "the real pump committed the actual native success hint");
            Check(await DirtyPathCountAsync(authority.DatabasePath, pair.Id, "native-success.txt") == 1,
                "the production journal dirties the exact native path");
            Check(monitor.LastFailureReason is null && Volatile.Read(ref durableNotifications) > 0,
                "legitimate native success leaves no failure and reaches the production durable consumer");
            Check(await ScalarAsync(authority.DatabasePath,
                "SELECT count(*) FROM monitor_event WHERE monitor_cursor_id=$cursor AND normalized_path_key=$path AND json_extract(event_json,'$.absenceAuthority')='none' AND json_extract(event_json,'$.reconciliation')='full_pair';",
                ("$cursor", monitor.CursorId), ("$path", "native-success.txt")) > 0,
                "native journal evidence remains a reconciliation hint, never absence/effect authority");
            observations.Add(new { phase = "native-success", callbackReturnMilliseconds = firstReturnMs });
            completedIds.Add("H6-native-success-callback-return-and-journal");

            // Close the healthy startup/path hints through the REAL consumer acknowledgement,
            // not a fixture SQL update. This makes the restart dirty assertion decisive rather
            // than relying on a pair-dirty row left over from startup.
            await Task.Delay(500);
            await authority.CloseMonitorHintsThroughAsync(pair.Id,await authority.CaptureMonitorWatermarkAsync(pair.Id));
            Check(!await authority.HasPendingPairDirtyHintForTestAsync(pair.Id),
                "the healthy predecessor's pair-wide hint is closed before the fault");
            var nativeFaultEventsBefore = await EventCountAsync(authority.DatabasePath, monitor.CursorId);
            var faultCursorBefore = await authority.LoadMonitorCursorAsync(monitor.CursorId);
            writerBlock = await WriterBlock.AcquireAsync(authority.DatabasePath);
            var failedReturnMs = await ProduceNativeAsync("native-failed-commit.txt", "native-fault");
            await WaitUntilAsync(() => Task.FromResult(monitor.LastFailureReason is not null), FaultLimit,
                "the production pump retained its actual fixture SQLite lock failure");
            retainedFailure = monitor.LastFailureReason;
            Check(retainedFailure is { Length: > 0 and <= 400 } &&
                (retainedFailure.Contains("locked", StringComparison.OrdinalIgnoreCase) ||
                 retainedFailure.Contains("busy", StringComparison.OrdinalIgnoreCase)),
                "the real instance-local persistence fault is retained as bounded lock/busy failure text");
            Check(await EventCountAsync(authority.DatabasePath, monitor.CursorId) == nativeFaultEventsBefore &&
                await CursorEqualsAsync(authority.DatabasePath, monitor.CursorId, faultCursorBefore?.CursorValue),
                "failed persistence neither commits a phantom native event nor advances the cursor");

            // A second actual native callback still returns AFTER the handled consumer failure.
            // Do not enqueue a synthetic event or call Enqueue/OnCreated to make this pass.
            var laterReturnMs = await ProduceNativeAsync("native-after-fault.txt", "native-after-fault");
            Check(monitor.LastFailureReason is not null,
                "later native input does not conceal the retained pump fault as success");
            Check(await PathEventCountAsync(authority.DatabasePath, monitor.CursorId, "native-failed-commit.txt") == 0 &&
                await PathEventCountAsync(authority.DatabasePath, monitor.CursorId, "native-after-fault.txt") == 0,
                "both failed native hints remain uncommitted while the real writer is reserved");
            observations.Add(new { phase = "native-consumer-failure", failure = retainedFailure,
                callbackReturnMilliseconds = failedReturnMs, laterCallbackReturnMilliseconds = laterReturnMs });
            completedIds.Add("H6-native-callback-return-after-instance-persistence-failure");

            // Cancel BEFORE releasing the fixture writer. This prevents a racing successful
            // retry from erasing the failure/restart witness. The production DisposeAsync waits
            // for its owned pump (including its handled status write); releasing the writer lets
            // that already-running fault path finish without killing any thread or process.
            observedWatcher.Created -= observer;
            observedWatcher = null;
            var draining = monitor.DisposeAsync().AsTask();
            await writerBlock.DisposeAsync();
            writerBlock = null;
            await draining.WaitAsync(PumpLimit);
            Check(draining.IsCompletedSuccessfully, "production async disposal drains its owned pump");
            Check(monitor.LastFailureReason == retainedFailure,
                "the exact safe consumer failure survives drained disposal");
            Check((await authority.LoadMonitorCursorAsync(monitor.CursorId))?.Status == "unavailable",
                "the contained fault retains durable unavailable status, not fabricated health");
            var disposedCount = await EventCountAsync(authority.DatabasePath, monitor.CursorId);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "native-after-disposal.txt"), "after-disposal");
            nativeWrites++;
            await Task.Delay(500);
            Check(await EventCountAsync(authority.DatabasePath, monitor.CursorId) == disposedCount,
                "a drained/disposed watcher accepts no later real filesystem hint");
            completedIds.Add("H6-native-fault-retained-and-disposal-drained");

            // Reopen the real SQLite authority, with its normal exact-schema/full-health gate.
            // The missed native paths need not be replayed as exact events: a successor must
            // durably require full reconciliation BEFORE enabling its new watcher.
            var reopened = new SyncAuthorityDatabase(authority.DatabasePath);
            await reopened.VerifyHealthAsync();
            schemaUnchanged = await SchemaAsync(reopened.DatabasePath) == schemaBefore;
            Check(await reopened.QuickCheckAsync() == "ok" && schemaUnchanged == true,
                "the fault input leaves real authority health and the complete schema unchanged");
            Check(!await reopened.HasPendingPairDirtyHintForTestAsync(pair.Id),
                "reopen cannot borrow a previously dirty startup sentinel as evidence");
            successor = CreateMonitor(reopened);
            await successor.StartAsync();
            Check((await reopened.LoadMonitorCursorAsync(successor.CursorId))?.Status == "unavailable",
                "new native session establishes durable uncertainty before StartAsync returns");
            await WaitUntilAsync(async () =>
                await reopened.HasPendingPairDirtyHintForTestAsync(pair.Id) &&
                (await reopened.LoadMonitorCursorAsync(successor.CursorId))?.Status == "active", PumpLimit,
                "successor real pump commits a fresh pair-wide reconciliation obligation");
            Check(await ScalarAsync(reopened.DatabasePath,
                "SELECT count(*) FROM monitor_event WHERE monitor_cursor_id=$cursor AND consumed_utc IS NULL AND normalized_path_key IS NULL AND event_kind='overflow' AND json_extract(event_json,'$.absenceAuthority')='none' AND json_extract(event_json,'$.reconciliation')='full_pair';",
                ("$cursor", successor.CursorId)) > 0,
                "restart uncertainty has actual new unconsumed journal evidence, never replay/effect authority");
            Check(await PathEventCountAsync(reopened.DatabasePath, successor.CursorId, "native-failed-commit.txt") == 0 &&
                await PathEventCountAsync(reopened.DatabasePath, successor.CursorId, "native-after-fault.txt") == 0,
                "restart does not invent exact events lost with the prior session");
            operationAttemptReceiptCount = await ScalarAsync(reopened.DatabasePath,
                "SELECT (SELECT count(*) FROM operation)+(SELECT count(*) FROM operation_attempt)+(SELECT count(*) FROM effect_receipt);");
            Check(operationAttemptReceiptCount == 0,
                "callbacks, fault containment and restart grant no operation, attempt or effect receipt");
            await successor.DisposeAsync();
            successor = null;
            completedIds.Add("H6-native-reopen-fresh-dirty-no-lost-reconciliation-authority");
            readiness = "PASS native event callback supplement; OS Error callback remains unverified";
            Console.WriteLine("PASS original H6 native callback supplement: " + assertions +
                " assertions; actual production FileSystemWatcher Created -> Enqueue -> real pump/SQLite; " +
                "bounded native input, instance lock fault, retained failure, disposal and fresh restart dirty; " +
                "OS Error callback NOT elicited/verified.");
        }
        finally
        {
            // Exceptional cleanup also cancels before releasing the owned lock. Never leave a
            // writer reservation or production watcher behind if a decisive assertion rejects.
            if (observedWatcher is not null && observer is not null) observedWatcher.Created -= observer;
            monitor.Dispose();
            successor?.Dispose();
            if (writerBlock is not null) await writerBlock.DisposeAsync();
            await monitor.DisposeAsync().AsTask().WaitAsync(PumpLimit);
            if (successor is not null) await successor.DisposeAsync().AsTask().WaitAsync(PumpLimit);
            await File.WriteAllTextAsync(Path.Combine(root, "native-monitor-callback-checks.json"),
                JsonSerializer.Serialize(new
                {
                    originalId = "H6", readiness, assertions, completedIds, observations,
                    entryPoint = "SyncAuthorityLocalMonitor.StartAsync -> actual FileSystemWatcher.Created -> Enqueue -> PumpAsync -> SyncAuthorityDatabase.CommitMonitorBatchAsync",
                    healthNotifications = Volatile.Read(ref healthNotifications),
                    durableNotifications = Volatile.Read(ref durableNotifications),
                    qualifiedGap = "OS FileSystemWatcher.Error was not deterministically elicited; no overflow/flood or invoked handler substitutes for it.",
                    nativeWrites, staticHooksChanged = false, schemaUnchanged,
                    operationAttemptReceiptCount, providerConstructed = false
                }, new JsonSerializerOptions { WriteIndented = true }));
        }

        SyncAuthorityLocalMonitor CreateMonitor(SyncAuthorityDatabase database) => new(pair, database, bindingId,
            _ => { Interlocked.Increment(ref durableNotifications); return Task.CompletedTask; },
            _ => { Interlocked.Increment(ref healthNotifications); return Task.CompletedTask; },
            enableUsnJournal: false);

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidDataException("original H6 native callback assertion failed: " + message);
        }

        async Task<double> ProduceNativeAsync(string name, string text)
        {
            var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            Check(tailReturns.TryAdd(name, completion), "each native producer path is unique");
            var started = Stopwatch.GetTimestamp();
            await File.WriteAllTextAsync(Path.Combine(localRoot, name), text).WaitAsync(CallbackLimit);
            nativeWrites++;
            var returned = await completion.Task.WaitAsync(CallbackLimit);
            var elapsed = Stopwatch.GetElapsedTime(started, returned).TotalMilliseconds;
            Check(elapsed < CallbackLimit.TotalMilliseconds,
                "the appended Created observer proves the production native callback returned promptly: " + name);
            return elapsed;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan limit, string label)
    {
        var started = Stopwatch.StartNew();
        while (!await condition())
        {
            if (started.Elapsed >= limit) throw new InvalidOperationException("Timed out: " + label);
            await Task.Delay(20);
        }
    }

    private static Task<long> EventCountAsync(string path, string cursorId) => ScalarAsync(path,
        "SELECT count(*) FROM monitor_event WHERE monitor_cursor_id=$cursor;", ("$cursor", cursorId));

    private static Task<long> PathEventCountAsync(string path, string cursorId, string relativePath) => ScalarAsync(path,
        "SELECT count(*) FROM monitor_event WHERE monitor_cursor_id=$cursor AND normalized_path_key=$path;",
        ("$cursor", cursorId), ("$path", relativePath));

    private static Task<long> DirtyPathCountAsync(string path, string pairId, string relativePath) => ScalarAsync(path,
        "SELECT count(*) FROM dirty_item WHERE pair_id=$pair AND normalized_path_key=$path AND status='dirty';",
        ("$pair", pairId), ("$path", relativePath));

    private static async Task<bool> CursorEqualsAsync(string path, string cursorId, string? expected) =>
        await ScalarAsync(path,
            "SELECT count(*) FROM monitor_cursor WHERE monitor_cursor_id=$cursor AND cursor_value IS $value;",
            ("$cursor", cursorId), ("$value", (object?)expected ?? DBNull.Value)) == 1;

    private static async Task<long> ScalarAsync(string path, string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new SqliteConnection(ReadOnlyConnectionString(path));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> SchemaAsync(string path)
    {
        await using var connection = new SqliteConnection(ReadOnlyConnectionString(path));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql,char(10)) FROM (SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name);";
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static string ReadOnlyConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
    }.ToString();

    private sealed class WriterBlock(SqliteConnection connection, SqliteTransaction transaction) : IAsyncDisposable
    {
        private SqliteTransaction? _transaction = transaction;

        internal static async Task<WriterBlock> AcquireAsync(string databasePath)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString());
            try
            {
                await connection.OpenAsync();
                // Reserves only this fixture file's writer; no schema/data/trigger mutation.
                return new WriterBlock(connection, connection.BeginTransaction(deferred: false));
            }
            catch { await connection.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            var retained = Interlocked.Exchange(ref _transaction, null);
            if (retained is null) return;
            try { await retained.RollbackAsync(); }
            finally { await retained.DisposeAsync(); await connection.DisposeAsync(); }
        }
    }
}
