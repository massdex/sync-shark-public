using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// DBP02/DBP03/DBP04 producer-growth proofs. Every row comes from the real Host progress entry point, the
// real append path or the real reconciliation planner, and every read goes through the real projection or a
// read-only SQLite connection to the same file. No provider, credential or installed state is used.
internal static class CompactProducerChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        // J2 aggregation: each independent case keeps its own isolated root, every case runs even when an
        // earlier one fails, and the runner reports one consolidated failure after all six have been tried.
        var casesToRun = new (string Name, string Root, Func<string, Task> Run)[]
        {
            ("transient-scale", Path.Combine(root, "transient-scale"), RunTransientScaleAsync),
            ("held-append", Path.Combine(root, "held-append"), RunHeldAppendAsync),
            ("held-writer", Path.Combine(root, "held-writer"), RunTransientDeliveryUnderHeldWriterAsync),
            ("planner-no-op", Path.Combine(root, "planner-no-op"), RunPlannerNoOpAndSafetyAsync),
            ("planner-skip", Path.Combine(root, "planner-skip"), RunPlannerSkipDirectionAsync),
            ("historic-rows", Path.Combine(root, "historic-rows"), RunHistoricRowReadabilityAsync)
        };
        var failures = new List<string>();
        foreach (var item in casesToRun)
        {
            try { await item.Run(item.Root); }
            catch (Exception error)
            {
                failures.Add(item.Name + ": " + error.GetType().Name + ": " + error.Message);
                Console.WriteLine("FAIL " + item.Name + ": " + error.GetType().Name + ": " + error.Message);
            }
        }
        if (failures.Count > 0)
        {
            foreach (var failure in failures) Console.WriteLine("FAIL " + failure);
            throw new InvalidDataException(failures.Count + " of " + casesToRun.Length + " compact-producer cases failed.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", check = "compact-producer-checks" }));
    }

    // DBP02: a real Host progress entry, >=10000 distinct transient callbacks, no new presentation rows and
    // all three fixed cache bounds, with terminal run and per-action outcomes still retained exactly.
    private static async Task RunTransientScaleAsync(string root)
    {
        var database = new SyncAuthorityDatabase(Path.Combine(root, "activity.db"));
        await database.InitializeAsync("compact-producer", "compact-profile", "owner-fixture", "{}");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "instance.lock"));
        using var client = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
            authorityDatabase: database);
        var pair = new SyncPair("pair-compact-producer", "Compact producer fixture", root, 1, 1, "/fixture", "id:fixture");
        await using var admittedHistory = await AdmittedHistoryFixture.StartAsync(host, database, pair);
        // An offset-free monotonic clock so the ten-minute cache bound is proved without waiting ten minutes.
        var clock = Environment.TickCount64;
        host.ActivityProgressClockForTest = () => Volatile.Read(ref clock);
        await host.RetainProgressForTestAsync(new(pair, "Pending", null, null, null));
        // F5: the per-action observed instant is captured only after the Pending start row above.
        var observed = DateTimeOffset.UtcNow;
        var terminalPhases = new[] { SyncActivityPhase.Completed, SyncActivityPhase.Blocked,
            SyncActivityPhase.Conflict, SyncActivityPhase.Cancelled, SyncActivityPhase.EffectUnknown,
            SyncActivityPhase.Superseded, SyncActivityPhase.Unknown };
        foreach (var phase in terminalPhases)
            await host.RetainProgressForTestAsync(new(pair, "Running", null, null, null,
                Action: new SyncAuthorityLiveAction("retained-" + phase, "folder/retained-" + phase + ".txt",
                    "Download", phase.ToString(), "Retained terminal reason for " + phase + ".",
                    observed, "File", "TXT", OperationKind: null, Phase: phase)));
        await host.RetainProgressForTestAsync(new(pair, "Completed", null, null, null));
        var durable = await LoadPairRowsAsync(database, pair.Id);
        Require(durable.Count == terminalPhases.Length + 2 && durable[0].Outcome == "Sync started" &&
                durable[^1].Outcome == "Sync finished" && durable[^1].Reason == "Sync finished" &&
                durable[^1].EndpointContext is not null &&
                JsonSerializer.Serialize(durable[^1].EndpointContext) ==
                    JsonSerializer.Serialize(SyncEndpointContext.Capture(pair)) &&
                terminalPhases.All(phase => durable.Any(row => row.RelativePath == "folder/retained-" + phase + ".txt" &&
                    row.Phase == phase && row.OperationId == "retained-" + phase &&
                    row.Reason == "Retained terminal reason for " + phase + ".")),
            "The real host persists the batch start, the batch terminal outcome and every per-action terminal phase with its exact path, phase, operation identity, reason and endpoint context");
        Require(!host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "Retaining the batch start and every terminal outcome leaves no pending or omitted presentation event");

        var phases = new[] { "Reading Local Inventory", "Hashing file contents", "Comparing files" };
        var order = new List<string>();
        var live = 0;
        for (var index = 0; index < 10000; index++)
        {
            var phase = phases[index % phases.Length];
            var path = "folder/file-" + index.ToString("D5") + ".txt";
            await host.RetainProgressForTestAsync(new(pair, "Running", null, null, null,
                    Action: new SyncAuthorityLiveAction("op-" + index, path, "Download", phase,
                        "Transient " + phase + " callback for " + path + ".", observed, "File", "TXT",
                        OperationKind: null, Phase: SyncActivityPhase.Running,
                        Progress: new SyncActivityProgress(index, 10000, "items checked", 0))),
                progress =>
                {
                    live++;
                    order.Add(progress.Action!.OperationId + "|" + progress.Action.RelativePath + "|" + progress.Action.Stage);
                    return Task.CompletedTask;
                });
        }
        Require(live == 10000 && order.Count == 10000 && order.Distinct(StringComparer.Ordinal).Count() == 10000 &&
                order[0] == "op-0|folder/file-00000.txt|Reading Local Inventory" &&
                order[9999] == "op-9999|folder/file-09999.txt|" + phases[9999 % phases.Length] &&
                order.SequenceEqual(Enumerable.Range(0, 10000).Select(index => "op-" + index + "|folder/file-" +
                    index.ToString("D5") + ".txt|" + phases[index % phases.Length])),
            "10000 distinct scan, hash and comparison callbacks are delivered to their live callback exactly once each, in submission order, with their exact operation, path and phase");
        var afterTransient = await LoadPairRowsAsync(database, pair.Id);
        Require(afterTransient.Count == durable.Count &&
                JsonSerializer.Serialize(afterTransient) == JsonSerializer.Serialize(durable) &&
                !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "Not one transient callback adds or alters a presentation row, a pending event or a log gap");
        var cache = host.ActivityProgressCacheForTest;
        Require(cache.Entries <= 1000 && cache.Entries > 0 && cache.Entries < 10000 &&
                cache.Bytes <= 2 * 1024 * 1024,
            "Across 10000 distinct transient keys the phase/path timing cache stays inside its at-most-1000-entry and at-most-2MiB bounds and really evicts (entries=" + cache.Entries + ", bytes=" + cache.Bytes + ")");

        for (var index = 0; index < 600; index++)
            await host.RetainProgressForTestAsync(new(pair, "Running", null, null, null,
                Action: new SyncAuthorityLiveAction("long-" + index, "folder/" + new string('y', 4000) + "-" + index + ".txt",
                    "Download", "Hashing file contents", "Long transient path callback.", observed, "File", "TXT",
                    OperationKind: null, Phase: SyncActivityPhase.Running)));
        var byBytes = host.ActivityProgressCacheForTest;
        Require(byBytes.Bytes <= 2 * 1024 * 1024 && byBytes.Entries < 1000 && byBytes.Entries > 0,
            "The conservatively charged byte bound is enforced on its own, evicting least-recently-updated transient entries first (entries=" + byBytes.Entries + ", bytes=" + byBytes.Bytes + ")");

        clock += (long)TimeSpan.FromMinutes(10).TotalMilliseconds + 2000;
        await host.RetainProgressForTestAsync(new(pair, "Running", null, null, null,
            Action: new SyncAuthorityLiveAction("aged-proof", "folder/aged.txt", "Download", "Hashing file contents",
                "Post-age transient callback.", observed, "File", "TXT", OperationKind: null,
                Phase: SyncActivityPhase.Running)));
        var aged = host.ActivityProgressCacheForTest;
        Require(aged.Entries == 1 && aged.Bytes <= 2 * 1024 * 1024,
            "The phase/path timing cache is bounded to the latest ten minutes: every aged entry is dropped (entries=" + aged.Entries + ")");

        var oversizePath = "folder/" + new string('z', 3 * 1024 * 1024) + ".txt";
        var oversizeDelivered = false;
        await host.RetainProgressForTestAsync(new(pair, "Comparing", null, null, null,
                Comparison: new("Hashing file contents", oversizePath, 1, 1, observed, observed, "Oversize transient callback.")),
            progress => { oversizeDelivered = progress.Comparison?.RelativePath == oversizePath; return Task.CompletedTask; });
        var afterOversize = host.ActivityProgressCacheForTest;
        Require(oversizeDelivered && afterOversize.Entries == 1 && afterOversize.Bytes <= 2 * 1024 * 1024,
            "An entry whose own conservative charge exceeds the byte bound stays uncached while its live callback still receives the complete untruncated path");
        Require((await LoadPairRowsAsync(database, pair.Id)).Count == durable.Count,
            "The bounded cache never becomes a presentation row: the transient stream still retains nothing");
    }

    // DBP03: a held real append keeps prior ordering, immutable identities and prefix dequeue; transient
    // updates add no durable backlog; before/after failures retry idempotently without re-running a callback.
    private static async Task RunHeldAppendAsync(string root)
    {
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "activity.db"));
        await database.InitializeAsync("compact-held", "compact-profile", "owner-fixture", "{}");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "instance.lock"));
        using var client = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
            authorityDatabase: database);
        var pair = new SyncPair("pair-compact-held", "Compact held fixture", root, 1, 1, "/fixture", "id:fixture");
        await using var admittedHistory = await AdmittedHistoryFixture.StartAsync(host, database, pair);
        var observed = DateTimeOffset.UtcNow;
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
            delivered.Add(progress.Action?.OperationId ?? "transient");
            Interlocked.Decrement(ref callbacksActive);
        }
        var tasks = new List<Task> { host.RetainProgressForTestAsync(TerminalAction(pair, 0, observed), Present) };
        await firstAppend.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var index = 1; index < 32; index++)
            tasks.Add(host.RetainProgressForTestAsync(TerminalAction(pair, index, observed), Present));
        var transientDelivered = 0;
        for (var index = 0; index < 200; index++)
            tasks.Add(host.RetainProgressForTestAsync(new(pair, "Checking", null, null, null,
                    Comparison: new("Hashing file contents", "file-" + index.ToString("D3") + ".txt", index, 0,
                        observed, observed, "Held-writer transient callback.")),
                progress => { transientDelivered++; return Present(progress); }));
        Require(host.ActivityLogPendingCount == 32 && delivered.Count == 0 && transientDelivered == 0 &&
                (await ReadEventsAsync(database)).Count == 0,
            "Only the 32 durable producer records queue behind the held real commit: 200 transient updates add no durable backlog and no committed row");
        releaseAppend.SetResult(true);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Require(!overlapped && transientDelivered == 200 && delivered.Count == 232 &&
            delivered.Take(32).SequenceEqual(Enumerable.Range(0, 32).Select(index => "operation-held-" + index)) &&
            delivered.Skip(32).All(value => value == "transient"),
            "Releasing the held commit drains every durable record first, in submission order, and then every transient callback, without overlap");
        var expected = batches.SelectMany(batch => batch).ToArray();
        var retained = await ReadEventsAsync(database);
        Require(retained.Count == 32 && expected.Length == 32 &&
                expected.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() == 32 &&
                expected.Select(item => item.OperationId).SequenceEqual(delivered.Take(32)) &&
                ExpandedPayloads(retained).SequenceEqual(expected.Select(CanonicalPayload)) &&
                !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "The durable prefix keeps one immutable identity per record and its exact admitted payload, and the atomic prefix dequeue removed exactly it");

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
            var callbacksBeforeRetry = delivered.Count;
            await host.RetainProgressForTestAsync(TerminalAction(pair, failureBoundary == "before" ? 100 : 101, observed));
            Require(injected && host.ActivityLogIncomplete && host.ActivityLogPendingCount == 1 &&
                    host.ActivityLogError?.Contains("fixture persistence failure " + failureBoundary) == true,
                "A " + failureBoundary + "-commit failure retains the exact pending record and reports its real reason");
            Require((await ReadEventsAsync(database)).Count == before.Count + (failureBoundary == "after" ? 1 : 0),
                "The injected " + failureBoundary + "-commit failure proves the actual append boundary rather than a substituted result");
            database.ActivityAppendBoundaryForTest = null;
            await host.RetryPendingActivityPersistenceAsync();
            var retried = await ReadEventsAsync(database);
            Require(retried.Count == before.Count + 1 && attempted is not null &&
                    retried[^1] == CanonicalPayload(attempted!) && delivered.Count == callbacksBeforeRetry &&
                    !host.ActivityLogIncomplete && host.ActivityLogError is null && host.ActivityLogPendingCount == 0,
                "Retrying after a " + failureBoundary + "-commit failure lands one record with the original identity and payload, re-runs no producer callback and clears the truthful log status");
        }
        await host.RetainProgressForTestAsync(new(pair, "Completed", null, null, null));
        var terminalRows = await LoadPairRowsAsync(database, pair.Id);
        Require(terminalRows[^1].Outcome == "Sync finished" && terminalRows.Count == 35 &&
                !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "The terminal run outcome still persists after both injected failures and their retries");
    }

    // The separate proof the policy requires: transient callbacks are delivered while a real append is held.
    private static async Task RunTransientDeliveryUnderHeldWriterAsync(string root)
    {
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "activity.db"));
        await database.InitializeAsync("compact-held-writer", "compact-profile", "owner-fixture", "{}");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "instance.lock"));
        using var client = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
            authorityDatabase: database);
        var pair = new SyncPair("pair-compact-writer", "Compact held writer fixture", root, 1, 1, "/fixture", "id:fixture");
        await using var admittedHistory = await AdmittedHistoryFixture.StartAsync(host, database, pair);
        var observed = DateTimeOffset.UtcNow;
        // A fixed clock keeps the half-second timer flush out of this proof, so the only writer activity is the
        // one deliberately held append below.
        var clock = Environment.TickCount64;
        host.ActivityProgressClockForTest = () => Volatile.Read(ref clock);
        // One successful real drain first, so the retained half-second flush boundary is already satisfied and
        // the record below is admitted to the queue without starting a write of its own.
        await host.RetainProgressForTestAsync(new(pair, "Pending", null, null, null));
        Require(host.ActivityLogPendingCount == 0 && !host.ActivityLogIncomplete,
            "The batch start record drains through the real append path before the held-writer proof begins");
        // I3: the Pending start above is a real committed row, so the held-writer proof compares against the
        // exact retained baseline instead of expecting an empty table.
        var committedBaseline = (await ReadEventsAsync(database)).Count;
        await host.RetainProgressForTestAsync(new(pair, "Running", null, null, null,
            Action: new SyncAuthorityLiveAction("held-record", "held.txt", "Download", "Completed",
                "Transient pre-registration.", observed, "File", "TXT", OperationKind: null,
                Phase: SyncActivityPhase.Running)));
        await host.RetainProgressForTestAsync(new(pair, "Running", null, null, null,
            Action: new SyncAuthorityLiveAction("held-record", "held.txt", "Download", "Completed",
                "Retained durable record under a held writer.", observed, "File", "TXT", OperationKind: null,
                Phase: SyncActivityPhase.Completed),
            Comparison: new("Comparing files", "held.txt", 1, 1, observed, observed, "Held durable record.")));
        Require(host.ActivityLogPendingCount == 1 && !host.ActivityLogIncomplete,
            "One durable record is admitted to the presented-batch queue while no transient callback has queued");
        var entered = Signal();
        var release = Signal();
        database.ActivityAppendBoundaryForTest = async (boundary, _) =>
        {
            if (boundary != "before") return;
            entered.TrySetResult(true);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        var heldFlush = Task.Run(() => host.RetryPendingActivityPersistenceAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var deliveredLive = new List<string>();
        for (var index = 0; index < 400; index++)
            await host.RetainProgressForTestAsync(new(pair, "Checking", null, null, null,
                    Comparison: new("Reading Local Inventory", "file-" + index.ToString("D3") + ".txt", index, 0,
                        observed, observed, "Transient callback under a held writer.")),
                progress => { deliveredLive.Add(progress.Comparison!.RelativePath); return Task.CompletedTask; });
        Require(deliveredLive.Count == 400 &&
                deliveredLive.SequenceEqual(Enumerable.Range(0, 400).Select(index => "file-" + index.ToString("D3") + ".txt")) &&
                host.ActivityLogPendingCount == 1 && !host.ActivityLogIncomplete &&
                (await ReadEventsAsync(database)).Count == committedBaseline,
            "All 400 transient callbacks are delivered live and in order while a real append is held, adding no pending event, no committed row and no gap");
        release.SetResult(true);
        await heldFlush.WaitAsync(TimeSpan.FromSeconds(30));
        database.ActivityAppendBoundaryForTest = null;
        var retained = await LoadPairRowsAsync(database, pair.Id);
        // The captured action time can precede the synthesized start event. Match durable identity and
        // content without imposing a timestamp order that the producer never promised.
        var completed = retained.SingleOrDefault(row => row.OperationId == "held-record");
        Require(retained.Count == 2 && retained.Count(row => row.Outcome == "Sync started") == 1 &&
                completed is { Outcome: "Download \u2014 Completed", RelativePath: "held.txt",
                    Reason: "Retained durable record under a held writer.", Phase: SyncActivityPhase.Completed } &&
                !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "Releasing the held append lands the admitted durable record with its exact reason, operation, phase and path, and leaves no pending event");
    }

    // DBP04: a real planner no-op scan is stable and retains nothing, while real conflict/delete/edit and
    // both-absent cases keep their safety mutations and can never be converted into a no-op.
    private static async Task RunPlannerNoOpAndSafetyAsync(string root)
    {
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var remote = new FakeSyncRemoteStore();
        var names = new[] { "same.txt", "edit-local.txt", "edit-remote.txt", "edit-both.txt",
            "gone-both.txt", "gone-local.txt" };
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(local, name), "matching content " + name);
            remote.Put(name, "matching content " + name);
        }
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
        var pair = new SyncPair("pair-compact-planner", "Compact planner fixture", local,
            identity.VolumeSerialNumber, identity.FileId, "/Compact", "id:compact");
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await database.InitializeAsync("compact-planner-install", "compact-planner-profile", "compact-planner", "{}");
        await database.RegisterPairAsync(pair.Id, 1);
        var admission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await database.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await database.RegisterRootBindingAsync("compact-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), local);
        await database.RegisterRootBindingAsync("compact-dropbox", pair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = "id:compact" }), pair.DropboxRoot);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, database,
            "compact-local", "compact-dropbox", _ => Task.CompletedTask);
        await observer.ObserveAsync("initial");
        await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(database).AdoptAsync(pair.Id);
        var planner = new SyncAuthorityReconciliationPlanner(database,
            new SyncAuthorityPlannerOptions(1, "compact-planner-profile", "compact-control", "compact-control"),
            onComparison: _ => Task.CompletedTask, admission: admission);
        var decisionsBefore = await CountDecisionsAsync(database.DatabasePath, null);
        var first = await planner.PlanAsync(pair.Id);
        var decisionsAfterFirst = await CountDecisionsAsync(database.DatabasePath, null);
        var second = await planner.PlanAsync(pair.Id);
        var decisionsAfterSecond = await CountDecisionsAsync(database.DatabasePath, null);
        Require(first.Count == names.Length && first.All(row => row.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "The real planner classifies a complete no-op scan of every retained path as unchanged");
        Require(second.Count == first.Count && Describe(second) == Describe(first),
            "A repeated complete no-op scan returns the identical complete outcome set");
        Require(decisionsAfterFirst == decisionsBefore && decisionsAfterSecond == decisionsBefore &&
                await CountDecisionsAsync(database.DatabasePath, ["unchanged", "skipped"]) == 0,
            "Two complete no-op scans add no durable decision row at all, so the unchanged and skipped row count stays constant at zero");
        Require(await CountDecisionsAsync(database.DatabasePath, ["operation", "conflict", "tombstone", "blocked"]) == 0,
            "A no-op scan retains no safety authority mutation and cannot be converted into one");

        await File.WriteAllTextAsync(Path.Combine(local, "edit-local.txt"), "changed locally only");
        await observer.ObserveAsync("manual");
        var edited = await planner.PlanAsync(pair.Id);
        Require(edited.Single(row => row.RelativePath == "edit-local.txt").Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                await CountDecisionsAsync(database.DatabasePath, ["operation"]) >= 1 &&
                await CountDecisionsAsync(database.DatabasePath, ["unchanged", "skipped"]) == 0,
            "A real one-sided edit is planned as an operation, is retained as durable authority and cannot be converted to a no-op");
        remote.Put("edit-remote.txt", "changed remotely only");
        await observer.ObserveAsync("manual");
        var remoteEdited = await planner.PlanAsync(pair.Id);
        Require(remoteEdited.Single(row => row.RelativePath == "edit-remote.txt").Kind == SyncAuthorityPlannerOutcomeKind.Operation,
            "A real remote-only edit is planned as an operation from the same three-view authority");
        await File.WriteAllTextAsync(Path.Combine(local, "edit-both.txt"), "divergent local body");
        remote.Put("edit-both.txt", "divergent remote body");
        await observer.ObserveAsync("manual");
        var conflicted = await planner.PlanAsync(pair.Id);
        Require(conflicted.Single(row => row.RelativePath == "edit-both.txt").Kind == SyncAuthorityPlannerOutcomeKind.Conflict &&
                await CountDecisionsAsync(database.DatabasePath, ["conflict"]) >= 1,
            "A real divergent two-sided edit keeps its retained conflict authority and cannot be converted to a no-op");
        File.Delete(Path.Combine(local, "gone-local.txt"));
        await observer.ObserveAsync("manual");
        var deleted = await planner.PlanAsync(pair.Id);
        var localAbsence = deleted.Single(row => row.RelativePath == "gone-local.txt");
        Require(localAbsence.Kind == SyncAuthorityPlannerOutcomeKind.Tombstone &&
                await CountDecisionsAsync(database.DatabasePath, ["tombstone"]) >= 1,
            "A real one-sided deletion keeps its retained tombstone authority, its absence reason and its decision row");
        File.Delete(Path.Combine(local, "gone-both.txt"));
        remote.Delete("gone-both.txt");
        await observer.ObserveAsync("manual");
        var bothAbsent = await planner.PlanAsync(pair.Id);
        var confirmation = bothAbsent.Single(row => row.RelativePath == "gone-both.txt");
        Require(confirmation.Kind == SyncAuthorityPlannerOutcomeKind.Tombstone && confirmation.IsBothAbsentConfirmation &&
                confirmation.Reason.Contains("Both complete observations prove absence") &&
                await CountDecisionsAsync(database.DatabasePath, ["tombstone"]) >= 2,
            "A real both-sides absence still returns its complete both-absence confirmation and keeps its retained tombstone authority");
        Require(await CountDecisionsAsync(database.DatabasePath, ["unchanged", "skipped"]) == 0,
            "No unchanged or skipped decision row exists after the whole real mutation sequence");
        Require(remote.UploadCount == 0 && remote.DownloadCount == 0 && remote.DeleteCount == 0 &&
                remote.ArchiveCount == 0 && remote.MoveCount == 0,
            "Real planning authority authorised no provider effect in any of these cases");
    }

    // DBP04: a real direction-skipped path returns its complete skipped outcome without a durable row.
    private static async Task RunPlannerSkipDirectionAsync(string root)
    {
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var remote = new FakeSyncRemoteStore();
        await File.WriteAllTextAsync(Path.Combine(local, "shared.txt"), "shared body");
        remote.Put("shared.txt", "shared body");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
        var pair = new SyncPair("pair-compact-skip", "Compact skip fixture", local,
            identity.VolumeSerialNumber, identity.FileId, "/CompactSkip", "id:compact-skip");
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await database.InitializeAsync("compact-skip-install", "compact-skip-profile", "compact-skip", "{}");
        await database.RegisterPairAsync(pair.Id, 1);
        var admission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await database.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await database.RegisterRootBindingAsync("compact-skip-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), local);
        await database.RegisterRootBindingAsync("compact-skip-dropbox", pair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = "id:compact-skip" }), pair.DropboxRoot);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, database,
            "compact-skip-local", "compact-skip-dropbox", _ => Task.CompletedTask);
        await observer.ObserveAsync("initial");
        await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(database).AdoptAsync(pair.Id);
        remote.Put("remote-only.txt", "remote only body");
        await observer.ObserveAsync("manual");
        var uploadOnly = new SyncAuthorityReconciliationPlanner(database,
            new SyncAuthorityPlannerOptions(1, "compact-skip-profile", "compact-control", "compact-control",
                Direction: SyncMode.UploadOnly),
            onComparison: _ => Task.CompletedTask, admission: admission);
        var outcomes = await uploadOnly.PlanAsync(pair.Id);
        Require(outcomes.Single(row => row.RelativePath == "remote-only.txt").Kind == SyncAuthorityPlannerOutcomeKind.Skipped &&
                outcomes.Single(row => row.RelativePath == "shared.txt").Kind == SyncAuthorityPlannerOutcomeKind.Unchanged &&
                await CountDecisionsAsync(database.DatabasePath, ["skipped", "unchanged"]) == 0 &&
                await CountDecisionsAsync(database.DatabasePath, ["operation"]) == 0,
            "A real direction-skipped path still returns its complete skipped outcome and an unchanged path its unchanged outcome, while neither writes a decision row or an effect operation");
    }

    // Existing historic rows - the previous producer-format presentation row and an informational unchanged
    // decision row - stay readable and unchanged now that no new changed-state row of that kind is produced.
    private static async Task RunHistoricRowReadabilityAsync(string root)
    {
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "historic.db");
        var database = new SyncAuthorityDatabase(databasePath);
        await database.InitializeAsync("compact-historic", "compact-historic-profile", "owner-fixture", "{}");
        await database.RegisterPairAsync("pair-historic", 1);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(root)) identity = lease.Identity;
        await database.RegisterRootBindingAsync("historic-local", "pair-historic", "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), root);
        await database.RegisterRootBindingAsync("historic-dropbox", "pair-historic", "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = "id:historic" }), "/Historic");
        await LegacyHistoryFixture.SeedAsync(database, new SyncActivityEvent("evt-historic-presentation",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), [SyncActivityCategories.Sync], "Information",
            "Folder pair", "Sync started", "Retained historic message.", "account-historic", "pair-historic",
            "run-historic"));
        await InsertHistoricUnchangedDecisionAsync(databasePath, "decision-historic-unchanged", "pair-historic",
            JsonSerializer.Serialize(new { path = "historic.txt", kind = "Unchanged",
                reason = "Retained historic no-op reason.", isBothAbsentConfirmation = false }));
        var rows = await database.LoadActivityProjectionPageAsync(50, pairId: "pair-historic",
            query: new SyncActivityQuery([SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics,
                    SyncActivityCategories.Files], OldestFirst: true));
        Require(rows.Count == 2 &&
                rows.Any(row => row.EventId == "history:evt-historic-presentation" && row.Outcome == "Sync started" &&
                    row.Reason == "Retained historic message." && row.RunId == "run-historic" &&
                    row.ConnectionId == "account-historic") &&
                rows.Any(row => row.EventId == "decision:decision-historic-unchanged" && row.Outcome == "Identical" &&
                    row.RelativePath == "historic.txt" && row.Reason == "Retained historic no-op reason."),
            "An existing historic presentation row and an existing informational unchanged decision row remain readable and unaltered");
    }

    private static SyncAuthorityBatchProgress TerminalAction(SyncPair pair, int index, DateTimeOffset observed) =>
        new(pair, "Running", null, null, null,
            Action: new SyncAuthorityLiveAction("operation-held-" + index, "folder/held-" + index.ToString("D3") + ".bin",
                "Download", "Completed", "Completed the retained held-writer action " + index + ".",
                observed.AddMilliseconds(index), "File", "BIN", OperationKind: null,
                Phase: SyncActivityPhase.Completed,
                Progress: new SyncActivityProgress(index, 256, "bytes", index * 10)));

    private static async Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadPairRowsAsync(
        SyncAuthorityDatabase database, string pairId) =>
        await database.LoadActivityProjectionPageAsync(200, pairId: pairId,
            query: new SyncActivityQuery([SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics,
                    SyncActivityCategories.Files], OldestFirst: true));

    private static string Describe(IReadOnlyList<SyncAuthorityPlannerOutcome> outcomes) =>
        string.Join(";", outcomes.OrderBy(row => row.RelativePath, StringComparer.Ordinal)
            .Select(row => row.RelativePath + "|" + row.Kind + "|" + row.Reason + "|" + row.IsBothAbsentConfirmation));

    private static async Task<int> CountDecisionsAsync(string databasePath, IReadOnlyList<string>? kinds)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = kinds is null
            ? "SELECT count(*) FROM reconciliation_decision;"
            : "SELECT count(*) FROM reconciliation_decision WHERE decision_kind IN (" +
              string.Join(",", kinds.Select((_, index) => "$kind" + index)) + ");";
        for (var index = 0; kinds is not null && index < kinds.Count; index++)
            command.Parameters.AddWithValue("$kind" + index, kinds[index]);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task InsertHistoricUnchangedDecisionAsync(string databasePath, string decisionId,
        string pairId, string decisionJson)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reconciliation_decision(reconciliation_decision_id,pair_id," +
            "decision_kind,decision_json,decided_utc) VALUES($id,$pair,'unchanged',$json,$utc);";
        command.Parameters.AddWithValue("$id", decisionId);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$json", decisionJson);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadEventsAsync(SyncAuthorityDatabase database)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.detail_json,h.activity_context_key,c.context_json FROM history_event h
            LEFT JOIN activity_context c ON c.context_key=h.activity_context_key
            WHERE h.event_kind='presentation' ORDER BY h.rowid;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
        {
            var payload = System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(0))!.AsObject();
            if (!reader.IsDBNull(1))
            {
                if (reader.IsDBNull(2)) throw new InvalidDataException("A retained event lost its detached context.");
                payload["EndpointContext"] = System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(2));
            }
            result.Add(JsonSerializer.Serialize(JsonSerializer.Deserialize<SyncActivityEvent>(payload.ToJsonString())));
        }
        return result;
    }

    private static string CanonicalPayload(SyncActivityEvent item) => JsonSerializer.Serialize(item with
    {
        OccurredUtc = item.OccurredUtc.ToUniversalTime(),
        Action = SyncActivityText.Safe(item.Action),
        Message = SyncActivityText.Safe(item.Message)
    });

    // The retained store keeps one full endpoint context per append transaction and an internal one-hop
    // reference for the other rows of that same transaction, so the logical record is expanded before it is
    // compared with the admitted payload.
    private static IReadOnlyList<string> ExpandedPayloads(IReadOnlyList<string> stored)
    {
        var byId = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.Ordinal);
        var items = new List<System.Text.Json.Nodes.JsonObject>();
        foreach (var payload in stored)
        {
            var item = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
            items.Add(item);
            byId[item["EventId"]!.GetValue<string>()] = item;
        }
        return items.Select(item =>
        {
            if (item["EndpointContext"] is System.Text.Json.Nodes.JsonObject reference &&
                reference["EndpointContextEventId"] is System.Text.Json.Nodes.JsonValue value &&
                value.TryGetValue<string>(out var anchor) && anchor is { Length: > 0 } &&
                byId.TryGetValue(anchor, out var anchorPayload) && anchorPayload["EndpointContext"] is not null)
            {
                item["EndpointContext"] = anchorPayload["EndpointContext"]!.DeepClone();
            }
            return JsonSerializer.Serialize(JsonSerializer.Deserialize<SyncActivityEvent>(item.ToJsonString()));
        }).ToList();
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }
}

