using System.Text.Json;
using Massdex.SyncShark;

internal static class CompletedCopyMoveChecks
{
    internal static async Task RunAsync(string root, bool normalizedProofOnly = false, bool successorOnly = false, bool legacySuccessorOnly = false, bool legacySchemaOnly = false)
    {
        Directory.CreateDirectory(root);
        if (normalizedProofOnly) await Migration13Async(root);
        foreach (var scenario in successorOnly || legacySuccessorOnly ? new[] { "successor" } : normalizedProofOnly ? new[] { "compact-success", "legacy-materialization", "baseline-invalidation", "repeated-moves", "observed-absence" } : new[] { "success", "old-edited", "copy-edited", "cloud-edited", "identity-changed",
            "interrupted", "interrupted-not-applied", "interrupted-ambiguous", "changed-after-plan", "recreated-after-completion",
            "cancel-owner-native-read" })
        {
            var fixture = Path.Combine(root, scenario);
            var local = Path.Combine(fixture, "local");
            Directory.CreateDirectory(local);
            var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            remote.Put("old.txt", "unchanged accepted bytes");
            if (scenario == "cancel-owner-native-read")
                for (var index = 0; index < 64; index++)
                    remote.Put($"unchanged-{index}.txt", "native read input");
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            var state = instance.StateAuthority;
            var connections = new DropboxConnectionStore(state);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:copy-move", "move-root", "move-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            await connections.SaveNewAsync(binding);
            var database = new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db"));
            using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
            using var host = new SyncAuthorityRuntimeHost(state, http, new SyncPairStore(state),
                new SyncRuntimeStateStore(state), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
                database, (_, _) => remote);
            await host.LoadPairsAsync();
            using var rootLease = PilotDirectoryLease.Open(local);
            var pair = SyncPair.FromPilot(new PilotSettings(local, rootLease.Identity.VolumeSerialNumber,
                rootLease.Identity.FileId, "/CopyMove", "id:copy-move"), "Copied move", binding.RootNamespaceId)
                with { ConnectionId = binding.ConnectionId, InitialReviewApproved = false, PropagateDeletions = false };
            await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(pair, select: true);
            var operations = new FirstSyncEffectChecks.FixtureOperations(host, state);
            using var output = new StringWriter(); using var error = new StringWriter();
            var scanExit = await SyncCli.ExecuteAsync(["--cli", "scan", "--pair", pair.Id, "--yes", "--json"],
                output, error, () => operations);
            Require(scanExit == 0, "initial real scan failed: " + error);
            using var scan = JsonDocument.Parse(output.ToString());
            var choices = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = scan.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = scan.RootElement.GetProperty("data").GetProperty("dropboxScanGenerationId").GetString()
            });
            output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
            var applied = await SyncCli.ExecuteAsync(["--cli", "first-sync", "apply", "--pair", pair.Id,
                "--choices", choices, "--allow-recommended", "--yes", "--json"], output, error, () => operations);
            Require(applied == 0 && File.ReadAllText(Path.Combine(local, "old.txt")) == "unchanged accepted bytes",
                "initial real download failed: " + error);
            remote.Move("old.txt", "moved.txt");
            pair = (await host.LoadPairsAsync()).Pairs.Single(p => p.Id == pair.Id);
            var localRoot = await database.LoadActiveRootBindingIdAsync(pair.Id, "local");
            var remoteRoot = await database.LoadActiveRootBindingIdAsync(pair.Id, "dropbox");
            await new SyncAuthorityObservationCoordinator(pair, remote, database, localRoot, remoteRoot)
                .ObserveAsync("manual");
            var options = new SyncAuthorityPlannerOptions(pair.PolicyRevision, host.CapabilityProfileIdForTest,
                pair.ControlNamespace!.NamespaceId, pair.ControlNamespace.NamespaceId);
            // Reproduce the historical completed-copy lineage through the real
            // planner and executor, not fabricated receipts or SQL status edits.
            var admission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await database.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            await database.RegisterOperationGroupAsync("historical-copied-move", "compound", "{}");
            var plan = await new SyncAuthorityReconciliationPlanner(database, options,
                effectPathFilter: new HashSet<string> { "moved.txt" }, operationGroupId: "historical-copied-move",
                expectedOperationKinds: new Dictionary<string, SyncAuthorityOperationKind>
                    { ["moved.txt"] = SyncAuthorityOperationKind.CreateLocal }, admission: admission).PlanAsync(pair.Id);
            var copyOperation = plan.Single(p => p.RelativePath == "moved.txt" && p.OperationId is not null);
            await database.MarkOperationGroupActiveAsync("historical-copied-move");
            var committedWithoutCompletion = false;
            SyncAuthorityCreateLocalExecutor.PhaseForTest = phase =>
            {
                if (phase != "commit-before-cleanup") return;
                Require(database.LoadCompletedCopyMovesAsync(pair.Id, default).GetAwaiter().GetResult().Count == 0,
                    "committed copy state granted completed-copy authority before fenced operation completion");
                committedWithoutCompletion = true;
            };
            try
            {
                await new SyncAuthorityCreateLocalExecutor(pair, remote, database)
                    .ExecuteAsync(copyOperation.OperationId!, "fixture-copy");
            }
            finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
            Require(committedWithoutCompletion, "actual copy execution did not exercise its pre-completion boundary");
            await database.CompleteOperationGroupAsync("historical-copied-move");
            var proof = (await database.LoadCompletedCopyMovesAsync(pair.Id, default)).Single();
            Require(await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                "the scoped production guard rejects its exact retained candidate");
            Require(await database.IsNormalizedCompletedCopyMoveAsync(pair.Id, proof, default),
                "a real completed known-success copy did not capture its exact normalized current proof");
            using (var inspect = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = database.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString()))
            {
                await inspect.OpenAsync();
                using var count = inspect.CreateCommand();
                count.CommandText = """
                    SELECT count(*) FROM current_copy_completion c
                    JOIN operation o ON o.operation_id=c.copy_operation_id AND o.status='completed' AND o.kind='create_local'
                    JOIN effect_receipt r ON r.effect_receipt_id=c.receipt_id AND r.certainty='known_success'
                    WHERE c.pair_id=$pair AND c.object_id=$object AND c.coverage_verified=1
                        AND json_extract(c.receipt_evidence_json,'$.digest')=r.evidence_digest
                        AND json_extract(c.receipt_evidence_json,'$.operationId')=o.operation_id;
                    """;
                count.Parameters.AddWithValue("$pair", pair.Id); count.Parameters.AddWithValue("$object", proof.ObjectId);
                Require(Convert.ToInt64(await count.ExecuteScalarAsync()) == 1,
                    "captured copy proof does not match its real terminal operation and exact receipt metadata");
                if (scenario == "compact-success")
                {
                    count.CommandText = "EXPLAIN QUERY PLAN " + SyncAuthorityDatabase.CurrentCopyCandidateQuery(true);
                    count.Parameters.AddWithValue("$unverified", 0);
                    count.Parameters.AddWithValue("$prior", proof.PriorPointId);
                    count.Parameters.AddWithValue("$current", proof.CurrentPointId);
                    count.Parameters.AddWithValue("$copy", proof.CopyOperationId);
                    var queryPlan = new List<string>();
                    using (var rows = await count.ExecuteReaderAsync())
                        while (await rows.ReadAsync()) queryPlan.Add(rows.GetString(3));
                    Require(queryPlan.Any(line => line.Contains(
                                "p USING INDEX sqlite_autoindex_copy_predecessor_1 (pair_id=? AND object_id=?", StringComparison.Ordinal)) &&
                            queryPlan.Any(line => line.Contains(
                                "c USING INDEX sqlite_autoindex_current_copy_completion_1 (pair_id=? AND object_id=?", StringComparison.Ordinal)),
                        "an exact copy guard scans a whole pair/root instead of exact pair/object primary keys");
                    Console.WriteLine("COPY EXACT QUERY PLAN " + JsonSerializer.Serialize(queryPlan));
                }
                if (scenario == "legacy-materialization")
                {
                    // Input construction only: this isolated fixture represents a
                    // predecessor with no compact projections. Restore the exact
                    // current guard in the same transaction before production reads.
                    using var resetTransaction = inspect.BeginTransaction();
                    using var reset = inspect.CreateCommand();
                    reset.Transaction = resetTransaction;
                    reset.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='both_absent_empty_copy_delete_guard';";
                    var guard = (string?)await reset.ExecuteScalarAsync()
                        ?? throw new InvalidDataException("The current projection deletion guard is missing.");
                    reset.CommandText = "DROP TRIGGER both_absent_empty_copy_delete_guard; " +
                        "DELETE FROM copy_predecessor; DELETE FROM current_copy_completion; " + guard + ";";
                    await reset.ExecuteNonQueryAsync();
                    reset.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='both_absent_empty_copy_delete_guard';";
                    Require((string?)await reset.ExecuteScalarAsync()==guard,
                        "legacy fixture construction did not restore the exact production deletion guard");
                    resetTransaction.Commit();
                }
            }
            if(legacySuccessorOnly)
            {
                if(!legacySchemaOnly)
                    await EssentialSuccessorChecks.RunLegacyCopySliceAsync(database.DatabasePath,
                        Path.Combine(fixture,"essential-legacy-copy"),pair.Id,proof);
                await EssentialSuccessorChecks.RunLegacyCopySliceAsync(database.DatabasePath,
                    Path.Combine(fixture,"essential-legacy7-copy"),pair.Id,proof,legacySchema:true);
                continue;
            }
            if(successorOnly)
            {
                await EssentialSuccessorChecks.RunNormalizedCopySliceAsync(database.DatabasePath,
                    Path.Combine(fixture,"essential-copy"),pair.Id,proof);
                continue;
            }
            if (scenario == "legacy-materialization")
            {
                Require((await database.LoadCompletedCopyMovesAsync(pair.Id, default)).SequenceEqual([proof]),
                    "the explicit legacy projection changed before normalization");
                Require(await database.MaterializeCurrentCopyProofAsync(pair.Id, proof.ObjectId, default) &&
                    (await database.LoadCompletedCopyMovesAsync(pair.Id, default)).SequenceEqual([proof]) &&
                    await database.IsNormalizedCompletedCopyMoveAsync(pair.Id, proof, default),
                    "bounded legacy materialization failed complete 14-field equality");
                Require(await database.MaterializeCurrentCopyProofAsync(pair.Id, proof.ObjectId, default),
                    "verified per-object materialization was not idempotent");
            }
            if (scenario == "repeated-moves")
            {
                remote.Move("moved.txt", "third.txt");
                await new SyncAuthorityObservationCoordinator(pair, remote, database, localRoot, remoteRoot)
                    .ObserveAsync("manual");
                await database.RegisterOperationGroupAsync("third-copied-move", "compound", "{}");
                var thirdPlan = await new SyncAuthorityReconciliationPlanner(database, options,
                    effectPathFilter: new HashSet<string> { "third.txt" }, operationGroupId: "third-copied-move",
                    expectedOperationKinds: new Dictionary<string, SyncAuthorityOperationKind>
                        { ["third.txt"] = SyncAuthorityOperationKind.CreateLocal }, admission: admission).PlanAsync(pair.Id);
                var thirdCopy = thirdPlan.Single(p => p.RelativePath == "third.txt" && p.OperationId is not null);
                Require((await database.LoadCompletedCopyMovesAsync(pair.Id, default)).SequenceEqual([proof]),
                    "registering an uncompleted third copy replaced the prior completed authority");
                await database.MarkOperationGroupActiveAsync("third-copied-move");
                await new SyncAuthorityCreateLocalExecutor(pair, remote, database)
                    .ExecuteAsync(thirdCopy.OperationId!, "fixture-third-copy");
                await database.CompleteOperationGroupAsync("third-copied-move");
                var candidates = await database.LoadCompletedCopyMovesAsync(pair.Id, default);
                Require(candidates.Count == 2 && candidates.Select(x => x.OldPath).Order()
                        .SequenceEqual(new[] { "moved.txt", "old.txt" }) &&
                        candidates.All(x => x.NewPath == "third.txt" && x.CopyOperationId == thirdCopy.OperationId),
                    "A-to-B-to-C did not retain both old memberships against the exact latest completion");
                await using (var inspection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database.DatabasePath))
                {
                    await inspection.OpenAsync();
                    using var count = inspection.CreateCommand();
                    count.CommandText = "SELECT (SELECT count(*) FROM current_copy_completion)+(SELECT count(*) FROM copy_predecessor);";
                    Require(Convert.ToInt64(await count.ExecuteScalarAsync()) == 3,
                        "repeated moves retained historical proof cohorts instead of one current plus two predecessors");
                }
                foreach (var candidate in candidates)
                {
                    var retirement = await SyncAuthorityCompletedCopyMove.PlanAsync(pair, remote, database, options,
                        candidate, admission, default);
                    await new SyncAuthorityDeleteLocalExecutor(pair, database, remote)
                        .ExecuteAsync(retirement, "repeated-copy-retirement");
                }
                Require(!File.Exists(Path.Combine(local, "old.txt")) && !File.Exists(Path.Combine(local, "moved.txt")) &&
                        File.ReadAllText(Path.Combine(local, "third.txt")) == "unchanged accepted bytes" &&
                        (await database.LoadCompletedCopyMovesAsync(pair.Id, default)).Count == 0,
                    "repeated-move retirement changed the newest copy or retained an obsolete candidate");
                Console.WriteLine("PASS A-to-B-to-C real copies retain two exact old memberships, linear compact rows and independently retire both without altering current C");
                continue;
            }
            if (scenario == "observed-absence")
            {
                File.Delete(Path.Combine(local, "old.txt")); // Own generated input; observation itself never deletes.
                var incomplete = await database.BeginScanGenerationAsync(pair.Id, localRoot, "local", "manual",
                    "{\"scope\":\"complete\"}");
                Require(await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                    "an incomplete observation retired the old candidate");
                var partial = await database.BeginScanGenerationAsync(pair.Id, localRoot, "local", "manual",
                    "{\"scope\":\"partial\"}");
                await database.CompleteScanGenerationAsync(partial);
                Require(await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                    "a partial empty observation retired the old candidate");
                var uncertain = await database.BeginScanGenerationAsync(pair.Id, localRoot, "local", "manual",
                    "{\"scope\":\"complete_with_uncertainty\"}");
                await database.CompleteScanGenerationAsync(uncertain);
                Require(await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                    "an uncertain observation retired the old candidate");
                await database.RetainPairDefinitionAsync(pair with { SelectionDefaultIncluded = false }, select: true);
                var excluded = await database.BeginScanGenerationAsync(pair.Id, localRoot, "local", "manual",
                    "{\"scope\":\"complete\"}");
                await database.CompleteScanGenerationAsync(excluded);
                Require(await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                    "an excluded old path was treated as confirmed absence");
                await database.RetainPairDefinitionAsync(pair, select: true);
                await new SyncAuthorityObservationCoordinator(pair, remote, database, localRoot, remoteRoot)
                    .ObserveAsync("manual");
                Require((await database.LoadCompletedCopyMovesAsync(pair.Id, default)).Count == 0 &&
                        File.ReadAllText(Path.Combine(local, "moved.txt")) == "unchanged accepted bytes",
                    "the exact fresh complete observed absence failed to retire only the obsolete candidate");
                Console.WriteLine("PASS incomplete, partial, uncertain and excluded scans preserve candidates; real fresh complete same-root absence retires metadata only");
                continue;
            }
            if (scenario == "baseline-invalidation")
            {
                var point = (await database.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(x => x.ObjectId == proof.ObjectId);
                await database.AcceptSynchronizationPointAsync("point-uncompleted-replacement", pair.Id, proof.ObjectId,
                    proof.NewVersionId, proof.NewRemoteVersionId, proof.NewMembershipId, point.DropboxMembershipId);
                Require((await database.LoadCompletedCopyMovesAsync(pair.Id, default)).Count == 0 &&
                    !await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                    "replacing the current baseline retained stale completed-copy authority");
                Require(File.Exists(Path.Combine(local, "old.txt")) && File.Exists(Path.Combine(local, "moved.txt")),
                    "baseline proof invalidation changed physical files");
                Console.WriteLine("PASS current copy baseline replacement invalidates proof atomically without file effects");
                continue;
            }
            if (normalizedProofOnly)
            {
                var retirement = await SyncAuthorityCompletedCopyMove.PlanAsync(pair, remote, database, options, proof, admission, default);
                var operation = await database.LoadOperationAsync(retirement);
                Require(!operation.Dependencies.Any(d => d.DependsOnOperationId == proof.CopyOperationId),
                    "the exact normalized retirement still pins the old copy journal");
                await new SyncAuthorityDeleteLocalExecutor(pair, database, remote).ExecuteAsync(retirement, "compact-proof-retire");
                Require(!File.Exists(Path.Combine(local, "old.txt")) &&
                    File.ReadAllText(Path.Combine(local, "moved.txt")) == "unchanged accepted bytes" &&
                    (await database.LoadCompletedCopyMovesAsync(pair.Id, default)).Count == 0,
                    "actual normalized auxiliary retirement lost current bytes or retained an obsolete candidate");
                Console.WriteLine("PASS current copy " + scenario + ": real receipt, exact proof, bounded legacy equality, guarded retirement and candidate lifetime");
                continue;
            }

            Require(!await database.CompletedCopyMoveMatchesAsync(pair.Id,
                    proof with { CopyOperationId = "unrelated-copy" }, default) &&
                !await database.CompletedCopyMoveMatchesAsync(pair.Id,
                    proof with { PriorPointId = proof.CurrentPointId }, default) &&
                !await database.CompletedCopyMoveMatchesAsync(pair.Id,
                    proof with { OldPath = "unrelated.txt" }, default),
                "the scoped production guard accepts a changed receipt, point or path");
            Require(File.Exists(Path.Combine(local, "old.txt")) && File.Exists(Path.Combine(local, "moved.txt")),
                "historical duplicate fixture was not produced by the real executor");
            var downloads = remote.DownloadCount;
            var uploads = remote.UploadCount;
            if (scenario == "cancel-owner-native-read")
            {
                var enteredNativeRead = 0;
                var stopObserved = false;
                Task<int>? stop = null;
                string? runId = null;
                using var stopOutput = new StringWriter(); using var stopError = new StringWriter();
                SyncAuthorityDatabase.CompletedCopyMoveReadProgressForTest = () =>
                {
                    if (Interlocked.Exchange(ref enteredNativeRead, 1) != 0) return;
                    runId = host.ActiveRun?.Id;
                    if (runId is null) return;
                    // The observer schedules normal exact-run CLI control on a
                    // separate task; it never queries/mutates the read connection
                    // or replaces the production cancellation decision.
                    stop = Task.Run(() => SyncCli.ExecuteAsync(["--cli", "cancel", "--key", runId,
                        "--yes", "--json"], stopOutput, stopError, () => operations));
                    stopObserved = SpinWait.SpinUntil(() => host.ActiveRun?.StopRequested == true,
                        TimeSpan.FromSeconds(2));
                };
                using var repairOutput = new StringWriter(); using var repairError = new StringWriter();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int repairExit;
                try
                {
                    repairExit = await Task.Run(() => SyncCli.ExecuteAsync(["--cli", "first-sync", "repair-moves",
                        "--pair", pair.Id, "--yes", "--json"], repairOutput, repairError, () => operations));
                }
                finally { SyncAuthorityDatabase.CompletedCopyMoveReadProgressForTest = null; }
                Require(enteredNativeRead == 1 && stopObserved && stop is not null &&
                    await stop == 0 && repairExit != 0 && !host.HasActiveRun && watch.Elapsed < TimeSpan.FromSeconds(5),
                    "supported exact-run cancellation did not terminate actual native SQLite work: " + stopError + repairError);
                await using (var inspection = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = database.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString()))
                {
                    await inspection.OpenAsync();
                    await using var command = inspection.CreateCommand();
                    command.CommandText = "SELECT status FROM command_request WHERE command_request_id=$run;";
                    command.Parameters.AddWithValue("$run", runId!);
                    Require((string?)await command.ExecuteScalarAsync() == "blocked",
                        "the cancelled repair did not retain its terminal command state");
                }
                Require(remote.DownloadCount == downloads && remote.UploadCount == uploads &&
                    File.Exists(Path.Combine(local, "old.txt")) &&
                    await database.CompletedCopyMoveMatchesAsync(pair.Id, proof, default),
                    "cancelled read changed files, replayed effects, or left subsequent reads unusable");
                Console.WriteLine("PASS actual CLI exact-owner cancellation enters production SQLite VM, reaches retained checkpoint and leaves later reads usable without effects");
                continue;
            }
            var oldText = "unchanged accepted bytes";
            var copyText = oldText;
            if (scenario == "old-edited")
                await File.WriteAllTextAsync(Path.Combine(local, "old.txt"), oldText = "keep edited predecessor");
            if (scenario == "copy-edited")
                await File.WriteAllTextAsync(Path.Combine(local, "moved.txt"), copyText = "keep edited completed copy");
            if (scenario == "cloud-edited") remote.Put("moved.txt", "changed cloud copy");
            if (scenario == "identity-changed") remote.PutWithId("moved.txt", "replacement identity", "id:unrelated");
            if (scenario is "interrupted" or "interrupted-not-applied" or "interrupted-ambiguous" or "changed-after-plan")
            {
                var retirement = await SyncAuthorityCompletedCopyMove.PlanAsync(pair, remote, database, options, proof, admission, default);
                if (scenario == "changed-after-plan")
                    await File.WriteAllTextAsync(Path.Combine(local, "old.txt"), oldText = "late predecessor edit");
                else SyncAuthorityDeleteLocalExecutor.PhaseForTest = phase =>
                {
                    if (phase == (scenario == "interrupted-not-applied"
                        ? "local-dispatched-before-retention" : "local-retained-before-receipt"))
                        throw new IOException("fixture interruption at the selected durable retention boundary");
                };
                try
                {
                    await new SyncAuthorityDeleteLocalExecutor(pair, database, remote).ExecuteAsync(retirement, "fixture-retire");
                    throw new InvalidOperationException("The injected interruption/change did not reject.");
                }
                catch (IOException) { }
                finally { SyncAuthorityDeleteLocalExecutor.PhaseForTest = null; }
                if (scenario == "interrupted-ambiguous")
                    await File.WriteAllTextAsync(Path.Combine(local, "old.txt"), oldText = "unrelated newly created occupant");
            }
            var success = scenario is "success" or "interrupted" or "interrupted-not-applied" or "recreated-after-completion";
            await CliChecks.CheckCompletedMoveRepairAsync(host, state, pair.Id, success);
            Require(remote.DownloadCount == downloads && remote.UploadCount == uploads,
                "repair replayed a completed download or uploaded cloud data");
            Require(File.ReadAllText(Path.Combine(local, "moved.txt")) == copyText, "repair changed the accepted local copy");
            if (success)
            {
                Require(!File.Exists(Path.Combine(local, "old.txt")), "obsolete active membership remains");
                var artifacts = await database.LoadRecoveryArtifactsAsync();
                Require(artifacts.Any(a => a.OperationId == SyncAuthorityCompletedCopyMove.OperationId(pair.Id, proof)),
                    "retired predecessor is not retrievable from the real Recovery catalog");
                await CliChecks.CheckCompletedMoveRepairAsync(host, state, pair.Id, true, expectCandidates: false);
                Require(remote.DownloadCount == downloads && remote.UploadCount == uploads, "second repair repeated provider effects");
                if (scenario == "recreated-after-completion")
                {
                    await File.WriteAllTextAsync(Path.Combine(local, "old.txt"), "unrelated post-completion occupant");
                    await CliChecks.CheckCompletedMoveRepairAsync(host, state, pair.Id, true, expectCandidates: false);
                    Require(File.ReadAllText(Path.Combine(local, "old.txt")) == "unrelated post-completion occupant",
                        "a completed operation was replayed against a newly occupied path");
                }
            }
            else Require(File.ReadAllText(Path.Combine(local, "old.txt")) == oldText, "rejected repair lost predecessor bytes");
            Console.WriteLine("PASS actual CLI completed-copy move " + scenario + ": exact lineage, original/copy/provider fences, retained preimage, no provider effect replay");
        }
    }

    private static async Task Migration13Async(string root)
    {
        var path = Path.Combine(root, "version13.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            await connection.OpenAsync();
            foreach (var sql in new[] { SyncAuthoritySchema.Sql, SyncAuthoritySchemaSupplement.Sql,
                SyncAuthoritySchemaMigration3.Sql, SyncAuthoritySchemaMigration4.Sql,
                SyncAuthoritySchemaMigration5.Sql, SyncAuthoritySchemaMigration6.Sql,
                SyncAuthoritySchemaMigration7.Sql, SyncAuthoritySchemaMigration8.Sql,
                SyncAuthoritySchemaMigration9.Sql, SyncAuthoritySchemaMigration10.Sql,
                SyncAuthoritySchemaMigration11.Sql, SyncAuthoritySchemaMigration12.Sql,
                "INSERT INTO installation(installation_id,database_generation,created_utc) VALUES('copy-v13','copy-generation-v13','2026-01-01T00:00:00.0000000Z');",
                SyncAuthoritySchemaMigration13.Sql,
                "INSERT INTO schema_migration(schema_version,applied_utc,source_revision) VALUES(13,'2026-01-01T00:00:00.0000000Z','copy-v13'); PRAGMA user_version=13;" })
            {
                await using var command = connection.CreateCommand(); command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }
        var database = new SyncAuthorityDatabase(path);
        var rejected = false;
        SyncAuthorityDatabase.MigrationBoundaryForTest = boundary =>
        { if (boundary == "v14-schema-applied-before-version-commit") throw new IOException("fixture interrupted copy-proof migration"); };
        try { await database.InitializeAsync("copy-v13", "copy-v14", "copy-v14", "{}"); }
        catch (IOException) { rejected = true; }
        finally { SyncAuthorityDatabase.MigrationBoundaryForTest = null; }
        Require(rejected, "migration fault injection did not reach the real transaction");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
            Require(Convert.ToInt64(await query.ExecuteScalarAsync()) == 13, "failed migration published a partial schema version");
        }
        await database.InitializeAsync("copy-v13", "copy-v14", "copy-v14", "{}");
        await database.VerifyHealthAsync();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database.Version13RollbackDatabasePath + ";Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
            Require(Convert.ToInt64(await query.ExecuteScalarAsync()) == 13, "version13 rollback does not preserve its exact predecessor schema");
            query.CommandText = "SELECT database_generation FROM installation;";
            Require((string?)await query.ExecuteScalarAsync() == "copy-generation-v13", "migration rollback changed installation generation");
        }
        Console.WriteLine("PASS real version13-to14 migration rollback/retry preserves predecessor schema and installation");
    }

    private static void Require(bool value, string detail)
    { if (!value) throw new InvalidOperationException(detail); }
}
