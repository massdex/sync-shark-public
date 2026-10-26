using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using IOPath = System.IO.Path;

// Compact retry consent proof: one reviewed first-sync approval is retained EXACTLY ONCE and every
// retry keeps only an indexed selection filter over that exact original order. Exercises the real
// production payload builder, real accepted-command storage, the real host ancestry/preview
// readers, a real reviewed first-sync + retry + retry-of-retry on a disposable provider fixture,
// the SQL operation-history ownership branch and the ancestry refusal matrix. No live provider,
// credential or retained product database is used.
internal static class CompactRetryConsentChecks
{
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _assertions = 0;
        if (!OperatingSystem.IsWindows()) return;

        ConsentEncodingChecks();
        await RetainedConsentStorageChecksAsync();
        await ReviewedConsentJourneyAsync();
        await GroupOwnershipChecksAsync();
        await AncestryRefusalChecksAsync();

        Console.WriteLine("PASS compact retry consent checks: " + _assertions +
            " assertions; the reviewed selection is retained once, every retry keeps only its indexed" +
            " filter over that original order, and every reachable reader preserved its behaviour.");
    }

    // ---- CRC1: the consent reference itself -------------------------------------------------

    private static void ConsentEncodingChecks()
    {
        Require(SyncReviewedConsentReference.EncodedByteLength(0) == 0 &&
            SyncReviewedConsentReference.EncodedByteLength(1) == 1 &&
            SyncReviewedConsentReference.EncodedByteLength(7) == 1 &&
            SyncReviewedConsentReference.EncodedByteLength(8) == 1 &&
            SyncReviewedConsentReference.EncodedByteLength(9) == 2 &&
            SyncReviewedConsentReference.EncodedByteLength(1000) == 125 &&
            SyncReviewedConsentReference.EncodedByteLength(10000) == 1250,
            "CRC1 the encoded filter length is ceil(count/8) at 0/1/7/8/9 and at 1000/10000 choices");

        foreach (var count in new[] { 1, 7, 8, 9, 64, 1000, 10000 })
        {
            var original = BuildChoices(count, "Local only", "Upload");
            var plan = new SyncBootstrapActionPlan("pair-compact", "local-scan", "remote-scan", original);
            for (var index = 0; index < Math.Min(count, 9); index++)
            {
                var selected = original.Select((item, position) => position == index
                    ? item : item with { Action = "Skip for now" }).ToArray();
                var reference = new SyncReviewedConsentReference("root-run", new string('a', 64), count,
                    SyncReviewedConsentReference.EncodeSelection(original, selected));
                var decoded = SyncReviewedConsentReference.DecodeSelection(reference);
                Require(decoded.Length == count && decoded.Count(bit => bit) == 1 && decoded[index],
                    "CRC1 bit " + index + " of " + count + " selects exactly that original choice");
                Require(Convert.FromBase64String(reference.SelectionBitsBase64).Length == (count + 7) / 8,
                    "CRC1 the " + count + "-choice filter encodes exactly ceil(count/8) bytes");
                SyncReviewedConsentReference.RequireSelectionAgainstRoot(reference, plan);
            }

            // The filter follows the RETAINED ORIGINAL order, never the caller's enumeration order.
            var reversed = original.Reverse().ToArray();
            var allUpload = original.Select(item => item with { Action = "Upload" }).ToArray();
            Require(SyncReviewedConsentReference.EncodeSelection(original, reversed) ==
                SyncReviewedConsentReference.EncodeSelection(original, allUpload),
                "CRC1 the " + count + "-choice filter is independent of the caller's choice order");

            // An all-completed approval legitimately produces an all-zero filter.
            var allSkipped = original.Select(item => item with { Action = "Skip for now" }).ToArray();
            var emptyFilter = SyncReviewedConsentReference.EncodeSelection(original, allSkipped);
            var emptyReference = new SyncReviewedConsentReference("root-run", new string('a', 64), count, emptyFilter);
            Require(Convert.FromBase64String(emptyFilter).All(value => value == 0) &&
                SyncReviewedConsentReference.DecodeSelection(emptyReference).All(bit => !bit),
                "CRC1 an already-completed " + count + "-choice approval keeps an all-zero filter");
            SyncReviewedConsentReference.RequireSelectionAgainstRoot(emptyReference, plan);
        }

        var fourChoices = BuildChoices(4, "Local only", "Upload");
        var skipPlan = new SyncBootstrapActionPlan("pair-compact", "local-scan", "remote-scan",
            fourChoices.Select((item, position) => position == 3
                ? item with { Action = "Skip for now" } : item).ToArray());
        var hash = new string('a', 64);
        Require(Refuses(() => SyncReviewedConsentReference.RequireSelectionAgainstRoot(
                new SyncReviewedConsentReference("root-run", hash, 4, Bits(8)), skipPlan)),
            "CRC1 selecting an original Skip for now is refused");
        Require(Refuses(() => SyncReviewedConsentReference.RequireSelectionAgainstRoot(
                new SyncReviewedConsentReference("root-run", hash, 5, Bits(0)), skipPlan)),
            "CRC1 a filter whose count differs from the retained original is refused");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(
                new SyncReviewedConsentReference("root-run", hash, 4, Bits(0xF0)))),
            "CRC1 nonzero bits beyond the original choice count are refused");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(
                new SyncReviewedConsentReference("root-run", hash, 4, Bits(0, 0)))),
            "CRC1 a bitmap decoding to the wrong byte length is refused");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(
                new SyncReviewedConsentReference("root-run", hash, 4, Bits(0, 0, 0, 0, 0, 0, 0, 0)))),
            "CRC1 an oversized base64 filter is refused before allocation");
        Require(Refuses(() => SyncReviewedConsentReference.DecodeSelection(
                new SyncReviewedConsentReference("root-run", hash, 4, "!!!!"))),
            "CRC1 a malformed base64 filter is refused");
        Require(Refuses(() => SyncReviewedConsentReference.ReadRequired(
                Json("{\"commandId\":\"root-run\",\"requestSha256\":\"ABC\",\"choiceCount\":4,\"selectionBitsBase64\":\"AA==\"}"))),
            "CRC1 a consent reference without a lowercase SHA-256 is refused");
        Require(Refuses(() => SyncReviewedConsentReference.ReadRequired(
                Json("{\"commandId\":\"\",\"requestSha256\":\"" + hash + "\",\"choiceCount\":4,\"selectionBitsBase64\":\"AA==\"}"))),
            "CRC1 a consent reference without its original approval identity is refused");
    }

    // ---- CRC2: the production builder over real accepted-command storage ---------------------

    private static async Task RetainedConsentStorageChecksAsync()
    {
        var root = IOPath.Combine(_root, "retained-consent-storage");
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(IOPath.Combine(root, "authority.db"));
        await database.InitializeAsync("compact-retry-fixture", "compact-retry-profile", "owner-fixture", "{}");
        await database.VerifyHealthAsync();
        var pair = new SyncPair("pair-retained-consent", "Retained consent", IOPath.Combine(root, "local"), 0, 0,
            "/RetainedConsent", "id:retained-consent", ConnectionId: "connection-retained-consent");

        foreach (var count in new[] { 1000, 10000 })
        {
            var label = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var original = BuildChoices(count, "Local only", "Upload");
            var rootPlan = new SyncBootstrapActionPlan(pair.Id, "local-scan", "remote-scan", original);
            var rootId = "root-consent-" + label;
            await SeedTerminalLegacyCommandAsync(database, rootId,
                SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, rootPlan,
                    "Reviewed first sync", pair.PolicyRevision, null, null));
            var retainedRoot = await database.LoadRunByIdAsync(rootId)
                ?? throw new InvalidDataException("The retained consent root " + rootId + " was not written.");
            Require(retainedRoot.RequestJson.Contains("\"reviewedPlan\"", StringComparison.Ordinal) &&
                !retainedRoot.RequestJson.Contains("reviewedConsent", StringComparison.Ordinal),
                "CRC2 the original " + label + "-choice approval keeps its complete inline plan exactly once");

            var beforeBytes = DatabaseBytes(database.DatabasePath);
            var childBytes = new List<int>();
            for (var retry = 0; retry < 10; retry++)
            {
                var selected = original.Select((item, position) => position % 97 == retry
                    ? item : item with { Action = "Skip for now" }).ToArray();
                var consent = SyncReviewedConsentReference.Create(retainedRoot, rootPlan, selected);
                var childId = "retry-consent-" + label + "-" +
                    retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await SeedTerminalLegacyCommandAsync(database, childId,
                    SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, rootPlan with { Choices = selected },
                        "Reviewed first sync", pair.PolicyRevision, rootId, consent));
                var child = await database.LoadRunByIdAsync(childId)
                    ?? throw new InvalidDataException("The retained retry " + childId + " was not written.");
                Require(!child.RequestJson.Contains("reviewedPlan", StringComparison.Ordinal) &&
                    child.RequestJson.Contains("\"reviewedConsent\"", StringComparison.Ordinal),
                    "CRC2 retry " + retry + " of " + label + " choices stores no second copy of the reviewed plan");
                var childUtf8 = Encoding.UTF8.GetByteCount(child.RequestJson);
                childBytes.Add(childUtf8);
                Require(childUtf8 * 10 < Encoding.UTF8.GetByteCount(retainedRoot.RequestJson),
                    "CRC2 retry " + retry + " of " + label + " choices grows only with ceil(N/8) and fixed metadata");
            }
            var afterBytes = DatabaseBytes(database.DatabasePath);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "retained-consent-storage",
                choices = count,
                rootUtf8Bytes = Encoding.UTF8.GetByteCount(retainedRoot.RequestJson),
                retryUtf8Bytes = childBytes,
                retries = childBytes.Count,
                databaseGrowthBytes = afterBytes - beforeBytes
            }));
        }
    }

    // ---- CRC3: a real reviewed first sync, retry and retry-of-retry --------------------------

    private static async Task ReviewedConsentJourneyAsync()
    {
        using var rig = await RigAsync("journey");
        var localRoot = Directory.CreateDirectory(IOPath.Combine(rig.Local, "journey-local")).FullName;
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "a-upload.txt"), "uploaded payload");
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "b-retry.txt"), "retried upload payload");
        var pair = await BindAsync(rig, "Reviewed consent journey", localRoot, "/ReviewedConsentJourney",
            "id:reviewed-consent-journey");
        rig.Remote.Put("c-download.txt", "downloaded payload");
        rig.Remote.Put("d-skip.txt", "explicitly skipped payload");
        rig.Remote.UploadFailureForTest = path => path == "b-retry.txt"
            ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: controlled lock on b-retry.txt") : null;
        rig.Remote.DownloadFailureForTest = path => path == "c-download.txt"
            ? new DropboxApiException(HttpStatusCode.InternalServerError,
                "fixture: controlled refusal on c-download.txt") : null;

        var plan = await ReviewAsync(rig, pair, skipPath: "d-skip.txt");
        var first = await rig.Host.RunBootstrapActionPlanAsync(plan);
        Require(first.Result.Uploaded == 1 && first.Result.Failed >= 1 &&
            rig.Remote.Exists("a-upload.txt") && !rig.Remote.Exists("b-retry.txt") &&
            !File.Exists(IOPath.Combine(localRoot, "c-download.txt")) &&
            !File.Exists(IOPath.Combine(localRoot, "d-skip.txt")),
            "CRC3 the mixed Upload/Download/Skip review completes its independent action and retains its failures");

        var origin = (await rig.Database.LoadRunHistoryAsync(40)).Single(item =>
            item.RequestJson.Contains("Reviewed first sync", StringComparison.Ordinal) &&
            item.RequestJson.Contains(pair.Id, StringComparison.Ordinal));
        using (var rootRequest = JsonDocument.Parse(origin.RequestJson))
            Require(!rootRequest.RootElement.TryGetProperty("reviewedPlan", out _) &&
                rootRequest.RootElement.TryGetProperty("consentId", out var normalizedConsent) &&
                !string.IsNullOrWhiteSpace(normalizedConsent.GetString()) &&
                !rootRequest.RootElement.TryGetProperty("reviewedConsent", out _),
                "CRC3 the current original approval stores its normalized consent once without an inline duplicate");

        var originPreview = await rig.Host.LoadRunRetryAsync(origin.Id, default, pair.Id);
        Require(originPreview.UnavailableReason is null &&
            originPreview.Items.Single(item => item.Path == "b-retry.txt").CanRetry &&
            originPreview.Items.Single(item => item.Path == "c-download.txt").CanRetry &&
            !originPreview.Items.Single(item => item.Path == "a-upload.txt").CanRetry &&
            originPreview.Items.All(item => item.Path != "d-skip.txt"),
            "CRC3 the review preview offers exactly the retained failures and never the completed or skipped files");

        rig.Remote.UploadFailureForTest = null;
        var uploadsBefore = rig.Remote.UploadCount;
        var retryOne = await rig.Host.RetryRunAsync(origin.Id, ["b-retry.txt"], "failed", selectedPairId: pair.Id);
        Require(retryOne.Result.Failed == 0 && rig.Remote.UploadCount == uploadsBefore + 1 &&
            rig.Remote.Exists("b-retry.txt") &&
            (await rig.Database.LoadRunByIdAsync(origin.Id))!.ResultJson == origin.ResultJson,
            "CRC3 the first retry copies only the repaired upload and never rewrites the original failure");

        var child = await NewestRetryAsync(rig, origin.Id);
        using (var childRequest = JsonDocument.Parse(child.RequestJson))
        {
            var hasConsent = childRequest.RootElement.TryGetProperty("reviewedConsent", out var consent);
            Require(!childRequest.RootElement.TryGetProperty("reviewedPlan", out _) && hasConsent,
                "CRC3 the retry command retains no second copy of the reviewed plan");
            Require(consent.GetProperty("commandId").GetString() == origin.Id &&
                consent.GetProperty("requestSha256").GetString() ==
                    SyncReviewedConsentReference.LowercaseRequestSha256(origin.RequestJson) &&
                consent.GetProperty("choiceCount").GetInt32() == plan.Choices.Count,
                "CRC3 the retry names the exact terminal original approval by identity, hash and count");
            Require(childRequest.RootElement.GetProperty("policyRevision").GetInt64() == pair.PolicyRevision &&
                childRequest.RootElement.GetProperty("retryOfRunId").GetString() == origin.Id &&
                childRequest.RootElement.TryGetProperty("pairIds", out _),
                "CRC3 the retry keeps its immediate ancestor and its scalar policy/pair metadata");
        }

        // Equivalent legacy-inline and compact histories must project identically.
        var inlineId = "inline-equivalent-" + Guid.NewGuid().ToString("N");
        await SeedTerminalLegacyCommandAsync(rig.Database, inlineId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, Filtered(plan, "b-retry.txt"),
                "Reviewed first sync", pair.PolicyRevision, origin.Id, null));
        var compactPreview = await rig.Host.LoadRunRetryAsync(child.Id, default, pair.Id);
        var inlinePreview = await rig.Host.LoadRunRetryAsync(inlineId, default, pair.Id);
        Require(compactPreview.UnavailableReason is null && inlinePreview.UnavailableReason is null &&
            compactPreview.PolicyRevision == inlinePreview.PolicyRevision &&
            compactPreview.Items.Count == inlinePreview.Items.Count &&
            compactPreview.Items.Zip(inlinePreview.Items).All(candidate =>
                candidate.First.Path == candidate.Second.Path &&
                candidate.First.State == candidate.Second.State &&
                candidate.First.CanRetry == candidate.Second.CanRetry),
            "CRC3 equivalent legacy-inline and compact retry histories project identically");
        Require(compactPreview.Items.Single(item => item.Path == "a-upload.txt") is
                { State: "Completed", CanRetry: false } &&
            !compactPreview.Items.Single(item => item.Path == "b-retry.txt").CanRetry,
            "CRC3 completed ancestor files are never re-offered after the compact retry");

        rig.Remote.DownloadFailureForTest = null;
        var downloadsBefore = rig.Remote.DownloadCount;
        var retryTwo = await rig.Host.RetryRunAsync(child.Id, ["c-download.txt"], "failed", selectedPairId: pair.Id);
        Require(retryTwo.Result.Failed == 0 && rig.Remote.DownloadCount == downloadsBefore + 1 &&
            await File.ReadAllTextAsync(IOPath.Combine(localRoot, "c-download.txt")) == "downloaded payload",
            "CRC3 retry-of-retry completes the retained download without replaying the upload ancestor");
        var grandchild = await NewestRetryAsync(rig, child.Id);
        using (var grandchildRequest = JsonDocument.Parse(grandchild.RequestJson))
            Require(grandchildRequest.RootElement.GetProperty("reviewedConsent").GetProperty("commandId").GetString() == origin.Id &&
                grandchildRequest.RootElement.GetProperty("retryOfRunId").GetString() == child.Id &&
                !grandchildRequest.RootElement.TryGetProperty("reviewedPlan", out _),
                "CRC3 retry-of-retry derives the original consent from the retained ancestry, not the child");
        var finalPreview = await rig.Host.LoadRunRetryAsync(grandchild.Id, default, pair.Id);
        Require(finalPreview.UnavailableReason is null &&
            finalPreview.Items.All(item => item.Path != "d-skip.txt") &&
            !File.Exists(IOPath.Combine(localRoot, "d-skip.txt")),
            "CRC3 the mixed compact chain stays readable and no retry turns the original skip into consent");
    }

    // ---- CRC4: SQL operation-history ownership ----------------------------------------------

    private static async Task GroupOwnershipChecksAsync()
    {
        using var rig = await RigAsync("ownership");
        var localRoot = Directory.CreateDirectory(IOPath.Combine(rig.Local, "ownership-local")).FullName;
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "a-done.txt"), "already completed");
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "b-prefix.txt"), "interrupted planning payload one");
        await File.WriteAllTextAsync(IOPath.Combine(localRoot, "c-prefix.txt"), "interrupted planning payload two");
        var pair = await BindAsync(rig, "Group ownership", localRoot, "/ReviewedConsentOwnership",
            "id:reviewed-consent-ownership");
        rig.Remote.UploadFailureForTest = path => path is "b-prefix.txt" or "c-prefix.txt"
            ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: controlled lock on " + path) : null;

        var plan = await ReviewAsync(rig, pair, skipPath: null);
        await rig.Host.RunBootstrapActionPlanAsync(plan);
        var origin = (await rig.Database.LoadRunHistoryAsync(40)).Single(item =>
            item.RequestJson.Contains("Reviewed first sync", StringComparison.Ordinal) &&
            item.RequestJson.Contains(pair.Id, StringComparison.Ordinal));

        // Interrupt the retry AFTER its first operation is retained, so that operation is owned by
        // the shared durable group binding and has NO per-file presentation event to fall back on.
        SyncAuthorityOperationPlan? prefix = null;
        var planned = 0;
        rig.Database.BeforeOperationRegistrationForTest = candidate =>
        {
            if (candidate.Kind != SyncAuthorityOperationKind.CreateRemote) return;
            if (++planned == 1) prefix = candidate;
            else throw new IOException("fixture: interrupted retry planning before dispatch");
        };
        try
        {
            await rig.Host.RetryRunAsync(origin.Id, null, "failed", selectedPairId: pair.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine("fixture interrupted retry planning: " + error.GetType().Name);
        }
        finally
        {
            rig.Database.BeforeOperationRegistrationForTest = null;
        }

        Require(prefix is not null, "CRC4 the interrupted retry retained its planning prefix operation");
        var prefixId = prefix!.OperationId;
        var prefixPath = (await rig.Database.LoadRetryOperationEvidenceAsync([prefixId])).Single().Path;
        var child = await NewestRetryAsync(rig, origin.Id);
        var groupId = await ScalarAsync(rig.DatabasePath,
            "SELECT operation_group_id FROM operation_group WHERE json_extract(detail_json,'$.runId')=$run;",
            ("$run", child.Id));
        Require(groupId.Length > 0 && prefixPath.Length > 0,
            "CRC4 the retry group carries its explicit run owner for " + prefixPath);

        Require((await rig.Database.LoadRunOperationIdsAsync(child.Id, pair.Id)).Contains(prefixId),
            "CRC4 the explicit run group resolves the compact child's retained operation");

        await ExecuteAsync(rig.DatabasePath,
            "UPDATE operation_group SET detail_json=json_remove(detail_json,'$.runId') WHERE operation_group_id=$group;",
            ("$group", groupId));
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT count(*) FROM operation_group WHERE operation_group_id=$group AND json_extract(detail_json,'$.runId') IS NULL;",
                ("$group", groupId)) == "1" &&
            !(await rig.Database.LoadRunOperationIdsAsync(child.Id, pair.Id)).Contains(prefixId),
            "CRC4 a compact child cannot borrow the legacy implicit-owner branch without its explicit run owner");

        // The same group with an equivalent legacy INLINE child: the historical fallback is unchanged.
        var inlineId = "inline-owner-" + Guid.NewGuid().ToString("N");
        await SeedTerminalLegacyCommandAsync(rig.Database, inlineId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, Filtered(plan, prefixPath),
                "Reviewed first sync", pair.PolicyRevision, origin.Id, null), pair.Id);
        await ExecuteAsync(rig.DatabasePath,
            "UPDATE command_request SET requested_utc=(SELECT requested_utc FROM command_request WHERE command_request_id=$source)," +
            " terminal_utc=(SELECT terminal_utc FROM command_request WHERE command_request_id=$source) WHERE command_request_id=$target;" +
            " UPDATE command_request SET pair_id=NULL WHERE pair_id=$pair AND command_request_id<>$target;",
            ("$source", child.Id), ("$target", inlineId), ("$pair", pair.Id));
        Require((await rig.Database.LoadRunOperationIdsAsync(inlineId, pair.Id)).Contains(prefixId),
            "CRC4 the historical inline fallback still owns its exact original scan/path/direction operation");
    }

    // ---- CRC5: the ancestry refusal matrix --------------------------------------------------

    private static async Task AncestryRefusalChecksAsync()
    {
        using var rig = await RigAsync("refusal");
        var pair = await BindAsync(rig, "Ancestry refusal", IOPath.Combine(rig.Local, "refusal-local"),
            "/ReviewedConsentRefusal", "id:reviewed-consent-refusal");
        var revision = pair.PolicyRevision;
        var original = new[]
        {
            new SyncBootstrapActionChoice("folder/first.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/second.txt", "Dropbox only", "Download"),
            new SyncBootstrapActionChoice("folder/third.txt", "Local only", "Upload"),
            new SyncBootstrapActionChoice("folder/skipped.txt", "Dropbox only", "Skip for now")
        };
        var plan = new SyncBootstrapActionPlan(pair.Id, "refusal-local-scan", "refusal-remote-scan", original);
        var selected = original.Select((item, position) => position == 0
            ? item : item with { Action = "Skip for now" }).ToArray();
        var uploadsBefore = rig.Remote.UploadCount;
        var downloadsBefore = rig.Remote.DownloadCount;

        var controlRootId = "refusal-root-control";
        await SeedTerminalLegacyCommandAsync(rig.Database, controlRootId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "Reviewed first sync",
                revision, null, null));
        var controlRoot = await rig.Database.LoadRunByIdAsync(controlRootId)
            ?? throw new InvalidDataException("The refusal control root was not written.");
        var controlId = "refusal-child-control";
        await SeedTerminalLegacyCommandAsync(rig.Database, controlId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected }, "Reviewed first sync",
                revision, controlRootId, SyncReviewedConsentReference.Create(controlRoot, plan, selected)));
        var control = await rig.Host.LoadRunRetryAsync(controlId, default, pair.Id);
        Require(control.UnavailableReason is null && control.Items.Count == 3 &&
            control.Items.All(item => item.CanRetry && item.State == "Not attempted"),
            "CRC5 an unmodified compact child reads the retained original approval exactly as inline did");

        async Task RefuseAsync(string label, Func<JsonObject, bool> mutate, string message)
        {
            var rootId = "refusal-root-" + label;
            await SeedTerminalLegacyCommandAsync(rig.Database, rootId,
                SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "Reviewed first sync",
                    revision, null, null));
            var root = await rig.Database.LoadRunByIdAsync(rootId)
                ?? throw new InvalidDataException("The refusal root " + rootId + " was not retained.");
            var childId = "refusal-child-" + label;
            var node = JsonNode.Parse(SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected },
                "Reviewed first sync", revision, rootId,
                SyncReviewedConsentReference.Create(root, plan, selected)))!.AsObject();
            if (!mutate(node)) throw new InvalidDataException("The refusal fixture " + label + " did not mutate.");
            await SeedTerminalLegacyCommandAsync(rig.Database, childId, node.ToJsonString());

            var refused = false;
            try { await rig.Host.LoadRunRetryAsync(childId, default, pair.Id); }
            catch (InvalidDataException) { refused = true; }
            Require(refused, message);
            var commandsBefore = await CountAsync(rig.DatabasePath, "command_request");
            var effectRefused = false;
            try { await rig.Host.RetryRunAsync(childId, null, "all", selectedPairId: pair.Id); }
            catch (InvalidDataException) { effectRefused = true; }
            Require(effectRefused && await CountAsync(rig.DatabasePath, "command_request") == commandsBefore,
                message + " on the effect-bearing entry point without any command");
        }

        await RefuseAsync("wrong-root", node =>
        {
            node["reviewedConsent"]!["commandId"] = "foreign-root-identity";
            return true;
        }, "CRC5 a consent reference naming a foreign root is refused");
        await RefuseAsync("wrong-hash", node =>
        {
            node["reviewedConsent"]!["requestSha256"] = new string('f', 64);
            return true;
        }, "CRC5 a consent reference with a substituted full root hash is refused");
        await RefuseAsync("wrong-count", node =>
        {
            node["reviewedConsent"]!["choiceCount"] = original.Length + 1;
            return true;
        }, "CRC5 a consent reference claiming a different original count is refused");
        await RefuseAsync("malformed-base64", node =>
        {
            node["reviewedConsent"]!["selectionBitsBase64"] = "!!!!";
            return true;
        }, "CRC5 a malformed base64 filter is refused");
        await RefuseAsync("oversized-base64", node =>
        {
            node["reviewedConsent"]!["selectionBitsBase64"] = Bits(0, 0, 0, 0, 0, 0, 0, 0);
            return true;
        }, "CRC5 an oversized base64 filter is refused");
        await RefuseAsync("wrong-decoded-length", node =>
        {
            node["reviewedConsent"]!["selectionBitsBase64"] = Bits(0, 0);
            return true;
        }, "CRC5 a filter decoding to the wrong byte length is refused");
        await RefuseAsync("trailing-bits", node =>
        {
            node["reviewedConsent"]!["selectionBitsBase64"] = Bits(0xF0);
            return true;
        }, "CRC5 a filter setting bits beyond the original count is refused");
        await RefuseAsync("selected-original-skip", node =>
        {
            node["reviewedConsent"]!["selectionBitsBase64"] = Bits(8);
            return true;
        }, "CRC5 a retry selecting an original Skip for now is refused");
        await RefuseAsync("changed-revision", node =>
        {
            node["policyRevision"] = revision + 1;
            return true;
        }, "CRC5 a compact child whose policy revision changed is refused");
        await RefuseAsync("foreign-pair", node =>
        {
            node["pairIds"] = new JsonArray(pair.Id + "-foreign");
            return true;
        }, "CRC5 a compact child naming a foreign pair is refused");
        await RefuseAsync("both-representations", node =>
        {
            node["reviewedPlan"] = JsonNode.Parse(JsonSerializer.Serialize(plan));
            return true;
        }, "CRC5 a command keeping both reviewed representations is refused");
        await RefuseAsync("malformed-reference", node =>
        {
            node["reviewedConsent"] = JsonNode.Parse("{\"commandId\":\"only-an-id\"}");
            return true;
        }, "CRC5 a present-but-malformed consent reference is never an ordinary run");
        await RefuseAsync("missing-parent", node =>
        {
            node["retryOfRunId"] = "absent-ancestor-identity";
            return true;
        }, "CRC5 a compact child naming a missing immediate ancestor is refused");
        await RefuseAsync("missing-root", node =>
        {
            node["reviewedConsent"]!["commandId"] = "absent-root-identity";
            node["retryOfRunId"] = "absent-root-identity";
            return true;
        }, "CRC5 a compact child whose named root is not retained is refused");

        // A changed original raw payload under the same identity is a hash mismatch, not rebinding.
        var rewriteRootId = "refusal-root-rewritten";
        await SeedTerminalLegacyCommandAsync(rig.Database, rewriteRootId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "Reviewed first sync",
                revision, null, null));
        var rewriteRoot = await rig.Database.LoadRunByIdAsync(rewriteRootId)
            ?? throw new InvalidDataException("The rewritten-payload root was not retained.");
        var rewriteChildId = "refusal-child-rewritten";
        await SeedTerminalLegacyCommandAsync(rig.Database, rewriteChildId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected }, "Reviewed first sync",
                revision, rewriteRootId, SyncReviewedConsentReference.Create(rewriteRoot, plan, selected)));
        Require((await rig.Host.LoadRunRetryAsync(rewriteChildId, default, pair.Id)).UnavailableReason is null,
            "CRC5 the rewritten-payload fixture is consented before its original payload changes");
        await ExecuteAsync(rig.DatabasePath,
            "UPDATE command_request SET payload_json=replace(payload_json,'folder/first.txt','folder/first-x.txt')" +
            " WHERE command_request_id=$id;", ("$id", rewriteRootId));
        Require(await RefusesAsync(() => rig.Host.LoadRunRetryAsync(rewriteChildId, default, pair.Id)),
            "CRC5 a rewritten original payload under the same identity is a hash mismatch, not rebound consent");

        // A legacy inline child keeps every existing plan equality and skip-filter check.
        var legacyRootId = "refusal-root-legacy";
        await SeedTerminalLegacyCommandAsync(rig.Database, legacyRootId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "Reviewed first sync",
                revision, null, null));
        var scanChangedId = "refusal-legacy-scan";
        await SeedTerminalLegacyCommandAsync(rig.Database, scanChangedId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
                new SyncBootstrapActionPlan(pair.Id, "refusal-local-scan", "substituted-remote-scan", selected),
                "Reviewed first sync", revision, legacyRootId, null));
        Require(await RefusesAsync(() => rig.Host.LoadRunRetryAsync(scanChangedId, default, pair.Id)),
            "CRC5 a legacy inline child with a substituted reviewed inventory is refused");
        var moveChangedId = "refusal-legacy-move";
        await SeedTerminalLegacyCommandAsync(rig.Database, moveChangedId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
                new SyncBootstrapActionPlan(pair.Id, "refusal-local-scan", "refusal-remote-scan",
                [
                    new SyncBootstrapActionChoice("folder/first.txt", "Local only", "MoveRemote",
                        new SyncBootstrapMoveReview(SyncAuthorityOperationKind.MoveRemote, "folder/first-old.txt",
                            "object-first", "local-version", "remote-version")),
                    .. selected.Skip(1)
                ]), "Reviewed first sync", revision, legacyRootId, null));
        Require(await RefusesAsync(() => rig.Host.LoadRunRetryAsync(moveChangedId, default, pair.Id)),
            "CRC5 a legacy inline child that changed an original move is refused");
        var actionChangedId = "refusal-legacy-action";
        await SeedTerminalLegacyCommandAsync(rig.Database, actionChangedId,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair,
                new SyncBootstrapActionPlan(pair.Id, "refusal-local-scan", "refusal-remote-scan",
                [
                    new SyncBootstrapActionChoice("folder/first.txt", "Local only", "Download"),
                    .. selected.Skip(1)
                ]), "Reviewed first sync", revision, legacyRootId, null));
        Require(await RefusesAsync(() => rig.Host.LoadRunRetryAsync(actionChangedId, default, pair.Id)),
            "CRC5 a legacy inline child that changed an original file action is refused");

        Require(rig.Remote.UploadCount == uploadsBefore && rig.Remote.DownloadCount == downloadsBefore,
            "CRC5 no refused ancestry input issued a provider transfer");

        // Cycle and the existing bounded ancestry remain enforced.
        var cycleFirst = "refusal-cycle-first";
        var cycleSecond = "refusal-cycle-second";
        await SeedTerminalLegacyCommandAsync(rig.Database, cycleFirst,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected }, "Reviewed first sync",
                revision, cycleSecond,
                new SyncReviewedConsentReference(cycleFirst, new string('a', 64), original.Length, Bits(0, 0, 0, 0))));
        await SeedTerminalLegacyCommandAsync(rig.Database, cycleSecond,
            SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected }, "Reviewed first sync",
                revision, cycleFirst,
                new SyncReviewedConsentReference(cycleSecond, new string('a', 64), original.Length, Bits(0, 0, 0, 0))));
        Require(await RefusesAsync(() => rig.Host.LoadRunRetryAsync(cycleFirst, default, pair.Id)),
            "CRC5 a cyclic retry ancestry is refused");

        var chain = new string[101];
        for (var index = 0; index < chain.Length; index++)
            chain[index] = "refusal-chain-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < chain.Length; index++)
            await SeedTerminalLegacyCommandAsync(rig.Database, chain[index], index == chain.Length - 1
                ? SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "Reviewed first sync",
                    revision, null, null)
                : SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan with { Choices = selected }, "Reviewed first sync",
                    revision, chain[index + 1],
                    new SyncReviewedConsentReference(chain[0], new string('a', 64), original.Length, Bits(0, 0, 0, 0))));
        Require(await RefusesAsync(() => rig.Host.LoadRunRetryAsync(chain[0], default, pair.Id)),
            "CRC5 an ancestry beyond the existing 100-node bound is refused");
    }

    // ---- fixtures and helpers ---------------------------------------------------------------

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

    private static async Task<Rig> RigAsync(string name)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var instance = PilotInstanceLock.Acquire(IOPath.Combine(root, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:compact-retry-consent", "consent-root", "consent-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Compact retry consent fixture", Email: "compact-consent@example.invalid").Normalize());
        var databasePath = IOPath.Combine(root, "authority", "syncshark.db");
        var database = new SyncAuthorityDatabase(databasePath);
        var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        var binding = connections.Load() ?? throw new InvalidDataException("The fixture connection was not retained.");
        var wire = new CloudPairHostBindingChecks.HostWire();
        wire.AddDropboxFolder(binding, "/Apps/Sync Shark/Recovery", "id:recovery-root");
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority,
            new HttpClient(wire), new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), connections,
            new MemoryDropboxRefreshTokenStore(CloudPairHostBindingChecks.HostWire.RefreshTokenFor(binding)), database, (_, _) => remote);
        await host.LoadPairsAsync();
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

    private static async Task<SyncBootstrapActionPlan> ReviewAsync(Rig rig, SyncPair pair, string? skipPath)
    {
        var review = await rig.Host.PrepareBootstrapInventoryAsync(pair.Id);
        var choices = review.Items.Where(item => SyncBootstrapActions.IsActionable(item.Group))
            .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group,
                string.Equals(item.RelativePath, skipPath, StringComparison.Ordinal)
                    ? "Skip for now"
                    : item.Move?.Action ?? SyncBootstrapActions.TransferAction(item.Group)!))
            .ToArray();
        if (choices.Length == 0) throw new InvalidDataException("The reviewed fixture retained no actionable item.");
        return new SyncBootstrapActionPlan(pair.Id, review.LocalScanGenerationId!,
            review.DropboxScanGenerationId!, choices);
    }

    private static SyncBootstrapActionPlan Filtered(SyncBootstrapActionPlan plan, string selectedPath) =>
        plan with
        {
            Choices = plan.Choices.Select(item =>
                string.Equals(item.RelativePath, selectedPath, StringComparison.Ordinal)
                    ? item : item with { Action = "Skip for now" }).ToArray()
        };

    private static async Task<SyncAuthorityRunHistory> NewestRetryAsync(Rig rig, string parentRunId)
    {
        var children = (await rig.Database.LoadRunHistoryAsync(50)).Where(item =>
            item.RequestJson.Contains("\"retryOfRunId\":\"" + parentRunId + "\"", StringComparison.Ordinal)).ToArray();
        if (children.Length == 0)
            throw new InvalidDataException("The retained retry of " + parentRunId + " was not written.");
        return children.OrderByDescending(item => item.StartedUtc).First();
    }

    private static async Task SeedTerminalLegacyCommandAsync(SyncAuthorityDatabase database, string commandId, string payloadJson,
        string? pairId = null)
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

    private static SyncBootstrapActionChoice[] BuildChoices(int count, string group, string action)
    {
        var choices = new SyncBootstrapActionChoice[count];
        for (var index = 0; index < count; index++)
            choices[index] = new SyncBootstrapActionChoice(
                "folder/file-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
                group, action);
        return choices;
    }

    private static string Bits(params byte[] values) => Convert.ToBase64String(values);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<bool> RefusesAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static bool Refuses(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return true; }
        return false;
    }

    private static long DatabaseBytes(string databasePath) =>
        new FileInfo(databasePath).Length +
        (File.Exists(databasePath + "-wal") ? new FileInfo(databasePath + "-wal").Length : 0) +
        (File.Exists(databasePath + "-shm") ? new FileInfo(databasePath + "-shm").Length : 0);

    private static async Task<long> CountAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM " + table + ";";
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
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
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
}

