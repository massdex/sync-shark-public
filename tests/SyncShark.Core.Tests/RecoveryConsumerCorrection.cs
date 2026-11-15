using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Only the provider transport is a fixture. All parsing, planning, persistence,
// recovery, conflict decisions and local filesystem effects use production code.
internal static class RecoveryConsumerCorrection
{
    private const string Before = "exact predecessor bytes";
    private const string After = "exact successor bytes, different length";
    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }

    internal static async Task RunAsync(string group = "all")
    {
        if (group is not ("all" or "runner" or "bounded-run" or "conflict" or "conflict-batch" or "retirement" or "replacement" or "transfers" or "historical" or "placeholder" or "legacy" or "legacy-rejection" or "restore" or "restore-held-object" or "acceptance-gaps" or "containment-lifecycle" or "live-progress"))
            throw new ArgumentOutOfRangeException(nameof(group));
        if (group is "all" or "retirement")
        {
            foreach (var mode in new[] { "not-applied", "unknown", "applied", "active-lease", "ready" })
                await PairRetirementKnownNoEffectAsync(mode);
            await PairRetirementKnownNoEffectAsync("not-applied", replace: true, retireBeforeBind: true);
        }
        // Editing an active pair is a different contract from retire-then-create.
        // Exercise the real host handoff and every unsafe interrupted-work boundary.
        if (group is "all" or "replacement")
        {
            foreach (var mode in new[] { "not-applied", "unknown", "applied", "active-lease", "ready" })
                await PairRetirementKnownNoEffectAsync(mode, replace: true);
            foreach (var mode in new[] { "stale-revision", "foreign-overlap", "late-root-overlap", "publication-drift" })
                await PairReplacementAtomicRejectionAsync(mode);
        }
        if (group is "all" or "live-progress")
            foreach (var reject in new[] { false, true }) await RecoveryLiveProgressAsync(reject);
        if (group is "all" or "runner") {
        await RunnerAsync(false);
        await RunnerAsync(true);
        }
        if (group is "all" or "bounded-run") await AuthorityBoundedObservedRunAsync();
        if (group is "all" or "conflict")
        {
            foreach (var kind in Enum.GetValues<SyncAuthorityConflictResolutionKind>())
                foreach (var reject in new[] { false, true }) await ConflictAsync(kind, reject);
            await AuthorityConflictNestedAsync(false);
            await AuthorityConflictNestedAsync(true);
            await AuthorityConflictContainmentNestedAsync();
        }
        if (group is "all" or "conflict" or "conflict-batch")
        {
            await AuthorityConflictBatchAsync(false);
            await AuthorityConflictBatchAsync(true);
            await AuthorityConflictBatchFinalizationGapAsync();
            await AuthorityConflictInterruptedReadyAsync();
            await AuthorityConflictRetainedSourceRefusalAsync();
            await AuthorityConflictSourceDriftAsync();
            foreach (var change in new[] { "source", "destination", "identical" })
                await AuthorityOrdinaryChangeAfterPlanAsync(change);
            foreach (var change in new[] { "none", "bytes", "identity" })
                await AuthorityLegacyIdentityBaselineAsync(change);
            await AuthorityConflictBatchPersistenceAsync();
        }
        if (group is "all" or "transfers")
        foreach (var mode in new[] { "create", "update", "delete" })
            foreach (var reject in new[] { false, true }) await LocalOrDeleteAsync(mode, reject);
        if (group is "all" or "historical")
        foreach (var mode in new[] { "retained", "deleted", "new-revision", "moved-before", "moved-after" })
            await HistoricalAsync(mode);
        if (group is "all" or "placeholder")
        {
        foreach (var interrupted in new[] { false, true }) await PlaceholderCreateAsync(interrupted);
        foreach (var reject in new[] { false, true }) await PlaceholderAsync(reject);
        }
        if (group is "all" or "legacy")
        foreach (var mode in new[] { "create", "update", "keep-dropbox" })
            foreach (var reject in new[] { false, true }) await LegacyAsync(mode, reject);
        if (group is "all" or "restore")
            foreach (var reject in new[] { false, true }) await RestoreAsync(reject);
        if (group is "all" or "restore-held-object")
        {
            foreach (var directory in new[] { false, true })
                foreach (var rejectPersistence in new[] { false, true })
                    await RestoreHeldDestinationAsync(directory, rejectPersistence);
            foreach (var provider in new[] { SyncProviders.Dropbox, SyncProviders.GoogleDrive, SyncProviders.Local })
                await ManualArchiveProviderIdentityAsync(provider);
        }
        if (group == "legacy-rejection") await LegacyAsync("keep-dropbox", true);
        if (group == "acceptance-gaps")
        {
            await PreservePrerequisiteRejectionAsync();
            foreach (var mode in new[] { "remote-update", "remote-delete", "keep-local" })
                foreach (var reject in new[] { false, true }) await LegacyAsync(mode, reject);
            foreach (var mode in new[] { "archive", "revision", "directory" })
                foreach (var reject in new[] { false, true }) await RestoreOccupiedAsync(mode, reject);
            foreach (var failure in new[] { "race", "lost-reply", "malformed", "cancelled" })
                await RevisionRestoreFailureAsync(failure);
        }
        if (group == "containment-lifecycle")
        {
            foreach (var mode in new[] { "legacy", "bound", "outside", "wrong-identity" })
                await DropboxRecoveryRemovalLifecycleAsync(mode);
            await InvalidRecoveryPublicLifecycleAsync(missingBinding: false);
            await InvalidRecoveryPublicLifecycleAsync(missingBinding: true);
        }
    }

    private static async Task DropboxRecoveryRemovalLifecycleAsync(string mode)
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, After);
        var operation = await f.PlanOperationAsync();
        var key = "recovery-cleanup-contained";
        var source = f.Wire.Put(mode switch
        {
            "legacy" => SyncDropboxArtifactPaths.BuildRecoveryOperationPath(f.Pair.Id,
                "0123456789abcdef0123456789abcdef"),
            "outside" => "/Elsewhere/" + key + ".recovery",
            _ => SyncDropboxArtifactPaths.RecoveryContainerPath + "/" + key + ".recovery"
        }, Encoding.UTF8.GetBytes(Before));
        var identity = JsonSerializer.Serialize(new
        {
            kind = "file", FileId = mode == "wrong-identity" ? "id:not-the-retained-file" : source.Id,
            source.Revision, RecoveryPath = source.Path,
            originalPath = "file.txt"
        });
        // Startup recovery deliberately ignores Ready operations with no issued effect.
        // This fixture explicitly executes its fresh plan before testing retained-copy cleanup.
        var pending = await f.Runner().RecoverAsync();
        Require(pending.Decisions.Count == 0 && f.Wire.Uploads.Count == 0 && f.Wire.Deletes == 0 &&
                (await f.Db.LoadOperationAsync(operation.OperationId)).Status == SyncAuthorityOperationStatus.Ready,
            "Startup recovery issued a fresh, unstarted action without explicit execution.");
        var recovered = await f.Runner().ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(
            f.Pair.Id, [], [new SyncAuthorityPlannedPairOperation("file.txt", operation)]));
        var recoveredOperation = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(recoveredOperation.Status == SyncAuthorityOperationStatus.Completed,
            $"The bounded cleanup fixture operation did not complete ({mode}, {recoveredOperation.Status}). " +
            JsonSerializer.Serialize(recovered));
        await f.Db.RegisterRecoveryArtifactAsync(key, operation.OperationId, operation.ObjectId!, null,
            "dropbox", identity, source.Hash, source.Bytes.LongLength);
        await AgeRecoveryArtifactAsync(f, key);
        var item = (await f.WithHostAsync(host => host.LoadRecoveryCatalogAsync(f.Pair.Id)))
            .Single(candidate => candidate.Key == key);
        Require(item.CanRemove, "The aged retained Dropbox Recovery copy was not eligible for explicit cleanup.");
        if (mode is "outside" or "wrong-identity")
        {
            var deletesBefore = f.Wire.Deletes;
            var uploadsBefore = f.Wire.Uploads.Count;
            var rejected = false;
            try { await f.WithHostAsync(host => host.RemoveRecoveryCopyAsync(item)); }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            { rejected = true; }
            Require(rejected && f.Wire.Deletes == deletesBefore && f.Wire.Uploads.Count == uploadsBefore &&
                    f.Wire.Files.ContainsKey(source.Path) &&
                    (await f.Db.LoadRecoveryArtifactsAsync()).Single(candidate => candidate.RecoveryArtifactId == key)
                    .Status == "retained",
                "Recovery cleanup accepted an out-of-root or different-identity artifact, or changed retained state.");
            Console.WriteLine($"PASS TC3 public Dropbox Recovery removal/{mode}: zero mutations and no release authorization");
            return;
        }
        var removed = await f.WithHostAsync(host => host.RemoveRecoveryCopyAsync(item));
        Require(f.Wire.Deletes == 1 && !f.Wire.Files.ContainsKey(source.Path) &&
                removed.Detail.Contains("removed", StringComparison.OrdinalIgnoreCase) &&
                (await f.Db.LoadRecoveryArtifactsAsync()).Single(candidate => candidate.RecoveryArtifactId == key)
                .Status == "released",
            "Public Dropbox Recovery cleanup did not delete the exact artifact once and retain released state.");
        var requestsAfterRemoval = f.Wire.RequestCount;
        var replay = await f.WithHostAsync(host => host.RemoveRecoveryCopyAsync(item));
        Require(f.Wire.Deletes == 1 && f.Wire.RequestCount == requestsAfterRemoval &&
                replay.Detail.Contains("already removed", StringComparison.OrdinalIgnoreCase),
            "Restart/replay repeated a completed Dropbox Recovery deletion or contacted the provider.");
        Console.WriteLine($"PASS TC3 public Dropbox Recovery removal/{mode}: exact one-sided delete, durable release, restart replay has zero duplicate effects");
    }

    private static async Task InvalidRecoveryPublicLifecycleAsync(bool missingBinding)
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, After);
        var operation = await f.PlanOperationAsync();
        var key = missingBinding ? "recovery-public-missing" : "recovery-public-invalid";
        var source = f.Wire.Put(SyncDropboxArtifactPaths.BuildRecoveryOperationPath(f.Pair.Id,
                missingBinding ? "11111111111111111111111111111111" : "22222222222222222222222222222222"),
            Encoding.UTF8.GetBytes(Before));
        var identity = JsonSerializer.Serialize(new
        {
            kind = "file", FileId = source.Id, source.Revision, RecoveryPath = source.Path,
            originalPath = "file.txt"
        });
        var executed = await f.Runner().ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(
            f.Pair.Id, [], [new SyncAuthorityPlannedPairOperation("file.txt", operation)]));
        var executedOperation = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(executedOperation.Status == SyncAuthorityOperationStatus.Completed,
            $"The bounded cleanup fixture operation did not complete ({executedOperation.Status}). " +
            JsonSerializer.Serialize(executed));
        await f.Db.RegisterRecoveryArtifactAsync(key, operation.OperationId, operation.ObjectId!, null,
            "dropbox", identity, source.Hash, source.Bytes.LongLength);
        await AgeRecoveryArtifactAsync(f, key);
        var item = (await f.WithHostAsync(host => host.LoadRecoveryCatalogAsync(f.Pair.Id)))
            .Single(candidate => candidate.Key == key);
        var persisted = f.Pair with
        {
            RecoveryRoot = missingBinding
                ? null
                : f.Pair.RecoveryRoot! with { RootPath = "/Elsewhere" }
        };
        await UpdatePersistedPairAsync(f, persisted);

        using var appLock = PilotInstanceLock.Acquire(Path.Combine(f.Root,
            missingBinding ? "public-missing-state" : "public-invalid-state", "instance.lock"));
        var product = new SyncProductStateStore(appLock.StateAuthority);
        await product.UpdateAsync(current => current with
        { InstallationId = f.Pair.ControlNamespace!.InstallationId });
        await new SyncPairStore(appLock.StateAuthority).SaveAsync(new SyncPairSet([f.Pair], f.Pair.Id));
        var connections = new DropboxConnectionStore(appLock.StateAuthority);
        await connections.SaveNewAsync(Fixture.Connection);
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, f.Http,
            new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
            connections, new MemoryDropboxRefreshTokenStore("fixture-refresh"), f.Db);
        var requests = f.Wire.RequestCount;
        var commands = await CountRowsAsync(f, "command_request");
        await ExpectRecoveryBindingRejectionAsync(() => host.PreviewRecoveryAsync(item, restoreAsCopy: false));
        await ExpectRecoveryBindingRejectionAsync(() => host.RestoreRecoveryAsync(item, restoreAsCopy: false));
        await ExpectRecoveryBindingRejectionAsync(() => host.RemoveRecoveryCopyAsync(item));
        if (!missingBinding)
            await ExpectRecoveryBindingRejectionAsync(() => host.PreviewRecoveryAsync(item with
            {
                Source = SyncRecoverySource.DropboxRevision
            }, restoreAsCopy: true));
        Require(f.Wire.RequestCount == requests && await CountRowsAsync(f, "command_request") == commands &&
                f.Wire.Deletes == 0 && f.Wire.Files.ContainsKey(source.Path) &&
                (await f.Db.LoadRecoveryArtifactsAsync()).Single(candidate => candidate.RecoveryArtifactId == key)
                .Status == "retained",
            "Invalid or missing Recovery binding reached provider, command, transition, or artifact effects.");
        Console.WriteLine($"PASS TC3 public Recovery lifecycle/{(missingBinding ? "missing" : "invalid")}: preview, restore, cleanup and revision reject before provider or durable effect");
    }

    private static async Task ExpectRecoveryBindingRejectionAsync(Func<Task> action)
    {
        try
        {
            await action();
            throw new Exception("The invalid persisted Recovery binding was accepted by a public lifecycle entry point.");
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { }
    }

    private static async Task AgeRecoveryArtifactAsync(Fixture fixture, string recoveryArtifactId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = fixture.Db.DatabasePath }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recovery_artifact SET retained_utc=$utc WHERE recovery_artifact_id=$id;";
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.AddDays(-31).ToString("O"));
        command.Parameters.AddWithValue("$id", recoveryArtifactId);
        Require(await command.ExecuteNonQueryAsync() == 1, "The bounded cleanup fixture artifact was not aged.");
    }

    private static async Task UpdatePersistedPairAsync(Fixture fixture, SyncPair pair)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = fixture.Db.DatabasePath }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync_pair SET pair_json=$json WHERE pair_id=$id;";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(pair));
        command.Parameters.AddWithValue("$id", pair.Id);
        Require(await command.ExecuteNonQueryAsync() == 1, "The persisted pair fixture was not changed.");
    }

    private static async Task<long> CountRowsAsync(Fixture fixture, string table)
    {
        if (table != "command_request") throw new ArgumentOutOfRangeException(nameof(table));
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = fixture.Db.DatabasePath }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM command_request;";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task PreservePrerequisiteRejectionAsync()
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, After);
        var remote = f.Wire.Put("/Pilot/file.txt", Encoding.UTF8.GetBytes("Dropbox conflicting predecessor"));
        await f.Observer().ObserveAsync("monitor");
        var conflictPlan = (await f.Planner().PlanAsync(f.Pair.Id)).Single();
        var conflict = await f.Db.LoadConflictAsync(conflictPlan.ConflictId!);
        var coordinator = f.Conflicts();
        var plan = await coordinator.PlanAsync(conflict.ConflictId, conflict.ConflictGeneration,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        f.Wire.RejectRevision = remote.Revision;
        var rejected = false;
        try { await coordinator.ExecuteAsync(plan, "fixture-visible-rejection"); }
        catch (IOException) { rejected = true; }
        var preserve = await f.Db.LoadOperationAsync(plan.PreserveOperationId!);
        var primary = await f.Db.LoadOperationAsync(plan.OperationId);
        Require(rejected && preserve.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                primary.Status == SyncAuthorityOperationStatus.Ready &&
                (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status != "resolved" &&
                f.Wire.Uploads.Count == 0 && f.Wire.Deletes == 0 &&
                f.Wire.Read("/Pilot/file.txt") == "Dropbox conflicting predecessor" &&
                await File.ReadAllTextAsync(f.LocalPath) == After,
            "Rejected PreserveBoth prerequisite advanced the visible copy, primary operation or conflict.");
        Console.WriteLine("PASS TC3 preserve-prerequisite/rejected: actual coordinator retains EffectIssuing prerequisite; zero visible/recovery/replacement effects");
    }

    private static async Task RestoreAsync(bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        f.Wire.Put("/Pilot/file.txt", After);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(f.Root, "restore-state", "instance.lock"));
        var connections = new DropboxConnectionStore(appLock.StateAuthority);
        await connections.SaveNewAsync(Fixture.Connection);
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, f.Http,
            new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
            connections, new MemoryDropboxRefreshTokenStore("fixture-refresh"), f.Db);
        var catalog = await host.LoadDropboxRevisionCatalogAsync(f.Pair.Id, "file.txt");
        var original = catalog.Single(c => c.ContentHash == DropboxContentHash.Compute(Encoding.UTF8.GetBytes(Before)));
        f.Wire.RejectDownloads = reject;
        SyncRecoveryRestoreResult? result = null;
        try { result = await host.RestoreRecoveryAsync(original, true); }
        catch (IOException) when (reject) { }
        Require(reject ? result is null && f.Wire.Uploads.Count == 0 :
                result is not null && f.Wire.Read("/Pilot/" + result.TargetRelativePath) == Before && f.Wire.Uploads.Count == 1,
            "Recovery tab restore promoted rejected/wrong historical bytes.");
        Require(f.Wire.Read("/Pilot/file.txt") == After && f.Wire.Deletes == 0,
            "Historical restore-as-copy displaced its current original.");
        Console.WriteLine($"PASS TC3 recovery-tab-restore/{reject}: actual catalog, command and historical download; exact copy or zero mutations");
    }

    private static async Task RestoreHeldDestinationAsync(bool directory, bool rejectPersistence)
    {
        await using var f = await Fixture.CreateAsync();
        f.Wire.Put(f.RemotePath, After);
        var operation = await f.PlanOperationAsync();
        await f.Runner().ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(f.Pair.Id, [],
            [new SyncAuthorityPlannedPairOperation(f.RelativePath, operation)]));
        Require((await f.Db.LoadOperationAsync(operation.OperationId)).Status == SyncAuthorityOperationStatus.Completed &&
                await File.ReadAllTextAsync(f.LocalPath) == After,
            "The actual local replacement must create the retained predecessor before manual restore.");
        var original = (await f.Db.LoadRecoveryArtifactsAsync()).Single(item => item.Side == "local");
        var key = original.RecoveryArtifactId;
        if (directory)
        {
            key = "manual-held-directory-recipe";
            await f.Db.RegisterRecoveryArtifactAsync(key, operation.OperationId, operation.ObjectId!, null,
                "local", JsonSerializer.Serialize(new
                {
                    kind = "directory", recipe = "recreate_empty_directory", originalPath = f.RelativePath
                }), new string('0', 64), 0);
        }
        var replacement = Path.Combine(f.Root, "unrelated-replacement.txt");
        const string replacementBytes = "a separately saved replacement must never be deleted";
        await File.WriteAllTextAsync(replacement, replacementBytes);
        // Establish ordinary write/delete access first: a refusal below must come from the held
        // object, not a fixture whose ACL already makes any replacement impossible.
        using (var writable = SyncLocalFileAuthority.OpenExistingExclusive(f.LocalPath, FileAccess.ReadWrite))
            Require(writable.Stream.CanWrite && writable.FinalPath == f.LocalPath,
                "The ordinary destination must be writable and delete-capable before restore holds it.");
        var boundaryCalls = 0;
        var persistenceProbeFired = false;
        var priorTiming = SyncAuthorityDatabase.TimingForTest;
        var writeRejected = false;
        var replacementRejected = false;
        SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = () =>
        {
            boundaryCalls++;
            try { File.WriteAllText(f.LocalPath, "new edit during retention"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeRejected = true; }
            try { File.Move(replacement, f.LocalPath, overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { replacementRejected = true; }
            Require(writeRejected && replacementRejected && File.Exists(replacement),
                "The real backup-to-registration boundary must hold the exact destination against edits and substitution.");
            if (rejectPersistence)
                SyncAuthorityDatabase.TimingForTest = (phase, _) =>
                {
                    if (phase != "write-body") return;
                    // Fault input at the existing real pre-commit boundary: registration executes
                    // its SQL and transaction rollback remains production code. No schema change,
                    // mocked persistence result, disabled guard or source-deletion decision.
                    persistenceProbeFired = true;
                    SyncAuthorityDatabase.TimingForTest = priorTiming;
                    throw new IOException("manual-restore-fixture-precommit-rejected");
                };
        };
        SyncRecoveryRestoreResult? restored = null;
        try
        {
            restored = await f.WithHostAsync(async host =>
            {
                var selected = (await host.LoadRecoveryCatalogAsync(f.Pair.Id)).Single(item => item.Key == key);
                return await host.RestoreRecoveryAsync(selected, restoreAsCopy: false);
            });
        }
        catch (IOException error) when (rejectPersistence && persistenceProbeFired &&
                                        error.Message == "manual-restore-fixture-precommit-rejected") { }
        finally
        {
            SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = null;
            SyncAuthorityDatabase.TimingForTest = priorTiming;
        }
        var artifacts = await f.Db.LoadRecoveryArtifactsAsync();
        var preimages = artifacts.Where(item => item.Side == "local" &&
            item.IdentityJson.Contains("manual_", StringComparison.Ordinal)).ToArray();
        Require(boundaryCalls == 1 && writeRejected && replacementRejected &&
                await File.ReadAllTextAsync(replacement) == replacementBytes &&
                artifacts.Any(item => item == original) && f.Wire.Uploads.Count == 0 && f.Wire.Deletes == 0,
            "Manual restore must preserve the original history and unrelated replacement with zero cloud writes.");
        if (rejectPersistence)
        {
            Require(persistenceProbeFired && restored is null && preimages.Length == 0 && !Directory.Exists(f.LocalPath) &&
                    await File.ReadAllTextAsync(f.LocalPath) == After,
                "A real recovery-registration rejection must not delete or replace the current destination.");
            var failedRun = (await f.Db.LoadRunHistoryAsync()).Single(item => item.Kind == "reconcile" &&
                item.Status == "blocked");
            using var failureDocument = JsonDocument.Parse(failedRun.ResultJson!);
            var failure = failureDocument.RootElement.GetProperty("commandFailure");
            Require(failure.GetProperty("Code").GetString() == "recovery_restore_failed" &&
                    failure.GetProperty("RelativePath").GetString() == f.RelativePath &&
                    failure.GetProperty("Message").GetString()!.Contains("manual-restore-fixture-precommit-rejected") &&
                    failure.GetProperty("NextAction").GetString()!.Contains("uncertain effect"),
                "The real restore rejection must retain its exact cause, target and safe next action in its run.");
            var events = await f.Db.LoadActivityProjectionPageAsync(200, pairId: f.Pair.Id,
                query: new SyncActivityQuery(RunId: failedRun.Id));
            Require(events.Any(item => item.Reason.Contains("manual-restore-fixture-precommit-rejected") &&
                    item.Reason.Contains(f.RelativePath)),
                "The same exact recovery failure must be reachable in the run-linked operation log.");
            f.ReopenAuthority();
            var problems = await f.WithHostAsync(host => host.LoadRetainedCommandProblemsAsync());
            Require(problems.Any(item => item.CommandId == failedRun.Id && item.PairId == f.Pair.Id &&
                    item.Code == "recovery_restore_failed" &&
                    item.Message.Contains("manual-restore-fixture-precommit-rejected") &&
                    item.Message.Contains(f.RelativePath)),
                "Problems must retain the exact restore failure after reopening rather than show a generic approval loop.");
            // The handle is released on rejection, but no delayed delete may follow an edit saved afterward.
            File.Move(replacement, f.LocalPath, overwrite: true);
            Require(await File.ReadAllTextAsync(f.LocalPath) == replacementBytes,
                "A replacement saved after safe rejection was removed by delayed cleanup.");
        }
        else
        {
            Require(restored is not null && preimages.Length == 1 &&
                    (directory ? Directory.Exists(f.LocalPath) : await File.ReadAllTextAsync(f.LocalPath) == Before),
                "The public restore must complete the file or directory recipe after exact retention.");
            using var identity = JsonDocument.Parse(preimages.Single().IdentityJson);
            var retainedPath = identity.RootElement.GetProperty("path").GetString()!;
            Require(await File.ReadAllTextAsync(retainedPath) == After &&
                    preimages.Single().ContentHash == SyncContentIdentity.Compute(f.Pair.Provider, Encoding.UTF8.GetBytes(After)),
                "The durable preimage must retain the exact displaced bytes and provider-aware identity.");
        }
        Console.WriteLine($"PASS H4 public local {(directory ? "folder" : "file")} restore/{(rejectPersistence ? "database rejection" : "success")}: exact held object rejects concurrent edit/substitution; predecessor, unrelated replacement and history preserved; zero cloud writes");
    }

    private static async Task ManualArchiveProviderIdentityAsync(string provider)
    {
        await using var f = await Fixture.CreateAsync();
        using var root = PilotDirectoryLease.Open(f.Pair.LocalRoot);
        using var control = SyncControlNamespace.OpenLocal(f.Pair.ControlNamespace!, root);
        SyncLocalArchiveResult? durable = null;
        var retained = await SyncLocalRecovery.ArchiveForManualRestoreAsync(root, control, f.Pair.Id,
            Guid.NewGuid().ToString("N"), f.RelativePath, provider, (value, _) =>
            {
                durable = value;
                Require(File.Exists(f.LocalPath), "The destination must remain present until persistence succeeds.");
                return Task.CompletedTask;
            });
        Require(durable == retained && !File.Exists(f.LocalPath) &&
                await File.ReadAllTextAsync(retained.RecoveryPath) == Before &&
                retained.ContentHash == SyncContentIdentity.Compute(provider, Encoding.UTF8.GetBytes(Before)),
            "Shared manual retention must use the consuming provider's exact content algorithm, not a Dropbox-only hash.");
        Console.WriteLine($"PASS H4 shared manual archive/{provider}: actual held-object removal follows successful persistence; retained bytes and provider algorithm exact");
    }

    private static async Task LegacyAsync(string mode, bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        var root = SyncControlNamespace.RemoteContainerPath + "/namespace-correction";
        var markerPath = root + "/.syncshark-owner.json";
        f.Wire.Folders[root] = "id:legacy-control";
        var marker = f.Wire.Put(markerPath, SyncControlNamespace.MarkerBytes("installation-correction", "namespace-correction"));
        var pair = f.Pair with { ControlNamespace = f.Pair.ControlNamespace! with
        { Remote = new SyncRemoteControlNamespaceBinding(root, "id:legacy-control", markerPath, marker.Id, marker.Revision, marker.Hash) } };
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(f.Root, "legacy-state", "instance.lock"));
        await new SyncProductStateStore(appLock.StateAuthority).UpdateAsync(current => current with
        { InstallationId = pair.ControlNamespace!.InstallationId });
        await new SyncPairStore(appLock.StateAuthority).SaveAsync(new SyncPairSet([pair], pair.Id));
        var store = new SyncRuntimeStateStore(appLock.StateAuthority);
        var engine = new BidirectionalSyncEngine(pair, f.Client, store, new PilotRunControl());
        await engine.RunAsync(); await engine.RunAsync();
        Require(store.LoadPair(pair.Id).BaselineEstablished, "Legacy runtime did not establish its real baseline.");
        var path = mode == "create" ? "new.txt" : "file.txt";
        if (mode is "create" or "update" or "keep-dropbox") f.Wire.Put("/Pilot/" + path, After);
        if (mode == "keep-dropbox") await File.WriteAllTextAsync(f.LocalPath, "local conflict");
        if (mode == "remote-update") await File.WriteAllTextAsync(f.LocalPath, After);
        if (mode == "remote-delete") File.Delete(f.LocalPath);
        if (mode == "keep-local")
        {
            await File.WriteAllTextAsync(f.LocalPath, "local conflict winner");
            f.Wire.Put("/Pilot/file.txt", "Dropbox conflict predecessor");
        }
        var remoteBefore = f.Wire.Files["/Pilot/" + path];
        f.Wire.RejectRevision = reject ? remoteBefore.Revision : null;
        SyncRunResult? result = null;
        try
        {
            result = mode == "keep-dropbox" ? await engine.ResolveConflictAsync(path, SyncConflictResolution.KeepDropboxArchiveLocal) :
                mode == "keep-local" ? await engine.ResolveConflictAsync(path, SyncConflictResolution.KeepLocalArchiveDropbox) :
                await engine.RunAsync();
        }
        catch (IOException) when (reject) { }
        var target = Path.Combine(pair.LocalRoot, path);
        if (mode is "remote-update" or "remote-delete" or "keep-local")
        {
            var expectedWinner = mode == "keep-local" ? "local conflict winner" : After;
            var expectedRecovery = Encoding.UTF8.GetString(remoteBefore.Bytes);
            if (reject)
            {
                _ = store.LoadPair(pair.Id).PendingTransfer ?? throw new Exception("Missing retained legacy remote intent.");
                Require(f.Wire.LegacyRecoveryUploads.Count == 0 && f.Wire.Deletes == 0 &&
                        f.Wire.Read("/Pilot/" + path) == expectedRecovery &&
                        result!.Decisions.Any(d => d.Kind == SyncDecisionKind.Blocked),
                    "Rejected legacy remote predecessor advanced archive/replacement/deletion.");
                f.Wire.RejectRevision = null;
                var resumed = await engine.RunAsync();
                Require(store.LoadPair(pair.Id).PendingTransfer is null &&
                        resumed.Decisions.Any(d => d.Kind is SyncDecisionKind.Archived or SyncDecisionKind.Recovered),
                    "Legacy remote intent did not safely resume.");
            }
            if (mode == "remote-delete")
                Require(!f.Wire.Files.ContainsKey("/Pilot/" + path) && f.Wire.Deletes == 1,
                    "Legacy remote deletion did not conditionally delete exactly once.");
            else
                Require(f.Wire.Read("/Pilot/" + path) == expectedWinner &&
                        f.Wire.Uploads.Count(u => u.Conditional) == 1,
                    "Legacy remote replacement did not commit the chosen local bytes exactly once.");
            Require(f.Wire.LegacyRecoveryUploads.Count == 1 &&
                    Encoding.UTF8.GetString(f.Wire.LegacyRecoveryUploads.Single().Bytes) == expectedRecovery &&
                    f.Wire.DownloadRevisions.Contains(remoteBefore.Revision),
                "Legacy remote consumer did not retain its exact predecessor through the real download parser.");
        }
        else if (reject && mode == "keep-dropbox")
        {
            // This unchanged legacy API is explicitly archive-first. Rejection must
            // retain the real local predecessor and composite intent, never a successor.
            var pending = store.LoadPair(pair.Id).PendingTransfer ?? throw new Exception("Missing retained legacy intent.");
            var recoveryPath = pending.RecoveryPath ?? throw new Exception("Missing retained legacy recovery path.");
            Require(!File.Exists(target) &&
                    await File.ReadAllTextAsync(recoveryPath) == "local conflict" &&
                    result!.Decisions.Any(d => d.Kind == SyncDecisionKind.Blocked) &&
                    result.Decisions.All(d => d.Kind != SyncDecisionKind.Downloaded),
                "Legacy rejection lost its archived predecessor or reported a committed successor.");
            f.Wire.RejectRevision = null;
            var resumed = await engine.RunAsync();
            Require(await File.ReadAllTextAsync(target) == After &&
                    await File.ReadAllTextAsync(recoveryPath) == "local conflict" &&
                    store.LoadPair(pair.Id).PendingTransfer is null &&
                    resumed.Decisions.Any(d => d.Kind is SyncDecisionKind.Downloaded or SyncDecisionKind.Recovered),
                "Legacy rejected-download intent could not safely resume with exact recovery intact.");
        }
        else if (reject)
            Require(mode == "create" ? !File.Exists(target) : await File.ReadAllTextAsync(target) == Before,
                "Legacy transfer promoted rejected bytes.");
        else Require(await File.ReadAllTextAsync(target) == After && result is not null,
            "Legacy consumer did not commit exact accepted bytes.");
        Require(f.Wire.DownloadRevisions.Contains(remoteBefore.Revision),
            "Legacy consumer never reached actual download parser.");
        Console.WriteLine($"PASS TC3 legacy/{mode}/{reject}: public engine and SyncLocalTransfer lifecycle, exact bytes or no promotion");
    }

    private static async Task RestoreOccupiedAsync(string mode, bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        var current = f.Wire.Put("/Pilot/file.txt", Encoding.UTF8.GetBytes(After));
        var operation = mode == "revision" ? null : await f.PlanOperationAsync();
        var key = "recovery-occupied-" + mode;
        var source = f.Wire.Put(SyncDropboxArtifactPaths.RecoveryContainerPath + "/" + key + ".recovery",
            Encoding.UTF8.GetBytes(Before));
        SyncRecoveryCatalogItem item;
        if (mode == "revision")
        {
            var historical = f.Wire.History.Values.Single(x => Encoding.UTF8.GetString(x.Bytes) == Before &&
                                                               x.Path == "/Pilot/file.txt");
            item = new SyncRecoveryCatalogItem("revision:" + historical.Revision, f.Pair.Id, f.Pair.Name,
                SyncRecoverySource.DropboxRevision, "file.txt", "Dropbox fixture", historical.Revision,
                historical.Bytes.Length, DateTimeOffset.UtcNow, "fixture", "Available from Dropbox",
                historical.Hash, historical.Id, historical.Revision, "{}", true);
        }
        else
        {
            var identity = mode == "directory"
                ? JsonSerializer.Serialize(new { kind = "directory", recipe = "recreate_empty_directory", originalPath = "file.txt" })
                : JsonSerializer.Serialize(new { kind = "file", FileId = source.Id, source.Revision,
                    RecoveryPath = source.Path, originalPath = "file.txt" });
            var hash = mode == "directory" ? new string('0', 64) : source.Hash;
            var size = mode == "directory" ? 0 : source.Bytes.LongLength;
            await f.Db.RegisterRecoveryArtifactAsync(key, operation!.OperationId, operation.ObjectId!, null,
                "dropbox", identity, hash, size);
            item = new SyncRecoveryCatalogItem(key, f.Pair.Id, f.Pair.Name, SyncRecoverySource.DropboxArchive,
                "file.txt", "Dropbox fixture", source.Path, size, DateTimeOffset.UtcNow, "fixture",
                "Available", hash, mode == "directory" ? null : source.Id,
                mode == "directory" ? null : source.Revision, identity, true,
                mode == "directory" ? "folder" : "file");
        }
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(f.Root, "restore-occupied-state", "instance.lock"));
        var connections = new DropboxConnectionStore(appLock.StateAuthority);
        await connections.SaveNewAsync(Fixture.Connection);
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, f.Http,
            new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
            connections, new MemoryDropboxRefreshTokenStore("fixture-refresh"), f.Db);
        var uploads = f.Wire.Uploads.Count;
        var deletes = f.Wire.Deletes;
        var copies = f.Wire.Copies;
        var restores = f.Wire.Restores;
        var folders = f.Wire.FoldersCreated;
        f.Wire.RejectRevision = reject ? current.Revision : null;
        SyncRecoveryRestoreResult? result = null;
        try { result = await host.RestoreRecoveryAsync(item, false); }
        catch (IOException) when (reject) { }
        if (reject)
            Require(result is null && f.Wire.Read("/Pilot/file.txt") == After &&
                    f.Wire.Uploads.Count == uploads && f.Wire.Deletes == deletes &&
                    f.Wire.Copies == copies && f.Wire.Restores == restores &&
                    f.Wire.FoldersCreated == folders,
                "Rejected occupied restore displaced its current file or issued a restore effect.");
        else
        {
            Require(result is not null && f.Wire.RecoveryUploads.Count == 1 &&
                    Encoding.UTF8.GetString(f.Wire.RecoveryUploads.Single().Bytes) == After,
                "Occupied restore did not retain the exact displaced current bytes.");
            if (mode == "directory")
                Require(!f.Wire.Files.ContainsKey("/Pilot/file.txt") && f.Wire.Folders.ContainsKey("/Pilot/file.txt") &&
                        f.Wire.Deletes == deletes + 1 && f.Wire.FoldersCreated == folders + 1,
                    "Directory recipe did not retain, delete and recreate the occupied path.");
            else
                Require(f.Wire.Read("/Pilot/file.txt") == Before &&
                        (mode == "archive" ? f.Wire.Copies == copies + 1 && f.Wire.Deletes == deletes + 1 :
                            f.Wire.Restores == restores && f.Wire.Deletes == deletes &&
                            f.Wire.Uploads.Count(upload => upload.Path == "/Pilot/file.txt" && upload.Conditional) == 1),
                    "Occupied file restore did not commit exact retained bytes through its real effect mode.");
        }
        Console.WriteLine($"PASS TC3 restore-occupied/{mode}/{reject}: current bytes retained on success; rejected metadata precedes delete/copy/restore/folder creation");
    }

    private static async Task RevisionRestoreFailureAsync(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        f.Wire.Put("/Pilot/file.txt", After);
        var historical = f.Wire.History.Values.Single(row => row.Path == "/Pilot/file.txt" &&
            Encoding.UTF8.GetString(row.Bytes) == Before);
        var item = new SyncRecoveryCatalogItem("revision:" + historical.Revision, f.Pair.Id, f.Pair.Name,
            SyncRecoverySource.DropboxRevision, "file.txt", "Dropbox fixture", historical.Revision,
            historical.Bytes.Length, DateTimeOffset.UtcNow, "fixture", "Available", historical.Hash,
            historical.Id, historical.Revision, "{}", true);
        var journalChecks = 0;
        f.Wire.BeforeMutation = async path =>
        {
            await using var connection = new SqliteConnection("Data Source=" + f.Db.DatabasePath + ";Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM operation_attempt a JOIN operation o USING(operation_id) " +
                "WHERE a.status='request_sent' AND o.status='effect_issuing' " +
                "AND json_extract(o.preconditions_json,'$.revisionRestore')=1;";
            Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1,
                "The provider mutation preceded its real durable dispatched attempt.");
            journalChecks++;
        };
        f.Wire.RevisionUploadFailure = failure;
        try
        {
            await f.WithHostAsync(async host => await host.RestoreRecoveryAsync(item, false));
            throw new Exception("The injected revision restore failure was reported as success.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        Require(journalChecks == 2 && f.Wire.Restores == 0 && f.Wire.Deletes == 0,
            "Revision restore did not journal both predecessor retention and conditional upload, or used an unconditional effect.");
        f.AssertRecoveryBytes(After);
        Require(f.Wire.Read("/Pilot/file.txt") == (failure == "race" ? "new collaborator bytes" : Before),
            "The actual provider mutation/race state differs from the retained outcome.");
        await using (var connection = new SqliteConnection("Data Source=" + f.Db.DatabasePath + ";Mode=ReadOnly"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM operation o JOIN operation_attempt a USING(operation_id) " +
                "WHERE json_extract(o.preconditions_json,'$.revisionRestore')=1 " +
                "AND o.status='effect_unknown' AND a.status='effect_unknown';";
            Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1,
                "The uncertain conditional request was not retained as its exact unresolved operation.");
        }
        var uploads = f.Wire.Uploads.Count;
        f.ReopenAuthority();
        f.Wire.RevisionUploadFailure = null;
        f.Wire.BeforeMutation = null;
        try
        {
            await f.WithHostAsync(async host => await host.RestoreRecoveryAsync(item, false));
            throw new Exception("An unresolved revision effect was silently replayed after reopening.");
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or SqliteException) { }
        Require(f.Wire.Uploads.Count == uploads && f.Wire.Restores == 0,
            "Reopening issued a second unresolved revision mutation.");
        Console.WriteLine("PASS revision-restore journal/race/lost-reply/reopen: " + failure);
    }

    private static async Task RecoveryLiveProgressAsync(bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, After);
        var operation = await f.PlanOperationAsync();
        f.Wire.FailNextDownload = true;
        try
        {
            await new SyncAuthorityUpdateRemoteExecutor(f.Pair, f.Client, f.Db)
                .ExecuteAsync(operation.OperationId, "live-progress-interruption");
        }
        catch (IOException) { }
        Require((await f.Db.LoadOperationAsync(operation.OperationId)).Status ==
                SyncAuthorityOperationStatus.EffectIssuing && f.Wire.Uploads.Count == 0,
            "The real executor must retain its pre-transfer interruption for recovery.");
        f.ReopenAuthority();
        f.Wire.RejectDownloads = reject;
        var progress = new List<SyncAuthorityBatchProgress>();
        var result = await f.WithHostAsync(host => host.RecoverPairsAsync([f.Pair], update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        }));
        var actions = progress.Where(item => item.Action?.OperationId == operation.OperationId)
            .Select(item => item.Action!).ToArray();
        Require(actions.Length >= 2 && actions.First().Stage == "Starting" &&
                actions.Last().Stage == (reject ? "Needs attention" : "Completed") &&
                actions.All(item => item.RelativePath == f.RelativePath &&
                                    !string.IsNullOrWhiteSpace(item.Detail)),
            "Recovery must expose the exact operation/file and start/outcome through its public progress callback.");
        var log = await f.Db.LoadActivityProjectionPageAsync(500,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
        var retained = log.Where(item => item.OperationId == operation.OperationId).ToArray();
        Require(retained.Length >= 2 && retained.All(item => !string.IsNullOrWhiteSpace(item.RunId)) &&
                retained.Any(item => item.Reason.Contains("Checking retained", StringComparison.Ordinal)) &&
                retained.Any(item => item.Reason == actions.Last().Detail),
            "Recovery's start and exact terminal cause must survive in the run-linked detailed operation log.");
        if (reject)
            Require((await f.Db.LoadOperationAsync(operation.OperationId)).Status !=
                    SyncAuthorityOperationStatus.Completed && f.Wire.Uploads.Count == 0 &&
                    f.Wire.Read(f.RemotePath) == Before,
                "Rejected recovery must log failure without changing or replaying remote bytes.");
        else
        {
            Require(result.Items.Single().Result?.Uploaded == 1,
                "Successful recovery must complete the real interrupted file.");
            f.AssertRemoteReplacement();
            var effects = f.Wire.Uploads.Count;
            await f.WithHostAsync(host => host.RecoverPairsAsync([f.Pair]));
            Require(f.Wire.Uploads.Count == effects, "Completed recovery is never replayed for logging.");
        }
        Console.WriteLine($"PASS recovery-live-progress/{reject}: exact live file/operation, retained outcome and no replay");
    }

    private static async Task RunnerAsync(bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, After);
        var operation = await f.PlanOperationAsync();
        var allocation = operation.Allocations.Single().AllocationId;
        var idleRecovery = await f.Runner().RecoverAsync();
        var unissued = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(idleRecovery.Decisions.Count == 0 && unissued.Status == SyncAuthorityOperationStatus.Ready &&
                unissued.Attempts.Count == 0 && f.Wire.Uploads.Count == 0,
            "Recovery must not start a retained but unissued fresh-plan operation.");
        f.Wire.FailNextDownload = true;
        var initial = await f.Runner().ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(
            f.Pair.Id, [], [new(f.RelativePath, operation)]));
        var interrupted = await f.Db.LoadOperationAsync(operation.OperationId);
        Console.WriteLine("RECOVERY-CONSUMER runner interruption: " + JsonSerializer.Serialize(new
        {
            initial.Decisions,
            interrupted.Status,
            interrupted.Attempts,
            interrupted.Allocations
        }));
        Require(initial.Decisions.Single().Kind == SyncDecisionKind.ResponseUnknown &&
                interrupted.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                interrupted.Allocations.Single().Status == SyncAuthorityAllocationStatus.Creating,
            "Runner did not retain the exact pre-effect interruption.");
        Require(f.Wire.Uploads.Count == 0 && f.Wire.Read("/Pilot/file.txt") == Before,
            "A predecessor changed before recovery.");
        var replayRejected = false;
        try { await new SyncAuthorityUpdateRemoteExecutor(f.Pair, f.Client, f.Db)
                .ExecuteAsync(operation.OperationId, "fixture-illegal-unreconciled-replay"); }
        catch (InvalidOperationException) { replayRejected = true; }
        Require(replayRejected && f.Wire.Uploads.Count == 0,
            "An unreconciled direct replay dispatched another effect.");
        SqliteConnection.ClearAllPools();
        f.Db = new SyncAuthorityDatabase(f.Db.DatabasePath);
        f.Wire.RejectDownloads = reject;
        SyncRunResult? result = null;
        try { result = await f.Runner().RecoverAsync(); }
        catch (IOException) when (reject) { }
        var resumed = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(resumed.Allocations.Single().AllocationId == allocation &&
                resumed.Attempts.Any(a => a.Status == SyncAuthorityAttemptStatus.VerifiedNotApplied),
            "Reopened runner replaced allocation or lost VerifiedNotApplied history.");
        if (reject)
            Require(resumed.Status != SyncAuthorityOperationStatus.Completed && f.Wire.Uploads.Count == 0 &&
                    f.Wire.Read("/Pilot/file.txt") == Before, "Rejected runner metadata changed remote bytes.");
        else
        {
            Require(resumed.Status == SyncAuthorityOperationStatus.Completed && result is not null &&
                    result.Decisions.Any(d => d.Kind == SyncDecisionKind.Uploaded),
                "Production runner did not surface completed upload.");
            Require(resumed.Attempts.Count == 3 && resumed.Attempts.Count(a =>
                    a.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) == 2,
                "Runner did not retain three terminal attempts.");
            f.AssertRemoteReplacement();
            await f.Runner().RecoverAsync();
            f.AssertRemoteReplacement();
        }
        Console.WriteLine($"PASS TC2 runner-reopen/{reject}: same operation/allocation, real HTTP predecessor bytes, terminal result, no duplicate effects");
    }

    private static async Task ConflictAsync(SyncAuthorityConflictResolutionKind kind, bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, After);
        f.Wire.Put("/Pilot/file.txt", "Dropbox conflicting predecessor");
        await f.Observer().ObserveAsync("monitor");
        var conflictPlan = (await f.Planner().PlanAsync(f.Pair.Id)).Single();
        var conflict = await f.Db.LoadConflictAsync(conflictPlan.ConflictId!);
        // Exercise the actual UI content-preview runtime and its error presentation.
        await f.PreviewAsync(reject);
        f.Wire.RejectDownloads = false;
        var coordinator = f.Conflicts();
        var plan = await coordinator.PlanAsync(conflict.ConflictId, conflict.ConflictGeneration, kind);
        if (kind != SyncAuthorityConflictResolutionKind.KeepDropbox)
        {
            // Let PreserveBoth finish its legitimate visible prerequisite first.
            if (plan.PreserveOperationId is not null)
            {
                // Match the real coordinator's sealed-plan admission before its first prerequisite.
                // Directly leasing a merely planned group is correctly rejected by production.
                await f.Db.MarkConflictResolutionExecutingAsync(conflict.ConflictId, plan.OperationGroupId);
                await new SyncAuthorityPreserveConflictCopyExecutor(f.Pair, f.Client, f.Db)
                    .ExecuteAsync(plan.PreserveOperationId, "fixture-visible-prerequisite");
            }
            f.Wire.FailNextDownload = true;
            try { await coordinator.ExecuteAsync(plan, "fixture-interrupt"); }
            catch (IOException) { }
            var interrupted = await f.Db.LoadOperationAsync(plan.OperationId);
            Require(interrupted.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                    interrupted.Allocations.Single().Status == SyncAuthorityAllocationStatus.Creating &&
                    (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status != "resolved",
                "Conflict did not retain interrupted recovery before resolution.");
            var allocation = interrupted.Allocations.Single().AllocationId;
            var reconciled = await coordinator.ExecuteAsync(plan, "fixture-supported-reconcile");
            Require(reconciled.Status == SyncAuthorityOperationStatus.Ready &&
                    (await f.Db.LoadOperationAsync(plan.OperationId)).Allocations.Single().AllocationId == allocation,
                "Conflict coordinator did not reconcile EffectIssuing to Ready with same allocation.");
        }
        f.Wire.RejectDownloads = reject;
        SyncAuthorityExecutionResult? result = null;
        try { result = await coordinator.ExecuteAsync(plan, "fixture-resume"); }
        catch (IOException) when (reject) { }
        if (reject)
            Require((await f.Db.LoadConflictAsync(conflict.ConflictId)).Status != "resolved" &&
                    f.Wire.Read("/Pilot/file.txt") == "Dropbox conflicting predecessor" &&
                    await File.ReadAllTextAsync(f.LocalPath) == After && f.Wire.RecoveryUploads.Count == 0,
                "Rejected conflict download changed/resolved either version.");
        else
        {
            Require(result?.Status == SyncAuthorityOperationStatus.Completed &&
                    (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "resolved",
                "Conflict did not resolve only after completion.");
            if (kind == SyncAuthorityConflictResolutionKind.KeepDropbox)
            {
                Require(await File.ReadAllTextAsync(f.LocalPath) == "Dropbox conflicting predecessor",
                    "KeepDropbox did not promote exact bytes.");
                await f.AssertLocalRecoveryAsync(After);
            }
            else f.AssertRemoteReplacement("Dropbox conflicting predecessor");
        }
        if (plan.ConflictCopyPath is not null)
            Require(f.Wire.Read("/Pilot/" + plan.ConflictCopyPath) == "Dropbox conflicting predecessor" &&
                    f.Wire.Uploads.Count(u => u.Path == "/Pilot/" + plan.ConflictCopyPath) == 1,
                "PreserveBoth lost or duplicated its visible loser.");
        Console.WriteLine($"PASS TC1/TC3 conflict/{kind}/{reject}: production coordinator, preview, retention and exact completion semantics");
    }

    private static async Task PairRetirementKnownNoEffectAsync(string mode, bool replace = false,
        bool retireBeforeBind = false)
    {
        await using var f = await Fixture.CreateAsync();
        f.Wire.Put("/Pilot/new.txt", After);
        var operation = await f.PlanOperationAsync("new.txt");
        if (mode != "ready")
        {
            f.Wire.FailNextDownload = mode != "applied";
            SyncAuthorityCreateLocalExecutor.PhaseForTest = phase =>
            {
                if (mode == "applied" && phase == "target-promoted-before-receipt")
                    throw new IOException("Native promoted target before receipt fixture.");
            };
            try
            {
                try
                {
                    await new SyncAuthorityCreateLocalExecutor(f.Pair, f.Client, f.Db)
                        .ExecuteAsync(operation.OperationId, "retirement-native-create");
                    throw new Exception("The actual interrupted-create boundary was not reached.");
                }
                catch (IOException) { }
            }
            finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
        }
        if (mode is "not-applied" or "active-lease")
        {
            await new SyncAuthorityCreateLocalReconciler(f.Pair, f.Db)
                .ReconcileAsync(operation.OperationId, "retirement-native-reconcile");
            Require(await f.Db.IsKnownNoEffectRefusalAsync(operation.OperationId, default),
                "Real reconciliation did not establish the exact shared no-effect classification.");
        }
        if (mode == "active-lease")
            await f.Db.AcquireOperationLeaseAsync(operation.OperationId, "retirement-active-holder",
                TimeSpan.FromMinutes(5));
        var before = await f.Db.LoadOperationAsync(operation.OperationId);
        var retained = JsonSerializer.Serialize(before);
        var downloads = f.Wire.DownloadRevisions.Count;
        Exception? refusal = null;
        try
        {
            await f.WithHostAsync(async host =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Native pair retirement/replacement requires Windows.");
                if (!replace) return await host.RetirePairAsync(f.Pair.Id);
                if (retireBeforeBind) await host.RetirePairAsync(f.Pair.Id);
                var replacementRoot = Directory.CreateDirectory(Path.Combine(f.Root, "replacement-local")).FullName;
                var result = await host.BindPairAsync(new SyncPairBindingRequest(retireBeforeBind ? null : f.Pair.Id, f.Pair.Name,
                    replacementRoot, new DropboxFolderEntry("Pilot", "/Pilot", f.Pair.DropboxFolderId),
                    new DropboxFolderEntry("Recovery", f.Pair.RecoveryRoot!.RootPath, f.Pair.RecoveryRoot.RootFolderId),
                    Enabled: true, Priority: 0, PropagateDeletions: f.Pair.PropagateDeletions,
                    ExpectedPolicyRevision: retireBeforeBind ? null : f.Pair.PolicyRevision,
                    ConnectionId: f.Pair.ConnectionId));
                Require(result.Pair.Id != f.Pair.Id && result.Pair.LocalRoot == replacementRoot &&
                        result.Pair.InitialReviewApproved == false && !Directory.EnumerateFileSystemEntries(replacementRoot).Any(),
                    "Replacement inherited the retired pair's identity/review or populated files without consent.");
                return result.PairSet;
            });
        }
        catch (InvalidOperationException error) { refusal = error; }
        Require((refusal is null) == (mode == "not-applied"),
            "Pair " + (retireBeforeBind ? "recreation after retirement" : replace ? "replacement" : "retirement") +
            " did not distinguish proven no-effect history from pending authority: " + mode +
            "; exact refusal: " + refusal?.Message);
        Require(JsonSerializer.Serialize(await f.Db.LoadOperationAsync(operation.OperationId)) == retained &&
                f.Wire.DownloadRevisions.Count == downloads && f.Wire.Uploads.Count == 0 &&
                await File.ReadAllTextAsync(f.LocalPath) == Before,
            "Retirement changed immutable failure history, replayed an effect or changed another file.");
        Require((await f.Db.LoadPairStateAsync(f.Pair.Id) == "retired") == (mode == "not-applied"),
            "The rejected retirement changed pair state or accepted retirement remained active.");
        var currentPending = mode == "not-applied" ? 0 : 1;
        Require(await f.Db.LoadPendingQueueCountAsync() == currentPending &&
                (await f.Db.LoadQueueProjectionAsync()).Count == currentPending &&
                !(await f.Db.LoadPairIdsNeedingAttentionAsync()).Contains(f.Pair.Id) ==
                    (mode == "not-applied" || before.Status is not (SyncAuthorityOperationStatus.Blocked or
                        SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.CleanupPending)),
            "Current queue/count/attention either leaked retired history or hid active pending authority.");
        Require(File.Exists(Path.Combine(f.Pair.LocalRoot, "new.txt")) == (mode == "applied"),
            "Pair retirement changed the actual native interrupted target.");
        Console.WriteLine("PASS native pair " + (retireBeforeBind ? "recreation-after-retirement/" : replace ? "replacement/" : "retirement/") + mode +
            ": actual create/reconcile/host caller; exact history and user bytes preserved, no effect replay");
    }

    private static async Task PairReplacementAtomicRejectionAsync(string mode)
    {
        await using var f = await Fixture.CreateAsync();
        var root = Directory.CreateDirectory(Path.Combine(f.Root, "replacement-local")).FullName;
        var oldDefinition = JsonSerializer.Serialize((await f.Db.LoadPairSetAsync()).Pairs.Single());
        var oldBytes = await File.ReadAllTextAsync(f.LocalPath);
        var downloads = f.Wire.DownloadRevisions.Count;
        if (mode == "foreign-overlap")
        {
            using var lease = PilotDirectoryLease.Open(root);
            await f.Db.RegisterPairAsync("foreign-active-pair", 1, new SyncAuthorityPairConfiguration(
                "recoverable", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                RecursiveMode: true, AutomaticPruning: false, RecoveryRetentionDays: 30));
            await f.Db.RegisterLocalRootBindingAsync("foreign-physical-root", "foreign-active-pair", 1,
                "installation-correction", f.VolumeId, lease.Identity.VolumeSerialNumber.ToString("x8"),
                lease.Identity.FileId.ToString("x16"), root);
        }
        if (mode == "publication-drift")
            SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = boundary =>
            {
                if (boundary == "allocation-authorized-before-filesystem-effect")
                    f.Db.RetirePairDefinitionAsync(f.Pair.Id).GetAwaiter().GetResult();
            };
        if (mode == "late-root-overlap")
            SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = boundary =>
            {
                if (boundary != "allocation-authorized-before-filesystem-effect") return;
                using var lease = PilotDirectoryLease.Open(root);
                f.Db.RegisterPairAsync("late-foreign-pair", 1, new SyncAuthorityPairConfiguration(
                    "recoverable", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                    RecursiveMode: true, AutomaticPruning: false, RecoveryRetentionDays: 30)).GetAwaiter().GetResult();
                f.Db.RegisterLocalRootBindingAsync("late-foreign-physical-root", "late-foreign-pair", 1,
                    "installation-correction", f.VolumeId, lease.Identity.VolumeSerialNumber.ToString("x8"),
                    lease.Identity.FileId.ToString("x16"), root).GetAwaiter().GetResult();
            };
        Exception? failure = null;
        try
        {
            await f.WithHostAsync(async host =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Native pair replacement requires Windows.");
                return await host.BindPairAsync(new SyncPairBindingRequest(f.Pair.Id,
                f.Pair.Name, root, new DropboxFolderEntry("Pilot", "/Pilot", f.Pair.DropboxFolderId),
                new DropboxFolderEntry("Recovery", f.Pair.RecoveryRoot!.RootPath, f.Pair.RecoveryRoot.RootFolderId),
                Enabled: true, Priority: 0, PropagateDeletions: true,
                ExpectedPolicyRevision: mode == "stale-revision" ? f.Pair.PolicyRevision + 1 : f.Pair.PolicyRevision,
                ConnectionId: f.Pair.ConnectionId));
            });
        }
        catch (InvalidOperationException error) { failure = error; }
        finally { SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = null; }
        Require(failure is not null, "Unsafe replacement was published: " + mode);
        Require(await File.ReadAllTextAsync(f.LocalPath) == oldBytes &&
                !Directory.EnumerateFileSystemEntries(root).Any() &&
                f.Wire.DownloadRevisions.Count == downloads && f.Wire.Uploads.Count == 0,
            "Rejected replacement touched user bytes or replayed a transfer: " + mode);
        if (mode != "publication-drift")
        {
            Require(JsonSerializer.Serialize((await f.Db.LoadPairSetAsync()).Pairs.Single(pair => pair.Id == f.Pair.Id)) == oldDefinition &&
                    await f.Db.LoadPairStateAsync(f.Pair.Id) == "active",
                "Rejected replacement changed the source definition or state: " + mode);
            await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = f.Db.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await read.OpenAsync();
            await using var check = read.CreateCommand();
            check.CommandText = "SELECT count(*) FROM root_binding WHERE root_binding_id IN ($local,$remote) AND pair_id=$pair AND state='active';";
            check.Parameters.AddWithValue("$local", f.LocalRootBindingId);
            check.Parameters.AddWithValue("$remote", f.RemoteRootBindingId);
            check.Parameters.AddWithValue("$pair", f.Pair.Id);
            Require(Convert.ToInt64(await check.ExecuteScalarAsync()) == 2,
                "A rejected publication left the old live root authority out of scope: " + mode);
        }
        else Require((await f.Db.LoadPairSetAsync()).Pairs.Count == 0,
            "Publication adopted a candidate after its source retired.");
        Console.WriteLine("PASS native pair replacement/" + mode +
            ": real host and control allocator; refusal preserves the source's current authority and user bytes, no candidate publication or replay");
    }

    private static async Task LocalOrDeleteAsync(string mode, bool reject)
    {
        await using var f = await Fixture.CreateAsync();
        if (mode == "create") f.Wire.Put("/Pilot/new.txt", After);
        else if (mode == "update") f.Wire.Put("/Pilot/file.txt", After);
        else File.Delete(f.LocalPath);
        var operation = await f.PlanOperationAsync(mode == "create" ? "new.txt" : "file.txt");
        f.Wire.RejectDownloads = reject;
        var execution = await f.Runner().ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(
            f.Pair.Id, [], [new(mode == "create" ? "new.txt" : "file.txt", operation)]));
        var result = new SyncRunResult(f.Pair.Id, execution.Decisions);
        var current = await f.Db.LoadOperationAsync(operation.OperationId);
        Console.WriteLine("RECOVERY-CONSUMER " + mode + "/" + reject + ": " + JsonSerializer.Serialize(new
        {
            current.Status,
            current.Attempts,
            current.Allocations,
            result.Decisions
        }));
        Require((current.Status == SyncAuthorityOperationStatus.Completed) == !reject,
            "Wrong consumer completion: " + mode);
        if (mode == "delete")
        {
            Require(f.Wire.Deletes == (reject ? 0 : 1), "Rejected metadata reached deletion.");
            if (!reject) f.AssertRecoveryBytes(Before);
        }
        else if (mode == "create")
        {
            var path = Path.Combine(f.Pair.LocalRoot, "new.txt");
            Require(reject ? !File.Exists(path) : await File.ReadAllTextAsync(path) == After,
                "Create-local promoted rejected/wrong bytes.");
        }
        else
        {
            Require(await File.ReadAllTextAsync(f.LocalPath) == (reject ? Before : After),
                "Update-local promoted rejected/wrong bytes.");
            if (!reject) await f.AssertLocalRecoveryAsync(Before);
        }
        Console.WriteLine($"PASS TC3 ordinary/{mode}/{reject}: actual runner decision, exact bytes, no unsafe promotion/deletion");
    }

    private static async Task HistoricalAsync(string mode)
    {
        await using var f = await Fixture.CreateAsync();
        var expected = (await f.Client.ListEntriesAsync()).Single();
        if (mode == "deleted") f.Wire.Remove("/Pilot/file.txt");
        if (mode == "new-revision") f.Wire.Put("/Pilot/file.txt", After);
        if (mode == "moved-before") f.Wire.MoveOutside();
        if (mode == "moved-after") f.Wire.MoveAfterDownload = true;
        Exception? error = null;
        try { await f.Client.CopyRevisionToRecoveryAsync(expected, f.Pair.RecoveryRoot!, "historical-proof-0001"); }
        catch (Exception e) when (e is IOException or InvalidDataException) { error = e; }
        Require((error is not null) == mode.StartsWith("moved", StringComparison.Ordinal),
            "Historical membership result differs from retained/deleted/moved contract.");
        if (error is null) f.AssertRecoveryBytes(Before);
        else Require(f.Wire.Uploads.Count == 0 && f.Wire.Deletes == 0,
            "Historical membership rejection reached a mutation.");
        Console.WriteLine($"PASS TC4 historical/{mode}: pathless response, real before/after membership, exact retained bytes or zero mutations");
    }

    private static async Task PlaceholderAsync(bool reject)
    {
        await using var f = await Fixture.CreateAsync(placeholder: true);
        f.Wire.Put("/Pilot/file.txt", After);
        var op = await f.PlanOperationAsync();
        f.Wire.RejectRevision = reject ? f.Wire.History.Values.First(x => Encoding.UTF8.GetString(x.Bytes) == Before).Revision : null;
        SyncRunResult? result = null;
        try { result = await f.Runner().RecoverAsync(); }
        catch (IOException) when (reject) { }
        var current = await f.Db.LoadOperationAsync(op.OperationId);
        Require((current.Status == SyncAuthorityOperationStatus.Completed) == !reject,
            "Placeholder consumer completion mismatch.");
        Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(f.LocalPath, out var info) && info is not null,
            "Placeholder identity was lost.");
        Require(info!.Identity.ContentHash == DropboxContentHash.Compute(Encoding.UTF8.GetBytes(reject ? Before : After)),
            "Rejected historical predecessor download promoted placeholder successor.");
        if (reject) Require(f.Wire.RecoveryUploads.Count == 0, "Rejected predecessor was archived.");
        else f.AssertRecoveryBytes(Before);
        Require(f.Wire.DownloadRevisions.Contains(f.Wire.History.Values.First(x =>
                    Encoding.UTF8.GetString(x.Bytes) == Before).Revision),
            "Placeholder consumer never reached historical CopyRevisionToRecoveryAsync.");
        if (!reject)
        {
            await f.AssertStablePlaceholderPlanAsync();
            var firstName = current.Allocations.Single(a => a.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).DeterministicName;
            f.Wire.Put("/Pilot/file.txt", "third cloud version");
            var next = await f.PlanOperationAsync();
            var nextName = next.Allocations.Single(a => a.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).DeterministicName;
            Require(firstName.StartsWith("recovery-", StringComparison.Ordinal) && nextName != firstName,
                "Successive placeholder predecessors were allocated the same remote recovery name.");
            SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
            {
                if (phase == "cloud-placeholder-updated-before-receipt")
                    throw new IOException("Fixture: native successor applied before receipt.");
            };
            try { await f.Runner().RecoverAsync(); }
            catch (IOException) { }
            finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
            Require((await f.Db.LoadOperationAsync(next.OperationId)).Status == SyncAuthorityOperationStatus.EffectIssuing,
                "Native lost-receipt fixture did not retain an interrupted update.");
            await f.Runner().RecoverAsync();
            Require((await f.Db.LoadOperationAsync(next.OperationId)).Status == SyncAuthorityOperationStatus.Completed &&
                    f.Wire.RecoveryUploads.Count == 2 &&
                    f.Wire.RecoveryUploads.Select(u => u.Path).Distinct(StringComparer.Ordinal).Count() == 2 &&
                    f.Wire.RecoveryUploads.Any(u => Encoding.UTF8.GetString(u.Bytes) == Before) &&
                    f.Wire.RecoveryUploads.Any(u => Encoding.UTF8.GetString(u.Bytes) == After),
                "A successive placeholder update lost or collided with an earlier predecessor.");
            var completed = await f.Db.LoadOperationAsync(next.OperationId);
            Require(completed.Allocations.Where(a => a.Kind != SyncAuthorityAllocationKind.LocalRecoveryFile)
                    .All(a => a.Status == SyncAuthorityAllocationStatus.Authorized && a.BoundIdentity is null),
                "Native completion fabricated a created or released ordinary local stage.");
            await f.AssertStablePlaceholderPlanAsync();
        }
        Console.WriteLine($"PASS TC3 placeholder-predecessor/{reject}: native placeholder, actual historical recovery branch, exact bytes and identity");
    }

    private static async Task PlaceholderCreateAsync(bool interrupted)
    {
        await using var f = await Fixture.CreateAsync(placeholder: true);
        const string path = "created.txt";
        f.Wire.Put("/Pilot/" + path, "cloud-created-bytes");
        var operation = await f.PlanOperationAsync(path);
        if (interrupted)
        {
            SyncAuthorityCreateLocalExecutor.PhaseForTest = phase =>
            {
                if (phase == "cloud-placeholder-created-before-receipt")
                    throw new IOException("Fixture: created placeholder response was lost.");
            };
            try { await f.Runner().RecoverAsync(); }
            catch (IOException) { }
            finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
            Require((await f.Db.LoadOperationAsync(operation.OperationId)).Status ==
                    SyncAuthorityOperationStatus.EffectIssuing,
                "Native placeholder-create interruption was not retained before its receipt.");
        }
        await f.Runner().RecoverAsync();
        var completed = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(completed.Status == SyncAuthorityOperationStatus.Completed &&
                completed.Allocations.All(item =>
                    item.Status == SyncAuthorityAllocationStatus.Authorized && item.BoundIdentity is null) &&
                WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                    Path.Combine(f.Pair.LocalRoot, path), out var placeholder) && placeholder is not null &&
                placeholder.State.Availability == SyncCloudFileAvailability.OnlineOnly &&
                f.Wire.Uploads.Count == 0 && f.Wire.RecoveryUploads.Count == 0,
            "Cloud-only placeholder create did not complete exactly without a remote write.");
        await f.AssertStablePlaceholderPlanAsync(path);
        Console.WriteLine($"PASS TC3 placeholder-create/{interrupted}: normal or lost-receipt completion reobserves unchanged");
    }

    internal static async Task RunNestedLocalPathCorrectionAsync()
    {
        var witnesses = new List<string> { "TC-NESTED-01" };
        await AuthorityHostNestedAsync(false); witnesses.Add("TC-NESTED-02.a");
        await AuthorityHostNestedAsync(true); witnesses.Add("TC-NESTED-02.b");
        await AuthorityInterruptedNestedAsync("local-update-predecessor-retention-attempt-started",
            "TC-NESTED-03.a"); witnesses.Add("TC-NESTED-03.a");
        await AuthorityInterruptedNestedAsync("local-update-predecessor-retained-before-receipt",
            "TC-NESTED-03.b"); witnesses.Add("TC-NESTED-03.b");
        await AuthorityInterruptedNestedAsync("local-update-stage-promoted-before-receipt",
            "TC-NESTED-03.c"); witnesses.Add("TC-NESTED-03.c");
        await AuthorityContainmentNestedAsync();
        witnesses.Add("TC-NESTED-04.a"); witnesses.Add("TC-NESTED-04.c");
        await AuthorityAmbiguityNestedAsync(); witnesses.Add("TC-NESTED-03.d-both-present");
        foreach (var mode in new[] { "both-absent", "wrong-target", "wrong-stage", "wrong-recovery" })
        {
            await AuthorityCorruptReconciliationNestedAsync(mode);
            witnesses.Add("TC-NESTED-03.d-" + mode);
        }
        await AuthorityConflictNestedAsync(false); witnesses.Add("TC-NESTED-05.a");
        await AuthorityConflictNestedAsync(true); witnesses.Add("TC-NESTED-05.b");
        await AuthorityConflictContainmentNestedAsync(); witnesses.Add("TC-NESTED-05.b-path");
        await LegacyNestedAsync(); witnesses.Add("TC-NESTED-06.a");
        await LegacyInterruptedNestedAsync(); witnesses.Add("TC-NESTED-06.b");
        await LegacyConflictNestedAsync(); witnesses.Add("TC-NESTED-06.c");
        await LegacySafeCreateRecoveryNestedAsync(); witnesses.Add("TC-NESTED-06.d");
        await LegacyContainmentNestedAsync(); witnesses.Add("TC-NESTED-04.b");
        await DropboxNestedParentResponseShapeAsync(); witnesses.Add("TC-NESTED-07.parent-response");
        witnesses.Add("TC-NESTED-07.parent-response.id-mismatch-fallback");
        Require(witnesses.Count == 23 && witnesses.Distinct(StringComparer.Ordinal).Count() == 23,
            "The bounded nested-path witness registry is incomplete or duplicated.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            selector = "--nested-local-update-path",
            executedCaseCount = witnesses.Count,
            witnessIds = witnesses,
            canonicalSeparatorControl = "TC-NESTED-04.c",
            safeCreateRecoveryDisposition = "unchanged production recovery uses ResolveContainedPath; uncalled DownloadAsync helper excluded from production proof"
        }));
    }

    private static async Task DropboxNestedParentResponseShapeAsync()
    {
        var wire = new WireStore { OmitCreateFolderTag = true };
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot",
            Fixture.Connection, "id:pilot-folder");
        await client.VerifyRootIdentityAsync();
        var bytes = Encoding.UTF8.GetBytes("provider folder response shape");
        await using var source = new MemoryStream(bytes, writable: false);
        var hash = DropboxContentHash.Compute(bytes);
        await client.UploadCreateOnlyAsync("focused/metadata-path.txt", source, hash);
        Require(wire.FoldersCreated == 1 &&
                wire.Folders.TryGetValue("/Pilot/focused", out var folderId) &&
                !string.IsNullOrWhiteSpace(folderId) &&
                wire.Files.TryGetValue("/Pilot/focused/metadata-path.txt", out var created) &&
                created.Bytes.SequenceEqual(bytes),
            "Dropbox nested-parent creation did not accept the provider's tagless create acknowledgement and exact readback.");

        var rejectedWire = new WireStore
        {
            OmitCreateFolderTag = true,
            CreatedFolderReadbackNameOverride = "foreign-folder"
        };
        using var rejectedHttp = new HttpClient(rejectedWire);
        var rejectedClient = new DropboxPilotClient(rejectedHttp, new FixedTokenSource(), "/Pilot",
            Fixture.Connection, "id:pilot-folder");
        await rejectedClient.VerifyRootIdentityAsync();
        await using var rejectedSource = new MemoryStream(bytes, writable: false);
        DropboxEffectUncertainException? rejection = null;
        try
        {
            await rejectedClient.UploadCreateOnlyAsync("focused/metadata-path.txt", rejectedSource, hash);
        }
        catch (DropboxEffectUncertainException exception)
        {
            rejection = exception;
        }
        Require(rejection is not null && rejectedWire.FoldersCreated == 1 && rejectedWire.Uploads.Count == 0,
            "A mismatched nested-parent readback reached the file upload effect.");

        var rejectedIdWire = new WireStore
        {
            OmitCreateFolderTag = true,
            CreatedFolderReadbackIdOverride = "id:foreign-folder"
        };
        using var rejectedIdHttp = new HttpClient(rejectedIdWire);
        var rejectedIdClient = new DropboxPilotClient(rejectedIdHttp, new FixedTokenSource(), "/Pilot",
            Fixture.Connection, "id:pilot-folder");
        await rejectedIdClient.VerifyRootIdentityAsync();
        await using var rejectedIdSource = new MemoryStream(bytes, writable: false);
        DropboxEffectUncertainException? idRejection = null;
        try
        {
            await rejectedIdClient.UploadCreateOnlyAsync("focused/metadata-path.txt", rejectedIdSource, hash);
        }
        catch (DropboxEffectUncertainException exception)
        {
            idRejection = exception;
        }
        Require(idRejection is not null && rejectedIdWire.FoldersCreated == 1 && rejectedIdWire.Uploads.Count == 0,
            "An ID-mismatched nested-parent readback fell through to a name-only fallback and reached upload.");
        Console.WriteLine("PASS TC-NESTED-07.parent-response: tagless acknowledgement succeeds after exact readback; name- or ID-mismatched readback rejects before upload");
    }

    private static async Task AuthorityHostNestedAsync(bool substituteAfterPlan)
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        f.Wire.Put(f.RemotePath, After);
        var operation = await f.PlanOperationAsync();
        string? preserved = null;
        if (substituteAfterPlan)
        {
            preserved = Path.Combine(f.Root, "preserved-predecessor.txt");
            File.Move(f.LocalPath, preserved);
            await File.WriteAllTextAsync(f.LocalPath, Before);
        }
        var batch = await f.WithHostAsync(host => host.RunPairsAsync([f.Pair],
            substituteAfterPlan ? "Startup" : "Manual"));
        var item = batch.Items.Single();
        var current = await f.Db.LoadOperationAsync(operation.OperationId);
        if (substituteAfterPlan)
        {
            Require(item.FailureType is not null || item.Result?.Blocked > 0 || current.Status != SyncAuthorityOperationStatus.Completed,
                "The host presented a substituted nested predecessor as completed.");
            Require(await File.ReadAllTextAsync(f.LocalPath) == Before &&
                    await File.ReadAllTextAsync(preserved!) == Before &&
                    (await f.Db.LoadRecoveryArtifactsAsync()).All(entry => entry.OperationId != operation.OperationId),
                "The host changed or retained the substituted nested predecessor.");
        }
        else
        {
            var transaction = current.Allocations.Single(item =>
                item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
            var stage = current.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
            var recovery = current.Allocations.Single(item =>
                item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
            Require(item.FailureType is null && item.Result?.Downloaded == 1 &&
                    current.Status == SyncAuthorityOperationStatus.Completed &&
                    current.Attempts.Count == 2 &&
                    current.Attempts.All(attempt => attempt.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) &&
                    transaction.Status == SyncAuthorityAllocationStatus.Verified &&
                    transaction.BoundIdentity is not null && transaction.ContentHash is not null &&
                    stage.Status == SyncAuthorityAllocationStatus.Released &&
                    stage.BoundIdentity is not null && stage.ContentHash is not null && stage.Size == After.Length &&
                    recovery.Status == SyncAuthorityAllocationStatus.Verified &&
                    recovery.BoundIdentity is not null && recovery.ContentHash is not null && recovery.Size == Before.Length &&
                    await File.ReadAllTextAsync(f.LocalPath) == After,
                "The production host did not complete the legitimate nested UpdateLocal operation: " +
                JsonSerializer.Serialize(new { item.FailureType, item.FailureMessage, item.Result,
                    current.Status, local = await File.ReadAllTextAsync(f.LocalPath) }));
            await f.AssertLocalRecoveryAsync(Before);
            var unchanged = await f.WithHostAsync(host => host.RunPairsAsync([f.Pair], "Automatic"));
            Require(unchanged.Items.Single().FailureType is null &&
                    unchanged.Items.Single().Result is { Downloaded: 0, Uploaded: 0, Conflicts: 0, Blocked: 0 } &&
                    (await f.Db.LoadOperationAsync(operation.OperationId)).Attempts.Count == current.Attempts.Count,
                "Automatic/background re-observation replayed the completed nested operation.");
        }
        Console.WriteLine($"PASS {(substituteAfterPlan ? "TC-NESTED-02.b" : "TC-NESTED-02.a")}: production host manual/automatic nested update");
    }

    private static async Task AuthorityInterruptedNestedAsync(string phase, string witness)
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        var remote = f.Wire.Put(f.RemotePath, After);
        var operation = await f.PlanOperationAsync();
        var allocationIds = operation.Allocations.Select(item => item.AllocationId).Order().ToArray();
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = observed =>
        {
            if (observed == phase) throw new IOException("Fixture interruption at " + phase);
        };
        try { await f.Runner().RecoverAsync(); }
        catch (IOException) { }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        var interrupted = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(interrupted.Status == SyncAuthorityOperationStatus.EffectIssuing,
            "The nested interruption did not retain EffectIssuing authority: " + phase);
        f.ReopenAuthority();
        var recovered = await f.Runner().RecoverAsync();
        var completed = await f.Db.LoadOperationAsync(operation.OperationId);
        var expectedAttempts = phase == "local-update-predecessor-retention-attempt-started" ? 3 : 2;
        Require(completed.Status == SyncAuthorityOperationStatus.Completed && recovered.Downloaded == 1 &&
                completed.Allocations.Select(item => item.AllocationId).Order().SequenceEqual(allocationIds) &&
                completed.Attempts.Count == expectedAttempts &&
                completed.Attempts.All(item => item.Status is SyncAuthorityAttemptStatus.VerifiedSuccess or
                    SyncAuthorityAttemptStatus.VerifiedNotApplied) &&
                (phase != "local-update-predecessor-retention-attempt-started" ||
                 completed.Attempts.Count(item =>
                     item.Status == SyncAuthorityAttemptStatus.VerifiedNotApplied) == 1) &&
                (await f.Db.LoadRecoveryArtifactsAsync()).Count(item =>
                    item.OperationId == operation.OperationId && item.Side == "local") == 1 &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 1 &&
                await File.ReadAllTextAsync(f.LocalPath) == After,
            "The reopened runner did not reconcile the exact nested interruption: " + phase);
        await f.AssertLocalRecoveryAsync(Before);
        var attempts = completed.Attempts.Count;
        await f.Runner().RecoverAsync();
        Require((await f.Db.LoadOperationAsync(operation.OperationId)).Attempts.Count == attempts,
            "The nested reconciliation replayed an already completed effect: " + phase);
        Console.WriteLine($"PASS {witness}: reopened runner reconciled {phase}");
    }

    private static async Task AuthorityContainmentNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        f.Wire.Put(f.RemotePath, After);
        var operation = await f.PlanOperationAsync();
        var originalDirectory = Path.GetDirectoryName(f.LocalPath)!;
        var outsideDirectory = Path.Combine(f.Root, "outside-focused");
        Directory.Move(originalDirectory, outsideDirectory);
        CreateDirectoryJunction(originalDirectory, outsideDirectory);
        try
        {
            Exception? rejected = null;
            try { await f.Runner().RecoverAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = error; }
            Require(rejected is not null && await File.ReadAllTextAsync(Path.Combine(outsideDirectory,
                        "metadata-path.txt")) == Before &&
                    (await f.Db.LoadOperationAsync(operation.OperationId)).Status != SyncAuthorityOperationStatus.Completed &&
                    (await f.Db.LoadRecoveryArtifactsAsync()).All(item => item.OperationId != operation.OperationId),
                "A same-identity ancestor reparse escaped the authority root or changed the outside object.");
        }
        finally
        {
            if (Directory.Exists(originalDirectory)) Directory.Delete(originalDirectory);
        }
        Console.WriteLine("PASS TC-NESTED-04.a/04.c: canonical separators succeed elsewhere; same-identity ancestor reparse rejects with zero effect");
    }

    private static async Task AuthorityAmbiguityNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        f.Wire.Put(f.RemotePath, After);
        var operation = await f.PlanOperationAsync();
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-update-stage-promoted-before-receipt")
                throw new IOException("Fixture promoted-stage interruption.");
        };
        try { await f.Runner().RecoverAsync(); }
        catch (IOException) { }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        var interrupted = await f.Db.LoadOperationAsync(operation.OperationId);
        var transaction = interrupted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stage = interrupted.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        var control = f.Pair.ControlNamespace!.Local.RootPath;
        var transactionPath = Path.Combine(control, transaction.DeterministicName);
        var stagePath = Path.Combine(transactionPath, stage.DeterministicName);
        Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
        await File.WriteAllTextAsync(stagePath, "ambiguous-extra-stage");
        f.ReopenAuthority();
        var result = await f.Runner().RecoverAsync();
        var blocked = await f.Db.LoadOperationAsync(operation.OperationId);
        Require(result.Blocked == 1 && blocked.Status == SyncAuthorityOperationStatus.Blocked &&
                await File.ReadAllTextAsync(f.LocalPath) == After && File.Exists(stagePath),
            "The both-present nested ambiguity was not retained and blocked.");
        Console.WriteLine("PASS TC-NESTED-03.d: promoted target plus unexpected stage remains visibly blocked and preserved");
    }

    private static async Task AuthorityCorruptReconciliationNestedAsync(string mode)
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        f.Wire.Put(f.RemotePath, After);
        var operation = await f.PlanOperationAsync();
        var phase = mode is "both-absent" or "wrong-target"
            ? "local-update-stage-promoted-before-receipt"
            : "local-update-predecessor-retained-before-receipt";
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = observed =>
        {
            if (observed == phase) throw new IOException("Fixture interruption for " + mode);
        };
        try { await f.Runner().RecoverAsync(); }
        catch (IOException) { }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }

        var interrupted = await f.Db.LoadOperationAsync(operation.OperationId);
        var transaction = interrupted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stage = interrupted.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        var recovery = interrupted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        var transactionPath = Path.Combine(f.Pair.ControlNamespace!.Local.RootPath,
            transaction.DeterministicName);
        var stagePath = Path.Combine(transactionPath, stage.DeterministicName);
        var recoveryPath = Path.Combine(transactionPath, recovery.DeterministicName);
        if (File.Exists(stagePath))
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath, f.LocalPath);
        if (File.Exists(recoveryPath))
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(recoveryPath, recoveryPath, f.LocalPath);
        string? preserved = null;
        string? substitutedPath = null;
        LocalArtifactSnapshot? substituted = null;
        LocalArtifactSnapshot? preservedBefore = null;
        switch (mode)
        {
            case "both-absent":
                File.Delete(f.LocalPath);
                break;
            case "wrong-target":
                preserved = Path.Combine(f.Root, "preserved-promoted-target.txt");
                File.Move(f.LocalPath, preserved);
                await File.WriteAllTextAsync(f.LocalPath, After);
                substitutedPath = f.LocalPath;
                substituted = await SnapshotLocalArtifactAsync(substitutedPath);
                break;
            case "wrong-stage":
                preserved = Path.Combine(f.Root, "preserved-stage.txt");
                File.Move(stagePath, preserved);
                await File.WriteAllTextAsync(stagePath, After);
                substitutedPath = stagePath;
                substituted = await SnapshotLocalArtifactAsync(substitutedPath);
                break;
            case "wrong-recovery":
                preserved = Path.Combine(f.Root, "preserved-recovery.txt");
                File.Move(recoveryPath, preserved);
                await File.WriteAllTextAsync(recoveryPath, Before);
                substitutedPath = recoveryPath;
                substituted = await SnapshotLocalArtifactAsync(substitutedPath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (preserved is not null)
            preservedBefore = await SnapshotLocalArtifactAsync(preserved);
        var downloads = f.Wire.DownloadRevisions.Count;
        f.ReopenAuthority();
        var batch = await f.WithHostAsync(host => host.RunPairsAsync([f.Pair], "Startup recovery"));
        var item = batch.Items.Single();
        var retained = await f.Db.LoadOperationAsync(operation.OperationId);
        var substitutedAfter = substitutedPath is null ? null : await SnapshotLocalArtifactAsync(substitutedPath);
        var preservedAfter = preserved is null ? null : await SnapshotLocalArtifactAsync(preserved);
        Require((retained.Status == SyncAuthorityOperationStatus.Blocked || item.FailureType is not null ||
                 item.Result?.Blocked > 0) &&
                retained.Status != SyncAuthorityOperationStatus.Completed &&
                f.Wire.DownloadRevisions.Count == downloads && f.Wire.Read(f.RemotePath) == After &&
                (preservedBefore is null || preservedAfter == preservedBefore) &&
                (substituted is null || substitutedAfter == substituted),
            "A corrupt or ambiguous nested reconciliation was not visibly rejected without replay: " +
            JsonSerializer.Serialize(new { mode, item.FailureType, item.FailureMessage, item.Result,
                retained.Status, downloads, observedDownloads = f.Wire.DownloadRevisions.Count }));
        Console.WriteLine("PASS TC-NESTED-03.d-" + mode +
                          ": corrupt/ambiguous nested reconciliation remains visible and non-effecting");
    }

    private sealed record LocalArtifactSnapshot(uint VolumeSerialNumber, ulong FileId, long Size, string ContentHash);

    private static async Task<LocalArtifactSnapshot> SnapshotLocalArtifactAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!PilotDirectoryLease.GetFileInformationByHandle(stream.SafeFileHandle, out var information))
            throw new IOException("The focused substituted-artifact identity could not be read.");
        var identity = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        var hash = await DropboxContentHash.ComputeAsync(stream);
        return new LocalArtifactSnapshot(information.VolumeSerialNumber, identity, stream.Length, hash);
    }

    private static async Task AuthorityConflictNestedAsync(bool interrupted)
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        await File.WriteAllTextAsync(f.LocalPath, "local nested loser");
        f.Wire.Put(f.RemotePath, "dropbox nested winner");
        await f.Observer().ObserveAsync("manual");
        var plan = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        var conflict = await f.Db.LoadConflictAsync(plan.ConflictId!);
        if (interrupted)
            SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
            {
                if (phase == "local-update-predecessor-retention-attempt-started")
                    throw new IOException("Fixture conflict interruption.");
            };
        SyncRunResult? result = null;
        try
        {
            result = await f.WithHostAsync(host => host.ResolveConflictCommandAsync(f.Pair, f.RelativePath,
                SyncAuthorityConflictResolutionKind.KeepDropbox, conflict.ConflictId,
                conflict.ConflictGeneration));
        }
        catch (IOException) when (interrupted) { }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        if (interrupted)
        {
            Require((await f.Db.LoadConflictAsync(conflict.ConflictId)).Status != "resolved",
                "The interrupted nested conflict completed before its effect was verified.");
            f.ReopenAuthority();
            result = await f.Runner().RecoverAsync();
        }
        Require(result is not null && result.Recovered + result.Downloaded == 1 &&
                await File.ReadAllTextAsync(f.LocalPath) == "dropbox nested winner" &&
                (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "resolved",
            "Keep Dropbox did not terminally resolve the exact nested conflict: " +
            JsonSerializer.Serialize(new { result,
                status = (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status,
                local = File.Exists(f.LocalPath) ? await File.ReadAllTextAsync(f.LocalPath) : "<absent>" }));
        await f.AssertLocalRecoveryAsync("local nested loser");
        if (!interrupted)
        {
            var retained = (await f.Db.LoadRunHistoryAsync()).First(item => item.Kind == "resolve_conflict" &&
                item.Status == "completed");
            using var terminal = JsonDocument.Parse(retained.ResultJson!);
            Require(terminal.RootElement.GetProperty("files").EnumerateArray().Single()
                    .GetProperty("RelativePath").GetString() == f.RelativePath &&
                    terminal.RootElement.GetProperty("pairs").EnumerateArray().Single()
                    .GetProperty("files").GetArrayLength() == 1,
                "The real conflict command retains its exact per-file result for Runs and retry navigation.");
            var events = await f.Db.LoadActivityProjectionPageAsync(200, 0, null, null, null, f.Pair.Id,
                null, default, null, new SyncActivityQuery(RunId: retained.Id));
            Require(events.Any(item => item.Reason.Contains("losing version must be retained")) &&
                    events.Any(item => item.Reason.Contains("Downloading to verified staging")),
                "The real conflict path retains its exact action and byte-progress stages in the shared log.");
        }
        var artifactCount = (await f.Db.LoadRecoveryArtifactsAsync()).Count;
        Exception? replay = null;
        try { await f.WithHostAsync(host => host.ResolveConflictCommandAsync(f.Pair, f.RelativePath,
            SyncAuthorityConflictResolutionKind.KeepDropbox)); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { replay = error; }
        Require(replay is not null && (await f.Db.LoadRecoveryArtifactsAsync()).Count == artifactCount,
            "The resolved nested conflict replayed or duplicated recovery.");
        Console.WriteLine($"PASS {(interrupted ? "TC-NESTED-05.b" : "TC-NESTED-05.a")}: nested Keep Dropbox completes only after verified effect");
    }

    private static async Task AuthorityConflictBatchAsync(bool stopBeforeSecond)
    {
        await using var f = await Fixture.CreateAsync();
        using var scheduling = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(stopBeforeSecond ? 1 : 2);
        var paths = stopBeforeSecond ? new[] { f.RelativePath, "second.txt", "third.txt" }
            : new[] { f.RelativePath, "second.txt" };
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, path), "local loser " + path);
            f.Wire.Put("/Pilot/" + path, "cloud winner " + path);
        }
        await f.Observer().ObserveAsync("manual");
        var conflicts = (await f.Planner().PlanAsync(f.Pair.Id)).Where(item => paths.Contains(item.RelativePath))
            .ToDictionary(item => item.RelativePath);
        var requests = new List<SyncConflictResolutionRequest>();
        foreach (var path in paths)
        {
            var conflict = await f.Db.LoadConflictAsync(conflicts[path].ConflictId!);
            requests.Add(new(new(conflict.ConflictId, f.Pair.Id, path, conflict.ConflictGeneration),
                SyncAuthorityConflictResolutionKind.KeepDropbox));
        }
        var admissions = 0;
        var activeStarts = 0;
        var overlappingStarts = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var batch = await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(requests,
            cancellationToken: cancel.Token, admissionProgress: text =>
            {
                if (text.StartsWith("Admission: recording the exact command")) admissions++;
                if (stopBeforeSecond && text.StartsWith("Conflict 2 of")) cancel.Cancel();
            }, onProgress: async progress =>
            {
                if (!stopBeforeSecond && progress.Action is { Stage: "Starting" })
                {
                    if (Interlocked.Increment(ref activeStarts) == 2) overlappingStarts.TrySetResult(true);
                    await overlappingStarts.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }));
        var result = batch.Items.Single().Result!;
        Require(admissions == 1 && result.Recovered == (stopBeforeSecond ? 1 : 2),
            "One exact conflict snapshot must have one effect admission and retain every completed target.");
        if (!stopBeforeSecond)
            Require(activeStarts == 2 && overlappingStarts.Task.IsCompletedSuccessfully,
                "Independent native conflict replacements must overlap through the shared production pump.");
        var run = (await f.Db.LoadRunHistoryAsync()).Single(item => item.Kind == "resolve_conflict");
        using (var json = JsonDocument.Parse(run.ResultJson!))
            Require(json.RootElement.GetProperty("pairs").EnumerateArray().Single().GetProperty("files").GetArrayLength() == paths.Length &&
                json.RootElement.GetProperty("resolved").GetInt32() == result.Recovered,
                "The batch terminal row must retain exact completed, interrupted and unattempted paths.");
        Require(await File.ReadAllTextAsync(f.LocalPath) == "cloud winner " + f.RelativePath &&
            (await f.Db.LoadConflictAsync(requests[0].Conflict.ConflictId)).Status == "resolved",
            "The batch did not verify the first selected cloud winner.");
        if (stopBeforeSecond)
            Require(batch.Stopped && result.NotAttempted == 1 && result.NeedsVerification == 1 &&
                await File.ReadAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, "second.txt")) == "local loser second.txt" &&
                await File.ReadAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, "third.txt")) == "local loser third.txt",
                "Cancellation lost the first result or issued a later replacement.");
        var artifacts = (await f.Db.LoadRecoveryArtifactsAsync()).Count;
        Exception? rejected = null;
        try { await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(requests)); }
        catch (InvalidOperationException error) { rejected = error; }
        Require(rejected is not null && (await f.Db.LoadRecoveryArtifactsAsync()).Count == artifacts,
            "A completed selection was recaptured or replayed instead of rejecting the stale exact generation.");
        Console.WriteLine($"PASS conflict batch {(stopBeforeSecond ? "cancel" : "success")}: one admission, retained outcomes and no replay");
    }

    private static async Task AuthorityConflictBatchFinalizationGapAsync()
    {
        await using var f = await Fixture.CreateAsync();
        using var scheduling = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(1);
        var paths = new[] { f.RelativePath, "second.txt" };
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, path), "local loser " + path);
            f.Wire.Put("/Pilot/" + path, "cloud winner " + path);
        }
        await f.Observer().ObserveAsync("manual");
        var plans = (await f.Planner().PlanAsync(f.Pair.Id)).Where(item => paths.Contains(item.RelativePath))
            .ToDictionary(item => item.RelativePath);
        var requests = new List<SyncConflictResolutionRequest>();
        foreach (var path in paths)
        {
            var conflict = await f.Db.LoadConflictAsync(plans[path].ConflictId!);
            requests.Add(new(new(conflict.ConflictId, f.Pair.Id, path, conflict.ConflictGeneration),
                SyncAuthorityConflictResolutionKind.KeepDropbox));
        }
        var changed = false;
        var starts = 0;
        var batch = await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(requests,
            onProgress: async progress =>
            {
                if (progress.Action?.Stage == "Starting") starts++;
                if (progress.Action is not { Stage: "Completed" } action || action.RelativePath != f.RelativePath) return;
                // Input-only fault injection into this isolated native database: the effect and its
                // real receipt are complete, but final conflict readback is no longer resolved.
                // The production reducer, scheduler and file effects are not mocked.
                await using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = f.Db.DatabasePath }.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE conflict SET status='resolving' WHERE conflict_id=$id AND status='resolved';";
                command.Parameters.AddWithValue("$id", requests[0].Conflict.ConflictId);
                Require(await command.ExecuteNonQueryAsync() == 1, "The exact completed-conflict readback fixture was not changed.");
                changed = true;
            }));
        var result = batch.Items.Single().Result!;
        Require(changed && starts == 1 && result.Recovered == 0 && result.NeedsVerification == 1 && result.NotAttempted == 1 &&
            await File.ReadAllTextAsync(f.LocalPath) == "cloud winner " + f.RelativePath &&
            await File.ReadAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, "second.txt")) == "local loser second.txt" &&
            (await f.Db.LoadRecoveryArtifactsAsync()).Count == 1,
            "An unverified conflict finalization admitted another file or discarded completed effect/recovery truth.");
        var run = (await f.Db.LoadRunHistoryAsync()).Single(item => item.Kind == "resolve_conflict");
        using var json = JsonDocument.Parse(run.ResultJson!);
        Require(json.RootElement.GetProperty("resolved").GetInt32() == 0 &&
            json.RootElement.GetProperty("attempted").GetInt32() == 1 &&
            json.RootElement.GetProperty("files").GetArrayLength() == 2 && run.Status == "blocked",
            "The retained run credited a verification-needed result as resolved or lost unattempted paths.");
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = f.Db.DatabasePath }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT operation_group_id FROM conflict_resolution WHERE conflict_id=$id;";
            command.Parameters.AddWithValue("$id", requests[0].Conflict.ConflictId);
            var groupId = (string)(await command.ExecuteScalarAsync())!;
            Require(!await f.Db.MarkStaleUnissuedConflictResolutionAsync(requests[0].Conflict.ConflictId,
                    requests[0].Conflict.Generation, groupId, CancellationToken.None) &&
                (await f.Db.LoadConflictAsync(requests[0].Conflict.ConflictId)).Status == "resolving" &&
                (await f.Db.LoadRecoveryArtifactsAsync()).Count == 1,
                "Stale-choice retirement hid a real completed effect or changed its retained recovery authority.");
        }
        Console.WriteLine("PASS conflict batch finalization gap: exact readback governs admission/success, completed effect retained");
    }

    private static async Task AuthorityConflictInterruptedReadyAsync()
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, "local loser");
        f.Wire.Put(f.RemotePath, "cloud winner");
        await f.Observer().ObserveAsync("manual");
        var planned = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        var conflict = await f.Db.LoadConflictAsync(planned.ConflictId!);
        var request = new SyncConflictResolutionRequest(
            new(conflict.ConflictId, f.Pair.Id, f.RelativePath, conflict.ConflictGeneration),
            SyncAuthorityConflictResolutionKind.KeepDropbox);
        // Create retained operation authority through the same production host/profile/recovery
        // namespace as the retry, not the fixture coordinator's unrelated capability profile.
        using (var cancellation = new CancellationTokenSource())
            await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync([request],
                cancellationToken: cancellation.Token,
                admissionProgress: text => { if (text.StartsWith("Conflict 1 of")) cancellation.Cancel(); }));
        string groupId;
        string operationId;
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = f.Db.DatabasePath }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.operation_group_id,o.operation_id FROM conflict_resolution r
                JOIN operation o ON o.operation_group_id=r.operation_group_id
                WHERE r.conflict_id=$id AND r.requested_generation=$generation;
                """;
            command.Parameters.AddWithValue("$id", conflict.ConflictId);
            command.Parameters.AddWithValue("$generation", conflict.ConflictGeneration);
            await using var reader = await command.ExecuteReaderAsync();
            Require(await reader.ReadAsync(), "The real host did not retain the interrupted operation.");
            groupId = reader.GetString(0);
            operationId = reader.GetString(1);
            Require(!await reader.ReadAsync(), "The exact host choice retained ambiguous operation authority.");
        }
        // The real persisted boundary observed on Child: marked executing, then interrupted
        // before acquiring/issuing the primary effect. This is not a manufactured new plan.
        await f.Db.MarkConflictResolutionExecutingAsync(conflict.ConflictId, groupId);
        var before = await f.Db.LoadOperationAsync(operationId);
        Require(before.Status == SyncAuthorityOperationStatus.Ready && before.Attempts.Count == 0 &&
            (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "resolving" &&
            await File.ReadAllTextAsync(f.LocalPath) == "local loser",
            "The interrupted READY fixture issued an effect or lost its retained conflict authority.");
        Require(!await f.Db.MarkStaleUnissuedConflictResolutionAsync(conflict.ConflictId,
                conflict.ConflictGeneration, groupId, CancellationToken.None) &&
            (await f.Db.LoadOperationAsync(operationId)).Status == SyncAuthorityOperationStatus.Ready &&
            (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "resolving",
            "A runnable retained READY choice was retired without a real guarded source-version refusal.");
        Exception? differentChoice = null;
        try { await f.Conflicts().PlanAsync(conflict.ConflictId, conflict.ConflictGeneration,
            SyncAuthorityConflictResolutionKind.KeepLocal); }
        catch (InvalidOperationException error) { differentChoice = error; }
        Require(differentChoice is not null && f.Wire.Uploads.Count == 0,
            "An executing resolution accepted a different choice instead of retaining its existing authority.");
        var batch = await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync([request]));
        var current = await f.Db.LoadOperationAsync(operationId);
        Console.WriteLine("Interrupted READY retained native result: " + JsonSerializer.Serialize(new {
            batch.Items.Single().FailureType, batch.Items.Single().FailureMessage,
            decisions = batch.Items.Single().Result?.Decisions, status = current.Status,
            attempts = current.Attempts.Select(item => item.EffectKind), recovery = (await f.Db.LoadRecoveryArtifactsAsync()).Count }));
        Require(batch.Items.Single().Result?.Recovered == 1 && current.Status == SyncAuthorityOperationStatus.Completed &&
            current.Attempts.Select(item => item.EffectKind).SequenceEqual(
                ["local-retain-predecessor", "local-promote-stage"]) &&
            await File.ReadAllTextAsync(f.LocalPath) == "cloud winner" &&
            (await f.Db.LoadRecoveryArtifactsAsync()).Count == 1 &&
            (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "resolved",
            "Same-choice resumption did not complete the original READY operation exactly once with recovery.");
        Exception? replay = null;
        try { await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(
            [new(new(conflict.ConflictId, f.Pair.Id, f.RelativePath, conflict.ConflictGeneration),
                SyncAuthorityConflictResolutionKind.KeepDropbox)])); }
        catch (InvalidOperationException error) { replay = error; }
        Require(replay is not null && (await f.Db.LoadRecoveryArtifactsAsync()).Count == 1 &&
            (await f.Db.LoadOperationAsync(operationId)).Attempts.Select(item => item.AttemptId)
                .SequenceEqual(current.Attempts.Select(item => item.AttemptId)),
            "Completed conflict resumption was replayed.");
        Console.WriteLine("PASS interrupted READY conflict: same exact choice resumes once, different choice/completed replay refused");
    }

    private static async Task AuthorityConflictRetainedSourceRefusalAsync()
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, "local loser");
        f.Wire.Put(f.RemotePath, "cloud winner");
        await f.Observer().ObserveAsync("manual");
        var planned = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        var conflict = await f.Db.LoadConflictAsync(planned.ConflictId!);
        var request = new SyncConflictResolutionRequest(
            new(conflict.ConflictId, f.Pair.Id, f.RelativePath, conflict.ConflictGeneration),
            SyncAuthorityConflictResolutionKind.KeepDropbox);
        using (var cancellation = new CancellationTokenSource())
            await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync([request],
                cancellationToken: cancellation.Token,
                admissionProgress: text => { if (text.StartsWith("Conflict 1 of")) cancellation.Cancel(); }));
        string groupId;
        string operationId;
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = f.Db.DatabasePath }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.operation_group_id,o.operation_id FROM conflict_resolution r
                JOIN operation o ON o.operation_group_id=r.operation_group_id WHERE r.conflict_id=$id;
                """;
            command.Parameters.AddWithValue("$id", conflict.ConflictId);
            await using var reader = await command.ExecuteReaderAsync();
            Require(await reader.ReadAsync(), "The native host did not retain the original conflict choice.");
            groupId = reader.GetString(0); operationId = reader.GetString(1);
            Require(!await reader.ReadAsync(), "The original conflict operation was ambiguous.");
        }
        await f.Db.MarkConflictResolutionExecutingAsync(conflict.ConflictId, groupId);
        f.Wire.Put(f.RemotePath, "newer cloud winner");
        SyncAuthoritySourceChangedException? failure = null;
        const string worker = "legacy-conflict-source-refusal";
        try { await new SyncAuthorityUpdateLocalExecutor(f.Pair, f.Client, f.Db).ExecuteAsync(operationId, worker); }
        catch (SyncAuthoritySourceChangedException error) { failure = error; }
        Require(failure is not null, "The real executor did not reach the pre-effect source-version refusal.");
        // Exercise the older caller's exact retained boundary using the real failure journal:
        // primary superseded, resolution still executing, zero attempts and no prepared resources.
        // No production decision, lease, status or effect is substituted with a SQL mutation.
        await f.Db.RetainExecutionFailureAsync(operationId, worker, failure!, CancellationToken.None);
        var retained = await f.Db.LoadOperationAsync(operationId);
        Require(retained.Status == SyncAuthorityOperationStatus.Superseded && retained.Attempts.Count == 0 &&
            retained.Allocations.All(item => item.Status == SyncAuthorityAllocationStatus.Authorized) &&
            (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "resolving",
            "The real old-caller fixture did not reproduce the retained Child boundary.");
        var resumed = await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync([request]));
        Require(resumed.Items.Single().Result is { NotAttempted: 1, NeedsVerification: 0, Recovered: 0 } &&
            (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status == "superseded" &&
            await File.ReadAllTextAsync(f.LocalPath) == "local loser" &&
            (await f.Db.LoadRecoveryArtifactsAsync()).Count == 0 && f.Wire.Uploads.Count == 0,
            "A retained zero-effect superseded primary left its old conflict choice runnable or uncertain.");
        await f.Observer().ObserveAsync("manual");
        var fresh = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        Require(fresh.ConflictId is not null && fresh.ConflictId != conflict.ConflictId,
            "Fresh ordinary classification did not preserve the genuine current two-sided conflict.");
        Console.WriteLine("PASS native retained source refusal: real old-caller journal, safe stale retirement and fresh classification");
    }

    private static async Task AuthorityConflictSourceDriftAsync()
    {
        await using var f = await Fixture.CreateAsync();
        using var scheduling = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(1);
        var paths = new[] { f.RelativePath, "second.txt" };
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, path), "local loser " + path);
            f.Wire.Put("/Pilot/" + path, "cloud winner " + path);
        }
        await f.Observer().ObserveAsync("manual");
        var plans = (await f.Planner().PlanAsync(f.Pair.Id)).Where(item => paths.Contains(item.RelativePath))
            .ToDictionary(item => item.RelativePath);
        var requests = new List<SyncConflictResolutionRequest>();
        foreach (var path in paths)
        {
            var conflict = await f.Db.LoadConflictAsync(plans[path].ConflictId!);
            requests.Add(new(new(conflict.ConflictId, f.Pair.Id, path, conflict.ConflictGeneration),
                SyncAuthorityConflictResolutionKind.KeepDropbox));
        }
        f.Wire.Put(f.RemotePath, "newer cloud winner");
        var batch = await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(requests));
        var result = batch.Items.Single().Result!;
        Require(result.Recovered == 1 && result.NotAttempted == 1 && result.NeedsVerification == 0 &&
            await File.ReadAllTextAsync(f.LocalPath) == "local loser " + f.RelativePath &&
            await File.ReadAllTextAsync(PilotPath.ResolveContainedPath(f.Pair.LocalRoot, "second.txt")) == "cloud winner second.txt" &&
            (await f.Db.LoadConflictAsync(requests[0].Conflict.ConflictId)).Status == "superseded" &&
            (await f.Db.LoadConflictAsync(requests[1].Conflict.ConflictId)).Status == "resolved",
            "A pre-effect source-version refusal became uncertain or discarded the independent completed file.");
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = f.Db.DatabasePath }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.status,g.status,o.status,
                       (SELECT count(*) FROM operation_attempt a WHERE a.operation_id=o.operation_id)
                FROM conflict_resolution r JOIN operation_group g ON g.operation_group_id=r.operation_group_id
                JOIN operation o ON o.operation_group_id=r.operation_group_id WHERE r.conflict_id=$id;
                """;
            command.Parameters.AddWithValue("$id", requests[0].Conflict.ConflictId);
            await using var reader = await command.ExecuteReaderAsync();
            Require(await reader.ReadAsync() && reader.GetString(0) == "stale" && reader.GetString(1) == "cancelled" &&
                reader.GetString(2) == "superseded" && reader.GetInt64(3) == 0 && !await reader.ReadAsync(),
                "The old immutable choice did not retain an exact, conclusively unissued terminal history.");
        }
        await f.Observer().ObserveAsync("manual");
        var fresh = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        Require(fresh.ConflictId is not null && fresh.ConflictId != requests[0].Conflict.ConflictId,
            "Actual incompatible edits to both baseline sides must remain a fresh conflict, not an automatic winner.");
        var current = await f.Db.LoadConflictAsync(fresh.ConflictId!);
        Require(current.ConflictGeneration > requests[0].Conflict.Generation,
            "Fresh conflict authority reused the superseded generation.");
        var resolved = await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(
            [new(new(current.ConflictId, f.Pair.Id, f.RelativePath, current.ConflictGeneration),
                SyncAuthorityConflictResolutionKind.KeepDropbox)]));
        Require(resolved.Items.Single().Result?.Recovered == 1 && await File.ReadAllTextAsync(f.LocalPath) == "newer cloud winner" &&
            (await f.Db.LoadRecoveryArtifactsAsync()).Count == 2,
            "Fresh explicit authority failed to preserve the losing version or replayed the completed peer.");
        Exception? replay = null;
        try { await f.WithHostAsync(host => host.ResolveConflictSelectionCommandAsync(requests)); }
        catch (InvalidOperationException error) { replay = error; }
        Require(replay is not null && (await f.Db.LoadRecoveryArtifactsAsync()).Count == 2,
            "The superseded/completed old selection was replayed.");
        Console.WriteLine("PASS native conflict source drift: exact zero-effect stale history, peer completion, fresh classification/choice and no replay");
    }

    private static async Task AuthorityOrdinaryChangeAfterPlanAsync(string change)
    {
        await using var f = await Fixture.CreateAsync();
        f.Wire.Put(f.RemotePath, After);
        var changed = false;
        var batch = await f.WithHostAsync(host => host.RunPairsAsync([f.Pair], "Manual", onProgress: async progress =>
        {
            if (changed || progress.Action is not { Stage: "Starting", RelativePath: "file.txt" }) return;
            changed = true;
            if (change == "destination") await File.WriteAllTextAsync(f.LocalPath, "independent local edit");
            else
            {
                f.Wire.Put(f.RemotePath, "source edited after planning");
                if (change == "identical") await File.WriteAllTextAsync(f.LocalPath, "source edited after planning");
            }
        }));
        Require(changed && batch.Items.Single().FailureType is null,
            "The actual host caller did not reach the post-plan edit boundary.");
        var result = batch.Items.Single().Result!;
        if (change == "destination")
            Require(await File.ReadAllTextAsync(f.LocalPath) == "independent local edit" && f.Wire.Read(f.RemotePath) == After &&
                result.Conflicts == 1 && (await f.Db.LoadRecoveryArtifactsAsync()).Count == 0,
                "A changed destination must be preserved and reclassified as a genuine two-sided conflict.");
        else
            Require(await File.ReadAllTextAsync(f.LocalPath) == "source edited after planning" &&
                result.Conflicts == 0 && result.Blocked == 0 && result.NeedsVerification == 0 &&
                result.Downloaded == (change == "source" ? 1 : 0) &&
                (await f.Db.LoadRecoveryArtifactsAsync()).Count == (change == "source" ? 1 : 0),
                "Source-only or already identical current content required another user choice or retained a stale refusal.");
        Console.WriteLine("PASS native ordinary post-plan " + change + ": current baseline-aware outcome, no silent overwrite or repeated choice");
    }

    private static async Task AuthorityBoundedObservedRunAsync()
    {
        await using var f = await Fixture.CreateAsync();
        f.Wire.Put(f.RemotePath, After);
        PilotStateAuthority? state = null;
        await f.WithHostAsync(async host =>
        {
            var rounds = 0;
            host.CoordinatedPlanningPhaseForTest = phase =>
            {
                if (phase == "after-dispatch-before-next-observation-set")
                    f.Wire.Put("/Pilot/arrival-" + ++rounds + ".txt", "later cloud arrival");
                return Task.CompletedTask;
            };
            try { await CliChecks.CheckBoundedRunDispatchAsync(host, state!, f.Pair.Id, 1); }
            finally { host.CoordinatedPlanningPhaseForTest = null; }
            Require(rounds == 1 && await File.ReadAllTextAsync(f.LocalPath) == After &&
                    !File.Exists(Path.Combine(f.Pair.LocalRoot, "arrival-1.txt")),
                "only the observed work completes; continual post-dispatch arrivals do not force another full scan");
            await CliChecks.CheckBoundedRunDispatchAsync(host, state!, f.Pair.Id, 1);
            Require(await File.ReadAllTextAsync(Path.Combine(f.Pair.LocalRoot, "arrival-1.txt")) ==
                    "later cloud arrival", "the next normal run actually syncs the later arrival");
            await CliChecks.CheckBoundedRunDispatchAsync(host, state!, f.Pair.Id, 0);
            Require((await host.LoadPairsAsync()).Pairs.Single(item => item.Id == f.Pair.Id)
                    .InitialReviewApproved == true,
                "bounded completion and repeat preserve approval without reopening first-sync choices");
            return true;
        }, authority => state = authority);
        Console.WriteLine("PASS bounded observed run: real CLI/host/native DB/HTTP/local effects; late arrivals retained for next sync, then zero-effect repeat");
    }

    private static async Task AuthorityLegacyIdentityBaselineAsync(string localChange)
    {
        await using var f = await Fixture.CreateAsync();
        var point = (await f.Db.LoadLatestSynchronizationPointsAsync(f.Pair.Id)).Single();
        var observed = await f.Db.LoadObjectVersionAsync(point.LocalObjectVersionId!);
        var identity = JsonNode.Parse(observed.IdentityJson)!.AsObject();
        identity.Remove("contentHash"); identity.Remove("sizeBytes");
        const string legacyId = "version-local-legacy-native-fixture";
        await f.Db.RegisterObjectVersionAsync(legacyId, observed.ObjectId, "local", identity.ToJsonString(),
            observed.ContentHash, observed.Size);
        await f.Db.AcceptSynchronizationPointAsync("syncpoint-legacy-native-fixture", f.Pair.Id, point.ObjectId,
            legacyId, point.DropboxObjectVersionId, point.LocalMembershipId, point.DropboxMembershipId);
        var originalTime = File.GetLastWriteTimeUtc(f.LocalPath);
        f.Wire.Put(f.RemotePath, After);
        if (localChange == "bytes")
        {
            // A same-size edit preserving metadata must still be a real edit.
            await File.WriteAllTextAsync(f.LocalPath, new string('x', Encoding.UTF8.GetByteCount(Before)));
            File.SetLastWriteTimeUtc(f.LocalPath, originalTime);
        }
        else if (localChange == "identity")
        {
            var replacement = Path.Combine(f.Root, "replacement.tmp");
            await File.WriteAllTextAsync(replacement, Before);
            File.SetLastWriteTimeUtc(replacement, originalTime);
            File.Move(replacement, f.LocalPath, overwrite: true);
        }
        var batch = await f.WithHostAsync(host => host.RunPairsAsync([f.Pair], "Manual"));
        var result = batch.Items.Single().Result!;
        Require(batch.Items.Single().FailureType is null &&
            (localChange == "none"
                ? result.Downloaded == 1 && result.Conflicts == 0 && result.Blocked == 0 &&
                  await File.ReadAllTextAsync(f.LocalPath) == After
                : result.Downloaded == 0 && result.Conflicts == 1 &&
                  await File.ReadAllTextAsync(f.LocalPath) == (localChange == "bytes"
                      ? new string('x', Encoding.UTF8.GetByteCount(Before)) : Before)),
            "Legacy byte-field enrichment was confused with an edit, or actual byte/physical-identity changes were hidden.");
        Require((await f.Db.LoadObjectVersionAsync(legacyId)).IdentityJson == identity.ToJsonString(),
            "Legacy immutable authority was rewritten instead of compared.");
        Console.WriteLine("PASS native legacy identity " + localChange + ": representation equivalence, real edit/identity refusal, immutable baseline retained");
    }

    private static async Task AuthorityConflictBatchPersistenceAsync()
    {
        await using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.LocalPath, "local loser");
        f.Wire.Put(f.RemotePath, "cloud winner");
        await f.Observer().ObserveAsync("manual");
        var plan = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        var conflict = await f.Db.LoadConflictAsync(plan.ConflictId!);
        // An old revision continues to exist, but must not authorise a new resolution.
        var reserved = await f.Conflicts().PlanAsync(conflict.ConflictId, conflict.ConflictGeneration,
            SyncAuthorityConflictResolutionKind.KeepDropbox);
        await f.Db.MarkConflictResolutionExecutingAsync(reserved.ConflictId, reserved.OperationGroupId);
        var lease = await f.Db.AcquireOperationLeaseAsync(reserved.OperationId, "policy-fixture", TimeSpan.FromMinutes(1));
        Exception? refused = null;
        try { await f.Db.RegisterPairAsync(f.Pair.Id, 2, SyncAuthorityPairConfiguration.FromPair(f.Pair with { PolicyRevision = 2 })); }
        catch (InvalidOperationException error) { refused = error; }
        Require(refused?.Message.Contains("nonterminal authority", StringComparison.Ordinal) == true,
            "The normal settings boundary must refuse a revision change while unfinished authority exists.");
        // Input-only legacy/stale-state fixture, not an allowed public settings transition. The real
        // lease/dispatch guard must independently reject this persisted mismatch before any effect.
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = f.Db.DatabasePath }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sync_pair SET configuration_revision=2 WHERE pair_id=$id AND configuration_revision=1;";
            command.Parameters.AddWithValue("$id", f.Pair.Id);
            Require(await command.ExecuteNonQueryAsync() == 1, "The bounded stale-policy fixture was not changed.");
        }
        refused = null;
        try { await f.Db.StartDispatchedAttemptAsync(reserved.OperationId, lease, new string('a', 64), "download"); }
        catch (InvalidOperationException error) { refused = error; }
        Require(refused?.Message.Contains("policy changed", StringComparison.Ordinal) == true &&
            (await f.Db.LoadOperationAsync(reserved.OperationId)).Attempts.Count == 0 &&
            await File.ReadAllTextAsync(f.LocalPath) == "local loser",
            "A persisted stale policy after planning/lease must reject actual dispatch without any file effect.");
        refused = null;
        try { await f.Conflicts().PlanAsync(conflict.ConflictId, conflict.ConflictGeneration,
            SyncAuthorityConflictResolutionKind.KeepDropbox); }
        catch (InvalidOperationException error) { refused = error; }
        Require(refused is not null && await File.ReadAllTextAsync(f.LocalPath) == "local loser" &&
            (await f.Db.LoadRecoveryArtifactsAsync()).Count == 0,
            "A retained historical policy revision authorised a new conflict replacement.");
        const string id = "command-fixture-conflict-checkpoint";
        var checkpoint = JsonSerializer.Serialize(new { resolved = 1, files = new[] {
            new SyncDecision("completed.txt", SyncDecisionKind.Recovered, "Verified existing result.") } });
        await f.Db.CreateCommandRequestAsync(id, "resolve_conflict", f.Pair.Id, null, null, "{}");
        refused = null;
        try { await f.Db.CheckpointExecutingCommandResultAsync(id, checkpoint); }
        catch (InvalidOperationException error) { refused = error; }
        Require(refused is not null, "A nonexecuting command accepted a result checkpoint.");
        await f.Db.TransitionCommandRequestAsync(id, "accepted");
        await f.Db.TransitionCommandRequestAsync(id, "executing");
        await f.Db.CheckpointExecutingCommandResultAsync(id, checkpoint);
        f.ReopenAuthority();
        await f.Db.RecoverInterruptedCommandRequestsAsync();
        var retained = await f.Db.LoadRunByIdAsync(id);
        using var json = JsonDocument.Parse(retained!.ResultJson!);
        Require(retained.Status == "blocked" && json.RootElement.GetProperty("resolved").GetInt32() == 1 &&
            json.RootElement.GetProperty("files")[0].GetProperty("RelativePath").GetString() == "completed.txt" &&
            json.RootElement.GetProperty("recoveryRequired").GetBoolean(),
            "Restart replaced the exact durable batch checkpoint with a reason-only result.");
        Console.WriteLine("PASS conflict batch policy/checkpoint: current revision required, executing-only checkpoint, reopen preserves outcomes");
    }

    private static async Task AuthorityConflictContainmentNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        await File.WriteAllTextAsync(f.LocalPath, "local nested loser");
        f.Wire.Put(f.RemotePath, "dropbox nested winner");
        await f.Observer().ObserveAsync("manual");
        var plan = (await f.Planner().PlanAsync(f.Pair.Id)).Single(item => item.RelativePath == f.RelativePath);
        var conflict = await f.Db.LoadConflictAsync(plan.ConflictId!);
        var originalDirectory = Path.GetDirectoryName(f.LocalPath)!;
        var outsideDirectory = Path.Combine(f.Root, "outside-conflict");
        Directory.Move(originalDirectory, outsideDirectory);
        CreateDirectoryJunction(originalDirectory, outsideDirectory);
        try
        {
            Exception? rejected = null;
            try
            {
                await f.WithHostAsync(host => host.ResolveConflictCommandAsync(f.Pair, f.RelativePath,
                    SyncAuthorityConflictResolutionKind.KeepDropbox, conflict.ConflictId,
                    conflict.ConflictGeneration));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = error; }
            Require(rejected is not null && (await f.Db.LoadConflictAsync(conflict.ConflictId)).Status != "resolved" &&
                    await File.ReadAllTextAsync(Path.Combine(outsideDirectory, "metadata-path.txt")) == "local nested loser",
                "The nested conflict authority followed an ancestor reparse or resolved after rejection.");
        }
        finally
        {
            if (Directory.Exists(originalDirectory)) Directory.Delete(originalDirectory);
        }
        Console.WriteLine("PASS TC-NESTED-05.b-path: nested Keep Dropbox preserves unresolved conflict on path rejection");
    }

    private static async Task LegacyNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        using var legacy = await LegacyHarness.CreateAsync(f);

        const string createdRelative = "focused/created.txt";
        var createdPath = PilotPath.ResolveContainedPath(legacy.Pair.LocalRoot, createdRelative);
        var firstRemote = f.Wire.Put("/Pilot/" + createdRelative, "nested create bytes");
        var created = await legacy.Engine().RunAsync();
        Require(created.Downloaded == 1 && await File.ReadAllTextAsync(createdPath) == "nested create bytes" &&
                f.Wire.DownloadRevisions.Count(item => item == firstRemote.Revision) == 1,
            "The public engine did not create the nested Dropbox file exactly once.");

        var secondRemote = f.Wire.Put("/Pilot/" + createdRelative, "nested replacement bytes");
        var updatedCreated = await legacy.Engine().RunAsync();
        Require(updatedCreated.Downloaded == 1 &&
                await File.ReadAllTextAsync(createdPath) == "nested replacement bytes" &&
                f.Wire.DownloadRevisions.Count(item => item == secondRemote.Revision) == 1,
            "The public engine did not replace the nested created file exactly once.");

        var updatedExisting = f.Wire.Put(f.RemotePath, After);
        string? cleanupTransactionPath = null;
        SyncTransferIntent? cleanupIntent = null;
        SyncFileBaseline? cleanupBaseline = null;
        BidirectionalSyncEngine.CleanupStatePersistedForTest = (kind, path) =>
        {
            if (kind != SyncIntentKind.Transfer || !string.Equals(path, f.RelativePath, StringComparison.Ordinal))
                return;
            var persisted = legacy.Store.LoadPair(legacy.Pair.Id);
            cleanupIntent = persisted.PendingTransfer ??
                            throw new Exception("The committed nested update had no durable cleanup intent.");
            cleanupBaseline = persisted.Files.Single(item => item.RelativePath == f.RelativePath);
            cleanupTransactionPath = Path.Combine(legacy.Pair.ControlNamespace!.Local.RootPath,
                cleanupIntent.TransactionDirectoryName!);
            var backupPath = Path.Combine(cleanupTransactionPath, cleanupIntent.BackupFileName!);
            Require(cleanupIntent.Phase == SyncIntentPhase.CommittedCleanupPending &&
                    File.Exists(backupPath) && File.ReadAllText(backupPath) == Before &&
                    cleanupBaseline.LocalContentHash == updatedExisting.Hash &&
                    cleanupBaseline.RemoteContentHash == updatedExisting.Hash &&
                    cleanupBaseline.RemoteId == updatedExisting.Id &&
                    cleanupBaseline.RemoteRevision == updatedExisting.Revision &&
                    cleanupBaseline.LocalVolumeSerialNumber is not null && cleanupBaseline.LocalFileId is not null,
                "The committed nested update did not durably bind its exact baseline and predecessor before cleanup.");
        };
        SyncRunResult updated;
        try { updated = await legacy.Engine().RunAsync(); }
        finally { BidirectionalSyncEngine.CleanupStatePersistedForTest = null; }
        var terminalBaseline = legacy.Store.LoadPair(legacy.Pair.Id).Files.Single(item =>
            item.RelativePath == f.RelativePath);
        Require(updated.Downloaded == 1 && await File.ReadAllTextAsync(f.LocalPath) == After &&
                f.Wire.DownloadRevisions.Count(item => item == updatedExisting.Revision) == 1 &&
                cleanupIntent is not null && cleanupBaseline is not null && cleanupTransactionPath is not null &&
                !Directory.Exists(cleanupTransactionPath) &&
                legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is null &&
                terminalBaseline.LocalContentHash == updatedExisting.Hash &&
                terminalBaseline.RemoteContentHash == updatedExisting.Hash &&
                terminalBaseline.RemoteId == updatedExisting.Id &&
                terminalBaseline.RemoteRevision == updatedExisting.Revision &&
                terminalBaseline.LocalVolumeSerialNumber is not null && terminalBaseline.LocalFileId is not null,
            "The public engine did not complete the ordinary nested update path.");
        var targetDownloads = f.Wire.DownloadRevisions.Count(item =>
            item == firstRemote.Revision || item == secondRemote.Revision || item == updatedExisting.Revision);
        var unchanged = await legacy.Engine().RunAsync();
        Require(unchanged.Downloaded == 0 && unchanged.Uploaded == 0 && unchanged.Conflicts == 0 &&
                f.Wire.DownloadRevisions.Count(item =>
                    item == firstRemote.Revision || item == secondRemote.Revision ||
                    item == updatedExisting.Revision) == targetDownloads,
            "The public engine replayed a completed nested create or update: " +
            JsonSerializer.Serialize(new { unchanged, targetDownloads,
                observedTargetDownloads = f.Wire.DownloadRevisions.Count(item =>
                    item == firstRemote.Revision || item == secondRemote.Revision ||
                    item == updatedExisting.Revision) }));
        Console.WriteLine("PASS TC-NESTED-06.a: public engine nested create/update and unchanged no-replay");
    }

    private static async Task LegacyInterruptedNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        using var legacy = await LegacyHarness.CreateAsync(f);
        var remote = f.Wire.Put(f.RemotePath, After);
        SyncLocalTransfer.TargetPromotedForTest = _ =>
            throw new IOException("Fixture lost receipt after nested target promotion.");
        SyncRunResult interrupted;
        try { interrupted = await legacy.Engine().RunAsync(); }
        finally { SyncLocalTransfer.TargetPromotedForTest = null; }
        var pending = legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer;
        Require(interrupted.Blocked == 1 && pending is not null &&
                await File.ReadAllTextAsync(f.LocalPath) == After &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 1,
            "The public engine did not retain the promoted nested operation after a lost receipt.");
        var recovered = await legacy.Engine().RunAsync();
        Require(recovered.Recovered == 1 && legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is null &&
                await File.ReadAllTextAsync(f.LocalPath) == After &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 1,
            "The restarted public engine did not reconcile the exact promoted nested object without redownload.");
        var successorDownloads = f.Wire.DownloadRevisions.Count(item => item == remote.Revision);
        var unchanged = await legacy.Engine().RunAsync();
        Require(unchanged.Downloaded == 0 && unchanged.Recovered == 0 &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == successorDownloads,
            "The restarted public engine replayed its reconciled nested update.");
        Console.WriteLine("PASS TC-NESTED-06.b: persisted public-engine nested recovery is exact and non-replaying");
    }

    private static async Task LegacyConflictNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        using var legacy = await LegacyHarness.CreateAsync(f);
        const string localLoser = "nested local conflict loser";
        const string dropboxWinner = "nested Dropbox conflict winner";
        await File.WriteAllTextAsync(f.LocalPath, localLoser);
        var remote = f.Wire.Put(f.RemotePath, dropboxWinner);
        SyncLocalTransfer.TargetPromotedForTest = _ =>
            throw new IOException("Fixture lost receipt after nested conflict promotion.");
        SyncRunResult interrupted;
        try
        {
            interrupted = await legacy.Engine().ResolveConflictAsync(f.RelativePath,
                SyncConflictResolution.KeepDropboxArchiveLocal);
        }
        finally { SyncLocalTransfer.TargetPromotedForTest = null; }
        var pending = legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer ??
                      throw new Exception("The interrupted nested conflict retained no composite intent.");
        var recoveryPath = pending.RecoveryPath ??
                           throw new Exception("The interrupted nested conflict retained no recovery path.");
        Require(interrupted.Blocked == 1 && await File.ReadAllTextAsync(recoveryPath) == localLoser &&
                await File.ReadAllTextAsync(f.LocalPath) == dropboxWinner &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 1,
            "Keep Dropbox did not retain its nested loser before the interrupted winner promotion.");
        var recovered = await legacy.Engine().RunAsync();
        var baseline = legacy.Store.LoadPair(legacy.Pair.Id).Files.Single(item =>
            item.RelativePath == f.RelativePath);
        Require(recovered.Recovered == 1 && legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is null &&
                await File.ReadAllTextAsync(recoveryPath) == localLoser &&
                await File.ReadAllTextAsync(f.LocalPath) == dropboxWinner &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 1 &&
                baseline.LocalContentHash == remote.Hash && baseline.RemoteContentHash == remote.Hash &&
                baseline.RemoteId == remote.Id && baseline.RemoteRevision == remote.Revision &&
                baseline.LocalVolumeSerialNumber is not null && baseline.LocalFileId is not null,
            "The public conflict lifecycle did not reconcile the exact nested winner and recovery file.");

        await LegacyConflictRecoveryCollisionNestedAsync();
        await LegacyConflictRecoveryIdentityMismatchNestedAsync();
        Console.WriteLine("PASS TC-NESTED-06.c: public Keep Dropbox conflict retains loser and reconciles winner exactly once");
    }

    private static async Task LegacyConflictRecoveryCollisionNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        using var legacy = await LegacyHarness.CreateAsync(f);
        const string localLoser = "nested collision loser";
        const string dropboxWinner = "nested collision winner";
        await File.WriteAllTextAsync(f.LocalPath, localLoser);
        var remote = f.Wire.Put(f.RemotePath, dropboxWinner);
        SyncLocalRecovery.BeforeCreateForTest = path =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "foreign recovery occupant");
        };
        SyncRunResult blocked;
        try
        {
            blocked = await legacy.Engine().ResolveConflictAsync(f.RelativePath,
                SyncConflictResolution.KeepDropboxArchiveLocal);
        }
        finally { SyncLocalRecovery.BeforeCreateForTest = null; }
        var pending = legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer ??
                      throw new Exception("The recovery collision did not retain its composite intent.");
        Require(blocked.Blocked == 1 && pending.RecoveryPath is not null &&
                await File.ReadAllTextAsync(pending.RecoveryPath) == "foreign recovery occupant" &&
                await File.ReadAllTextAsync(f.LocalPath) == localLoser &&
                f.Wire.Read(f.RemotePath) == dropboxWinner &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 0,
            "A nested recovery collision changed either source, winner, or occupied recovery evidence.");
    }

    private static async Task LegacyConflictRecoveryIdentityMismatchNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        using var legacy = await LegacyHarness.CreateAsync(f);
        const string localLoser = "nested identity loser";
        const string dropboxWinner = "nested identity winner";
        await File.WriteAllTextAsync(f.LocalPath, localLoser);
        var remote = f.Wire.Put(f.RemotePath, dropboxWinner);
        SyncLocalRecovery.SourcePreservedForTest = _ =>
            throw new IOException("Fixture interruption after exact local recovery retention.");
        try
        {
            await legacy.Engine().ResolveConflictAsync(f.RelativePath,
                SyncConflictResolution.KeepDropboxArchiveLocal);
        }
        finally { SyncLocalRecovery.SourcePreservedForTest = null; }
        var pending = legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer ??
                      throw new Exception("The interrupted recovery retained no composite intent.");
        var recoveryPath = pending.RecoveryPath ??
                           throw new Exception("The interrupted recovery retained no exact recovery path.");
        var preservedPath = Path.Combine(f.Root, "preserved-exact-recovery.txt");
        File.Move(recoveryPath, preservedPath);
        await File.WriteAllTextAsync(recoveryPath, localLoser);
        var substituted = await SnapshotLocalArtifactAsync(recoveryPath);
        var downloads = f.Wire.DownloadRevisions.Count(item => item == remote.Revision);
        var rejected = await legacy.Engine().RunAsync();
        Require(rejected.Blocked == 1 && legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is not null &&
                !File.Exists(f.LocalPath) && File.Exists(preservedPath) &&
                await File.ReadAllTextAsync(preservedPath) == localLoser &&
                await SnapshotLocalArtifactAsync(recoveryPath) == substituted &&
                f.Wire.Read(f.RemotePath) == dropboxWinner &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == downloads,
            "A substituted nested recovery identity was accepted or either recovery object was changed.");
    }

    private static async Task LegacySafeCreateRecoveryNestedAsync()
    {
        const string relativePath = "focused/safe-created.txt";
        const string content = "persisted safe-create successor";

        await using (var absentFixture = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt"))
        {
            using var legacy = await LegacyHarness.CreateAsync(absentFixture);
            var remote = absentFixture.Wire.Put("/Pilot/" + relativePath, content);
            var intentId = Guid.NewGuid().ToString("N");
            var pending = SafeCreateIntent(legacy.Pair, relativePath, remote, intentId,
                $"{legacy.Pair.LocalVolumeSerialNumber:x8}:0000000000000001");
            var state = legacy.Store.LoadPair(legacy.Pair.Id);
            await legacy.Store.SavePairAsync(state with { PendingTransfer = pending });
            var recovered = await legacy.Engine().RunAsync();
            Require(recovered.Recovered == 1 && legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is null &&
                    !File.Exists(PilotPath.ResolveContainedPath(legacy.Pair.LocalRoot, relativePath)) &&
                    absentFixture.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 0,
                "The public engine did not clear a proved-not-applied persisted safe-create intent.");
            var retried = await legacy.Engine().RunAsync();
            Require(retried.Downloaded == 1 &&
                    await File.ReadAllTextAsync(PilotPath.ResolveContainedPath(legacy.Pair.LocalRoot, relativePath)) == content &&
                    absentFixture.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 1,
                "The public engine did not safely retry the nested create after not-applied recovery.");
        }

        await using (var committedFixture = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt"))
        {
            using var legacy = await LegacyHarness.CreateAsync(committedFixture);
            var remote = committedFixture.Wire.Put("/Pilot/" + relativePath, content);
            var targetPath = PilotPath.ResolveContainedPath(legacy.Pair.LocalRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, content);
            var target = await SnapshotLocalArtifactAsync(targetPath);
            var intentId = Guid.NewGuid().ToString("N");
            var pending = SafeCreateIntent(legacy.Pair, relativePath, remote, intentId,
                $"{target.VolumeSerialNumber:x8}:{target.FileId:x16}");
            var state = legacy.Store.LoadPair(legacy.Pair.Id);
            await legacy.Store.SavePairAsync(state with { PendingTransfer = pending });
            var recovered = await legacy.Engine().RunAsync();
            var baseline = legacy.Store.LoadPair(legacy.Pair.Id).Files.Single(item =>
                item.RelativePath == relativePath);
            Require(recovered.Recovered == 1 && legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is null &&
                    committedFixture.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 0 &&
                    baseline.LocalContentHash == remote.Hash && baseline.RemoteContentHash == remote.Hash &&
                    baseline.RemoteId == remote.Id && baseline.RemoteRevision == remote.Revision &&
                    baseline.LocalVolumeSerialNumber == target.VolumeSerialNumber &&
                    baseline.LocalFileId == target.FileId,
                "The public engine did not adopt the exact already-committed safe-create object without replay.");
        }
    }

    private static SyncTransferIntent SafeCreateIntent(SyncPair pair, string relativePath, RemoteFile remote,
        string intentId, string stageArtifactId) => new(intentId, pair.Id, relativePath, SyncDirection.Download,
        remote.Hash, null, remote.Revision, SyncCreateNewLocalTransfer.StageName(intentId), null,
        DateTimeOffset.UtcNow, SyncIntentKind.Transfer,
        ExpectedSourceId: remote.Id, ExpectedSourceRevision: remote.Revision, ExpectedSourceSize: remote.Bytes.Length,
        CreationPolicyRevision: pair.PolicyRevision, StageArtifactId: stageArtifactId);

    private static async Task LegacyContainmentNestedAsync()
    {
        await using var f = await Fixture.CreateAsync(relativePath: "focused/metadata-path.txt");
        using var legacy = await LegacyHarness.CreateAsync(f);
        const string createdRelative = "focused/reparse-created.txt";
        var remote = f.Wire.Put("/Pilot/" + createdRelative, "contained successor");
        var originalDirectory = Path.GetDirectoryName(f.LocalPath)!;
        var outsideDirectory = Path.Combine(f.Root, "outside-legacy");
        var hookRan = false;
        SyncLocalTransfer.BeforeTargetCommitForTest = _ =>
        {
            if (hookRan) return;
            hookRan = true;
            Directory.Move(originalDirectory, outsideDirectory);
            CreateDirectoryJunction(originalDirectory, outsideDirectory);
        };
        SyncRunResult blocked;
        try { blocked = await legacy.Engine().RunAsync(); }
        finally { SyncLocalTransfer.BeforeTargetCommitForTest = null; }
        try
        {
            Require(hookRan && blocked.Blocked == 1 &&
                    !File.Exists(Path.Combine(outsideDirectory, "reparse-created.txt")) &&
                    await File.ReadAllTextAsync(Path.Combine(outsideDirectory, "metadata-path.txt")) == Before,
                "The public engine followed a substituted nested ancestor or lost its blocked intent: " +
                JsonSerializer.Serialize(new { hookRan, blocked,
                    outsideCreated = File.Exists(Path.Combine(outsideDirectory, "reparse-created.txt")),
                    outsidePredecessor = await File.ReadAllTextAsync(Path.Combine(outsideDirectory,
                        "metadata-path.txt")),
                    pending = legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer }));
        }
        finally
        {
            if (Directory.Exists(originalDirectory)) Directory.Delete(originalDirectory);
            Directory.Move(outsideDirectory, originalDirectory);
        }
        var resumed = await legacy.Engine().RunAsync();
        var createdPath = PilotPath.ResolveContainedPath(legacy.Pair.LocalRoot, createdRelative);
        Require(resumed.Downloaded == 1 &&
                await File.ReadAllTextAsync(createdPath) == "contained successor" &&
                legacy.Store.LoadPair(legacy.Pair.Id).PendingTransfer is null &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 2,
            "The public engine could not safely resume the nested create after containment was restored.");
        var unchanged = await legacy.Engine().RunAsync();
        Require(unchanged.Downloaded == 0 &&
                f.Wire.DownloadRevisions.Count(item => item == remote.Revision) == 2,
            "The public engine replayed the nested create after its safe retry committed.");
        Console.WriteLine("PASS TC-NESTED-04.b: public engine rejects nested ancestor substitution and safely resumes");
    }

    private sealed class LegacyHarness : IDisposable
    {
        private readonly Fixture fixture;
        private readonly PilotInstanceLock appLock;
        internal SyncPair Pair { get; }
        internal SyncRuntimeStateStore Store { get; }

        private LegacyHarness(Fixture fixture, PilotInstanceLock appLock, SyncPair pair,
            SyncRuntimeStateStore store)
        {
            this.fixture = fixture;
            this.appLock = appLock;
            Pair = pair;
            Store = store;
        }

        internal BidirectionalSyncEngine Engine() =>
            new(Pair, fixture.Client, Store, new PilotRunControl());

        internal static async Task<LegacyHarness> CreateAsync(Fixture fixture)
        {
            var root = SyncControlNamespace.RemoteContainerPath + "/namespace-correction";
            var markerPath = root + "/.syncshark-owner.json";
            fixture.Wire.Folders[root] = "id:legacy-control";
            var marker = fixture.Wire.Put(markerPath,
                SyncControlNamespace.MarkerBytes("installation-correction", "namespace-correction"));
            var pair = fixture.Pair with
            {
                ControlNamespace = fixture.Pair.ControlNamespace! with
                {
                    Remote = new SyncRemoteControlNamespaceBinding(root, "id:legacy-control", markerPath,
                        marker.Id, marker.Revision, marker.Hash)
                }
            };
            var appLock = PilotInstanceLock.Acquire(Path.Combine(fixture.Root,
                "legacy-nested-state", "instance.lock"));
            try
            {
                await new SyncProductStateStore(appLock.StateAuthority).UpdateAsync(current => current with
                { InstallationId = pair.ControlNamespace!.InstallationId });
                await new SyncPairStore(appLock.StateAuthority).SaveAsync(new SyncPairSet([pair], pair.Id));
                var store = new SyncRuntimeStateStore(appLock.StateAuthority);
                var harness = new LegacyHarness(fixture, appLock, pair, store);
                await harness.Engine().RunAsync();
                await harness.Engine().RunAsync();
                Require(store.LoadPair(pair.Id).BaselineEstablished,
                    "The public engine did not establish the nested fixture baseline.");
                return harness;
            }
            catch
            {
                appLock.Dispose();
                throw;
            }
        }

        public void Dispose() => appLock.Dispose();
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(Path.GetFullPath(junctionPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(targetPath));
        using var process = Process.Start(startInfo) ??
                            throw new IOException("The directory-junction probe could not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"The directory-junction probe failed ({process.ExitCode}): {error}{output}");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root = "";
        internal string? NeutralRoot;
        internal string RelativePath = "file.txt";
        internal string LocalPath => PilotPath.ResolveContainedPath(Pair.LocalRoot, RelativePath);
        internal string RemotePath => "/Pilot/" + RelativePath;
        internal string ProviderAccountId => AuthorityId("provider-account", Connection.AccountId,
            Connection.RootNamespaceId, Connection.HomeNamespaceId);
        internal string VolumeId => AuthorityId("volume", "installation-correction",
            Pair.LocalVolumeSerialNumber.ToString("x8"));
        internal string LocalRootBindingId => AuthorityId("root-local", Pair.Id, VolumeId,
            Pair.LocalRootFileId.ToString("x16"));
        internal string RemoteRootBindingId => AuthorityId("root-dropbox", Pair.Id,
            Connection.RootNamespaceId, Pair.DropboxFolderId);
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal WireStore Wire = new();
        internal HttpClient Http = null!;
        internal DropboxPilotClient Client = null!;
        internal WindowsCloudFilesProvider? Cloud;
        internal SyncAuthorityPlannerOptions Options = new(1, "profile-correction", "namespace-correction", "namespace-correction",
            EnableDeletionEffects: true, DeletionMinimumInterval: TimeSpan.Zero);
        internal static readonly DropboxConnectionBinding Connection = new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();

        internal static async Task<Fixture> CreateAsync(bool placeholder = false,
            string relativePath = "file.txt")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Real NTFS proof requires Windows.");
            var f = new Fixture();
            f.RelativePath = PilotPath.NormalizeRelative(relativePath);
            f.Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TestData", "Projects", "sync-shark", "cloud-pro-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(f.Root);
            var local = placeholder
                ? Path.Combine(f.NeutralRoot = RecoveryNativeTestRoot.Create(), "local")
                : Path.Combine(f.Root, "local");
            Directory.CreateDirectory(local);
            f.Http = new HttpClient(f.Wire);
            f.Client = new DropboxPilotClient(f.Http, new FixedTokenSource(), "/Pilot", Connection, "id:pilot-folder");
            var parent = Path.GetDirectoryName(f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var accumulated = "";
                foreach (var segment in f.RelativePath.Split('/')[..^1])
                {
                    accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
                    f.Wire.Folders["/Pilot/" + accumulated] = "id:fixture-folder-" + accumulated.Replace('/', '-');
                }
            }
            f.Wire.Put(f.RemotePath, Before);
            using (var lease = PilotDirectoryLease.Open(local))
            {
                var control = SyncControlNamespace.BindLocalForTest(lease, "installation-correction", "namespace-correction");
                f.Pair = new SyncPair("namespace-correction", "Correction", local, lease.Identity.VolumeSerialNumber,
                    lease.Identity.FileId, "/Pilot", "id:pilot-folder", PropagateDeletions: true,
                    ControlNamespace: new SyncControlNamespaceBinding("installation-correction", "namespace-correction", control),
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                        "id:recovery", "root-namespace"),
                    ConnectionId: Connection.ConnectionId, InitialReviewApproved: true, FilesOnDemandEnabled: placeholder);
            }
            if (placeholder)
            {
                f.Cloud = new WindowsCloudFilesProvider(f.Pair, f.Client, Path.Combine(f.Root, "stage"));
                try
                {
                    f.Cloud.RegisterAndConnect();
                    f.Cloud.CreatePlaceholder((await f.Client.ListEntriesAsync()).Single());
                }
                catch { await f.DisposeAsync(); throw; }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.LocalPath)!);
                await File.WriteAllTextAsync(f.LocalPath, Before);
            }
            f.Db = new SyncAuthorityDatabase(Path.Combine(f.Root, "db", "authority.db"));
            await f.Db.InitializeAsync("installation-correction", "profile-correction", "correction", "{}");
            await f.Db.RegisterPairAsync(f.Pair.Id, 1, new SyncAuthorityPairConfiguration("recoverable", 0, 1,
                "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false, RecoveryRetentionDays: 30));
            var volumeSerial = f.Pair.LocalVolumeSerialNumber.ToString("x8");
            var localFileId = f.Pair.LocalRootFileId.ToString("x16");
            await f.Db.RegisterProviderAccountAsync(f.ProviderAccountId, "installation-correction",
                Connection.AccountId, Connection.RootNamespaceId, Connection.HomeNamespaceId, "Dropbox", 1);
            await f.Db.RegisterVolumeAsync(f.VolumeId, "installation-correction", volumeSerial, "NTFS", null,
                supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true);
            await f.Db.RegisterLocalRootBindingAsync(f.LocalRootBindingId, f.Pair.Id, 1,
                "installation-correction", f.VolumeId, volumeSerial, localFileId, local);
            await f.Db.RegisterDropboxRootBindingAsync(f.RemoteRootBindingId, f.Pair.Id, 1,
                "installation-correction", f.ProviderAccountId, Connection.RootNamespaceId,
                f.Pair.DropboxFolderId, "/Pilot");
            await f.Db.RetainPairDefinitionAsync(f.Pair, true);
            f.Admission = await f.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", f.Pair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await f.Db.TransitionCommandRequestAsync(f.Admission.CommandRequestId, "executing");
            await f.Observer().ObserveAsync("initial");
            await f.Observer().ObserveAsync("initial");
            await new SyncAuthorityInitialPlanner(f.Db).AdoptAsync(f.Pair.Id);
            await f.Db.SetPairStateAsync(f.Pair.Id, "active");
            return f;
        }
        internal static string AuthorityId(string role, params string[] parts) => role + "-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
                .ToLowerInvariant()[..32];
        internal SyncAuthorityObservationCoordinator Observer() => new(Pair, Client, Db, LocalRootBindingId, RemoteRootBindingId);
        internal SyncAuthorityReconciliationPlanner Planner() => new(Db, Options, Admission);
        internal SyncAuthorityPairRunner Runner() => new(Pair, Client, Db, LocalRootBindingId, RemoteRootBindingId, Options, new PilotRunControl(), Admission, Cloud);
        internal SyncAuthorityConflictResolutionCoordinator Conflicts() => new(Pair, Client, Db, Options, Admission);
        internal void ReopenAuthority()
        {
            SqliteConnection.ClearAllPools();
            Db = new SyncAuthorityDatabase(Db.DatabasePath);
        }
        internal async Task<T> WithHostAsync<T>(Func<SyncAuthorityRuntimeHost, Task<T>> action,
            Action<PilotStateAuthority>? captureState = null)
        {
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(Root, "host-state", "instance.lock"));
            captureState?.Invoke(appLock.StateAuthority);
            var product = new SyncProductStateStore(appLock.StateAuthority);
            await product.UpdateAsync(current => current with
            { InstallationId = Pair.ControlNamespace!.InstallationId });
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            if (pairStore.Load().Pairs.All(item => !string.Equals(item.Id, Pair.Id, StringComparison.Ordinal)))
                await pairStore.SaveAsync(new SyncPairSet([Pair], Pair.Id));
            var connections = new DropboxConnectionStore(appLock.StateAuthority);
            if (connections.Load() is null) await connections.SaveNewAsync(Connection);
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, Http, pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connections,
                new MemoryDropboxRefreshTokenStore("fixture-refresh"), Db);
            await host.LoadPairsAsync();
            return await action(host);
        }
        internal async Task<SyncAuthorityOperationSnapshot> PlanOperationAsync(string? path = null)
        {
            path ??= RelativePath;
            await Observer().ObserveAsync("monitor");
            var plan = (await Planner().PlanAsync(Pair.Id)).Single(x => x.RelativePath == path);
            Require(plan.OperationId is not null, "No actual operation: " + plan.Reason);
            return await Db.LoadOperationAsync(plan.OperationId!);
        }
        internal void AssertRecoveryBytes(string expected)
        {
            Require(Wire.RecoveryUploads.Count == 1 && Encoding.UTF8.GetString(Wire.RecoveryUploads.Single().Bytes) == expected,
                "Recovery upload did not contain exactly one exact predecessor.");
            var entry = Wire.Files[Wire.RecoveryUploads.Single().Path];
            Require(entry.Hash == DropboxContentHash.Compute(Encoding.UTF8.GetBytes(expected)), "Wrong retained predecessor hash.");
        }
        internal void AssertRemoteReplacement(string expected = Before)
        {
            AssertRecoveryBytes(expected);
            Require(Wire.Uploads.Count(u => u.Conditional) == 1 && Wire.Read("/Pilot/file.txt") == After,
                "Successor was not exactly once conditionally replaced.");
        }
        internal async Task AssertLocalRecoveryAsync(string expected)
        {
            var artifact = (await Db.LoadRecoveryArtifactsAsync()).Single(x => x.Side == "local");
            // The actual retained file, not only its database hash, is the oracle.
            using var json = JsonDocument.Parse(artifact.IdentityJson);
            var path = json.RootElement.GetProperty("path").GetString()!;
            Require(await File.ReadAllTextAsync(path) == expected, "Wrong actual retained local bytes.");
        }
        internal async Task AssertStablePlaceholderPlanAsync(string path = "file.txt")
        {
            await Observer().ObserveAsync("manual");
            var outcome = (await Planner().PlanAsync(Pair.Id)).Single(item =>
                item.RelativePath == path);
            Require(outcome.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged && outcome.OperationId is null,
                "Reobserving a completed placeholder producer fabricated another synchronization operation.");
            var point = (await Db.LoadLatestSynchronizationPointsAsync(Pair.Id)).Single(item =>
                item.LocalRelativePath == path);
            var observed = (await Db.LoadLatestCompleteObservationAsync(Pair.Id, "local")).Single(item =>
                item.RelativePath == path);
            Require(point.LocalObjectVersionId == observed.ObjectVersionId,
                "Placeholder completion and observer retained different local ObjectVersionIds.");
            var version = await Db.LoadObjectVersionAsync(observed.ObjectVersionId);
            using var identity = JsonDocument.Parse(version.IdentityJson);
            Require(!identity.RootElement.GetProperty("cloudFiles").TryGetProperty("availability", out _),
                "Placeholder completion retained mutable availability in local version identity.");
        }
        internal async Task PreviewAsync(bool reject)
        {
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(Root, "state", "instance.lock"));
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connections = new DropboxConnectionStore(appLock.StateAuthority);
            await connections.SaveNewAsync(Connection);
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, Http, pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connections,
                new MemoryDropboxRefreshTokenStore("fixture-refresh"), Db);
            Wire.RejectDownloads = reject;
            var result = await host.LoadConflictContentAsync(Pair.Id, "file.txt");
            Require(reject ? result.DropboxBytes is null && result.Note is not null :
                    result.DropboxBytes is not null && Encoding.UTF8.GetString(result.DropboxBytes) == "Dropbox conflicting predecessor",
                "Runtime preview presented unavailable/wrong content as success.");
            Require(Wire.Uploads.Count == 0 && Wire.Deletes == 0, "Preview mutated provider.");
        }
        public ValueTask DisposeAsync()
        {
            Cloud?.Dispose();
            if (Cloud is not null && WindowsCloudFilesProvider.IsOwnedSyncRoot(Pair.LocalRoot, Pair.Id))
            {
                var hr = WindowsCloudFilesProvider.Native.CfUnregisterSyncRoot(Pair.LocalRoot);
                if (hr < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
            }
            Http.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
            if (NeutralRoot is not null) RecoveryNativeTestRoot.Remove(NeutralRoot);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RemoteFile(string Path, string Id, string Revision, byte[] Bytes)
    { internal string Hash => DropboxContentHash.Compute(Bytes); }
    private sealed record Upload(string Path, byte[] Bytes, bool Conditional);
    private sealed class WireStore : HttpMessageHandler
    {
        internal Dictionary<string, RemoteFile> Files = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, RemoteFile> History = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase);
        internal List<Upload> Uploads = [];
        internal List<Upload> RecoveryUploads => Uploads.Where(u =>
            u.Path.StartsWith(SyncDropboxArtifactPaths.RecoveryContainerPath + "/", StringComparison.Ordinal)).ToList();
        internal List<Upload> LegacyRecoveryUploads => Uploads.Where(u =>
            u.Path.StartsWith("/Apps/Sync Shark/Recovery/", StringComparison.Ordinal)).ToList();
        internal List<string> DownloadRevisions = [];
        internal bool FailNextDownload, RejectDownloads, MoveAfterDownload, OmitCreateFolderTag;
        internal string? RejectRevision;
        internal string? RevisionUploadFailure;
        internal Func<string, Task>? BeforeMutation;
        internal string? CreatedFolderReadbackNameOverride;
        internal string? CreatedFolderReadbackIdOverride;
        internal int RequestCount, Deletes, Copies, Restores, FoldersCreated;
        private int sequence;
        internal RemoteFile Put(string path, string text) => Put(path, Encoding.UTF8.GetBytes(text));
        internal RemoteFile Put(string path, byte[] bytes)
        {
            var file = new RemoteFile(path, Files.TryGetValue(path, out var old) ? old.Id : "id:fixture-" + (++sequence),
                "revision-" + (++sequence), bytes.ToArray());
            Files[path] = file;
            History[file.Revision] = file;
            return file;
        }
        internal string Read(string path) => Encoding.UTF8.GetString(Files[path].Bytes);
        internal void Remove(string path) => Files.Remove(path);
        internal void MoveOutside()
        { var f = Files["/Pilot/file.txt"]; Files.Remove(f.Path); Files["/Outside/file.txt"] = f with { Path = "/Outside/file.txt" }; }
        private static string Resolve(string path) => path.StartsWith("id:pilot-folder/", StringComparison.Ordinal)
            ? "/Pilot/" + path[16..]
            : path.StartsWith("id:recovery/", StringComparison.Ordinal)
                ? SyncDropboxArtifactPaths.RecoveryContainerPath + "/" + path[12..]
                : path;
        private static JsonObject Metadata(RemoteFile f, bool download = false) => new()
        {
            [".tag"] = "file", ["name"] = Path.GetFileName(f.Path), ["path_display"] = f.Path,
            ["path_lower"] = f.Path.ToLowerInvariant(), ["id"] = f.Id, ["rev"] = f.Revision,
            ["size"] = f.Bytes.Length, ["content_hash"] = f.Hash,
            // No modified time: newest wins cannot decide, so two-sided edits stay manual conflicts here.
            ["is_downloadable"] = true
        };
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        // A legitimate missing-path INPUT must carry Dropbox's authoritative tagged union.
        // An empty structured error is deliberately uncertainty in the production classifier;
        // do not substitute that malformed response for absence in successful restore cases.
        private static HttpResponseMessage Missing() => Json(new Dictionary<string, object>
        {
            ["error_summary"] = "path/not_found/",
            ["error"] = new Dictionary<string, object>
            {
                [".tag"] = "path",
                ["path"] = new Dictionary<string, object> { [".tag"] = "not_found" }
            }
        }, HttpStatusCode.Conflict);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/oauth2/token")) return Json(new { access_token = "fixture-access", token_type = "bearer", expires_in = 14400 });
            if (route.EndsWith("/users/get_current_account")) return Json(new
            {
                account_id = "account-id", root_info = new Dictionary<string, object>
                { [".tag"] = "user", ["root_namespace_id"] = "root-namespace", ["home_namespace_id"] = "home-namespace" }
            });
            var raw = request.Headers.TryGetValues("Dropbox-API-Arg", out var args) ? args.Single() : await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var input = doc.RootElement;
            if (route.EndsWith("/files/get_metadata"))
            {
                var path = Resolve(input.GetProperty("path").GetString()!);
                var folder = Folders.SingleOrDefault(x => x.Key == path || x.Value == path);
                if (folder.Key is not null)
                    return Json(new Dictionary<string, object> { [".tag"] = "folder",
                        ["id"] = CreatedFolderReadbackIdOverride ?? folder.Value,
                        ["name"] = CreatedFolderReadbackNameOverride ?? Path.GetFileName(folder.Key),
                        ["path_display"] = folder.Key, ["path_lower"] = folder.Key.ToLowerInvariant() });
                if (path is "id:pilot-folder" or "/Pilot" or "id:recovery" or "/Apps/Sync Shark/Recovery")
                {
                    var recovery = path is "id:recovery" or "/Apps/Sync Shark/Recovery";
                    return Json(new Dictionary<string, object> { [".tag"] = "folder", ["name"] = recovery ? "Recovery" : "Pilot",
                        ["id"] = recovery ? "id:recovery" : "id:pilot-folder",
                        ["path_display"] = recovery ? SyncDropboxArtifactPaths.RecoveryContainerPath : "/Pilot",
                        ["path_lower"] = recovery ? SyncDropboxArtifactPaths.RecoveryContainerPath.ToLowerInvariant() : "/pilot" });
                }
                var file = Files.Values.SingleOrDefault(f => path == f.Id) ?? Files.GetValueOrDefault(path);
                return file is null ? Missing() : Json(Metadata(file));
            }
            if (route.EndsWith("/files/list_folder/get_latest_cursor"))
                return Json(new { cursor = "fixture-cursor" });
            if (route.EndsWith("/files/list_folder"))
            {
                var entries = new List<JsonObject>();
                entries.AddRange(Folders.Where(folder => folder.Key.StartsWith("/Pilot/", StringComparison.Ordinal))
                    .Select(folder => new JsonObject
                    {
                        [".tag"] = "folder", ["name"] = Path.GetFileName(folder.Key),
                        ["path_display"] = folder.Key, ["path_lower"] = folder.Key.ToLowerInvariant(),
                        ["id"] = folder.Value
                    }));
                entries.AddRange(Files.Values.Where(file => file.Path.StartsWith("/Pilot/", StringComparison.Ordinal))
                    .Select(file => Metadata(file)));
                return Json(new { entries, has_more = false, cursor = "fixture-cursor" });
            }
            if (route.EndsWith("/files/list_revisions")) return Json(new
            { entries = History.Values.Where(f => f.Path == Resolve(input.GetProperty("path").GetString()!)).Select(f => Metadata(f)).ToArray(), is_deleted = false });
            if (route.EndsWith("/files/download"))
            {
                if (FailNextDownload) { FailNextDownload = false; throw new IOException("Injected bounded pre-effect download interruption."); }
                var revision = input.GetProperty("path").GetString()![4..];
                var f = History[revision];
                DownloadRevisions.Add(revision);
                var metadata = Metadata(f);
                metadata.Remove(".tag");
                if (!f.Path.EndsWith("/.syncshark-owner.json", StringComparison.Ordinal))
                { metadata.Remove("path_display"); metadata.Remove("path_lower"); }
                if (RejectDownloads || RejectRevision == revision) metadata["id"] = "id:foreign-object";
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Bytes) };
                response.Headers.Add("Dropbox-API-Result", metadata.ToJsonString());
                if (MoveAfterDownload) { MoveAfterDownload = false; MoveOutside(); }
                return response;
            }
            if (route.EndsWith("/files/upload"))
            {
                var path = Resolve(input.GetProperty("path").GetString()!);
                if (BeforeMutation is not null) await BeforeMutation(path);
                var mode = input.GetProperty("mode");
                var conditional = mode.ValueKind == JsonValueKind.Object;
                if (conditional && RevisionUploadFailure == "race") Put(path, "new collaborator bytes");
                if (conditional ? !Files.TryGetValue(path, out var old) || old.Revision != mode.GetProperty("update").GetString() : Files.ContainsKey(path))
                    return Missing();
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                Uploads.Add(new Upload(path, bytes.ToArray(), conditional));
                var successor = Put(path, bytes);
                if (conditional && RevisionUploadFailure == "lost-reply") throw new IOException("Injected reply loss after conditional upload.");
                if (conditional && RevisionUploadFailure == "cancelled") throw new OperationCanceledException("Injected cancellation after conditional upload.");
                if (conditional && RevisionUploadFailure == "malformed") return Json(new { malformed = true });
                return Json(Metadata(successor));
            }
            if (route.EndsWith("/files/delete_v2"))
            {
                var requested = input.GetProperty("path").GetString()!;
                var path = Files.Values.SingleOrDefault(file => file.Id == requested)?.Path ?? Resolve(requested);
                if (!Files.TryGetValue(path, out var f) || f.Revision != input.GetProperty("parent_rev").GetString()) return Missing();
                Files.Remove(path); Deletes++; return Json(new { metadata = Metadata(f) });
            }
            if (route.EndsWith("/files/copy_v2"))
            {
                var sourceId = input.GetProperty("from_path").GetString();
                var path = Resolve(input.GetProperty("to_path").GetString()!);
                var source = Files.Values.SingleOrDefault(f => f.Id == sourceId);
                if (source is null || Files.ContainsKey(path) || Folders.ContainsKey(path)) return Missing();
                var copied = Put(path, source.Bytes); Copies++;
                return Json(new { metadata = Metadata(copied) });
            }
            if (route.EndsWith("/files/restore"))
            {
                var path = Resolve(input.GetProperty("path").GetString()!);
                var revision = input.GetProperty("rev").GetString()!;
                if (!History.TryGetValue(revision, out var historical)) return Missing();
                var restored = new RemoteFile(path, historical.Id, "revision-" + (++sequence), historical.Bytes.ToArray());
                Files[path] = restored; History[restored.Revision] = restored; Restores++;
                return Json(Metadata(restored));
            }
            if (route.EndsWith("/files/create_folder_v2"))
            {
                var path = Resolve(input.GetProperty("path").GetString()!);
                if (Files.ContainsKey(path) || Folders.ContainsKey(path)) return Missing();
                var id = "id:fixture-folder-" + (++sequence);
                Folders[path] = id; FoldersCreated++;
                var metadata = new Dictionary<string, object>
                {
                    ["name"] = Path.GetFileName(path), ["id"] = id,
                    ["path_display"] = path, ["path_lower"] = path.ToLowerInvariant()
                };
                if (!OmitCreateFolderTag) metadata[".tag"] = "folder";
                return Json(new { metadata });
            }
            throw new InvalidOperationException("Unexpected bounded fixture route: " + route);
        }
    }
}
