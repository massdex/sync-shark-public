using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using IOPath = System.IO.Path;

// Normalized original consent proof (schema 12). One exact original reviewed selection is retained exactly once,
// in a single consent_header plus one immutable consent_item body per original ordinal, and every reviewed
// command, retry and retry-of-retry resolves through that normalized original instead of repeating a whole
// Choices array in each command payload. This file drives the real production preparation (bounded
// non-authorizing chunks plus one atomic seal), the real command-acceptance consent binding and reservation
// identity, the real runtime host over real NTFS plus the synthetic provider fixture, the real selected-retry
// decision point, the real referential constraints, the reviewed-producer binding contract and the real
// version 11 predecessor migration. No live provider, credential, signing, install, updater or retained
// product database is touched.
internal static class NormalizedOriginalConsentChecks
{
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _assertions = 0;
        if (!OperatingSystem.IsWindows()) return;

        await HostMixedWinnerJourneyAsync();
        await RunOrdinaryWindowBackpressureAsync(IOPath.Combine(_root,"ordinary-window-backpressure"));
        await RunReviewedWindowBackpressureAsync(IOPath.Combine(_root,"reviewed-window-backpressure"));
        await RunFolderPlanWindowsAsync(IOPath.Combine(_root,"folder-windows"),33);
        await RunOrdinaryWindowBackpressureAsync(IOPath.Combine(_root,"standalone-window-backpressure"),standalone:true);
        await RunOrdinaryPlanWindowAsync(IOPath.Combine(_root,"ordinary-windows"),33);
        await RunOrdinaryPlanWindowAsync(IOPath.Combine(_root,"ordinary-window-stop"),65,stopBetweenWindows:true);
        await CompactedCompletionJourneyAsync();
        await ClosedSelectionJourneyAsync();
        await LargeOriginalAndSmallRetryScalingAsync();
        await PreparationCancellationChecksAsync();
        await LegacyOriginalImportChecksAsync();
        await ExpiredAndBindingRefusalChecksAsync();
        await NormalizedContractRefusalChecksAsync();
        await ReferentialConstraintChecksAsync();
        await SelectedRetryLifecycleChecksAsync();
        await ReservationIdentityChecksAsync();
        await ReviewedProducerBindingChecksAsync();
        await PredecessorMigrationChecksAsync();

