using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ActivityPersistenceConcurrencyChecks
{
    internal static async Task RunObservationAdmissionAsync(string root)
    {
        foreach (var (concurrent, cancel, publication) in new[]
                 { (false, false, false), (true, false, false), (true, true, false),
                     (false, false, true), (true, false, true), (true, true, true) })
        {
            var fixture = Path.Combine(root, $"concurrent-{concurrent}-cancel-{cancel}-publication-{publication}");
            if (Directory.Exists(fixture)) throw new IOException("Use fresh observation fixtures.");
            var local = Path.Combine(fixture, "local"); Directory.CreateDirectory(local);
            // Publication proofs must cross the larger trusted metadata boundary,
            // not exercise only a single commit. Hash admission keeps its fixture.
            var fileCount = publication ? 1088 : 128;
            for (var index = 0; index < fileCount; index++)
            {
                var parent = index % 32 == 1 ? Path.Combine(local, $"file-{index:D3}-directory") : local;
                Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(Path.Combine(parent, $"file-{index:D3}.txt"), "fresh held bytes " + index);
            }
            using var localLease = PilotDirectoryLease.Open(local);
            var pair = new SyncPair("observation-admission", "Observation admission", local,
                localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId, "/Fixture", "id:fixture-root");
            var database = new SyncAuthorityDatabase(Path.Combine(fixture, "activity.db"));
            await database.InitializeAsync("observation-install", "observation-profile", "test", "{}");
            await database.RegisterPairAsync(pair.Id, 1);
            await database.RetainPairDefinitionAsync(pair, true);
            await database.RegisterRootBindingAsync("observation-local", pair.Id, "local", 1,
                JsonSerializer.Serialize(localLease.Identity), local);
            await database.RegisterRootBindingAsync("observation-remote", pair.Id, "dropbox", 1,
                "{\"folderId\":\"id:fixture-root\"}", pair.DropboxRoot);
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
                authorityDatabase: database);
            var remote = new FakeSyncRemoteStore();
            var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var appended = new List<SyncActivityEvent>();
            database.ActivityAppendBoundaryForTest = async (boundary, items) =>
            {
                if (boundary == "before")
                {
                    appended.AddRange(items);
                    if (items.Any(item => publication
                            ? item.Message.StartsWith("Saving Folder Inventories | Saved ", StringComparison.Ordinal)
                            : item.Message.StartsWith("Hashing file contents | ", StringComparison.Ordinal)) && held.TrySetResult(items.Count))
                        await release.Task;
                }
            };
            var submitted = 0; var delivered = 0; var presenting = 0; var overlapping = false; var forcedPublicationFlush = false;
            var submittedEvents = new List<SyncComparisonProgress>();
            var presentedEvents = new List<SyncComparisonProgress>();
            var actionSequence = 0;
            Task Feedback(SyncComparisonProgress item)
            {
                Interlocked.Increment(ref submitted);
                submittedEvents.Add(item);
                // A real inventory observation callback is informational, so this fixture keeps the same real
                // held-append boundary by submitting the equivalent durable terminal action record instead. Its
                // exact phase, path, reason and measured count are carried through unchanged.
                var durable = new SyncAuthorityBatchProgress(pair, "Running", null, null, null,
                    Action: new SyncAuthorityLiveAction("observation-" + Interlocked.Increment(ref actionSequence),
                        item.RelativePath, "Inspection", "Completed", item.Phase + " | " + item.Detail,
                        item.ReportedUtc, "File", "Unknown", OperationKind: null, Phase: SyncActivityPhase.Completed,
                        Progress: new SyncActivityProgress(item.CompletedChecks,
                            item.TotalChecks > 0 ? item.TotalChecks : null, item.ProgressUnit, 0)));
                var delivery = host.RetainProgressForTestAsync(durable,
                    async _ =>
                    {
                        if (Interlocked.Increment(ref presenting) != 1) overlapping = true;
                        try { await Task.Yield(); presentedEvents.Add(item); delivered++; }
                        finally { Interlocked.Decrement(ref presenting); }
                    });
                // Hold the actual first saved-row append, independent of timer
                // batching. The observer must drain this original Host operation.
                if (publication && !forcedPublicationFlush && item.Detail.StartsWith("Saved ", StringComparison.Ordinal))
                {
                    forcedPublicationFlush = true;
                    return Task.WhenAll(delivery, host.RetryPendingActivityPersistenceAsync());
                }
                return delivery;
            }
            var resources = new SyncTransferResourceController(new SyncTransferSettings(
                ResourceMode: "Performance", MaximumConcurrentFiles: 8), TimeProvider.System,
                () => new SyncResourcePressure(0, .1, 8L * 1024 * 1024 * 1024));
            var observer = new SyncAuthorityObservationCoordinator(pair, remote, database,
                "observation-local", "observation-remote", Feedback)
                { InventoryResources = resources, ConcurrentProgressRetention = concurrent };
            using var cancellation = new CancellationTokenSource();
            var observation = observer.ObserveAsync("initial", cancellation.Token);
            try
            {
                var heldCount = await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(publication ? resources.Snapshot().ActiveFiles == 0 : resources.Snapshot().ActiveFiles > 0,
                    publication ? "The actual saved-row append is held after every real inspection lease drained" :
                        "The held original append occurs while real file-inspection workers still own their leases");
                if (concurrent)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    while (host.ActivityLogPendingCount <= heldCount && DateTime.UtcNow < deadline)
                        await Task.Delay(5);
                    Require(host.ActivityLogPendingCount > heldCount,
                        publication ? "Trusted publication admits its bounded saved-row prefix during a held original append" :
                            "Actual concurrent hash observer admits additional immutable Host events during a held real append");
                }
                else
                {
                    await Task.Delay(50);
                    Require(host.ActivityLogPendingCount == heldCount,
                        "Default arbitrary observer callback remains serialized through its awaited completion");
                }
                if (cancel) cancellation.Cancel();
                release.TrySetResult();
                if (cancel)
                {
                    try { await observation.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Cancelled scan published success"); }
                    catch (OperationCanceledException) { }
                    await using var cancelledRead = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
                    await cancelledRead.OpenAsync();
                    using var eligibility = cancelledRead.CreateCommand();
                    eligibility.CommandText = "SELECT (SELECT count(*) FROM scan_generation WHERE pair_id=$pair AND status='complete') + " +
                        "(SELECT count(*) FROM sync_path_index WHERE pair_id=$pair);";
                    eligibility.Parameters.AddWithValue("$pair", pair.Id);
                    Require(Convert.ToInt64(await eligibility.ExecuteScalarAsync()) == 0,
                        "Cancellation publishes neither local nor remote completed scan, path index or new comparison-eligible inventory");
                }
                else
                {
                    var result = await observation.WaitAsync(TimeSpan.FromSeconds(10));
                    Require(result.LocalEntries == fileCount + fileCount / 32 && result.LocalFilesHashed == fileCount,
                        "Actual mixed-directory observation publishes every fresh file and verified directory after all callbacks complete");
                }
                Require(!overlapping && submitted == delivered && presenting == 0 && resources.Snapshot().ActiveFiles == 0,
                    "Ordered Host presentation drains every submitted event and inspection lease before return");
                Require(submittedEvents.SequenceEqual(presentedEvents),
                    "Every particular immutable observer notification is presented exactly once in submission order");
                await host.RetryPendingActivityPersistenceAsync();
                var rows = await ReadEventsAsync(database);
                Require(rows.Count == submitted && !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
                    "Actual append-only history retains every observer event exactly once after success or cancellation");
                Require(appended.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() == submitted,
                    "Every retained observer record has one unique admitted event identity");
                Require(ExpandedPayloads(rows).SequenceEqual(appended.Select(CanonicalPayload)),
                    "Reopened SQLite retains every exact admitted identity and sanitized payload in submission order");
                Require(appended.Zip(submittedEvents).All(tuple =>
                        tuple.First.Action == "Inspection | Completed" &&
                        tuple.First.RelativePath == tuple.Second.RelativePath && tuple.First.OccurredUtc == tuple.Second.ReportedUtc &&
                        tuple.First.Message == tuple.Second.Phase + " | " + tuple.Second.Detail &&
                        tuple.First.Progress?.Completed == tuple.Second.CompletedChecks),
                    "Each pre-sanitization admitted record corresponds to its particular submitted phase, path, time, full reason and count");
                foreach (var file in Directory.GetFiles(local, "*", SearchOption.AllDirectories))
                {
                    using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                Require(remote.UploadCount == 0 && remote.DownloadCount == 0 && remote.DeleteCount == 0,
                    "Observed admission/cancellation proofs authorize no provider effect and release every native file handle");
            }
            finally
            {
                release.TrySetResult(); cancellation.Cancel();
                try { await observation; } catch (OperationCanceledException) { }
            }
        }
        await RunArbitraryObservationCallbackAsync(Path.Combine(root, "non-host-callback"));
    }

    private static async Task RunArbitraryObservationCallbackAsync(string fixture)
    {
        var local = Path.Combine(fixture, "local"); Directory.CreateDirectory(local);
        for (var index = 0; index < 64; index++)
            await File.WriteAllTextAsync(Path.Combine(local, $"file-{index:D3}.txt"), "non-host bytes " + index);
        using var localLease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("non-host-observation", "Non-Host callback", local,
            localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId, "/Fixture", "id:fixture-root");
        var database = new SyncAuthorityDatabase(Path.Combine(fixture, "activity.db"));
        await database.InitializeAsync("non-host-install", "observation-profile", "test", "{}");
        await database.RegisterPairAsync(pair.Id, 1);
        await database.RegisterRootBindingAsync("non-host-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(localLease.Identity), local);
        await database.RegisterRootBindingAsync("non-host-remote", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:fixture-root\"}", pair.DropboxRoot);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0; var active = 0; var overlapping = false;
        async Task ArbitraryCallback(SyncComparisonProgress item)
        {
            Interlocked.Increment(ref entered);
            if (Interlocked.Increment(ref active) != 1) overlapping = true;
            try
            {
                if (item.Phase == "Hashing file contents" && held.TrySetResult()) await release.Task;
                await Task.Yield();
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var resources = new SyncTransferResourceController(new SyncTransferSettings(
            ResourceMode: "Performance", MaximumConcurrentFiles: 8), TimeProvider.System,
            () => new SyncResourcePressure(0, .1, 8L * 1024 * 1024 * 1024));
        var observer = new SyncAuthorityObservationCoordinator(pair, new FakeSyncRemoteStore(), database,
            "non-host-local", "non-host-remote", ArbitraryCallback) { InventoryResources = resources };
        using var cancellation = new CancellationTokenSource();
        var observation = observer.ObserveAsync("initial", cancellation.Token);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var before = Volatile.Read(ref entered);
            await Task.Delay(50);
            Require(Volatile.Read(ref entered) == before && Volatile.Read(ref active) == 1 && !overlapping,
                "A genuinely non-Host delayed callback defaults to serialized invocation despite competing file inspections");
            release.TrySetResult();
            var result = await observation.WaitAsync(TimeSpan.FromSeconds(10));
            Require(result.LocalEntries == 64 && result.LocalFilesHashed == 64 && active == 0 && !overlapping &&
                resources.Snapshot().ActiveFiles == 0,
                "Arbitrary callback completion preserves fresh observation and drains all inspection leases");
            foreach (var file in Directory.GetFiles(local))
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            release.TrySetResult(); cancellation.Cancel();
            try { await observation; } catch (OperationCanceledException) { }
        }
    }

    internal static async Task RunInventoryBatchingAsync(string root)
    {
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "activity.db"));
        await database.InitializeAsync("inventory-log", "activity-profile", "owner-fixture", "{}");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "instance.lock"));
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
            authorityDatabase: database);
        var pair = new SyncPair("pair-inventory-batching", "Inventory batch fixture", root, 1, 1,
            "/fixture", "id:fixture");
        var batches = new List<SyncActivityEvent[]>();
        database.ActivityAppendBoundaryForTest = async (boundary, items) =>
        {
            if (boundary != "before") return;
            batches.Add(items.ToArray());
            await Task.Delay(5); // Real append remains in place; only its bounded latency is injected.
        };
        var now = DateTimeOffset.UtcNow;
        var deliveredInventory = new List<SyncComparisonProgress>();
        for (var index = 0; index < 200; index++)
        foreach (var phase in new[] { "Reading Local Inventory", "Hashing file contents" })
            await host.RetainProgressForTestAsync(new(pair, "Checking", null, null, null,
                    Comparison: new(phase, $"file-{index}.txt", index, 0, now, DateTimeOffset.UtcNow, phase)),
                progress => { deliveredInventory.Add(progress.Comparison!); return Task.CompletedTask; });
        Require(batches.Count == 0 && deliveredInventory.Count == 400 && !host.ActivityLogIncomplete &&
                host.ActivityLogPendingCount == 0 && (await ReadEventsAsync(database)).Count == 0 &&
                deliveredInventory.Select(item => item.RelativePath).SequenceEqual(
                    Enumerable.Range(0, 200).SelectMany(index => Enumerable.Repeat($"file-{index}.txt", 2))),
            "Four hundred alternating transient inventory callbacks deliver live and in order while adding no durable row, no pending event and no log gap");
        await host.RetainProgressForTestAsync(new(pair, "Stopped", null, "FixtureCancelled",
            "The bounded inventory was cancelled; no file effect was authorized."));
        var rows = await ReadEventsAsync(database);
        Require(rows.Count == 1 && rows[^1].Contains("FixtureCancelled") &&
                rows[^1].Contains("no file effect was authorized") && !host.ActivityLogIncomplete &&
                host.ActivityLogPendingCount == 0 && batches.Count == 1,
            "The terminal cancellation is the only retained record, keeps its exact reason and drains its durable queue");
        Console.WriteLine($"PASS inventory transient delivery: 400 transient callbacks and one terminal outcome in {batches.Count} real commit");
    }

    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh activity-persistence fixture root.");
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "activity.db"));
        await database.InitializeAsync("activity-concurrency", "activity-profile", "owner-fixture", "{}");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
            authorityDatabase: database);
        var pair = new SyncPair("pair-log-concurrency", "Concurrent log fixture", root, 1, 1,
            "/fixture", "id:fixture");
        var firstAppend = Signal();
        var releaseAppend = Signal();
        var batches = new List<SyncActivityEvent[]>();
        var delivered = new List<string>();
        var callbacksActive = 0;
        var overlapped = false;
        database.ActivityAppendBoundaryForTest = async (boundary, items) =>
        {
            if (boundary != "before") return;
            batches.Add(items.ToArray());
            if (batches.Count == 1)
            {
                firstAppend.SetResult(true);
                await releaseAppend.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        async Task Present(SyncAuthorityBatchProgress progress)
        {
            if (Interlocked.Increment(ref callbacksActive) != 1) overlapped = true;
            await Task.Yield();
            delivered.Add(progress.Action!.OperationId);
            Interlocked.Decrement(ref callbacksActive);
        }
        var tasks = new List<Task> { host.RetainProgressForTestAsync(Event(pair, 0), Present) };
        await firstAppend.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var index = 1; index < 64; index++)
            tasks.Add(host.RetainProgressForTestAsync(Event(pair, index), Present));
        Require(host.ActivityLogPendingCount == 64 && tasks.All(task => !task.IsCompleted) && delivered.Count == 0,
            "All 64 producers enter the durable queue without publishing or completing ahead of the held real commit");
        releaseAppend.SetResult(true);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Require(batches.Count < 64 && batches.Any(batch => batch.Length > 1),
            "Concurrent events share real database commits instead of one awaited commit per producer");
        Require(!overlapped && delivered.SequenceEqual(Enumerable.Range(0, 64).Select(index => "operation-" + index)),
            "Asynchronous presentation callbacks retain submission order and never overlap");
        var expected = batches.SelectMany(batch => batch).ToArray();
        Require(expected.Length == 64 && expected.Select(item => item.EventId).Distinct().Count() == 64 &&
            expected.Select(item => item.OperationId).SequenceEqual(delivered),
            "Every admitted event retains one identity in the same durable and presentation order");
        Require(expected.Select((item, index) =>
        {
            var input = Event(pair, index).Action!;
            return item.OperationId == input.OperationId && item.RelativePath == input.RelativePath &&
                item.OccurredUtc == input.OccurredUtc && item.Action == input.Action + " — " + input.Stage &&
                item.Message == input.Detail && item.PairId == pair.Id && item.Entity == "File" &&
                item.Severity == "Success" && item.Categories.SequenceEqual(new[] { SyncActivityCategories.Sync }) &&
                item.OperationKind == input.OperationKind && item.Phase == input.Phase && item.Progress == input.Progress &&
                JsonSerializer.Serialize(item.EndpointContext) == JsonSerializer.Serialize(SyncEndpointContext.Capture(pair));
        }).All(matches => matches),
            "Durable admission preserves the original terminal operation, endpoint, state and progress semantics");
        var retained = await ReadEventsAsync(database);
        Require(ExpandedPayloads(retained).SequenceEqual(expected.Select(CanonicalPayload)),
            "Reopened SQLite history retains every event field, detail, path, phase and measured counter exactly");
        Require(!host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "The successful durable drain leaves no pending event or incomplete-log warning");

        // Exercise truly concurrent host callers, not only the runner's serialized
        // submission prefix. Retained order and callback order must still agree.
        batches.Clear();
        delivered.Clear();
        database.ActivityAppendBoundaryForTest = (boundary, items) =>
        {
            if (boundary == "before") batches.Add(items.ToArray());
            return Task.CompletedTask;
        };
        await Task.WhenAll(Enumerable.Range(64, 128).Select(index => Task.Run(() =>
            host.RetainProgressForTestAsync(Event(pair, index), Present)))).WaitAsync(TimeSpan.FromSeconds(30));
        Require(!overlapped && batches.SelectMany(batch => batch).Select(item => item.OperationId).SequenceEqual(delivered) &&
            delivered.Count == 128 && delivered.Distinct().Count() == 128,
            "Concurrent host callers cannot reorder or lose retained events relative to their UI/CLI delivery");
        retained = await ReadEventsAsync(database);
        Require(ExpandedPayloads(retained).SequenceEqual(
                expected.Concat(batches.SelectMany(batch => batch)).Select(CanonicalPayload)),
            "Concurrent admission preserves exact payloads through the real append-only database");
        Require(retained.Count(payload => JsonNode.Parse(payload)?["EndpointContext"]?["EndpointContextEventId"]
                is not null) > 0,
            "Concurrent admission really retained batch-local context references, so the logical expansion above is decisive");

        database.ActivityAppendBoundaryForTest = null;
        foreach (var failureBoundary in new[] { "before", "after" })
        {
            var before = await ReadEventsAsync(database);
            SyncActivityEvent? attempted = null;
            var injected = false;
            database.ActivityAppendBoundaryForTest = (boundary, items) =>
            {
                if (boundary == "before") attempted ??= items.Single();
                if (!injected && boundary == failureBoundary)
                {
                    injected = true;
                    throw new IOException("fixture persistence failure " + failureBoundary);
                }
                return Task.CompletedTask;
            };
            await host.RetainProgressForTestAsync(Event(pair, failureBoundary == "before" ? 192 : 193));
            Require(injected && host.ActivityLogIncomplete && host.ActivityLogPendingCount == 1 &&
                host.ActivityLogError?.Contains("fixture persistence failure " + failureBoundary) == true,
                "A " + failureBoundary + "-commit failure retains the exact pending event and exposes its reason");
            var failed = await ReadEventsAsync(database);
            Require(failed.Count == before.Count + (failureBoundary == "after" ? 1 : 0),
                "Failure injection proves the actual " + failureBoundary + "-commit boundary, not a substituted persistence result");
            database.ActivityAppendBoundaryForTest = null;
            await host.RetryPendingActivityPersistenceAsync();
            var retried = await ReadEventsAsync(database);
            Require(retried.Count == before.Count + 1 && retried[^1] == CanonicalPayload(attempted!) &&
                !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
                "Retry after " + failureBoundary + "-commit failure is idempotent and preserves every original payload field");
        }

        var failedCallbackObserved = false;
        await host.RetainProgressForTestAsync(Event(pair, 194), _ =>
        {
            failedCallbackObserved = true;
            throw new IOException("fixture presentation failure");
        });
        var followingCallbackObserved = false;
        await host.RetainProgressForTestAsync(new(pair, "Completed", null, null, null), _ =>
        {
            followingCallbackObserved = true;
            return Task.CompletedTask;
        });
        await host.RetryPendingActivityPersistenceAsync();
        var terminalRows = await new SyncAuthorityDatabase(database.DatabasePath).LoadActivityProjectionPageAsync(500,
            pairId: pair.Id, query: new SyncActivityQuery(DetailedLog: true));
        Require(failedCallbackObserved && followingCallbackObserved && terminalRows.Count == 196 &&
            terminalRows[^1].Outcome == "Sync finished" && !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "A presentation failure cannot poison later delivery or suppress the final durable run outcome");
        var terminal = JsonSerializer.Deserialize<SyncActivityEvent>((await ReadEventsAsync(database))[^1])!;
        var rejected = false;
        try { await database.AppendActivityEventAsync(terminal with { Message = "different retained authority" }); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && (await ReadEventsAsync(database)).Count == 196,
            "A repeated event identity with different content is still rejected without changing retained history");
    }

    private static SyncAuthorityBatchProgress Event(SyncPair pair, int index)
    {
        // A durable terminal action record. An informational Planned/Running/Internal action phase is transient
        // now, and a real persisted operation is a terminal one, so this fixture uses the retained phase.
        var action = new SyncAuthorityLiveAction("operation-" + index, $"folder/file-{index:D3}.bin",
            "Download", "Completed", "Completed the retained download of fixture file " + index,
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(index), "File", "BIN",
            SyncAuthorityOperationKind.CreateLocal, SyncActivityPhase.Completed,
            new SyncActivityProgress(index, 256, "bytes", index * 10));
        return new(pair, "Running", null, null, null, action);
    }

    private static string CanonicalPayload(SyncActivityEvent item) => JsonSerializer.Serialize(item with
    {
        OccurredUtc = item.OccurredUtc.ToUniversalTime(), Action = SyncActivityText.Safe(item.Action),
        Message = SyncActivityText.Safe(item.Message)
    });

    // The retained store keeps one full endpoint context per append transaction (its first NEW row) and an
    // internal one-hop reference for the other rows of that same transaction. A stored payload is therefore
    // expanded through its anchor exactly as the shared projection resolves it, so these assertions compare the
    // complete logical record rather than the presentation representation chosen to store it.
    private static IReadOnlyList<string> ExpandedPayloads(IReadOnlyList<string> stored)
    {
        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var items = new List<JsonObject>();
        foreach (var payload in stored)
        {
            var item = JsonNode.Parse(payload)!.AsObject();
            items.Add(item);
            byId[item["EventId"]!.GetValue<string>()] = item;
        }
        return items.Select(item =>
        {
            if (item["EndpointContext"] is JsonObject reference &&
                reference["EndpointContextEventId"] is JsonValue value &&
                value.TryGetValue<string>(out var anchor) && anchor is { Length: > 0 } &&
                byId.TryGetValue(anchor, out var anchorPayload))
            {
                item["EndpointContext"] = anchorPayload["EndpointContext"]!.DeepClone();
            }
            return JsonSerializer.Serialize(JsonSerializer.Deserialize<SyncActivityEvent>(item.ToJsonString()));
        }).ToList();
    }

    private static async Task<List<string>> ReadEventsAsync(SyncAuthorityDatabase database)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail_json FROM history_event WHERE event_kind='presentation' ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }
}
