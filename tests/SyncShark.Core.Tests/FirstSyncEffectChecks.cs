using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// First-sync CLI effect proof: the real CLI parser/dispatcher drives the real operations base, the real host
// admission/planner/executor and the real first-sync apply, with the existing FakeSyncRemoteStore supplying
// external input through the host's remoteFactory. No live provider, credential or production state is used
// and every real HTTP attempt is rejected.
internal static class FirstSyncEffectChecks
{
    private static bool IsOriginalReviewedRun(SyncAuthorityRunHistory run, string pairId)
    {
        using var json = JsonDocument.Parse(run.RequestJson);
        var value = json.RootElement;
        return value.TryGetProperty("action", out var action) && action.GetString() == "reviewed_first_sync" &&
            value.TryGetProperty("pairIds", out var pairs) && pairs.EnumerateArray().Any(item => item.GetString() == pairId) &&
            (!value.TryGetProperty("retryOfRunId", out var parent) || parent.ValueKind == JsonValueKind.Null);
    }

    internal static async Task RunVerifiedRetryAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh verified-retry fixture root.");
        foreach (var reviewed in new[] { true, false })
        {
            var fixture = Path.Combine(root, reviewed ? "reviewed" : "approved");
            var local = Path.Combine(fixture, "local");
            Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, "a-good.txt"), "successful sibling bytes");
            await File.WriteAllTextAsync(Path.Combine(local, "b-failed.txt"), "selected retry bytes");
            var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true,
                UploadFailureForTest = path => path == "b-failed.txt"
                    ? new IOException("fixture: connection lost before upload acceptance") : null };
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            var authority = instance.StateAuthority;
            var connections = new DropboxConnectionStore(authority);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:verified-retry", "retry-root", "retry-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            await connections.SaveNewAsync(binding);
            var database = new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db"));
            using var http = new HttpClient(new NoNetworkHandler());
            using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(), database, (_, _) => remote);
            await host.LoadPairsAsync();
            using var lease = PilotDirectoryLease.Open(local);
            var pair = SyncPair.FromPilot(new PilotSettings(local, lease.Identity.VolumeSerialNumber,
                lease.Identity.FileId, "/VerifiedRetry", "id:verified-retry"), "Verified retry", binding.RootNamespaceId) with
            { ConnectionId = binding.ConnectionId, InitialReviewApproved = !reviewed, PropagateDeletions = false };
            // A removed predecessor keeps its historical definition but relinquishes folder
            // ownership. Missing/inactive roots on that retired pair must not prevent this
            // replacement from saving its final first-sync approval.
            var retired = pair with { Id = "retired-" + pair.Id, Name = "Retired predecessor" };
            await database.RegisterPairAsync(retired.Id, retired.PolicyRevision, new SyncAuthorityPairConfiguration(
                "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: retired.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(retired, select: true);
            await database.RetirePairDefinitionAsync(retired.Id);
            if (!(await database.LoadAllPairDefinitionsAsync()).Any(item => item.Id == retired.Id) ||
                (await database.LoadAllPairDefinitionsAsync(includeRetired: false)).Any(item => item.Id == retired.Id))
                throw new InvalidOperationException("Retired history and current folder ownership must remain distinct.");
            await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(pair, select: true);
            var operations = new FixtureOperations(host, authority);
            async Task<(int Exit, JsonDocument? Data, string Error)> Cli(params string[] arguments)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                var exit = await SyncCli.ExecuteAsync(arguments, output, error, () => operations);
                return (exit, output.ToString().Trim().Length == 0 ? null : JsonDocument.Parse(output.ToString()), error.ToString());
            }
            void Require(bool value, string message)
            { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + (reviewed ? "reviewed " : "approved ") + message); }
            var scan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json");
            Require(scan.Exit == 0, "initial inventory: " + (scan.Exit == 0 ? "complete" : scan.Error));
            if (reviewed)
            {
                await File.WriteAllTextAsync(Path.Combine(local, "late-conflict.txt"), "late local version");
                remote.PutBytes("late-conflict.txt", Encoding.UTF8.GetBytes("late cloud version"));
                scan = await Cli("--cli", "first-sync", "prepare", "--pair", pair.Id, "--yes", "--json");
                var late = await Cli("--cli", "conflicts", "show", "--pair", pair.Id,
                    "--path", "late-conflict.txt", "--json");
                Require(scan.Exit == 0 && late.Exit == 0 && remote.UploadCount == 0 &&
                    await File.ReadAllTextAsync(Path.Combine(local, "late-conflict.txt")) == "late local version",
                    "a later first-sync scan exposes a new conflict without changing either version");
                var conflict = late.Data!.RootElement.GetProperty("data").GetProperty("conflict");
                var resolve = await Cli("--cli", "conflicts", "resolve", "--pair", pair.Id,
                    "--path", "late-conflict.txt", "--key", conflict.GetProperty("conflictId").GetString()!,
                    "--generation", conflict.GetProperty("generation").GetInt64().ToString(),
                    "--choice", "KeepDropbox", "--yes", "--json");
                Require(resolve.Exit == 0 && remote.UploadCount == 0 &&
                    await File.ReadAllTextAsync(Path.Combine(local, "late-conflict.txt")) == "late cloud version",
                    "the later first-sync conflict can be resolved through the real CLI");
                scan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json");
                Require(scan.Exit == 0, "resolved conflict remains identical during the next first-sync scan");
            }
            var data = scan.Data!.RootElement.GetProperty("data");
            var choices = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = data.GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = data.GetProperty("dropboxScanGenerationId").GetString(),
                ["a-good.txt"] = "upload", ["b-failed.txt"] = "upload"
            });
            string? interruptedPlanningRun = null;
            if (reviewed)
            {
                SyncAuthorityOperationPlan? prefix = null;
                var planned = 0;
                database.BeforeOperationRegistrationForTest = plan =>
                {
                    if (plan.Kind != SyncAuthorityOperationKind.CreateRemote) return;
                    if (++planned == 1) prefix = plan;
                    else throw new IOException("fixture interrupted during planning before transfer dispatch");
                };
                try
                {
                    var interruptedPlanning = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
                        "--choices", choices, "--yes", "--json");
                    Require(interruptedPlanning.Exit != 0 && remote.UploadCount == 0 && prefix is not null,
                        "planning interruption retains a prefix without issuing any upload");
                }
                finally { database.BeforeOperationRegistrationForTest = null; }
                interruptedPlanningRun = (await database.LoadRunHistoryAsync(20)).First(item =>
                    item.RequestJson.Contains("Reviewed first sync") && item.RequestJson.Contains(pair.Id)).Id;
                var prefixId = prefix!.OperationId;
                var prefixPath = (await database.LoadRetryOperationEvidenceAsync([prefixId])).Single().Path;
                async Task RequirePrefix(string label)
                {
                    var retryPreview = await host.LoadRunRetryAsync(interruptedPlanningRun, selectedPairId: pair.Id);
                    Require(retryPreview.UnavailableReason is null && retryPreview.Items.Count == 2 &&
                        retryPreview.Items.All(item => item.CanRetry && item.State == "Not attempted") &&
                        retryPreview.Items.Single(item => item.Path == prefixPath).OperationId == prefixId,
                        label + " retains the exact planned operation before any per-file log event");
                }
                await RequirePrefix("explicit group/run ownership");

                // A freshly started retry has no cached health admission. Its
                // real read path must forward the existing database diagnostics,
                // not leave CLI/GUI consumers on one generic startup message.
                using (var coldHost = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                    new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(),
                    new SyncAuthorityDatabase(database.DatabasePath), (_, _) => remote))
                {
                    var messages = new List<string>();
                    var coldPreview = await coldHost.LoadRunRetryAsync(interruptedPlanningRun,
                        selectedPairId: pair.Id, onReadProgress: messages.Add);
                    Require(coldPreview.UnavailableReason is null && remote.UploadCount == 0 &&
                        messages.Count(message => message.Contains("running SQLite PRAGMA quick_check", StringComparison.Ordinal)) == 0 &&
                        messages.Any(message => message.Contains("Startup: loading retained folder-pair definitions.", StringComparison.Ordinal)) &&
                        messages.Any(message => message.Contains("reading the exact retained command_request row", StringComparison.Ordinal)),
                        "eligible cold retry preview is tentative and read-only; full health is reserved for actual command admission");
                }

                // A metadata lookup is NOT a cached healthy verdict. Change the
                // isolated fixture after that read; startup must reject it before
                // recovery, command registration, or provider effects.
                using (var changedHost = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                    new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(),
                    new SyncAuthorityDatabase(database.DatabasePath), (_, _) => remote))
                using (var mutation = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database.DatabasePath))
                {
                    await mutation.OpenAsync();
                    using var sql = mutation.CreateCommand();
                    sql.CommandText = "PRAGMA user_version;";
                    var version = Convert.ToInt32(await sql.ExecuteScalarAsync());
                    sql.CommandText = "SELECT count(*) FROM command_request;";
                    var commandsBefore = Convert.ToInt64(await sql.ExecuteScalarAsync());
                    var uploadsBefore = remote.UploadCount;
                    var downloadsBefore = remote.DownloadCount;
                    var changed = false;
                    var refused = false;
                    try
                    {
                        await changedHost.LoadRunRetryAsync(interruptedPlanningRun, selectedPairId: pair.Id,
                            onReadProgress: message =>
                            {
                                if (changed || !message.StartsWith("Checking the retained pair identity", StringComparison.Ordinal)) return;
                                sql.CommandText = "PRAGMA user_version=999;";
                                sql.ExecuteNonQuery();
                                changed = true;
                            });
                    }
                    catch (InvalidDataException) { refused = true; }
                    finally
                    {
                        sql.CommandText = "PRAGMA user_version=" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
                        sql.ExecuteNonQuery();
                    }
                    sql.CommandText = "SELECT count(*) FROM command_request;";
                    Require(changed && refused && remote.UploadCount == uploadsBefore && remote.DownloadCount == downloadsBefore &&
                        Convert.ToInt64(await sql.ExecuteScalarAsync()) == commandsBefore,
                        "a schema change after retry metadata inspection is freshly rejected before any command or file effect");
                }

                // Compatibility fixture only: reproduce a pre-linkage retained
                // group. No production state or runtime repair SQL is used.
                using var edit = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database.DatabasePath);
                await edit.OpenAsync();
                async Task ChangeGroup(string expression)
                {
                    using var sql = edit.CreateCommand();
                    sql.CommandText = "UPDATE operation_group SET detail_json=" + expression + " WHERE operation_group_id=$group;";
                    sql.Parameters.AddWithValue("$group", prefix.OperationGroupId);
                    await sql.ExecuteNonQueryAsync();
                }
                await ChangeGroup("json_remove(detail_json,'$.runId')");
                await RequirePrefix("legacy exact original scan/path/direction ownership");
                await ChangeGroup("json_set(detail_json,'$.runId','foreign-run')");
                Require(!(await database.LoadRunOperationIdsAsync(interruptedPlanningRun, pair.Id)).Contains(prefixId),
                    "an explicit foreign group owner cannot fall back to legacy timing");
                await ChangeGroup("json_remove(detail_json,'$.runId')");
                using var altered = edit.CreateCommand();
                altered.CommandText = "UPDATE command_request SET payload_json=json_set(payload_json,'$.reviewedPlan.LocalScanGenerationId',$scan) WHERE command_request_id=$id;";
                altered.Parameters.AddWithValue("$id", interruptedPlanningRun);
                altered.Parameters.AddWithValue("$scan", "foreign-scan");
                await altered.ExecuteNonQueryAsync();
                Require(!(await database.LoadRunOperationIdsAsync(interruptedPlanningRun, pair.Id)).Contains(prefixId),
                    "legacy timing without the exact reviewed inventory cannot claim a retained operation");
                altered.Parameters["$scan"].Value = data.GetProperty("localScanGenerationId").GetString()!;
                await altered.ExecuteNonQueryAsync();
                await RequirePrefix("restored exact legacy approval");
            }
            async Task<(int Exit, JsonDocument? Data, string Error)> ColdRetry()
            {
                using var cold = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                    new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(),
                    new SyncAuthorityDatabase(database.DatabasePath), (_, _) => remote);
                var healthCount = 0;
                SyncAuthorityDatabase.HealthCheckCompletedForTest = _ => healthCount++;
                using var output = new StringWriter(); using var error = new StringWriter();
                int exit;
                try
                {
                    exit = await SyncCli.ExecuteAsync(["--cli", "run", "retry", "--run", interruptedPlanningRun!,
                        "--pair", pair.Id, "--choice", "all", "--yes", "--json"], output, error,
                        () => new FixtureOperations(cold, authority));
                }
                finally { SyncAuthorityDatabase.HealthCheckCompletedForTest = null; }
                if (healthCount != 0)
                    throw new InvalidOperationException("Cold CLI retry health-check count was " + healthCount + ": " + error);
                Require(healthCount == 0, "actual cold CLI retry retains command guards without a full health scan");
                Require(error.ToString().Split("Retry preview ready:", StringSplitOptions.None).Length - 1 == 1,
                    "cold CLI retry reuses its single tentative preparation preview; locked handoff remains fresh");
                return (exit, output.ToString().Trim().Length == 0 ? null : JsonDocument.Parse(output.ToString()), error.ToString());
            }
            var first = reviewed
                ? await ColdRetry()
                : await Cli("--cli", "run", "--pair", pair.Id, "--yes", "--json");
            Require(first.Exit != 0 && remote.UploadCount == 1 && remote.ReadText("a-good.txt") == "successful sibling bytes",
                "partial failure preserves completed sibling");
            if (reviewed)
                Require(first.Error.Contains("reading the exact retained command_request row", StringComparison.Ordinal),
                    "CLI retry forwards retained-run preparation details to its live error stream");
            var origin = (await database.LoadRunHistoryAsync(20)).Single(item =>
                item.RequestJson.Contains(pair.Id) && item.ResultJson?.Contains("b-failed.txt") == true &&
                item.ResultJson.Contains("\"files\""));
            var preview = await host.LoadRunRetryAsync(origin.Id);
            var failed = preview.Items.Single(item => item.Path == "b-failed.txt");
            Require(!failed.CanRetry && failed.OperationId is not null, "uncertain upload requires verification first");
            await Refuse("uncertain outcome", origin.Id, failed.Path, failed.OperationId!, pair.PolicyRevision);
            var held = await database.AcquireOperationLeaseAsync(failed.OperationId!, "fixture-lease", TimeSpan.FromMinutes(1));
            try { await Refuse("active lease", origin.Id, failed.Path, failed.OperationId!, pair.PolicyRevision); }
            finally { await database.ReleaseLeaseAsync(held); }
            var before = remote.UploadCount;
            remote.UploadFailureForTest = null;
            var unrelatedRun = "test-unrelated-" + Guid.NewGuid().ToString("N");
            await database.CreateCommandRequestAsync(unrelatedRun, "run_pair", pair.Id, null, null,
                JsonSerializer.Serialize(new { pairIds = new[] { pair.Id }, policyRevision = pair.PolicyRevision }));
            await database.TransitionCommandRequestAsync(unrelatedRun, "accepted");
            await database.TransitionCommandRequestAsync(unrelatedRun, "executing");
            await database.TransitionCommandRequestAsync(unrelatedRun, "completed", "{}");
            var stateBeforeUnrelated = (await database.LoadOperationAsync(failed.OperationId!)).Status;
            var unrelatedVerify = await Cli("--cli", "run", "verify", "--run", unrelatedRun, "--yes", "--json");
            Require(unrelatedVerify.Exit == 0 && remote.UploadCount == before &&
                (await database.LoadOperationAsync(failed.OperationId!)).Status == stateBeforeUnrelated,
                "verifying another run does not reconcile or dispatch this run's operation");
            var verification = await Cli("--cli", "run", "verify", "--run", origin.Id, "--yes", "--json");
            Require(verification.Exit == 0 && remote.UploadCount == before && !remote.Exists("b-failed.txt"),
                "verify reconciles exact outcome without transfer: " + (verification.Exit == 0 ? "no new effect" : verification.Error));
            preview = await host.LoadRunRetryAsync(origin.Id);
            failed = preview.Items.Single(item => item.Path == "b-failed.txt");
            Require(failed.CanRetry && !preview.Items.Single(item => item.Path == "a-good.txt").CanRetry &&
                (await database.LoadOperationAsync(failed.OperationId!)).Status == SyncAuthorityOperationStatus.Ready,
                "verified absence is Ready and only failed file is retryable");
            var repeatedVerify = await Cli("--cli", "run", "verify", "--run", origin.Id, "--yes", "--json");
            Require(repeatedVerify.Exit == 0 && remote.UploadCount == before, "repeated verification never dispatches Ready work");
            // A deadline may retain per-operation events but no terminal files array.
            // Reconstruct that supported persistence boundary without rewriting the
            // actual run or any operation/receipt. The public preview must retain the
            // exact failure, not call it unattempted or authorize a completed sibling.
            var interrupted = "test-interrupted-" + Guid.NewGuid().ToString("N");
            await database.CreateCommandRequestAsync(interrupted, "run_pair", pair.Id, null, null, origin.RequestJson);
            await database.TransitionCommandRequestAsync(interrupted, "accepted");
            await database.TransitionCommandRequestAsync(interrupted, "executing");
            const string retainedReason = "fixture: provider refused this exact file before acceptance";
            var successful = preview.Items.Single(item => item.Path == "a-good.txt");
            await LegacyHistoryFixture.SeedAsync(database, [
                new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, [SyncActivityCategories.Sync],
                    "Information", "File", "Upload", "Completed", PairId: pair.Id, RunId: interrupted,
                    OperationId: successful.OperationId, RelativePath: successful.Path, Phase: SyncActivityPhase.Completed),
                new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, [SyncActivityCategories.Sync],
                    "Error", "File", "Upload", retainedReason, PairId: pair.Id, RunId: interrupted,
                    OperationId: failed.OperationId, RelativePath: "presentation-is-not-path-authority.txt", Phase: SyncActivityPhase.Blocked),
                new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, [SyncActivityCategories.Sync],
                    "Error", "File", "Upload", "Unrelated run must not replace the failure", PairId: pair.Id, RunId: unrelatedRun,
                    OperationId: failed.OperationId, RelativePath: failed.Path, Phase: SyncActivityPhase.Blocked)
            ]);
            await database.TransitionCommandRequestAsync(interrupted, "blocked", JsonSerializer.Serialize(new
                { pairs = new[] { new { pairId = pair.Id, policyRevision = pair.PolicyRevision } } }));
            var interruptedPreview = await Cli("--cli", "run", "retry-plan", "--run", interrupted,
                "--pair", pair.Id, "--json");
            var interruptedData = interruptedPreview.Data!.RootElement.GetProperty("data");
            var interruptedItems = interruptedData.GetProperty("items").Deserialize<SyncRunRetryItem[]>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Require(interruptedPreview.Exit == 0 && interruptedData.GetProperty("failed").GetInt32() == 1 &&
                interruptedData.GetProperty("notAttempted").GetInt32() == 0 &&
                interruptedItems.Single(item => item.Path == failed.Path) is { State: "Failed", CanRetry: true } failureItem &&
                failureItem.Reason == retainedReason &&
                interruptedItems.Single(item => item.Path == successful.Path) is { State: "Completed", CanRetry: false } &&
                remote.UploadCount == before,
                "public retry preview retains exact failed-file reason without terminal summaries, ignores foreign-run events and never replays completed effects");
            // Exercise the actual transactional handoff rejection, without rewriting retained rows.
            async Task Refuse(string label, string run, string path, string operationId, long revision,
                bool includeValidSibling = false)
            {
                var command = "test-retry-" + Guid.NewGuid().ToString("N");
                await database.CreateCommandRequestAsync(command, "run_pair", pair.Id, null, null,
                    JsonSerializer.Serialize(new { retryOfRunId = run, policyRevision = revision }));
                await database.TransitionCommandRequestAsync(command, "accepted");
                await database.TransitionCommandRequestAsync(command, "executing");
                var unchanged = (await database.LoadOperationAsync(failed.OperationId!)).Status;
                var refused = false;
                try { await database.SupersedeRetryReadyOperationsAsync(run, command, pair.Id, revision,
                    includeValidSibling ? [(failed.Path, failed.OperationId!), (path, operationId)] : [(path, operationId)]); }
                catch (InvalidOperationException) { refused = true; }
                finally { await database.TransitionCommandRequestAsync(command, "blocked", "{}"); }
                Require(refused && (await database.LoadOperationAsync(failed.OperationId!)).Status == unchanged,
                    "handoff refuses " + label + " without changing selected reservation");
            }
            await Refuse("foreign run", "not-the-origin", failed.Path, failed.OperationId!, pair.PolicyRevision);
            await Refuse("wrong path", origin.Id, "other.txt", failed.OperationId!, pair.PolicyRevision);
            await Refuse("wrong policy", origin.Id, failed.Path, failed.OperationId!, pair.PolicyRevision + 1);
            var completed = preview.Items.Single(item => item.Path == "a-good.txt");
            await Refuse("completed operation", origin.Id, completed.Path, completed.OperationId!, pair.PolicyRevision);
            await Refuse("mixed valid/invalid batch atomically", origin.Id, completed.Path, completed.OperationId!,
                pair.PolicyRevision, includeValidSibling: true);
            var retry = await Cli("--cli", "run", "retry", "--run", origin.Id, "--pair", pair.Id,
                "--choice", "all", "--selection", "[\"b-failed.txt\"]", "--yes", "--json");
            Require(retry.Exit == 0 && remote.UploadCount == before + 1 && remote.ReadText("b-failed.txt") == "selected retry bytes",
                "explicit retry transfers failed file exactly once: " + (retry.Exit == 0 ? "complete" : retry.Error));
            Require((await database.LoadRunByIdAsync(origin.Id))!.ResultJson == origin.ResultJson &&
                (await database.LoadOperationAsync(failed.OperationId!)).Status == SyncAuthorityOperationStatus.Superseded,
                "retry preserves original failure history and retires only its old reservation");
            Require((await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id).InitialReviewApproved == true &&
                (await database.LoadAllPairDefinitionsAsync()).Any(item => item.Id == retired.Id),
                "first sync saves approval without treating retired history as a current folder owner");
            await database.VerifyHealthAsync();
        }
    }

    internal static async Task RunDirectoryConcurrencyAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh directory first-sync fixture root.");
        foreach (var occupied in new[] { false, true })
        {
            var fixture = Path.Combine(root, occupied ? "occupied-target" : "success");
            var local = Path.Combine(fixture, "local");
            Directory.CreateDirectory(local);
            var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            remote.PutDirectory("a"); remote.PutDirectory("b"); remote.PutDirectory("a/child");
            remote.Put("a/child/ok.txt", "verified child payload");
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            var authority = instance.StateAuthority;
            var connections = new DropboxConnectionStore(authority);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:directory-concurrency", "directory-root", "directory-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            await connections.SaveNewAsync(binding);
            var database = new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db"));
            using var http = new HttpClient(new NoNetworkHandler());
            using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(), database, (_, _) => remote);
            await host.LoadPairsAsync();
            using var rootLease = PilotDirectoryLease.Open(local);
            var pair = SyncPair.FromPilot(new PilotSettings(local, rootLease.Identity.VolumeSerialNumber,
                rootLease.Identity.FileId, "/DirectoryConcurrency", "id:directory-concurrency"),
                "Directory concurrency", binding.RootNamespaceId) with
            { ConnectionId = binding.ConnectionId, InitialReviewApproved = false, PropagateDeletions = false };
            await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(pair, select: true);
            var operations = new FixtureOperations(host, authority);
            async Task<(int Exit, JsonDocument Result, string Progress)> Cli(params string[] args)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                var exit = await SyncCli.ExecuteAsync(args, output, error, () => operations);
                return (exit, JsonDocument.Parse(output.ToString()), error.ToString());
            }
            var scan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json");
            using var scanResult = scan.Result;
            if (scan.Exit != 0) throw new InvalidOperationException("Directory fixture scan: " + scan.Progress);
            var scanData = scanResult.RootElement.GetProperty("data");
            var choices = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = scanData.GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = scanData.GetProperty("dropboxScanGenerationId").GetString()
            });
            var entered = 0;
            var siblings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.BeforeReadEntryForTest = async (path, token) =>
            {
                if (path is "a" or "b")
                {
                    if (path == "b" && occupied)
                    {
                        Directory.CreateDirectory(Path.Combine(local, "b"));
                        await File.WriteAllTextAsync(Path.Combine(local, "b", "external-keep.txt"), "preserve", token);
                    }
                    if (Interlocked.Increment(ref entered) == 2) siblings.TrySetResult();
                    await siblings.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                }
                if (path.StartsWith("a/", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(local, "a")))
                    throw new InvalidOperationException("A child entered its executor before the native parent existed.");
            };
            // Fixed fixture capacity makes overlap deterministic; resource policy is
            // independently exercised by the unchanged adaptive admission suite.
            using var concurrency = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(4);
            var apply = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json",
                "--choices", choices, "--allow-recommended", "--timeout-seconds", "30");
            using var applyResult = apply.Result;
            var data = applyResult.RootElement.GetProperty("data");
            if (!siblings.Task.IsCompletedSuccessfully || entered != 2 ||
                data.GetProperty("firstSyncComplete").GetBoolean() == occupied ||
                !Directory.Exists(Path.Combine(local, "a", "child")) ||
                await File.ReadAllTextAsync(Path.Combine(local, "a", "child", "ok.txt")) != "verified child payload")
                throw new InvalidOperationException("Real directory CLI overlap/parent/copy outcome failed: " + apply.Progress);
            if (occupied && await File.ReadAllTextAsync(Path.Combine(local, "b", "external-keep.txt")) != "preserve")
                throw new InvalidOperationException("Occupied directory contents were changed.");
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND o.kind='create_local' AND o.status='completed' AND r.certainty='known_success';";
            command.Parameters.AddWithValue("$pair", pair.Id);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) != (occupied ? 3 : 4) || remote.DownloadCount != 1)
                throw new InvalidOperationException("Directory creates did not retain exact successful receipts without duplicate file transfer.");
            Console.WriteLine(occupied
                ? "PASS real CLI sibling creates overlap; occupied target retained; independent parent/child/file peers complete with exact receipts"
                : "PASS real CLI sibling directory creates overlap; parent precedes child; all four exact receipts and one file download retained");
        }
    }

    internal static async Task RunPolicyHandoffAsync(string root)
    {
        await RunPolicyHandoffCaseAsync(root, null);
        foreach (var pause in new[] { "global", "account", "pair" })
            await RunPolicyHandoffCaseAsync(Path.Combine(root, "late-" + pause), pause);
    }

    private static async Task RunPolicyHandoffCaseAsync(string root, string? latePause)
    {
        Directory.CreateDirectory(root);
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        remote.PutBytes("cloud.txt", Encoding.UTF8.GetBytes("retained cloud payload"));
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var connections = new DropboxConnectionStore(authority);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:policy-handoff", "handoff-root", "handoff-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var http = new HttpClient(new NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(), database,
            (_, _) => remote);
        await host.LoadPairsAsync();
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/PolicyHandoff", "id:policy-handoff"),
            "Policy handoff pair", binding.RootNamespaceId) with
        {
            ConnectionId = binding.ConnectionId, InitialReviewApproved = false, PropagateDeletions = false
        };
        var configuration = new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
            "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
            RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue);
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, configuration);
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FixtureOperations(host, authority);
        using var scanOutput = new StringWriter();
        using var scanError = new StringWriter();
        var scanExit = await SyncCli.ExecuteAsync(["--cli", "scan", "--pair", pair.Id, "--yes", "--json"],
            scanOutput, scanError, () => operations);
        if (scanExit != 0) throw new InvalidOperationException("Policy handoff scan failed: " + scanError);
        using var scan = JsonDocument.Parse(scanOutput.ToString());
        var scanData = scan.RootElement.GetProperty("data");
        var choices = new Dictionary<string, string?>
        {
            ["__plan"] = scanData.GetProperty("localScanGenerationId").GetString(),
            ["__remote_plan"] = scanData.GetProperty("dropboxScanGenerationId").GetString(),
            ["cloud.txt"] = "download"
        };
        var changed = false;
        using var output = new StringWriter();
        using var error = new HandoffWriter(value =>
        {
            if (latePause is not null || changed || !value.Contains("Applying ", StringComparison.Ordinal) ||
                !value.Contains("(" + pair.Id + ")", StringComparison.Ordinal)) return;
            changed = true;
            // Supply a real retained policy change precisely after the CLI's last
            // revision read and before Host admission. Do not replace that decision.
            database.RegisterPairAsync(pair.Id, checked(pair.PolicyRevision + 1), configuration)
                .GetAwaiter().GetResult();
            database.RetainPairDefinitionAsync(pair with { PolicyRevision = checked(pair.PolicyRevision + 1) },
                select: true).GetAwaiter().GetResult();
        });
        var previousHealthObserver = SyncAuthorityDatabase.CommandAdmissionCheckedForTest;
        var preferences = new UiPreferencesStore(authority);
        var originalPreferences = preferences.Load();
        if (latePause is not null)
            SyncAuthorityDatabase.CommandAdmissionCheckedForTest = path =>
            {
                previousHealthObserver?.Invoke(path);
                if (changed || !string.Equals(path, database.DatabasePath, StringComparison.OrdinalIgnoreCase)) return;
                // Fresh effect admission runs inside the published preparing-run reservation. Global and
                // account pause changes are rechecked before effects; a pair configuration mutation must
                // now be refused by that reservation without changing the retained pair.
                changed = true;
                if (latePause == "pair")
                    host.SetPairEnabledAsync(pair.Id, false).GetAwaiter().GetResult();
                else
                    preferences.SaveAsync(latePause == "global"
                        ? originalPreferences with { PauseUntilResumed = true }
                        : originalPreferences with { AccountPauses = [new(binding.ConnectionId, true, null)] })
                        .GetAwaiter().GetResult();
            };
        int exit;
        try
        {
            exit = await SyncCli.ExecuteAsync(["--cli", "first-sync", "apply", "--pair", pair.Id,
                    "--yes", "--json", "--generation", pair.PolicyRevision.ToString(), "--choices",
                    JsonSerializer.Serialize(choices)], output, error, () => operations);
        }
        finally { SyncAuthorityDatabase.CommandAdmissionCheckedForTest = previousHealthObserver; }
        var expectedReason = latePause is "global" or "account" ? "paused" : "pair settings changed";
        var rejected = exit != 0 && error.ToString().Contains(expectedReason, StringComparison.OrdinalIgnoreCase);
        if (latePause == "pair")
        {
            var retainedPair = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
            rejected = exit != 0 && error.ToString().Contains(
                "Wait for the active sync to reach its checkpoint before changing a folder pair.", StringComparison.Ordinal) &&
                retainedPair.PolicyRevision == pair.PolicyRevision && retainedPair.Enabled == pair.Enabled;
        }
        var noEffects = remote.UploadCount == 0 && remote.DownloadCount == 0 &&
            !File.Exists(Path.Combine(localRoot, "cloud.txt"));
        if (!changed || !rejected || !noEffects)
            throw new InvalidOperationException("Policy handoff must reject before effects: changed=" + changed +
                ", exit=" + exit + ", uploads=" + remote.UploadCount + ", downloads=" + remote.DownloadCount +
                "; stderr=" + error);
        Console.WriteLine(latePause is null
            ? "PASS real CLI policy change at final handoff is rejected before provider or local effects"
            : latePause == "pair"
                ? "PASS a pair mutation during real CLI preparation is refused at the active-run checkpoint boundary, retains the original pair and starts no file effect"
                : "PASS real CLI " + latePause + " pause arriving during fresh effect admission is rejected under the execution lock with zero transfers");
    }

    private static async Task CheckSelectionDirectoryMetadataAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "nested", "child"));
        var path = Path.Combine(root, "nested", "locked.txt");
        await File.WriteAllTextAsync(path, "selection refresh must not open these bytes");
        using var rootLease = PilotDirectoryLease.Open(root);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var seen = new List<string>();
        var metadata = await new PilotLocalScanner().ScanAsync(rootLease,
            onInventory: (item, _) => { seen.Add(item.Replace('\\', '/')); return Task.CompletedTask; },
            directoriesOnly: true);
        if (metadata.FilesHashed != 0 || metadata.TotalBytes != 0 || metadata.Decisions.Count != 0 ||
            metadata.Entries.Count != 2 || metadata.Entries.Values.Any(item =>
                item.Kind != PilotEntryKind.Directory || item.FileId is null || item.VolumeSerialNumber is null) ||
            !seen.Contains("nested/locked.txt"))
            throw new InvalidOperationException("Selection metadata scan must retain verified directories/progress without opening locked file contents.");
        var normal = await new PilotLocalScanner().ScanAsync(rootLease);
        if (!normal.Decisions.Any(item => item.RelativePath == "nested/locked.txt"))
            throw new InvalidOperationException("Default content scan must still report the unreadable file, not silently omit it.");
        Console.WriteLine("PASS directory-only selection refresh retains directory identities and live paths without opening file content; default scan still reports unreadable content");
    }

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The first-sync effect fixture requires Windows.");
        Directory.CreateDirectory(root);
        CheckAncestorPathIndex();
        await CheckSelectionDirectoryMetadataAsync(Path.Combine(root, "selection-directory-metadata"));
        await RunPolicyHandoffAsync(Path.Combine(root, "policy-handoff"));
        await RemoteEntryLookupChecks.RunAsync();
        await ExactItemDispatchChecks.RunAsync();
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        var uploadBytes = Encoding.UTF8.GetBytes("first-sync local payload");
        await File.WriteAllBytesAsync(Path.Combine(localRoot, "upload.txt"), uploadBytes);
        var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        var pairRemotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);
        var downloadBytes = Encoding.UTF8.GetBytes("first-sync remote payload");
        remote.PutBytes("download.txt", downloadBytes);
        var lockPath = Path.Combine(root, "state", "instance.lock");
        using var instance = PilotInstanceLock.Acquire(lockPath);
        var authority = instance.StateAuthority;
        var connections = new DropboxConnectionStore(authority);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:first-sync", "fs-root", "fs-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var http = new HttpClient(new NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(), database,
            (requestedPair, _) => pairRemotes.TryGetValue(requestedPair.Id, out var selectedRemote) ? selectedRemote : remote);
        await host.LoadPairsAsync();
        // Fixture-only seeding with the EXACT production-derived configuration that
        // RegisterPairAuthorityAsync registers, so the later preparation path matches this row and creates the
        // root bindings itself. This is a dispatcher/operations/planner/executor proof, not a BindPairAsync
        // admission proof: the admission path verifies root identity over the real provider and cannot be
        // driven by the remoteFactory seam.
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/FirstSync", "id:first-sync"),
            "First sync pair", binding.RootNamespaceId) with
        {
            ConnectionId = binding.ConnectionId,
            InitialReviewApproved = false,
            // Real Parent has selective-sync rules. Even a path-only exclusion must
            // not turn a reviewed apply into a silent, unrestricted recursive scan.
            SelectionRules = [new SyncSelectionRule("never-selected", false, null)]
        };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1, "manual_preserve_both",
            "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
            RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FixtureOperations(host, authority);
        var checks = 0;
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("First-sync effect check failed: " + label);
            checks++;
            Console.WriteLine("PASS " + label.Split('\n')[0]);
        }
        Action<string>? cliProgressBoundary = null;
        async Task<(int exit, JsonDocument? json, string stderr)> Cli(params string[] args)
        {
            var stdout = new StringWriter();
            var stderr = new HandoffWriter(line => cliProgressBoundary?.Invoke(line));
            var exit = await SyncCli.ExecuteAsync(args, stdout, stderr, () => operations);
            var text = stdout.ToString().Trim();
            return (exit, text.Length == 0 ? null : JsonDocument.Parse(text), stderr.ToString());
        }

        // R697 item 2: measure the real authority database and its sidecars at each phase.
        var dbGrowthBeforePrepare = DbSidecars(database.DatabasePath);
        var scan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json");
        Require(scan.exit == 0, "the CLI scan command completed through the real inventory coordinator (exit " +
            scan.exit + "; stderr: " + scan.stderr.Trim() + ")");
        // The legacy remote fixture returns one inventory atomically, without per-entry callbacks.
        // Its real wait names the remote folder; the local scanner supplies its actual current file.
        Require(scan.stderr.Contains("inventory database") && scan.stderr.Contains("upload.txt") &&
            scan.stderr.Contains("Waiting for the selected endpoint") && scan.stderr.Contains("/FirstSync"),
            "CLI scan reports concrete database work, current local file and exact remote inventory wait");
        var plan = await Cli("--cli", "plan", "--pair", pair.Id, "--json");
        Require(plan.exit == 0, "the CLI plan command returned the retained first-sync plan (exit " + plan.exit +
            "; stderr: " + plan.stderr.Trim() + ")");
        var planData = plan.json!.RootElement.GetProperty("data");
        var items = planData.GetProperty("items").EnumerateArray().ToArray();
        Require(items.Any(item => item.GetProperty("relativePath").GetString() == "upload.txt" &&
                                  item.GetProperty("group").GetString() == "Local only"),
            "the retained plan contains the seeded local-only file");
        Require(items.Any(item => item.GetProperty("relativePath").GetString() == "download.txt" &&
                                  item.GetProperty("group").GetString() == "Dropbox only"),
            "the retained plan contains the seeded remote-only file");
        var dbGrowthAfterPrepare = DbSidecars(database.DatabasePath);
        // The reviewed plan identity comes from the scan result that produced it, which is exactly what the
        // apply command compares against the retained inventory generation.
        var localScanGeneration = scan.json!.RootElement.GetProperty("data")
            .GetProperty("localScanGenerationId").GetString();

        var unreviewedRun = await host.RunPairsAsync([pair], "Admission: unreviewed pair");
        Require(unreviewedRun.Items.Single().Result is { Blocked: > 0 } &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "the real host refuses ordinary new effects while first-sync review is pending");
        var preferences = new UiPreferencesStore(authority);
        var originalPreferences = preferences.Load();
        foreach (var pause in new[] { "global", "account", "pair" })
        {
            try
            {
                if (pause == "pair")
                {
                    var beforeDisable = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                    var disabledEdit = await Cli("--cli", "pairs", "edit", "--pair", pair.Id, "--disable", "--yes", "--json");
                    var afterDisable = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                    Require(disabledEdit.exit == 0 && !afterDisable.Enabled &&
                            afterDisable.PolicyRevision == beforeDisable.PolicyRevision + 1 &&
                            afterDisable.InitialReviewApproved == beforeDisable.InitialReviewApproved,
                        "CLI pairs edit disable uses shared policy revision and preserves initial review state");
                }
                else await preferences.SaveAsync(pause == "global"
                    ? originalPreferences with { PauseUntilResumed = true }
                    : originalPreferences with { AccountPauses = [new(binding.ConnectionId, true, null)] });
                var pausedRun = await host.RunPairsAsync([pair], "Admission: " + pause + " pause");
                Require(pausedRun.Items.Single().Result is { Blocked: > 0 } &&
                        pausedRun.Items.Single().Result!.Decisions.Any(item => item.Reason.Contains("paused")),
                    "the real host reports the " + pause + " pause instead of silently admitting or dropping the pair");
                var pausedHealthChecks = 0;
                var priorPausedHealthObserver = SyncAuthorityDatabase.HealthCheckCompletedForTest;
                SyncAuthorityDatabase.HealthCheckCompletedForTest = _ => pausedHealthChecks++;
                try
                {
                    var pausedApply = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
                        "--yes", "--json", "--choices", "{\"__plan\":\"" + localScanGeneration +
                        "\",\"upload.txt\":\"upload\",\"download.txt\":\"download\"}");
                    Require(pausedApply.exit != 0 && remote.UploadCount == 0 && remote.DownloadCount == 0,
                        "first-sync apply cannot bypass the saved " + pause + " pause or issue a transfer");
                    Require(pausedApply.stderr.Contains("paused", StringComparison.OrdinalIgnoreCase) &&
                            !pausedApply.stderr.Contains("reading the complete plan", StringComparison.Ordinal) &&
                            pausedHealthChecks == 0,
                        "known " + pause + " pause is explained before CLI review materialization and effect health scan");
                    var directRefused = false;
                    try
                    {
                        await host.RunBootstrapActionPlanAsync(new SyncBootstrapActionPlan(pair.Id,
                            localScanGeneration!, scan.json!.RootElement.GetProperty("data")
                                .GetProperty("dropboxScanGenerationId").GetString()!, []));
                    }
                    catch (InvalidOperationException error) when (error.Message.Contains("paused",
                               StringComparison.OrdinalIgnoreCase)) { directRefused = true; }
                    Require(directRefused && pausedHealthChecks == 0 && remote.UploadCount == 0 && remote.DownloadCount == 0,
                        "direct host rejects known " + pause + " pause before effect health or provider work");
                }
                finally { SyncAuthorityDatabase.HealthCheckCompletedForTest = priorPausedHealthObserver; }
                var recovered = await host.RecoverPairsAsync([pair]);
                Require(recovered.Items.Count == 1 && recovered.Items[0].FailureType is null &&
                        remote.UploadCount == 0 && remote.DownloadCount == 0,
                    "startup recovery still inspects the " + pause + " paused pair without starting new work");
            }
            finally
            {
                if (pause == "pair")
                {
                    var beforeEnable = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                    var enabledEdit = await Cli("--cli", "pairs", "edit", "--pair", pair.Id, "--enable", "--yes", "--json");
                    var afterEnable = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                    Require(enabledEdit.exit == 0 && afterEnable.Enabled &&
                            afterEnable.PolicyRevision == beforeEnable.PolicyRevision + 1 &&
                            afterEnable.InitialReviewApproved == beforeEnable.InitialReviewApproved,
                        "CLI pairs edit enable uses shared policy revision and preserves initial review state");
                }
                await preferences.SaveAsync(originalPreferences);
            }
        }

        var listingsBeforeFirstTransfer = 0;
        var exactLookupsBeforeApply = remote.ExactPathLookupCount;
        remote.BeforeListEntriesForTest = _ =>
        {
            if (remote.UploadCount == 0 && remote.DownloadCount == 0) listingsBeforeFirstTransfer++;
            return Task.CompletedTask;
        };
        var keyMismatch = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
            "--yes", "--json", "--allow-recommended", "--key", "not-the-reviewed-scan");
        Require(keyMismatch.exit == 5 && remote.UploadCount == 0 && remote.DownloadCount == 0,
            "a CLI --key mismatch is a specific retained-review refusal before any transfer");
        var missingSelectionPath = await Cli("--cli", "folders", "exclude", "--pair", pair.Id, "--yes", "--json");
        Require(missingSelectionPath.exit == 2 && (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id)
                .SelectionDefaultIncluded,
            "a missing folder --path is usage failure and does not clear the pair-root selection");
        // R697 item 2 (isolated): snapshot immediately around the apply so later fixture phases cannot
        // masquerade as apply growth.
        var dbGrowthBeforeApply = DbSidecars(database.DatabasePath);
        var reviewedPolicy = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        var apply = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json", "--choices",
            "{\"__plan\":\"" + localScanGeneration + "\",\"upload.txt\":\"upload\",\"download.txt\":\"download\"}");
        remote.BeforeListEntriesForTest = null;
        Require(listingsBeforeFirstTransfer == 0 && remote.ExactPathLookupCount > exactLookupsBeforeApply,
            "first sync plans against its exact approved inventory without a whole-provider listing before the first transfer");
        Require(apply.exit == 0, "the explicit first-sync decisions were applied (exit " + apply.exit + ")");
        var appliedPolicy = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        Require(appliedPolicy.SelectionRules is { } appliedRules &&
                reviewedPolicy.SelectionRules is { } reviewedRules && appliedRules.SequenceEqual(reviewedRules) &&
                appliedPolicy.PolicyRevision == reviewedPolicy.PolicyRevision + 1,
            "reviewed selection rules remain unchanged; only completed first-sync approval advances policy");
        var dbGrowthAfterApplyOpen = DbSidecars(database.DatabasePath);
        Console.WriteLine("{\"db_growth_apply\":" + JsonSerializer.Serialize(new
        {
            database = database.DatabasePath,
            missing_sidecar_encoding = "-1 means the file is genuinely absent",
            before_apply = DbSnapshot(dbGrowthBeforeApply),
            after_apply_open = DbSnapshot(dbGrowthAfterApplyOpen),
            delta_apply = DbDelta(dbGrowthBeforeApply, dbGrowthAfterApplyOpen)
        }) + "}");
        // Applying an already-reviewed snapshot must not perform or pretend to
        // perform a fresh whole-folder scan/persistence pass. Assert its actual
        // snapshot validation, action persistence and exact per-file effects.
        var detailedApplyProgress = apply.stderr.Contains("Validating every retained choice") &&
            apply.stderr.Contains("Loading current complete saved inventories") &&
            apply.stderr.Contains("Saving the proposed actions and conflict decisions") &&
            apply.stderr.Contains("Completed Upload upload.txt") &&
            apply.stderr.Contains("Completed Download download.txt");
        if (!detailedApplyProgress)
            Console.Error.WriteLine("FIRST_SYNC_FAILED_PROGRESS=" + apply.stderr);
        Require(detailedApplyProgress,
            "first-sync CLI describes actual reviewed-snapshot validation, action persistence and both exact completed file operations");
        Require(apply.stderr.Contains("Execution runtime: first-sync-effect-fixture; authority process ") &&
                apply.stderr.Contains("Reading retained members and allocations for ") &&
                apply.stderr.Contains("Reading retained operation ") &&
                apply.stderr.Contains("status, members, dependencies, allocations and attempts") &&
                apply.stderr.Contains("Building the ordered transfer batch"),
            "real CLI identifies its actual runtime and each file's post-planning preparation, not a stale Saving phase");
        Require(apply.stderr.Contains("active") && apply.stderr.Contains("queued") && apply.stderr.Contains("files/s") &&
            apply.stderr.Contains("payload processed"), "real first-sync CLI streams measured per-pair batch totals alongside each file");
        Require(remote.UploadCount == 1, "exactly one intended upload reached the remote");
        Require(remote.Exists("upload.txt") &&
                remote.ReadText("upload.txt") == Encoding.UTF8.GetString(uploadBytes),
            "the uploaded remote bytes match the local source byte for byte");
        Require(File.Exists(Path.Combine(localRoot, "download.txt")) &&
                (await File.ReadAllBytesAsync(Path.Combine(localRoot, "download.txt"))).SequenceEqual(downloadBytes),
            "the downloaded local bytes match the remote source byte for byte");
        var uploadsAfterApply = remote.UploadCount;
        var downloadsAfterApply = remote.DownloadCount;
        var planAgain = await Cli("--cli", "plan", "--pair", pair.Id, "--json");
        Require(planAgain.exit == 0 && remote.UploadCount == uploadsAfterApply,
            "re-reading the retained plan caused no duplicate write");
        var unchangedRun = await Cli("--cli", "run", "--pair", pair.Id, "--yes", "--json");
        Require(unchangedRun.exit == 0 && remote.UploadCount == uploadsAfterApply &&
                remote.DownloadCount == downloadsAfterApply,
            "an unchanged ordinary sync succeeded with no further transfers (exit " + unchangedRun.exit +
            "; stderr: " + unchangedRun.stderr.Trim() + ")");
        Require(unchangedRun.stderr.Contains("inventory database") && unchangedRun.stderr.Contains("upload.txt"),
            "ordinary CLI sync describes database work and the file being checked");
        var reapply = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json", "--choices",
            "{\"__plan\":\"" + localScanGeneration + "\",\"upload.txt\":\"upload\"}");
        Require(reapply.exit != 0 && remote.UploadCount == uploadsAfterApply &&
                remote.DownloadCount == downloadsAfterApply,
            "re-applying an already completed plan was refused before any effect");
        // R697 item 1: a GENUINE two-sided conflict (different bytes locally and remotely at 'clash.txt').
        // Seed it AFTER the earlier no-effect assertions so the ordinary-sync proof above is unchanged, then
        // let a real scan retain the conflict before resolving it through the real CLI.
        var localClashBytes = Encoding.UTF8.GetBytes("first-sync local clash payload");
        var remoteClashBytes = Encoding.UTF8.GetBytes("first-sync remote clash payload");
        await File.WriteAllBytesAsync(Path.Combine(localRoot, "clash.txt"), localClashBytes);
        remote.PutBytes("clash.txt", remoteClashBytes);
        var conflictScan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json");
        Require(conflictScan.exit == 0, "the real scan inventoried the seeded two-sided conflict (exit " +
            conflictScan.exit + "; stderr: " + conflictScan.stderr.Trim() + ")");
        // The PLANNER is what records an open conflict. Drive the ordinary run so the real reconciliation planner
        // retains it; a genuine two-sided conflict is a needs-attention run (exit 5), never a silent overwrite.
        var conflictRun = await Cli("--cli", "run", "--pair", pair.Id, "--yes", "--json");
        Require(conflictRun.exit == 5 && remote.UploadCount == uploadsAfterApply,
            "the real planner retained the two-sided conflict as a needs-attention run with no transfer (exit " +
            conflictRun.exit + "; stderr: " + conflictRun.stderr.Trim() + ")");
        // The only surface that may consume an exact conflict identity and generation is 'conflicts resolve';
        // first-sync apply deliberately refuses to overwrite it. Resolve through the REAL CLI and prove the
        // retained decision, the stale-generation refusal, and the resulting exact bytes on both sides.
        var conflictShow = await Cli("--cli", "conflicts", "show", "--pair", pair.Id, "--path", "clash.txt",
            "--json");
        Require(conflictShow.exit == 0, "the real CLI surfaces the retained two-sided conflict (exit " +
            conflictShow.exit + "; stderr: " + conflictShow.stderr.Trim() + ")");
        var conflictDetail = conflictShow.json!.RootElement.GetProperty("data");
        var conflictNode = conflictDetail.GetProperty("conflict");
        Console.Error.WriteLine("CONFLICT_DETAIL=" + conflictShow.json!.RootElement.GetRawText());
        Require(conflictNode.GetProperty("relativePath").GetString() == "clash.txt" &&
                conflictNode.GetProperty("status").GetString() == "open" &&
                conflictNode.GetProperty("localSize").GetInt64() == localClashBytes.Length &&
                conflictNode.GetProperty("dropboxSize").GetInt64() == remoteClashBytes.Length &&
                conflictNode.GetProperty("localHash").GetString() != conflictNode.GetProperty("dropboxHash").GetString(),
            "the retained conflict records both exact differing sides (" + localClashBytes.Length + " vs " +
            remoteClashBytes.Length + " bytes, distinct hashes) as open");
        var conflictId = conflictNode.GetProperty("conflictId").GetString()!;
        var conflictGeneration = conflictNode.GetProperty("generation").GetInt64();
        var staleResolve = await Cli("--cli", "conflicts", "resolve", "--pair", pair.Id, "--path", "clash.txt",
            "--choice", "KeepDropbox", "--key", conflictId, "--generation",
            (conflictGeneration + 7).ToString(), "--yes", "--json");
        Require(staleResolve.exit != 0 &&
                (await File.ReadAllTextAsync(Path.Combine(localRoot, "clash.txt"))) == "first-sync local clash payload" &&
                remote.ReadText("clash.txt") == "first-sync remote clash payload",
            "a stale conflict generation is refused with both sides untouched");
        var resolve = await Cli("--cli", "conflicts", "resolve", "--pair", pair.Id, "--path", "clash.txt",
            "--choice", "KeepDropbox", "--key", conflictId, "--generation", conflictGeneration.ToString(),
            "--yes", "--json");
        Require(resolve.exit == 0, "the exact KeepDropbox conflict decision applied (exit " + resolve.exit +
            "; stderr: " + resolve.stderr.Trim() + ")");
        Require((await File.ReadAllTextAsync(Path.Combine(localRoot, "clash.txt"))) == "first-sync remote clash payload",
            "the cloud-wins resolution replaced the local file with the exact retained Dropbox bytes");
        Require(remote.ReadText("clash.txt") == "first-sync remote clash payload" &&
                remote.UploadCount == uploadsAfterApply,
            "the resolved remote side and the total upload count are unchanged by conflict resolution");
        var openAfterResolve = await Cli("--cli", "conflicts", "list", "--pair", pair.Id, "--json");
        Require(openAfterResolve.exit == 0 && openAfterResolve.json!.RootElement.GetProperty("data")
                .GetProperty("totalOpen").GetInt32() == 0,
            "the resolved conflict is no longer open in the real retained conflict catalog");
        // The KeepDropbox resolution deliberately performs exactly one download (the retained Dropbox version is
        // written to the local path). Re-baseline the no-further-effect counters so every later assertion still
        // proves that nothing after this point transfers again.
        uploadsAfterApply = remote.UploadCount;
        downloadsAfterApply = remote.DownloadCount;

        // This is a genuinely cold CLI host/database, not the already initialized
        // fixture above. Recovery used to read its origin through a silent ordinary
        // connection before reaching the instrumented startup/admission path.
        var completedRunId = apply.json!.RootElement.GetProperty("data").GetProperty("runId").GetString()!;
        var completedRunBeforeVerification = await database.LoadRunByIdAsync(completedRunId);
        var completedOperationIds = (await database.LoadRunOperationIdsAsync(completedRunId, pair.Id))
            .Reverse().ToArray();
        Require(completedOperationIds.Length >= 2, "real upload and download retain separate operation identities");
        var individualSnapshots = new List<SyncAuthorityOperationSnapshot>();
        foreach (var operationId in completedOperationIds)
            individualSnapshots.Add(await database.LoadOperationAsync(operationId));
        var batchConnections = 0;
        var reportedReads = new List<(int Index, string Id)>();
        var previousTiming = SyncAuthorityDatabase.TimingForTest;
        IReadOnlyList<SyncAuthorityOperationSnapshot> batchSnapshots;
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase == "connection-open") batchConnections++;
        };
        try
        {
            batchSnapshots = await database.LoadOperationsAsync(completedOperationIds, onRead: (index, id) =>
            {
                reportedReads.Add((index, id));
                return Task.CompletedTask;
            });
            Require(batchConnections == 1 && reportedReads.Select(item => item.Id).SequenceEqual(completedOperationIds) &&
                    reportedReads.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, completedOperationIds.Length)),
                "actual retained operations reuse one checked connection and report every input in order");
            batchConnections = 0;
            Require((await database.LoadOperationsAsync([])).Count == 0 && batchConnections == 0,
                "an empty operation batch does not open retained state");
            using var canceledRead = new CancellationTokenSource();
            canceledRead.Cancel();
            var readCanceled = false;
            try { await database.LoadOperationsAsync(completedOperationIds, canceledRead.Token); }
            catch (OperationCanceledException) { readCanceled = true; }
            Require(readCanceled && batchConnections == 0,
                "a pre-cancelled operation batch opens no database and returns no partial success");
        }
        finally { SyncAuthorityDatabase.TimingForTest = previousTiming; }
        Require(JsonSerializer.Serialize(batchSnapshots) == JsonSerializer.Serialize(individualSnapshots) &&
                batchSnapshots.All(item => item.Status == SyncAuthorityOperationStatus.Completed &&
                                           item.Members.Count > 0 && item.Attempts.Count > 0),
            "batch reads preserve exact completed status, members, dependencies, allocations, attempts and authority fields");
        var missingOperationRefused = false;
        try { await database.LoadOperationsAsync([completedOperationIds[0], "not-a-retained-operation"]); }
        catch (InvalidDataException) { missingOperationRefused = true; }
        Require(missingOperationRefused && remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply,
            "a missing batch operation is refused, without partial success or any replayed file effect");
        async Task<(int Exit, string Output, string Progress)> ColdVerify(string runId,
            CancellationToken token = default)
        {
            var coldDatabase = new SyncAuthorityDatabase(database.DatabasePath);
            using var coldHost = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(), coldDatabase,
                (requestedPair, _) => pairRemotes.TryGetValue(requestedPair.Id, out var selectedRemote)
                    ? selectedRemote : remote);
            return await CliChecks.CheckRetainedRunVerificationDispatchAsync(coldHost, authority, runId, token);
        }
        var readOnlyColdChecks = 0;
        var completedColdChecks = 0;
        var priorHealthConnectionObserver = SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest;
        var priorHealthCompletionObserver = SyncAuthorityDatabase.HealthCheckCompletedForTest;
        SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest = connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only;";
            Require(Convert.ToInt64(command.ExecuteScalar()) == 1 &&
                    SQLitePCL.raw.sqlite3_db_readonly(connection.Handle!, "main") == 1,
                "cold run verification performs full health on the actual native read-only, query-only connection");
            command.CommandText = "PRAGMA cache_size;";
            Require(Convert.ToInt64(command.ExecuteScalar()) == -65536,
                "cold run verification reuses the existing bounded health cache, not ordinary connection defaults");
            readOnlyColdChecks++;
        };
        SyncAuthorityDatabase.HealthCheckCompletedForTest = _ => completedColdChecks++;
        try
        {
            var verified = await ColdVerify(completedRunId);
            Require(verified.Exit == 0 && readOnlyColdChecks == 0 && completedColdChecks == 0,
                "the real cold recovery CLI retains origin and admission guards without full database scans");
            var structure = verified.Progress.IndexOf("running SQLite PRAGMA quick_check", StringComparison.Ordinal);
            var relationships = verified.Progress.IndexOf("SQLite foreign_key_check completed in", StringComparison.Ordinal);
            var originRead = verified.Progress.IndexOf("reading the exact retained command_request row", StringComparison.Ordinal);
            var admitted = verified.Progress.IndexOf("accepted; acquiring its run ownership", StringComparison.Ordinal);
            Require(structure == -1 && relationships == -1 && originRead >= 0 && admitted > originRead &&
                    verified.Progress.Contains("without changing Explorer storage choices", StringComparison.Ordinal) &&
                    verified.Progress.Contains("reading retained Rewind ownership", StringComparison.Ordinal),
                "real cold verification names database/policy/admission phases and explicitly avoids mutating Explorer storage choices");
            Require(remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply &&
                    (await database.LoadRunByIdAsync(completedRunId)) == completedRunBeforeVerification,
                "verification of a completed run preserves its original receipt and never replays its file effects");

            var checksBeforeMissing = completedColdChecks;
            var absent = await ColdVerify("not-retained-run");
            Require(absent.Exit != 0 && completedColdChecks == checksBeforeMissing &&
                    absent.Progress.Contains("reading the exact retained command_request row", StringComparison.Ordinal) &&
                    !absent.Progress.Contains("Admission: command ", StringComparison.Ordinal) &&
                    remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply,
                "cold recovery rejects a missing origin after real health without admitting a recovery command or provider effect");

            using var coldCancellation = new CancellationTokenSource();
            var checksBeforeCancellation = completedColdChecks;
            coldCancellation.Cancel();
            var cancelled = await ColdVerify(completedRunId, coldCancellation.Token);
            Require(cancelled.Exit == 130 && completedColdChecks == checksBeforeCancellation &&
                    !cancelled.Progress.Contains("reading the exact retained command_request row", StringComparison.Ordinal) &&
                    !cancelled.Progress.Contains("Admission: command ", StringComparison.Ordinal) &&
                    remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply,
                "cancelling actual cold health produces no healthy verdict, origin query, command admission or duplicate effect");
        }
        finally
        {
            SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest = priorHealthConnectionObserver;
            SyncAuthorityDatabase.HealthCheckCompletedForTest = priorHealthCompletionObserver;
        }

        // This independent unapproved pair distinguishes stale inventory rejection from the already-reviewed guard.
        var staleRoot = Path.Combine(root, "stale-local");
        Directory.CreateDirectory(staleRoot);
        var staleFile = Path.Combine(staleRoot, "stale.txt");
        await File.WriteAllTextAsync(staleFile, "before review");
        using var staleLease = PilotDirectoryLease.Open(staleRoot);
        var stalePair = SyncPair.FromPilot(new PilotSettings(staleRoot,
            staleLease.Identity.VolumeSerialNumber, staleLease.Identity.FileId, "/Stale", "id:stale"),
            "Stale review pair", binding.RootNamespaceId) with
        {
            ConnectionId = binding.ConnectionId,
            InitialReviewApproved = false
        };
        await database.RegisterPairAsync(stalePair.Id, stalePair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: stalePair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(stalePair, select: true);
        var firstStaleScan = await Cli("--cli", "scan", "--pair", stalePair.Id, "--yes", "--json");
        Require(firstStaleScan.exit == 0, "the independent unapproved pair produced its first inventory");
        var oldGeneration = firstStaleScan.json!.RootElement.GetProperty("data")
            .GetProperty("localScanGenerationId").GetString();
        await File.WriteAllTextAsync(staleFile, "changed after review");
        var refreshedStaleScan = await Cli("--cli", "scan", "--pair", stalePair.Id, "--yes", "--json");
        Require(refreshedStaleScan.exit == 0 && oldGeneration != refreshedStaleScan.json!.RootElement
                .GetProperty("data").GetProperty("localScanGenerationId").GetString(),
            "refreshing the changed unapproved pair replaced the reviewed inventory generation");
        var staleApply = await Cli("--cli", "first-sync", "apply", "--pair", stalePair.Id, "--yes", "--json",
            "--choices", JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = oldGeneration,
                ["stale.txt"] = "upload"
            }));
        Require(staleApply.exit == 5 && staleApply.stderr.Contains(
                "inventory changed since the plan you reviewed", StringComparison.Ordinal) &&
                remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply &&
                !remote.Exists("stale.txt") && await File.ReadAllTextAsync(staleFile) == "changed after review",
            "an old generation on an unapproved pair was specifically rejected with no transfer or content change");
        var batchUnreviewed = await Cli("--cli", "run", "batch", "--selection",
            JsonSerializer.Serialize(new[] { pair.Id, stalePair.Id }), "--yes", "--json");
        Require(batchUnreviewed.exit == 5 && remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply,
            "batch preflight rejects an unreviewed second target before any selected transfer");
        var batch = await Cli("--cli", "run", "batch", "--selection", JsonSerializer.Serialize(new[] { pair.Id }), "--yes", "--json");
        Require(batch.exit == 0 && batch.json!.RootElement.GetProperty("data").GetProperty("count").GetInt32() == 1 &&
            remote.UploadCount == uploadsAfterApply && remote.DownloadCount == downloadsAfterApply,
            "explicit batch reaches the real scheduler and returns its unchanged retained pair result");
        var secondRoot = Path.Combine(root, "batch-second");
        Directory.CreateDirectory(secondRoot);
        using var secondLease = PilotDirectoryLease.Open(secondRoot);
        var secondPair = SyncPair.FromPilot(new PilotSettings(secondRoot, secondLease.Identity.VolumeSerialNumber,
            secondLease.Identity.FileId, "/BatchSecond", "id:batch-second"), "Second batch pair", binding.RootNamespaceId)
            with { ConnectionId = binding.ConnectionId, InitialReviewApproved = true };
        var secondRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        pairRemotes.Add(secondPair.Id, secondRemote);
        await database.RegisterPairAsync(secondPair.Id, secondPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: secondPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(secondPair, select: false);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "batch-first.txt"), "first destination payload");
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "batch-second.txt"), "second destination payload");
        var twoPairBatch = await Cli("--cli", "run", "batch", "--selection",
            JsonSerializer.Serialize(new[] { pair.Id, secondPair.Id }), "--yes", "--json");
        Require(twoPairBatch.exit == 0 && twoPairBatch.json!.RootElement.GetProperty("data").GetProperty("count").GetInt32() == 2,
            "two exact independent targets complete through one real scheduler batch: " + twoPairBatch.stderr.Trim());
        Require(twoPairBatch.stderr.Contains("inventory database") && twoPairBatch.stderr.Contains("batch-first.txt") &&
            twoPairBatch.stderr.Contains("batch-second.txt") && twoPairBatch.stderr.Contains("Upload"),
            "batch CLI sync retains concrete process, file and transfer operation output for each target");
        Require(remote.ReadText("batch-first.txt") == "first destination payload" &&
            secondRemote.ReadText("batch-second.txt") == "second destination payload" &&
            !remote.Exists("batch-second.txt") && !secondRemote.Exists("batch-first.txt"),
            "batch payloads reach only their own bound destination stores with exact bytes");

        // (1) R473-5: more than 500 reviewed rows where only the final page's row has an effect. The adapter must
        // read the complete retained review, so the 520th row is included while every other row is a real skip.
        var bigRoot = Path.Combine(root, "big-local");
        Directory.CreateDirectory(bigRoot);
        for (var index = 0; index < 520; index++)
            await File.WriteAllTextAsync(Path.Combine(bigRoot, "row-" + index.ToString("0000") + ".txt"), "row " + index);
        using var bigLease = PilotDirectoryLease.Open(bigRoot);
        var bigPair = SyncPair.FromPilot(new PilotSettings(bigRoot, bigLease.Identity.VolumeSerialNumber,
            bigLease.Identity.FileId, "/Big", "id:big"), "Large reviewed pair", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var bigRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        pairRemotes.Add(bigPair.Id, bigRemote);
        await database.RegisterPairAsync(bigPair.Id, bigPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: bigPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(bigPair, select: false);
        var bigScan = await Cli("--cli", "scan", "--pair", bigPair.Id, "--yes", "--json");
        Require(bigScan.exit == 0, "the large reviewed pair produced its inventory (exit " + bigScan.exit + ")");
        var bigGeneration = bigScan.json!.RootElement.GetProperty("data")
            .GetProperty("localScanGenerationId").GetString();
        var fullReviewQueries = 0;
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase == "bootstrap-evidence-query") fullReviewQueries++;
        };
        try
        {
            var allRows = await host.LoadBootstrapReviewAsync(bigPair.Id);
            Require(allRows.Count == 520 && allRows[^1].RelativePath == "row-0519.txt" &&
                fullReviewQueries == 1,
                "the real host loads every reviewed row with one classification query across the presentation-page boundary");
        }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        var bigChoices = new Dictionary<string, string?>
        {
            ["__plan"] = bigGeneration,
            ["__remote_plan"] = bigScan.json.RootElement.GetProperty("data")
                .GetProperty("dropboxScanGenerationId").GetString()
        };
        var orphanBinding = await Cli("--cli", "first-sync", "apply", "--pair", bigPair.Id,
            "--yes", "--json", "--allow-recommended", "--choices",
            JsonSerializer.Serialize(new Dictionary<string, string?>
            { ["__remote_plan"] = bigChoices["__remote_plan"] }));
        Require(orphanBinding.exit != 0 && orphanBinding.stderr.Contains(
                "__remote_plan requires the matching __plan", StringComparison.Ordinal) &&
                bigRemote.UploadCount == 0 && bigRemote.DownloadCount == 0,
            "a right-only reviewed binding is rejected rather than silently approving current recommendations");
        for (var index = 0; index < 519; index++)
            bigChoices["row-" + index.ToString("0000") + ".txt"] = "no-change";
        bigChoices["row-0519.txt"] = "upload";
        var bigAdmission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", bigPair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await database.TransitionCommandRequestAsync(bigAdmission.CommandRequestId, "executing");
        await CancelSkippedPlanningIteration(baselineLoop: false);
        fullReviewQueries = 0;
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase == "bootstrap-evidence-query") fullReviewQueries++;
        };
        (int exit, JsonDocument? json, string stderr) bigApply;
        try
        {
            bigApply = await Cli("--cli", "first-sync", "apply", "--pair", bigPair.Id, "--yes", "--json",
                "--choices", JsonSerializer.Serialize(bigChoices));
        }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        // One bound complete CLI projection plus independent host validation.
        // Finalization uses those exact approved rows and verified per-file
        // outcomes, not a third rescan/classification of future folder changes.
        var validationIndex = bigApply.stderr.IndexOf("Loaded 520 reviewed inventory rows", StringComparison.Ordinal);
        var effectIndex = bigApply.stderr.IndexOf("Starting Upload row-0519.txt", StringComparison.Ordinal);
        Require(fullReviewQueries == 2 && validationIndex >= 0 && effectIndex > validationIndex,
            "the real CLI uses one complete bound review and retains independent execution validation " +
            "(classification queries " + fullReviewQueries + ")");
        Require(bigApply.stderr.Contains("including saved inventory identities") &&
            bigApply.stderr.Contains("reading the complete plan in one database query") &&
            bigApply.stderr.Contains("checking the requested choices") &&
            bigApply.stderr.Contains("rechecking pair policy") &&
            bigApply.stderr.Contains("independently validate every choice"),
            "the real CLI explains each preparation operation before admitting file effects");
        Require(bigApply.exit == 5 && bigApply.json!.RootElement.GetProperty("data").GetProperty("skippedItems").GetInt32() == 519 &&
                !bigApply.json.RootElement.GetProperty("data").GetProperty("firstSyncComplete").GetBoolean() && bigRemote.UploadCount == 1 &&
                bigRemote.Exists("row-0519.txt") && bigRemote.ReadText("row-0519.txt") == "row 519",
            "the final page of a 520-row reviewed plan was included and was the only effect through the real CLI " +
            "(exit " + bigApply.exit + "; uploads " + bigRemote.UploadCount + "; stderr: " + bigApply.stderr.Trim() + ")");
        var approvedComparisons = bigApply.stderr.Split('\n').Where(line => line.Contains(
            "Comparing this item's local and remote versions", StringComparison.Ordinal)).ToArray();
        Require(approvedComparisons.Length == 1 && approvedComparisons[0].Contains(
                "row-0519.txt", StringComparison.Ordinal),
            "reviewed planning logs the actual approved file comparison, not serialized per-file callbacks for 519 unapproved skips");
        await CancelSkippedPlanningIteration(baselineLoop: true);
        var indexedSaved = await database.LoadPlanningObservationSetAsync(bigPair.Id, bigPair.PolicyRevision,
            await database.LoadActiveRootBindingIdAsync(bigPair.Id, "local"),
            await database.LoadActiveRootBindingIdAsync(bigPair.Id, "dropbox"),
            await database.LoadLatestCompleteScanIdAsync(bigPair.Id, "local"),
            await database.LoadLatestCompleteScanIdAsync(bigPair.Id, "dropbox"));
        var countedExclusions = new CountingPathSet(indexedSaved.ExcludedPaths);
        var indexedProgress = new List<SyncComparisonProgress>();
        var indexedOutcomes = await new SyncAuthorityReconciliationPlanner(database,
            new(bigPair.PolicyRevision, "indexed-planning", "control-local", "control-dropbox"), bigAdmission,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), onComparison: step =>
            {
                indexedProgress.Add(step);
                return Task.CompletedTask;
            }).PlanAsync(indexedSaved with { ExcludedPaths = countedExclusions },
                SyncAuthorityReconciliationPlanner.ObservationSetIdentity(indexedSaved));
        Require(countedExclusions.Enumerations <= 1 && indexedOutcomes.Count == 0 && bigRemote.UploadCount == 1,
            "the real planner indexes the exact retained exclusions once across saved baselines, without admitting unapproved effects");
        Require(indexedProgress.Any(step => step.Phase == "Preparing Comparison" && step.CompletedChecks > 0),
            "the real planner reports actual saved-baseline preparation separately from loading inventories");

        async Task CancelSkippedPlanningIteration(bool baselineLoop)
        {
            var saved = await database.LoadPlanningObservationSetAsync(bigPair.Id, bigPair.PolicyRevision,
                await database.LoadActiveRootBindingIdAsync(bigPair.Id, "local"),
                await database.LoadActiveRootBindingIdAsync(bigPair.Id, "dropbox"),
                await database.LoadLatestCompleteScanIdAsync(bigPair.Id, "local"),
                await database.LoadLatestCompleteScanIdAsync(bigPair.Id, "dropbox"));
            Require(baselineLoop ? saved.SynchronizationPoints.Count > 0 : saved.SynchronizationPoints.Count == 0,
                "skip cancellation fixture retains the real " + (baselineLoop ? "saved-baseline" : "path-only") + " inventory");
            using var cancelled = new CancellationTokenSource();
            // Supply exactly the retained entries. Only cancellation is injected,
            // after current authority validation and before the actual skipped loop.
            // Baselines are enumerated for lookup/uncertainty twice, then sorting.
            var supplied = baselineLoop
                ? saved with { SynchronizationPoints = new CancelOnEnumeration<SyncAuthoritySynchronizationPoint>(
                    saved.SynchronizationPoints, cancelled, 4) }
                : saved with { Local = new CancelOnEnumeration<SyncAuthorityObservedEntry>(saved.Local, cancelled, 1) };
            var reported = new List<SyncComparisonProgress>();
            var planner = new SyncAuthorityReconciliationPlanner(database,
                new SyncAuthorityPlannerOptions(bigPair.PolicyRevision, "cancelled-planning", "control-local", "control-dropbox"), bigAdmission,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), onComparison: step =>
                {
                    reported.Add(step);
                    return Task.CompletedTask;
                });
            var rejected = false;
            var uploads = bigRemote.UploadCount;
            try
            {
                await planner.PlanAsync(supplied, SyncAuthorityReconciliationPlanner.ObservationSetIdentity(supplied),
                    cancellationToken: cancelled.Token);
            }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected && cancelled.IsCancellationRequested &&
                    reported.All(step => step.Phase == "Preparing Comparison") && bigRemote.UploadCount == uploads,
                "cancellation stops the first unselected " + (baselineLoop ? "baseline" : "path") +
                " iteration before a decision, save or effect; preparation feedback remains visible");
        }

        // (2) R473-1: a reviewed source edited after the review is uploaded from its latest same-direction version,
        // while an unrelated path is excluded from captured consent. Finishing that finite consent must not
        // require a globally quiet folder: only a separate ordinary run may process the later arrival.
        var editRoot = Path.Combine(root, "edit-local");
        Directory.CreateDirectory(editRoot);
        await File.WriteAllTextAsync(Path.Combine(editRoot, "edit.txt"), "reviewed version");
        using var editLease = PilotDirectoryLease.Open(editRoot);
        var editPair = SyncPair.FromPilot(new PilotSettings(editRoot, editLease.Identity.VolumeSerialNumber,
            editLease.Identity.FileId, "/Edit", "id:edit"), "Edited reviewed pair", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var editRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        pairRemotes.Add(editPair.Id, editRemote);
        await database.RegisterPairAsync(editPair.Id, editPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: editPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(editPair, select: false);
        var editScan = await Cli("--cli", "scan", "--pair", editPair.Id, "--yes", "--json");
        var editGeneration = editScan.json!.RootElement.GetProperty("data")
            .GetProperty("localScanGenerationId").GetString();
        await File.WriteAllTextAsync(Path.Combine(editRoot, "edit.txt"), "latest source version");
        await File.WriteAllTextAsync(Path.Combine(editRoot, "arrived-after-review.txt"), "unreviewed arrival");
        var editApply = await Cli("--cli", "first-sync", "apply", "--pair", editPair.Id, "--yes", "--json",
            "--choices", JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = editGeneration,
                ["edit.txt"] = "upload"
            }));
        var editComplete = editApply.json!.RootElement.GetProperty("data").GetProperty("firstSyncComplete").GetBoolean();
        Require(editApply.exit == 0 && editRemote.Exists("edit.txt") &&
                editRemote.ReadText("edit.txt") == "latest source version" &&
                !editRemote.Exists("arrived-after-review.txt") && editComplete &&
                (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == editPair.Id) is
                    { InitialReviewApproved: true } approvedEdit &&
                approvedEdit.PolicyRevision == editPair.PolicyRevision + 1,
            "a reviewed source edited after the review was uploaded from its latest version while the unrelated " +
            "arrival stayed outside completed finite consent (exit " + editApply.exit + "; complete " + editComplete +
            "; retained result: " + editApply.json.RootElement.GetProperty("data").GetRawText() +
            "; live output: " + editApply.stderr + ")");
        var editedUploads = editRemote.UploadCount;
        var laterArrivalRun = await Cli("--cli", "run", "--pair", editPair.Id, "--yes", "--json");
        Require(laterArrivalRun.exit == 0 && editRemote.Exists("arrived-after-review.txt") &&
                editRemote.ReadText("arrived-after-review.txt") == "unreviewed arrival" &&
                editRemote.ReadText("edit.txt") == "latest source version" &&
                editRemote.UploadCount == editedUploads + 1,
            "only a separate normal sync transfers the new arrival without replaying the completed reviewed edit");
        var laterArrivalRepeat = await Cli("--cli", "run", "--pair", editPair.Id, "--yes", "--json");
        Require(laterArrivalRepeat.exit == 0 && editRemote.UploadCount == editedUploads + 1,
            "an unchanged repeat performs neither the reviewed edit nor the late arrival twice");

        // (3) R473-1/ENG1: a destination that changed after the review is refused with no unsafe effect, and no
        // newest-timestamp winner is chosen for the two-sided difference.
        var destRoot = Path.Combine(root, "dest-local");
        Directory.CreateDirectory(destRoot);
        await File.WriteAllTextAsync(Path.Combine(destRoot, "dest.txt"), "local version");
        using var destLease = PilotDirectoryLease.Open(destRoot);
        var destPair = SyncPair.FromPilot(new PilotSettings(destRoot, destLease.Identity.VolumeSerialNumber,
            destLease.Identity.FileId, "/Dest", "id:dest"), "Changed destination pair", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var destRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        pairRemotes.Add(destPair.Id, destRemote);
        await database.RegisterPairAsync(destPair.Id, destPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: destPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(destPair, select: false);
        var destScan = await Cli("--cli", "scan", "--pair", destPair.Id, "--yes", "--json");
        var destGeneration = destScan.json!.RootElement.GetProperty("data")
            .GetProperty("localScanGenerationId").GetString();
        destRemote.Put("dest.txt", "changed destination");
        var destApply = await Cli("--cli", "first-sync", "apply", "--pair", destPair.Id, "--yes", "--json",
            "--choices", JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = destGeneration,
                ["dest.txt"] = "upload"
            }));
        var destUploads = destRemote.UploadCount;
        Require(destRemote.ReadText("dest.txt") == "changed destination" &&
                await File.ReadAllTextAsync(Path.Combine(destRoot, "dest.txt")) == "local version" &&
                destUploads == 0,
            "a destination that changed after the review was refused with both sides unchanged and no upload " +
            "(exit " + destApply.exit + "; uploads " + destUploads + ")");

        // (4) R473-3: the real approval/terminal transaction can fail after
        // completed effects. It must roll back approval, retain the exact failed
        // run and completed bytes, and never replay those effects. A removed
        // whole-folder follow-up rescan is not a valid injection boundary.
        var followRoot = Path.Combine(root, "follow-local");
        Directory.CreateDirectory(followRoot);
        var followPaths = new[] { "follow.txt", "follow-second.txt", "follow-third.txt" };
        foreach (var path in followPaths)
            await File.WriteAllTextAsync(Path.Combine(followRoot, path), "follow payload");
        using var followLease = PilotDirectoryLease.Open(followRoot);
        var followPair = SyncPair.FromPilot(new PilotSettings(followRoot, followLease.Identity.VolumeSerialNumber,
            followLease.Identity.FileId, "/Follow", "id:follow"), "Follow-up failure pair", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var followRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        pairRemotes.Add(followPair.Id, followRemote);
        await database.RegisterPairAsync(followPair.Id, followPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: followPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(followPair, select: false);
        var followScan = await Cli("--cli", "scan", "--pair", followPair.Id, "--yes", "--json");
        var followGeneration = followScan.json!.RootElement.GetProperty("data")
            .GetProperty("localScanGenerationId").GetString();
        var followChoices = followPaths.ToDictionary(path => path, _ => (string?)"upload");
        followChoices["__plan"] = followGeneration;
        followChoices["__remote_plan"] = followScan.json.RootElement.GetProperty("data")
            .GetProperty("dropboxScanGenerationId").GetString();
        var uploadsBeforeFollow = followRemote.UploadCount;
        var followEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFollow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousFinalizationBoundary = database.FirstSyncFinalizationBoundaryForTest;
        database.FirstSyncFinalizationBoundaryForTest = phase =>
        {
            if (phase != "approval-written-before-terminal-result") return;
            throw new InvalidOperationException("Synthetic terminal approval write failed after completed effects");
        };
        var boundaryHeld = 0;
        cliProgressBoundary = line =>
        {
            // Hold real CLI completion reporting after every provider file has been written,
            // but outside SQLite's atomic approval transaction. A competing command must be
            // refused by run ownership, not blocked by a test-created database writer lock.
            if (!line.Contains("Completed Upload", StringComparison.Ordinal) ||
                followRemote.UploadCount != uploadsBeforeFollow + followPaths.Length ||
                Interlocked.Exchange(ref boundaryHeld, 1) != 0) return;
            followEntered.TrySetResult();
            releaseFollow.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        };
        try
        {
            // Run the actual CLI off this test continuation so its synchronous
            // progress boundary can be held while competing admission is tested.
            var followTask = Task.Run(() => Cli("--cli", "first-sync", "apply", "--pair", followPair.Id, "--yes", "--json",
                "--choices", JsonSerializer.Serialize(followChoices)));
            await followEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var heldRun = host.ActiveRun;
            Require(heldRun is not null && heldRun.Kind == "run_pair" && heldRun.PairId == followPair.Id &&
                    (await database.LoadRunHistoryAsync(20, 0)).Any(item => item.Id == heldRun.Id && item.Status == "executing"),
                "the CLI reviewed command retains its exact executing run identity during delayed terminal approval");
            var competingRejected = false;
            try { await host.RefreshBootstrapInventoryAsync(followPair.Id); }
            catch (InvalidOperationException) { competingRejected = true; }
            Require(competingRejected && host.ActiveRun?.Id == heldRun!.Id &&
                    host.ActiveRun?.Progress == heldRun!.Progress &&
                    !(await host.LoadRetainedFirstSyncProblemsAsync()).Any(item => item.PairId == followPair.Id),
                "a competing scan is rejected without replacing the reviewed command's lease or progress, or inventing a scan failure");
            releaseFollow.SetResult();
            var followApply = await followTask.WaitAsync(TimeSpan.FromSeconds(30));
            cliProgressBoundary = null;
            database.FirstSyncFinalizationBoundaryForTest = previousFinalizationBoundary;
            var followProblem = (await host.LoadRetainedFirstSyncProblemsAsync())
                .SingleOrDefault(item => item.PairId == followPair.Id);
            var followUploads = followRemote.UploadCount;
            var afterFailedApproval = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == followPair.Id);
            Require(followApply.exit != 0 && followPaths.All(path => followRemote.ReadText(path) == "follow payload") &&
                    followUploads == uploadsBeforeFollow + followPaths.Length && followProblem is not null &&
                    followProblem.Code == "first_sync_partial_failure" && followProblem.Phase == "approval" &&
                    followProblem.CommandId == heldRun!.Id && followProblem.NextAction.Length > 0 && host.ActiveRun is null &&
                    afterFailedApproval.InitialReviewApproved == false && afterFailedApproval.PolicyRevision == followPair.PolicyRevision &&
                    !followProblem.Message.Contains("Nothing was changed", StringComparison.OrdinalIgnoreCase),
                "a failed real terminal approval retains all completed effects, rolls back approval and reports its exact non-success outcome " +
                "(exit " + followApply.exit + "; uploads " + followUploads + "; problem " + followProblem?.Code + ")");
            // A stopped/failed command can lack terminal per-file summaries. Retry must
            // still find EVERY completed operation in its append-only presentation log.
            // history_event.operation_id is NULL for these records; grouping by that
            // column (instead of the JSON operation identity) collapses the whole run.
            var followOperationIds = await database.LoadRunOperationIdsAsync(heldRun!.Id, followPair.Id);
            Require(followOperationIds.Count == followPaths.Length &&
                    followOperationIds.Distinct(StringComparer.Ordinal).Count() == followPaths.Length,
                "failed-run operation membership keeps all distinct JSON operation identities, not the NULL history column");
            var followRetryPlan = await Cli("--cli", "run", "retry-plan", "--run", heldRun.Id, "--json");
            var followRetryItems = followRetryPlan.json!.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
            Require(followRetryPlan.exit == 0 && followRetryItems.Length == followPaths.Length &&
                    followRetryItems.All(item => item.GetProperty("state").GetString() == "Completed" &&
                        !item.GetProperty("canRetry").GetBoolean()) &&
                    followRetryItems.Select(item => item.GetProperty("path").GetString()).ToHashSet().SetEquals(followPaths),
                "real CLI retry reconstructs all completed files after terminal failure without per-file result summaries");
            var followRetry = await Cli("--cli", "first-sync", "apply", "--pair", followPair.Id, "--yes", "--json",
                "--choices", JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["__plan"] = followGeneration,
                    ["follow.txt"] = "upload"
                }));
            Require(followRemote.UploadCount == uploadsBeforeFollow + followPaths.Length && followRetry.exit != 0,
                "the terminal approval failure was not replayed on a retry (exit " + followRetry.exit + ")");
            var failedReceipt = (await database.LoadRunByIdAsync(heldRun.Id))!.ResultJson;
            var missingSource = Path.Combine(followRoot, "follow-second.txt");
            var parkedSource = Path.Combine(root, "parked-reviewed-source.txt");
            File.Move(missingSource, parkedSource);
            try
            {
                var removedSourceApply = await Cli("--cli", "first-sync", "apply", "--pair", followPair.Id,
                    "--yes", "--json", "--choices", JsonSerializer.Serialize(followChoices));
                Require(removedSourceApply.exit == 5 && !removedSourceApply.json!.RootElement.GetProperty("data")
                        .GetProperty("firstSyncComplete").GetBoolean() && !File.Exists(missingSource) &&
                        followRemote.UploadCount == followUploads &&
                        followPaths.All(path => followRemote.ReadText(path) == "follow payload") &&
                        (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == followPair.Id).InitialReviewApproved == false &&
                        (await database.LoadRunByIdAsync(heldRun.Id))!.ResultJson == failedReceipt,
                    "a genuinely removed completed source remains unapproved, is not recreated and never deletes its peer or replays completed effects");
                var removedSourceRetry = await Cli("--cli", "run", "retry", "--run", heldRun.Id,
                    "--choice", "all", "--yes", "--json");
                Require(removedSourceRetry.exit == 5 && !removedSourceRetry.json!.RootElement.GetProperty("data")
                        .GetProperty("firstSyncComplete").GetBoolean() && !File.Exists(missingSource) &&
                        followRemote.UploadCount == followUploads &&
                        (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == followPair.Id).InitialReviewApproved == false &&
                        (await database.LoadRunByIdAsync(heldRun.Id))!.ResultJson == failedReceipt,
                    "all-completed retry still validates the completed source before metadata-only approval and preserves its failed receipt");
            }
            finally { File.Move(parkedSource, missingSource); }
            var finishApproval = await Cli("--cli", "run", "retry", "--run", heldRun.Id,
                "--choice", "all", "--yes", "--json");
            var finallyApproved = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == followPair.Id);
            Require(finishApproval.exit == 0 && finishApproval.json!.RootElement.GetProperty("data")
                    .GetProperty("firstSyncComplete").GetBoolean() && finallyApproved.InitialReviewApproved == true &&
                    finallyApproved.PolicyRevision == followPair.PolicyRevision + 1 &&
                    followRemote.UploadCount == followUploads && followPaths.All(path => followRemote.ReadText(path) == "follow payload") &&
                    (await database.LoadRunByIdAsync(heldRun.Id))!.ResultJson == failedReceipt,
                "clearing a terminal-only fault lets all-completed retry finish the original full reviewed approval without replay or changing the failure receipt " +
                "(exit " + finishApproval.exit + "; result " + finishApproval.json?.RootElement.GetRawText() +
                "; progress " + finishApproval.stderr + ")");
        }
        finally
        {
            releaseFollow.TrySetResult();
            cliProgressBoundary = null;
            database.FirstSyncFinalizationBoundaryForTest = previousFinalizationBoundary;
        }

        // A full old snapshot remains valid consent after an unrelated rescan;
        // one provider-rejected file must not stop independent reviewed files.
        var partialRoot = Path.Combine(root, "partial-local");
        Directory.CreateDirectory(partialRoot);
        foreach (var path in new[] { "a-good.txt", "b-denied.txt", "c-good.txt" })
            await File.WriteAllTextAsync(Path.Combine(partialRoot, path), "reviewed " + path);
        using var partialLease = PilotDirectoryLease.Open(partialRoot);
        var partialPair = SyncPair.FromPilot(new PilotSettings(partialRoot,
            partialLease.Identity.VolumeSerialNumber, partialLease.Identity.FileId,
            "/Partial", "id:partial"), "Partial file failure", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var partialRemote = new FakeSyncRemoteStore
        {
            ExactPathMetadataForTest = true,
            UploadFailureForTest = path => path == "b-denied.txt"
                ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: this file is locked by another editor") : null
        };
        pairRemotes.Add(partialPair.Id, partialRemote);
        await database.RegisterPairAsync(partialPair.Id, partialPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: partialPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(partialPair, select: false);
        var partialScan = await Cli("--cli", "scan", "--pair", partialPair.Id, "--yes", "--json");
        Require(partialScan.exit == 0, "partial-failure fixture has real complete inventories: " + partialScan.stderr);
        var partialData = partialScan.json!.RootElement.GetProperty("data");
        var partialChoices = new Dictionary<string, string?>
        {
            ["__plan"] = partialData.GetProperty("localScanGenerationId").GetString(),
            ["__remote_plan"] = partialData.GetProperty("dropboxScanGenerationId").GetString(),
            ["a-good.txt"] = "upload", ["b-denied.txt"] = "upload", ["c-good.txt"] = "upload"
        };
        await File.WriteAllTextAsync(Path.Combine(partialRoot, "a-good.txt"), "latest legitimate source");
        await File.WriteAllTextAsync(Path.Combine(partialRoot, "new-unreviewed.txt"), "new item");
        Require((await Cli("--cli", "scan", "--pair", partialPair.Id, "--yes", "--json")).exit == 0,
            "a newer complete inventory exists before applying the old two-sided snapshot");
        var partialListingsBeforeFirstTransfer = 0;
        partialRemote.BeforeListEntriesForTest = _ =>
        {
            if (partialRemote.UploadCount == 0) partialListingsBeforeFirstTransfer++;
            return Task.CompletedTask;
        };
        var partialApply = await Cli("--cli", "first-sync", "apply", "--pair", partialPair.Id,
            "--yes", "--json", "--choices", JsonSerializer.Serialize(partialChoices));
        partialRemote.BeforeListEntriesForTest = null;
        Require(partialApply.exit == 5 && partialRemote.UploadCount == 2 &&
            partialRemote.ReadText("a-good.txt") == "latest legitimate source" &&
            partialRemote.Exists("c-good.txt") && !partialRemote.Exists("b-denied.txt") &&
            !partialRemote.Exists("new-unreviewed.txt"),
            "both independent files finish despite one rejected file; refreshed source is used and unreviewed arrival stays pending: " + partialApply.stderr);
        Require(partialListingsBeforeFirstTransfer == 0,
            "a newer saved scan preserves original consent without another whole-provider listing before the first transfer");
        Require(partialApply.stderr.Contains("b-denied.txt") &&
            partialApply.stderr.Contains("this file is locked by another editor"),
            "CLI emits the exact failed file and safe provider cause: " + partialApply.stderr);
        var partialLog = await host.LoadActivityProjectionPageAsync(500, 0, null, "All", null, partialPair.Id, null,
            query: new SyncActivityQuery(DetailedLog: true));
        Require(partialLog.Any(item => item.PairId == partialPair.Id && item.RelativePath == "b-denied.txt" &&
            item.Reason.Contains("this file is locked by another editor")),
            "complete operation log retains the failed file and exact cause after the run");

        var originalRun = (await database.LoadRunHistoryAsync(40)).Single(item =>
            IsOriginalReviewedRun(item, partialPair.Id));
        var goodOperation = partialLog.First(item => item.RelativePath == "c-good.txt" && item.OperationId is not null).OperationId;
        await LegacyHistoryFixture.SeedAsync(database, new SyncActivityEvent("fixture-prerequisite-progress", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Sync], "Information", "File", "Checking prerequisite", "Reading a different file is not a completed effect.",
            PairId: partialPair.Id, RunId: originalRun.Id, OperationId: goodOperation, RelativePath: "b-denied.txt"));
        var retryPlan = await Cli("--cli", "run", "retry-plan", "--run", originalRun.Id, "--json");
        Require(retryPlan.exit == 0 && retryPlan.json!.RootElement.GetProperty("data").GetProperty("items")
            .EnumerateArray().Count(item => item.GetProperty("canRetry").GetBoolean()) == 1,
            "CLI retry preview exposes only the failed original file, not completed or newly arrived files: " +
                retryPlan.stderr + " " + retryPlan.json?.RootElement.GetRawText());
        var retryPage = await Cli("--cli", "run", "retry-plan", "--run", originalRun.Id,
            "--limit", "1", "--offset", "1", "--json");
        var retryPageData = retryPage.json!.RootElement.GetProperty("data");
        Require(retryPage.exit == 0 && retryPageData.GetProperty("returned").GetInt32() == 1 &&
                retryPageData.GetProperty("offset").GetInt32() == 1 && retryPageData.GetProperty("totalItems").GetInt32() == 3 &&
                retryPageData.GetProperty("failed").GetInt32() == 1 && retryPageData.GetProperty("hasMore").GetBoolean(),
            "real CLI retry preview pages without losing whole-run eligibility counts or starting a file effect");
        var replayRefusal = await Cli("--cli", "run", "retry", "--run", originalRun.Id, "--choice", "all",
            "--selection", "[\"a-good.txt\"]", "--yes", "--json");
        Require(replayRefusal.exit != 0 && partialRemote.UploadCount == 2,
            "retry rejects an already-completed selected file without any provider effect");
        partialRemote.UploadFailureForTest = null;
        var retryResult = await Cli("--cli", "run", "retry", "--run", originalRun.Id, "--choice", "failed", "--yes", "--json");
        Require(retryResult.exit == 0 && partialRemote.UploadCount == 3 &&
            partialRemote.Exists("b-denied.txt") && !partialRemote.Exists("new-unreviewed.txt"),
            "retry copies only the fixed failed file and preserves the two completed transfers: " + retryResult.stderr);
        var linkedRetry = (await database.LoadRunHistoryAsync(40)).Single(item =>
            item.RequestJson.Contains("\"retryOfRunId\":\"" + originalRun.Id + "\""));
        Require(linkedRetry.Id != originalRun.Id && (await database.LoadRunByIdAsync(originalRun.Id))!.ResultJson == originalRun.ResultJson,
            "retry has its own linked run while original failure/history remain byte-for-byte unchanged");
        Require(retryResult.json!.RootElement.GetProperty("data").GetProperty("runId").GetString() == linkedRetry.Id,
            "retry terminal receipt names its exact new retained run without returning the entire authority object");
        Require(retryResult.stderr.Contains("bytes processed") && retryResult.stderr.Contains("b-denied.txt"),
            "actual retry hashing and transfer streams emit current file and measured byte progression");

        // A failed remote READ may leave a partial private local stage, but it is
        // not an uncertain remote write. The real local reconciler must prove no
        // target effect, retire only its own stage and let independent files run.
        foreach (var failureCase in new (string Key, string Marker, Func<Exception> CreateError)[]
        {
            ("http", "HTTP 500", () => new DropboxApiException(HttpStatusCode.InternalServerError,
                "fixture: HTTP 500 while downloading b-failed.txt")),
            ("stream", "truncated content", () => new IOException("fixture: truncated content for b-failed.txt")),
            ("access", "stage access denied", () => new UnauthorizedAccessException("fixture: stage access denied for b-failed.txt")),
            ("skip", "HTTP 500", () => new DropboxApiException(HttpStatusCode.InternalServerError,
                "fixture: HTTP 500 while downloading b-failed.txt"))
        })
        {
            var failedDownloadRoot = Path.Combine(root, "partial-download-local-" + failureCase.Key);
            Directory.CreateDirectory(failedDownloadRoot);
            using var failedDownloadLease = PilotDirectoryLease.Open(failedDownloadRoot);
            var failedDownloadPair = SyncPair.FromPilot(new PilotSettings(failedDownloadRoot,
                failedDownloadLease.Identity.VolumeSerialNumber, failedDownloadLease.Identity.FileId,
                "/PartialDownload-" + failureCase.Key, "id:partial-download-" + failureCase.Key),
                "Partial download failure " + failureCase.Key, binding.RootNamespaceId) with
            { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
            var failedDownloadRemote = new FakeSyncRemoteStore
            {
                ExactPathMetadataForTest = true,
                DownloadFailureForTest = path => path == "b-failed.txt"
                    ? failureCase.CreateError() : null
            };
            foreach (var path in new[] { "a-good.txt", "b-failed.txt", "c-good.txt" })
                failedDownloadRemote.Put(path, "remote payload " + path);
            if (failureCase.Key == "skip")
                failedDownloadRemote.Put("d-explicitly-skipped.txt", "not approved for copying");
            pairRemotes.Add(failedDownloadPair.Id, failedDownloadRemote);
            await database.RegisterPairAsync(failedDownloadPair.Id, failedDownloadPair.PolicyRevision,
                new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                    RecursiveMode: true, AutomaticPruning: false,
                    RecoveryRetentionDays: failedDownloadPair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(failedDownloadPair, select: false);
            var downloadScan = await Cli("--cli", "scan", "--pair", failedDownloadPair.Id, "--yes", "--json");
            Require(downloadScan.exit == 0, "partial-download fixture has complete real inventories");
            var downloadChoices = new Dictionary<string, string?>
            {
                ["__plan"] = downloadScan.json!.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = downloadScan.json.RootElement.GetProperty("data").GetProperty("dropboxScanGenerationId").GetString(),
                ["a-good.txt"] = "download", ["b-failed.txt"] = "download", ["c-good.txt"] = "download"
            };
            if (failureCase.Key == "skip") downloadChoices["d-explicitly-skipped.txt"] = "no-change";
            var downloadApply = await Cli("--cli", "first-sync", "apply", "--pair", failedDownloadPair.Id,
                "--yes", "--json", "--choices", JsonSerializer.Serialize(downloadChoices));
            Require(downloadApply.exit == 5 && failedDownloadRemote.DownloadCount == 3 &&
                    await File.ReadAllTextAsync(Path.Combine(failedDownloadRoot, "a-good.txt")) == "remote payload a-good.txt" &&
                    await File.ReadAllTextAsync(Path.Combine(failedDownloadRoot, "c-good.txt")) == "remote payload c-good.txt" &&
                    !File.Exists(Path.Combine(failedDownloadRoot, "b-failed.txt")),
                "a failed download preserves both independent successes and does not promote its partial stage " +
                $"(exit {downloadApply.exit}; exact download calls {failedDownloadRemote.DownloadCount}): " + downloadApply.stderr);
            Require(downloadApply.stderr.Contains("b-failed.txt") && downloadApply.stderr.Contains(failureCase.Marker),
                "download failure publishes the exact file and provider error in the live CLI log");
            var downloadRun = (await database.LoadRunHistoryAsync(40)).Single(item =>
                IsOriginalReviewedRun(item, failedDownloadPair.Id));
            var downloadRetryPlan = await host.LoadRunRetryAsync(downloadRun.Id);
            Require(downloadRetryPlan.UnavailableReason is null &&
                    downloadRetryPlan.Items.Count(item => item.CanRetry) == 1 &&
                    downloadRetryPlan.Items.Single(item => item.CanRetry).Path == "b-failed.txt",
                "the verified-no-effect failed download alone is offered for retry");
            var recoveryEvents = await database.LoadActivityProjectionPageAsync(500, pairId: failedDownloadPair.Id);
            Require(recoveryEvents.Any(item => item.Outcome == "Previous outcome checked" &&
                    item.Categories!.Contains(SyncActivityCategories.Diagnostics) && item.OperationKind is null),
                "real no-effect reconciliation is a diagnostic check, not a newly recorded download");
            var fileEvents = await database.LoadActivityProjectionPageAsync(500, pairId: failedDownloadPair.Id,
                query: new SyncActivityQuery([SyncActivityCategories.Files]));
            Require(fileEvents.All(item => item.Outcome != "Previous outcome checked"),
                "Home file activity excludes the no-effect recovery check while the complete history retains it");
            failedDownloadRemote.DownloadFailureForTest = null;
            using (var retainedRequest = JsonDocument.Parse(downloadRun.RequestJson))
            {
                var consentId = retainedRequest.RootElement.GetProperty("consentId").GetString()!;
                var header = await database.LoadConsentHeaderAsync(consentId)
                    ?? throw new InvalidDataException("The exact original consent header is missing.");
                using var originalHeader = JsonDocument.Parse(header.HeaderJson);
                var originalItems = await database.LoadConsentItemsAsync(consentId,
                    Enumerable.Range(0, header.OriginalChoiceCount).ToArray(),
                    SyncAuthorityConsentSelectionMode.ExactRead);
                var originalApproval = originalHeader.RootElement.GetProperty("plan")
                    .Deserialize<SyncBootstrapActionPlan>()! with
                {
                    Choices = originalItems.OrderBy(item => item.Ordinal)
                        .Select(item => SyncAuthorityConsentEncoding.DeserializeChoice(item.ChoiceJson)).ToArray()
                };
                foreach (var forgedPlan in new[] { originalApproval,
                             originalApproval with { LocalScanGenerationId = "unrelated-reviewed-inventory" } })
                {
                    var refused = false;
                    try { await host.RunBootstrapActionPlanAsync(forgedPlan, retryOfRunId: downloadRun.Id); }
                    catch (InvalidDataException) { refused = true; }
                    Require(refused && failedDownloadRemote.DownloadCount == 3 &&
                            (await database.LoadRunByIdAsync(downloadRun.Id))!.ResultJson == downloadRun.ResultJson,
                        "retained retry authority rejects replay of completed choices or a substituted snapshot before effects");
                }
            }
            var downloadRetry = await Cli("--cli", "run", "retry", "--run", downloadRun.Id,
                "--choice", "failed", "--yes", "--json");
            Require(downloadRetry.exit == (failureCase.Key == "skip" ? 5 : 0) && failedDownloadRemote.DownloadCount == 4 &&
                    await File.ReadAllTextAsync(Path.Combine(failedDownloadRoot, "b-failed.txt")) == "remote payload b-failed.txt",
                "retry downloads only the failed file and never replays the two completed files: " + downloadRetry.stderr);
            var approvalAfterRetry = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == failedDownloadPair.Id);
            Require(failureCase.Key == "skip"
                    ? approvalAfterRetry is { InitialReviewApproved: false, PolicyRevision: 1 } &&
                      !File.Exists(Path.Combine(failedDownloadRoot, "d-explicitly-skipped.txt")) &&
                      failedDownloadRemote.ReadText("d-explicitly-skipped.txt") == "not approved for copying"
                    : approvalAfterRetry is { InitialReviewApproved: true, PolicyRevision: 2 },
                failureCase.Key == "skip"
                    ? "a genuine original user skip remains pending and untouched after failed-file retry; completed-peer exclusions do not approve it"
                    : "successful final retry completes first-sync approval while retaining its older proven no-effect failure history");
        }

        // A linked retry can finish an effect, then fail its atomic terminal
        // approval. Its exact completed-file summary and receipts must survive,
        // and the original run must still exclude that effect. Conversely, a
        // linked uncertain effect must remain fenced.
        foreach (var linkedCase in new[] { "followup-failure", "uncertain-target", "retry-of-retry" })
        {
            var linkedRoot = Path.Combine(root, "linked-retry-" + linkedCase);
            Directory.CreateDirectory(linkedRoot);
            using var linkedLease = PilotDirectoryLease.Open(linkedRoot);
            var linkedPair = SyncPair.FromPilot(new PilotSettings(linkedRoot,
                linkedLease.Identity.VolumeSerialNumber, linkedLease.Identity.FileId,
                "/LinkedRetry-" + linkedCase, "id:linked-retry-" + linkedCase),
                "Linked retry " + linkedCase, binding.RootNamespaceId) with
            { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
            var linkedRemote = new FakeSyncRemoteStore
            {
                ExactPathMetadataForTest = true,
                DownloadFailureForTest = path => path == "b-retry.txt"
                    ? new DropboxApiException(HttpStatusCode.InternalServerError, "fixture: first download refused") : null
            };
            linkedRemote.Put("a-done.txt", "already completed");
            linkedRemote.Put("b-retry.txt", "retry completes once");
            pairRemotes.Add(linkedPair.Id, linkedRemote);
            await database.RegisterPairAsync(linkedPair.Id, linkedPair.PolicyRevision,
                new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                    RecursiveMode: true, AutomaticPruning: false,
                    RecoveryRetentionDays: linkedPair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(linkedPair, select: false);
            var linkedScan = await Cli("--cli", "scan", "--pair", linkedPair.Id, "--yes", "--json");
            Require(linkedScan.exit == 0, "linked retry fixture has complete real inventories");
            var linkedChoices = new Dictionary<string, string?>
            {
                ["__plan"] = linkedScan.json!.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = linkedScan.json.RootElement.GetProperty("data").GetProperty("dropboxScanGenerationId").GetString(),
                ["a-done.txt"] = "download", ["b-retry.txt"] = "download"
            };
            var linkedApply = await Cli("--cli", "first-sync", "apply", "--pair", linkedPair.Id,
                "--yes", "--json", "--choices", JsonSerializer.Serialize(linkedChoices));
            Require(linkedApply.exit == 5 && linkedRemote.DownloadCount == 2 &&
                    File.Exists(Path.Combine(linkedRoot, "a-done.txt")) && !File.Exists(Path.Combine(linkedRoot, "b-retry.txt")),
                "original linked-retry run has one completed and one proven failed effect");
            var linkedOrigin = (await database.LoadRunHistoryAsync(40)).Single(item =>
                IsOriginalReviewedRun(item, linkedPair.Id));
            linkedRemote.DownloadFailureForTest = linkedCase == "followup-failure" ? null :
                linkedCase == "retry-of-retry" ? path => path == "b-retry.txt"
                    ? new DropboxApiException(HttpStatusCode.InternalServerError, "fixture: retry request refused again") : null : path =>
            {
                if (path != "b-retry.txt") return null;
                File.WriteAllText(Path.Combine(linkedRoot, path), "foreign target must survive");
                return new DropboxApiException(HttpStatusCode.InternalServerError, "fixture: uncertain foreign target");
            };
            var previousLinkedFinalizationBoundary = database.FirstSyncFinalizationBoundaryForTest;
            if (linkedCase == "followup-failure")
                database.FirstSyncFinalizationBoundaryForTest = phase =>
                {
                    if (phase == "approval-written-before-terminal-result")
                        throw new IOException("fixture: linked retry terminal approval unavailable");
                };
            (int exit, JsonDocument? json, string stderr) linkedResult;
            try
            {
                linkedResult = await Cli("--cli", "run", "retry", "--run", linkedOrigin.Id,
                    "--pair", linkedPair.Id, "--choice", "failed", "--yes", "--json");
            }
            finally { database.FirstSyncFinalizationBoundaryForTest = previousLinkedFinalizationBoundary; }
            var linkedRun = (await database.LoadRunHistoryAsync(40)).Single(item =>
                item.RequestJson.Contains("\"retryOfRunId\":\"" + linkedOrigin.Id + "\""));
            Require(linkedRemote.DownloadCount == 3 && linkedRun.CompletedUtc is not null,
                "actual linked retry attempts only its failed file and closes its own retained run");
            if (linkedCase == "retry-of-retry")
            {
                Require(linkedResult.exit == 5, "a failed retry retains its file receipt and returns review-required, not success");
                var nestedPreview = await host.LoadRunRetryAsync(linkedRun.Id);
                Require(nestedPreview.UnavailableReason is null && nestedPreview.Items.Count == 2 &&
                    nestedPreview.Items.Single(item => item.Path == "a-done.txt") is { State: "Completed", CanRetry: false } &&
                    nestedPreview.Items.Single(item => item.Path == "b-retry.txt") is { State: "Failed", CanRetry: true },
                    "latest retry includes the original completed ancestor and the exact remaining failure");
                linkedRemote.DownloadFailureForTest = null;
                var nestedResult = await Cli("--cli", "run", "retry", "--run", linkedRun.Id,
                    "--pair", linkedPair.Id, "--choice", "failed", "--yes", "--json");
                var approvedNested = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == linkedPair.Id);
                Require(nestedResult.exit == 0 && nestedResult.json!.RootElement.GetProperty("data")
                    .GetProperty("firstSyncComplete").GetBoolean() && linkedRemote.DownloadCount == 4 &&
                    approvedNested is { InitialReviewApproved: true, PolicyRevision: 2 } &&
                    await File.ReadAllTextAsync(Path.Combine(linkedRoot, "a-done.txt")) == "already completed" &&
                    await File.ReadAllTextAsync(Path.Combine(linkedRoot, "b-retry.txt")) == "retry completes once" &&
                    (await database.LoadRunByIdAsync(linkedOrigin.Id))!.ResultJson == linkedOrigin.ResultJson,
                    "retry-of-retry completes and atomically approves original finite consent without replaying its successful ancestor or rewriting failure history");
                var nestedReplay = await Cli("--cli", "run", "retry", "--run", linkedRun.Id,
                    "--pair", linkedPair.Id, "--choice", "all", "--yes", "--json");
                Require(nestedReplay.exit != 0 && linkedRemote.DownloadCount == 4,
                    "completed multi-level review refuses replay from either child identity");
                continue;
            }
            if (linkedCase == "followup-failure")
            {
                using var linkedTerminal = JsonDocument.Parse(linkedRun.ResultJson!);
                Require(linkedResult.exit != 0 && linkedTerminal.RootElement.GetProperty("skipped").GetInt32() == 0 &&
                        linkedTerminal.RootElement.GetProperty("files").EnumerateArray()
                        .Any(item => item.GetProperty("RelativePath").GetString() == "b-retry.txt" &&
                            item.GetProperty("Kind").GetInt32() == (int)SyncDecisionKind.Downloaded) &&
                        await File.ReadAllTextAsync(Path.Combine(linkedRoot, "b-retry.txt")) == "retry completes once",
                    "actual failed linked retry retains its exact completed-file summary and bytes after atomic approval rollback; completed peers are not counted as user skips");
            }
            var linkedPreview = await host.LoadRunRetryAsync(linkedOrigin.Id);
            Require(linkedPreview.UnavailableReason is null && linkedPreview.Items.Count == 2 &&
                    linkedPreview.Items.Single(item => item.Path == "a-done.txt") is { State: "Completed", CanRetry: false } &&
                    linkedPreview.Items.Single(item => item.Path == "b-retry.txt") is
                        { State: var linkedState, CanRetry: false, OperationId: not null } &&
                    linkedState == (linkedCase == "followup-failure" ? "Completed" : "Verify first"),
                "original preview consumes linked operation receipts: completed is not replayed and uncertain is not admitted");
            var linkedReplay = await Cli("--cli", "run", "retry", "--run", linkedOrigin.Id,
                "--choice", "all", "--yes", "--json");
            Require((linkedCase == "followup-failure" ? linkedReplay.exit == 0 : linkedReplay.exit != 0) && linkedRemote.DownloadCount == 3 &&
                    await File.ReadAllTextAsync(Path.Combine(linkedRoot, "b-retry.txt")) ==
                        (linkedCase == "followup-failure" ? "retry completes once" : "foreign target must survive"),
                "all-completed retained work finishes atomic approval without replay; linked uncertainty still refuses effects");
        }

        var foreignRoot = Path.Combine(root, "failed-download-foreign-target");
        Directory.CreateDirectory(foreignRoot);
        using var foreignLease = PilotDirectoryLease.Open(foreignRoot);
        var foreignPair = SyncPair.FromPilot(new PilotSettings(foreignRoot,
            foreignLease.Identity.VolumeSerialNumber, foreignLease.Identity.FileId,
            "/ForeignDownload", "id:foreign-download"), "Conflicting download outcome", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var foreignRemote = new FakeSyncRemoteStore
        {
            ExactPathMetadataForTest = true,
            DownloadFailureForTest = path =>
            {
                if (path != "a-conflict.txt") return null;
                File.WriteAllText(Path.Combine(foreignRoot, path), "another application's independent file");
                return new DropboxApiException(HttpStatusCode.InternalServerError, "fixture: HTTP 500 with foreign target");
            }
        };
        foreignRemote.Put("a-conflict.txt", "remote content");
        foreignRemote.Put("b-later.txt", "independent work already admitted may finish safely");
        pairRemotes.Add(foreignPair.Id, foreignRemote);
        await database.RegisterPairAsync(foreignPair.Id, foreignPair.PolicyRevision,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                RecursiveMode: true, AutomaticPruning: false,
                RecoveryRetentionDays: foreignPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(foreignPair, select: false);
        var foreignScan = await Cli("--cli", "scan", "--pair", foreignPair.Id, "--yes", "--json");
        Require(foreignScan.exit == 0, "foreign-target rejection fixture has complete real inventories");
        var foreignChoices = new Dictionary<string, string?>
        {
            ["__plan"] = foreignScan.json!.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString(),
            ["__remote_plan"] = foreignScan.json.RootElement.GetProperty("data").GetProperty("dropboxScanGenerationId").GetString(),
            ["a-conflict.txt"] = "download", ["b-later.txt"] = "download"
        };
        var foreignApply = await Cli("--cli", "first-sync", "apply", "--pair", foreignPair.Id,
            "--yes", "--json", "--choices", JsonSerializer.Serialize(foreignChoices));
        var foreignResult = foreignApply.json!.RootElement.GetProperty("data");
        Require(foreignApply.exit == 5 && !foreignResult.GetProperty("firstSyncComplete").GetBoolean() &&
                foreignResult.GetProperty("files").EnumerateArray().Any(item =>
                    item.GetProperty("relativePath").GetString() == "a-conflict.txt" &&
                    item.GetProperty("kind").GetInt32() == (int)SyncDecisionKind.ResponseUnknown) &&
                await File.ReadAllTextAsync(Path.Combine(foreignRoot, "a-conflict.txt")) == "another application's independent file",
            "an unverified foreign target is preserved and never mislabeled as a safe no-effect download failure: " + foreignApply.stderr);
        // Concurrent independent work can already be in flight when the unknown
        // outcome is discovered. It must be drained and reported truthfully;
        // unadmitted work remains untouched (the scheduler checks prove that fence).
        var otherFile = foreignResult.GetProperty("files").EnumerateArray().Single(item =>
            item.GetProperty("relativePath").GetString() == "b-later.txt");
        var otherNotAttempted = otherFile.GetProperty("notAttempted").GetBoolean();
        Require(otherNotAttempted
                ? foreignRemote.DownloadCount == 1 && !File.Exists(Path.Combine(foreignRoot, "b-later.txt"))
                : foreignRemote.DownloadCount == 2 &&
                  otherFile.GetProperty("kind").GetInt32() == (int)SyncDecisionKind.Downloaded &&
                  await File.ReadAllTextAsync(Path.Combine(foreignRoot, "b-later.txt")) ==
                      "independent work already admitted may finish safely",
            "unknown-effect stop retains the actual outcome of independent admitted work and never invents an unattempted result");
        var foreignRevisionRejected = false;
        try
        {
            await database.RegisterPairAsync(foreignPair.Id, foreignPair.PolicyRevision + 1,
                new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                    RecursiveMode: true, AutomaticPruning: false,
                    RecoveryRetentionDays: foreignPair.RecoveryRetentionDays ?? int.MaxValue));
        }
        catch (InvalidOperationException) { foreignRevisionRejected = true; }
        var foreignDefinitionRejected = false;
        try { await database.RetainPairDefinitionAsync(foreignPair with { Name = "Changed despite unknown effect" }, false); }
        catch (InvalidOperationException) { foreignDefinitionRejected = true; }
        Require(foreignRevisionRejected && foreignDefinitionRejected &&
                (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == foreignPair.Id)
                    is { InitialReviewApproved: false, PolicyRevision: 1 },
            "both pair revision and definition changes remain fenced while a foreign-target outcome is unresolved");

        // Use an already approved pair for the same partial-file behavior and
        // per-file retry. These are production scheduler/runner decisions.
        var regularPair = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        await File.WriteAllTextAsync(Path.Combine(regularPair.LocalRoot, "normal-good.txt"), "normal good");
        await File.WriteAllTextAsync(Path.Combine(regularPair.LocalRoot, "normal-denied.txt"), "normal denied");
        var normalRemote = remote;
        var attempts = 0;
        normalRemote.UploadFailureForTest = path => path == "normal-denied.txt"
            ? (++attempts > 0 ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: normal file locked") : null) : null;
        var normal = await Cli("--cli", "run", "--pair", regularPair.Id, "--yes", "--json");
        Require(normalRemote.Exists("normal-good.txt") && !normalRemote.Exists("normal-denied.txt") && attempts == 1,
            "normal sync completes good files and attempts the rejected file only once in that run");
        var normalRun = (await database.LoadRunHistoryAsync(40)).First(item => item.RequestJson.Contains(regularPair.Id) &&
            item.ResultJson?.Contains("normal-denied.txt") == true);
        Require(normal.exit == 5 && normal.json!.RootElement.GetProperty("data")
                    .GetProperty("runId").GetString() == normalRun.Id &&
                normal.json.RootElement.GetProperty("data").GetProperty("failed").GetInt32() > 0 &&
                normal.stderr.Contains("PRAGMA quick_check", StringComparison.Ordinal),
            "normal partial failure carries its exact retained run, failed-file counts and live database admission detail");
        var normalPlan = await host.LoadRunRetryAsync(normalRun.Id);
        Require(normalPlan.UnavailableReason is null && normalPlan.Items.Any(item => item.Path == "normal-denied.txt" && item.CanRetry),
            "normal-run retry exposes the exact retained failed file");
        var beforeNormalRetry = normalRemote.UploadCount;
        normalRemote.UploadFailureForTest = null;
        var normalRetry = await Cli("--cli", "run", "retry", "--run", normalRun.Id, "--choice", "failed", "--yes", "--json");
        Require(normalRetry.exit == 0 && normalRemote.Exists("normal-denied.txt") && normalRemote.UploadCount == beforeNormalRetry + 1,
            "normal-run retry uses the shared engine and does not recopy completed files: " + normalRetry.stderr);
        var resolvedRetryPlan = await host.LoadRunRetryAsync(normalRun.Id);
        Require(resolvedRetryPlan.Items.Single(item => item.Path == "normal-denied.txt") is { State: "Completed", CanRetry: false },
            "an original failed run cannot re-offer a file already completed by its linked retry");
        var operationLog = await database.LoadActivityProjectionPageAsync(500, pairId: regularPair.Id,
            query: new SyncActivityQuery(RunId: normalRun.Id, DetailedLog: true));
        Require(!operationLog.Any(item => item.Outcome == "Preparing Command") &&
                normal.stderr.Contains("PRAGMA quick_check", StringComparison.Ordinal),
            "normal CLI delivers the actual pre-transfer check live without permanently retaining transient preparation");
        Require(operationLog.Any(item => item.RelativePath == "normal-denied.txt" &&
                    item.Reason.Contains("normal file locked", StringComparison.Ordinal)) &&
                !operationLog.Any(item => item.Reason.Contains("prerequisite check for normal-denied.txt")),
            "the exact failed-file outcome remains in the operation log while transient source hashing is not retained");

        // The CLI and GUI must both finish independent one-sided files while
        // leaving a two-sided conflict untouched, never interpreting it as consent.
        var mixedRoot = Path.Combine(root, "mixed-conflict-local");
        Directory.CreateDirectory(mixedRoot);
        await File.WriteAllTextAsync(Path.Combine(mixedRoot, "conflict.txt"), "local conflict bytes");
        using var mixedLease = PilotDirectoryLease.Open(mixedRoot);
        var mixedPair = SyncPair.FromPilot(new PilotSettings(mixedRoot,
            mixedLease.Identity.VolumeSerialNumber, mixedLease.Identity.FileId,
            "/MixedConflict", "id:mixed-conflict"), "Mixed first sync", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        var mixedRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        mixedRemote.Put("conflict.txt", "remote conflict bytes");
        mixedRemote.Put("good.txt", "independent remote payload");
        pairRemotes.Add(mixedPair.Id, mixedRemote);
        await database.RegisterPairAsync(mixedPair.Id, mixedPair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: mixedPair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(mixedPair, select: false);
        var mixedScan = await Cli("--cli", "scan", "--pair", mixedPair.Id, "--yes", "--json");
        Require(mixedScan.exit == 0, "mixed conflict fixture has complete real inventories");
        var mixedChoices = new Dictionary<string, string?>
        {
            ["__plan"] = mixedScan.json!.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString(),
            ["good.txt"] = "download", ["conflict.txt"] = "download"
        };
        var unsafeConflict = await Cli("--cli", "first-sync", "apply", "--pair", mixedPair.Id,
            "--yes", "--json", "--choices", JsonSerializer.Serialize(mixedChoices));
        Require(unsafeConflict.exit != 0 && mixedRemote.DownloadCount == 0 && mixedRemote.UploadCount == 0 &&
                await File.ReadAllTextAsync(Path.Combine(mixedRoot, "conflict.txt")) == "local conflict bytes",
            "first-sync choices cannot turn a conflict into an unreviewed overwrite");
        mixedChoices["conflict.txt"] = "no-change";

        // Retain an actual active lease to prove early admission failures now
        // produce a retrievable exact run/problem, not a transient footer only.
        var remoteGeneration = mixedScan.json.RootElement.GetProperty("data").GetProperty("dropboxScanGenerationId").GetString()!;
        var good = (await database.LoadCompleteObservationAsync(mixedPair.Id, "dropbox", remoteGeneration))
            .Single(item => item.RelativePath == "good.txt");
        var mixedAdmission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", mixedPair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await database.TransitionCommandRequestAsync(mixedAdmission.CommandRequestId, "executing");
        var heldRegistration = await database.ReserveActionAsync("admission-held-key", mixedAdmission);
        var heldSet = await database.LoadPlanningObservationSetAsync(mixedPair.Id, mixedPair.PolicyRevision,
            await database.LoadActiveRootBindingIdAsync(mixedPair.Id, "local"),
            await database.LoadActiveRootBindingIdAsync(mixedPair.Id, "dropbox"),
            mixedScan.json.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString()!,
            remoteGeneration);
        await database.CreateOperationAsync(new(heldRegistration.OperationId, "admission-held-key", SyncAuthorityOperationKind.CreateLocal,
            mixedPair.Id, good.ObjectId, mixedPair.PolicyRevision, host.CapabilityProfileIdForTest, "{}", 0,
            [new(heldRegistration.OperationId + "-source", mixedPair.Id, good.ObjectId, "source", good.RootBindingId,
                good.MembershipId, good.ObjectVersionId, good.MembershipGeneration, good.RelativePath, "{}")], [], [], PlanningAuthorities: [heldSet.Bind(
                SyncAuthorityReconciliationPlanner.ObservationSetIdentity(heldSet), good.ObjectId)]),
            mixedAdmission, reservation: heldRegistration.Reservation);
        var heldLease = await database.AcquireOperationLeaseAsync(heldRegistration.OperationId, "held-worker", TimeSpan.FromMinutes(5));
        var heldRetryEvidence = (await database.LoadRetryOperationEvidenceAsync([heldRegistration.OperationId])).Single();
        Require(!heldRetryEvidence.Unissued && !heldRetryEvidence.KnownNoEffectRefusal && heldRetryEvidence.Path == "good.txt",
            "batched retry evidence preserves the exact path and refuses an active lease without an issued attempt");
        var refusedAdmission = await Cli("--cli", "first-sync", "apply", "--pair", mixedPair.Id,
            "--yes", "--json", "--choices", JsonSerializer.Serialize(mixedChoices));
        var admissionProblem = (await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == mixedPair.Id);
        var admissionRun = await database.LoadRunByIdAsync(admissionProblem.CommandId);
        Require(refusedAdmission.exit != 0 && admissionProblem.Message.Contains(heldRegistration.OperationId) &&
                admissionProblem.Message.Contains("good.txt") && admissionRun?.CompletedUtc is not null &&
                admissionRun.Status == "blocked" && mixedRemote.DownloadCount == 0,
            "an admission refusal is retained with its exact file, operation, terminal run and first-sync problem");
        Require((await database.LoadOperationAsync(heldRegistration.OperationId)).Status == SyncAuthorityOperationStatus.Leased,
            "reviewed retry never supersedes a plan held by an active operation lease");
        await database.ReleaseLeaseAsync(heldLease);
        var noEffectFailure = await database.RetainExecutionFailureAsync(heldRegistration.OperationId, "fixture-released",
            new IOException("Fixture holder stopped before dispatch"), CancellationToken.None);
        await database.TransitionCommandRequestAsync(mixedAdmission.CommandRequestId, "completed");
        var retainedNoEffect = await database.LoadOperationAsync(heldRegistration.OperationId);
        Require(noEffectFailure.Status == SyncAuthorityOperationStatus.Blocked &&
                retainedNoEffect.Status == SyncAuthorityOperationStatus.Superseded &&
                retainedNoEffect.Attempts.Count == 0 && noEffectFailure.Reason.Contains("Fixture holder stopped") &&
                mixedRemote.DownloadCount == 0,
            "an unissued execution refusal uses the sealed supersession transition and retains its real failure without effects");
        var mixedApply = await Cli("--cli", "first-sync", "apply", "--pair", mixedPair.Id,
            "--yes", "--json", "--choices", JsonSerializer.Serialize(mixedChoices));
        // A conflict explicitly left for separate resolution is not a failed
        // reviewed action. The finite approved download completes and approval
        // is saved; neither conflicting version is touched or approved to copy.
        Require(mixedApply.exit == 0 && mixedApply.json!.RootElement.GetProperty("data")
                    .GetProperty("firstSyncComplete").GetBoolean() &&
                mixedRemote.DownloadCount == 1 && mixedRemote.UploadCount == 0 &&
                await File.ReadAllTextAsync(Path.Combine(mixedRoot, "good.txt")) == "independent remote payload" &&
                await File.ReadAllTextAsync(Path.Combine(mixedRoot, "conflict.txt")) == "local conflict bytes" &&
                mixedRemote.ReadText("conflict.txt") == "remote conflict bytes" &&
                mixedApply.stderr.Contains("1 conflict(s) will remain unchanged"),
            "CLI applies independent reviewed files and reports the preserved conflict (exit " + mixedApply.exit +
            "; result " + mixedApply.json?.RootElement.GetRawText() + "): " + mixedApply.stderr);

        await InterruptedReviewedPlanRetryAsync(priorBuild: false);
        await InterruptedReviewedPlanRetryAsync(priorBuild: true);
        await LeaseRenewalChecks.RunReviewedReplanningGuardsAsync(Path.Combine(root, "retained-replanning-guards"));
        await CloudProAuthorityPreflight.AssertDropboxRevisionChurnMoveGuardsAsync(
            Path.Combine(root, "retained-move-guards"),
            new HashSet<string>(["occupied-remote-source", "reserved-local-target",
                "reviewed-copy-content-change", "reviewed-copy-local-divergence",
                "reviewed-copy-metadata-change", "reviewed-copy-reserved-target"], StringComparer.Ordinal),
            verifyRetryIdentities: true);

        async Task InterruptedReviewedPlanRetryAsync(bool priorBuild)
        {
            var label = priorBuild ? "prior-build" : "same-build";
            var retryRoot = Path.Combine(root, "interrupted-" + label);
            Directory.CreateDirectory(retryRoot);
            using var retryLease = PilotDirectoryLease.Open(retryRoot);
            var retryPair = SyncPair.FromPilot(new PilotSettings(retryRoot,
                retryLease.Identity.VolumeSerialNumber, retryLease.Identity.FileId,
                "/Interrupted-" + label, "id:interrupted-" + label), "Interrupted " + label,
                binding.RootNamespaceId) with { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
            var retryRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            retryRemote.Put("a.txt", "first exact remote bytes");
            retryRemote.Put("b.txt", "second exact remote bytes");
            pairRemotes.Add(retryPair.Id, retryRemote);
            await database.RegisterPairAsync(retryPair.Id, retryPair.PolicyRevision,
                new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                    "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                    RecoveryRetentionDays: retryPair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(retryPair, select: false);
            var scanResult = await Cli("--cli", "scan", "--pair", retryPair.Id, "--yes", "--json");
            Require(scanResult.exit == 0, label + ": the actual CLI saves both complete reviewed inventories");
            var scanData = scanResult.json!.RootElement.GetProperty("data");
            var localGeneration = scanData.GetProperty("localScanGenerationId").GetString()!;
            var remoteGeneration = scanData.GetProperty("dropboxScanGenerationId").GetString()!;
            var retainedPair = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == retryPair.Id);
            var localControl = retainedPair.ControlNamespace!;
            var profile = host.CapabilityProfileIdForTest;
            if (priorBuild)
            {
                profile = "fixture-prior-build-profile";
                await database.InitializeAsync(localControl.InstallationId, profile, "fixture-prior-source", "{}");
            }
            var observations = await database.LoadPlanningObservationSetAsync(retryPair.Id,
                retryPair.PolicyRevision, await database.LoadActiveRootBindingIdAsync(retryPair.Id, "local"),
                await database.LoadActiveRootBindingIdAsync(retryPair.Id, "dropbox"), localGeneration, remoteGeneration);
            var interruptedAdmission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", retryPair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await database.TransitionCommandRequestAsync(interruptedAdmission.CommandRequestId, "executing");
            var oldGroup = "fixture-interrupted-" + label;
            await database.RegisterOperationGroupAsync(oldGroup, "compound", "{}");
            var options = new SyncAuthorityPlannerOptions(retryPair.PolicyRevision, profile,
                localControl.NamespaceId, localControl.NamespaceId);
            // This download-only input uses only the actual retained local namespace.
            // Inject cancellation, never a planning decision or manufactured authority result.
            using var cancellation = new CancellationTokenSource();
            var cancelled = false;
            var planner = new SyncAuthorityReconciliationPlanner(database, options, interruptedAdmission,
                new HashSet<string>(["a.txt", "b.txt"], StringComparer.OrdinalIgnoreCase), oldGroup,
                onComparison: step =>
                {
                    if (step.RelativePath == "b.txt")
                    {
                        cancellation.Cancel();
                        return Task.FromCanceled(cancellation.Token);
                    }
                    return Task.CompletedTask;
                });
            try
            {
                await planner.PlanAsync(observations,
                    SyncAuthorityReconciliationPlanner.ObservationSetIdentity(observations),
                    cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            string oldId;
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                             { DataSource = database.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                                 Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT operation_id FROM operation WHERE pair_id=$pair AND status='ready';";
                command.Parameters.AddWithValue("$pair", retryPair.Id);
                await using var reader = await command.ExecuteReaderAsync();
                Require(cancelled && await reader.ReadAsync(), label + ": cancellation occurs after a real plan is saved");
                oldId = reader.GetString(0);
                Require(!await reader.ReadAsync() && retryRemote.DownloadCount == 0,
                    label + ": exactly one unissued plan remains and no file effect was attempted");
            }
            var old = await database.LoadOperationAsync(oldId);
            await database.ReconcileUnissuedOperationLeasesAsync(retryPair.Id, CancellationToken.None);
            Require((await database.LoadOperationAsync(oldId)).Status == SyncAuthorityOperationStatus.Ready,
                label + ": ordinary recovery does not retire a ready plan");
            var rawPreconditions = System.Text.Json.Nodes.JsonNode.Parse(old.PreconditionsJson)!.AsObject();
            rawPreconditions.Remove("planningAuthorities");
            var exactPlan = new SyncAuthorityOperationPlan(old.OperationId, old.IdempotencyKey, old.Kind,
                old.PairId, old.ObjectId, old.ConfigurationRevision, old.CapabilityProfileId,
                rawPreconditions.ToJsonString(), 50, old.Members, old.Dependencies, old.Allocations, oldGroup,
                old.PlanningAuthorities);
            await database.CreateOperationAsync(exactPlan, interruptedAdmission);
            Require((await database.LoadOperationAsync(oldId)).Attempts.Count == 0,
                label + ": exact immutable-plan replay remains idempotent without dispatch");
            await database.InitializeAsync(localControl.InstallationId, "fixture-conflicting-profile",
                "fixture-conflicting-source", "{}");
            var profileCollisionRejected = false;
            try { await database.CreateOperationAsync(exactPlan with { CapabilityProfileId = "fixture-conflicting-profile" }, interruptedAdmission); }
            catch (InvalidOperationException error) { profileCollisionRejected = error.Message.Contains("collides with different retained authority"); }
            Require(profileCollisionRejected && (await database.LoadOperationAsync(oldId)).CapabilityProfileId == profile,
                label + ": the immutable collision guard still rejects a changed profile on the same operation id");
            var conflictingGroup = oldGroup + "-conflicting";
            await database.RegisterOperationGroupAsync(conflictingGroup, "compound", "{}");
            var collisionRejected = false;
            try { await database.CreateOperationAsync(exactPlan with { OperationGroupId = conflictingGroup }, interruptedAdmission); }
            catch (InvalidOperationException error) { collisionRejected = error.Message.Contains("collides with different retained authority"); }
            Require(collisionRejected && (await database.LoadOperationAsync(oldId)).Status == SyncAuthorityOperationStatus.Ready,
                label + ": the immutable collision guard still rejects a changed group on the same operation id");

            if (priorBuild)
            {
                await database.EnsureReviewedRunCanStartAsync(retryPair.Id, CancellationToken.None);
                var currentProfilePlanner = new SyncAuthorityReconciliationPlanner(database,
                    options with { CapabilityProfileId = host.CapabilityProfileIdForTest }, interruptedAdmission,
                    new HashSet<string>(["a.txt", "b.txt"], StringComparer.OrdinalIgnoreCase), oldGroup);
                var currentPlans = await currentProfilePlanner.PlanAsync(observations,
                    SyncAuthorityReconciliationPlanner.ObservationSetIdentity(observations));
                Require(currentPlans.Count == 2 && currentPlans.All(item => item.OperationId is not null &&
                            item.OperationId != oldId) && retryRemote.DownloadCount == 0,
                    "a changed capability profile alone produces distinct real plans within the same group, never an effect replay");
            }

            await database.TransitionCommandRequestAsync(interruptedAdmission.CommandRequestId, "completed");
            var retry = await Cli("--cli", "first-sync", "apply", "--pair", retryPair.Id, "--yes", "--json",
                "--choices", JsonSerializer.Serialize(new Dictionary<string, string?>
                { ["__plan"] = localGeneration, ["a.txt"] = "download", ["b.txt"] = "download" }));
            var superseded = await database.LoadOperationAsync(oldId);
            Require(retry.exit == 0 && retryRemote.DownloadCount == 2 &&
                    await File.ReadAllTextAsync(Path.Combine(retryRoot, "a.txt")) == "first exact remote bytes" &&
                    await File.ReadAllTextAsync(Path.Combine(retryRoot, "b.txt")) == "second exact remote bytes",
                label + ": the real CLI retries the same reviewed inventories and completes both exact files: " + retry.stderr);
            Require(superseded.Status == SyncAuthorityOperationStatus.Superseded && superseded.Attempts.Count == 0 &&
                    superseded.CapabilityProfileId == old.CapabilityProfileId &&
                    superseded.PreconditionsJson == old.PreconditionsJson &&
                    superseded.IdempotencyKey == old.IdempotencyKey &&
                    superseded.Members.SequenceEqual(old.Members),
                label + ": retry retains the original immutable authority and never dispatches its superseded plan");
            var retirementLog = (await database.LoadActivityProjectionPageAsync(pairId: retryPair.Id,
                    query: new SyncActivityQuery(DetailedLog: true))).Single(item =>
                    item.OperationId == oldId && item.Outcome == "Unissued plan discarded");
            Require(retirementLog.Phase == SyncActivityPhase.Superseded &&
                    retirementLog.Reason.Contains("No file was transferred or changed") &&
                    retirementLog.Categories!.Contains(SyncActivityCategories.Sync),
                label + ": the real detailed log exposes the exact no-effect retirement instead of hiding maintenance");
        }

        await ReviewedPlanningBatchAsync("success");
        await ReviewedPlanningBatchAsync("nested-success");
        await ReviewedPlanningBatchAsync("commit-refusal");
        await ReviewedPlanningBatchAsync("cancellation");
        await ReviewedPlanningBatchAsync("later-commit-refusal");
        await ReviewedPlanningBatchAsync("stream-cancellation");
        checks += await ExecutionReservationCandidateChecks.RunAsync(database);

        async Task ReviewedPlanningBatchAsync(string mode)
        {
            const int count = 24;
            var batchRoot = Path.Combine(root, "reviewed-batch-" + mode);
            Directory.CreateDirectory(batchRoot);
            using var batchLease = PilotDirectoryLease.Open(batchRoot);
            var batchPair = SyncPair.FromPilot(new PilotSettings(batchRoot,
                batchLease.Identity.VolumeSerialNumber, batchLease.Identity.FileId,
                "/ReviewedBatch-" + mode, "id:reviewed-batch-" + mode),
                "Reviewed metadata batch " + mode, binding.RootNamespaceId) with
            { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
            var batchRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            var prefix = mode == "nested-success" ? "existing/" : "";
            string BatchPath(int index) => prefix + "batch-" + index.ToString("000") + ".txt";
            if (prefix.Length != 0)
            {
                Directory.CreateDirectory(Path.Combine(batchRoot, "existing"));
                batchRemote.PutDirectory("existing");
            }
            for (var index = 0; index < count; index++)
                batchRemote.PutBytes(BatchPath(index),
                    Encoding.UTF8.GetBytes("exact reviewed payload " + index));
            pairRemotes.Add(batchPair.Id, batchRemote);
            await database.RegisterPairAsync(batchPair.Id, batchPair.PolicyRevision,
                new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                    "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                    RecoveryRetentionDays: batchPair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(batchPair, select: false);
            var scanBatch = await Cli("--cli", "scan", "--pair", batchPair.Id, "--yes", "--json");
            Require(scanBatch.exit == 0, mode + ": real reviewed batch scan is complete");
            var choices = new Dictionary<string, string?>
            {
                ["__plan"] = scanBatch.json!.RootElement.GetProperty("data")
                    .GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = scanBatch.json.RootElement.GetProperty("data")
                    .GetProperty("dropboxScanGenerationId").GetString()
            };
            for (var index = 0; index < count; index++)
                choices[BatchPath(index)] = "download";

            // AUD02: own and dispose the failed run's actual Host before reopening
            // the same database/state. The other batch cases retain their wiring.
            ISyncRemoteStore RetainedRemote(SyncPair requestedPair, DropboxConnectionBinding _) =>
                pairRemotes.TryGetValue(requestedPair.Id, out var selectedRemote) ? selectedRemote : remote;
            using var prefixHost = mode is "later-commit-refusal" or "stream-cancellation"
                ? new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                    new SyncRuntimeStateStore(authority), connections, new FixtureCredentialStore(),
                    database, RetainedRemote)
                : null;
            var batchHost = prefixHost ?? host;
            var batchOperations = prefixHost is null ? operations : new FixtureOperations(batchHost, authority);

            // Persist through the product store, not the host settings API. The
            // latter updates the limiter and would hide a missing first-sync load.
            var priorTransferSettings = batchHost.LoadTransferSettings();
            await batchHost.SaveTransferSettingsAsync(new SyncTransferSettings(ResourceMode: "Auto"));
            var persistedTransferSettings = new SyncTransferSettings(ResourceMode: "Performance",
                MaximumConcurrentFiles: 4);
            var transferStore = new SyncTransferSettingsStore(authority);
            await transferStore.SaveAsync(persistedTransferSettings);
            Require(batchHost.TransferResourceStatus.Mode == "Auto" &&
                    transferStore.Load() == persistedTransferSettings,
                mode + ": fixture retains Performance settings without pre-initializing the host limiter");

            var batches = new System.Collections.Concurrent.ConcurrentQueue<(int Size, int Effects, bool Planning)>();
            var candidateSnapshots = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var timings = new System.Collections.Concurrent.ConcurrentDictionary<string, (long Count, double Milliseconds)>();
            using var cancellation = new CancellationTokenSource();
            var firstCommit = 0;
            const string laterCommitFailure = "Injected reviewed-planning second pre-commit refusal.";
            (string Id, string Group, string GroupState)[] prefixAtFault = [];
            var laterFaultInjected = false;
            SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest = candidateSnapshots.Enqueue;
            SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = size =>
            {
                var planning = batchHost.ActiveRun?.Progress?.Comparison?.Phase == "Comparing Files and Folders";
                batches.Enqueue((size, batchRemote.DownloadCount, planning));
                // Stage-change log flushes can legitimately commit one request. Attack
                // the actual multi-file PLANNING commit, not a progress or effect batch.
                if (!planning || size < 2) return;
                var ordinal = Interlocked.Increment(ref firstCommit);
                if (mode == "stream-cancellation" && batchRemote.DownloadCount >= 16)
                    cancellation.Cancel();
                if (mode == "later-commit-refusal" && ordinal == 2)
                {
                    // This independent WAL reader sees only earlier COMMITTED rows,
                    // not the rows in the transaction about to be refused. It does
                    // not decide what the planner or public recovery may execute.
                    prefixAtFault = ReadBatchStateAsync().GetAwaiter().GetResult().Plans;
                    laterFaultInjected = true;
                    throw new IOException(laterCommitFailure);
                }
                if (ordinal != 1) return;
                if (mode == "commit-refusal") throw new IOException("Injected reviewed-planning pre-commit refusal.");
                if (mode == "cancellation") cancellation.Cancel();
            };
            SyncAuthorityDatabase.TimingForTest = (phase, elapsed) => timings.AddOrUpdate(phase,
                (1, elapsed), (_, previous) => (previous.Count + 1, previous.Milliseconds + elapsed));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                exit = await SyncCli.ExecuteAsync(["--cli", "first-sync", "apply", "--pair", batchPair.Id,
                    "--yes", "--json", "--choices", JsonSerializer.Serialize(choices)],
                    stdout, stderr, () => batchOperations, cancellation.Token);
            }
            finally
            {
                watch.Stop();
                SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null;
                SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest = null;
                SyncAuthorityDatabase.TimingForTest = null;
            }
            Require(batchHost.TransferResourceStatus.Mode == "Performance" &&
                    batchHost.TransferResourceStatus.MaximumFiles == 4,
                mode + ": real first-sync CLI loads the persisted mode and file limit before planning or dispatch");
            Require(batches.Any(item => item.Planning && item.Size is > 1 and <= 16 && item.Effects == 0),
                mode + ": actual reviewed planner batches independent file metadata before any effect");
            Require(!batches.IsEmpty && timings.ContainsKey("write-body"),
                mode + ": actual SQLite timing is captured, not inferred from progress counters");
            Require(!candidateSnapshots.IsEmpty && timings.ContainsKey("reservation-candidate-snapshot"),
                mode + ": actual planning transactions build complete coarse headers with observed timing");
            if (mode is "success" or "nested-success")
                Require(candidateSnapshots.Count < count,
                    "real CLI planning reuses transaction-bound headers instead of reading history once per file");
            Console.WriteLine("TIMING reviewed planning " + mode + ": " + JsonSerializer.Serialize(new
            {
                elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                candidateSnapshotSizes = candidateSnapshots.ToArray(),
                batchSizes = batches.Select(item => item.Size).ToArray(),
                phases = timings.ToDictionary(item => item.Key,
                    item => new { count = item.Value.Count, milliseconds = item.Value.Milliseconds })
            }));
            var retainedPair = (await batchHost.LoadPairsAsync()).Pairs.Single(item => item.Id == batchPair.Id);
            if (mode is "success" or "nested-success")
            {
                Require(batches.Any(item => item.Planning && item.Effects > 0 && item.Effects < count),
                    "a sealed first batch finishes real file effects BEFORE all later independent files are planned");
                Require(exit == 0 && batchRemote.DownloadCount == count &&
                    Enumerable.Range(0, count).All(index =>
                        File.ReadAllText(Path.Combine(batchRoot, BatchPath(index))) ==
                        "exact reviewed payload " + index),
                    "all independent reviewed files complete through the real CLI/host/planner/executor with exact bytes");
                var lines = stderr.ToString().Split('\n').Where(line => line.Contains(
                    "Comparing this item's local and remote versions", StringComparison.Ordinal)).ToArray();
                Require(lines.Length == count && Enumerable.Range(0, count).All(index =>
                    lines.Any(line => line.Contains("batch-" + index.ToString("000") + ".txt", StringComparison.Ordinal))),
                    "metadata batching retains a concrete CLI comparison record for EVERY approved file");
                Require(retainedPair.InitialReviewApproved == true, "successful metadata batch completes first-sync approval");
            }
            else
            {
                var completedPrefix = mode == "stream-cancellation" ? 16 : 0;
                Require(exit != 0 && batchRemote.DownloadCount == completedPrefix &&
                    Directory.EnumerateFiles(batchRoot, "batch-*.txt").Count() == completedPrefix,
                    mode + ": preparation failure/cancellation drains writes and preserves only the completed sealed prefix");
                Require(retainedPair.InitialReviewApproved == false, mode + ": failed/cancelled preparation never silently approves first sync");
                await database.VerifyHealthAsync();
                Require(true, mode + ": real authority database remains healthy after the metadata queue drains");
            }
            if (mode == "later-commit-refusal")
            {
                Require(laterFaultInjected && firstCommit == 2 && prefixAtFault.Length is > 0 and < count &&
                        prefixAtFault.Select(item => item.Group).Distinct().Count() == 1 &&
                        prefixAtFault.All(item => item.GroupState == "planned") &&
                        stderr.ToString().Contains(laterCommitFailure, StringComparison.Ordinal),
                    "AUD02: second actual planning commit fails after an earlier committed unsealed prefix, not at startup or first commit: " + stderr);
                var failedState = await ReadBatchStateAsync();
                var prefixIds = prefixAtFault.Select(item => item.Id).ToArray();
                var prefixSnapshots = await database.LoadOperationsAsync(prefixIds);
                Require(failedState.Plans.SequenceEqual(prefixAtFault) &&
                        failedState.Attempts == 0 && failedState.Receipts == 0 && failedState.Leases == 0 &&
                        prefixSnapshots.All(item => item.Status == SyncAuthorityOperationStatus.Ready &&
                            item.Kind == SyncAuthorityOperationKind.CreateLocal && item.Attempts.Count == 0 &&
                            item.CurrentLeaseId is null && item.Allocations.All(allocation =>
                                allocation.Status == SyncAuthorityAllocationStatus.Authorized)) && NoBatchEffects(),
                    "AUD02: failed later transaction rolls back completely; exact earlier ready prefix remains with no attempts, receipts, operation leases or file effects");
                Require(!batchHost.HasActiveRun && batchHost.ActiveRun is null &&
                        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest is null,
                    "AUD02: failed planning run and queue are drained and the input fault is cleared before reopening");
                prefixHost!.Dispose();
                var reopenedDatabase = new SyncAuthorityDatabase(database.DatabasePath);
                using var reopenedHost = new SyncAuthorityRuntimeHost(authority, http,
                    new SyncPairStore(authority), new SyncRuntimeStateStore(authority),
                    new DropboxConnectionStore(authority), new FixtureCredentialStore(), reopenedDatabase,
                    RetainedRemote);
                var reopenedPair = (await reopenedHost.LoadPairsAsync()).Pairs.Single(item => item.Id == batchPair.Id);
                Require(reopenedPair.InitialReviewApproved == false &&
                        (await reopenedDatabase.ListRecoveryOperationsForPairAsync(batchPair.Id)).Count == 0,
                    "AUD02: genuinely reopened Host/database retains pending review and excludes the unissued prefix from recovery admission");
                using var recoveryBound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var recovery = await reopenedHost.RecoverPairsAsync([reopenedPair], cancellationToken: recoveryBound.Token);
                var recoveredState = await ReadBatchStateAsync();
                var recoveredPrefix = await reopenedDatabase.LoadOperationsAsync(prefixIds);
                Require(!recovery.Stopped && recovery.Items.Count == 1 && recovery.Items[0].FailureType is null &&
                        recovery.Items[0].Result is { AppliedItems: 0, Blocked: 0, NeedsVerification: 0 } &&
                        recoveredState.Plans.SequenceEqual(prefixAtFault) && recoveredState.Attempts == 0 &&
                        recoveredState.Receipts == 0 && recoveredState.Leases == 0 && NoBatchEffects() &&
                        JsonSerializer.Serialize(recoveredPrefix) == JsonSerializer.Serialize(prefixSnapshots) &&
                        (await reopenedHost.LoadPairsAsync()).Pairs.Single(item => item.Id == batchPair.Id).InitialReviewApproved == false,
                    "AUD02: actual public recovery after reopen does not execute, retire or approve any unissued reviewed prefix or alter its immutable authority");
                await reopenedDatabase.VerifyHealthAsync();

                var reopenedOperations = new FixtureOperations(reopenedHost, authority);
                async Task<(int Exit, string Error)> ReviewedRetryAsync()
                {
                    using var retryOutput = new StringWriter();
                    using var retryError = new StringWriter();
                    var retryExit = await SyncCli.ExecuteAsync(["--cli", "first-sync", "apply", "--pair", batchPair.Id,
                        "--yes", "--json", "--choices", JsonSerializer.Serialize(choices), "--timeout-seconds", "60"],
                        retryOutput, retryError, () => reopenedOperations);
                    return (retryExit, retryError.ToString());
                }
                // elapsed-only instrumentation: capture the AUD02 retry aggregate and its SQLite phases,
                // Emit in finally even when the fixture's explicit CLI deadline expires. The measured
                // successful retry took 28.1 seconds; 60 seconds gives this correctness check headroom.
                // Production deadlines, inputs, authority and exact-effect/no-replay assertions are unchanged.
                (int Exit, string Error) retry = default;
                var retryTimings = new System.Collections.Concurrent.ConcurrentDictionary<string,
                    (long Count, double Milliseconds)>();
                var retryWatch = System.Diagnostics.Stopwatch.StartNew();
                var retryPreviousTiming = SyncAuthorityDatabase.TimingForTest;
                SyncAuthorityDatabase.TimingForTest = (phase, elapsed) => retryTimings.AddOrUpdate(phase,
                    (1, elapsed), (_, existing) => (existing.Count + 1, existing.Milliseconds + elapsed));
                try
                {
                    retry = await ReviewedRetryAsync();
                }
                finally
                {
                    retryWatch.Stop();
                    SyncAuthorityDatabase.TimingForTest = retryPreviousTiming;
                    Console.WriteLine("TIMING aud02 retry: " + JsonSerializer.Serialize(new
                    {
                        elapsedMilliseconds = retryWatch.Elapsed.TotalMilliseconds,
                        retryExit = retry.Exit,
                        timedOut = (retry.Error ?? "").Contains("deadline", StringComparison.Ordinal),
                        comparisonLines = (retry.Error ?? "").Split('\n').Count(line => line.Contains(
                            "Comparing this item's local and remote versions", StringComparison.Ordinal)),
                        lastProgress = (retry.Error ?? "").Split('\n').LastOrDefault(line => line.Contains(
                            "items checked", StringComparison.Ordinal)),
                        phases = retryTimings.ToDictionary(item => item.Key,
                            item => new { count = item.Value.Count, milliseconds = item.Value.Milliseconds })
                    }));
                }
                var retriedState = await ReadBatchStateAsync();
                var retriedPrefix = await reopenedDatabase.LoadOperationsAsync(prefixIds);
                var completedIds = retriedState.Plans.Select(item => item.Id).Except(prefixIds).ToArray();
                var completed = await reopenedDatabase.LoadOperationsAsync(completedIds);
                Console.WriteLine("AUD02 retry state: " + JsonSerializer.Serialize(new
                {
                    retryExit = retry.Exit,
                    downloadCount = batchRemote.DownloadCount,
                    otherEffectCounts = batchRemote.UploadCount + batchRemote.DeleteCount +
                        batchRemote.MoveCount + batchRemote.DirectoryCreateCount,
                    planCount = retriedState.Plans.Count(),
                    attempts = retriedState.Attempts,
                    successful = retriedState.SuccessfulOperations,
                    completedCount = completed.Count(item => item.Status == SyncAuthorityOperationStatus.Completed),
                    approved = (await reopenedHost.LoadPairsAsync()).Pairs.Single(item => item.Id == batchPair.Id)
                        .InitialReviewApproved,
                    prefixCount = prefixIds.Length
                }));
                Require(retry.Exit == 0 && batchRemote.DownloadCount == count &&
                        batchRemote.UploadCount + batchRemote.DeleteCount + batchRemote.MoveCount + batchRemote.DirectoryCreateCount == 0 &&
                        Enumerable.Range(0, count).All(index => File.ReadAllText(Path.Combine(batchRoot,
                            "batch-" + index.ToString("000") + ".txt")) == "exact reviewed payload " + index) &&
                        completed.Count == count && completed.All(item => item.Status == SyncAuthorityOperationStatus.Completed &&
                            item.Attempts.Count == 1 && item.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess) &&
                        retriedState.Attempts == count && retriedState.SuccessfulOperations == count &&
                        (await reopenedHost.LoadPairsAsync()).Pairs.Single(item => item.Id == batchPair.Id).InitialReviewApproved == true,
                    "AUD02: explicit real reviewed retry with the same saved choices completes every exact file once and saves approval: " + retry.Error);
                Require(retriedPrefix.All(item => item.Status == SyncAuthorityOperationStatus.Superseded &&
                            item.Attempts.Count == 0 && item.CurrentLeaseId is null) &&
                        JsonSerializer.Serialize(retriedPrefix.Select(item => item with { Status = SyncAuthorityOperationStatus.Ready })) ==
                        JsonSerializer.Serialize(prefixSnapshots),
                    "AUD02: only explicit reviewed retry retires the exact immutable unissued prefix; no old plan is dispatched");
                var repeated = await ReviewedRetryAsync();
                using var completedRecoveryBound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var completedRecovery = await reopenedHost.RecoverPairsAsync(
                    [(await reopenedHost.LoadPairsAsync()).Pairs.Single(item => item.Id == batchPair.Id)],
                    cancellationToken: completedRecoveryBound.Token);
                var repeatedState = await ReadBatchStateAsync();
                Require(repeated.Exit != 0 && !completedRecovery.Stopped &&
                        completedRecovery.Items.Single().FailureType is null && batchRemote.DownloadCount == count &&
                        repeatedState.Attempts == retriedState.Attempts && repeatedState.Receipts == retriedState.Receipts &&
                        repeatedState.Leases == retriedState.Leases &&
                        JsonSerializer.Serialize(await reopenedDatabase.LoadOperationsAsync(completedIds)) == JsonSerializer.Serialize(completed),
                    "AUD02: repeated reviewed apply is safely refused and subsequent public recovery does not replay any completed effect, attempt, receipt or operation lease");
                await reopenedDatabase.VerifyHealthAsync();
                Console.WriteLine("EVIDENCE AUD02 later planning failure/reopen/retry: " + JsonSerializer.Serialize(new
                { pairId = batchPair.Id, planningCommit = firstCommit,
                    prefixAtFault = prefixAtFault.Select(item => new { operationId = item.Id,
                        operationGroupId = item.Group, groupState = item.GroupState }), prefixIds, completedIds,
                    initialExit = exit, retryExit = retry.Exit, repeatedExit = repeated.Exit,
                    failedState.Attempts, failedState.Receipts, failedState.Leases,
                    successfulAttempts = retriedState.Attempts, successfulOperations = retriedState.SuccessfulOperations,
                    downloads = batchRemote.DownloadCount }));
            }
            if (mode == "stream-cancellation")
            {
                var interruptedRun = (await database.LoadRunHistoryAsync(30)).First(item =>
                    item.RequestJson.Contains("Reviewed first sync") && item.RequestJson.Contains(batchPair.Id)).Id;
                var prefixState = await ReadBatchStateAsync();
                var completed = (await database.LoadOperationsAsync(prefixState.Plans.Select(item => item.Id).ToArray()))
                    .Where(item => item.Status == SyncAuthorityOperationStatus.Completed).ToArray();
                Require(completed.Length == 16 && completed.All(item => item.Attempts.Count == 1),
                    "stream cancellation retains sixteen independently completed exact operations");
                prefixHost!.Dispose();
                var reopenedDatabase = new SyncAuthorityDatabase(database.DatabasePath);
                using var reopenedHost = new SyncAuthorityRuntimeHost(authority, http,
                    new SyncPairStore(authority), new SyncRuntimeStateStore(authority),
                    new DropboxConnectionStore(authority), new FixtureCredentialStore(), reopenedDatabase, RetainedRemote);
                var preview = await reopenedHost.LoadRunRetryAsync(interruptedRun, selectedPairId: batchPair.Id);
                Require(preview.Items.Count(item => item.State == "Completed") == 16 &&
                    preview.Items.Count(item => item.CanRetry) == count - 16,
                    "reopened streaming retry excludes every completed sibling and retains the unfinished approval");
                using var retryOutput = new StringWriter(); using var retryError = new StringWriter();
                var retryExit = await SyncCli.ExecuteAsync(["--cli", "run", "retry", "--run", interruptedRun,
                    "--pair", batchPair.Id, "--choice", "all", "--yes", "--json"], retryOutput, retryError,
                    () => new FixtureOperations(reopenedHost, authority));
                Require(retryExit == 0 && batchRemote.DownloadCount == count &&
                    Enumerable.Range(0, count).All(index => File.ReadAllText(Path.Combine(batchRoot,
                        "batch-" + index.ToString("000") + ".txt")) == "exact reviewed payload " + index),
                    "reopened real CLI completes only remaining batches with exact bytes: " + retryError);
                Require(JsonSerializer.Serialize(await reopenedDatabase.LoadOperationsAsync(
                        completed.Select(item => item.OperationId).ToArray())) == JsonSerializer.Serialize(completed),
                    "stream retry never alters or replays the completed prefix's attempts and immutable authority");
            }
            await host.SaveTransferSettingsAsync(priorTransferSettings);

            bool NoBatchEffects() => batchRemote.DownloadCount + batchRemote.UploadCount + batchRemote.DeleteCount +
                batchRemote.MoveCount + batchRemote.DirectoryCreateCount == 0 &&
                !Directory.EnumerateFiles(batchRoot, "batch-*.txt").Any() &&
                Enumerable.Range(0, count).All(index => batchRemote.ReadText("batch-" + index.ToString("000") + ".txt") ==
                    "exact reviewed payload " + index);

            async Task<((string Id, string Group, string GroupState)[] Plans, long Attempts, long Receipts,
                long Leases, long SuccessfulOperations)> ReadBatchStateAsync()
            {
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = database.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                        Pooling = false }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.Parameters.AddWithValue("$pair", batchPair.Id);
                command.CommandText = "SELECT o.operation_id,g.operation_group_id,g.status FROM operation o JOIN operation_group g ON g.operation_group_id=o.operation_group_id WHERE o.pair_id=$pair ORDER BY o.operation_id;";
                var plans = new List<(string, string, string)>();
                await using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) plans.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                command.CommandText = """
                    SELECT
                      (SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND o.kind<>'allocate_control'),
                      (SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND o.kind<>'allocate_control'),
                      (SELECT count(*) FROM lease l JOIN operation o ON o.operation_id=l.scope_id WHERE l.scope_kind='operation' AND o.pair_id=$pair AND o.kind<>'allocate_control'),
                      (SELECT count(DISTINCT o.operation_id) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND o.kind<>'allocate_control' AND o.status='completed' AND r.certainty='known_success');
                    """;
                await using var counts = await command.ExecuteReaderAsync();
                if (!await counts.ReadAsync()) throw new InvalidDataException("The AUD02 fixture state query returned no counts.");
                return (plans.ToArray(), counts.GetInt64(0), counts.GetInt64(1), counts.GetInt64(2), counts.GetInt64(3));
            }
        }


        // The optimization must retain the original decision meaning, not just return quickly.
        var allOperationIds = new List<string>();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                         new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                         { DataSource = database.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                             Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT operation_id FROM operation ORDER BY operation_id;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) allOperationIds.Add(reader.GetString(0));
        }
        var retryEvidence = await database.LoadRetryOperationEvidenceAsync(allOperationIds);
        Require(retryEvidence.Select(item => item.OperationId).SequenceEqual(allOperationIds),
            "batched retry evidence retains every requested operation in exact caller order");
        foreach (var item in retryEvidence)
        {
            var original = await database.LoadOperationAsync(item.OperationId);
            Require(item.Status == original.Status && item.PairId == original.PairId &&
                    item.Path == SyncAuthorityPairRunner.RecoveryDecisionPath(original) &&
                    item.Unissued == await database.IsUnissuedOperationAsync(item.OperationId, CancellationToken.None) &&
                    item.KnownNoEffectRefusal == await database.IsKnownNoEffectRefusalAsync(item.OperationId, CancellationToken.None),
                "batched identity, path and retry safety match the original real checks for " + item.OperationId);
        }
        var missingRejected = false;
        try { await database.LoadRetryOperationEvidenceAsync(["not-retained-fixture-operation"]); }
        catch (InvalidDataException) { missingRejected = true; }
        Require(missingRejected, "batched retry evidence fails closed for missing retained operations");

        // Keep the changed shared admission controller in this same focused real
        // first-sync proof. The resource suite reports its own named assertions;
        // the first-sync checks count below does not include those assertions.
        await AdaptiveResourceChecks.RunAsync(Path.Combine(root, "adaptive-resource"));

        // R697 item 2: the "clean disposal" phase. Host.Dispose is idempotent, and the pool close releases the
        // last shared connection so the WAL sidecars reflect a settled database rather than a live writer.
        host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var dbGrowthAfterCleanDisposal = DbSidecars(database.DatabasePath);
        static object DbSnapshot((long Db, long Wal, long Shm) sides) =>
            new { db = sides.Db, wal = sides.Wal, shm = sides.Shm };
        static object DbDelta((long Db, long Wal, long Shm) a, (long Db, long Wal, long Shm) b) =>
            new { db = b.Db - a.Db, wal = b.Wal - a.Wal, shm = b.Shm - a.Shm };
        Console.WriteLine("{\"db_growth\":" + JsonSerializer.Serialize(new
        {
            database = database.DatabasePath,
            missing_sidecar_encoding = "-1 means the file is genuinely absent",
            before_prepare = DbSnapshot(dbGrowthBeforePrepare),
            after_prepare = DbSnapshot(dbGrowthAfterPrepare),
            after_apply_open = DbSnapshot(dbGrowthAfterApplyOpen),
            after_clean_disposal = DbSnapshot(dbGrowthAfterCleanDisposal),
            delta_prepare = DbDelta(dbGrowthBeforePrepare, dbGrowthAfterPrepare),
            delta_apply = DbDelta(dbGrowthAfterPrepare, dbGrowthAfterApplyOpen),
            delta_disposal_cumulative = DbDelta(dbGrowthAfterApplyOpen, dbGrowthAfterCleanDisposal),
            delta_disposal_note = "cumulative: later fixture phases (cold verify, batch reads, retry evidence, AUD02, adaptive-resource) also write this database before disposal; the isolated apply delta is in the earlier db_growth_apply line"
        }) + "}");

        Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + checks +
            ",\"note\":\"first-sync checks plus separately reported adaptive-resource suite; existing fake remote, no live provider or credential\"}");
    }

    /// <summary>
    /// R697 item 2: the exact on-disk bytes of an authority database and its sidecars. A missing file is
    /// reported as -1 so a genuinely absent -wal/-shm is recorded rather than silently shown as zero.
    /// </summary>
    internal static (long Db, long Wal, long Shm) DbSidecars(string databasePath)
    {
        long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : -1;
        return (Size(databasePath), Size(databasePath + "-wal"), Size(databasePath + "-shm"));
    }

    private static void CheckAncestorPathIndex()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS " + message);
        }
        var enumerations = 0;
        IEnumerable<string> Ancestors()
        {
            if (++enumerations != 1) throw new InvalidOperationException("Ancestor input was rescanned.");
            yield return "Excluded/Child";
            yield return "provider:name";
            for (var i = 0; i < 100_000; i++) yield return "other/" + i;
        }
        var index = new SyncAuthorityReconciliationPlanner.AncestorPathIndex(Ancestors());
        Require(index.Contains("excluded/CHILD") && index.Contains("Excluded/Child/deep/file.txt") &&
                !index.Contains("Excluded/Child-copy/file.txt") && !index.Contains("Excluded") &&
                !index.Contains("Excluded/Child", strict: true) &&
                index.Contains("Excluded/Child/deep/file.txt", strict: true) &&
                index.Contains("provider:name/file.txt") && !index.Contains(null),
            "indexed ancestor fences preserve case-insensitive inclusive/strict boundaries and provider-only names");
        for (var i = 0; i < 100_000; i++)
            if (index.Contains("unrelated/" + i + "/file.txt"))
                throw new InvalidOperationException("An unrelated path inherited an exclusion.");
        Require(enumerations == 1,
            "100000 inventory lookups never enumerate 100002 ancestors again");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var rejected = false;
        try { _ = new SyncAuthorityReconciliationPlanner.AncestorPathIndex(["a"], cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected, "ancestor-index preparation respects cancellation before processing an entry");
    }

    private sealed class CountingPathSet(IReadOnlySet<string> paths) : IReadOnlySet<string>
    {
        internal int Enumerations { get; private set; }
        public int Count => paths.Count;
        public bool Contains(string item) => paths.Contains(item);
        public bool IsProperSubsetOf(IEnumerable<string> other) => paths.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<string> other) => paths.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<string> other) => paths.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => paths.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<string> other) => paths.Overlaps(other);
        public bool SetEquals(IEnumerable<string> other) => paths.SetEquals(other);
        public IEnumerator<string> GetEnumerator()
        {
            Enumerations++;
            return paths.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelOnEnumeration<T>(IReadOnlyList<T> entries,
        CancellationTokenSource cancellation, int targetEnumeration) : IReadOnlyList<T>
    {
        private int _enumerations;
        public int Count => entries.Count;
        public T this[int index] => entries[index];
        public IEnumerator<T> GetEnumerator()
        {
            if (++_enumerations == targetEnumeration) cancellation.Cancel();
            return entries.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class HandoffWriter(Action<string> onLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null) onLine(value);
        }
    }

    internal sealed class FixtureOperations(SyncAuthorityRuntimeHost host, PilotStateAuthority authority)
        : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => authority;
        public override string Mode => "first-sync-effect-fixture";
    }

    internal sealed class FixtureCredentialStore : IDropboxRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => this;
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fixture-refresh-token");
        public Task WriteAsync(string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("First-sync effect checks must never contact a provider: " +
                request.RequestUri);
    }
}
