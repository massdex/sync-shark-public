using System.Text.Json;
using Microsoft.Data.Sqlite;
using Massdex.SyncShark;

// R473-2/R473-3/ENG4 focused contract. It drives the real runtime host and the real authority over an isolated
// temporary root and the existing fake remote, and asserts that a reviewed first-sync call with NO presentation
// callbacks still retains its real stages and its typed terminal outcome, that the command only becomes terminal
// once the real final outcome is known, that a failed follow-up check is retained truthfully, and that no
// completed effect is ever replayed.
internal static class FirstSyncRetentionChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "upload.txt"), "retained upload");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "skip-local.txt"), "retained skip");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "same.txt"), "identical both sides");
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(Binding());
        var remote = new FakeSyncRemoteStore();
        remote.Put("download.txt", "retained download");
        remote.Put("same.txt", "identical both sides");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        using var http = new HttpClient(new DropboxRecoveryBrowserContractHandler());
        var rejectScanPreparation = false;
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) =>
                rejectScanPreparation ? throw new IOException("Controlled scan preparation failure",
                    new HttpRequestException("Provider metadata request failed at https://fixture.invalid/list?access_token=scan-fixture-secret&state=scan-fixture-state",
                        null, System.Net.HttpStatusCode.ServiceUnavailable)) : remote);
        var binding = await host.BindPairAsync(new SyncPairBindingRequest(null, "Runtime first sync", localRoot,
            new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
            Enabled: true, Priority: 50, PropagateDeletions: true, FilesOnDemandEnabled: false,
            SelectionDefaultIncluded: true, RecoveryRetentionDays: 180));
        var pair = binding.Pair;
        rejectScanPreparation = true;
        await CliChecks.CheckRetainedScanDispatchAsync(host, appLock.StateAuthority, pair.Id, expectSuccess: false);
        var scanFailure = (await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pair.Id);
        Require(scanFailure.Code == "first_sync_scan_failed" && scanFailure.Phase == "scan" &&
                (await authority.LoadRunHistoryAsync(20, 0)).Single(item => item.Id == scanFailure.CommandId).Status == "blocked",
            "A failed Scan Again retains a typed problem and a terminal command retrievable from Runs.");
        Require(scanFailure.Message.Contains("Controlled scan preparation failure") &&
                scanFailure.Message.Contains("IOException") && scanFailure.Message.Contains("HttpRequestException (HTTP 503)") &&
                scanFailure.Message.Contains("The HTTP request failed.") && scanFailure.Message.Contains("[0x") &&
                !scanFailure.Message.Contains("https://fixture.invalid") &&
                !scanFailure.Message.Contains("Provider metadata request failed") &&
                !scanFailure.Message.Contains("scan-fixture-secret") && !scanFailure.Message.Contains("scan-fixture-state"),
            "The retained scan problem preserves the application cause, nested HTTP type/status and diagnostic codes while withholding untrusted transport message and credentials.");
        var scanRetry = await host.LoadRunRetryAsync(scanFailure.CommandId);
        Require(scanRetry.PairId == pair.Id && scanRetry.Items.Count == 0 &&
                scanRetry.UnavailableReason?.Contains("only scanned") == true &&
                scanRetry.UnavailableReason.Contains("Scan Again") && !scanRetry.UnavailableReason.Contains("older run"),
            "A failed scan identifies its pair and offers Scan Again, never a fictional older-run explanation or file retry.");
        Require((await host.LoadRunRetryAsync(scanFailure.CommandId, selectedPairId: "unrelated-pair"))
                .UnavailableReason?.Contains("does not belong") == true,
            "A scan-only run cannot borrow an unrelated pair identity for retry navigation.");
        await ThrowsAsync<InvalidOperationException>(() => host.RetryRunAsync(scanFailure.CommandId, null, "failed"));
        Require(remote.UploadCount == 0 && remote.DownloadCount == 0,
            "The real retry entry point refuses a scan-only run without any provider file effect.");
        var scanEvents = await authority.LoadActivityProjectionPageAsync(50, 0, null, null, null, pair.Id,
            null, default, null, new SyncActivityQuery(RunId: scanFailure.CommandId));
        Require(scanEvents.Any(item => item.Severity == "Error" && item.Reason.Contains("No file actions were started")),
            "The failed scan is retained in Activity with its exact run and no false file-effect claim.");
        Require(scanEvents.Any(item => item.Severity == "Error" &&
                item.Reason.Contains("Controlled scan preparation failure") && item.Reason.Contains("HTTP 503")) &&
                scanEvents.All(item => !item.Reason.Contains("scan-fixture-secret")),
            "The GUI/CLI retained activity projection carries the exact sanitized scan cause, not only the transient exception.");
        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote))
            Require((await restarted.LoadRetainedFirstSyncProblemsAsync()).Single().CommandId == scanFailure.CommandId,
                "The scan failure survives loading a fresh runtime host.");
        rejectScanPreparation = false;
        using (var scanCancellation = new CancellationTokenSource())
        {
            SyncComparisonProgress? lastReported = null;
            await ThrowsAsync<OperationCanceledException>(() => host.RefreshBootstrapInventoryAsync(pair.Id,
                scanCancellation.Token, progress =>
                {
                    if (progress.Comparison is { } observed)
                    {
                        lastReported = observed;
                        scanCancellation.Cancel();
                    }
                    return Task.CompletedTask;
                }));
            var interrupted = (await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pair.Id);
            Require(lastReported is not null && interrupted.Code == "first_sync_scan_stopped" &&
                    interrupted.Message.Contains("cancelled or reached its configured time limit") &&
                    interrupted.Message.Contains(lastReported.Phase) &&
                    interrupted.Message.Contains("Last reported work:") &&
                    interrupted.Message.Contains("No file actions were started") &&
                    (string.IsNullOrWhiteSpace(lastReported.RelativePath) ||
                        interrupted.Message.Contains(lastReported.RelativePath)) &&
                    interrupted.NextAction.Contains("operation log"),
                "Real scan cancellation retains the phase, last reported target/work, truthful cancellation-or-deadline cause and useful recovery route.");
            var interruptedLog = await authority.LoadActivityProjectionPageAsync(50, pairId: pair.Id,
                query: new(RunId: interrupted.CommandId, DetailedLog: true));
            Require(interruptedLog.Any(item => item.Reason.Contains("Last reported work:")) &&
                    remote.UploadCount == 0 && remote.DownloadCount == 0,
                "The cancelled real scan has a detailed run-linked terminal record and no file effect.");
            var explicitStop = SyncAuthorityRuntimeHost.DescribeFirstSyncScanInterruption(lastReported, true);
            Require(explicitStop.Contains("stopped by request") && !explicitStop.Contains("configured time limit"),
                "A known stop request is not falsely reported as a deadline.");
        }
        await CliChecks.CheckRetainedScanDispatchAsync(host, appLock.StateAuthority, pair.Id, expectSuccess: true);
        var successfulScan = (await authority.LoadRunHistoryAsync(20, 0)).First(item =>
            item.ResultJson?.Contains("\"firstSyncScanComplete\":true") == true);
        var successfulScanRetry = await host.LoadRunRetryAsync(successfulScan.Id);
        Require(successfulScanRetry.PairId == pair.Id && successfulScanRetry.Items.Count == 0 &&
                successfulScanRetry.UnavailableReason?.Contains("did not approve or start any file actions") == true,
            "Successful inventory scans also provide no implicit file-effect retry authority.");
        await authority.CreateCommandRequestAsync("legacy-no-file-approval", "run_pair", pair.Id, null, null, "{}");
        await authority.TransitionCommandRequestAsync("legacy-no-file-approval", "accepted");
        await authority.TransitionCommandRequestAsync("legacy-no-file-approval", "executing");
        await authority.TransitionCommandRequestAsync("legacy-no-file-approval", "completed", "{}");
        var legacyRetry = await host.LoadRunRetryAsync("legacy-no-file-approval");
        Require(legacyRetry.PairId is null && legacyRetry.Items.Count == 0 &&
                legacyRetry.UnavailableReason?.Contains("No approval is inferred") == true &&
                !legacyRetry.UnavailableReason.Contains("older run"),
            "Unknown run provenance remains safely non-retryable without guessing its age or authority.");
        Require(!(await host.LoadRetainedFirstSyncProblemsAsync()).Any(item => item.PairId == pair.Id) &&
                (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id).InitialReviewApproved == false &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "A successful retry resolves only the scan problem, without approving first sync or copying files.");
        var preparationMessages = new List<SyncComparisonProgress>();
        var preparedReview = await host.PrepareBootstrapInventoryAsync(pair.Id, onProgress: progress =>
        {
            if (progress.Comparison?.Phase == "Checking endpoint bindings")
                preparationMessages.Add(progress.Comparison);
            return Task.CompletedTask;
        });
        Require(preparedReview.InventoryComplete && !preparedReview.HasMore &&
                preparationMessages.Count(item => item.Detail.StartsWith("Starting: ", StringComparison.Ordinal)) == 13 &&
                preparationMessages.Count(item => item.Detail.StartsWith("Completed: ", StringComparison.Ordinal)) == 13 &&
                preparationMessages.All(item => item.RelativePath == pair.Name &&
                    item.ReportedUtc >= item.StartedUtc) &&
                preparationMessages.Any(item => item.Detail.Contains("running host's initialized installation identity", StringComparison.Ordinal)) &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "Actual Host preparation exposes each existing warm binding/registration operation with ordered starts/completions and no file effects.");
        var diagnosticSinkFailed = await host.PrepareBootstrapInventoryAsync(pair.Id,
            onProgress: _ => throw new IOException("Controlled diagnostic presentation failure"));
        Require(diagnosticSinkFailed.InventoryComplete && !diagnosticSinkFailed.HasMore &&
                (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id).InitialReviewApproved == false &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "A failed presentation sink cannot change the real preparation result, approve first sync or perform effects.");
        await host.RunPairsAsync([pair], "Retention inventory");
        var page = await host.LoadBootstrapReviewPageAsync(pair.Id, 200, 0);
        var groups = page.Items.ToDictionary(item => item.RelativePath, item => item.Group,
            StringComparer.OrdinalIgnoreCase);
        Require(groups["same.txt"] == "Identical" && groups["upload.txt"] == "Local only" &&
                groups["download.txt"] == "Dropbox only",
            "The retention fixture inventory did not keep its identical and one-sided groups.");
        var plan = new SyncBootstrapActionPlan(pair.Id, page.LocalScanGenerationId!,
            page.DropboxScanGenerationId!,
            [
                new SyncBootstrapActionChoice("upload.txt", "Local only", "Upload"),
                new SyncBootstrapActionChoice("skip-local.txt", "Local only", "Skip for now"),
                new SyncBootstrapActionChoice("download.txt", "Dropbox only", "Download")
            ]);
        // R473-2: a direct caller with NO callbacks still gets the host's real stage in the live run snapshot.
        var observedStates = new List<string>();
        string? observedRunId = null;
        string? observedPairId = null;
        var watching = true;
        var watcher = Task.Run(async () =>
        {
            while (watching)
            {
                if (host.ActiveRun is { } snapshot)
                {
                    observedRunId ??= snapshot.Id;
                    if (snapshot.Progress is { } progress)
                    {
                        observedPairId ??= progress.Pair.Id;
                        var state = progress.Action is { } action ? action.Stage + " " + action.Action
                            : progress.Comparison is { } comparison ? comparison.Phase
                            : progress.State;
                        if (!observedStates.Contains(state)) observedStates.Add(state);
                    }
                }
                await Task.Delay(1);
            }
        });
        var applied = await host.RunBootstrapActionPlanAsync(plan);
        watching = false;
        watcher.Wait(TimeSpan.FromSeconds(5));
        Require(observedPairId == pair.Id && observedRunId is not null && observedStates.Count > 0,
            "The authoritative host retained no stage for a direct caller with no presentation callbacks: " +
            string.Join(", ", observedStates));
        Require(applied.AppliedItems == 2 && applied.SkippedItems == 1 && !applied.FirstSyncComplete,
            "The reviewed run did not apply exactly the reviewed one-sided choices.");
        Require(remote.ReadText("upload.txt") == "retained upload" &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")) == "retained download" &&
                !remote.Exists("skip-local.txt") &&
                remote.ReadText("same.txt") == "identical both sides" &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "same.txt")) == "identical both sides",
            "The reviewed run changed a skipped or identical item, or missed a reviewed one.");
        var problem = (await host.LoadRetainedFirstSyncProblemsAsync()).SingleOrDefault(item => item.PairId == pair.Id);
        Require(problem is not null && problem.Code == "first_sync_incomplete" &&
                problem.RelativePath == "skip-local.txt" &&
                problem.CommandId.StartsWith("command-", StringComparison.Ordinal),
            "The retained first-sync problem did not name the exact pending path and command.");
        Require(problem is not null && !problem.Message.Contains("Exception", StringComparison.Ordinal) &&
                !problem.Message.Contains("   at ", StringComparison.Ordinal),
            "The retained first-sync problem carried exception text instead of product-owned wording.");
        var commandId = problem!.CommandId;
        var retention = await authority.LoadActivityProjectionPageAsync(50, 0, null, "Needs attention", null,
            pair.Id, null, default, null, new SyncActivityQuery(RunId: commandId));
        Require(retention.Any(item => item.PairId == pair.Id && item.Severity == "Warning" &&
                item.Reason.Contains("First sync is not complete", StringComparison.Ordinal)),
            "The retained activity did not keep this run's warning presentation event: " +
            string.Join(" | ", retention.Select(item => item.Severity + ":" + item.Reason)));
        var retainedRun = (await authority.LoadRunHistoryAsync(20, 0)).SingleOrDefault(item => item.Id == commandId);
        Require(retainedRun is not null && retainedRun.Status == "completed" &&
                retainedRun.ResultJson is not null &&
                retainedRun.ResultJson.Contains("\"firstSyncComplete\":false", StringComparison.Ordinal) &&
                retainedRun.ResultJson.Contains("first_sync_incomplete", StringComparison.Ordinal),
            "The retained command was not terminal only after the real final outcome: " + retainedRun?.ResultJson);
        var effectsBefore = (remote.UploadCount, remote.DownloadCount);
        var replayed = await host.RunBootstrapActionPlanAsync(plan);
        Require(replayed.AppliedItems == 0 && !replayed.FirstSyncComplete,
            "Reapplying unchanged consent performs no completed effect again and preserves the skipped path.");
        Require(effectsBefore == (remote.UploadCount, remote.DownloadCount),
            "Replaying a captured plan repeated a reviewed effect.");
        // New per-item consent permits an unchanged repeat. A genuinely invalid action must
        // still be rejected and retained, independently of that non-mutating repeat.
        var invalid = plan with { Choices = plan.Choices.Select(choice => choice.RelativePath == "upload.txt"
            ? choice with { Action = "Delete without approval" } : choice).ToArray() };
        await ThrowsAsync<InvalidDataException>(() => host.RunBootstrapActionPlanAsync(invalid));
        Require(effectsBefore == (remote.UploadCount, remote.DownloadCount),
            "A refused action cannot replay successful effects or mutate the skipped path.");
        var refused = (await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pair.Id);
        Require(refused.CommandId != commandId &&
                refused.Code is "first_sync_plan_rejected" or "first_sync_refused" &&
                (await authority.LoadRunHistoryAsync(20, 0)).Any(item => item.Id == commandId),
            "The refused replay did not retain its own typed reason while the earlier history was preserved.");

        rejectScanPreparation = true;
        await ThrowsAsync<IOException>(() => host.RefreshBootstrapInventoryAsync(pair.Id));
        Require((await host.LoadRetainedFirstSyncProblemsAsync()).Count(item => item.PairId == pair.Id) == 2,
            "A scan failure does not overwrite the separate unresolved reviewed-apply problem.");
        rejectScanPreparation = false;
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        Require((await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pair.Id).CommandId == refused.CommandId &&
                effectsBefore == (remote.UploadCount, remote.DownloadCount),
            "Successful Scan Again preserves the earlier apply problem and performs no reviewed file effects.");

        // The current problem is a per-pair projection, not a page of recent global first-sync runs.
        await authority.RegisterPairAsync("other-retention-pair", 1);
        for (var index = 0; index < 210; index++)
        {
            var otherId = "other-first-sync-" + index;
            await authority.CreateCommandRequestAsync(otherId, "run_pair", "other-retention-pair", null, null, "{}");
            await authority.TransitionCommandRequestAsync(otherId, "accepted");
            await authority.TransitionCommandRequestAsync(otherId, "executing");
            await authority.TransitionCommandRequestAsync(otherId, "completed",
                "{\"pairId\":\"other-retention-pair\",\"firstSyncComplete\":true}");
        }
        // A historical malformed result must not make every pair's projection unreadable.
        await using (var fixtureConnection = new SqliteConnection("Data Source=" + authority.DatabasePath))
        {
            await fixtureConnection.OpenAsync();
            await using var corruptLegacy = fixtureConnection.CreateCommand();
            corruptLegacy.CommandText = "UPDATE command_request SET result_json='{' WHERE command_request_id='other-first-sync-209';";
            var invalidJsonRejected = false;
            try { await corruptLegacy.ExecuteNonQueryAsync(); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { invalidJsonRejected = true; }
            Require(invalidJsonRejected, "The authority rejects malformed JSON at its real storage boundary.");
            corruptLegacy.CommandText = "UPDATE command_request SET result_json='[]' WHERE command_request_id='other-first-sync-209';";
            await corruptLegacy.ExecuteNonQueryAsync();
        }
        using var reloadedHost = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
        var stillVisible = (await reloadedHost.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pair.Id);
        Require(stillVisible.CommandId == refused.CommandId && stillVisible.Phase == refused.Phase &&
                stillVisible.RelativePath == refused.RelativePath && stillVisible.EndpointContext is not null &&
                JsonSerializer.Serialize(stillVisible.EndpointContext) == JsonSerializer.Serialize(refused.EndpointContext),
            "A fresh host preserves this pair's exact problem and captured endpoints after more than200 other outcomes and a legacy non-object result.");
        await authority.CreateCommandRequestAsync("retention-resolved", "run_pair", pair.Id, null, null, "{}");
        await authority.TransitionCommandRequestAsync("retention-resolved", "accepted");
        await authority.TransitionCommandRequestAsync("retention-resolved", "executing");
        await authority.TransitionCommandRequestAsync("retention-resolved", "completed",
            JsonSerializer.Serialize(new { pairId = pair.Id, firstSyncComplete = true }));
        Require(!(await host.LoadRetainedFirstSyncProblemsAsync()).Any(item => item.PairId == pair.Id),
            "A newer success for the same pair resolves its current problem without deleting the earlier outcome.");

        // R473-3: a failed follow-up check after a successful bounded action retains a truthful non-success
        // outcome, and the completed effect is never replayed.
        var retryPage = await host.LoadBootstrapReviewPageAsync(pair.Id, 200, 0);
        var retryPlan = new SyncBootstrapActionPlan(pair.Id, retryPage.LocalScanGenerationId!,
            retryPage.DropboxScanGenerationId!,
            [new SyncBootstrapActionChoice("skip-local.txt", "Local only", "Upload")]);
        var uploadsBefore = remote.UploadCount;
        // A deterministic uncertain reviewed effect: the fixture applies the upload and then reports an unknown
        // result, so the terminal outcome must be a truthfully retained non-success with a next action, and the
        // completed effect must never be replayed.
        remote.InterruptNextUploadAfterEffect = true;
        var uncertain = await host.RunBootstrapActionPlanAsync(retryPlan);
        Require(uncertain.Result.NeedsVerification == 1 && !uncertain.FirstSyncComplete,
            "An uncertain reviewed effect returns a retained non-success outcome instead of an unhandled UI exception.");
        remote.InterruptNextUploadAfterEffect = false;
        Require(remote.ReadText("skip-local.txt") == "retained skip" && remote.UploadCount == uploadsBefore + 1,
            "The uncertain reviewed effect was not retained exactly once.");
        var followUp = (await host.LoadRetainedFirstSyncProblemsAsync())
            .SingleOrDefault(item => item.PairId == pair.Id);
        Require(followUp is { Code: "first_sync_needs_attention", Phase: "verification", RelativePath: "skip-local.txt" } &&
                followUp.NextAction.Contains("Verify outcomes", StringComparison.Ordinal) &&
                !followUp.Message.Contains("0 item(s) still need review", StringComparison.Ordinal) &&
                (await authority.LoadRunHistoryAsync(20, 0))
                    .Single(item => item.Id == followUp.CommandId).ResultJson!.Contains("\"needsVerification\":1", StringComparison.Ordinal),
            "An uncertain reviewed effect did not retain its own truthful non-success outcome: " + followUp?.Code);
        Require(remote.UploadCount == uploadsBefore + 1 && remote.ReadText("skip-local.txt") == "retained skip",
            "The uncertain reviewed effect was replayed or lost its completed effect.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            caseId = "TC-SS-FIRST-SYNC-RETENTION-TERMINAL",
            retainedStages = observedStates.Count,
            typedProblem = problem.Code,
            refusedReplay = refused.Code,
            followUpProblem = followUp!.Code
        }));
        await FiniteReviewFinalizationAsync(Path.Combine(root, "finite-review"));
        await CommandProblemRetentionAsync(Path.Combine(root, "command-problems"));
    }

    private static async Task CommandProblemRetentionAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var pairStore = new SyncPairStore(instance.StateAuthority);
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(Binding());
        var remote = new FakeSyncRemoteStore();
        using var http = new HttpClient(new DropboxRecoveryBrowserContractHandler());
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        var rejectPairPreparation = false;
        const string pairFailureCause = "Controlled pair preparation failure";
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairStore,
            new SyncRuntimeStateStore(instance.StateAuthority), connections,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database,
            (_, _) => rejectPairPreparation ? throw new IOException(pairFailureCause) : remote);
        var pair = (await host.BindPairAsync(new SyncPairBindingRequest(null, "Command problem", local,
            new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
            Enabled: true, Priority: 50, PropagateDeletions: false, FilesOnDemandEnabled: false,
            SelectionDefaultIncluded: true, RecoveryRetentionDays: 180))).Pair;
        var unscanned = await host.LoadCompleteBootstrapReviewAsync(pair.Id);
        Require(!unscanned.InventoryComplete && unscanned.LocalScanGenerationId is null &&
                unscanned.DropboxScanGenerationId is null,
            "Empty folders without two completed scans are still unreviewed, not implicit approval.");
        var review = await host.PrepareBootstrapInventoryAsync(pair.Id);
        Require(review.InventoryComplete && review.TotalItems == 0 && review.Items.Count == 0 &&
                !string.IsNullOrWhiteSpace(review.LocalScanGenerationId) &&
                !string.IsNullOrWhiteSpace(review.DropboxScanGenerationId),
            "Two genuinely empty complete inventories retain their exact scan identities for first-sync confirmation.");
        var approved = await host.RunBootstrapActionPlanAsync(new(pair.Id,
            review.LocalScanGenerationId!, review.DropboxScanGenerationId!, []));
        pair = approved.Pair;
        Require(approved.FirstSyncComplete && pair.InitialReviewApproved == true &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "Empty first-sync confirmation commits approval with no file effects rather than asking for another scan.");
        await CliChecks.CheckApprovedScanDispatchAsync(host, instance.StateAuthority, pair.Id, expectSuccess: true);
        var approvedScan = (await database.LoadRunHistoryAsync(20)).First(run =>
            run.ResultJson?.Contains("\"inventoryScanComplete\":true", StringComparison.Ordinal) == true);
        Require(approvedScan.ResultJson?.Contains("firstSyncScanComplete", StringComparison.Ordinal) == false &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "Mature inventory scan is retained as an effect-free scan, not another first-sync completion or file transfer.");
        rejectPairPreparation = true;
        await CliChecks.CheckApprovedScanDispatchAsync(host, instance.StateAuthority, pair.Id, expectSuccess: false);
        var matureScanProblem = (await host.LoadRetainedCommandProblemsAsync()).Single();
        Require(matureScanProblem.Code == "pair_scan_failed" && matureScanProblem.PairId == pair.Id &&
                matureScanProblem.Message.Contains(pairFailureCause, StringComparison.Ordinal) &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "Approved scan failure retains its actual cause and run in ordinary Problems without file effects or repeated approval.");
        rejectPairPreparation = false;
        const string cause = "Controlled accepted command failure before dispatch";
        await ThrowsAsync<IOException>(() => host.RunPairsAsync([pair], "Command retention failure",
            admissionProgress: message =>
            {
                if (message.StartsWith("Admission: command ", StringComparison.Ordinal))
                    throw new IOException(cause);
            }));
        var problem = (await host.LoadRetainedCommandProblemsAsync()).Single();
        Require(problem.PairId == pair.Id && problem.Message.Contains(cause, StringComparison.Ordinal) &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "A real accepted command failure retains its exact target/cause with no dispatched file effect.");
        var failed = (await database.LoadRunHistoryAsync(20)).Single(run => run.Id == problem.CommandId);
        Require(failed.Status == "blocked" && failed.CompletedUtc is not null,
            "The command problem references the actual durable blocked run, not an invented file failure.");
        await database.RegisterPairAsync("unrelated-pair", 1);
        for (var index = 0; index < 40; index++)
            await RecordOutcomeAsync("unrelated-" + index, "run_pair", "unrelated-pair", "{}", "{}");
        Require((await host.LoadRetainedCommandProblemsAsync()).Single().CommandId == problem.CommandId,
            "Unrelated retained history beyond the visible page cannot evict an unresolved command failure.");
        using (var reopened = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(instance.StateAuthority), connections,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database, (_, _) => remote))
        {
            Require((await reopened.LoadRetainedCommandProblemsAsync()).Single().CommandId == problem.CommandId,
                "The exact accepted-command cause survives reading a fresh runtime host.");
            await CliChecks.CheckRetainedCommandAttentionDispatchAsync(reopened, instance.StateAuthority,
                pair.Id, problem.CommandId, cause, expected: true);
        }
        // Controlled input enters the real database query. The decision about
        // whether an outcome can resolve the issue remains production code.
        var targetRequest = JsonSerializer.Serialize(new { pairIds = new[] { pair.Id }, recoveryOnly = false });
        await RecordOutcomeAsync("stopped-after-error", "run_pair", pair.Id, targetRequest,
            JsonSerializer.Serialize(new { Stopped = true, pairs = new[] {
                new { pairId = pair.Id, stopped = true, failureType = (string?)null, failureMessage = "",
                    failed = 0, blocked = 0, conflicts = 0, needsVerification = 0, notAttempted = 0 } } }));
        await RecordOutcomeAsync("recovery-after-error", "reconcile", pair.Id,
            JsonSerializer.Serialize(new { pairIds = new[] { pair.Id }, recoveryOnly = true }),
            "{\"Stopped\":false,\"pairs\":[]}");
        await RecordOutcomeAsync("malformed-pair-after-error", "run_pair", pair.Id, targetRequest,
            "{\"Stopped\":false,\"pairs\":[\"not an outcome\",null,17]}");
        Require((await host.LoadRetainedCommandProblemsAsync()).Single().CommandId == problem.CommandId,
            "Stopped, recovery-only and malformed/unknown pair results cannot manufacture a successful resolution.");
        var completed = await host.RunPairsAsync([pair], "Resolve retained command problem by real normal check");
        Require(!completed.Stopped && completed.Items.All(item => item.FailureMessage is null) &&
                !(await host.LoadRetainedCommandProblemsAsync()).Any() && remote.UploadCount == 0 &&
                remote.DownloadCount == 0,
            "A real successful ordinary check resolves the current command issue without any file replay.");
        await CliChecks.CheckRetainedCommandAttentionDispatchAsync(host, instance.StateAuthority,
            pair.Id, problem.CommandId, cause, expected: false);
        var resolution = await host.LoadRetainedCommandProblemProjectionAsync();
        Require(resolution.Problems.Count == 0 &&
                resolution.SuccessfulRunStartedUtcByPair.TryGetValue(pair.Id, out var successStarted) &&
                successStarted > failed.CompletedUtc,
            "The shared projection exposes only the actual later successful target run, never a UI readback timestamp.");
        var preserved = (await database.LoadRunHistoryAsync(100)).Single(run => run.Id == problem.CommandId);
        Require(preserved == failed,
            "Resolving the current presentation never rewrites the failed command's immutable history.");
        rejectPairPreparation = true;
        var partial = await host.RunPairsAsync([pair], "Retain a pair preparation failure in a completed batch");
        var pairProblem = (await host.LoadRetainedCommandProblemsAsync()).Single();
        Require(partial.Items.Single().FailureMessage?.Contains(pairFailureCause, StringComparison.Ordinal) == true &&
                pairProblem.Code == "pair_run_failed" && pairProblem.PairId == pair.Id &&
                pairProblem.Message.Contains("IOException", StringComparison.Ordinal) &&
                pairProblem.Message.Contains(pairFailureCause, StringComparison.Ordinal) &&
                (await database.LoadRunHistoryAsync(100)).Single(run => run.Id == pairProblem.CommandId).Status == "completed",
            "A completed batch with a failed pair retains that pair's exact error without claiming the whole command failed.");
        using (var reopened = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(instance.StateAuthority), connections,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database, (_, _) => remote))
            await CliChecks.CheckRetainedCommandAttentionDispatchAsync(reopened, instance.StateAuthority,
                pair.Id, pairProblem.CommandId, pairFailureCause, expected: true);
        rejectPairPreparation = false;
        await host.RunPairsAsync([pair], "Successful check after pair preparation repair");
        Require(!(await host.LoadRetainedCommandProblemsAsync()).Any() &&
                remote.UploadCount == 0 && remote.DownloadCount == 0,
            "A repaired pair's real normal success resolves only its current problem and performs no file replay.");

        // Exercise the real already-approved-path retry, not a manually written
        // failure row or a substitute for the host's admission decision.
        const string retryPath = "retry-command.txt";
        await File.WriteAllTextAsync(Path.Combine(local, retryPath), "Retained retry bytes");
        remote.UploadFailureForTest = path => path == retryPath
            ? new DropboxApiException(System.Net.HttpStatusCode.Conflict, "Controlled file lock") : null;
        var partialFiles = await host.RunPairsAsync([pair], "Original ordinary failed-file run");
        Require(partialFiles.Items.Single().Result is { Failed: 1 } && remote.UploadCount == 0,
            "The ordinary run retains a real file-specific refusal before its approved-path retry.");
        var origin = (await database.LoadRunHistoryAsync(100)).Single(run =>
            run.RequestJson.Contains("Original ordinary failed-file run", StringComparison.Ordinal));
        var preview = await host.LoadRunRetryAsync(origin.Id);
        Require(preview.UnavailableReason is null && preview.Items.Any(item => item.Path == retryPath && item.CanRetry),
            "The production retry reader admits only the retained failed path.");
        remote.UploadFailureForTest = null;
        rejectPairPreparation = true;
        await ThrowsAsync<IOException>(() => host.RetryRunAsync(origin.Id, [retryPath], "failed"));
        var retryProblem = (await host.LoadRetainedCommandProblemsAsync()).Single();
        var failedRetry = (await database.LoadRunRetriesAsync(origin.Id)).Single();
        Require(retryProblem.Code == "retry_failed" && retryProblem.CommandId == failedRetry.Id &&
                retryProblem.PairId == pair.Id && retryProblem.Message.Contains(pairFailureCause, StringComparison.Ordinal) &&
                failedRetry.Status == "blocked" && remote.UploadCount == 0 &&
                (await database.LoadRunByIdAsync(origin.Id)) == origin,
            "A real accepted retry command retains the same exact cause/run contract without changing its origin or dispatching files.");
        using (var reopened = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(instance.StateAuthority), connections,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database, (_, _) => remote))
            await CliChecks.CheckRetainedCommandAttentionDispatchAsync(reopened, instance.StateAuthority,
                pair.Id, failedRetry.Id, pairFailureCause, expected: true);
        rejectPairPreparation = false;
        var repairedRetry = await host.RetryRunAsync(origin.Id, [retryPath], "failed");
        Require(repairedRetry.Result is { Uploaded: 1, Failed: 0 } && remote.UploadCount == 1,
            "Repairing retry preparation copies only its originally approved failed file.");
        await host.RunPairsAsync([pair], "Full successful check after retained retry repair");
        Require(!(await host.LoadRetainedCommandProblemsAsync()).Any() && remote.UploadCount == 1 &&
                (await database.LoadRunByIdAsync(failedRetry.Id)) == failedRetry &&
                (await database.LoadRunByIdAsync(origin.Id)) == origin,
            "The later full successful check resolves the current retry problem without rewriting either failure or replaying the completed file.");

        async Task RecordOutcomeAsync(string id, string kind, string target, string request, string result)
        {
            await database.CreateCommandRequestAsync(id, kind, target, null, null, request);
            await database.TransitionCommandRequestAsync(id, "accepted");
            await database.TransitionCommandRequestAsync(id, "executing");
            await database.TransitionCommandRequestAsync(id, "completed", result);
        }
    }

    private static async Task FiniteReviewFinalizationAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var rollback in new[] { false, true })
        {
            var scenario = Path.Combine(root, rollback ? "atomic-refusal" : "late-arrival");
            Directory.CreateDirectory(scenario);
            using var instance = PilotInstanceLock.Acquire(Path.Combine(scenario, "state", "instance.lock"));
            var local = Path.Combine(scenario, "local");
            Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, "chosen.txt"), "reviewed bytes");
            var pairStore = new SyncPairStore(instance.StateAuthority);
            var connections = new DropboxConnectionStore(instance.StateAuthority);
            await connections.SaveNewAsync(Binding());
            var remote = new FakeSyncRemoteStore();
            var database = new SyncAuthorityDatabase(Path.Combine(scenario, "authority", "syncshark.db"));
            using var http = new HttpClient(new DropboxRecoveryBrowserContractHandler());
            var rejectFurtherScan = false;
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairStore,
                new SyncRuntimeStateStore(instance.StateAuthority), connections,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database,
                (_, _) => rejectFurtherScan ? throw new IOException("A later global refresh is unavailable") : remote);
            var bound = await host.BindPairAsync(new SyncPairBindingRequest(null, "Finite reviewed work", local,
                new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
                new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
                Enabled: true, Priority: 50, PropagateDeletions: true, FilesOnDemandEnabled: false,
                SelectionDefaultIncluded: true, RecoveryRetentionDays: 180));
            var pair = bound.Pair;
            var review = await host.PrepareBootstrapInventoryAsync(pair.Id);
            var plan = new SyncBootstrapActionPlan(pair.Id, review.LocalScanGenerationId!,
                review.DropboxScanGenerationId!, [new("chosen.txt", "Local only", "Upload")]);
            if (rollback)
                database.FirstSyncFinalizationBoundaryForTest = phase =>
                {
                    if (phase == "approval-written-before-terminal-result")
                        throw new IOException("Controlled atomic first-sync finalization refusal");
                };
            async Task LateArrivalAsync(SyncAuthorityLiveAction action)
            {
                if (action.Stage == "Completed")
                {
                    remote.Put("later.txt", "not part of the approved review");
                    rejectFurtherScan = true;
                }
                await Task.CompletedTask;
            }
            if (rollback)
                await ThrowsAsync<IOException>(() => host.RunBootstrapActionPlanAsync(plan, LateArrivalAsync));
            else
            {
                var result = await host.RunBootstrapActionPlanAsync(plan, LateArrivalAsync);
                Require(result.FirstSyncComplete && result.AppliedItems == 1 && result.Result.Uploaded == 1,
                    "The exact chosen file completes consent despite a later arrival and an unavailable global refresh.");
            }
            database.FirstSyncFinalizationBoundaryForTest = null;
            await database.RecoverInterruptedCommandRequestsAsync();
            using var reopened = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairStore,
                new SyncRuntimeStateStore(instance.StateAuthority), connections,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database, (_, _) => remote);
            var retainedPair = (await reopened.LoadPairsAsync()).Pairs.Single();
            Require(retainedPair.InitialReviewApproved == !rollback &&
                    retainedPair.PolicyRevision == pair.PolicyRevision + (rollback ? 0 : 1),
                "Reopen retains either the whole approval/revision/result commit or the original unapproved revision.");
            var retainedRun = (await database.LoadRunHistoryAsync()).Single(item =>
                item.ResultJson?.Contains("\"reviewed\":1") == true);
            using var outcome = JsonDocument.Parse(retainedRun.ResultJson!);
            Require(outcome.RootElement.GetProperty("Uploaded").GetInt32() == 1 &&
                    outcome.RootElement.GetProperty("files").EnumerateArray().Any(item =>
                        item.GetProperty("RelativePath").GetString() == "chosen.txt" &&
                        item.GetProperty("Kind").GetInt32() == (int)SyncDecisionKind.Uploaded) &&
                    retainedRun.Status == (rollback ? "blocked" : "completed") && remote.UploadCount == 1,
                "Both terminal routes retain the actual successful file exactly once, including a rolled-back approval.");
            Require(!File.Exists(Path.Combine(local, "later.txt")) && remote.ReadText("chosen.txt") == "reviewed bytes",
                "A later unreviewed arrival is not copied by the captured first-sync consent.");
            Require(await database.LoadLastSuccessfulSyncUtcAsync() is null,
                "Saved finite consent does not falsely advertise a globally clean sync.");
            if (!rollback)
            {
                await ThrowsAsync<InvalidOperationException>(() => reopened.RunBootstrapActionPlanAsync(plan));
                Require(!(await reopened.LoadRetainedFirstSyncProblemsAsync()).Any(),
                    "Saved consent does not reopen first-sync review after restart or replay rejection.");
                var normal = await reopened.RunPairsAsync([retainedPair], "Process genuinely new work");
                Require(normal.Items.All(item => item.FailureMessage is null) &&
                        await File.ReadAllTextAsync(Path.Combine(local, "later.txt")) == "not part of the approved review" &&
                        remote.UploadCount == 1,
                    "Only the subsequent ordinary run copies the late arrival without replaying the reviewed upload.");
            }
            else
            {
                var next = await reopened.PrepareBootstrapInventoryAsync(pair.Id);
                var choices = next.Items.Where(item => SyncBootstrapActions.IsActionable(item.Group))
                    .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group, item.Recommendation)).ToArray();
                var completed = await reopened.RunBootstrapActionPlanAsync(new(pair.Id,
                    next.LocalScanGenerationId!, next.DropboxScanGenerationId!, choices));
                Require(completed.FirstSyncComplete && remote.UploadCount == 1 &&
                        completed.Pair.PolicyRevision == pair.PolicyRevision + 1,
                    "A new reviewed attempt after atomic refusal commits once without replaying the already completed upload.");
            }
        }
    }

    private static DropboxConnectionBinding Binding() => new DropboxConnectionBinding(
        DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
        DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes);

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidDataException("Expected " + typeof(T).Name + " but no exception was thrown.");
    }
}