        Console.WriteLine("PASS normalized original consent checks: " + _assertions +
            " assertions; the reviewed original selection is retained exactly once, every preparation chunk and" +
            " the single seal are bound to their exact reserved command, and every missing, altered, duplicate," +
            " completed or expired original choice refuses before any effect.");
    }

    // ---- NCC1/NCC2: the real Host, restart, and the consent-backed retry chain ------------------------

    private static async Task HostMixedWinnerJourneyAsync()
    {
        var root = IOPath.Combine(_root, "host-journey");
        Directory.CreateDirectory(root);
        var localRoot = IOPath.Combine(root, "local", "normalized-consent-journey");
        Directory.CreateDirectory(IOPath.Combine(localRoot, "nested"));
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "alpha-upload.txt"), "alpha payload");
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "beta-fail.txt"), "beta payload");
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "nested", "inside-upload.txt"), "nested payload");

        JourneyState state;
        var first = await RigAsync(root, fresh: true);
        try
        {
            state = await RunReviewedJourneyAsync(first, localRoot);
        }
        finally
        {
            first.Dispose();
            SqliteConnection.ClearAllPools();
        }

        // A full product restart over the same retained state: the normalized original and its binding must survive
        // byte-for-byte and the retry chain must still resolve from the retained ancestry.
        var restarted = await RigAsync(root, fresh: false, provider: first.Remote);
        try
        {
            var headerAfter = await restarted.Database.LoadConsentHeaderAsync(state.ConsentId);
            Require(headerAfter is not null &&
                headerAfter.Lifecycle == SyncAuthorityConsentLifecycle.Active &&
                headerAfter.OriginalChoiceDigest == state.ConsentDigest &&
                headerAfter.OriginalChoiceCount == state.ConsentCount &&
                headerAfter.SealedUtc == state.ConsentSealedUtc,
                "NCC2 the sealed original consent survives a full restart with its exact digest, count and seal");
            var bindingAfter = await restarted.Database.LoadConsentBindingForCommandAsync(state.Origin.Id);
            Require(bindingAfter is not null && bindingAfter.ConsentId == state.ConsentId,
                "NCC2 the command-to-consent binding survives a full restart");

            // Age only disposable fixture state. Use the real action closure and
            // collection paths, then remove completed choice bodies before the
            // real Host retry. Failed/unknown exact operations remain pinned.
            await restarted.Database.EnableActivityRetentionForTestAsync();
            var maintenanceClock = DateTimeOffset.UtcNow;
            restarted.Database.ActivityRetentionClockForTest = () => maintenanceClock;
            for (var pass = 0; pass < 8; pass++)
                await restarted.Database.RetireSettledActionBatchAsync();
            maintenanceClock = maintenanceClock.AddDays(8);
            for (var pass = 0; pass < 12; pass++)
                await restarted.Database.RetireSettledActionBatchAsync();
            for (var pass = 0; pass < 12; pass++)
                await restarted.Database.RetireInactiveConsentBatchAsync();
            var afterCompaction = await restarted.Database.MaterializeRetainedOriginalConsentPlanAsync(state.ConsentId, null);
            Require(afterCompaction.Choices.Count < state.Plan.Choices.Count &&
                afterCompaction.Choices.All(item => item.RelativePath != "alpha-upload.txt"),
                "NCC2 real successful action and completed choice detail retire before Host retry");
            Require(await RefusedAsync(() => restarted.Host.RetryRunAsync(state.Origin.Id,
                    ["alpha-upload.txt"], "all", selectedPairId: state.Pair.Id)),
                "NCC2 Host refuses a retired completed path without reconstructing consent");
            restarted.Database.ActivityRetentionClockForTest = null;

            restarted.Remote.UploadFailureForTest = null;
            var uploadsBefore = restarted.Remote.UploadCount;
            var retryOne = await restarted.Host.RetryRunAsync(state.Origin.Id, ["beta-fail.txt"], "failed",
                selectedPairId: state.Pair.Id);
            Require(retryOne.Result.Failed == 0 && restarted.Remote.UploadCount == uploadsBefore + 1 &&
                restarted.Remote.Exists("beta-fail.txt") &&
                restarted.Remote.ReadText("beta-fail.txt") == "beta payload" &&
                (await restarted.Database.LoadRunByIdAsync(state.Origin.Id))!.ResultJson == state.Origin.ResultJson,
                "NCC2 the retry completes exactly the retained failure and never rewrites the original result");

            var child = await NewestRetryAsync(restarted, state.Origin.Id);
            foreach (var compactRun in new[] { state.Origin, child })
            {
                using var summary = JsonDocument.Parse(compactRun.ResultJson!);
                var summaryRoot = summary.RootElement;
                Require(!summaryRoot.TryGetProperty("files", out _) &&
                    summaryRoot.GetProperty("pairs").EnumerateArray().All(scope =>
                        !scope.TryGetProperty("files", out _) &&
                        scope.GetProperty("detailsSource").GetString() == "retained_history_and_authority"),
                    "NCC2 real original/retry run summaries retain aggregates without duplicate per-file bodies");
            }
            using (var childRequest = JsonDocument.Parse(child.RequestJson))
            {
                var hasConsentNode = childRequest.RootElement.TryGetProperty("reviewedConsent", out var consentNode);
                Require(!childRequest.RootElement.TryGetProperty("reviewedPlan", out _) && hasConsentNode,
                    "NCC2 the retry keeps no second copy of the reviewed original plan");
                var reference = SyncReviewedConsentReference.ReadRequired(consentNode);
                Require(reference.CommandId == state.Origin.Id &&
                    reference.RequestSha256 ==
                        SyncReviewedConsentReference.LowercaseRequestSha256(state.Origin.RequestJson) &&
                    reference.ChoiceCount == state.Plan.Choices.Count &&
                    reference.ConsentId == state.ConsentId,
                    "NCC2 the retry binds the exact terminal original identity, fingerprint, count and consent");
            }

            restarted.Remote.DownloadFailureForTest = null;
            var downloadsBefore = restarted.Remote.DownloadCount;
            var retryTwo = await restarted.Host.RetryRunAsync(child.Id, ["gamma-download.txt"], "failed",
                selectedPairId: state.Pair.Id);
            Require(retryTwo.Result.Failed == 0 && restarted.Remote.DownloadCount == downloadsBefore + 1 &&
                await File.ReadAllTextAsync(IOPath.Combine(localRoot, "gamma-download.txt")) == "gamma payload",
                "NCC2 retry-of-retry completes the retained download without replaying the upload ancestor");

            var grandchild = await NewestRetryAsync(restarted, child.Id);
            using (var grandchildRequest = JsonDocument.Parse(grandchild.RequestJson))
                Require(grandchildRequest.RootElement.GetProperty("retryOfRunId").GetString() == child.Id &&
                    grandchildRequest.RootElement.GetProperty("reviewedConsent").GetProperty("commandId")
                        .GetString() == state.Origin.Id &&
                    !grandchildRequest.RootElement.TryGetProperty("reviewedPlan", out _),
                    "NCC2 retry-of-retry derives the same original consent from the retained ancestry, not the child");

            var finalPreview = await restarted.Host.LoadRunRetryAsync(grandchild.Id, default, state.Pair.Id);
            Require(finalPreview.UnavailableReason is null &&
                finalPreview.Items.All(item => item.Path != "delta-skip.txt") &&
                !File.Exists(IOPath.Combine(localRoot, "delta-skip.txt")),
                "NCC2 the compact consent chain stays readable and no retry turns the original skip into consent");

            await restarted.Database.EnableActivityRetentionForTestAsync();
            var idleResults = new List<SyncActivityRetirementResult?>();
            for (var i = 0; i < 4; i++)
                idleResults.Add(await restarted.Host.MaintainOrdinaryActivityHistoryAsync());
            Require(idleResults.Any(result => result?.Consents is not null),
                "NCC2 the real Host invokes bounded consent maintenance in its existing idle opportunities");
            Require(await restarted.Host.MaintainOrdinaryActivityHistoryAsync() is null,
                "NCC2 consent maintenance adds no fifth idle batch or independent timer budget");
            var window = await restarted.Database.LoadActivityRetentionWindowAsync();
            Require(window.Consents is { AccountingPending: false } && window.Describe().Contains("Original selections"),
                "NCC2 shared activity status exposes measured selection lifetime without reading all choice bodies");

        }
        finally
        {
            restarted.Dispose();
        }
    }

    private static async Task CompactedCompletionJourneyAsync()
    {
        using var rig = await RigAsync(IOPath.Combine(_root, "compact-completion"), fresh: true);
        var pair = await BindAsync(rig, "Compact completion", rig.Local, "/CompactCompletion", "id:compact-completion");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local, "already-done.txt"), "completed original");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local, "retry-only.txt"), "remaining original");
        rig.Remote.UploadFailureForTest = path => path == "retry-only.txt"
            ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: known no effect") : null;
        var plan = await ReviewAsync(rig, pair);
        var first = await rig.Host.RunBootstrapActionPlanAsync(plan);
        Require(first.Result.Uploaded == 1 && first.Result.Failed == 1,
            "compact completion starts with one real success and one real no-effect refusal");
        var origin = (await rig.Database.LoadRunHistoryAsync(50)).Single(run =>
            run.RequestJson.Contains("\"action\":\"reviewed_first_sync\"", StringComparison.Ordinal));
        using var request = JsonDocument.Parse(origin.RequestJson);
        var consent = request.RootElement.GetProperty("consentId").GetString()!;
        await rig.Database.EnableActivityRetentionForTestAsync();
        var clock = DateTimeOffset.UtcNow;
        rig.Database.ActivityRetentionClockForTest = () => clock;
        for (var i = 0; i < 8; i++) await rig.Database.RetireSettledActionBatchAsync();
        clock = clock.AddDays(8);
        for (var i = 0; i < 12; i++) await rig.Database.RetireSettledActionBatchAsync();
        for (var i = 0; i < 12; i++) await rig.Database.RetireInactiveConsentBatchAsync();
        var retained = await rig.Database.MaterializeRetainedOriginalConsentPlanAsync(consent, null);
        Require(retained.Choices.Count == 1 && retained.Choices[0].RelativePath == "retry-only.txt",
            "compact completion collects the successful action and original body through real maintenance");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation WHERE status='blocked' AND consent_id=$id AND closure_utc IS NULL;",
                ("$id",consent)) == "1",
            "an issued original choice still pins its exact no-effect refusal while retry remains needed");
        rig.Database.ActivityRetentionClockForTest = null;
        rig.Remote.UploadFailureForTest = null;
        var uploads = rig.Remote.UploadCount;
        var retried = await rig.Host.RetryRunAsync(origin.Id, ["retry-only.txt"], "failed", selectedPairId: pair.Id);
        pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(value => value.Id == pair.Id);
        Require(retried.Result.Uploaded == 1 && rig.Remote.UploadCount == uploads + 1 &&
            pair.InitialReviewApproved == true && rig.Remote.ReadText("already-done.txt") == "completed original",
            "compact completion verifies retired successful paths and approves the pair without replay");

        await File.WriteAllTextAsync(IOPath.Combine(rig.Local, "normal-failure.txt"), "ordinary remaining");
        rig.Remote.UploadFailureForTest = path => path == "normal-failure.txt"
            ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: ordinary no effect") : null;
        await rig.Host.RunPairsAsync([pair], "Compact normal-run fixture");
        var normal = (await rig.Database.LoadRunHistoryAsync(50)).First(run =>
            run.RequestJson.Contains("Compact normal-run fixture", StringComparison.Ordinal));
        using (var summary = JsonDocument.Parse(normal.ResultJson!))
            Require(summary.RootElement.GetProperty("pairs").EnumerateArray().All(value =>
                !value.TryGetProperty("files", out _)),
                "normal runs retain aggregate summaries without per-file result arrays");
        var preview = await rig.Host.LoadRunRetryAsync(normal.Id, selectedPairId: pair.Id);
        Require(preview.UnavailableReason is null && preview.Items.Any(item =>
                item.Path == "normal-failure.txt" && item.CanRetry && item.State == "Failed"),
            "normal retry reads the exact no-effect action without a per-file summary");
        rig.Remote.UploadFailureForTest = null;
        uploads = rig.Remote.UploadCount;
        await rig.Host.RetryRunAsync(normal.Id, ["normal-failure.txt"], "failed", selectedPairId: pair.Id);
        Require(rig.Remote.UploadCount == uploads + 1 &&
            rig.Remote.ReadText("normal-failure.txt") == "ordinary remaining",
            "normal retry completes exactly its retained failed action after compact summaries");
        var after = await rig.Host.LoadRunRetryAsync(normal.Id, selectedPairId: pair.Id);
        Require(after.Items.All(item => !item.CanRetry),
            "the completed normal retry cannot replay its original failure");
        clock = DateTimeOffset.UtcNow;
        rig.Database.ActivityRetentionClockForTest = () => clock;
        for (var i = 0; i < 12; i++) await rig.Database.RetireSettledActionBatchAsync();
        Require(await ScalarAsync(rig.DatabasePath,
            "SELECT count(*) FROM operation WHERE closure_kind='known_nonapplication';") == "2",
            "completed original and ordinary retry failures receive exact all-attempt nonapplication closure");
        var closedFailure = await ScalarAsync(rig.DatabasePath,
            "SELECT operation_id FROM operation WHERE closure_kind='known_nonapplication' ORDER BY operation_id LIMIT 1;");
        Require(await RefusedAsync(() => rig.Database.AcquireReconciliationOperationLeaseAsync(
                closedFailure, "closed-refusal-probe", TimeSpan.FromMinutes(1))),
            "a closed known-no-effect action cannot acquire another reconciliation lease");
        clock = clock.AddDays(8);
        for (var i = 0; i < 12; i++) await rig.Database.RetireSettledActionBatchAsync();
        Require(await ScalarAsync(rig.DatabasePath,
            "SELECT count(*) FROM operation WHERE closure_kind='known_nonapplication';") == "2",
            "resolved failure detail retains its ninety-day age window separately from successful detail");
        clock = clock.AddDays(83);
        for (var i = 0; i < 12; i++) await rig.Database.RetireSettledActionBatchAsync();
        Require(await ScalarAsync(rig.DatabasePath,
            "SELECT count(*) FROM operation WHERE closure_kind='known_nonapplication';") == "0" &&
            rig.Remote.ReadText("normal-failure.txt") == "ordinary remaining" &&
            rig.Remote.ReadText("retry-only.txt") == "remaining original",
            "eligible old refusal details retire after positive closure without touching successful retry files");
    }

    private static async Task ClosedSelectionJourneyAsync()
    {
        using var rig = await RigAsync(IOPath.Combine(_root, "closed-selection"), fresh: true);
        var pair = await BindAsync(rig, "Closed selection", rig.Local, "/ClosedSelection", "id:closed-selection");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local, "original.txt"), "one successful original");
        var first = await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig, pair));
        Require(first.Result.Uploaded == 1 && (await rig.Host.LoadPairsAsync()).Pairs.Single().InitialReviewApproved == true,
            "closed selection starts with a real successful first sync and committed pair approval");
        var origin = (await rig.Database.LoadRunHistoryAsync(50)).Single(run =>
            run.RequestJson.Contains("\"action\":\"reviewed_first_sync\"", StringComparison.Ordinal));
        using var request = JsonDocument.Parse(origin.RequestJson);
        var consent = request.RootElement.GetProperty("consentId").GetString()!;
        await rig.Database.EnableActivityRetentionForTestAsync();
        var clock = DateTimeOffset.UtcNow;
        rig.Database.ActivityRetentionClockForTest = () => clock;
        for (var i = 0; i < 8; i++) await rig.Database.RetireSettledActionBatchAsync();
        clock = clock.AddDays(8);
        for (var i = 0; i < 12; i++) await rig.Database.RetireSettledActionBatchAsync();
        for (var i = 0; i < 12; i++) await rig.Database.RetireInactiveConsentBatchAsync();
        Require(await ScalarAsync(rig.DatabasePath, "SELECT count(*) FROM consent_item WHERE consent_id=$id;", ("$id",consent)) == "0",
            "closed selection has no choice body after real successful action and detail collection");
        for (var i = 0; i < 8; i++) await rig.Database.RetireRunSummaryBatchAsync();
        Require(await rig.Database.LoadConsentHeaderAsync(consent) is not null,
            "a completed selection header stays during its thirty-day closure grace");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
            "UPDATE command_request SET consent_id=NULL WHERE command_request_id=$id;", ("$id",origin.Id))),
            "ordinary SQL cannot detach the original selection authority");
        clock = clock.AddDays(32);
        rig.Database.RunSummaryRetirementBoundaryForTest = phase =>
        { if (phase == "before-commit") throw new InvalidOperationException("fixture closed-selection crash"); };
        Require(await RefusedAsync(() => rig.Database.RetireRunSummaryBatchAsync()),
            "closed selection retirement can be interrupted at the same transaction boundary");
        Require(await rig.Database.LoadConsentHeaderAsync(consent) is not null &&
            await ScalarAsync(rig.DatabasePath,"SELECT consent_id FROM command_request WHERE command_request_id=$id;",("$id",origin.Id))==consent,
            "interrupted header retirement preserves the original header and command binding");
        var delayedPasses = 0;
        rig.Database.RunSummaryRetirementBoundaryForTest = phase =>
        { if (phase == "after-run-maintenance-preflight") { delayedPasses++; Thread.Sleep(75); } };
        for (var i = 0; i < 12; i++) await rig.Database.RetireRunSummaryBatchAsync();
        rig.Database.RunSummaryRetirementBoundaryForTest = null;
        Require(delayedPasses == 12, "slow preflight still permits bounded closed-header and command detachment");
        Require(await rig.Database.LoadConsentHeaderAsync(consent) is null &&
            await ScalarAsync(rig.DatabasePath,"SELECT consent_id IS NULL FROM command_request WHERE command_request_id=$id;",("$id",origin.Id))=="1",
            "closed selection header retires only after complete closure and exact command detachment");
        var stillRetained = await rig.Database.LoadRunByIdAsync(origin.Id);
        Require(stillRetained?.RequestJson == origin.RequestJson && stillRetained.ResultJson == origin.ResultJson,
            "selection expiry preserves the original run summary and request bytes");
        var uploads = rig.Remote.UploadCount;
        var preview = await rig.Host.LoadRunRetryAsync(origin.Id, selectedPairId: pair.Id);
        Require(preview.UnavailableReason?.Contains("expired") == true && preview.Items.Count == 0 &&
            await RefusedAsync(() => rig.Host.RetryRunAsync(origin.Id, ["original.txt"], "all", selectedPairId: pair.Id)) &&
            rig.Remote.UploadCount == uploads,
            "expired header produces an explicit unavailable preview and no replay");
        Require((await rig.Database.LoadActivityRetentionWindowAsync()).Consents is { ExpiredHeaders: 1 },
            "shared selection status records the actual closed header expiry");
        clock = clock.AddDays(51);
        for (var i = 0; i < 12; i++) await rig.Database.RetireRunSummaryBatchAsync();
        Require(await rig.Database.LoadRunByIdAsync(origin.Id) is null &&
            await File.ReadAllTextAsync(IOPath.Combine(rig.Local,"original.txt")) == "one successful original" &&
            rig.Remote.ReadText("original.txt") == "one successful original",
            "the later summary window retires old command detail while both real file copies remain intact");
    }

    private sealed record JourneyState(SyncPair Pair, SyncBootstrapActionPlan Plan, SyncAuthorityRunHistory Origin,
        string ConsentId, string ConsentDigest, int ConsentCount, string? ConsentSealedUtc);

    private static async Task<JourneyState> RunReviewedJourneyAsync(Rig rig, string localRoot)
    {
        var pair = await BindAsync(rig, "Normalized consent journey", localRoot, "/NormalizedConsentJourney",
            "id:normalized-consent-journey");
        rig.Remote.Put("gamma-download.txt", "gamma payload");
        rig.Remote.Put("delta-skip.txt", "delta payload");
        rig.Remote.UploadFailureForTest = path => path == "beta-fail.txt"
            ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: controlled refusal on beta-fail.txt")
            : null;
        rig.Remote.DownloadFailureForTest = path => path == "gamma-download.txt"
            ? new DropboxApiException(HttpStatusCode.InternalServerError,
                "fixture: controlled refusal on gamma-download.txt")
            : null;
        var plan = await ReviewAsync(rig, pair, "delta-skip.txt");
        var run = await rig.Host.RunBootstrapActionPlanAsync(plan);
        Require(rig.Remote.Exists("alpha-upload.txt") && rig.Remote.ReadText("alpha-upload.txt") == "alpha payload" &&
            rig.Remote.Exists("nested/inside-upload.txt") &&
            rig.Remote.ReadText("nested/inside-upload.txt") == "nested payload",
            "NCC1 every upload winner, including the directory member, reaches the provider with its exact bytes");
        Require(!rig.Remote.Exists("beta-fail.txt") && run.Result.Failed >= 1,
            "NCC1 the controlled upload failure is retained as a failure with no provider effect");
        Require(!File.Exists(IOPath.Combine(localRoot, "gamma-download.txt")) &&
            !File.Exists(IOPath.Combine(localRoot, "delta-skip.txt")),
            "NCC1 the controlled download failure and the explicit skip create no local file");
        Require(await File.ReadAllTextAsync(IOPath.Combine(localRoot, "beta-fail.txt")) == "beta payload" &&
            await File.ReadAllTextAsync(IOPath.Combine(localRoot, "nested", "inside-upload.txt")) == "nested payload",
            "NCC1 the reviewed local originals retain their exact bytes");

        var origin = (await rig.Database.LoadRunHistoryAsync(50)).Single(item =>
            item.RequestJson.Contains("\"reviewed_first_sync\"", StringComparison.Ordinal) &&
            item.RequestJson.Contains(pair.Id, StringComparison.Ordinal));
        string consentId;
        using (var request = JsonDocument.Parse(origin.RequestJson))
        {
            var payload = request.RootElement;
            Require(payload.GetProperty("action").GetString() == "reviewed_first_sync" &&
                !payload.TryGetProperty("reviewedPlan", out _) &&
                !origin.RequestJson.Contains("\"Choices\"", StringComparison.Ordinal),
                "NCC1 the accepted reviewed command retains no inline Choices array and no repeated plan");
            var fingerprint = payload.GetProperty("originalRequestSha256").GetString() ?? string.Empty;
            Require(fingerprint.Length == 64 && fingerprint.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "NCC1 the retained original fingerprint is the exact lowercase SHA-256 of the approved request");
            consentId = payload.GetProperty("consentId").GetString() ?? string.Empty;
            Require(consentId == SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(origin.Id),
                "NCC1 the retained consent identity is the exact consent identity of this reserved command");
        }

        var binding = await rig.Database.LoadConsentBindingForCommandAsync(origin.Id);
        Require(binding is not null && binding.ConsentId == consentId && binding.PairId == pair.Id &&
            binding.OriginalChoiceCount == plan.Choices.Count &&
            binding.Lifecycle == SyncAuthorityConsentLifecycle.Active,
            "NCC1 the accepted command is bound to its exact sealed-then-active original consent");

        var header = await rig.Database.LoadConsentHeaderAsync(consentId)
            ?? throw new InvalidDataException("The reviewed original consent header was not retained.");
        Require(header.Lifecycle == SyncAuthorityConsentLifecycle.Active &&
            header.OriginalChoiceCount == plan.Choices.Count &&
            header.PreparedChoiceCount == plan.Choices.Count && header.PreparedChoiceBytes > 0 &&
            header.SealedUtc is not null && header.ExpiredChoiceCount == 0 &&
            header.PreparedCommandId == origin.Id &&
            header.HeaderJson.Contains("\"plan\"", StringComparison.Ordinal),
            "NCC1 one immutable header retains the exact original count, byte total, seal and reserved command");

        var outlines = await LoadAllOutlinesAsync(rig.Database, consentId, plan.Choices.Count);
        Require(OutlinesMatchPlan(outlines, plan.Choices),
            "NCC1 every reviewed ordinal round-trips its exact path, expected group and action through the lean projection");

        var selectedOrdinals = Enumerable.Range(0, plan.Choices.Count)
            .Where(index => !string.Equals(plan.Choices[index].Action, "Skip for now", StringComparison.Ordinal))
            .ToArray();
        var bodies = await rig.Database.LoadConsentItemsAsync(consentId, selectedOrdinals,
            SyncAuthorityConsentSelectionMode.ExactRead);
        Require(bodies.Count == selectedOrdinals.Length && bodies.All(item =>
                item.Ordinal >= 0 && item.Ordinal < plan.Choices.Count &&
                SyncAuthorityConsentEncoding.DeserializeChoice(item.ChoiceJson) ==
                    Normalized(plan.Choices[item.Ordinal])),
            "NCC1 every selected ordinal retains one complete immutable original choice body byte-for-byte");

        var preview = await rig.Host.LoadRunRetryAsync(origin.Id, default, pair.Id);
        Require(preview.UnavailableReason is null &&
            preview.Items.Single(item => item.Path == "alpha-upload.txt").CanRetry == false &&
            preview.Items.All(item => item.Path != "delta-skip.txt"),
            "NCC1 the retained preview re-offers neither the completed upload nor the explicit skip");
        var uploadsBeforeRetry = rig.Remote.UploadCount;
        Require(await RefusedAsync(() => rig.Host.RetryRunAsync(origin.Id, ["alpha-upload.txt"], "failed",
                selectedPairId: pair.Id)) && rig.Remote.UploadCount == uploadsBeforeRetry,
            "NCC1 a completed original choice is refused by retry without any provider effect");

        return new JourneyState(pair, plan, origin, consentId, header.OriginalChoiceDigest,
            header.OriginalChoiceCount, header.SealedUtc);
    }

    // ---- NCC3: one 100000-choice original and many small retries ---------------------------------------

    private static async Task LargeOriginalAndSmallRetryScalingAsync()
    {
        const int ChoiceCount = 100000;
        var moveOrdinals = new[] { 5, 1005, 2005, 3005, 4005, 5005, 6005, 7005, 8005, 9005 };
        var root = IOPath.Combine(_root, "scaling");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized consent scaling", IOPath.Combine(root, "local"),
            "/NormalizedConsentScaling", "id:normalized-consent-scaling");

        var choices = BuildChoices(ChoiceCount);
        foreach (var ordinal in moveOrdinals)
            choices[ordinal] = choices[ordinal] with
            {
                Action = "MoveRemote",
                Move = new SyncBootstrapMoveReview(SyncAuthorityOperationKind.MoveRemote,
                    "folder/prior-" + Degree(ordinal) + ".txt", "object-" + Degree(ordinal),
                    "local-version-" + Degree(ordinal), "remote-version-" + Degree(ordinal))
            };
        var prepared = choices.Select(SyncAuthorityConsentEncoding.Prepare).ToArray();
        var digest = SyncAuthorityConsentEncoding.OrderedDigest(prepared);
        var reservation = SyncAuthorityCommandReservation.Mint();
        var consentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(reservation.CommandId);
        var plan = new SyncBootstrapActionPlan(pair.Id, "scaling-local-scan", "scaling-remote-scan", choices);
        var originalRequestJson = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan,
            "Reviewed first sync", pair.PolicyRevision, null, null);
        var originalSha = SyncReviewedConsentReference.LowercaseRequestSha256(originalRequestJson);
        var headerJson = SyncReviewedConsentReference.BuildConsentHeaderJson(originalRequestJson, plan, consentId);

        await rig.Database.BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation(consentId,
            reservation.CommandId, reservation.CommandId, pair.Id, originalSha, digest, headerJson, ChoiceCount));
        var largestChunkRows = 0;
        var largestChunkBytes = 0L;
        var offset = 0;
        while (offset < ChoiceCount)
        {
            var count = Math.Min(SyncAuthorityConsentEncoding.MaximumItemsPerChunk, ChoiceCount - offset);
            var chunk = new SyncAuthorityConsentPreparedChoice[count];
            Array.Copy(prepared, offset, chunk, 0, count);
            var candidate = await rig.Database.AppendConsentItemsAsync(consentId, offset, chunk);
            var chunkBytes = chunk.Sum(item => item.EncodedBytes);
            if (count > largestChunkRows) largestChunkRows = count;
            if (chunkBytes > largestChunkBytes) largestChunkBytes = chunkBytes;
            offset += count;
            if (offset < ChoiceCount && candidate.PreparedChoiceCount != offset)
                throw new InvalidDataException("A preparation chunk did not advance the exact contiguous count.");
        }
        var sealedCandidate = await rig.Database.SealConsentAsync(consentId);
        Require(sealedCandidate.Lifecycle == SyncAuthorityConsentLifecycle.Sealed &&
            sealedCandidate.AlreadyComplete && sealedCandidate.PreparedChoiceCount == ChoiceCount &&
            largestChunkRows <= SyncAuthorityConsentEncoding.MaximumItemsPerChunk &&
            largestChunkBytes <= SyncAuthorityConsentEncoding.MaximumChunkEncodedBytes,
            "NCC3 bounded contiguous chunks retain the whole 100000-choice original under one atomic seal");
        Require(await ScalarAsync(rig.DatabasePath, "SELECT count(*) FROM consent_header WHERE consent_id=$c;",
                ("$c", consentId)) == "1",
            "NCC3 exactly one header retains the whole 100000-choice original");
        Require(await ScalarAsync(rig.DatabasePath, "SELECT count(*) FROM consent_item WHERE consent_id=$c;",
                ("$c", consentId)) == ChoiceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "NCC3 exactly one contiguous item cohort retains every original ordinal");

        // Ten deliberately selected ordinals must never decode the other 99990 original bodies: only the selected
        // ones come back with their full immutable Move identity, everything else stays the lean projection.
        var selectedOrdinals = moveOrdinals.ToArray();
        var tenPaths = selectedOrdinals.Select(ordinal => choices[ordinal].RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materialized = await rig.Database.MaterializeRetainedOriginalConsentPlanAsync(consentId, tenPaths);
        Require(materialized.Choices.Count == ChoiceCount &&
            materialized.Choices.Count(item => item.Move is not null) == selectedOrdinals.Length &&
            selectedOrdinals.All(ordinal => materialized.Choices[ordinal].Move is not null),
            "NCC3 a ten-path retry read decodes exactly ten original bodies and never the other 99990");
        var tenBodies = await rig.Database.LoadConsentItemsAsync(consentId, selectedOrdinals,
            SyncAuthorityConsentSelectionMode.ExactRead);
        var totalEncodedBytes = prepared.Sum(item => item.EncodedBytes);
        Require(tenBodies.Count == selectedOrdinals.Length &&
            tenBodies.Sum(item => item.EncodedBytes) * 100 < totalEncodedBytes,
            "NCC3 the exact selected read loads one hundredth or less of the retained original payload");
        var outlined = await LoadAllOutlinesAsync(rig.Database, consentId, ChoiceCount);
        Require(outlined.Count == ChoiceCount && outlined[0].Ordinal == 0 &&
            outlined[^1].Ordinal == ChoiceCount - 1,
            "NCC3 a whole original run streams in bounded lean pages without decoding any choice body");

        var rootPayload = SyncReviewedConsentReference.BuildConsentBackedRunRequestJson(pair, plan,
            "Reviewed first sync", pair.PolicyRevision, null, consentId, originalSha);
        Require(Encoding.UTF8.GetByteCount(rootPayload) < 2048,
            "NCC3 the consent-backed root payload stays small for 100000 original choices");
        await rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(reservation, consentId, "run_pair",
            pair.Id, null, null, rootPayload, SyncAuthorityCommandPurpose.NormalEffect);
        var rootRun = await rig.Database.LoadRunByIdAsync(reservation.CommandId)
            ?? throw new InvalidDataException("The consent-backed root command was not retained.");

        var filterBytes = SyncReviewedConsentReference.EncodedByteLength(ChoiceCount);
        for (var retry = 0; retry < 20; retry++)
        {
            var selected = choices.Select(item => tenPaths.Contains(item.RelativePath)
                ? item : item with { Action = "Skip for now" }).ToArray();
            var reference = SyncReviewedConsentReference.Create(rootRun, plan, selected, consentId);
            var payload = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected },
                "Reviewed first sync", pair.PolicyRevision, rootRun.Id, reference);
            Require(Encoding.UTF8.GetByteCount(payload) < filterBytes * 2 + 2048,
                "NCC3 retry " + retry.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " payload scales with its ceil(N/8) filter and fixed metadata, not the whole original request");
            await rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                SyncAuthorityCommandReservation.Mint(), consentId, "run_pair", pair.Id, null, null, payload,
                SyncAuthorityCommandPurpose.NormalEffect);
        }
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM command_request WHERE consent_id=$c;", ("$c", consentId)) == "21",
            "NCC3 the root plus twenty small retries all bind the one retained original consent");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM command_request WHERE consent_id=$c AND payload_json LIKE '%reviewedPlan%';",
                ("$c", consentId)) == "0",
            "NCC3 no consent-bound command repeats a reviewed plan payload");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM command_request WHERE consent_id=$c AND payload_json LIKE '%Choices%';",
                ("$c", consentId)) == "0",
            "NCC3 no consent-bound command retains a whole original Choices array");
        Log(new
        {
            result = "large-original-small-retry-scaling",
            choices = ChoiceCount,
            headerRows = 1,
            itemRows = ChoiceCount,
            largestChunkRows,
            largestChunkBytes,
            rootPayloadBytes = Encoding.UTF8.GetByteCount(rootPayload),
            filterBytes,
            retries = 20,
            totalOriginalEncodedBytes = totalEncodedBytes
        });
    }

    // ---- NCC4: cancellation between chunks and around the single seal ----------------------------------

    private static async Task PreparationCancellationChecksAsync()
    {
        var root = IOPath.Combine(_root, "preparation-cancellation");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized consent cancellation", IOPath.Combine(root, "local"),
            "/NormalizedConsentCancellation", "id:normalized-consent-cancellation");
        var commandsBefore = await CountAsync(rig.DatabasePath, "command_request");

        var choices = BuildChoices(700);
        var prepared = choices.Select(SyncAuthorityConsentEncoding.Prepare).ToArray();
        var digest = SyncAuthorityConsentEncoding.OrderedDigest(prepared);
        var reservation = SyncAuthorityCommandReservation.Mint();
        var consentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(reservation.CommandId);
        var plan = new SyncBootstrapActionPlan(pair.Id, "cancel-local-scan", "cancel-remote-scan", choices);
        var requestJson = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "Reviewed first sync",
            pair.PolicyRevision, null, null);
        var requestSha = SyncReviewedConsentReference.LowercaseRequestSha256(requestJson);
        var headerJson = SyncReviewedConsentReference.BuildConsentHeaderJson(requestJson, plan, consentId);
        var preparation = new SyncAuthorityConsentPreparation(consentId, reservation.CommandId,
            reservation.CommandId, pair.Id, requestSha, digest, headerJson, prepared.Length);

        var began = await rig.Database.BeginConsentPreparationAsync(preparation);
        Require(began.Lifecycle == SyncAuthorityConsentLifecycle.Building && began.PreparedChoiceCount == 0 &&
            !began.AlreadyComplete,
            "NCC4 a fresh preparation starts one building header that is not approval and retains no choice");

        var chunkOne = Slice(prepared, 0, 256);
        var chunkTwo = Slice(prepared, 256, 256);
        var chunkThree = Slice(prepared, 512, 188);
        var appendedOne = await rig.Database.AppendConsentItemsAsync(consentId, 0, chunkOne);
        Require(appendedOne.PreparedChoiceCount == 256 && appendedOne.PreparedChoiceBytes > 0,
            "NCC4 the first contiguous chunk records its exact prepared count and byte total");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await AttemptAsync(() => rig.Database.AppendConsentItemsAsync(consentId, 256, chunkTwo, cancelled.Token));
        }
        var afterCancellation = await rig.Database.LoadConsentHeaderAsync(consentId);
        Require(afterCancellation is not null &&
            afterCancellation.Lifecycle == SyncAuthorityConsentLifecycle.Building &&
            afterCancellation.PreparedChoiceCount == 256 &&
            afterCancellation.PreparedChoiceBytes == appendedOne.PreparedChoiceBytes,
            "NCC4 a cancelled preparation between chunks leaves only the exact non-authorizing building candidate");
        Require(await ScalarAsync(rig.DatabasePath, "SELECT count(*) FROM consent_item WHERE consent_id=$c;",
                ("$c", consentId)) == "256",
            "NCC4 a cancelled chunk retains no partial item row");
        Require(await CountAsync(rig.DatabasePath, "command_request") == commandsBefore &&
            rig.Remote.UploadCount == 0 && rig.Remote.DownloadCount == 0,
            "NCC4 no retained command and no provider effect ever sees a building or partial approval");

        Require(await RefusedAsync(() => rig.Database.SealConsentAsync(consentId)),
            "NCC4 an incomplete original selection never seals");
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(reservation,
                consentId, "run_pair", pair.Id, null, null, requestJson,
                SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC4 a building consent is never authority for a reviewed command");
        Require(await CountAsync(rig.DatabasePath, "command_request") == commandsBefore,
            "NCC4 the refused building-consent acceptance retained no command row");
        Require(await RefusedAsync(() => rig.Database.BeginConsentPreparationAsync(preparation)),
            "NCC4 a fresh reviewed run never silently resumes another incomplete preparation");

        var resumed = await rig.Database.BeginConsentPreparationAsync(
            preparation with { ContinueExistingPreparation = true });
        Require(resumed.Lifecycle == SyncAuthorityConsentLifecycle.Building &&
            resumed.PreparedChoiceCount == 256 && resumed.PreparedChoiceBytes == appendedOne.PreparedChoiceBytes,
            "NCC4 an explicit same-reservation resume is idempotent and rewrites nothing");
        var repeated = await rig.Database.AppendConsentItemsAsync(consentId, 0, chunkOne);
        Require(repeated.PreparedChoiceCount == 256,
            "NCC4 an identical repeated chunk is idempotent at its exact retained progress");
        Require(await ScalarAsync(rig.DatabasePath, "SELECT count(*) FROM consent_item WHERE consent_id=$c;",
                ("$c", consentId)) == "256",
            "NCC4 an identical repeated chunk retains no duplicate item row");

        Require(await RefusedAsync(() => rig.Database.AppendConsentItemsAsync(consentId, 400, chunkThree)),
            "NCC4 a chunk that skips ahead of the exact retained contiguous prefix is refused");
        var changedChunk = Slice(prepared, 0, 256);
        changedChunk[7] = SyncAuthorityConsentEncoding.Prepare(choices[7] with { Action = "Download" });
        Require(await RefusedAsync(() => rig.Database.AppendConsentItemsAsync(consentId, 0, changedChunk)),
            "NCC4 a repeated chunk whose bytes differ from the retained originals is refused");
        Require(await RefusedAsync(() => rig.Database.BeginConsentPreparationAsync(preparation with
                { ReservedCommandId = SyncAuthorityCommandReservation.Mint().CommandId,
                    ContinueExistingPreparation = true })),
            "NCC4 a foreign reservation can never adopt this preparation");
        Require(await RefusedAsync(() => rig.Database.BeginConsentPreparationAsync(preparation with
                { OriginalChoiceDigest = new string('a', 64), ContinueExistingPreparation = true })),
            "NCC4 a changed original ordered digest is refused against the retained header");

        using (var cancelledSeal = new CancellationTokenSource())
        {
            cancelledSeal.Cancel();
            await AttemptAsync(() => rig.Database.SealConsentAsync(consentId, cancelledSeal.Token));
        }
        var stillBuilding = await rig.Database.LoadConsentHeaderAsync(consentId);
        Require(stillBuilding is not null &&
            stillBuilding.Lifecycle == SyncAuthorityConsentLifecycle.Building && stillBuilding.SealedUtc is null,
            "NCC4 a cancelled seal retains no sealed header and no authority");

        var appendedTwo = await rig.Database.AppendConsentItemsAsync(consentId, 256, chunkTwo);
        var appendedThree = await rig.Database.AppendConsentItemsAsync(consentId, 512, chunkThree);
        Require(appendedTwo.PreparedChoiceCount == 512 && appendedThree.PreparedChoiceCount == 700,
            "NCC4 the resumed preparation completes every contiguous chunk");
        var sealedCandidate = await rig.Database.SealConsentAsync(consentId);
        Require(sealedCandidate.Lifecycle == SyncAuthorityConsentLifecycle.Sealed &&
            sealedCandidate.AlreadyComplete && sealedCandidate.PreparedChoiceCount == 700 &&
            sealedCandidate.SealedUtc is not null,
            "NCC4 the one atomic seal verifies the exact total, count and ordered digest");
        var sealedAgain = await rig.Database.SealConsentAsync(consentId);
        Require(sealedAgain.Lifecycle == SyncAuthorityConsentLifecycle.Sealed &&
            sealedAgain.SealedUtc == sealedCandidate.SealedUtc,
            "NCC4 sealing an already sealed original is idempotent and never rewrites the seal");
        var afterSeal = await rig.Database.AppendConsentItemsAsync(consentId, 0, chunkOne);
        Require(afterSeal.AlreadyComplete && afterSeal.PreparedChoiceCount == 700,
            "NCC4 an identical chunk after the seal is idempotent");
        Require(await RefusedAsync(() => rig.Database.AppendConsentItemsAsync(consentId, 0, changedChunk)),
            "NCC4 a changed chunk after the seal is refused");
        Require(await RefusedAsync(() => rig.Database.AppendConsentItemsAsync(consentId, prepared.Length, chunkOne)),
            "NCC4 a chunk beyond the exact original choice count is refused");
        Log(new
        {
            result = "preparation-cancellation",
            choices = prepared.Length,
            chunkRows = new[] { 256, 256, 188 },
            firstChunkBytes = appendedOne.PreparedChoiceBytes,
            preparedBytes = sealedCandidate.PreparedChoiceBytes,
            commandsRetained = commandsBefore,
            providerEffects = rig.Remote.UploadCount + rig.Remote.DownloadCount
        });
    }

    // ---- NCC5: bounded explicit legacy original import --------------------------------------------------

    private static async Task LegacyOriginalImportChecksAsync()
    {
        var root = IOPath.Combine(_root, "legacy-import");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized legacy import", IOPath.Combine(root, "local"),
            "/NormalizedLegacyImport", "id:normalized-legacy-import");
        var choices = new SyncBootstrapActionChoice[]
        {
            new SyncBootstrapActionChoice("folder/legacy-one.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/legacy-two.txt", "Local changed since sync", "Upload"),
            new SyncBootstrapActionChoice("folder/legacy-three.txt", "Dropbox only", "MoveRemote",
                new SyncBootstrapMoveReview(SyncAuthorityOperationKind.MoveRemote, "folder/legacy-three-old.txt",
                    "object-legacy", "local-version-legacy", "remote-version-legacy")),
            new SyncBootstrapActionChoice("folder/legacy-skip.txt", "Dropbox only", "Skip for now")
        };
        var plan = new SyncBootstrapActionPlan(pair.Id, "legacy-local-scan", "legacy-remote-scan", choices);
        var legacyPayload = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan,
            "Reviewed first sync", pair.PolicyRevision, null, null);
        var legacyId = "legacy-original-command-1";
        await SeedTerminalLegacyCommandAsync(rig.Database, legacyId, legacyPayload, pair.Id);
        var legacyRow = await rig.Database.LoadRunByIdAsync(legacyId)
            ?? throw new InvalidDataException("The retained legacy original command was not written.");

        // The isolated source fixture is a byte copy of the legacy state, checkpointed so the main file is complete,
        // and switched to a rollback journal so a read-only successor open needs no shared-memory side file.
        await ExecuteAsync(rig.DatabasePath, "PRAGMA wal_checkpoint(TRUNCATE);");
        SqliteConnection.ClearAllPools();
        var sourcePath = IOPath.Combine(root, "legacy-original-source.db");
        File.Copy(rig.DatabasePath, sourcePath, true);
        await ExecuteAsync(sourcePath, "PRAGMA journal_mode=DELETE;");
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(sourcePath + suffix)) File.Delete(sourcePath + suffix);
        var sourceHashBefore = Sha256File(sourcePath);
        var sourceWriteBefore = File.GetLastWriteTimeUtc(sourcePath);

        var import = await rig.Database.ImportLegacyOriginalConsentAsync(legacyId, sourcePath);
        Require(!import.AlreadyImported && import.SourceCommandId == legacyId &&
            import.ItemCount == choices.Length && import.ConsentId ==
                SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(legacyId) &&
            import.OriginalRequestSha256 == SyncReviewedConsentReference.LowercaseRequestSha256(legacyPayload),
            "NCC5 one explicit legacy import normalizes the exact original request identity and count");
        Require(Sha256File(sourcePath) == sourceHashBefore &&
            File.GetLastWriteTimeUtc(sourcePath) == sourceWriteBefore,
            "NCC5 the isolated legacy source database stays byte- and timestamp-identical");
        var legacyAfter = await rig.Database.LoadRunByIdAsync(legacyId);
        Require(legacyAfter is not null && legacyAfter.RequestJson == legacyRow.RequestJson &&
            await ScalarAsync(rig.DatabasePath,
                "SELECT coalesce(consent_id,'') FROM command_request WHERE command_request_id=$id;",
                ("$id", legacyId)) == "",
            "NCC5 the legacy source command keeps its exact payload and never gains a consent binding");

        var expected = choices.Select(Normalized).ToArray();
        var importedHeader = await rig.Database.LoadConsentHeaderAsync(import.ConsentId);
        Require(importedHeader is not null &&
            importedHeader.Lifecycle == SyncAuthorityConsentLifecycle.Sealed &&
            importedHeader.OriginalChoiceCount == expected.Length &&
            importedHeader.OriginalChoiceDigest == SyncAuthorityConsentEncoding.OrderedDigest(
                expected.Select(SyncAuthorityConsentEncoding.Prepare).ToArray()) &&
            importedHeader.OriginalRequestSha256 == import.OriginalRequestSha256,
            "NCC5 the imported original retains the exact count, ordered digest and request fingerprint");
        var importedItems = await rig.Database.LoadConsentItemsAsync(import.ConsentId,
            Enumerable.Range(0, expected.Length).ToArray(), SyncAuthorityConsentSelectionMode.ExactRead);
        Require(importedItems.Count == expected.Length && importedItems.All(item =>
                SyncAuthorityConsentEncoding.DeserializeChoice(item.ChoiceJson) == expected[item.Ordinal]),
            "NCC5 the imported original yields the identical exact approved actions including every move field");

        var reimport = await rig.Database.ImportLegacyOriginalConsentAsync(legacyId, sourcePath);
        Require(reimport.AlreadyImported && reimport.ConsentId == import.ConsentId &&
            reimport.ItemCount == import.ItemCount,
            "NCC5 re-importing the identical isolated original is idempotent");
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                SyncAuthorityCommandReservation.Mint(), import.ConsentId, "run_pair", pair.Id, null, null,
                legacyPayload, SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC5 an imported but never accepted sealed original is never authority for a retry command");
        Require(await RefusedAsync(() => rig.Database.ImportLegacyOriginalConsentAsync(legacyId,
                rig.DatabasePath)),
            "NCC5 a legacy import never runs against the target database itself");
        Require(await RefusedAsync(() => rig.Database.ImportLegacyOriginalConsentAsync("legacy-absent-command",
                sourcePath)),
            "NCC5 an absent legacy original refuses the import");

        await ExecuteAsync(sourcePath,
            "UPDATE command_request SET payload_json=replace(payload_json,'legacy-one.txt','legacy-one-x.txt')" +
            " WHERE command_request_id=$id;", ("$id", legacyId));
        Require(await RefusedAsync(() => rig.Database.ImportLegacyOriginalConsentAsync(legacyId, sourcePath)),
            "NCC5 a changed legacy source under the same identity is a fingerprint mismatch, not rebinding");
        Log(new
        {
            result = "legacy-original-import",
            items = import.ItemCount,
            sourceBytes = new FileInfo(sourcePath).Length,
            sourceSha256 = sourceHashBefore,
            alreadyImported = reimport.AlreadyImported
        });
    }

    // ---- NCC6: the expired original, the missing ordinal and the retry binding refusals ----------------

    private static async Task ExpiredAndBindingRefusalChecksAsync()
    {
        var root = IOPath.Combine(_root, "expired-refusal");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized consent expiry", IOPath.Combine(root, "local"),
            "/NormalizedConsentExpiry", "id:normalized-consent-expiry");
        var choices = new SyncBootstrapActionChoice[]
        {
            new SyncBootstrapActionChoice("folder/keep-one.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/keep-two.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/leave-skip.txt", "Dropbox only", "Skip for now"),
            new SyncBootstrapActionChoice("folder/leave-skip-two.txt", "Dropbox only", "Skip for now")
        };
        var accepted = await SealAndAcceptAsync(rig, pair, "expiry", choices);
        var skippedOrdinal = OrdinalOf(accepted.Plan, "folder/leave-skip.txt");
        var otherSkippedOrdinal = OrdinalOf(accepted.Plan, "folder/leave-skip-two.txt");
        var transferredOrdinal = OrdinalOf(accepted.Plan, "folder/keep-one.txt");
        Require(skippedOrdinal >= 0 && otherSkippedOrdinal >= 0 && transferredOrdinal >= 0,
            "NCC6 the reviewed fixture retained the transferred and both skipped original choices");

        var headerBefore = await rig.Database.LoadConsentHeaderAsync(accepted.ConsentId);
        var expired = await rig.Database.MarkConsentItemsExpiredAsync(accepted.ConsentId, [skippedOrdinal]);
        Require(expired.Lifecycle == SyncAuthorityConsentLifecycle.PartiallyExpired,
            "NCC6 expiring exactly one unneeded original advances the header to partially expired");
        var headerAfter = await rig.Database.LoadConsentHeaderAsync(accepted.ConsentId);
        Require(headerBefore is not null && headerAfter is not null &&
            headerAfter.ExpiredChoiceCount == 1 &&
            headerAfter.OriginalChoiceCount == headerBefore.OriginalChoiceCount &&
            headerAfter.OriginalChoiceDigest == headerBefore.OriginalChoiceDigest,
            "NCC6 an expired original never shrinks the retained original count or ordered digest");
        Require(await RefusedAsync(() => rig.Database.LoadConsentItemsAsync(accepted.ConsentId, [skippedOrdinal],
                SyncAuthorityConsentSelectionMode.ReissueSelection)),
            "NCC6 only the attempted expired original ordinal refuses while the original header is untouched");
        Require(await RefusedAsync(() => rig.Database.LoadConsentItemsAsync(accepted.ConsentId, [99],
                SyncAuthorityConsentSelectionMode.ReissueSelection)),
            "NCC6 a missing original ordinal always refuses the attempted selection");
        Require(await RefusedAsync(() => rig.Database.MarkConsentItemsExpiredAsync(accepted.ConsentId,
                [skippedOrdinal])),
            "NCC6 expiring an already expired original is refused rather than reapplied");
        var unrelatedRead = await rig.Database.LoadConsentItemsAsync(accepted.ConsentId, [otherSkippedOrdinal],
            SyncAuthorityConsentSelectionMode.ReissueSelection);
        Require(unrelatedRead.Count == 1 && unrelatedRead[0].Ordinal == otherSkippedOrdinal,
            "NCC6 an unrelated eligible ordinal stays exactly readable while another ordinal is expired");
        var exactRead = await rig.Database.LoadConsentItemsAsync(accepted.ConsentId,
            Enumerable.Range(0, accepted.Plan.Choices.Count).ToArray(), SyncAuthorityConsentSelectionMode.ExactRead);
        Require(exactRead.Count == accepted.Plan.Choices.Count,
            "NCC6 the exact read still returns every retained original item including the expired one");

        // Host-level refusals over the same retained original: a changed policy revision, a foreign original
        // identity, both representations at once and a missing ancestor all refuse with no provider effect.
        var uploadsBefore = rig.Remote.UploadCount;
        await RefuseChildAsync(rig, pair, accepted, "revision", payload =>
        {
            payload["policyRevision"] = accepted.Plan.Choices.Count + pair.PolicyRevision;
            return true;
        }, "NCC6 a consent-backed retry that changed the retained policy revision is refused");
        await RefuseChildAsync(rig, pair, accepted, "foreign-origin", payload =>
        {
            payload["reviewedConsent"]!["commandId"] = "foreign-original-command";
            return true;
        }, "NCC6 a consent reference naming a foreign original approval is refused");
        await RefuseChildAsync(rig, pair, accepted, "both-representations", payload =>
        {
            payload["reviewedPlan"] = JsonNode.Parse(JsonSerializer.Serialize(accepted.Plan));
            return true;
        }, "NCC6 a command keeping both reviewed representations at once is refused");
        await RefuseChildAsync(rig, pair, accepted, "missing-ancestor", payload =>
        {
            payload["retryOfRunId"] = "absent-ancestor-identity";
            return true;
        }, "NCC6 a compact consent retry naming a missing immediate ancestor is refused");
        Require(rig.Remote.UploadCount == uploadsBefore && rig.Remote.DownloadCount == 0,
            "NCC6 no refused consent binding issued any provider transfer");
        Log(new
        {
            result = "expired-and-binding-refusal",
            expiredOrdinal = skippedOrdinal,
            unrelatedEligibleOrdinal = otherSkippedOrdinal,
            retainedItems = exactRead.Count,
            providerEffects = rig.Remote.UploadCount + rig.Remote.DownloadCount
        });
    }

    private static async Task RefuseChildAsync(Rig rig, SyncPair pair, SealedConsent accepted, string label,
        Func<JsonObject, bool> mutate, string message)
    {
        var childId = "consent-refusal-child-" + label;
        var payload = JsonNode.Parse(SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
            accepted.Plan, "Reviewed first sync", pair.PolicyRevision, accepted.CommandId,
            new SyncReviewedConsentReference(accepted.CommandId,
                SyncReviewedConsentReference.LowercaseRequestSha256(accepted.RequestJson),
                accepted.Plan.Choices.Count,
                SyncReviewedConsentReference.EncodeSelection(accepted.Plan.Choices,
                    accepted.Plan.Choices.Select(item => item with { Action = "Skip for now" }).ToArray()),
                accepted.ConsentId)))!.AsObject();
        if (!mutate(payload)) throw new InvalidDataException("The refusal fixture " + label + " did not mutate.");
        await SeedTerminalLegacyCommandAsync(rig.Database, childId, payload.ToJsonString(), pair.Id);
        Require(await RefusedAsync(() => rig.Host.LoadRunRetryAsync(childId, default, pair.Id)), message);
    }

    // ---- NCC7: the schema-level contract refusals ------------------------------------------------------

    private static async Task NormalizedContractRefusalChecksAsync()
    {
        var root = IOPath.Combine(_root, "contract-refusals");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized contract refusals", IOPath.Combine(root, "local"),
            "/NormalizedContractRefusals", "id:normalized-contract-refusals");
        var foreign = await BindAsync(rig, "Normalized foreign pair", IOPath.Combine(root, "foreign"),
            "/NormalizedForeignPair", "id:normalized-foreign-pair");
        var commandsBefore = await CountAsync(rig.DatabasePath, "command_request");
        var accepted = await SealAndAcceptAsync(rig, pair, "refusals", RefusalChoices());

        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "UPDATE consent_item SET original_action='Download' WHERE consent_id=$c AND ordinal=0;",
                ("$c", accepted.ConsentId))),
            "NCC7 a retained original choice body and action are immutable");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "UPDATE consent_header SET original_choice_count=99 WHERE consent_id=$c;",
                ("$c", accepted.ConsentId))),
            "NCC7 the retained original count, digest and header identity are immutable");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "UPDATE command_request SET consent_id=NULL WHERE command_request_id=$id;",
                ("$id", accepted.CommandId))),
            "NCC7 the consent binding of a retained command is write-once");
        Require(await RefusedAsync(() => rig.Database.AppendConsentItemsAsync(accepted.ConsentId, 0,
                [SyncAuthorityConsentEncoding.Prepare(RefusalChoices()[0] with { Action = "Download" })])),
            "NCC7 a sealed original refuses a contradictory preparation chunk at a retained ordinal");

        var changedPayload = accepted.RequestJson.Replace("reviewed_first_sync", "reviewed_changed",
            StringComparison.Ordinal);
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                accepted.Reservation, accepted.ConsentId, "run_pair", pair.Id, null, null, changedPayload,
                SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC7 a reserved command identity already retained with a different payload is refused");
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                SyncAuthorityCommandReservation.Mint(), "absent-consent-identity", "run_pair", pair.Id, null,
                null, accepted.RequestJson, SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC7 a reviewed command naming an absent original consent is refused");
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                SyncAuthorityCommandReservation.Mint(), accepted.ConsentId, "run_pair", foreign.Id, null, null,
                accepted.RequestJson, SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC7 a reviewed command naming a foreign folder pair for this original consent is refused");
        Require(await CountAsync(rig.DatabasePath, "command_request") == commandsBefore + 1,
            "NCC7 every refused consent acceptance retained no additional command row");

        var duplicateReservation = SyncAuthorityCommandReservation.Mint();
        var duplicateConsentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(
            duplicateReservation.CommandId);
        var duplicateChoices = new SyncBootstrapActionChoice[]
        {
            new SyncBootstrapActionChoice("folder/duplicate.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/duplicate.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/other.txt", "Local only", "Upload")
        };
        var duplicatePlan = new SyncBootstrapActionPlan(pair.Id, "duplicate-local-scan",
            "duplicate-remote-scan", duplicateChoices);
        var duplicateRequest = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, duplicatePlan,
            "Reviewed first sync", pair.PolicyRevision, null, null);
        var duplicateSha = SyncReviewedConsentReference.LowercaseRequestSha256(duplicateRequest);
        var duplicatePrepared = duplicateChoices.Select(SyncAuthorityConsentEncoding.Prepare).ToArray();
        await rig.Database.BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation(duplicateConsentId,
            duplicateReservation.CommandId, duplicateReservation.CommandId, pair.Id, duplicateSha,
            SyncAuthorityConsentEncoding.OrderedDigest(duplicatePrepared),
            SyncReviewedConsentReference.BuildConsentHeaderJson(duplicateRequest, duplicatePlan,
                duplicateConsentId), duplicatePrepared.Length));
        Require(await RefusedAsync(() => rig.Database.AppendConsentItemsAsync(duplicateConsentId, 0,
                duplicatePrepared)),
            "NCC7 the original duplicate-path refusal still refuses one chunk naming a path twice");
        Require(await RefusedAsync(() => rig.Database.BeginConsentPreparationAsync(
                new SyncAuthorityConsentPreparation(duplicateConsentId, duplicateReservation.CommandId,
                    duplicateReservation.CommandId, foreign.Id, duplicateSha,
                    SyncAuthorityConsentEncoding.OrderedDigest(duplicatePrepared),
                    SyncReviewedConsentReference.BuildConsentHeaderJson(duplicateRequest, duplicatePlan,
                        duplicateConsentId), duplicatePrepared.Length, true))),
            "NCC7 the same consent identity under a foreign folder pair is refused");

        var altered = await SealAndAcceptAsync(rig, pair, "altered", RefusalChoices());
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "DELETE FROM consent_item WHERE consent_id=$c AND ordinal=2;", ("$c", altered.ConsentId))),
            "NCC7 unprivileged deletion cannot corrupt the original population");
        // This isolated fixture deliberately damages retained bytes to exercise the real
        // read/seal corruption checks after proving the production write guard refuses it.
        await using (var corrupt = new SqliteConnection("Data Source=" + rig.DatabasePath + ";Pooling=False"))
        {
            await corrupt.OpenAsync();
            corrupt.CreateFunction<string, long, long>("syncshark_consent_retirement_allowed",
                (id, ordinal) => id == altered.ConsentId && ordinal == 2 ? 1L : 0L);
            await ExecuteOnAsync(corrupt,
                "DELETE FROM consent_item WHERE consent_id=$c AND ordinal=2;", ("$c", altered.ConsentId));
        }
        Require(await RefusedAsync(() => rig.Database.SealConsentAsync(altered.ConsentId)),
            "NCC7 an altered original population can never seal");
        Require(await RefusedAsync(() => rig.Database.LoadConsentItemsAsync(altered.ConsentId, [2],
                SyncAuthorityConsentSelectionMode.ReissueSelection)),
            "NCC7 an ordinal removed from the retained population refuses the attempted selection");

        var reference = new SyncReviewedConsentReference(altered.CommandId,
            SyncReviewedConsentReference.LowercaseRequestSha256(altered.RequestJson),
            altered.Plan.Choices.Count,
            SyncReviewedConsentReference.EncodeSelection(altered.Plan.Choices,
                altered.Plan.Choices.Select(item => item with { Action = "Skip for now" }).ToArray()),
            altered.ConsentId);
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(reference with
                { SelectionBitsBase64 = Bits(0, 0) })),
            "NCC7 a selection filter decoding to the wrong byte length is refused");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(reference with
                { SelectionBitsBase64 = Bits(0xF0) })),
            "NCC7 a selection filter setting bits beyond the original count is refused");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(reference with
                { SelectionBitsBase64 = "!!!!" })),
            "NCC7 a malformed base64 selection filter is refused");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(reference with
                { SelectionBitsBase64 = Bits(0, 0, 0, 0, 0, 0, 0, 0) })),
            "NCC7 an oversized selection filter is refused before any allocation");
        Require(Refuses(() => SyncReviewedConsentReference.ReadRequired(
                Json("{\"commandId\":\"x\",\"requestSha256\":\"ABC\",\"choiceCount\":4," +
                    "\"selectionBitsBase64\":\"AA==\"}"))),
            "NCC7 a retained consent reference without a lowercase SHA-256 is refused");
        SyncReviewedConsentReference.RequireSelectionAgainstRoot(reference, altered.Plan);
        Require(SyncReviewedConsentReference.DecodeSelection(reference).All(selected => !selected),
            "NCC7 an empty retained selection authorizes none of the original actions or skips");
        Require(Refuses(() => SyncReviewedConsentReference.RequireSelectionAgainstRoot(reference with
                { ChoiceCount = altered.Plan.Choices.Count + 1 }, altered.Plan)),
            "NCC7 a selection filter whose count differs from the retained original is refused");
        Require(Refuses(() => SyncReviewedConsentReference.RequireSelectionAgainstRoot(reference with
                { SelectionBitsBase64 = Bits(8) }, altered.Plan)),
            "NCC7 a filter selecting the retained original skip ordinal is refused");
        Log(new
        {
            result = "schema-and-selection-refusals",
            consentId = accepted.ConsentId,
            commandsBefore,
            commandsRetained = await CountAsync(rig.DatabasePath, "command_request")
        });
    }

    // ---- NCC8: the operation-to-original-item referential invariant (I3) --------------------------------

    private static async Task ReferentialConstraintChecksAsync()
    {
        var root = IOPath.Combine(_root, "referential-constraints");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized referential constraints", IOPath.Combine(root, "local"),
            "/NormalizedReferentialConstraints", "id:normalized-referential-constraints");
        var accepted = await SealAndAcceptAsync(rig, pair, "referential", RefusalChoices());
        var other = await SealAndAcceptAsync(rig, pair, "referential-other", OtherChoices());
        var profileId = await ScalarAsync(rig.DatabasePath,
            "SELECT capability_profile_id FROM capability_profile LIMIT 1;");
        Require(!string.IsNullOrWhiteSpace(profileId),
            "NCC8 the isolated fixture has a production-created capability profile for its operation rows");

        // A legitimate insert control: a real operation row naming a retained original choice is accepted.
        await InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-1", "fixture-key-1", pair.Id,
            accepted.ConsentId, 0, profileId);
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT coalesce(consent_id,'') FROM operation WHERE operation_id=$id;",
                ("$id", "fixture-op-1")) == accepted.ConsentId &&
            await ScalarAsync(rig.DatabasePath,
                "SELECT coalesce(consent_item_ordinal,-1) FROM operation WHERE operation_id=$id;",
                ("$id", "fixture-op-1")) == "0",
            "NCC8 the positive control stamps a real operation row to its exact retained original ordinal");

        Require(await RefusedSqlAsync(() => InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-half-a",
                "fixture-key-half-a", pair.Id, accepted.ConsentId, null, profileId)),
            "NCC8 an operation naming an original consent without an ordinal is refused");
        Require(await RefusedSqlAsync(() => InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-half-b",
                "fixture-key-half-b", pair.Id, null, 0, profileId)),
            "NCC8 an operation naming an original ordinal without its consent is refused");
        Require(await RefusedSqlAsync(() => InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-absent",
                "fixture-key-absent", pair.Id, "absent-consent-identity", 0, profileId)),
            "NCC8 an operation naming an absent original consent is refused");
        Require(await RefusedSqlAsync(() => InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-ordinal",
                "fixture-key-ordinal", pair.Id, accepted.ConsentId, 57, profileId)),
            "NCC8 an operation naming a non-retained original ordinal is refused");
        Require(await RefusedSqlAsync(() => InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-foreign",
                "fixture-key-foreign", pair.Id, accepted.ConsentId, other.Plan.Choices.Count - 1, profileId)),
            "NCC8 an ordinal retained only under a different original consent is refused for this consent");

        var buildingReservation = SyncAuthorityCommandReservation.Mint();
        var buildingConsentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(
            buildingReservation.CommandId);
        var buildingPlan = new SyncBootstrapActionPlan(pair.Id, "building-local-scan", "building-remote-scan",
            RefusalChoices());
        var buildingRequest = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, buildingPlan,
            "Reviewed first sync", pair.PolicyRevision, null, null);
        var buildingPrepared = RefusalChoices().Select(SyncAuthorityConsentEncoding.Prepare).ToArray();
        await rig.Database.BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation(buildingConsentId,
            buildingReservation.CommandId, buildingReservation.CommandId, pair.Id,
            SyncReviewedConsentReference.LowercaseRequestSha256(buildingRequest),
            SyncAuthorityConsentEncoding.OrderedDigest(buildingPrepared),
            SyncReviewedConsentReference.BuildConsentHeaderJson(buildingRequest, buildingPlan,
                buildingConsentId), buildingPrepared.Length));
        await rig.Database.AppendConsentItemsAsync(buildingConsentId, 0, buildingPrepared);
        Require(await RefusedSqlAsync(() => InsertFixtureOperationAsync(rig.DatabasePath, "fixture-op-building",
                "fixture-key-building", pair.Id, buildingConsentId, 0, profileId)),
            "NCC8 a building consent header is never authority for an operation row");

        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "UPDATE operation SET consent_id=$other, consent_item_ordinal=0 WHERE operation_id=$id;",
                ("$other", other.ConsentId), ("$id", "fixture-op-1"))),
            "NCC8 the consent authority of an operation is write-once and cannot be rebound");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "UPDATE operation SET consent_id=NULL, consent_item_ordinal=NULL WHERE operation_id=$id;",
                ("$id", "fixture-op-1"))),
            "NCC8 the consent authority of an operation cannot be unbound");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "DELETE FROM consent_item WHERE consent_id=$c AND ordinal=0;", ("$c", accepted.ConsentId))),
            "NCC8 an original item a retained operation references can never be removed");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "DELETE FROM consent_header WHERE consent_id=$c;", ("$c", accepted.ConsentId))),
            "NCC8 a consent header a retained operation or command references can never be removed");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT coalesce(consent_id,'') || '/' || coalesce(consent_item_ordinal,-1) || '/' || status" +
                " FROM operation WHERE operation_id=$id;", ("$id", "fixture-op-1")) ==
            accepted.ConsentId + "/0/planned",
            "NCC8 every rejected referential edit left the retained operation row exactly unchanged");

        // The item lifecycle advances only along its declared path, and a completed item always refuses a retry.
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
                "UPDATE consent_item SET lifecycle='completed' WHERE consent_id=$c AND ordinal=1;",
                ("$c", accepted.ConsentId))),
            "NCC8 an eligible original item can never jump straight to completed");
        await ExecuteAsync(rig.DatabasePath,
            "UPDATE consent_item SET lifecycle='issued' WHERE consent_id=$c AND ordinal=1 AND lifecycle='eligible';",
            ("$c", accepted.ConsentId));
        await ExecuteAsync(rig.DatabasePath,
            "UPDATE consent_item SET lifecycle='completed' WHERE consent_id=$c AND ordinal=1 AND lifecycle='issued';",
            ("$c", accepted.ConsentId));
        var completedBody = await rig.Database.LoadConsentItemsAsync(accepted.ConsentId, [1],
            SyncAuthorityConsentSelectionMode.ExactRead);
        Require(completedBody.Single().Lifecycle == SyncAuthorityConsentItemLifecycle.Completed,
            "NCC8 the declared original item lifecycle path reaches completed");
        Require(await RefusedAsync(() => rig.Database.LoadConsentItemsAsync(accepted.ConsentId, [1],
                SyncAuthorityConsentSelectionMode.ReissueSelection)),
            "NCC8 a completed original choice can never be reissued by a retry");
        Require(await RefusedAsync(() => rig.Database.MarkConsentItemsExpiredAsync(accepted.ConsentId, [1])),
            "NCC8 an original item that is no longer eligible can never expire");
        Log(new
        {
            result = "referential-constraints",
            positiveRows = 1,
            rejectedInserts = 5,
            rejectedEdits = 4,
            lifecyclePath = "eligible->issued->completed",
            profilePresent = !string.IsNullOrWhiteSpace(profileId)
        });
    }

    // ---- NCC9: the selected-retry lifecycle gate --------------------------------------------------------

    private static async Task SelectedRetryLifecycleChecksAsync()
    {
        var root = IOPath.Combine(_root, "selected-retry-lifecycle");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized selected retry", IOPath.Combine(root, "local"),
            "/NormalizedSelectedRetry", "id:normalized-selected-retry");
        var choices = new SyncBootstrapActionChoice[]
        {
            new SyncBootstrapActionChoice("folder/completed.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/eligible-move.txt", "Local changed since sync", "MoveRemote",
                new SyncBootstrapMoveReview(SyncAuthorityOperationKind.MoveRemote, "folder/eligible-move-old.txt",
                    "object-eligible", "local-eligible", "remote-eligible")),
            new SyncBootstrapActionChoice("folder/eligible-two.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/expired-skip.txt", "Dropbox only", "Skip for now")
        };
        var accepted = await SealAndAcceptAsync(rig, pair, "selected-retry", choices);
        var expiredOrdinal = OrdinalOf(accepted.Plan, "folder/expired-skip.txt");
        var completedOrdinal = OrdinalOf(accepted.Plan, "folder/completed.txt");
        var eligibleOrdinal = OrdinalOf(accepted.Plan, "folder/eligible-move.txt");
        Require(expiredOrdinal >= 0 && completedOrdinal >= 0 && eligibleOrdinal >= 0,
            "NCC9 the selected-retry fixture retained its expired, completed and eligible original choices");
        await rig.Database.MarkConsentItemsExpiredAsync(accepted.ConsentId, [expiredOrdinal]);
        await ExecuteAsync(rig.DatabasePath,
            "UPDATE consent_item SET lifecycle='issued' WHERE consent_id=$c AND ordinal=$o AND lifecycle='eligible';",
            ("$c", accepted.ConsentId), ("$o", completedOrdinal));
        await ExecuteAsync(rig.DatabasePath,
            "UPDATE consent_item SET lifecycle='completed' WHERE consent_id=$c AND ordinal=$o AND lifecycle='issued';",
            ("$c", accepted.ConsentId), ("$o", completedOrdinal));

        // The exact decision point the retry path uses: materializing a selected plan must refuse a completed or
        // expired selected body before any plan, command or effect, and must not decode it.
        Require(await RefusedAsync(() => rig.Database.MaterializeRetainedOriginalConsentPlanAsync(
                accepted.ConsentId, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "folder/completed.txt" })),
            "NCC9 the selected-retry plan materialization refuses a completed original body");
        Require(await RefusedAsync(() => rig.Database.MaterializeRetainedOriginalConsentPlanAsync(
                accepted.ConsentId, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "folder/expired-skip.txt" })),
            "NCC9 the selected-retry plan materialization refuses an expired original body");
        Require(await RefusedAsync(() => rig.Database.MaterializeRetainedOriginalConsentPlanAsync(
                accepted.ConsentId, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "folder/completed.txt", "folder/eligible-move.txt" })),
            "NCC9 one invalid selected original refuses the entire attempted selection, not only its own entry");

        // A valid subset must stay exactly readable while an unrelated completed or expired ordinal is present.
        var valid = await rig.Database.MaterializeRetainedOriginalConsentPlanAsync(accepted.ConsentId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "folder/eligible-move.txt" });
        Require(valid.Choices.Count == choices.Length &&
            valid.Choices[eligibleOrdinal].Move is not null &&
            string.Equals(valid.Choices[eligibleOrdinal].Action, "MoveRemote", StringComparison.Ordinal) &&
            valid.Choices[completedOrdinal].Move is null &&
            string.Equals(valid.Choices[completedOrdinal].Action, "Upload", StringComparison.Ordinal) &&
            string.Equals(valid.Choices[expiredOrdinal].Action, "Skip for now", StringComparison.Ordinal) &&
            valid.PairId == pair.Id,
            "NCC9 an unrelated completed or expired ordinal never blocks an otherwise valid exact selection");
        var headerAfter = await rig.Database.LoadConsentHeaderAsync(accepted.ConsentId);
        Require(headerAfter is not null && headerAfter.OriginalChoiceCount == choices.Length,
            "NCC9 the original count and digest are unchanged by every refused selection");

        // The real retry entry point refuses an expired selection with no provider effect, and the retained
        // inspection preview still reads the same original without refusing.
        var uploadsBefore = rig.Remote.UploadCount;
        Require(await RefusedAsync(() => rig.Host.RetryRunAsync(accepted.CommandId,
                ["folder/expired-skip.txt"], "failed", selectedPairId: pair.Id)) &&
            rig.Remote.UploadCount == uploadsBefore,
            "NCC9 the real retry entry point refuses an expired selected original without any provider effect");
        var inspection = await rig.Host.LoadRunRetryAsync(accepted.CommandId, default, pair.Id);
        Require(inspection.Items.All(item => item.CanRetry == false),
            "NCC9 the read-only retry inspection still reads the retained original without refusing");
        Log(new
        {
            result = "selected-retry-lifecycle",
            completedOrdinal,
            expiredOrdinal,
            eligibleOrdinal,
            refusedSelections = 3,
            providerEffects = rig.Remote.UploadCount + rig.Remote.DownloadCount
        });
    }

    // ---- NCC10: one reservation identity per consent-backed acceptance ----------------------------------

    private static async Task ReservationIdentityChecksAsync()
    {
        var root = IOPath.Combine(_root, "reservation-identity");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var pair = await BindAsync(rig, "Normalized reservation identity", IOPath.Combine(root, "local"),
            "/NormalizedReservationIdentity", "id:normalized-reservation-identity");
        var choices = RefusalChoices();
        var accepted = await SealAndAcceptAsync(rig, pair, "reservation", choices);
        var commandsAfterRoot = await CountAsync(rig.DatabasePath, "command_request");

        // A supplied reservation is the exact retained identity, and an identical replay is the same identity.
        var replay = await rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(accepted.Reservation,
            accepted.ConsentId, "run_pair", pair.Id, null, null, accepted.RequestJson,
            SyncAuthorityCommandPurpose.NormalEffect);
        Require(replay.CommandRequestId == accepted.Reservation.CommandId &&
            replay.CommandRequestId == accepted.CommandId &&
            await CountAsync(rig.DatabasePath, "command_request") == commandsAfterRoot,
            "NCC10 a supplied reservation is the exact retained command identity and its replay adds no row");
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                accepted.Reservation, accepted.ConsentId, "run_pair", pair.Id, null, null,
                accepted.RequestJson.Replace("reviewed_first_sync", "reviewed_other", StringComparison.Ordinal),
                SyncAuthorityCommandPurpose.NormalEffect)) &&
            await CountAsync(rig.DatabasePath, "command_request") == commandsAfterRoot,
            "NCC10 the same reservation with a conflicting identity is refused and revives no command");

        // A null reservation mints exactly one identity whose id is the retained row id and the consent binding.
        var retryPayload = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
            accepted.Plan with { Choices = choices.Select(item => item with { Action = "Skip for now" }).ToArray() }, "Reviewed first sync",
            pair.PolicyRevision, accepted.CommandId,
            new SyncReviewedConsentReference(accepted.CommandId,
                SyncReviewedConsentReference.LowercaseRequestSha256(accepted.RequestJson), choices.Length,
                SyncReviewedConsentReference.EncodeSelection(choices, []), accepted.ConsentId));
        var minted = await rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(null, accepted.ConsentId,
            "run_pair", pair.Id, null, null, retryPayload, SyncAuthorityCommandPurpose.NormalEffect);
        Require(minted.CommandRequestId.StartsWith("command-", StringComparison.Ordinal) &&
            await CountAsync(rig.DatabasePath, "command_request") == commandsAfterRoot + 1,
            "NCC10 a null reservation mints one fresh pending identity and retains exactly one command");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT coalesce(consent_id,'') FROM command_request WHERE command_request_id=$id;",
                ("$id", minted.CommandRequestId)) == accepted.ConsentId,
            "NCC10 the minted identity is the row id and carries the exact consent binding");

        // A refused attempt consumes neither the identity nor a partial row, and the same reservation then inserts
        // exactly once. The consent is sealed but not yet accepted for this exact reserved identity.
        var pending = await SealOnlyAsync(rig, pair, "reservation-pending", choices);
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                pending.Reservation, pending.ConsentId, "run_pair", pair.Id, null, null,
                pending.RequestJson.Replace("reviewed_first_sync", "reviewed_other", StringComparison.Ordinal),
                SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC10 a sealed-but-unaccepted original refuses a conflicting reserved command identity");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM command_request WHERE command_request_id=$id;",
                ("$id", pending.Reservation.CommandId)) == "0",
            "NCC10 the refused attempt retained no partial command row for that reserved identity");
        var admitted = await rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(pending.Reservation,
            pending.ConsentId, "run_pair", pair.Id, null, null, pending.RequestJson,
            SyncAuthorityCommandPurpose.NormalEffect);
        Require(admitted.CommandRequestId == pending.Reservation.CommandId &&
            await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM command_request WHERE command_request_id=$id;",
                ("$id", pending.Reservation.CommandId)) == "1",
            "NCC10 the same reservation inserts exactly once after the refused attempt");
        Require(await RefusedAsync(() => rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(
                pending.Reservation, pending.ConsentId, "run_pair", pair.Id, null, null,
                pending.RequestJson.Replace("reviewed_first_sync", "reviewed_spent", StringComparison.Ordinal),
                SyncAuthorityCommandPurpose.NormalEffect)),
            "NCC10 a spent reserved identity is never revived for a different command payload");
        Log(new
        {
            result = "reservation-identity",
            rootCommands = commandsAfterRoot,
            mintedIdFormat = minted.CommandRequestId.StartsWith("command-", StringComparison.Ordinal),
            refusedAttemptsRetainedRows = 0,
            commandsRetained = await CountAsync(rig.DatabasePath, "command_request")
        });
    }

    // ---- NCC11: the reviewed-producer binding contract ---------------------------------------------------

    private static async Task ReviewedProducerBindingChecksAsync()
    {
        var root = IOPath.Combine(_root, "reviewed-producer-binding");
        Directory.CreateDirectory(root);
        using var rig = await RigAsync(root, fresh: true);
        var localRoot = IOPath.Combine(root, "local", "binding-journey");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "bound-one.txt"), "bound one payload");
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "bound-two.txt"), "bound two payload");
        var pair = await BindAsync(rig, "Normalized producer binding", localRoot, "/NormalizedProducerBinding",
            "id:normalized-producer-binding");
        var beforeRun = await ScalarAsync(rig.DatabasePath, "SELECT count(*) FROM operation WHERE consent_id IS NOT NULL;");
        Require(beforeRun == "0",
            "NCC11 no control-allocation or pair-binding work fabricated an original consent before the review");

        var plan = await ReviewAsync(rig, pair);
        await rig.Host.RunBootstrapActionPlanAsync(plan);
        var origin = (await rig.Database.LoadRunHistoryAsync(50)).Single(item =>
            item.RequestJson.Contains("\"reviewed_first_sync\"", StringComparison.Ordinal) &&
            item.RequestJson.Contains(pair.Id, StringComparison.Ordinal));
        string consentId;
        using (var request = JsonDocument.Parse(origin.RequestJson))
            consentId = request.RootElement.GetProperty("consentId").GetString() ?? string.Empty;
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation o WHERE o.consent_id IS NULL AND o.operation_group_id IN" +
                " (SELECT operation_group_id FROM operation_group WHERE json_valid(detail_json)" +
                " AND json_extract(detail_json,'$.consentId') IS NOT NULL);") == "0",
            "NCC11 every operation of the reviewed consent group is bound to an exact original choice");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation WHERE consent_id=$c;", ("$c", consentId)) != "0",
            "NCC11 the real reviewed route stamps its operations with the exact retained original consent");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation o JOIN consent_item i" +
                " ON i.consent_id=o.consent_id AND i.ordinal=o.consent_item_ordinal" +
                " WHERE o.consent_id IS NOT NULL AND i.consent_id IS NULL;") == "0",
            "NCC11 no operation names an original ordinal that is not a retained choice of its consent");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation WHERE consent_id IS NOT NULL AND consent_item_ordinal IS NULL;") == "0",
            "NCC11 no reviewed operation is retained with a half-set consent authority");
        Log(new
        {
            result = "reviewed-producer-binding",
            consentId,
            reviewedOperations = await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation WHERE consent_id=$c;", ("$c", consentId)),
            unboundReviewedGroupOperations = await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation o WHERE o.consent_id IS NULL AND o.operation_group_id IN" +
                " (SELECT operation_group_id FROM operation_group WHERE json_valid(detail_json)" +
                " AND json_extract(detail_json,'$.consentId') IS NOT NULL);"),
            consentBeforeReview = beforeRun
        });
    }

    // ---- NCC12: the exact version 11 predecessor migration ----------------------------------------------

    private static async Task PredecessorMigrationChecksAsync()
    {
        var root = IOPath.Combine(_root, "predecessor-migration");
        var authority = Directory.CreateDirectory(IOPath.Combine(root, "authority")).FullName;
        var databasePath = IOPath.Combine(authority, "syncshark.db");
        const string installationId = "installation-predecessor-fixture";
        const string profileId = "profile-predecessor-fixture";
        await BuildVersionElevenPredecessorAsync(databasePath, installationId);
        Require(await ScalarAsync(databasePath, "PRAGMA user_version;") == "11",
            "NCC12 the isolated fixture is an exact version 11 predecessor before any product open");

        var database = new SyncAuthorityDatabase(databasePath);
        var boundaries = new List<string>();
        var previousHook = SyncAuthorityDatabase.MigrationBoundaryForTest;
        try
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = name => boundaries.Add(name);
            await database.InitializeAsync(installationId, profileId, new string('a', 40), "{}");
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = previousHook;
        }
        var current = SyncAuthoritySchema.Version;
        var migrated = await ScalarAsync(databasePath, "PRAGMA user_version;");
        Require(migrated == current.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            int.Parse(migrated, System.Globalization.CultureInfo.InvariantCulture) >= 12 &&
            int.Parse(migrated, System.Globalization.CultureInfo.InvariantCulture) > 11,
            "NCC12 the migrated database reports the actual current schema version, not a hardcoded successor");
        Require(boundaries.Contains("v11-backup-verified-before-migration") &&
            boundaries.Contains("v12-schema-applied-before-version-commit") &&
            boundaries.Contains("v12-migration-committed"),
            "NCC12 the real version 11 rollback backup and the version 12 commit boundary are both observed");
        Require(File.Exists(database.Version11RollbackDatabasePath) &&
            await ScalarAsync(database.Version11RollbackDatabasePath, "PRAGMA user_version;") == "11",
            "NCC12 the replaced version 11 rollback database is retained and still reports version 11");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM schema_migration WHERE schema_version=$v;", ("$v", migrated)) == "1",
            "NCC12 exactly one current schema-migration receipt is retained after the migration");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM schema_migration WHERE schema_version=11;") == "1",
            "NCC12 the retained version 11 receipt is preserved rather than rewritten");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM sqlite_schema WHERE type='table'" +
                " AND name IN ('consent_header','consent_item');") == "2",
            "NCC12 the migration created both normalized original consent tables");

        SqliteConnection.ClearAllPools();
        var reopened = new SyncAuthorityDatabase(databasePath);
        await reopened.VerifyHealthAsync();
        Require(await ScalarAsync(databasePath, "PRAGMA user_version;") == migrated &&
            await ScalarAsync(databasePath, "SELECT count(*) FROM schema_migration;") == "2",
            "NCC12 a stable reopen keeps the same version and adds no second receipt");
        Log(new
        {
            result = "predecessor-migration",
            predecessorVersion = 11,
            currentVersion = current,
            migratedVersion = migrated,
            boundaries = boundaries.Count,
            receipts = await ScalarAsync(databasePath, "SELECT count(*) FROM schema_migration;")
        });
    }

    private static async Task BuildVersionElevenPredecessorAsync(string databasePath, string installationId)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await ExecuteOnAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;");
        // Exactly the product's own version 1 base plus supplement plus migrations 3..11: the same scripts a real
        // version 11 database was created from, so the predecessor inventory is the recorded expected predecessor.
        foreach (var script in new[]
        {
            SyncAuthoritySchema.Sql,
            SyncAuthoritySchemaSupplement.Sql,
            SyncAuthoritySchemaMigration3.Sql,
            SyncAuthoritySchemaMigration4.Sql,
            SyncAuthoritySchemaMigration5.Sql,
            SyncAuthoritySchemaMigration6.Sql,
            SyncAuthoritySchemaMigration7.Sql,
            SyncAuthoritySchemaMigration8.Sql,
            SyncAuthoritySchemaMigration9.Sql,
            SyncAuthoritySchemaMigration10.Sql,
            SyncAuthoritySchemaMigration11.Sql
        })
            await ExecuteOnAsync(connection, script);
        await ExecuteOnAsync(connection,
            "INSERT INTO schema_migration(schema_version, applied_utc, source_revision)" +
            " VALUES(11, '2026-10-01T00:00:00.0000000Z', 'predecessor-fixture');");
        await ExecuteOnAsync(connection,
            "INSERT INTO installation(installation_id, database_generation, created_utc)" +
            " VALUES($id, 'predecessor-generation', '2026-10-01T00:00:00.0000000Z');", ("$id", installationId));
        await ExecuteOnAsync(connection, "PRAGMA user_version=11;");
        SqliteConnection.ClearAllPools();
    }

    // ---- shared fixtures and helpers --------------------------------------------------------------------

    private sealed record SealedConsent(string ConsentId, string CommandId, string RequestJson,
        SyncAuthorityCommandReservation Reservation, SyncBootstrapActionPlan Plan);

    private static SyncBootstrapActionChoice[] RefusalChoices() =>
    [
        new SyncBootstrapActionChoice("folder/first.txt", "Local only", "Upload"),
        new SyncBootstrapActionChoice("folder/second.txt", "Dropbox only", "Download"),
        new SyncBootstrapActionChoice("folder/third.txt", "Local changed since sync", "MoveRemote",
            new SyncBootstrapMoveReview(SyncAuthorityOperationKind.MoveRemote, "folder/third-old.txt",
                "object-third", "local-third", "remote-third")),
        new SyncBootstrapActionChoice("folder/fourth.txt", "Dropbox only", "Skip for now")
    ];

    private static SyncBootstrapActionChoice[] OtherChoices() =>
    [
        .. RefusalChoices(),
        new SyncBootstrapActionChoice("folder/other-one.txt", "Local only", "Upload"),
        new SyncBootstrapActionChoice("folder/other-two.txt", "Dropbox only", "Download")
    ];

    private static async Task<SealedConsent> SealAndAcceptAsync(Rig rig, SyncPair pair, string label,
        IReadOnlyList<SyncBootstrapActionChoice> choices)
    {
        var sealedConsent = await SealOnlyAsync(rig, pair, label, choices);
        await rig.Database.CreateAcceptedCommandAdmissionWithConsentAsync(sealedConsent.Reservation,
            sealedConsent.ConsentId, "run_pair", pair.Id, null, null, sealedConsent.RequestJson,
            SyncAuthorityCommandPurpose.NormalEffect);
        return sealedConsent;
    }

    private static async Task<SealedConsent> SealOnlyAsync(Rig rig, SyncPair pair, string label,
        IReadOnlyList<SyncBootstrapActionChoice> choices)
    {
        var prepared = choices.Select(SyncAuthorityConsentEncoding.Prepare).ToArray();
        var digest = SyncAuthorityConsentEncoding.OrderedDigest(prepared);
        var reservation = SyncAuthorityCommandReservation.Mint();
        var consentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(reservation.CommandId);
        var plan = new SyncBootstrapActionPlan(pair.Id, label + "-local-scan", label + "-remote-scan", choices);
        var requestJson = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan,
            "Reviewed first sync", pair.PolicyRevision, null, null);
        var sha = SyncReviewedConsentReference.LowercaseRequestSha256(requestJson);
        var headerJson = SyncReviewedConsentReference.BuildConsentHeaderJson(requestJson, plan, consentId);
        await rig.Database.BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation(consentId,
            reservation.CommandId, reservation.CommandId, pair.Id, sha, digest, headerJson, prepared.Length));
        var offset = 0;
        while (offset < prepared.Length)
        {
            var count = Math.Min(SyncAuthorityConsentEncoding.MaximumItemsPerChunk, prepared.Length - offset);
            var chunk = new SyncAuthorityConsentPreparedChoice[count];
            Array.Copy(prepared, offset, chunk, 0, count);
            await rig.Database.AppendConsentItemsAsync(consentId, offset, chunk);
            offset += count;
        }
        await rig.Database.SealConsentAsync(consentId);
        var payload = SyncReviewedConsentReference.BuildConsentBackedRunRequestJson(pair, plan,
            "Reviewed first sync", pair.PolicyRevision, null, consentId, sha);
        return new SealedConsent(consentId, reservation.CommandId, payload, reservation, plan);
    }



    internal static async Task RunReviewedWindowBackpressureAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var rig=await RigAsync(root,fresh:true);
        var pair=await BindAsync(rig,"Reviewed window",rig.Local,"/ReviewedWindow","id:reviewed-window");
        foreach(var name in new[]{"first.txt","second.txt","skip.txt"})
            await File.WriteAllTextAsync(IOPath.Combine(rig.Local,name),"approved "+name);
        var review=await ReviewAsync(rig,pair,"skip.txt");
        await ExecuteAsync(rig.DatabasePath,"""
            WITH RECURSIVE positions(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM positions WHERE n<255)
            INSERT INTO operation(operation_id,kind,status,priority,configuration_revision,capability_profile_id,
                idempotency_key,preconditions_json,created_utc)
            SELECT 'occupied-'||n,'create_remote','planned',50,1,
                (SELECT capability_profile_id FROM capability_profile LIMIT 1),
                'occupied-'||n,'{}','2026-01-01T00:00:00Z' FROM positions;
            """);
        var uploads=rig.Remote.UploadCount;
        var first=await rig.Host.RunBootstrapActionPlanAsync(review);
        var origin=(await rig.Database.LoadRunHistoryAsync(20)).Single(run=>
            run.RequestJson.Contains("\"action\":\"reviewed_first_sync\"",StringComparison.Ordinal));
        using var request=JsonDocument.Parse(origin.RequestJson);
        var consent=request.RootElement.GetProperty("consentId").GetString()!;
        var header=await rig.Database.LoadConsentHeaderAsync(consent)
            ?? throw new InvalidDataException("Approved selection header missing.");
        if(first.Result.Failed!=0 || first.Result.NotAttempted!=2 || rig.Remote.UploadCount!=uploads)
            throw new InvalidDataException("Capacity changed approved files into failures or effects: "+origin.ResultJson);
        var preview=await rig.Host.LoadRunRetryAsync(origin.Id,selectedPairId:pair.Id);
        if(preview.UnavailableReason is not null ||
            !new[]{"first.txt","second.txt"}.All(path=>preview.Items.Any(item=>item.Path==path && item.CanRetry)))
            throw new InvalidDataException("Original approved paths lost retry authority at capacity.");
        Console.WriteLine("PASS reviewed full window keeps both original approvals retryable with no failed file or effect");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local,"later.txt"),"not in original approval");
        await ExecuteAsync(rig.DatabasePath,"UPDATE operation SET status='cancelled',terminal_utc='2026-01-02T00:00:00Z' WHERE operation_id LIKE 'occupied-%';");
        var retried=await rig.Host.RetryRunAsync(origin.Id,["first.txt","second.txt"],"all",selectedPairId:pair.Id);
        var retained=await rig.Database.LoadConsentHeaderAsync(consent)
            ?? throw new InvalidDataException("Original header lost during capacity continuation.");
        if(retried.Result.Uploaded!=2 || rig.Remote.UploadCount!=uploads+2 ||
            rig.Remote.ReadText("first.txt")!="approved first.txt" ||
            rig.Remote.ReadText("second.txt")!="approved second.txt" ||
            rig.Remote.Exists("skip.txt") || rig.Remote.Exists("later.txt") ||
            header.OriginalChoiceCount!=retained.OriginalChoiceCount || header.OriginalChoiceDigest!=retained.OriginalChoiceDigest)
            throw new InvalidDataException("Capacity continuation changed or replayed original approved selection.");
        Console.WriteLine("PASS released window completes each original once and preserves skipped/new paths and selection identity");
        await rig.Database.VerifyHealthAsync();
    }


    internal static async Task RunBothAbsentCollectionAsync(string root,bool successorOnly=false)
    {
        using var rig=await RigAsync(root,true);
        var db=rig.Database;var dbPath=rig.DatabasePath;
        string? originalPath=null,originalHash=null;
        var pair=await BindAsync(rig,"Both absent collection",rig.Local,"/AbsentCollection","id:absent-collection");
        var file=IOPath.Combine(rig.Local,"retire.txt");
        await File.WriteAllTextAsync(file,"retire only proved obsolete metadata");
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,pair));
        pair=(await rig.Host.LoadPairsAsync()).Pairs.Single(item=>item.Id==pair.Id);
        var point=(await db.LoadLatestSynchronizationPointsAsync(pair.Id)).Single();
        var localRoot=(await db.LoadActiveRootBindingIdAsync(pair.Id,"local"))!;
        var remoteRoot=(await db.LoadActiveRootBindingIdAsync(pair.Id,"dropbox"))!;
        var localScan=(await db.LoadLatestCompleteScanIdAsync(pair.Id,"local"))!;
        var remoteScan=(await db.LoadLatestCompleteScanIdAsync(pair.Id,"dropbox"))!;
        var prior=await db.LoadPlanningObservationSetAsync(pair.Id,pair.PolicyRevision,
            localRoot,remoteRoot,localScan,remoteScan);
        var oldAuthority=prior.Bind("captured-before-absence") with {
            ObjectId=point.ObjectId,SynchronizationPointId=point.SynchronizationPointId };
        File.Delete(file);rig.Remote.Delete("retire.txt");
        var effects=(rig.Remote.UploadCount,rig.Remote.DownloadCount,rig.Remote.DeleteCount,rig.Remote.MoveCount);
        await rig.Host.RunPairsAsync([pair],"close-both-absence");
        await rig.Host.RunPairsAsync([pair],"confirm-both-absence-one");
        await rig.Host.RunPairsAsync([pair],"confirm-both-absence-two");
        await db.EnableActivityRetentionForTestAsync();
        var id=(await ScalarAsync(dbPath,
            "SELECT tombstone_id FROM tombstone WHERE object_id=$object AND absent_side='both';",("$object",point.ObjectId)))!;
        Require(await RefusedSqlAsync(()=>ExecuteAsync(dbPath,
            "DELETE FROM synchronization_point WHERE synchronization_point_id=$id;",("$id",point.SynchronizationPointId))),
            "ordinary SQL cannot delete a both-absent accepted baseline");
        for(var i=0;i<4;i++)await db.RetireOrdinaryActivityBatchAsync();
        Require(await ScalarAsync(dbPath,
            "SELECT count(*) FROM synchronization_point WHERE synchronization_point_id=$id;",("$id",point.SynchronizationPointId))=="1",
            "idle cleanup preserves the absent baseline during its original ninety-day grace");

        if(successorOnly)
        {
            originalPath=dbPath;
            await ExecuteAsync(dbPath,"PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
            originalHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(dbPath)));
            var options=new SyncEssentialSuccessorOptions(root);
            var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(dbPath,options);
            var target=IOPath.Combine(root,"successor.db");
            var oldBasis=(await ScalarAsync(dbPath,"SELECT both_absent_basis_json FROM tombstone WHERE tombstone_id=$id;",("$id",id)))!;
            var oldClosed=await ScalarAsync(dbPath,"SELECT both_absent_closed_utc FROM tombstone WHERE tombstone_id=$id;",("$id",id));
            var prepared=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(dbPath,target,identity,options);
            await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(dbPath,target,identity,options);
            var expected=System.Text.Json.Nodes.JsonNode.Parse(oldBasis)!;
            expected["DatabaseGeneration"]=prepared.TargetGeneration;
            var actual=System.Text.Json.Nodes.JsonNode.Parse((await ScalarAsync(target,
                "SELECT both_absent_basis_json FROM tombstone WHERE tombstone_id=$id;",("$id",id)))!)!;
            Require(System.Text.Json.Nodes.JsonNode.DeepEquals(expected,actual) &&
                await ScalarAsync(target,"SELECT both_absent_closed_utc FROM tombstone WHERE tombstone_id=$id;",("$id",id))==oldClosed,
                "successor changes only proof generation and preserves original ninety-day clock, accepted point, roots, policy and absence cuts");
            dbPath=target;db=new SyncAuthorityDatabase(target);
            Require(await RefusedSqlAsync(()=>ExecuteAsync(dbPath,"""
                INSERT INTO tombstone(tombstone_id,pair_id,object_id,absent_side,prior_membership_id,
                    prior_object_version_id,observed_absent_utc,complete_observation_count,status,
                    both_absent_basis_json,both_absent_closed_utc)
                SELECT 'forged-absence',pair_id,object_id,absent_side,prior_membership_id,prior_object_version_id,
                    observed_absent_utc,complete_observation_count,status,both_absent_basis_json,both_absent_closed_utc
                FROM tombstone WHERE tombstone_id=$id;
                """,("$id",id))),"ordinary inserts cannot forge absence proof after the exact import guard is restored");
            Require(await db.LoadBothAbsentRetirementHoldAsync(id)=="ninety_day_grace",
                "successor generation does not bypass original absence grace");
            db.ActivityRetentionClockForTest=()=>DateTimeOffset.UtcNow.AddDays(91);
            var incomplete=await db.BeginScanGenerationAsync(pair.Id,localRoot,"local","manual","""{"scope":"complete"}""");
            Require(await db.LoadBothAbsentRetirementHoldAsync(id)=="two_fresh_complete_observations_required",
                "new incomplete observation blocks imported absence retirement");
            await db.CompleteScanGenerationAsync(incomplete);
            Require(await db.LoadBothAbsentRetirementHoldAsync(id)!="two_fresh_complete_observations_required",
                "imported second fresh complete observation plus actual completed continuation satisfies the observation check");
        }

        var clock=DateTimeOffset.UtcNow.AddDays(91);
        db.ActivityRetentionClockForTest=()=>clock;
        for(var i=0;i<8;i++)await db.RetireSettledActionBatchAsync();
        clock=clock.AddDays(8);
        for(var i=0;i<16;i++)await db.RetireSettledActionBatchAsync();
        for(var i=0;i<16;i++)await db.RetireInactiveConsentBatchAsync();
        for(var i=0;i<16;i++)await db.RetireRunSummaryBatchAsync();
        var held=await db.LoadBothAbsentRetirementHoldAsync(id);
        if(held!="")throw new InvalidDataException("The complete absence fixture still has a required consumer: "+held);
        Require(true,"expired original action and selection closure releases this exact absent object");

        var interrupted=false;
        db.ActivityRetirementBoundaryForTest=phase=>
        { if(phase=="exact-both-absent-before-delete")throw new IOException("fixture absence crash"); };
        for(var i=0;i<8&&!interrupted;i++)
        {
            var before=await ComponentCounts();
            try { await db.RetireOrdinaryActivityBatchAsync(); }
            catch(IOException)
            {
                interrupted=true;
                Require(await ComponentCounts()==before,"interrupted absence collection rolls back the complete attempted metadata transaction");
            }
        }
        if(!interrupted)throw new InvalidDataException("The exact absence deletion boundary was not reached.");
        db.ActivityRetirementBoundaryForTest=null;
        var completed=false;
        for(var i=0;i<120;i++)
        {
            if(i%20==0)clock=clock.AddDays(8);
            var result=await db.RetireOrdinaryActivityBatchAsync();
            if((result.Objects?.ExaminedRows??0)>60)throw new InvalidDataException("Object/absence cleanup exceeded its bounded row allowance.");
            if(await ScalarAsync(dbPath,"SELECT count(*) FROM logical_object WHERE object_id=$object;",
                    ("$object",point.ObjectId))=="0"){completed=true;break;}
        }
        if(!completed)
        {
            var status=(await db.LoadActivityRetentionWindowAsync()).Objects!;
            throw new InvalidDataException("The proved obsolete component did not drain: "+status.BothAbsentHold+" "+await ComponentCounts());
        }
        Require(await ScalarAsync(dbPath,"SELECT count(*) FROM tombstone WHERE tombstone_id=$id;",("$id",id))=="0" &&
            await ScalarAsync(dbPath,"SELECT count(*) FROM membership WHERE object_id=$object;",("$object",point.ObjectId))=="0" &&
            await ScalarAsync(dbPath,"SELECT count(*) FROM object_version WHERE object_id=$object;",("$object",point.ObjectId))=="0",
            "bounded idle collection removes the obsolete point, tombstone, memberships, versions and logical object");
        Require(!await db.IsPlanningAuthorityCurrentAsync(oldAuthority),
            "a captured pre-retirement planning authority cannot revive the removed baseline");
        Require((rig.Remote.UploadCount,rig.Remote.DownloadCount,rig.Remote.DeleteCount,rig.Remote.MoveCount)==effects,
            "metadata retirement performs no provider transfer, move or deletion");
        var window=(await db.LoadActivityRetentionWindowAsync()).Objects!;
        Require(window.BothAbsentExpiredObjects==1&&window.BothAbsentExpiredRows>0,
            "shared presentation reports the exact retired both-absent object and metadata totals");
        await db.VerifyHealthAsync();
        Require(true,"both-absent collection preserves every foreign key");
        if(successorOnly)
        {
            Require(await ScalarAsync(dbPath,"SELECT count(*) FROM consent_header;")=="0" &&
                await ScalarAsync(dbPath,"SELECT count(*) FROM command_request WHERE command_kind IN ('run_pair','run_all');")=="0",
                "expired original selection and old-generation run summary retire after their consumers drain");
            Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(originalPath!)))==originalHash,
                "original source stays byte-exact throughout successor absence retirement");
            return;
        }
        await File.WriteAllTextAsync(file,"new independent content");
        rig.Remote.PutWithId("retire.txt","new independent content","id:after-retirement");
        var fresh=await rig.Host.RunPairsAsync([pair],"reappear-after-retirement");
        Require(fresh.Items.All(item=>item.FailureMessage is null) &&
            (await db.LoadLatestSynchronizationPointsAsync(pair.Id)).Single().SynchronizationPointId!=point.SynchronizationPointId,
            "a real fresh reappearance converges after old metadata retirement without reusing old authority");

        Task<string> ComponentCounts()=>ScalarAsync(dbPath,"""
            SELECT json_array(
              (SELECT count(*) FROM logical_object WHERE object_id=$object),
              (SELECT count(*) FROM synchronization_point WHERE object_id=$object),
              (SELECT count(*) FROM reconciliation_decision WHERE object_id=$object),
              (SELECT count(*) FROM tombstone WHERE object_id=$object));
            """,("$object",point.ObjectId));
    }

    internal static async Task RunBothAbsentPlateauAsync(string root, bool verifyDependencyHold=false)
    {
        using var rig=await RigAsync(root,true);
        var pair=await BindAsync(rig,"Both absent plateau",rig.Local,"/AbsentPlateau","id:absent-plateau");
        var path=IOPath.Combine(rig.Local,"gone.txt");
        await File.WriteAllTextAsync(path,"first episode");
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,pair));
        pair=(await rig.Host.LoadPairsAsync()).Pairs.Single(item=>item.Id==pair.Id);
        var firstPoint=(await rig.Database.LoadLatestSynchronizationPointsAsync(pair.Id)).Single();
        File.Delete(path);rig.Remote.Delete("gone.txt");
        var effects=(rig.Remote.UploadCount,rig.Remote.DownloadCount,rig.Remote.DeleteCount,rig.Remote.MoveCount);
        await rig.Host.RunPairsAsync([pair],"absence-first");
        Require(await ScalarAsync(rig.DatabasePath,
            "SELECT count(*) FROM tombstone WHERE absent_side='both' AND both_absent_closed_utc IS NOT NULL AND json_extract(both_absent_basis_json,'$.PointId')=$point;",
            ("$point", firstPoint.SynchronizationPointId)) == "1",
            "real complete absence binds one immutable baseline, roots, database generation and original deletion policy");
        Require(await RefusedSqlAsync(() => ExecuteAsync(rig.DatabasePath,
            "UPDATE tombstone SET both_absent_closed_utc='2000-01-01T00:00:00Z' WHERE absent_side='both';")),
            "ordinary SQL cannot backdate or replace both-absence closure proof");
        var before=await ScalarAsync(rig.DatabasePath,"""
            SELECT json_group_array(json_object('id',tombstone_id,'utc',observed_absent_utc,'basis',both_absent_basis_json,'closed',both_absent_closed_utc))
            FROM tombstone WHERE pair_id=$pair AND absent_side='both';
            """,("$pair",pair.Id));
        var decisionBefore=await ScalarAsync(rig.DatabasePath,"""
            SELECT json_group_array(json_object('id',reconciliation_decision_id,'local',local_scan_generation_id,
                'remote',dropbox_scan_generation_id,'json',decision_json,'utc',decided_utc))
            FROM reconciliation_decision WHERE pair_id=$pair AND json_extract(decision_json,'$.isBothAbsentConfirmation')=1;
            """,("$pair",pair.Id));
        var absenceId=(await ScalarAsync(rig.DatabasePath,
            "SELECT tombstone_id FROM tombstone WHERE pair_id=$pair AND absent_side='both';",("$pair",pair.Id)))!;
        Require(await rig.Database.LoadBothAbsentRetirementHoldAsync(absenceId)=="ninety_day_grace",
            "both-absence metadata remains protected throughout its ninety-day grace");
        rig.Database.ActivityRetentionClockForTest=()=>DateTimeOffset.UtcNow.AddDays(91);
        Require(await rig.Database.LoadBothAbsentRetirementHoldAsync(absenceId)=="two_fresh_complete_observations_required",
            "closure observations cannot count as the two later complete confirmations");
        rig.Database.ActivityRetentionClockForTest=null;
        for(var i=0;i<3;i++)await rig.Host.RunPairsAsync([pair],"absence-repeat-"+i);
        rig.Database.ActivityRetentionClockForTest=()=>DateTimeOffset.UtcNow.AddDays(91);
        Require(await rig.Database.LoadBothAbsentRetirementHoldAsync(absenceId)=="retained_operation",
            "fresh confirmed absence never discards a retained exact operation graph");
        var retainedPairJson=await ScalarAsync(rig.DatabasePath,"SELECT pair_json FROM sync_pair WHERE pair_id=$pair;",("$pair",pair.Id));
        var excludedPair=JsonSerializer.Deserialize<SyncPair>(retainedPairJson)! with { SelectionDefaultIncluded=false };
        await ExecuteAsync(rig.DatabasePath,"UPDATE sync_pair SET pair_json=$json WHERE pair_id=$pair;",
            ("$json",JsonSerializer.Serialize(excludedPair)),("$pair",pair.Id));
        Require(await rig.Database.LoadBothAbsentRetirementHoldAsync(absenceId)=="outside_current_selection",
            "current selection exclusion remains a hold even with unchanged root and policy revision");
        await ExecuteAsync(rig.DatabasePath,"UPDATE sync_pair SET pair_json=$json WHERE pair_id=$pair;",
            ("$json",retainedPairJson),("$pair",pair.Id));
        var failureRoot=(await rig.Database.LoadActiveRootBindingIdAsync(pair.Id,"local"))!;
        var failed=await rig.Database.BeginScanGenerationAsync(pair.Id,failureRoot,"local","manual", """{"scope":"complete"}""");
        await rig.Database.CloseFailedScanGenerationsAsync([failed]);
        Require(await rig.Database.LoadBothAbsentRetirementHoldAsync(absenceId)=="two_fresh_complete_observations_required",
            "a newer failed scan cannot be skipped to manufacture two successful absence confirmations");
        rig.Database.ActivityRetentionClockForTest=null;

        if(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM tombstone WHERE pair_id=$pair AND absent_side='both';",
               ("$pair",pair.Id))!="1"||
           before!=await ScalarAsync(rig.DatabasePath,"""
               SELECT json_group_array(json_object('id',tombstone_id,'utc',observed_absent_utc,'basis',both_absent_basis_json,'closed',both_absent_closed_utc))
               FROM tombstone WHERE pair_id=$pair AND absent_side='both';
               """,("$pair",pair.Id))||
           decisionBefore!=await ScalarAsync(rig.DatabasePath,"""
               SELECT json_group_array(json_object('id',reconciliation_decision_id,'local',local_scan_generation_id,
                   'remote',dropbox_scan_generation_id,'json',decision_json,'utc',decided_utc))
               FROM reconciliation_decision WHERE pair_id=$pair AND json_extract(decision_json,'$.isBothAbsentConfirmation')=1;
               """,("$pair",pair.Id)))
            throw new InvalidDataException("Repeated absence grew or rebound the first retained episode evidence.");
        if(effects!=(rig.Remote.UploadCount,rig.Remote.DownloadCount,rig.Remote.DeleteCount,rig.Remote.MoveCount))
            throw new InvalidDataException("Repeated confirmed absence produced a provider effect.");
        Console.WriteLine("PASS repeated real Host both-absent scans retain exactly one tombstone and byte-exact first decision without provider effects");
        await File.WriteAllTextAsync(path,"new episode");rig.Remote.PutWithId("gone.txt","new episode","id:recreated-absence-file");
        var localRoot=(await rig.Database.LoadActiveRootBindingIdAsync(pair.Id,"local"))!;
        var remoteRoot=(await rig.Database.LoadActiveRootBindingIdAsync(pair.Id,"dropbox"))!;
        var observer=new SyncAuthorityObservationCoordinator(pair,rig.Remote,rig.Database,localRoot,remoteRoot);
        await observer.ObserveAsync("manual");
        if((await rig.Database.LoadLatestSynchronizationPointsAsync(pair.Id)).Single().SynchronizationPointId!=
           firstPoint.SynchronizationPointId)
            throw new InvalidDataException("Observation alone superseded accepted baseline authority.");
        Console.WriteLine("PASS observing replacement identities alone preserves the old accepted baseline");
        var reappeared=await rig.Host.RunPairsAsync([pair],"absence-reappeared");
        if(reappeared.Items.Any(item=>item.FailureMessage is not null))
            throw new InvalidDataException("The recreated file did not reach accepted convergence.");
        var current=(await rig.Database.LoadLatestSynchronizationPointsAsync(pair.Id)).Single();
        if(current.SynchronizationPointId==firstPoint.SynchronizationPointId||
           await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM synchronization_point WHERE pair_id=$pair;",
               ("$pair",pair.Id))!="2")
            throw new InvalidDataException("Current baseline selection discarded old evidence or selected the old identity.");
        var storage=await rig.Database.LoadStorageSizeSummaryAsync(pair.Id);
        if(storage.Files!=1||storage.Bytes!=Encoding.UTF8.GetByteCount("new episode"))
            throw new InvalidDataException("Current storage presentation counted a superseded path twice.");
        var localScan=await rig.Database.LoadLatestCompleteScanIdAsync(pair.Id,"local");
        var remoteScan=await rig.Database.LoadLatestCompleteScanIdAsync(pair.Id,"dropbox");
        var observed=await rig.Database.LoadPlanningObservationSetAsync(pair.Id,pair.PolicyRevision,localRoot,remoteRoot,
            localScan!,remoteScan!);
        var stale=observed.Bind("fresh-cut-with-old-point") with {
            ObjectId=firstPoint.ObjectId,SynchronizationPointId=firstPoint.SynchronizationPointId };
        if(await rig.Database.IsPlanningAuthorityCurrentAsync(stale))
            throw new InvalidDataException("A fresh cut revived superseded per-object baseline authority.");
        Console.WriteLine("PASS accepted replacement selects one current path, retains both historic points and rejects old authority under a fresh cut");
        File.Delete(path);rig.Remote.Delete("gone.txt");
        await rig.Host.RunPairsAsync([pair],"absence-second-episode");
        if(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM tombstone WHERE pair_id=$pair AND absent_side='both';",
               ("$pair",pair.Id))!="2")
            throw new InvalidDataException("A newly accepted baseline reused the earlier absence identity.");
        await rig.Database.VerifyHealthAsync();
        Console.WriteLine("PASS real reappearance advances the accepted baseline and subsequent absence retains a distinct episode");
        Require(await RefusedSqlAsync(()=>ExecuteAsync(rig.DatabasePath,
            "UPDATE tombstone SET object_id=$new WHERE object_id=$old AND both_absent_basis_json IS NOT NULL;",
            ("$new",current.ObjectId),("$old",firstPoint.ObjectId))),
            "proved absence identity cannot be reassigned to another valid object");

        if(verifyDependencyHold)
        {
            await rig.Host.RunPairsAsync([pair],"dominance-absence-confirm-one");
            await rig.Host.RunPairsAsync([pair],"dominance-absence-confirm-two");
            await rig.Database.EnableActivityRetentionForTestAsync();
            var retirementClock=DateTimeOffset.UtcNow.AddDays(91);
            rig.Database.ActivityRetentionClockForTest=()=>retirementClock;
            for(var i=0;i<8;i++)await rig.Database.RetireSettledActionBatchAsync();
            retirementClock=retirementClock.AddDays(8);
            for(var i=0;i<16;i++)await rig.Database.RetireSettledActionBatchAsync();
            for(var i=0;i<16;i++)await rig.Database.RetireInactiveConsentBatchAsync();
            for(var i=0;i<16;i++)await rig.Database.RetireRunSummaryBatchAsync();
            await rig.Database.RecordConflictAsync("retained-old-baseline-conflict",pair.Id,firstPoint.ObjectId,
                "Fixture retained safety consumer",firstPoint.LocalObjectVersionId!,firstPoint.LocalMembershipId,
                firstPoint.DropboxObjectVersionId!,firstPoint.DropboxMembershipId);
            var dependencyHeld=false;
            for(var i=0;i<40&&!dependencyHeld;i++)
            {
                await rig.Database.RetireOrdinaryActivityBatchAsync();
                dependencyHeld=(await rig.Database.LoadActivityRetentionWindowAsync()).Objects?.BothAbsentHold==
                    "accepted_point_protects_older_baseline";
            }
            Require(dependencyHeld &&
                (await rig.Database.LoadLatestSynchronizationPointsAsync(pair.Id)).Single().SynchronizationPointId==
                    current.SynchronizationPointId,
                "a safety-held older object keeps its dominating accepted point and cannot become current again");
            await rig.Database.VerifyHealthAsync();
        }

    }

    internal static async Task RunCrossPairWindowContinuationAsync(string root)
    {
        var sourceRemote=new FakeSyncRemoteStore { ExactPathMetadataForTest=true };
        var destinationRemote=new FakeSyncRemoteStore { ExactPathMetadataForTest=true };
        using var rig=await RigAsync(root,true,sourceRemote,
            pair=>pair.DropboxFolderId=="id:window-cross-destination"?destinationRemote:sourceRemote);
        var source=await BindAsync(rig,"Cross window source",rig.Local,"/WindowCrossSource","id:window-cross-source");
        var destination=await BindAsync(rig,"Cross window destination",IOPath.Combine(root,"destination"),
            "/WindowCrossDestination","id:window-cross-destination");
        foreach(var path in new[]{"first.txt","second.txt"})
        {
            await File.WriteAllTextAsync(IOPath.Combine(source.LocalRoot,path),"payload "+path);
        }
        await File.WriteAllTextAsync(IOPath.Combine(destination.LocalRoot,"anchor.txt"),"anchor");
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,source));
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,destination));
        var saved=(await rig.Host.LoadPairsAsync()).Pairs;
        source=saved.Single(item=>item.Id==source.Id);destination=saved.Single(item=>item.Id==destination.Id);
        await rig.Host.RunPairsAsync([source,destination],"cross-window-stable-baseline");
        foreach(var path in new[]{"first.txt","second.txt"})
            File.Move(IOPath.Combine(source.LocalRoot,path),IOPath.Combine(destination.LocalRoot,path));
        await ExecuteAsync(rig.DatabasePath,"""
            WITH RECURSIVE positions(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM positions WHERE n<254)
            INSERT INTO operation(operation_id,kind,status,priority,configuration_revision,capability_profile_id,
                idempotency_key,preconditions_json,created_utc)
            SELECT 'occupied-cross-'||n,'create_remote','planned',50,1,
                (SELECT capability_profile_id FROM capability_profile LIMIT 1),
                'occupied-cross-'||n,'{}','2026-01-01T00:00:00Z' FROM positions;
            """);
        var transfers=sourceRemote.UploadCount+sourceRemote.DownloadCount+
            destinationRemote.UploadCount+destinationRemote.DownloadCount;
        var operationCount=int.Parse(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;"));
        var rounds=0;var firstRoundReserved=0;var firstRoundOrdinary=0;
        rig.Host.CoordinatedPlanningPhaseForTest=async phase=>
        {
            if(phase=="after-cross-pair-reservations-before-ordinary-planning"&&++rounds==1)
            {
                firstRoundReserved=int.Parse(await ScalarAsync(rig.DatabasePath,
                    "SELECT count(*) FROM operation WHERE kind='cross_pair_move' AND status NOT IN ('completed','cancelled','superseded');"));
                // Reproduce another writer releasing capacity at the exact phase
                // boundary; deferred cross-pair objects still require a fresh cut.
                await ExecuteAsync(rig.DatabasePath,"UPDATE operation SET status='cancelled',terminal_utc='2026-01-02T00:00:00Z' WHERE operation_id LIKE 'occupied-cross-%';");
            }
            if(phase=="after-ordinary-planning-before-dispatch"&&rounds==1)
                firstRoundOrdinary=int.Parse(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;"))-
                    operationCount-firstRoundReserved;
        };
        SyncAuthorityBatchResult? result=null;
        try{result=await rig.Host.RunPairsAsync([source,destination],"cross-window-release-between-phases");}
        finally{rig.Host.CoordinatedPlanningPhaseForTest=null;}
        if(firstRoundReserved!=1||firstRoundOrdinary!=0||rounds<2)
            throw new InvalidDataException($"Cross-pair capacity boundary lost: reserved={firstRoundReserved}, ordinary={firstRoundOrdinary}, rounds={rounds}.");
        Console.WriteLine("PASS real Host defers ordinary planning when cross-pair window fills even after capacity releases between phases");
        if(result.Items.Any(item=>item.FailureMessage is not null)||sourceRemote.MoveCount!=2||
           transfers!=sourceRemote.UploadCount+sourceRemote.DownloadCount+destinationRemote.UploadCount+destinationRemote.DownloadCount||
           sourceRemote.DeleteCount!=0||destinationRemote.DeleteCount!=0)
            throw new InvalidDataException("Cross-pair continuation failed or substituted copy/delete effects.");
        foreach(var path in new[]{"first.txt","second.txt"})
            if(sourceRemote.Exists(path)||destinationRemote.ReadText(path)!="payload "+path)
                throw new InvalidDataException("Cross-pair continuation lost an exact original file.");
        Console.WriteLine("PASS real Host fresh continuation completes both native cross-pair moves once with no transfer or delete substitution");
        await rig.Host.RunPairsAsync([source,destination],"cross-window-noop");
        if(sourceRemote.MoveCount!=2)throw new InvalidDataException("Cross-pair no-op replayed the successful prefix.");
        await rig.Database.VerifyHealthAsync();
        Console.WriteLine("PASS real Host subsequent no-op preserves successful cross-pair prefix and database health");
    }

    internal static async Task RunOrdinaryWindowBackpressureAsync(string root,bool standalone=false)
    {
        Directory.CreateDirectory(root);
        using var rig=await RigAsync(root,fresh:true);
        var pair=await BindAsync(rig,"Window backpressure",rig.Local,"/WindowBackpressure","id:window-backpressure");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local,"seed.txt"),"seed original");
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,pair));
        pair=(await rig.Host.LoadPairsAsync()).Pairs.Single(value=>value.Id==pair.Id);
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local,"waiting.txt"),"waiting original");
        var peakLoaded=0;
        async Task<SyncRunResult> RunStandalone()
        {
            var local=await rig.Database.LoadActiveRootBindingIdAsync(pair.Id,"local");
            var remote=await rig.Database.LoadActiveRootBindingIdAsync(pair.Id,"dropbox");
            var profile=await ScalarAsync(rig.DatabasePath,
                "SELECT capability_profile_id FROM operation WHERE pair_id=$pair LIMIT 1;",("$pair",pair.Id));
            var recovery=await ScalarAsync(rig.DatabasePath,
                "SELECT recovery_root_binding_id FROM recovery_root_binding WHERE status='active' LIMIT 1;");
            var options=new SyncAuthorityPlannerOptions(pair.PolicyRevision,profile,pair.ControlNamespace!.NamespaceId,
                recovery,EnableMoveEffects:true);
            var admission=await rig.Database.CreateAcceptedCommandAdmissionAsync(null,"run_pair",pair.Id,null,null,
                "{}",SyncAuthorityCommandPurpose.NormalEffect);
            await rig.Database.TransitionCommandRequestAsync(admission.CommandRequestId,"executing");
            SyncAuthorityPairRunner.PlannedOperationBatchReadForTest=(index,id)=>
            {peakLoaded=Math.Max(peakLoaded,index+1);return Task.CompletedTask;};
            try{return await new SyncAuthorityPairRunner(pair,rig.Remote,rig.Database,local,remote,options,
                new PilotRunControl(),admission).RunAsync("manual");}
            finally{SyncAuthorityPairRunner.PlannedOperationBatchReadForTest=null;}
        }

        await ExecuteAsync(rig.DatabasePath,"""
            WITH RECURSIVE positions(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM positions WHERE n<255)
            INSERT INTO operation(operation_id,kind,status,priority,configuration_revision,capability_profile_id,
                idempotency_key,preconditions_json,created_utc)
            SELECT 'occupied-'||n,'create_remote','planned',50,1,
                (SELECT capability_profile_id FROM capability_profile LIMIT 1),
                'occupied-'||n,'{}','2026-01-01T00:00:00Z' FROM positions;
            """);
        var uploads=rig.Remote.UploadCount;
        var actions=await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;");
        if(standalone)
        {
            var blocked=await RunStandalone();
            if(blocked.Failed!=0 || blocked.NotAttempted<1)
                throw new InvalidDataException("Standalone capacity wait became a failed file.");
        }
        else
        {
        await rig.Host.RunPairsAsync([pair],"ordinary-capacity-full-fixture");
        var waiting=(await rig.Database.LoadRunHistoryAsync(20)).First(run=>
            run.RequestJson.Contains("ordinary-capacity-full-fixture",StringComparison.Ordinal));
        using(var parsed=JsonDocument.Parse(waiting.ResultJson!))
        {
            var item=parsed.RootElement.GetProperty("pairs")[0];
            if(item.GetProperty("failureType").ValueKind!=JsonValueKind.Null ||
                item.GetProperty("failed").GetInt32()!=0 || item.GetProperty("notAttempted").GetInt32()<1)
                throw new InvalidDataException("Window capacity was not retained as normal unattempted work: "+waiting.ResultJson);
        }
        }
        if(rig.Remote.UploadCount!=uploads || rig.Remote.Exists("waiting.txt") ||
            await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;")!=actions)
            throw new InvalidDataException("A full window admitted a new file effect or journal.");
        Console.WriteLine("PASS actual Host full window records waiting/unattempted work with zero failed files and no new operation");
        await ExecuteAsync(rig.DatabasePath,"UPDATE operation SET status='cancelled',terminal_utc='2026-01-02T00:00:00Z' WHERE operation_id LIKE 'occupied-%';");
        if(standalone)await RunStandalone();
        else await rig.Host.RunPairsAsync([pair],"ordinary-capacity-released-fixture");
        if(rig.Remote.UploadCount!=uploads+1 || rig.Remote.ReadText("waiting.txt")!="waiting original")
            throw new InvalidDataException("Releasing capacity lost or duplicated the waiting file.");
        Console.WriteLine("PASS next actual Host run completes the original waiting file once after capacity releases");
        if(standalone)
        {
            for(var i=0;i<33;i++)await File.WriteAllTextAsync(IOPath.Combine(rig.Local,$"stream-{i:D2}.txt"),"stream "+i);
            var streamed=await RunStandalone();
            if(streamed.Uploaded!=33 || peakLoaded>16 || rig.Remote.UploadCount!=uploads+34)
                throw new InvalidDataException("Standalone shared streaming lost files or exceeded its loaded window.");
            await RunStandalone();
            if(rig.Remote.UploadCount!=uploads+34)throw new InvalidDataException("Standalone no-op replayed effects.");
            Console.WriteLine("PASS standalone shared planner streams33 files with peak16 and no-op without replay");
        }
        await rig.Database.VerifyHealthAsync();
    }


    internal static async Task RunFolderPlanWindowsAsync(string root,int folderCount,bool performance=false)
    {
        Directory.CreateDirectory(root);
        using var rig=await RigAsync(root,fresh:true);
        if(performance)await rig.Host.SaveTransferSettingsAsync(new SyncTransferSettings(
            ResourceMode:"Performance",MaximumConcurrentFiles:16));
        var pair=await BindAsync(rig,"Folder windows",rig.Local,"/FolderWindows","id:folder-windows");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local,"seed.txt"),"seed");
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,pair));
        pair=(await rig.Host.LoadPairsAsync()).Pairs.Single(value=>value.Id==pair.Id);
        var beforeUploads=rig.Remote.UploadCount;var beforeDirectories=rig.Remote.DirectoryCreateCount;
        for(var index=0;index<folderCount;index++)
        {
            var path=IOPath.Combine(rig.Local,$"folder-{index:D4}");
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(IOPath.Combine(path,"child.txt"),"child "+index);
        }
        var resourceGate=new object();var minimumAllowed=int.MaxValue;var peakActive=0;
        var resourceReasons=new HashSet<string>(StringComparer.Ordinal);
        SyncAuthorityCreateRemoteExecutor.PhaseForTest=phase=>
        {
            var status=rig.Host.TransferResourceStatus;
            lock(resourceGate)
            {
                minimumAllowed=Math.Min(minimumAllowed,status.AllowedFiles);
                peakActive=Math.Max(peakActive,status.ActiveFiles);
                if(resourceReasons.Count<16)resourceReasons.Add(status.Reason);
            }
        };
        var beforeScans=long.Parse(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM scan_generation;"));
        var windows=0;var emptyWindows=0;var peakLoaded=0;var peakReady=0;
        SyncAuthorityPairRunner.PlannedOperationBatchReadForTest=(index,id)=>
        {peakLoaded=Math.Max(peakLoaded,index+1);return Task.CompletedTask;};
        rig.Host.CoordinatedPlanningPhaseForTest=async phase=>
        {
            if(phase!="after-ordinary-planning-before-dispatch")return;
            var ready=int.Parse(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation WHERE status='ready';"));
            if(ready==0){emptyWindows++;return;}
            windows++;
            peakReady=Math.Max(peakReady,ready);
        };
        var timings=new System.Collections.Concurrent.ConcurrentDictionary<string,(long Count,double Total,double Max)>();
        SyncAuthorityDatabase.TimingForTest=(phase,ms)=>timings.AddOrUpdate(phase,(1,ms,ms),
            (_,old)=>(old.Count+1,old.Total+ms,Math.Max(old.Max,ms)));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try{await rig.Host.RunPairsAsync([pair],"folder-frontier-fixture");}
        finally
        {
            SyncAuthorityPairRunner.PlannedOperationBatchReadForTest=null;
            rig.Host.CoordinatedPlanningPhaseForTest=null;
            SyncAuthorityDatabase.TimingForTest=null;
            SyncAuthorityCreateRemoteExecutor.PhaseForTest=null;
        }
        var run=(await rig.Database.LoadRunHistoryAsync(20)).First(item=>
            item.RequestJson.Contains("folder-frontier-fixture",StringComparison.Ordinal));
        await File.WriteAllTextAsync(IOPath.Combine(root,"window-timing.json"),JsonSerializer.Serialize(new {
            folderCount,windows,emptyWindows,peakLoaded,peakReady,elapsedMs=watch.ElapsedMilliseconds,
            settings=rig.Host.LoadTransferSettings(),minimumAllowed,peakActive,resourceReasons,
            timings=timings.OrderByDescending(item=>item.Value.Total).ToDictionary(item=>item.Key,
                item=>new {count=item.Value.Count,totalMs=item.Value.Total,maxMs=item.Value.Max})
        },new JsonSerializerOptions{WriteIndented=true}));
        if(rig.Remote.DirectoryCreateCount-beforeDirectories!=folderCount ||
            rig.Remote.UploadCount-beforeUploads!=folderCount)
            throw new InvalidDataException("Folder frontier did not complete all originals: "+run.ResultJson);
        for(var index=0;index<folderCount;index++)
            if(!rig.Remote.DirectoryExists($"folder-{index:D4}") ||
                rig.Remote.ReadText($"folder-{index:D4}/child.txt")!="child "+index)
                throw new InvalidDataException("Folder frontier bytes differ.");
        var scans=long.Parse(await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM scan_generation;"))-beforeScans;
        if(peakLoaded>16 || peakReady>16 || windows<4 || windows>2*((folderCount+15)/16) || scans>6)
            throw new InvalidDataException($"Unbounded frontier or capacity-driven rescan: loaded={peakLoaded},ready={peakReady},windows={windows},scans={scans}.");
        using(var result=JsonDocument.Parse(run.ResultJson!))
            if(result.RootElement.GetProperty("pairs")[0].GetProperty("failed").GetInt32()!=0)
                throw new InvalidDataException("Deferred children were represented as failed files.");
        Console.WriteLine($"PASS {folderCount} folders and children complete across {windows} windows; peak loaded/ready {peakLoaded}/{peakReady}; {scans} scans; {watch.ElapsedMilliseconds}ms");
        var operations=await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;");
        await rig.Host.RunPairsAsync([pair],"folder-frontier-noop");
        if(rig.Remote.UploadCount-beforeUploads!=folderCount ||
            rig.Remote.DirectoryCreateCount-beforeDirectories!=folderCount ||
            await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;")!=operations)
            throw new InvalidDataException("Folder frontier no-op replayed a completed effect.");
        Console.WriteLine("PASS folder continuation defers children until fresh parent observation and no-op does not replay");
        await rig.Database.VerifyHealthAsync();
    }

    internal static async Task RunOrdinaryPlanWindowAsync(string root,int pathCount,bool stopBetweenWindows=false)
    {
        Directory.CreateDirectory(root);
        using var rig=await RigAsync(root,fresh:true);
        var pair=await BindAsync(rig,"Ordinary window",rig.Local,"/OrdinaryWindow","id:ordinary-window");
        await File.WriteAllTextAsync(IOPath.Combine(rig.Local,"seed.txt"),"seed original");
        await rig.Host.RunBootstrapActionPlanAsync(await ReviewAsync(rig,pair));
        pair=(await rig.Host.LoadPairsAsync()).Pairs.Single(value=>value.Id==pair.Id);
        if(pair.InitialReviewApproved!=true)throw new InvalidDataException("Fixture seed review did not approve the pair.");
        var beforeUploads=rig.Remote.UploadCount;var beforeDownloads=rig.Remote.DownloadCount;
        for(var i=0;i<pathCount;i++)
        {
            var path=$"file-{i:D5}.txt";var content="window payload "+i;
            if(i%2==0)await File.WriteAllTextAsync(IOPath.Combine(rig.Local,path),content);
            else rig.Remote.Put(path,content);
        }
        using var windowCancellation=new CancellationTokenSource();
        var peakLoaded=0;var peakReady=0;var windows=0;
        SyncAuthorityPairRunner.PlannedOperationBatchReadForTest=(index,id)=>
        {peakLoaded=Math.Max(peakLoaded,index+1);return Task.CompletedTask;};
        rig.Host.CoordinatedPlanningPhaseForTest=async phase=>
        {
            if(phase!="after-ordinary-planning-before-dispatch")return;
            windows++;
            if(stopBetweenWindows && windows==3) windowCancellation.Cancel();
            peakReady=Math.Max(peakReady,int.Parse((await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation WHERE status IN ('planned','ready','leased','validating','allocation_authorized','staged','effect_issuing');"))!));
        };
        var timings=new System.Collections.Concurrent.ConcurrentDictionary<string,(long Count,double Total,double Max)>();
        var slowSchemaStacks=new System.Collections.Concurrent.ConcurrentDictionary<string,(long Count,double Total)>();
        SyncAuthorityDatabase.TimingForTest=(phase,ms)=>
        {
            timings.AddOrUpdate(phase,(1,ms,ms),(_,old)=>(old.Count+1,old.Total+ms,Math.Max(old.Max,ms)));
            if(phase=="schema-inventory" && ms>20)
            {
                var frames=new System.Diagnostics.StackTrace(false).GetFrames()
                    .Select(frame=>frame.GetMethod()).Where(method=>method?.DeclaringType?.Namespace=="Massdex.SyncShark")
                    .Select(method=>method!.DeclaringType!.Name+"."+method.Name).Take(12);
                slowSchemaStacks.AddOrUpdate(string.Join(" > ",frames),(1,ms),(_,old)=>(old.Count+1,old.Total+ms));
            }
        };
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try { await rig.Host.RunPairsAsync([pair],"ordinary-window-fixture",cancellationToken:windowCancellation.Token); }
        catch(OperationCanceledException) when(stopBetweenWindows && windowCancellation.IsCancellationRequested) { }
        finally
        {
            SyncAuthorityPairRunner.PlannedOperationBatchReadForTest=null;
            rig.Host.CoordinatedPlanningPhaseForTest=null;
            SyncAuthorityDatabase.TimingForTest=null;
        }
        var elapsed=watch.ElapsedMilliseconds;
        if(stopBetweenWindows)
        {
            var completed=rig.Remote.UploadCount-beforeUploads+rig.Remote.DownloadCount-beforeDownloads;
            if(!windowCancellation.IsCancellationRequested || completed<=0 || completed>=pathCount)
                throw new InvalidDataException("Stop did not occur after earlier windows drained.");
            var closed=(await rig.Database.LoadRunHistoryAsync(20)).First(run=>run.RequestJson.Contains("ordinary-window-fixture",StringComparison.Ordinal));
            if(closed.ResultJson is null || !closed.ResultJson.Contains("Stopped",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Stopped ordinary run lost its stopped outcome.");
            Console.WriteLine($"PASS stop between ordinary windows retains {completed} completed transfers and stops further dispatch");
            await rig.Host.RunPairsAsync([pair],"ordinary-window-resume-fixture");
            var resumed=(await rig.Database.LoadRunHistoryAsync(20)).First(run=>run.RequestJson.Contains("ordinary-window-resume-fixture",StringComparison.Ordinal));
            if(rig.Remote.UploadCount-beforeUploads+rig.Remote.DownloadCount-beforeDownloads!=pathCount)
                throw new InvalidDataException("Resume did not finish the remainder: "+resumed.ResultJson);
            Console.WriteLine("PASS a fresh ordinary run resumes the unvisited remainder after drained Stop");
        }
        if(rig.Remote.UploadCount-beforeUploads!=(pathCount+1)/2 || rig.Remote.DownloadCount-beforeDownloads!=pathCount/2)
            throw new InvalidDataException($"Ordinary window transfer counts differ: uploads {rig.Remote.UploadCount-beforeUploads}, downloads {rig.Remote.DownloadCount-beforeDownloads}, expected {pathCount}.");
        for(var i=0;i<pathCount;i++)
        {
            var path=$"file-{i:D5}.txt";var expected="window payload "+i;
            if(await File.ReadAllTextAsync(IOPath.Combine(rig.Local,path))!=expected || rig.Remote.ReadText(path)!=expected)
                throw new InvalidDataException("Window output bytes differ for "+path);
        }
        if(windows<=1 || peakLoaded>16 || peakReady>16)
            throw new InvalidDataException($"Ordinary planning was not bounded: windows {windows}, loaded {peakLoaded}, ready {peakReady}.");
        Console.WriteLine($"PASS actual Host ordinary mixed transfers: {pathCount} paths, {windows} windows, peak loaded {peakLoaded}, peak ready {peakReady}, {elapsed}ms");
        var normal=(await rig.Database.LoadRunHistoryAsync(50)).First(run=>run.RequestJson.Contains("ordinary-window-fixture",StringComparison.Ordinal));
        using(var result=JsonDocument.Parse(normal.ResultJson!))
            if(result.RootElement.GetProperty("pairs").EnumerateArray().Any(value=>value.TryGetProperty("files",out _)))
                throw new InvalidDataException("Ordinary window retained an unbounded per-file summary.");
        Console.WriteLine("PASS streamed outcomes retain the existing compact normal-run summary");
        var operations=await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;");
        var uploads=rig.Remote.UploadCount;var downloads=rig.Remote.DownloadCount;
        await rig.Host.RunPairsAsync([pair],"ordinary-window-noop-fixture");
        if(rig.Remote.UploadCount!=uploads || rig.Remote.DownloadCount!=downloads ||
            await ScalarAsync(rig.DatabasePath,"SELECT count(*) FROM operation;")!=operations)
            throw new InvalidDataException("Repeated ordinary window run replayed work or grew the action journal.");
        Console.WriteLine("PASS repeated no-op does not transfer again or create another action journal");
        await rig.Database.VerifyHealthAsync();
        await File.WriteAllTextAsync(IOPath.Combine(root,"ordinary-window-result.json"),JsonSerializer.Serialize(new{
            pathCount,windows,peakLoaded,peakReady,elapsedMs=elapsed,stopBetweenWindows,
            slowSchemaStacks=slowSchemaStacks.OrderByDescending(item=>item.Value.Total).Take(12)
                .Select(item=>new {stack=item.Key,count=item.Value.Count,totalMs=item.Value.Total}),
            timings=timings.OrderByDescending(item=>item.Value.Total).ToDictionary(item=>item.Key,
                item=>new {count=item.Value.Count,totalMs=item.Value.Total,maxMs=item.Value.Max}),
            uploads=rig.Remote.UploadCount-beforeUploads,
            downloads=rig.Remote.DownloadCount-beforeDownloads,result="PASS",
            limitation="Single-pair independent files; installation cap and dependent/multi-participant frontiers remain separate."
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    private sealed record Rig(string Root, string Local, string DatabasePath, SyncAuthorityDatabase Database,
        SyncAuthorityRuntimeHost Host, FakeSyncRemoteStore Remote, PilotInstanceLock Instance,
        CloudPairHostBindingChecks.HostWire Wire, DropboxConnectionBinding Binding) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Instance.Dispose();
        }
    }

    private static async Task<Rig> RigAsync(string root, bool fresh, FakeSyncRemoteStore? provider = null,
        Func<SyncPair,FakeSyncRemoteStore>? remoteSelector = null)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(IOPath.Combine(root, "state"));
        Directory.CreateDirectory(IOPath.Combine(root, "authority"));
        var instance = PilotInstanceLock.Acquire(IOPath.Combine(root, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        if (fresh)
            await connections.SaveNewAsync(new DropboxConnectionBinding(
                DropboxProviderConfiguration.RegisteredClientId, "dbid:normalized-original-consent",
                "consent-root", "consent-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes,
                DisplayName: "Normalized original consent fixture",
                Email: "normalized-consent@example.invalid").Normalize());
        var databasePath = IOPath.Combine(root, "authority", "syncshark.db");
        var database = new SyncAuthorityDatabase(databasePath);
        var remote = provider ?? new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        var binding = connections.Load() ?? throw new InvalidDataException("The fixture connection was not retained.");
        var wire = new CloudPairHostBindingChecks.HostWire();
        wire.AddDropboxFolder(binding, "/Apps/Sync Shark/Recovery", "id:recovery-root");
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority,
            new HttpClient(wire), new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), connections,
            new MemoryDropboxRefreshTokenStore(CloudPairHostBindingChecks.HostWire.RefreshTokenFor(binding)), database, (pair, _) => remoteSelector?.Invoke(pair) ?? remote);
        var pairs = await host.LoadPairsAsync();
        foreach (var pair in pairs.Pairs)
            wire.AddDropboxFolder(binding, pair.DropboxRoot, pair.DropboxFolderId);
        return new Rig(root, IOPath.Combine(root, "local"), databasePath, database, host, remote, instance, wire, binding);
    }

    private static async Task<SyncPair> BindAsync(Rig rig, string name, string local, string folderPath,
        string folderId)
    {
        Directory.CreateDirectory(local);
        rig.Wire.AddDropboxFolder(rig.Binding, folderPath, folderId);
        var request = new SyncPairBindingRequest(null, name, local,
            new DropboxFolderEntry(name, folderPath, folderId),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
            Enabled: true, Priority: 50, PropagateDeletions: false, FilesOnDemandEnabled: false,
            SelectionDefaultIncluded: true, RecoveryRetentionDays: 180);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pair binding requires Windows.");
        return (await rig.Host.BindPairAsync(request)).Pair;
    }

    private static async Task<SyncBootstrapActionPlan> ReviewAsync(Rig rig, SyncPair pair,
        params string[] skipPaths)
    {
        var skipped = skipPaths.ToHashSet(StringComparer.Ordinal);
        var review = await rig.Host.PrepareBootstrapInventoryAsync(pair.Id);
        var choices = review.Items.Where(item => SyncBootstrapActions.IsActionable(item.Group))
            .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group,
                skipped.Contains(item.RelativePath)
                    ? "Skip for now"
                    : item.Move?.Action ?? SyncBootstrapActions.TransferAction(item.Group)!))
            .ToArray();
        if (choices.Length == 0) throw new InvalidDataException("The reviewed fixture retained no actionable item.");
        return new SyncBootstrapActionPlan(pair.Id, review.LocalScanGenerationId!,
            review.DropboxScanGenerationId!, choices);
    }

    private static async Task<SyncAuthorityRunHistory> NewestRetryAsync(Rig rig, string parentRunId)
    {
        var children = (await rig.Database.LoadRunHistoryAsync(50)).Where(item =>
            item.RequestJson.Contains("\"retryOfRunId\":\"" + parentRunId + "\"", StringComparison.Ordinal))
            .ToArray();
        if (children.Length == 0)
            throw new InvalidDataException("The retained retry of " + parentRunId + " was not written.");
        return children.OrderByDescending(item => item.StartedUtc).First();
    }

    private static async Task SeedTerminalLegacyCommandAsync(SyncAuthorityDatabase database, string commandId,
        string payloadJson, string? pairId = null)
    {
        // Deliberate predecessor input for the real legacy reader. It has no current admission
        // and cannot authorize effects; fresh production commands still require a reservation.
        await using var connection = new SqliteConnection("Data Source=" + database.DatabasePath + ";Pooling=False");
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            PRAGMA foreign_keys=ON;
            INSERT INTO command_request(command_request_id,command_kind,pair_id,payload_json,
                requested_utc,status,terminal_utc,result_json)
            VALUES($id,'run_pair',$pair,$payload,$utc,'completed',$utc,'{}');
            """;
        seed.Parameters.AddWithValue("$id", commandId);
        seed.Parameters.AddWithValue("$pair", (object?)pairId ?? DBNull.Value);
        seed.Parameters.AddWithValue("$payload", payloadJson);
        seed.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<SyncAuthorityConsentItemOutline>> LoadAllOutlinesAsync(
        SyncAuthorityDatabase database, string consentId, int count)
    {
        var outlines = new List<SyncAuthorityConsentItemOutline>(count);
        for (var from = 0; from < count; from += SyncAuthorityConsentEncoding.OutlinePageSize)
            outlines.AddRange(await database.LoadConsentItemOutlinesAsync(consentId, from,
                Math.Min(SyncAuthorityConsentEncoding.OutlinePageSize, count - from)));
        return outlines;
    }

    private static bool OutlinesMatchPlan(IReadOnlyList<SyncAuthorityConsentItemOutline> outlines,
        IReadOnlyList<SyncBootstrapActionChoice> choices)
    {
        if (outlines.Count != choices.Count) return false;
        for (var index = 0; index < outlines.Count; index++)
        {
            var expected = Normalized(choices[index]);
            var outline = outlines[index];
            if (outline.Ordinal != index ||
                !string.Equals(outline.RelativePath, expected.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(outline.ExpectedGroup, expected.ExpectedGroup, StringComparison.Ordinal) ||
                !string.Equals(outline.Action, expected.Action, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static SyncBootstrapActionChoice Normalized(SyncBootstrapActionChoice choice) => choice with
    {
        RelativePath = PilotPath.NormalizeRelative(choice.RelativePath),
        ExpectedGroup = choice.ExpectedGroup.Trim(),
        Action = choice.Action.Trim()
    };

    private static int OrdinalOf(SyncBootstrapActionPlan plan, string relativePath)
    {
        for (var index = 0; index < plan.Choices.Count; index++)
            if (string.Equals(plan.Choices[index].RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

    private static SyncAuthorityConsentPreparedChoice[] Slice(SyncAuthorityConsentPreparedChoice[] source,
        int offset, int count)
    {
        var slice = new SyncAuthorityConsentPreparedChoice[count];
        Array.Copy(source, offset, slice, 0, count);
        return slice;
    }

    private static SyncBootstrapActionChoice[] BuildChoices(int count)
    {
        var choices = new SyncBootstrapActionChoice[count];
        for (var index = 0; index < count; index++)
            choices[index] = new SyncBootstrapActionChoice("folder/file-" + Degree(index) + ".txt",
                "Local only", "Upload");
        return choices;
    }

    private static string Degree(int value) =>
        value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private static string Bits(params byte[] values) => Convert.ToBase64String(values);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static bool Refuses(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return true; }
        return false;
    }

    private static async Task<bool> RefusedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return true; }
        catch (InvalidOperationException) { return true; }
        return false;
    }

    private static async Task<bool> RefusedSqlAsync(Func<Task> action)
    {
        try { await action(); }
        catch (SqliteException) { return true; }
        return false;
    }

    private static async Task AttemptAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { }
    }

    private static void Log(object value) => Console.WriteLine("RESULT " + JsonSerializer.Serialize(value));

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<long> CountAsync(string databasePath, string table)
    {
        return long.Parse(await ScalarAsync(databasePath, "SELECT count(*) FROM " + table + ";"),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await ExecuteOnAsync(connection, "PRAGMA foreign_keys=ON;");
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return value is null or DBNull
            ? string.Empty
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await ExecuteOnAsync(connection, "PRAGMA foreign_keys=ON;");
        await ExecuteOnAsync(connection, sql, parameters);
        SqliteConnection.ClearAllPools();
    }

    private static async Task ExecuteOnAsync(SqliteConnection connection, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertFixtureOperationAsync(string databasePath, string operationId,
        string idempotencyKey, string pairId, string? consentId, int? ordinal, string capabilityProfileId)
    {
        await ExecuteAsync(databasePath,
            "INSERT INTO operation(operation_id,pair_id,object_id,kind,status,priority,configuration_revision," +
            " capability_profile_id,idempotency_key,preconditions_json,created_utc,consent_id,consent_item_ordinal)" +
            " VALUES($id,$pair,NULL,'create_remote','planned',0,1,$profile,$key,'{}',$utc,$consent,$ordinal);",
            ("$id", operationId), ("$pair", pairId), ("$profile", capabilityProfileId),
            ("$key", idempotencyKey), ("$utc", "2026-10-12T00:00:00.0000000Z"), ("$consent", consentId),
            ("$ordinal", ordinal));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
}
