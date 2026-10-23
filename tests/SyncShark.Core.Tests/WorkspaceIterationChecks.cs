using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

internal static class WorkspaceIterationChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
        var remote = new FakeSyncRemoteStore();
        for (var i = 0; i < 12; i++)
        {
            var path = $"item-{i:00}.txt";
            await File.WriteAllTextAsync(Path.Combine(local, path), "matching content " + i);
            remote.Put(path, "matching content " + i);
        }
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
        var pair = new SyncPair("pair-workspace", "Workspace check", local, identity.VolumeSerialNumber,
            identity.FileId, "/Workspace", "id:workspace");
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("workspace-check-installation", "workspace-check-profile", "workspace-check", "{}");
        await db.RegisterPairAsync(pair.Id, 1);
        await db.RegisterRootBindingAsync("workspace-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), local);
        await db.RegisterRootBindingAsync("workspace-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:workspace\"}", pair.DropboxRoot);
        var inventory = new List<SyncComparisonProgress>();
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "workspace-local", "workspace-dropbox",
            value => { inventory.Add(value); return Task.CompletedTask; });
        await observer.ObserveAsync("initial"); await observer.ObserveAsync("initial");
        Console.WriteLine("INVENTORY STAGES " + JsonSerializer.Serialize(inventory
            .Select(value => new { value.Phase, value.ProgressUnit, value.TotalChecks }).Distinct()));
        Require(inventory.Any(value => value.Phase == "Reading Local Inventory" && value.RelativePath == "item-11.txt") &&
            inventory.Any(value => value.Phase == "Reading Right \u2014 Dropbox Inventory" && value.RelativePath == pair.DropboxRoot) &&
            inventory.Any(value => value.Phase == "Saving Folder Inventories" && value.CompletedChecks == 24 &&
                value.Detail == "Committing the completed inventory snapshots to Sync Shark's database."),
            "Real inventory reports the actual local file, ordered provider endpoint and completed inventory commit stage");
        var enumeration = inventory.Where(value => value.Phase is "Reading Local Inventory" or "Reading Right \u2014 Dropbox Inventory").ToArray();
        Require(enumeration.Length > 0 && enumeration.All(value => value.TotalChecks == 0),
            "Discovery never invents an unknown enumeration total or percentage");
        var committedMetadata = inventory.Where(value => value.Phase == "Saving Folder Inventories" &&
            value.ProgressUnit == "records saved" && value.Detail.StartsWith("Saved ", StringComparison.Ordinal)).ToArray();
        Require(committedMetadata.Length == 48 && committedMetadata.All(value => value.TotalChecks == 12 &&
                value.CompletedChecks is >= 1 and <= 12 && value.RelativePath.StartsWith("item-", StringComparison.Ordinal)) &&
                committedMetadata.Count(value => value.CompletedChecks == 12) == 4,
            "Both twelve-file inventories report every committed file and measured completion on both scans");
        var scannedPaths = new List<string>();
        using (var scanLease = PilotDirectoryLease.Open(local))
            await new PilotLocalScanner().ScanAsync(scanLease, onInventory: (path, _) =>
                { scannedPaths.Add(path); return Task.CompletedTask; });
        Require(scannedPaths.Contains("item-11.txt"), "Scanner progress reaches actual file paths, not just the root");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        var progress = new List<SyncComparisonProgress>();
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var planner = new SyncAuthorityReconciliationPlanner(db,
            new SyncAuthorityPlannerOptions(1, "workspace-check-profile", "workspace-control", "workspace-control"),
            onComparison: value => { progress.Add(value); return Task.CompletedTask; }, admission: admission);
        var timer = Stopwatch.StartNew();
        var unchanged = await planner.PlanAsync(pair.Id);
        timer.Stop();
        Require(unchanged.Count == 12 && unchanged.All(row => row.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged), "Real planner no-change result");
        Require(await CountRoutineDecisionsAsync(db.DatabasePath) == 0,
            "A no-change plan records no unchanged or skipped decision row");
        Require((await ReadDecisions(db.DatabasePath)).Count == 0,
            "A no-change plan records no other durable decision row");
        await planner.PlanAsync(pair.Id);
        Require(await CountRoutineDecisionsAsync(db.DatabasePath) == 0,
            "Exact observation replay records no unchanged or skipped decision row");
        await File.WriteAllTextAsync(Path.Combine(local, "new-local.txt"), "new material input");
        await observer.ObserveAsync("manual");
        var changed = await planner.PlanAsync(pair.Id);
        Require(changed.Single(row => row.RelativePath == "new-local.txt").Kind == SyncAuthorityPlannerOutcomeKind.Operation,
            "Material change still produces a real plan");
        Require(changed.Count(row => row.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged) == 12, "Mixed plan retains unchanged decisions");
        Require(await CountRoutineDecisionsAsync(db.DatabasePath) == 0,
            "A mixed plan still records no unchanged or skipped decision row");
        var retained = await ReadDecisions(db.DatabasePath);
        Require(retained.Count >= 1, "The material operation produced a real durable decision row");
        var atomicBaseline = retained.Count;
        var fresh = retained[0] with { DecisionId = "workspace-atomic-valid" };
        await ExpectFailure(() => db.RecordReconciliationDecisionsAsync([fresh,
            retained[0] with { DecisionId = "workspace-invalid-json", Json = "not-json" }]));
        Require((await ReadDecisions(db.DatabasePath)).Count == atomicBaseline, "Invalid input cannot partially commit");
        await ExpectFailure(() => db.RecordReconciliationDecisionsAsync([fresh,
            retained[0] with { DecisionId = "workspace-invalid-pair", PairId = "missing-pair" }]));
        Require((await ReadDecisions(db.DatabasePath)).Count == atomicBaseline, "Database rejection rolls back the entire batch");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ExpectFailure(() => db.RecordReconciliationDecisionsAsync([fresh], cancellation.Token));
        Require((await ReadDecisions(db.DatabasePath)).Count == atomicBaseline, "Cancellation leaves history unchanged");
        var all = await db.LoadActivityProjectionPageAsync(100, 0, null, "All actions");
        var changes = await db.LoadActivityProjectionPageAsync(100, 0, null, "Changes");
        Require(!all.Any(row => row.Outcome == "Unchanged" || row.Outcome == "Identical"),
            "Approved display policy no longer produces routine unchanged or identical history");
        Require(changes.Count > 0 && changes.All(row => row.Outcome is not ("Unchanged" or "Identical")), "Recent changes filters routine checks before pagination");
        Require(changes.Any(row => row.RelativePath == "new-local.txt"), "Recent changes retains planned material work");
        Require(progress.Any(value=>value.RelativePath.Length>0) && progress.Last().Phase=="Saving Comparison Results" &&
            progress.Last().CompletedChecks==progress.Last().TotalChecks, "Real planner reports exact path and bounded completed checks");
        var size = changes.First(row=>row.RelativePath=="new-local.txt");
        Require(size.SizeBytes==Encoding.UTF8.GetByteCount("new material input") && size.LocalRoot==local,
            "Activity uses recorded size and operation-bound historical root");
        var queue = await db.LoadQueueProjectionPageAsync(100, pairId:pair.Id,itemType:"TXT");
        Require(queue.Any(row=>row.RelativePath=="new-local.txt" && row.ItemKind=="File" && row.FileType=="TXT"),"Pending kind and type use recorded object metadata");
        Require((await db.LoadQueueProjectionPageAsync(100,pairId:"another-pair")).Count==0,"Pending pair filter runs before pagination");
        await db.CreateCommandRequestAsync("workspace-run", "run_pair", pair.Id, null, null,
            JsonSerializer.Serialize(new { pairIds = new[] { pair.Id }, trigger = "manual" }));
        await db.TransitionCommandRequestAsync("workspace-run", "accepted");
        await db.TransitionCommandRequestAsync("workspace-run", "executing");
        await db.TransitionCommandRequestAsync("workspace-run", "completed", "{\"completedPairs\":1,\"failedPairs\":0,\"Stopped\":false,\"pairs\":[{\"uploaded\":1,\"downloaded\":0,\"unchanged\":12,\"conflicts\":0,\"blocked\":0}]}");
        var reopened = new SyncAuthorityDatabase(db.DatabasePath);
        var runs = await reopened.LoadRunHistoryAsync();
        Require(runs.Count == 1 && runs[0].CompletedUtc is not null && runs[0].ResultJson!.Contains("uploaded"), "Run history and counts survive a new database instance");
        var successful = await db.LoadLastSuccessfulSyncUtcAsync();
        Require(successful is not null && await db.LoadLastSuccessfulSyncUtcAsync(allowedPairIds: [pair.Id]) == successful &&
            await db.LoadLastSuccessfulSyncUtcAsync(allowedPairIds: ["another-account-pair"]) is null &&
            await db.LoadLastSuccessfulSyncUtcAsync(allowedPairIds: []) is null,
            "Last successful sync is filtered to the exact account's pairs, including an empty account");
        await db.CreateCommandRequestAsync("workspace-stopped", "run_pair", pair.Id, null, null, "{}");
        await db.TransitionCommandRequestAsync("workspace-stopped", "accepted");
        await db.TransitionCommandRequestAsync("workspace-stopped", "executing");
        await db.TransitionCommandRequestAsync("workspace-stopped", "completed", "{\"completedPairs\":1,\"failedPairs\":0,\"Stopped\":true}");
        Require(successful is not null && await db.LoadLastSuccessfulSyncUtcAsync() == successful, "A stopped run cannot advance last-successful-sync state");
        await db.VerifyHealthAsync();
        var fast=Stopwatch.StartNew(); for(var i=0;i<20;i++) await db.LoadPendingQueueCountAsync(); fast.Stop();
        var full=Stopwatch.StartNew(); for(var i=0;i<20;i++) await new SyncAuthorityDatabase(db.DatabasePath).LoadPendingQueueCountAsync(); full.Stop();
        Console.WriteLine($"TIMING 20 existing-instance reads {fast.ElapsedMilliseconds}ms; 20 first-open schema-guarded reads {full.ElapsedMilliseconds}ms");
        var startupHealthChecks = 0;
        SyncAuthorityDatabase.HealthCheckCompletedForTest = path =>
        {
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(db.DatabasePath), StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref startupHealthChecks);
        };
        try
        {
            var startupDatabase = new SyncAuthorityDatabase(db.DatabasePath);
            var installationId = await startupDatabase.LoadInstallationIdAsync();
            await startupDatabase.InitializeAsync(installationId!, "workspace-check-profile", "workspace-check", "{}");
            await startupDatabase.LoadPairSetAsync();
            Require(startupHealthChecks == 0,
                "existing-profile identity, initialization and pair loading do not scan retained data");
            await startupDatabase.VerifyHealthAsync();
            Require(startupHealthChecks == 1,
                "explicit diagnostics still performs a fresh full integrity check after startup");
            try
            {
                await startupDatabase.InitializeAsync("different-installation", "workspace-check-profile", "workspace-check", "{}");
                throw new InvalidDataException("Startup accepted a different retained installation identity.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("different installation identity", StringComparison.Ordinal))
            { Console.WriteLine("PASS initialization still rejects a different retained installation identity"); }
            await startupDatabase.LoadPairSetAsync();
            Require(startupHealthChecks == 1,
                "failed identity initialization does not turn the next ordinary read into full diagnostics");
        }
        finally { SyncAuthorityDatabase.HealthCheckCompletedForTest = null; }
        await using (var corrupt = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=db.DatabasePath, ForeignKeys=false, Pooling=false }.ToString()))
        {
            await corrupt.OpenAsync(); await using var command=corrupt.CreateCommand();
            command.CommandText="UPDATE root_binding SET pair_id='missing-pair' WHERE root_binding_id='workspace-local';"; await command.ExecuteNonQueryAsync();
        }
        Require(!string.IsNullOrWhiteSpace(await db.LoadInstallationIdAsync()),
            "ordinary identity loading does not inspect unrelated retained foreign keys");
        await db.LoadPendingQueueCountAsync();
        try { await db.VerifyHealthAsync(); throw new InvalidOperationException("Diagnostics accepted a corrupt retained foreign key"); }
        catch(InvalidDataException) { Console.WriteLine("PASS explicit diagnostics rejects corrupted retained relationships"); }
        await db.LoadPendingQueueCountAsync();
        Console.WriteLine("PASS failed explicit diagnostics is not a cached verdict or an implicit full-scan trigger");
        Require(remote.UploadCount == 0 && remote.DownloadCount == 0 && remote.ArchiveCount == 0, "Planning proof made zero provider effects");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", unchanged_items = 12, planner_ms = timer.ElapsedMilliseconds, provider_effects = 0 }));
    }

    private static async Task<List<SyncAuthorityDecisionRecord>> ReadDecisions(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT reconciliation_decision_id,pair_id,object_id,local_scan_generation_id,dropbox_scan_generation_id,synchronization_point_id,decision_kind,decision_json FROM reconciliation_decision WHERE decision_kind NOT IN ('unchanged','skipped');";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<SyncAuthorityDecisionRecord>();
        while (await reader.ReadAsync()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    private static async Task<int> CountRoutineDecisionsAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reconciliation_decision WHERE decision_kind IN ('unchanged','skipped');";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or SqliteException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected safe rejection did not occur.");
    }
    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
}
