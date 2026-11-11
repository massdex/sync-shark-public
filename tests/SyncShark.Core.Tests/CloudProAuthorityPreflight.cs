using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class CloudProAuthorityPreflight
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static async Task RunResidualSafetyDeltaAsync()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "cloud-pro-tests"));
        var testRoot = Path.Combine(allowedRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            Require(typeof(SyncAuthorityDatabase).GetMethod("BackupAsync", BindingFlags.Public | BindingFlags.Instance) is null,
                "The retired arbitrary-destination backup API was reintroduced.");
            Console.WriteLine("PASS Retired arbitrary backup API remains absent; migration backup preservation has separate account-state coverage");
            var authority = new SyncAuthorityDatabase(Path.Combine(testRoot, "reservation.db"));
            await authority.InitializeAsync("installation-preflight", "profile-cloud-pro", "residual-safety", "{}");
            var admission = await SeedIdentityGraphAsync(authority);
            await AssertAtomicPathReservationAsync(authority, admission);
            Console.WriteLine("PASS Real SQLite ASCII/non-ASCII path reservations and two-connection race reject duplicate authority");
            await AssertConditionalDeleteFailureClassificationAsync();
            Console.WriteLine("PASS Real Dropbox file-delete adapter preserves pre-effect identity fences and classifies ambiguous responses");
            foreach (var failure in new[] { "body-read", "invalid-json", "oversized", "wrong-shape" })
                foreach (var applied in new[] { false, true })
                    await AssertLegacyDirectoryDeleteRecoveryAsync(testRoot, failure, applied);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(testRoot).StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(testRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The residual fixture changed identity; cleanup was refused.");
            Directory.Delete(testRoot, recursive: true);
        }
    }

    // Older installations could have an uncertain directory-delete attempt. Refusing
    // new folder effects must not disable query-only reconciliation of that history.
    private static async Task AssertLegacyDirectoryDeleteRecoveryAsync(string testRoot, string failure, bool applied)
    {
        var databasePath = Path.Combine(testRoot, $"delete-{failure}-{applied}.db");
        var authority = new SyncAuthorityDatabase(databasePath);
        await authority.InitializeAsync("installation-preflight", "profile-cloud-pro", "residual-safety", "{}");
        const string pairId = "pair-delete-response", objectId = "object-folder", versionId = "version-folder",
            memberId = "membership-folder", rootId = "root-delete-response", operationId = "operation-delete-response",
            tombstoneId = "tombstone-delete-response";
        var pair = new SyncPair(pairId, "Fixture", testRoot, 1, 1, "/Pilot", "id:pilot-folder");
        await authority.RegisterPairAsync(pairId, 1);
        var admission = await AdmitFixturePairsAsync(authority, pairId);
        await authority.RegisterRootBindingAsync(rootId, pairId, "dropbox", 1, "{\"folderId\":\"id:pilot-folder\"}", "/Pilot");
        await authority.RegisterObjectAsync(objectId, "directory");
        await authority.RegisterMembershipAsync(memberId, objectId, rootId, "folder", 1);
        await authority.RegisterObjectVersionAsync(versionId, objectId, "dropbox", "{\"fileId\":\"id:folder\",\"revision\":null}", null, 0);
        await authority.RecordTombstoneAsync(tombstoneId, pairId, objectId, "local", null, null, 2);
        await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId, "key-delete-response",
            SyncAuthorityOperationKind.DeleteRemote, pairId, objectId, 1, "profile-cloud-pro",
            JsonSerializer.Serialize(new { tombstoneId }), 100,
            [new SyncAuthorityMember("member-delete-response", pairId, objectId, "source", rootId,
                memberId, versionId, 1, "folder", "{}")], [], []), admission: admission);
        // Seed the historical crash boundary through the real journal APIs. No new
        // recursive Dropbox folder deletion is permitted just to manufacture history.
        var lease = await authority.AcquireOperationLeaseAsync(operationId, "legacy-writer", TimeSpan.FromMinutes(5));
        var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, HashA, "dropbox-delete-directory");
        await authority.RegisterRecoveryArtifactAsync("legacy-directory-recipe", operationId, objectId, null,
            "dropbox", "{\"path\":\"folder\",\"originalPath\":\"folder\",\"kind\":\"directory\",\"recipe\":\"recreate_empty_directory\",\"deletedIdentity\":\"id:folder\",\"provenance\":\"pre_delete_directory_recipe\"}", HashB, 0);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            "{}", "{}", JsonSerializer.Serialize(new { classification = "legacy response unknown", failure }), HashB);
        await authority.ReleaseLeaseAsync(lease);
        var handler = new LegacyDirectoryObservationHandler(applied);
        using var http = new HttpClient(handler);
        var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var remote = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", connection, "id:pilot-folder");
        // Reopen durable state, then try the ordinary executor before reconciliation.
        // Its production status gate, not a test substitute, must prohibit redispatch.
        var reopened = new SyncAuthorityDatabase(databasePath);
        var snapshot = await reopened.LoadOperationAsync(operationId);
        Require(snapshot.Status == SyncAuthorityOperationStatus.EffectUnknown &&
            snapshot.Attempts.Single().Status == SyncAuthorityAttemptStatus.EffectUnknown,
            "Ambiguous response certainty was not persisted across database reopen.");
        await ExpectRejectAsync(() => new SyncAuthorityDeleteRemoteExecutor(pair, remote, reopened)
            .ExecuteAsync(operationId, "worker-blind-retry"));
        Require(handler.DeleteCalls == 0, "An unreconciled legacy operation was dispatched again.");
        var recovered = await new SyncAuthorityDeleteRemoteReconciler(pair, remote, reopened)
            .ReconcileAsync(operationId, "worker-reconcile");
        Require(handler.DeleteCalls == 0 && recovered.Status == (applied
                ? SyncAuthorityOperationStatus.Completed : SyncAuthorityOperationStatus.Ready),
            "Query-only reconciliation repeated the effect or chose the wrong observed state.");
        if (!applied)
        {
            var retry = await new SyncAuthorityDeleteRemoteExecutor(pair, remote, reopened)
                .ExecuteAsync(operationId, "worker-verified-retry");
            Require(!retry.EffectDispatched && retry.Status == SyncAuthorityOperationStatus.Ready &&
                    (await reopened.LoadOperationAsync(operationId)).Status == SyncAuthorityOperationStatus.Ready &&
                    handler.DeleteCalls == 0,
                "A legacy verified-not-applied directory deletion bypassed the new capability guard.");
        }
        Console.WriteLine($"PASS legacy {failure}, applied={applied}: real journal/reconciler, durable unknown, blind-retry refusal, query-only recovery" +
            (applied ? " without redispatch" : " and safe refusal of a new unguarded folder effect"));
    }

    private sealed class LegacyDirectoryObservationHandler(bool applied) : HttpMessageHandler
    {
        private const string Folder = "{\".tag\":\"folder\",\"name\":\"folder\",\"id\":\"id:folder\",\"path_lower\":\"/pilot/folder\",\"path_display\":\"/Pilot/folder\"}";
        internal int DeleteCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
                return Json("{\".tag\":\"folder\",\"name\":\"Pilot\",\"id\":\"id:pilot-folder\",\"path_lower\":\"/pilot\",\"path_display\":\"/Pilot\"}");
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
                return Json("{\"entries\":[" + (applied ? "" : Folder) + "],\"cursor\":\"cursor\",\"has_more\":false}");
            if (route.EndsWith("/files/delete_v2", StringComparison.Ordinal)) DeleteCalls++;
            throw new InvalidOperationException("Legacy reconciliation must remain read-only: " + route);
        }
        private static Task<HttpResponseMessage> Json(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(value, Encoding.UTF8, "application/json") });
    }

    internal static async Task RunUiReliabilityDeltaAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local identity contract requires Windows.");
        var testRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "ui-reliability-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var localRoot = Path.Combine(testRoot, "pair");
            Directory.CreateDirectory(localRoot);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "live-action.txt"), "live activity payload");
            await File.WriteAllTextAsync(Path.Combine(localRoot, "LICENSE"), "extensionless live activity payload");
            Directory.CreateDirectory(Path.Combine(localRoot, "archive.v1"));
            PilotDirectoryIdentity identity;
            using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
            const string installationId = "installation-ui-reliability";
            const string namespaceId = "ui-reliability-control";
            SyncLocalControlNamespaceBinding control;
            using (var lease = PilotDirectoryLease.Open(localRoot, identity))
                control = SyncControlNamespace.BindLocalForTest(lease, installationId, namespaceId);
            var pair = new SyncPair("pair-ui-reliability", "Documents", localRoot,
                identity.VolumeSerialNumber, identity.FileId, "/Documents", "id:documents",
                ControlNamespace: new SyncControlNamespaceBinding(installationId, namespaceId, control));
            var authority = new SyncAuthorityDatabase(Path.Combine(testRoot, "authority.db"));
            await authority.InitializeAsync(installationId, "profile-ui-reliability", "ui-reliability", "{}");
            await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
            var admission = await AdmitFixturePairsAsync(authority, pair.Id);
            await authority.RegisterRootBindingAsync("root-ui-local", pair.Id, "local", 1,
                JsonSerializer.Serialize(new
                {
                    volumeSerial = identity.VolumeSerialNumber.ToString("x8"),
                    fileId = identity.FileId.ToString("x16")
                }), localRoot);
            await authority.RegisterRootBindingAsync("root-ui-dropbox", pair.Id, "dropbox", 1,
                "{\"folderId\":\"id:documents\"}", "/Documents");
            var remote = new FakeSyncRemoteStore();
            var options = new SyncAuthorityPlannerOptions(1, "profile-ui-reliability", namespaceId, namespaceId,
                EnableDeletionEffects: true, EnableMoveEffects: true, DeletionMinimumInterval: TimeSpan.Zero);
            var runner = new SyncAuthorityPairRunner(pair, remote, authority, "root-ui-local",
                "root-ui-dropbox", options, new PilotRunControl(), admission);
            var observer = new SyncAuthorityObservationCoordinator(pair, remote, authority,
                "root-ui-local", "root-ui-dropbox");
            await observer.ObserveAsync("initial");
            var observed = await observer.ObserveAsync("initial");
            await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
            var observationSet = await authority.LoadPlanningObservationSetAsync(pair.Id,
                options.ConfigurationRevision, "root-ui-local", "root-ui-dropbox",
                observed.LocalScan.ScanGenerationId, observed.DropboxScan.ScanGenerationId);
            var plan = await runner.PlanBoundAsync(observationSet, "focused-ui-reliability-observation");
            var actions = new List<SyncAuthorityLiveAction>();
            var result = await runner.ExecuteBoundAsync(plan, onAction: action =>
            {
                actions.Add(action);
                return Task.CompletedTask;
            });
            Require(result.CompletedEffects == 3 && remote.ReadText("live-action.txt") == "live activity payload" &&
                    remote.ReadText("LICENSE") == "extensionless live activity payload" &&
                    remote.DirectoryExists("archive.v1"),
                "The focused live-action fixture did not execute the real upload path.");
            var expectedTypes = new Dictionary<string, (string ItemKind, string FileType)>(StringComparer.Ordinal)
            {
                ["live-action.txt"] = ("File", "TXT"),
                ["LICENSE"] = ("File", "File"),
                ["archive.v1"] = ("Folder", "Folder")
            };
            Require(actions.Select(item => item.RelativePath).GroupBy(path => path, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
                        .OrderBy(item => item.Key, StringComparer.Ordinal)
                        .SequenceEqual(expectedTypes.Keys.Order(StringComparer.Ordinal)
                            .Select(path => new KeyValuePair<string, int>(path, 2))),
                "The live action stream did not contain exactly two events for each of the three expected paths.");
            var operationIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, expectedType) in expectedTypes)
            {
                var pathActions = actions.Where(item => string.Equals(item.RelativePath, path,
                    StringComparison.Ordinal)).ToArray();
                Require(pathActions.Length == 2 && pathActions[0].Stage == "Starting" &&
                        pathActions[1].Stage == "Completed" && pathActions.All(item =>
                            item.Action == "Upload" && item.ItemKind == expectedType.ItemKind &&
                            item.FileType == expectedType.FileType &&
                            string.Equals(item.OperationId, pathActions[0].OperationId, StringComparison.Ordinal)) &&
                        !string.IsNullOrWhiteSpace(pathActions[0].OperationId),
                    $"The live action stream did not preserve the ordered authoritative lifecycle for {path}.");
                operationIds[path] = pathActions[0].OperationId;
            }
            var operationId = operationIds["live-action.txt"];
            var activity = await authority.LoadActivityProjectionPageAsync(100, 0, "live-action.txt", "Transfers");
            Require(activity.Any(item => item.OperationId == operationId && item.RelativePath == "live-action.txt" &&
                                         item.Outcome is "Started upload" or "Completed upload" or "Uploaded"),
                "The full Activity projection did not retain the upload lifecycle under its pair and path filters.");
            var typedActivity = await authority.LoadActivityProjectionPageAsync(100, 0, null, "All actions",
                "TXT", pair.Id, DateTimeOffset.UtcNow.AddHours(-1));
            Require(typedActivity.Count > 0 && typedActivity.All(item => item.PairId == pair.Id &&
                        item.ItemKind == "File" && item.FileType == "TXT"),
                "The retained Activity projection did not apply its pair, date, item-kind and file-type filters together.");
            var rejectedType = await authority.LoadActivityProjectionPageAsync(100, 0, null, "All actions",
                "PDF", pair.Id, DateTimeOffset.UtcNow.AddHours(-1));
            Require(rejectedType.Count == 0,
                "The retained Activity projection admitted a non-matching file type.");
            var retainedFiles = await authority.LoadActivityProjectionPageAsync(100, 0, "LICENSE", "All actions",
                "Files", pair.Id, DateTimeOffset.UtcNow.AddHours(-1));
            Require(retainedFiles.Count > 0 && retainedFiles.All(item =>
                        item.OperationId == operationIds["LICENSE"] && item.RelativePath == "LICENSE" &&
                        item.ItemKind == "File" && item.FileType == "File") &&
                    retainedFiles.Any(item => item.Outcome == "Uploaded"),
                "The retained Activity projection misclassified an extensionless file.");
            var rejectedFileAsFolder = await authority.LoadActivityProjectionPageAsync(100, 0, "LICENSE",
                "All actions", "Folders", pair.Id, DateTimeOffset.UtcNow.AddHours(-1));
            Require(rejectedFileAsFolder.Count == 0,
                "The retained Activity projection admitted the extensionless file under Folders.");
            var retainedFolders = await authority.LoadActivityProjectionPageAsync(100, 0, "archive.v1", "All actions",
                "Folders", pair.Id, DateTimeOffset.UtcNow.AddHours(-1));
            Require(retainedFolders.Count > 0 && retainedFolders.All(item =>
                        item.OperationId == operationIds["archive.v1"] && item.RelativePath == "archive.v1" &&
                        item.ItemKind == "Folder" && item.FileType == "Folder") &&
                    retainedFolders.Any(item => item.Outcome == "Uploaded"),
                "The retained Activity projection misclassified a dotted directory.");
            var rejectedFolderAsFile = await authority.LoadActivityProjectionPageAsync(100, 0, "archive.v1",
                "All actions", "Files", pair.Id, DateTimeOffset.UtcNow.AddHours(-1));
            Require(rejectedFolderAsFile.Count == 0,
                "The retained Activity projection admitted the dotted directory under Files.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    internal static async Task RunCrossPairLiveActionDeltaAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cross-pair local identity tests require Windows.");
        var testRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "ui-reliability-cross-pair-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var authority = new SyncAuthorityDatabase(Path.Combine(testRoot, "authority.db"));
            await authority.InitializeAsync("installation-cross-pair-live", "profile-cloud-pro",
                "cross-pair-live-action", "{}");
            await AssertCrossPairLiveActionMetadataAsync(authority, testRoot);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    internal static async Task RunLocalCreateReceiptRecoveryAsync(string testRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native local receipt recovery requires Windows.");
        Directory.CreateDirectory(testRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        foreach (var directory in new[] { false, true })
        foreach (var phase in new[] { "receipt-before-version-registration", "membership-before-commit", "commit-before-cleanup" })
            await CheckAsync(directory, phase, "unchanged");
        foreach (var change in new[] { "different-identity", "absent", "changed-content" })
            await CheckAsync(false, "membership-before-commit", change);
        await CheckAsync(true, "membership-before-commit", "different-identity");
        Console.WriteLine("PASS 10 native local-create receipt/commit recovery cases; no repeated downloads or overwritten foreign targets");

        async Task CheckAsync(bool directory, string phase, string change)
        {
            var caseName = (directory ? "directory-" : "file-") + phase + "-" + change;
            var caseRoot = Path.Combine(testRoot, caseName);
            const string path = "item";
            var (pair, authority, remote, options, operationId, admission) = await LocalCreateRecoveryFixtureAsync(caseRoot, directory, token);
            var root = pair.LocalRoot;
            var reached = false;
            SyncAuthorityCreateLocalExecutor.PhaseForTest = boundary =>
            {
                if (boundary == phase) { reached = true; throw new IOException("simulated retained receipt interruption"); }
            };
            try
            {
                await ExpectRejectAsync(() => new SyncAuthorityCreateLocalExecutor(pair, remote, authority)
                    .ExecuteAsync(operationId, "worker-create-local", token));
            }
            finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
            Require(reached, "The expected native crash boundary was not executed: " + caseName);
            var retained = await authority.LoadOperationAsync(operationId, token);
            Require(retained.Attempts.Count == 1 && retained.Status ==
                (phase == "commit-before-cleanup" ? SyncAuthorityOperationStatus.Committed : SyncAuthorityOperationStatus.EffectObserved),
                "The test did not retain its exact receipt/commit boundary: " + caseName);
            var target = Path.Combine(root, path);
            var preserved = Path.Combine(root, "preserved-original");
            if (change == "different-identity" || change == "absent")
            {
                if (directory) Directory.Move(target, preserved); else File.Move(target, preserved);
                if (change == "different-identity")
                {
                    if (directory) Directory.CreateDirectory(target); else await File.WriteAllTextAsync(target, "exact cloud content", token);
                }
            }
            if (change == "changed-content") await File.WriteAllTextAsync(target, "foreign modified content", token);
            var membershipCount = await ScalarLongAsync(authority.DatabasePath, "SELECT count(*) FROM membership;");
            var downloads = remote.DownloadCount;
            var uploads = remote.UploadCount;
            if (change != "unchanged")
            {
                await ExpectRejectAsync(() => new SyncAuthorityCreateLocalReconciler(pair, authority)
                    .ReconcileAsync(operationId, "worker-create-local-reconcile", token));
                Require((await authority.LoadOperationAsync(operationId, token)).Status != SyncAuthorityOperationStatus.Completed &&
                    await ScalarLongAsync(authority.DatabasePath, "SELECT count(*) FROM membership;") == membershipCount &&
                    (change != "changed-content" || await File.ReadAllTextAsync(target, token) == "foreign modified content") &&
                    (change != "absent" || !File.Exists(target)),
                    "Recovery falsely accepted or overwrote a changed/missing target: " + caseName);
            }
            else
            {
                // Exercise the actual production dispatcher, then a second recovery
                // pass to prove it does not replay completed effects or memberships.
                var runner = new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
                    "root-pair-runner-dropbox", options, new PilotRunControl(), admission);
                await runner.RecoverAsync(token);
                retained = await authority.LoadOperationAsync(operationId, token);
                Require(retained.Status == SyncAuthorityOperationStatus.Completed &&
                    retained.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
                    "The actual dispatcher failed to finish the retained successful create: " + caseName);
                var completedMembershipCount = await ScalarLongAsync(authority.DatabasePath, "SELECT count(*) FROM membership;");
                await runner.RecoverAsync(token);
                Require(await ScalarLongAsync(authority.DatabasePath, "SELECT count(*) FROM membership;") == completedMembershipCount &&
                    (phase != "membership-before-commit" || membershipCount == completedMembershipCount) &&
                    await ScalarLongAsync(authority.DatabasePath, "SELECT count(*) FROM synchronization_point;") == 1 &&
                    (directory ? Directory.Exists(target) : await File.ReadAllTextAsync(target, token) == "exact cloud content"),
                    "The receipt replay duplicated membership/convergence or lost target content: " + caseName);
            }
            Require(remote.DownloadCount == downloads && remote.UploadCount == uploads &&
                downloads == (directory ? 0 : 1), "Recovery repeated a provider effect: " + caseName);
            Console.WriteLine("PASS " + caseName + "; exact receipt retained; repeated provider effects=0");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(SyncPair Pair, SyncAuthorityDatabase Authority, FakeSyncRemoteStore Remote,
        SyncAuthorityPlannerOptions Options, string OperationId, SyncAuthorityCommandAdmission Admission)> LocalCreateRecoveryFixtureAsync(
        string caseRoot, bool directory, CancellationToken token, bool additionalFile = false)
    {
        var root = Path.Combine(caseRoot, "local");
        Directory.CreateDirectory(root);
        PilotDirectoryIdentity identity;
        using (var held = PilotDirectoryLease.Open(root)) identity = held.Identity;
        const string installation = "installation-pair-runner";
        const string namespaceId = "pair-runner-control";
        SyncLocalControlNamespaceBinding localControl;
        using (var held = PilotDirectoryLease.Open(root, identity))
            localControl = SyncControlNamespace.BindLocalForTest(held, installation, namespaceId);
        var pair = new SyncPair("pair-runner", "Runner", root, identity.VolumeSerialNumber, identity.FileId,
            "/Runner", "id:runner", ControlNamespace: new SyncControlNamespaceBinding(installation, namespaceId, localControl));
        var authority = new SyncAuthorityDatabase(Path.Combine(caseRoot, "authority.db"));
        await authority.InitializeAsync(installation, "profile-pair-runner", "pair-runner-source", "{}", token);
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision, token);
        var admission = await AdmitFixturePairsAsync(authority, pair.Id);
        await authority.RegisterRootBindingAsync("root-pair-runner-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { volumeSerial = identity.VolumeSerialNumber.ToString("x8"),
                fileId = identity.FileId.ToString("x16") }), root, token);
        await authority.RegisterRootBindingAsync("root-pair-runner-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:runner\"}", "/Runner", token);
        var remote = new FakeSyncRemoteStore();
        if (directory) remote.PutDirectory("item"); else remote.Put("item", "exact cloud content");
        if (additionalFile) remote.Put("unrelated", "must not transfer or retire");
        var coordinator = new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox");
        await coordinator.ObserveAsync("initial", token);
        await coordinator.ObserveAsync("initial", token);
        await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id, token);
        var options = new SyncAuthorityPlannerOptions(1, "profile-pair-runner", namespaceId, namespaceId);
        var plan = (await new SyncAuthorityReconciliationPlanner(authority, options, admission: admission).PlanAsync(pair.Id,
            cancellationToken: token)).Single(item => item.RelativePath == "item");
        Require(plan.OperationId is not null, "The native recovery fixture did not plan its exact create.");
        return (pair, authority, remote, options, plan.OperationId!, admission);
    }

    internal static async Task RunLocalCreatePreparationRecoveryAsync(string testRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native preparation recovery requires Windows.");
        Directory.CreateDirectory(testRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var cases = 0;
        foreach (var verifyOnly in new[] { false, true })
        {
            foreach (var phase in new[] { "transaction-created-before-stage", "transaction-bound-before-stage", "transaction-verified-before-stage" })
            foreach (var change in new[] { "unchanged", "different-identity", "unexpected-content", "changed-acl", "active-lease" })
                await CheckAsync(phase, change, verifyOnly);
            await CheckAsync("transaction-verified-before-stage", "missing-verified", verifyOnly);
            await CheckAsync("transaction-bound-before-stage", "missing-bound", verifyOnly);
        }
        await CheckAsync("receipt-before-version-registration", "issued", false);
        await CheckScopedUnissuedLeaseAsync();
        Console.WriteLine($"PASS {cases} native pre-attempt recovery cases; no repeated provider effects or user-file changes");

        async Task CheckScopedUnissuedLeaseAsync()
        {
            var (pair, authority, remote, options, operationId, admission) = await LocalCreateRecoveryFixtureAsync(
                Path.Combine(testRoot, "exact-run-unissued-lease"), false, token, additionalFile: true);
            var unrelatedId = await ScalarStringAsync(authority.DatabasePath,
                "SELECT operation_id FROM operation WHERE kind='create_local' AND operation_id<>'" + operationId + "';");
            Require(!string.IsNullOrWhiteSpace(unrelatedId), "The scope fixture requires unrelated pending work.");
            foreach (var id in new[] { operationId, unrelatedId! })
            {
                var lease = await authority.AcquireOperationLeaseAsync(id, "interrupted-no-attempt", TimeSpan.FromMinutes(5), token);
                await authority.ReleaseLeaseAsync(lease, token);
            }
            var runner = new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
                "root-pair-runner-dropbox", options, new PilotRunControl(), admission);
            await runner.VerifyAsync([], token);
            Require((await authority.LoadOperationAsync(operationId, token)).Status == SyncAuthorityOperationStatus.Leased,
                "An empty selected run must not expand into pair-wide recovery.");
            await runner.VerifyAsync([operationId], token);
            var selected = await authority.LoadOperationAsync(operationId, token);
            var unrelated = await authority.LoadOperationAsync(unrelatedId!, token);
            Require(selected.Status == SyncAuthorityOperationStatus.Superseded && selected.Attempts.Count == 0 &&
                unrelated.Status == SyncAuthorityOperationStatus.Leased && unrelated.Attempts.Count == 0 &&
                remote.DownloadCount == 0 && remote.UploadCount == 0 &&
                !File.Exists(Path.Combine(pair.LocalRoot, "item")) && !File.Exists(Path.Combine(pair.LocalRoot, "unrelated")),
                "Verify retires only its selected unissued lease, without any new effect or unrelated recovery.");
            await runner.VerifyAsync([operationId], token);
            Require(await ScalarLongAsync(authority.DatabasePath,
                "SELECT count(*) FROM history_event WHERE event_kind='unissued_lease_recovered';") == 1,
                "Repeated selected verification must not repeat the recovery effect.");
            cases++;
            Console.WriteLine("PASS exact-run unissued lease verification: empty-scope rejection, unrelated-state preservation and no replay");
        }

        async Task CheckAsync(string phase, string change, bool verifyOnly)
        {
            var name = phase + "-" + change + (verifyOnly ? "-verify" : "-recover");
            var (pair, authority, remote, options, operationId, admission) =
                await LocalCreateRecoveryFixtureAsync(Path.Combine(testRoot, name), directory: false, token);
            var reached = false;
            SyncAuthorityCreateLocalExecutor.PhaseForTest = boundary =>
            {
                if (boundary == phase) { reached = true; throw new IOException("simulated preparation interruption"); }
            };
            try
            {
                await ExpectRejectAsync(() => new SyncAuthorityCreateLocalExecutor(pair, remote, authority)
                    .ExecuteAsync(operationId, "prepare-local", token));
            }
            finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
            Require(reached, "The actual preparation boundary was not reached: " + name);
            var before = await authority.LoadOperationAsync(operationId, token);
            var tx = before.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
            var txPath = Path.Combine(pair.ControlNamespace!.Local.RootPath, tx.DeterministicName);
            var downloads = remote.DownloadCount;
            var uploads = remote.UploadCount;
            var target = Path.Combine(pair.LocalRoot, "item");
            if (change == "issued")
            {
                await ExpectRejectAsync(() => new SyncAuthorityCreateLocalReconciler(pair, authority)
                    .ReconcileUnissuedPreparationAsync(operationId, "refuse-issued", token));
                Require(File.Exists(target) && (await authority.LoadOperationAsync(operationId, token)).Status == before.Status,
                    "Unissued cleanup consumed an issued effect.");
            }
            else
            {
                Require(before.Attempts.Count == 0 && before.Status == SyncAuthorityOperationStatus.Leased,
                    "The actual pre-attempt state was not retained: " + name);
                if (change == "different-identity")
                {
                    Directory.Move(txPath, txPath + "-preserved");
                    Directory.CreateDirectory(txPath);
                    SyncLocalTransactionDirectory.ApplyRestrictiveSecurity(txPath);
                }
                if (change == "unexpected-content") await File.WriteAllTextAsync(Path.Combine(txPath, "foreign.txt"), "preserve", token);
                if (change == "changed-acl")
                {
                    var security = new DirectoryInfo(txPath).GetAccessControl();
                    security.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                        FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                    new DirectoryInfo(txPath).SetAccessControl(security);
                }
                if (change is "missing-bound" or "missing-verified") Directory.Delete(txPath);
                // Cleanup is not a new download: exercise the production branch
                // with a direction which now prohibits create_local effects.
                var runner = new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
                    "root-pair-runner-dropbox", options with { Direction = SyncMode.UploadOnly }, new PilotRunControl(), admission);
                SyncAuthorityLease? active = null;
                if (change == "active-lease")
                    active = await authority.AcquireOperationLeaseAsync(operationId, "active-holder", TimeSpan.FromMinutes(5), token);
                try
                {
                    if (verifyOnly && active is not null)
                        // Verification cannot steal an active execution lease. Its explicit
                        // refusal is expected; the unchanged-state assertions below still apply.
                        await ExpectRejectAsync(() => runner.VerifyAsync([operationId], token));
                    else if (verifyOnly) await runner.VerifyAsync([operationId], token);
                    else await runner.RecoverAsync(token);
                    var after = await authority.LoadOperationAsync(operationId, token);
                    if (change is "unchanged" or "missing-verified")
                    {
                        Require(after.Status == SyncAuthorityOperationStatus.Superseded && after.Attempts.Count == 0 &&
                            after.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).Status == SyncAuthorityAllocationStatus.Released &&
                            !Directory.Exists(txPath) && !File.Exists(target), "Owned preparation was not safely retired: " + name);
                        await authority.EnsureReviewedRunCanStartAsync(pair.Id, token);
                        var recovered = await ScalarLongAsync(authority.DatabasePath, "SELECT count(*) FROM history_event WHERE event_kind='unissued_preparation_recovered';");
                        if (verifyOnly) await runner.VerifyAsync([operationId], token);
                        else await runner.RecoverAsync(token);
                        Require(recovered == 1 && await ScalarLongAsync(authority.DatabasePath,
                            "SELECT count(*) FROM history_event WHERE event_kind='unissued_preparation_recovered';") == 1,
                            "A second recovery repeated the retirement: " + name);
                    }
                    else
                    {
                        Require(after.Status == SyncAuthorityOperationStatus.Leased && after.Attempts.Count == 0 && !File.Exists(target) &&
                            (change == "missing-bound" || Directory.Exists(txPath)), "Unsafe preparation was deleted or accepted: " + name);
                        await ExpectRejectAsync(() => authority.EnsureReviewedRunCanStartAsync(pair.Id, token));
                        if (change != "active-lease")
                            Require(await ScalarLongAsync(authority.DatabasePath,
                                "SELECT count(*) FROM history_event WHERE event_kind='execution_failed';") == 1,
                                "The exact refusal was lost behind an invalid leased-to-blocked transition: " + name);
                        if (change == "unexpected-content") Require(await File.ReadAllTextAsync(Path.Combine(txPath, "foreign.txt"), token) == "preserve",
                            "Recovery removed unexpected contents.");
                    }
                }
                finally { if (active is not null) await authority.ReleaseLeaseAsync(active, token); }
            }
            Require(remote.DownloadCount == downloads && remote.UploadCount == uploads &&
                (change == "issued" || downloads == 0), "Preparation recovery issued a provider effect: " + name);
            cases++;
            Console.WriteLine("PASS " + name + "; provider effects during recovery=0");
        }
    }

    internal static async Task RunRecoveryCorrectionAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS recovery proof requires Windows.");
        var testRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "cloud-pro-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var authority = new SyncAuthorityDatabase(Path.Combine(testRoot, "authority.db"));
            await authority.InitializeAsync("installation-preflight", "profile-cloud-pro", "cloud-pro-preflight", "{}");
            await AssertObservationCoordinatorAsync(authority, testRoot, recoveryCorrectionOnly: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testRoot, recursive: true);
        }
    }

    public static async Task RunAsync()
    {
        AssertStablePairIdentity();
        if (OperatingSystem.IsWindows())
        {
            Require(string.Equals(SyncControlNamespace.LocalRootFor(@"D:\Sync", "direct-child-root"),
                    @"D:\.syncshark-control-direct-child-root", StringComparison.OrdinalIgnoreCase),
                "A selected root directly beneath a volume root did not resolve to a valid same-volume control root.");
        }
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "cloud-pro-tests"));
        var testRoot = Path.GetFullPath(Path.Combine(allowedRoot, Guid.NewGuid().ToString("N")));
        if (!testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The authority preflight root escaped its registered test allocation.");
        Directory.CreateDirectory(testRoot);

        try
        {
            var databasePath = Path.Combine(testRoot, "authority.db");
            var authority = new SyncAuthorityDatabase(databasePath);
            await authority.InitializeAsync("installation-preflight", "profile-cloud-pro", "cloud-pro-preflight", "{}");
            await AssertRuntimeAsync(authority, databasePath);
            await AssertSchemaInventoryAsync(databasePath);
            await AssertPairDefinitionAuthorityAsync(authority, testRoot);
            await AssertCommandRequestLifecycleAsync(authority);
            await AssertVersion2MigrationAsync(testRoot);
            await AssertInitializationAndStorageFailuresAsync(testRoot);
            await AssertWalCrashRecoveryAsync(testRoot);
            if (OperatingSystem.IsWindows())
                await AssertFreshCreateCrashBoundariesAsync(testRoot);
            if (OperatingSystem.IsWindows())
                await AssertControlAllocationCrashRecoveryAsync(testRoot);
            var admission = await SeedIdentityGraphAsync(authority);
            await AssertUploadSessionCheckpointAuthorityAsync(authority, admission);
            await AssertAtomicPathReservationAsync(authority, admission);
            await AssertEffectFreeObservationsAsync(authority);
            await AssertUncertainInitialDiscoveryAsync(authority, testRoot);
            await AssertObservationCoordinatorAsync(authority, testRoot);
            await AssertAncestorUncertaintyFencesAsync(authority, testRoot);
            await AssertNamespaceCorrectnessAsync(testRoot);
            await AssertDropboxRevisionChurnMoveGuardsAsync(testRoot);
            await AssertCurrentObservationIdentityResolutionAsync(authority);
            await AssertPairRunnerAsync(testRoot);
            await AssertMonitorBatchAtomicityAsync(authority, databasePath);

            await authority.CreateOperationAsync(Plan("operation-prerequisite-a"), admission: admission);
            await authority.CreateOperationAsync(Plan("operation-prerequisite-b"), admission: admission);
            await authority.CreateOperationAsync(Plan(
                "operation-frozen",
                new[] { new SyncAuthorityDependency("operation-prerequisite-a", "must_resolve") }), admission: admission);
            await authority.CreateOperationAsync(Plan(
                "operation-main",
                dependencies: null,
                allocations: new[]
                {
                    new SyncAuthorityAllocation("allocation-main", "control-local", SyncAuthorityAllocationKind.StageFile,
                        "stage-operation-main", "root-local-generation-1", HashA)
                }), admission: admission);
            await AssertRecoveryProjectionPagingAsync(authority);

            await AssertFrozenPlansAsync(authority, databasePath);
            await AssertFencedAttemptsAndReconciliationAsync(authority, databasePath);
            await AssertConcurrentLeaseUniquenessAsync(authority, databasePath, admission);
            await AssertNamespaceAllocationAuthorityAsync(authority, admission);
            if (OperatingSystem.IsWindows())
                await AssertAuthorizedLocalControlAllocatorAsync(authority, testRoot);

            Require(await ScalarLongAsync(databasePath,
                    "SELECT count(*) FROM operation WHERE operation_id IN ('operation-prerequisite-a','operation-prerequisite-b','operation-frozen','operation-main','operation-concurrent','operation-namespace-allocation');") == 6,
                "Independent operations were not retained as six separate authority records.");
            Require(string.Equals(await authority.QuickCheckAsync(), "ok", StringComparison.Ordinal),
                "SQLite quick_check failed after the authority transition witnesses.");
            Require(string.Equals(await authority.IntegrityCheckAsync(), "ok", StringComparison.Ordinal),
                "SQLite integrity_check failed after the authority transition witnesses.");

            await AssertConditionalDeleteFailureClassificationAsync();
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    internal static async Task RunStrictStartupPreflightAsync()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "strict-startup-tests"));
        var testRoot = Path.GetFullPath(Path.Combine(allowedRoot, Guid.NewGuid().ToString("N")));
        if (!testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The strict-startup preflight root escaped its registered test allocation.");
        Directory.CreateDirectory(testRoot);
        try
        {
            await AssertInitializationAndStorageFailuresAsync(testRoot);
            if (OperatingSystem.IsWindows()) await AssertFreshCreateCrashBoundariesAsync(testRoot);
        }
        finally
        {
            SyncAuthorityDatabase.FreshCreateBoundaryForTest = null;
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    internal static async Task RunVersion2MigrationPreflightAsync()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "migration-contract-tests"));
        var testRoot = Path.GetFullPath(Path.Combine(allowedRoot, Guid.NewGuid().ToString("N")));
        if (!testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The migration preflight root escaped its registered test allocation.");
        Directory.CreateDirectory(testRoot);
        try { await AssertVersion2MigrationAsync(testRoot); }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    internal static async Task RunInstalledStateMigrationPreflightAsync(string sourceStateRoot,
        string sourceVersion2Database)
    {
        sourceStateRoot = Path.GetFullPath(sourceStateRoot);
        sourceVersion2Database = Path.GetFullPath(sourceVersion2Database);
        if (!Directory.Exists(sourceStateRoot) || !File.Exists(sourceVersion2Database))
            throw new DirectoryNotFoundException(
                "The exact installed v0.2.4 state root or version-2 authority database is unavailable.");

        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "installed-migration-contract-tests"));
        var testRoot = Path.GetFullPath(Path.Combine(allowedRoot, Guid.NewGuid().ToString("N")));
        if (!testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed-state migration root escaped its test allocation.");
        Directory.CreateDirectory(testRoot);
        var sourceHash = HashFile(sourceVersion2Database);
        var sourceWriteUtc = File.GetLastWriteTimeUtc(sourceVersion2Database);
        try
        {
            await AssertExactVersion2CloneMigrationAsync(sourceVersion2Database, testRoot);

            var clonedStateRoot = Path.Combine(testRoot, "state");
            Directory.CreateDirectory(clonedStateRoot);
            foreach (var source in Directory.EnumerateFiles(sourceStateRoot, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => !string.Equals(Path.GetFileName(path),
                             SyncSharkRuntimePaths.InstanceLockFileName, StringComparison.OrdinalIgnoreCase)))
                File.Copy(source, Path.Combine(clonedStateRoot, Path.GetFileName(source)), overwrite: false);
            var stateHashes = Directory.EnumerateFiles(clonedStateRoot, "*", SearchOption.TopDirectoryOnly)
                .ToDictionary(path => Path.GetFileName(path)!, HashFile, StringComparer.OrdinalIgnoreCase);

            var databasePath = Path.Combine(testRoot, "vNext", "state", "syncshark.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            File.Copy(sourceVersion2Database, databasePath, overwrite: false);
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(clonedStateRoot,
                SyncSharkRuntimePaths.InstanceLockFileName));
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = connectionStore.Load()
                             ?? throw new InvalidDataException(
                                 "The exact installed state clone has no Dropbox account binding.");
            var legacyPairs = new SyncPairStore(appLock.StateAuthority).Load();
            Require(legacyPairs.Pairs.Count > 0,
                "The exact installed state clone has no pair continuity witness.");
            var legacyRuntime = new SyncRuntimeStateStore(appLock.StateAuthority);
            var legacyRuntimeByPair = legacyPairs.Pairs.ToDictionary(item => item.Id,
                item => legacyRuntime.LoadPair(item.Id), StringComparer.Ordinal);
            var legacyHistory = new SyncHistoryStore(appLock.StateAuthority).Load();
            var automationBefore = new SyncAutomationStore(appLock.StateAuthority).Load();
            var providerGuard = new NoProviderCallsHandler();
            using var http = new HttpClient(providerGuard);
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http,
                new SyncPairStore(appLock.StateAuthority), legacyRuntime, connectionStore,
                new MemoryDropboxRefreshTokenStore("protected-test-token"),
                new SyncAuthorityDatabase(databasePath));
            var migrated = await host.LoadPairsAsync();

            Require(providerGuard.Calls == 0,
                "Installed-state migration attempted a provider request.");
            Require(migrated.Pairs.Count == legacyPairs.Pairs.Count &&
                    legacyPairs.Pairs.All(source => migrated.Pairs.Any(item =>
                        string.Equals(item.Id, source.Id, StringComparison.Ordinal) &&
                        string.Equals(item.ConnectionId, connection.ConnectionId, StringComparison.Ordinal) &&
                        string.Equals(item.DropboxFolderId, source.DropboxFolderId, StringComparison.Ordinal))) &&
                    string.Equals(migrated.SelectedPairId, legacyPairs.SelectedPairId,
                        StringComparison.Ordinal),
                "Installed pair or account/namespace identity was not retained through migration.");
            Require(await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM sync_pair WHERE pair_json IS NOT NULL AND state='discovery_required';") ==
                    legacyPairs.Pairs.Count,
                "Migrated pairs were not retained as effect-free recursive-discovery candidates.");
            Require(await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM migration_import WHERE source_kind='legacy_0_1_1_pairs' AND status='accepted';") ==
                    legacyPairs.Pairs.Count,
                "Pair baselines, pending operation state, and recovery identities lack immutable migration receipts.");
            foreach (var source in legacyPairs.Pairs)
            {
                var runtime = legacyRuntimeByPair[source.Id];
                var sourceJson = JsonSerializer.Serialize(new
                {
                    Pair = SyncPairStore.NormalizePair(source),
                    Runtime = SyncRuntimeStateStore.NormalizePair(runtime)
                });
                var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson)))
                    .ToLowerInvariant();
                Require(await ScalarLongAsync(databasePath,
                            "SELECT count(*) FROM migration_import WHERE source_identity=$source AND source_sha256=$hash AND json_extract(imported_json,'$.RequiresRecursiveBootstrap')=1;",
                            ("$source", source.Id), ("$hash", expectedHash)) == 1,
                    "A legacy pair/runtime payload did not survive migration byte-identically.");
            }
            Require(await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM reconciliation_decision WHERE json_extract(decision_json,'$.migrated')=1;") ==
                    legacyHistory.Entries.Count(item => migrated.Pairs.Any(pair => pair.Id == item.PairId)),
                "Legacy activity/conflict history was not retained in the scalable activity projection.");
            Require(new SyncAutomationStore(appLock.StateAuthority).Load() == automationBefore,
                "Automatic-sync state changed during migration.");
            foreach (var item in stateHashes)
                Require(string.Equals(HashFile(Path.Combine(clonedStateRoot, item.Key)), item.Value,
                        StringComparison.Ordinal),
                    "Migration rewrote retained v0.2.4 state instead of importing it transactionally.");
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
            Require(string.Equals(HashFile(sourceVersion2Database), sourceHash, StringComparison.Ordinal) &&
                    File.GetLastWriteTimeUtc(sourceVersion2Database) == sourceWriteUtc,
                "The installed v0.2.4 rollback database changed during clone migration testing.");
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    internal static async Task RunCrossPairMovePreflightAsync()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncShark", "TestFixtures", "cross-pair-tests"));
        var testRoot = Path.Combine(allowedRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var authority = new SyncAuthorityDatabase(Path.Combine(testRoot, "authority.db"));
            await authority.InitializeAsync("installation-cross-pair", "profile-cloud-pro",
                "cross-pair-preflight", "{}");
            await AssertCrossPairMoveAsync(authority, testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }

    private static void AssertStablePairIdentity()
    {
        var original = new PilotSettings(@"C:\Original display path", 0x1234ABCD, 0x1122334455667788,
            "/Original Dropbox Path", "id:stable-dropbox-folder");
        var renamed = original with
        {
            LocalRoot = @"C:\Renamed display path",
            DropboxRoot = "/Renamed Dropbox Path"
        };
        Require(string.Equals(SyncPair.DeriveId(original, "root-namespace-a"),
                SyncPair.DeriveId(renamed, "root-namespace-a"), StringComparison.Ordinal),
            "Pair identity changed when only local or Dropbox display paths changed.");
        Require(!string.Equals(SyncPair.DeriveId(original, "root-namespace-a"),
                SyncPair.DeriveId(original, "root-namespace-b"), StringComparison.Ordinal),
            "Pair identity did not include the verified Dropbox root namespace.");
    }

    internal static async Task RunWalCrashProbeAsync(string databasePath)
    {
        var authority = new SyncAuthorityDatabase(databasePath);
        await authority.InitializeAsync("installation-wal", "profile-wal", "wal-crash-probe", "{}");
        var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA wal_autocheckpoint=0;
            BEGIN IMMEDIATE;
            INSERT INTO logical_object(object_id,object_kind,created_utc)
            VALUES('wal-survivor','file','2026-09-02T00:00:00.0000000Z');
            COMMIT;
            """;
        await command.ExecuteNonQueryAsync();
        NativeTestFiles.TerminateCurrentProcess(203);
    }

    internal static async Task RunFreshCreateCrashProbeAsync(string mode, string databasePath)
    {
        var authority = new SyncAuthorityDatabase(databasePath);
        var boundary = mode switch
        {
            "parent" => "parent-ready-before-file-create",
            "created" => "file-created-before-sqlite-open",
            "schema" => "schema-applied-before-version-commit",
            "committed" => "current-schema-committed",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown fresh-create crash boundary.")
        };
        SyncAuthorityDatabase.FreshCreateBoundaryForTest = phase =>
        {
            if (phase == boundary) NativeTestFiles.TerminateCurrentProcess(211);
        };
        await authority.InitializeAsync("installation-fresh-crash", "profile-fresh-crash",
            "fresh-create-crash-probe", "{}");
        throw new InvalidOperationException($"The authority fresh-create crash probe missed its {mode} boundary.");
    }

    [SupportedOSPlatform("windows")]
    internal static async Task RunControlAllocationCrashProbeAsync(string mode, string testRoot,
        string databasePath)
    {
        const string installationId = "installation-control-crash";
        const string profileId = "profile-control-crash";
        var pairId = "pair-control-crash-" + mode;
        var rootBindingId = "root-control-crash-" + mode;
        Directory.CreateDirectory(testRoot);
        var selectedPath = Path.Combine(testRoot, "selected-root");
        Directory.CreateDirectory(selectedPath);
        var authority = new SyncAuthorityDatabase(databasePath);
        await authority.InitializeAsync(installationId, profileId, "control-crash-probe", "{}");
        using var selected = PilotDirectoryLease.Open(selectedPath);
        var rootIdentity = JsonSerializer.Serialize(new
        {
            volumeSerial = selected.Identity.VolumeSerialNumber.ToString("x8"),
            fileId = selected.Identity.FileId.ToString("x16")
        });
        await authority.RegisterPairAsync(pairId, 1);
        await authority.RegisterRootBindingAsync(rootBindingId, pairId, "local", 1, rootIdentity, selected.Path);
        SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = boundary =>
        {
            if (mode == "authorized" && boundary == "allocation-authorized-before-filesystem-effect" ||
                mode == "attempt" && boundary == "attempt-retained-before-filesystem-effect")
                NativeTestFiles.TerminateCurrentProcess(207);
        };
        SyncControlNamespace.AllocationBoundaryForTest = boundary =>
        {
            if (mode == "unbound-directory" && boundary == "directory-created-before-authority-record" ||
                mode == "created" && boundary == "directory-authority-recorded-before-marker" ||
                mode == "marker" && boundary == "marker-written-before-binding-return")
                NativeTestFiles.TerminateCurrentProcess(207);
        };
        await new SyncAuthorityLocalControlNamespaceAllocator(authority).BindAsync(selected, installationId,
            pairId, rootBindingId, 1, profileId);
        throw new InvalidOperationException("The control allocation crash probe missed its requested boundary.");
    }

    private static async Task AssertVersion2MigrationAsync(string testRoot)
    {
        foreach (var boundary in new[]
                 {
                     "v2-backup-verified-before-migration",
                     "v3-schema-applied-before-version-commit",
                     "v3-migration-committed"
                 })
        {
            var databasePath = Path.Combine(testRoot, "migration-" + boundary + ".db");
            await using (var created = new FileStream(databasePath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 1, FileOptions.WriteThrough))
            {
                await created.FlushAsync();
                created.Flush(true);
            }
            await using (var connection = await OpenAsync(databasePath))
            await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, SyncAuthoritySchema.Sql, transaction);
                await ExecuteAsync(connection, SyncAuthoritySchemaSupplement.Sql, transaction);
                await ExecuteAsync(connection,
                    $"INSERT INTO schema_migration(schema_version,applied_utc,source_revision) VALUES({SyncAuthoritySchemaMigration3.PreviousVersion},'2026-09-04T00:00:00.0000000+00:00','v0.2.4');",
                    transaction);
                await ExecuteAsync(connection,
                    "INSERT INTO installation(installation_id,database_generation,created_utc) VALUES('installation-migration','generation-migration','2026-09-04T00:00:00.0000000+00:00');",
                    transaction);
                await ExecuteAsync(connection,
                    "INSERT INTO capability_profile(capability_profile_id,source_revision,profile_json,created_utc) VALUES('profile-v2','v0.2.4','{}','2026-09-04T00:00:00.0000000+00:00');",
                    transaction);
                await ExecuteAsync(connection,
                    "INSERT INTO sync_pair(pair_id,configuration_revision,state,created_utc,pair_json,selected) VALUES('pair-migration',1,'paused','2026-09-04T00:00:00.0000000+00:00',NULL,0);",
                    transaction);
                await ExecuteAsync(connection, $"PRAGMA user_version={SyncAuthoritySchemaMigration3.PreviousVersion};",
                    transaction);
                await transaction.CommitAsync();
            }

            var authority = new SyncAuthorityDatabase(databasePath);
            var interrupted = false;
            SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
            {
                if (!string.Equals(observed, boundary, StringComparison.Ordinal)) return;
                interrupted = true;
                throw new IOException("simulated migration interruption at " + boundary);
            };
            try
            {
                await authority.InitializeAsync("installation-migration", "profile-v3",
                    "full-client-migration", "{}");
            }
            catch (IOException) when (interrupted) { }
            finally { SyncAuthorityDatabase.MigrationBoundaryForTest = null; }
            Require(interrupted, "The requested v2 migration boundary did not execute: " + boundary);

            var versionAfterInterruption = await ScalarLongAsync(databasePath, "PRAGMA user_version;");
            if (boundary == "v3-migration-committed")
                Require(versionAfterInterruption == SyncAuthoritySchema.Version,
                    "A committed migration was not durable after its completion callback failed.");
            else
                Require(versionAfterInterruption == SyncAuthoritySchemaMigration3.PreviousVersion,
                    "An interrupted migration exposed a partially upgraded authority database.");
            Require(File.Exists(authority.Version2RollbackDatabasePath) &&
                    await ScalarLongAsync(authority.Version2RollbackDatabasePath, "PRAGMA user_version;") ==
                    SyncAuthoritySchemaMigration3.PreviousVersion,
                "The exact v2 rollback database was not durable before migration.");

            await authority.InitializeAsync("installation-migration", "profile-v3",
                "full-client-migration", "{}");
            Require(await ScalarLongAsync(databasePath, "PRAGMA user_version;") == SyncAuthoritySchema.Version &&
                    await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('upload_session_checkpoint','sync_path_index');") == 2 &&
                    await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM sync_pair WHERE pair_id='pair-migration' AND state='paused';") == 1 &&
                    string.Equals(await authority.LoadInstallationIdAsync(), "installation-migration",
                        StringComparison.Ordinal),
                "The v2→v3 migration did not preserve authority state and add only the new indexed capabilities.");
        }
    }

    private static async Task AssertExactVersion2CloneMigrationAsync(string sourceVersion2Database,
        string testRoot)
    {
        var baselinePath = Path.Combine(testRoot, "installed-v2-baseline.db");
        File.Copy(sourceVersion2Database, baselinePath, overwrite: false);
        Require(await ScalarLongAsync(baselinePath, "PRAGMA user_version;") ==
                SyncAuthoritySchemaMigration3.PreviousVersion &&
                string.Equals(await ScalarStringAsync(baselinePath, "PRAGMA quick_check;"), "ok",
                    StringComparison.Ordinal),
            "The supplied installed authority clone is not an exact healthy v0.2.4 version-2 database.");
        var installationId = await new SyncAuthorityDatabase(baselinePath).LoadInstallationIdAsync()
                             ?? throw new InvalidDataException(
                                 "The installed v0.2.4 authority clone has no installation identity.");
        var profile = await LoadExistingProfileAsync(baselinePath);
        var preserved = await CaptureTableFingerprintsAsync(baselinePath);

        foreach (var boundary in new[]
                 {
                     "v2-backup-verified-before-migration",
                     "v3-schema-applied-before-version-commit",
                     "v3-migration-committed"
                 })
        {
            var databasePath = Path.Combine(testRoot, "installed-" + boundary + ".db");
            File.Copy(sourceVersion2Database, databasePath, overwrite: false);
            var authority = new SyncAuthorityDatabase(databasePath);
            var interrupted = false;
            SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
            {
                if (!string.Equals(observed, boundary, StringComparison.Ordinal)) return;
                interrupted = true;
                throw new IOException("simulated exact-state migration interruption at " + boundary);
            };
            try
            {
                await authority.InitializeAsync(installationId, profile.Id, profile.Revision,
                    profile.Json);
            }
            catch (IOException) when (interrupted)
            {
            }
            finally
            {
                SyncAuthorityDatabase.MigrationBoundaryForTest = null;
            }
            Require(interrupted, "The exact-state migration boundary did not execute: " + boundary);

            var interruptedVersion = await ScalarLongAsync(databasePath, "PRAGMA user_version;");
            Require(interruptedVersion == (boundary == "v3-migration-committed"
                    ? SyncAuthoritySchema.Version
                    : SyncAuthoritySchemaMigration3.PreviousVersion),
                "An exact installed-state interruption exposed a partially committed schema.");
            await RequireFingerprintsAsync(databasePath, preserved,
                "An interrupted exact installed-state migration changed a retained v0.2.4 row.");
            Require(File.Exists(authority.Version2RollbackDatabasePath) &&
                    await ScalarLongAsync(authority.Version2RollbackDatabasePath, "PRAGMA user_version;") ==
                    SyncAuthoritySchemaMigration3.PreviousVersion,
                "The exact installed-state migration did not retain its version-2 rollback database.");
            await RequireFingerprintsAsync(authority.Version2RollbackDatabasePath, preserved,
                "The retained version-2 rollback database differs from the installed logical state.");

            await authority.InitializeAsync(installationId, profile.Id, profile.Revision, profile.Json);
            Require(await ScalarLongAsync(databasePath, "PRAGMA user_version;") ==
                    SyncAuthoritySchema.Version &&
                    await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM upload_session_checkpoint;") == 0 &&
                    await ScalarLongAsync(databasePath, "SELECT count(*) FROM sync_path_index;") == 0,
                "The exact installed-state migration did not converge to an empty additive v3 capability layer.");
            await RequireFingerprintsAsync(databasePath, preserved,
                "The converged exact installed-state migration changed a retained v0.2.4 row.");
        }
    }

    private static async Task<(string Id, string Revision, string Json)> LoadExistingProfileAsync(
        string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT capability_profile_id,source_revision,profile_json FROM capability_profile ORDER BY created_utc,capability_profile_id LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidDataException("The installed v0.2.4 authority has no capability profile.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<IReadOnlyDictionary<string, string>> CaptureTableFingerprintsAsync(
        string databasePath)
    {
        var tables = await QueryStringsAsync(databasePath,
            "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'schema_migration' ORDER BY name;");
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(databasePath);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var rowHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = reader.GetValue(index);
                    byte marker;
                    byte[] bytes;
                    switch (value)
                    {
                        case DBNull:
                            marker = (byte)'n';
                            bytes = [];
                            break;
                        case byte[] binary:
                            marker = (byte)'b';
                            bytes = binary;
                            break;
                        case long number:
                            marker = (byte)'i';
                            bytes = BitConverter.GetBytes(number);
                            break;
                        case double real:
                            marker = (byte)'r';
                            bytes = BitConverter.GetBytes(real);
                            break;
                        default:
                            marker = (byte)'s';
                            bytes = Encoding.UTF8.GetBytes(Convert.ToString(value,
                                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                            break;
                    }
                    rowHash.AppendData([marker]);
                    rowHash.AppendData(BitConverter.GetBytes(bytes.Length));
                    rowHash.AppendData(bytes);
                }
                rows.Add(Convert.ToHexString(rowHash.GetHashAndReset()).ToLowerInvariant());
            }
            rows.Sort(StringComparer.Ordinal);
            fingerprints[table] = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", rows)))).ToLowerInvariant();
        }
        return fingerprints;
    }

    private static async Task RequireFingerprintsAsync(string databasePath,
        IReadOnlyDictionary<string, string> expected, string message)
    {
        var actual = await CaptureTableFingerprintsAsync(databasePath);
        Require(expected.All(item => actual.TryGetValue(item.Key, out var value) &&
                                     string.Equals(value, item.Value, StringComparison.Ordinal)),
            message);
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task AssertInitializationAndStorageFailuresAsync(string testRoot)
    {
        var currentPath = Path.Combine(testRoot, "current-reopen.db");
        var current = new SyncAuthorityDatabase(currentPath);
        await current.InitializeAsync("installation-current", "profile-current", "fresh-current", "{}");
        await new SyncAuthorityDatabase(currentPath).InitializeAsync(
            "installation-current", "profile-current", "exact-current-reopen", "{}");
        Require(await ScalarLongAsync(currentPath, "PRAGMA user_version;") == SyncAuthoritySchema.Version &&
                string.Equals(await current.LoadInstallationIdAsync(), "installation-current", StringComparison.Ordinal),
            "An exact current-schema database did not reopen normally.");

        var olderPath = Path.Combine(testRoot, "older-schema.db");
        await CreateIncompatibleDatabaseAsync(olderPath, SyncAuthoritySchema.Version - 1);
        await AssertStartupRejectsWithoutWriteAsync(olderPath, "older");

        var newerPath = Path.Combine(testRoot, "newer-schema.db");
        await CreateIncompatibleDatabaseAsync(newerPath, SyncAuthoritySchema.Version + 1);
        await AssertStartupRejectsWithoutWriteAsync(newerPath, "newer");

        var unknownPath = Path.Combine(testRoot, "unknown-schema.db");
        await CreateIncompatibleDatabaseAsync(unknownPath, SyncAuthoritySchema.Version);
        await AssertStartupRejectsWithoutWriteAsync(unknownPath, "unknown");

        var corruptPath = Path.Combine(testRoot, "corrupt.db");
        await File.WriteAllBytesAsync(corruptPath, Enumerable.Range(0, 512).Select(index => (byte)(index % 251)).ToArray());
        await AssertStartupRejectsWithoutWriteAsync(corruptPath, "malformed");

        var fullPath = Path.Combine(testRoot, "full.db");
        var full = new SyncAuthorityDatabase(fullPath);
        await full.InitializeAsync("installation-full", "profile-full", "full", "{}");
        await full.RegisterObjectAsync("object-full", "file");
        await ExecuteAsync(fullPath, "PRAGMA wal_checkpoint(TRUNCATE);");
        var oversizedIdentity = "{\"payload\":\"" + new string('x', 1_000_000) + "\"}";
        await AssertFullDatabaseRejectsWriteAsync(fullPath, oversizedIdentity);

        var readOnlyPath = Path.Combine(testRoot, "read-only.db");
        var readOnly = new SyncAuthorityDatabase(readOnlyPath);
        await readOnly.InitializeAsync("installation-read-only", "profile-read-only", "read-only", "{}");
        await ExecuteAsync(readOnlyPath, "PRAGMA wal_checkpoint(TRUNCATE);");
        var originalAttributes = File.GetAttributes(readOnlyPath);
        File.SetAttributes(readOnlyPath, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            await ExpectRejectAsync(() => readOnly.RegisterObjectAsync("read-only-write", "file"));
        }
        finally
        {
            File.SetAttributes(readOnlyPath, originalAttributes);
        }
    }

    private static async Task CreateIncompatibleDatabaseAsync(string databasePath, int schemaVersion)
    {
        await File.WriteAllBytesAsync(databasePath, []);
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE incompatible_state(id INTEGER PRIMARY KEY, payload TEXT NOT NULL) STRICT;
            INSERT INTO incompatible_state(payload) VALUES('retained');
            PRAGMA user_version={schemaVersion};
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertStartupRejectsWithoutWriteAsync(string databasePath, string fixture)
    {
        var before = AuthorityFileSetSnapshot(databasePath);
        await ExpectExceptionAsync<InvalidDataException>(() => new SyncAuthorityDatabase(databasePath).InitializeAsync(
            "installation-" + fixture, "profile-" + fixture, "rejected-" + fixture, "{}"));
        var after = AuthorityFileSetSnapshot(databasePath);
        Require(before.SequenceEqual(after, StringComparer.Ordinal),
            $"The {fixture} authority database or a SQLite sidecar changed during read-only startup rejection.");
    }

    private static IReadOnlyList<string> AuthorityFileSetSnapshot(string databasePath)
    {
        return new[] { databasePath, databasePath + "-wal", databasePath + "-shm", databasePath + "-journal" }
            .Where(File.Exists)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return Path.GetFileName(path) + ":" + bytes.Length + ":" +
                       Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task AssertPairDefinitionAuthorityAsync(SyncAuthorityDatabase authority, string testRoot)
    {
        // Strict value comparison for the pair round-trip. Record equality compares the list members of
        // SyncPair by reference, and normalization always builds new list instances, so the previous
        // equality operator could not distinguish "same definition" from "different list instance".
        static bool SequenceValueEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
            ReferenceEquals(left, right) ||
            (left is not null && right is not null && left.SequenceEqual(right));

        static bool PairDefinitionsEqualByValue(SyncPair left, SyncPair right) =>
            left.Id == right.Id && left.Name == right.Name && left.LocalRoot == right.LocalRoot &&
            left.LocalVolumeSerialNumber == right.LocalVolumeSerialNumber &&
            left.LocalRootFileId == right.LocalRootFileId && left.DropboxRoot == right.DropboxRoot &&
            left.DropboxFolderId == right.DropboxFolderId && left.Enabled == right.Enabled &&
            left.Priority == right.Priority && left.PropagateDeletions == right.PropagateDeletions &&
            left.PolicyRevision == right.PolicyRevision &&
            Equals(left.ControlNamespace, right.ControlNamespace) &&
            Equals(left.RecoveryRoot, right.RecoveryRoot) &&
            SequenceValueEqual(left.SelectionRules, right.SelectionRules) &&
            left.SelectionDefaultIncluded == right.SelectionDefaultIncluded &&
            left.ConnectionId == right.ConnectionId && left.Provider == right.Provider &&
            left.InitialReviewApproved == right.InitialReviewApproved &&
            left.FilesOnDemandEnabled == right.FilesOnDemandEnabled &&
            left.RecoveryRetentionDays == right.RecoveryRetentionDays &&
            left.Direction == right.Direction &&
            SequenceValueEqual(left.DirectoryStorageRules, right.DirectoryStorageRules) &&
            SequenceValueEqual(left.DirectoryStorageApplyPaths, right.DirectoryStorageApplyPaths) &&
            left.DefaultStorage == right.DefaultStorage &&
            SequenceValueEqual(left.IgnoreRules, right.IgnoreRules) &&
            SequenceValueEqual(left.FileStorageRules, right.FileStorageRules);

        static string DescribePairDifferences(SyncPair left, SyncPair right)
        {
            var notes = new List<string>
            {
                "SelectionRules ref=" + ReferenceEquals(left.SelectionRules, right.SelectionRules) +
                " seq=" + SequenceValueEqual(left.SelectionRules, right.SelectionRules),
                "IgnoreRules ref=" + ReferenceEquals(left.IgnoreRules, right.IgnoreRules) +
                " seq=" + SequenceValueEqual(left.IgnoreRules, right.IgnoreRules),
                "DirectoryStorageRules ref=" +
                ReferenceEquals(left.DirectoryStorageRules, right.DirectoryStorageRules) +
                " seq=" + SequenceValueEqual(left.DirectoryStorageRules, right.DirectoryStorageRules),
                "DirectoryStorageApplyPaths ref=" +
                ReferenceEquals(left.DirectoryStorageApplyPaths, right.DirectoryStorageApplyPaths) +
                " seq=" + SequenceValueEqual(left.DirectoryStorageApplyPaths, right.DirectoryStorageApplyPaths),
                "FileStorageRules ref=" + ReferenceEquals(left.FileStorageRules, right.FileStorageRules) +
                " seq=" + SequenceValueEqual(left.FileStorageRules, right.FileStorageRules)
            };
            if (left.Id != right.Id) notes.Add("Id " + left.Id + " != " + right.Id);
            if (left.Name != right.Name) notes.Add("Name " + left.Name + " != " + right.Name);
            if (left.Provider != right.Provider) notes.Add("Provider " + left.Provider + " != " + right.Provider);
            if (left.ConnectionId != right.ConnectionId)
                notes.Add("ConnectionId " + left.ConnectionId + " != " + right.ConnectionId);
            if (left.PolicyRevision != right.PolicyRevision)
                notes.Add("PolicyRevision " + left.PolicyRevision + " != " + right.PolicyRevision);
            if (left.DropboxFolderId != right.DropboxFolderId)
                notes.Add("DropboxFolderId " + left.DropboxFolderId + " != " + right.DropboxFolderId);
            if (left.DropboxRoot != right.DropboxRoot)
                notes.Add("DropboxRoot " + left.DropboxRoot + " != " + right.DropboxRoot);
            if (left.LocalRoot != right.LocalRoot)
                notes.Add("LocalRoot " + left.LocalRoot + " != " + right.LocalRoot);
            if (!Equals(left.ControlNamespace, right.ControlNamespace))
                notes.Add("ControlNamespace differs");
            if (!Equals(left.RecoveryRoot, right.RecoveryRoot)) notes.Add("RecoveryRoot differs");
            return string.Join(" ; ", notes);
        }

        var localRoot = Path.Combine(testRoot, "pair-definition-root");
        Directory.CreateDirectory(localRoot);
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Pair Definition", "id:pair-definition"), "SQLite pair");
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        var retained = await authority.RetainPairDefinitionAsync(pair, select: true);
        var normalizedPair = SyncPairStore.NormalizePair(pair);
        Require(retained.Pairs.Count == 1 && retained.SelectedPairId == pair.Id,
            "The SQLite pair definition did not round-trip as selected authority.");
        var retainedPair = retained.Pairs[0];
        if (!PairDefinitionsEqualByValue(retainedPair, normalizedPair))
            Console.WriteLine("DIAG pair-difference :: " + DescribePairDifferences(retainedPair, normalizedPair));
        Require(PairDefinitionsEqualByValue(retainedPair, normalizedPair),
            "The SQLite pair definition did not round-trip as selected authority.");
        Require(!PairDefinitionsEqualByValue(normalizedPair, normalizedPair with { Name = "SQLite pair changed" }),
            "The pair value comparison must detect a changed scalar member.");
        Require(!PairDefinitionsEqualByValue(normalizedPair, normalizedPair with
            { SelectionRules = [new SyncSelectionRule("changed-folder", true)] }),
            "The pair value comparison must detect a changed list element.");
        Require(!PairDefinitionsEqualByValue(normalizedPair, normalizedPair with
            { DirectoryStorageRules = [new SyncDirectoryStorageRule("changed-folder", SyncDirectoryStorage.CloudOnly)] }),
            "The pair value comparison must detect a changed list element in a second list member.");
        var renamed = pair with { Name = "SQLite pair renamed" };
        retained = await authority.RetainPairDefinitionAsync(renamed, select: true);
        Require(retained.Pairs.Single().Name == renamed.Name &&
                await authority.IsPairDefinitionRetainedAsync(pair.Id),
            "A non-effectful SQLite pair presentation update was not retained.");
        await authority.RecordAcceptedLegacyPairImportAsync(pair, renamed);
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM migration_import WHERE source_kind='legacy_0_1_1_pairs' AND status='accepted';") == 1,
            "The accepted legacy-pair migration receipt was not retained in SQLite.");
    }

    private static async Task AssertCommandRequestLifecycleAsync(SyncAuthorityDatabase authority)
    {
        await authority.CreateCommandRequestAsync("command-complete", "run_all", null, null, null,
            "{\"trigger\":\"preflight\"}");
        await ExpectRejectAsync(() => authority.TransitionCommandRequestAsync("command-complete", "completed",
            "{}"));
        await authority.TransitionCommandRequestAsync("command-complete", "accepted");
        await authority.TransitionCommandRequestAsync("command-complete", "executing");
        await authority.TransitionCommandRequestAsync("command-complete", "completed", "{\"pairCount\":0}");
        Require(string.Equals(await ScalarStringAsync(authority.DatabasePath,
                "SELECT status FROM command_request WHERE command_request_id='command-complete';"), "completed",
                StringComparison.Ordinal),
            "A durable command request did not enforce and retain its terminal lifecycle.");

        await authority.CreateCommandRequestAsync("command-interrupted", "reconcile", null, null, null, "{}");
        await authority.TransitionCommandRequestAsync("command-interrupted", "accepted");
        await authority.TransitionCommandRequestAsync("command-interrupted", "executing");
        await authority.RecoverInterruptedCommandRequestsAsync();
        Require(string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    "SELECT status FROM command_request WHERE command_request_id='command-interrupted';"), "blocked",
                    StringComparison.Ordinal) &&
                (await ScalarStringAsync(authority.DatabasePath,
                    "SELECT result_json FROM command_request WHERE command_request_id='command-interrupted';"))!
                .Contains("process_interrupted", StringComparison.Ordinal),
            "Startup command recovery did not retain interrupted execution as blocked truth.");
    }

    private static async Task AssertWalCrashRecoveryAsync(string testRoot)
    {
        var databasePath = Path.Combine(testRoot, "wal-crash.db");
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--cloud-pro-wal-crash-probe");
        start.ArgumentList.Add(databasePath);
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("The WAL crash probe did not start.");
        await process.WaitForExitAsync();
        Require(process.ExitCode == 203, "The WAL crash probe did not terminate at its committed boundary.");

        var recovered = new SyncAuthorityDatabase(databasePath);
        Require(string.Equals(await recovered.QuickCheckAsync(), "ok", StringComparison.Ordinal),
            "SQLite did not recover a committed WAL after abrupt process termination.");
        Require(await ScalarLongAsync(databasePath,
                    "SELECT count(*) FROM logical_object WHERE object_id='wal-survivor';") == 1,
            "The committed WAL row was not recovered after abrupt process termination.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertFreshCreateCrashBoundariesAsync(string testRoot)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        foreach (var mode in new[] { "parent", "created", "schema", "committed" })
        {
            var databasePath = Path.Combine(testRoot, "fresh-create-crash-" + mode + ".db");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
                    StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--authority-fresh-create-crash-probe");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("The fresh-create crash probe did not start.");
            await process.WaitForExitAsync();
            Require(process.ExitCode == 211,
                $"The {mode} fresh-create crash probe did not terminate at its requested boundary.");

            if (mode == "parent")
            {
                Require(!File.Exists(databasePath),
                    "The parent-ready crash boundary unexpectedly created the authority database.");
                await new SyncAuthorityDatabase(databasePath).InitializeAsync(
                    "installation-fresh-crash", "profile-fresh-crash", "fresh-create-retry", "{}");
                continue;
            }

            if (mode == "committed")
            {
                await new SyncAuthorityDatabase(databasePath).InitializeAsync(
                    "installation-fresh-crash", "profile-fresh-crash", "fresh-create-reopen", "{}");
                Require(await ScalarLongAsync(databasePath, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
                    "A committed fresh schema did not reopen after process termination.");
                continue;
            }

            Require(File.Exists(databasePath),
                $"The {mode} crash boundary did not retain its incomplete fresh-create evidence.");
            await AssertStartupRejectsWithoutWriteAsync(databasePath, "fresh-crash-" + mode);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertControlAllocationCrashRecoveryAsync(string testRoot)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        foreach (var mode in new[] { "authorized", "attempt", "created", "marker", "unbound-directory" })
        {
            var modeRoot = Path.Combine(testRoot, "control-crash-" + mode);
            var databasePath = Path.Combine(modeRoot, "authority.db");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
                    StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--control-allocation-crash-probe");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(modeRoot);
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("The control allocation crash probe did not start.");
            await process.WaitForExitAsync();
            Require(process.ExitCode == 207,
                $"The {mode} control allocation crash probe did not terminate at its requested boundary.");

            await ExecuteAsync(databasePath,
                "UPDATE lease SET expires_utc='2000-01-01T00:00:00.0000000Z' WHERE status='active';");
            var pairId = "pair-control-crash-" + mode;
            var rootBindingId = "root-control-crash-" + mode;
            var selectedPath = Path.Combine(modeRoot, "selected-root");
            using var selected = PilotDirectoryLease.Open(selectedPath);
            var authority = new SyncAuthorityDatabase(databasePath);
            var allocator = new SyncAuthorityLocalControlNamespaceAllocator(authority);
            var controlPath = SyncControlNamespace.LocalRootFor(selected.Path, pairId);
            if (mode == "unbound-directory")
            {
                await ExpectRejectAsync(() => allocator.BindAsync(selected, "installation-control-crash", pairId,
                    rootBindingId, 1, "profile-control-crash"));
                Require(Directory.Exists(controlPath) &&
                        !File.Exists(Path.Combine(controlPath, SyncControlNamespace.MarkerName)),
                    "A crash-created directory without retained identity was adopted or removed.");
                Require(await ScalarLongAsync(databasePath,
                            "SELECT count(*) FROM operation WHERE kind='allocate_control' AND status='blocked';") == 1,
                    "An unbound crash-created directory did not become durable blocked authority.");
                continue;
            }

            var binding = await allocator.BindAsync(selected, "installation-control-crash", pairId,
                rootBindingId, 1, "profile-control-crash");
            Require(Directory.Exists(binding.RootPath) &&
                    File.Exists(Path.Combine(binding.RootPath, SyncControlNamespace.MarkerName)),
                $"The {mode} crash boundary did not recover the exact control directory and owner marker.");
            Require(await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM operation WHERE kind='allocate_control' AND status='completed';") == 1 &&
                    await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM operation_attempt WHERE status='verified_success';") == 1,
                $"The {mode} crash boundary did not converge to one completed allocation operation.");
        }
    }

    private static async Task AssertFullDatabaseRejectsWriteAsync(string databasePath, string oversizedIdentity)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var count = connection.CreateCommand();
        count.CommandText = "PRAGMA page_count;";
        var pageCount = Convert.ToInt64(await count.ExecuteScalarAsync());
        await using var limit = connection.CreateCommand();
        limit.CommandText = $"PRAGMA max_page_count={pageCount};";
        var appliedLimit = Convert.ToInt64(await limit.ExecuteScalarAsync());
        Require(appliedLimit == pageCount, "The SQLite full-database fixture did not apply its page ceiling.");

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc)
            VALUES('version-full','object-full','local',$identity,$hash,1000000,'2026-09-02T00:00:00.0000000Z');
            """;
        insert.Parameters.AddWithValue("$identity", oversizedIdentity);
        insert.Parameters.AddWithValue("$hash", HashA);
        try
        {
            await insert.ExecuteNonQueryAsync();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 13)
        {
            return;
        }
        throw new InvalidOperationException("The bounded SQLite database did not surface SQLITE_FULL.");
    }

    private static async Task AssertRuntimeAsync(SyncAuthorityDatabase authority, string databasePath)
    {
        var version = Version.Parse(await authority.GetSqliteVersionAsync());
        Require(version >= new Version(3, 51, 3) || version == new Version(3, 50, 7) || version == new Version(3, 44, 6),
            "The packaged SQLite version is outside the accepted WAL-reset fixes.");
        Require(string.Equals(await ScalarStringAsync(databasePath, "PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase),
            "The authority database is not in WAL mode.");
        Require(await ScalarLongAsync(databasePath, "PRAGMA synchronous;") == 2,
            "The authority database is not using FULL synchronous durability.");
        Require(await ScalarLongAsync(databasePath, "PRAGMA foreign_keys;") == 1,
            "Foreign-key enforcement is not active on the authority connection.");
        Require(await ScalarLongAsync(databasePath, "PRAGMA trusted_schema;") == 0,
            "SQLite trusted_schema was not disabled.");
        await AuthorityWalChecks.AssertProductionSettingsAsync(databasePath);
    }

    private static async Task AssertSchemaInventoryAsync(string databasePath)
    {
        var expectedTables = new[]
        {
            "schema_migration", "authority_guard", "installation", "process_instance", "provider_account",
            "recovery_root_binding",
            "capability_profile", "volume", "sync_pair", "pair_config_revision", "root_binding", "logical_object",
            "object_version", "membership", "scan_generation", "observation", "synchronization_point",
            "monitor_cursor", "monitor_event", "dirty_item", "reconciliation_decision", "conflict",
            "conflict_version", "operation_group", "operation_transition", "operation_step_transition",
            "attempt_transition", "allocation_transition", "operation", "operation_member", "operation_dependency",
            "operation_step", "resource_allocation", "namespace_allocation", "control_namespace",
            "pair_control_namespace", "upload_session_checkpoint", "lease_fence", "lease", "operation_attempt", "effect_receipt",
            "recovery_artifact", "tombstone", "conflict_resolution", "command_request", "scheduler_setting",
            "projection_watermark", "history_event", "migration_import"
        };
        var tables = await QueryStringsAsync(databasePath,
            "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name;");
        var missingTables = expectedTables.Except(tables, StringComparer.Ordinal).ToArray();
        Require(missingTables.Length == 0,
            "The authority schema is missing reviewed table families: " + string.Join(",", missingTables));

        var expectedTriggers = new[]
        {
            "operation_transition_guard", "attempt_transition_guard", "allocation_transition_guard",
            "operation_member_insert_only_while_planned", "operation_member_update_only_while_planned",
            "operation_member_delete_only_while_planned", "operation_dependency_insert_only_while_planned",
            "operation_dependency_update_only_while_planned", "operation_dependency_delete_only_while_planned",
            "attempt_requires_current_fence", "attempt_update_requires_current_fence", "receipt_requires_current_fence",
            "effect_transition_requires_current_fence", "allocation_transition_requires_current_fence",
            "no_new_attempt_while_unreconciled", "operation_step_transition_guard",
            "operation_step_transition_no_insert", "operation_step_transition_no_update",
            "operation_step_transition_no_delete",
            "operation_complete_requires_terminal_steps", "no_prune_unresolved_recovery",
            "namespace_allocation_transition_guard",
            "namespace_allocation_transition_requires_current_fence",
            "upload_session_checkpoint_allocation_guard",
            "upload_session_checkpoint_identity_immutable",
            "upload_session_checkpoint_requires_current_fence_insert",
            "upload_session_checkpoint_requires_current_fence_update"
        };
        var triggers = await QueryStringsAsync(databasePath,
            "SELECT name FROM sqlite_schema WHERE type='trigger' ORDER BY name;");
        var missingTriggers = expectedTriggers.Except(triggers, StringComparer.Ordinal).ToArray();
        Require(missingTriggers.Length == 0,
            "The authority schema is missing fail-closed triggers: " + string.Join(",", missingTriggers));
    }

    private static async Task<SyncAuthorityCommandAdmission> SeedIdentityGraphAsync(SyncAuthorityDatabase authority)
    {
        await authority.RegisterProviderAccountAsync("provider-account-a", "installation-preflight",
            "dropbox-account-a", "root-namespace-a", "home-namespace-a", "Test Dropbox", 1);
        await authority.RegisterVolumeAsync("volume-a", "installation-preflight", "A1B2C3D4", "NTFS",
            @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", true, true, true);
        await ExpectRejectAsync(() => authority.RegisterProviderAccountAsync("provider-account-a",
            "installation-preflight", "different-account", "root-namespace-a", "home-namespace-a",
            "Changed identity", 2));
        await ExpectRejectAsync(() => authority.RegisterVolumeAsync("volume-a", "installation-preflight",
            "FFFFFFFF", "NTFS", null, true, true, true));
        await ExpectRejectAsync(() => authority.RegisterVolumeAsync("volume-fat", "installation-preflight",
            "11111111", "exFAT", null, true, false, false));
        await authority.RegisterPairAsync("pair-a", 1);
        await ExpectRejectAsync(() => authority.RegisterPairAsync("pair-a", 1,
            new SyncAuthorityPairConfiguration("recoverable", 300, 2, "manual_preserve_both", "content_only",
                RecursiveMode: false, AutomaticPruning: false, RecoveryRetentionDays: 30)));
        Require(string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    "SELECT deletion_policy FROM pair_config_revision WHERE pair_id='pair-a' AND revision=1;"),
                "disabled", StringComparison.Ordinal),
            "A same-revision pair policy drift replaced the immutable retained configuration.");
        await authority.RegisterLocalRootBindingAsync("root-local", "pair-a", 1, "installation-preflight",
            "volume-a", "A1B2C3D4", "0000000000001234", "X:\\Sync");
        await ExpectRejectAsync(() => authority.RegisterLocalRootBindingAsync("root-local-wrong", "pair-a", 1,
            "installation-preflight", "volume-a", "FFFFFFFF", "0000000000005678", "X:\\Wrong"));
        await authority.RegisterDropboxRootBindingAsync("root-dropbox", "pair-a", 1,
            "installation-preflight", "provider-account-a", "root-namespace-a", "folder-a", "/Projects/A");
        await ExpectRejectAsync(() => authority.RegisterDropboxRootBindingAsync("root-dropbox-wrong", "pair-a", 1,
            "installation-preflight", "provider-account-a", "different-root-namespace", "folder-wrong",
            "/Projects/Wrong"));
        await authority.RegisterDropboxRecoveryRootAsync("recovery-dropbox", "installation-preflight",
            "provider-account-a", SyncDropboxArtifactPaths.RecoveryContainerPath, "recovery-folder", "root-namespace-a", 1);
        await authority.RegisterPairAsync("pair-b", 1);
        await ExpectRejectAsync(() => authority.RegisterLocalRootBindingAsync("root-local-alias", "pair-b", 1,
            "installation-preflight", "volume-a", "A1B2C3D4", "0000000000001234", "Y:\\Alias"));
        await ExpectRejectAsync(() => authority.RegisterDropboxRootBindingAsync("root-dropbox-alias", "pair-b", 1,
            "installation-preflight", "provider-account-a", "root-namespace-a", "folder-a",
            "/Projects/Alias"));
        await ExpectRejectAsync(() => authority.RegisterRootBindingAsync("root-local-overlap", "pair-b", "local", 1,
            "{\"volume\":\"test\"}", "X:\\Sync\\Nested"));
        await ExpectRejectAsync(() => authority.RegisterRootBindingAsync("root-dropbox-overlap", "pair-b", "dropbox", 1,
            "{\"folderId\":\"folder-b\"}", "/Projects/A/Nested"));
        await ExpectRejectAsync(() => authority.RegisterRootBindingAsync("root-recovery-overlap", "pair-b", "dropbox", 1,
            "{\"folderId\":\"folder-c\"}", SyncDropboxArtifactPaths.RecoveryContainerPath + "/Nested"));
        await authority.RegisterDropboxRootBindingAsync("root-dropbox", "pair-a", 1,
            "installation-preflight", "provider-account-a", "root-namespace-a", "folder-a",
            "/Projects/Renamed A");
        await ExpectRejectAsync(() => authority.RegisterDropboxRootBindingAsync("root-dropbox", "pair-a", 1,
            "installation-preflight", "provider-account-a", "root-namespace-a", "substituted-folder",
            "/Projects/Renamed A"));
        await authority.RegisterDropboxRecoveryRootAsync("recovery-dropbox", "installation-preflight",
            "provider-account-a", "/Recovery Renamed", "recovery-folder", "root-namespace-a", 1);
        Require(string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    "SELECT display_path FROM root_binding WHERE root_binding_id='root-dropbox';"),
                "/Projects/Renamed A", StringComparison.Ordinal) &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    "SELECT display_path FROM recovery_root_binding WHERE recovery_root_binding_id='recovery-dropbox';"),
                    "/Recovery Renamed", StringComparison.Ordinal),
            "Stable Dropbox identities did not accept their verified display-path revisions.");
        await authority.RegisterObjectAsync("object-a", "file");
        await authority.RegisterMembershipAsync("membership-a", "object-a", "root-local", "file.txt", 1);
        return await AdmitFixturePairsAsync(authority, "pair-a", "pair-b");
    }

    private static async Task AssertNamespaceAllocationAuthorityAsync(SyncAuthorityDatabase authority, SyncAuthorityCommandAdmission admission)
    {
        const string operationId = "operation-namespace-allocation";
        const string allocationId = "namespace-allocation-local-a";
        const string controlNamespaceId = "control-namespace-local-a";
        await authority.CreateOperationAsync(Plan(operationId), admission: admission);
        var lease = await authority.AcquireOperationLeaseAsync(operationId, "worker-namespace-allocation",
            TimeSpan.FromMinutes(5));
        var allocation = new SyncAuthorityNamespaceAllocation(allocationId, operationId, "local",
            "volume-parent-a", ".syncshark-control-authorized", HashA);
        await authority.AuthorizeNamespaceAllocationAsync(allocation, lease);
        var retained = await authority.LoadNamespaceAllocationAsync(allocationId);
        Require(retained.Status == SyncAuthorityAllocationStatus.Authorized && retained.BoundIdentity is null,
            "The namespace allocation was not durably authorized before object creation.");

        await ExpectRejectAsync(() => authority.TransitionNamespaceAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Verified, "{\"fileId\":\"early\"}"));
        await authority.TransitionNamespaceAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Creating);

        var restarted = new SyncAuthorityDatabase(authority.DatabasePath);
        retained = await restarted.LoadNamespaceAllocationAsync(allocationId);
        Require(retained.Status == SyncAuthorityAllocationStatus.Creating,
            "A process restart did not retain the in-progress namespace allocation authority.");
        await restarted.TransitionNamespaceAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Created);
        const string boundIdentity = "{\"volumeSerial\":\"a1b2c3d4\",\"fileId\":\"0000000000000042\"}";
        await restarted.TransitionNamespaceAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Bound, boundIdentity);
        await restarted.TransitionNamespaceAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Verified);
        await restarted.RegisterControlNamespaceAsync(new SyncAuthorityControlNamespace(controlNamespaceId,
            allocationId, "local", boundIdentity, "{\"fileId\":\"0000000000000043\"}", HashB,
            "active"));
        await restarted.BindPairControlNamespaceAsync("pair-a", "local", controlNamespaceId, 1);
        await ExpectRejectAsync(() => restarted.BindPairControlNamespaceAsync("pair-a", "local",
            controlNamespaceId, 2));
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM pair_control_namespace WHERE pair_id='pair-a' AND side='local' AND status='active';") == 1,
            "More than one active local control namespace was retained for the pair.");
        await restarted.ReleaseLeaseAsync(lease);
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertAuthorizedLocalControlAllocatorAsync(SyncAuthorityDatabase authority,
        string testRoot)
    {
        const string pairId = "pair-control-integration";
        const string rootBindingId = "root-control-integration";
        var parentPath = Path.Combine(testRoot, "control-allocation-parent");
        var selectedPath = Path.Combine(parentPath, "selected-root");
        Directory.CreateDirectory(selectedPath);
        using var selected = PilotDirectoryLease.Open(selectedPath);
        var rootIdentity = JsonSerializer.Serialize(new
        {
            volumeSerial = selected.Identity.VolumeSerialNumber.ToString("x8"),
            fileId = selected.Identity.FileId.ToString("x16")
        });
        await authority.RegisterPairAsync(pairId, 1);
        await authority.RegisterRootBindingAsync(rootBindingId, pairId, "local", 1, rootIdentity, selected.Path);
        var controlPath = SyncControlNamespace.LocalRootFor(selected.Path, pairId);
        var observedBoundaries = new List<string>();
        SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = boundary =>
        {
            observedBoundaries.Add(boundary);
            if (boundary == "allocation-authorized-before-filesystem-effect")
            {
                Require(!Directory.Exists(controlPath),
                    "The local control directory existed before SQLite allocation authorization committed.");
                Require(string.Equals(ScalarStringAsync(authority.DatabasePath,
                            $"SELECT status FROM namespace_allocation WHERE deterministic_name='.syncshark-control-{pairId}';")
                        .GetAwaiter().GetResult(), "authorized", StringComparison.Ordinal),
                    "The local control allocation authorization was not queryable before the filesystem effect.");
            }
            else if (boundary == "attempt-retained-before-filesystem-effect")
            {
                Require(!Directory.Exists(controlPath),
                    "The local control directory existed before its immutable dispatched attempt.");
                Require(ScalarLongAsync(authority.DatabasePath,
                            $"SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id='{pairId}' AND a.status='request_sent';")
                        .GetAwaiter().GetResult() == 1,
                    "The local control effect had no durable dispatched attempt boundary.");
            }
        };
        SyncControlNamespace.AllocationBoundaryForTest = boundary =>
        {
            observedBoundaries.Add(boundary);
            if (boundary == "directory-created-before-authority-record")
                Require(string.Equals(ScalarStringAsync(authority.DatabasePath,
                            $"SELECT status FROM namespace_allocation WHERE deterministic_name='.syncshark-control-{pairId}';")
                        .GetAwaiter().GetResult(), "creating", StringComparison.Ordinal),
                    "The control directory creation boundary did not retain the creating state.");
            else if (boundary == "directory-authority-recorded-before-marker")
            {
                Require(string.Equals(ScalarStringAsync(authority.DatabasePath,
                            $"SELECT status FROM namespace_allocation WHERE deterministic_name='.syncshark-control-{pairId}';")
                        .GetAwaiter().GetResult(), "created", StringComparison.Ordinal),
                    "The created control directory identity was not durable before marker creation.");
                Require(!File.Exists(Path.Combine(controlPath, SyncControlNamespace.MarkerName)),
                    "The owner marker existed before the created directory identity was retained.");
            }
        };
        SyncLocalControlNamespaceBinding binding;
        try
        {
            var allocator = new SyncAuthorityLocalControlNamespaceAllocator(authority);
            binding = await allocator.BindAsync(selected, "installation-preflight", pairId, rootBindingId, 1,
                "profile-cloud-pro");
            var repeated = await allocator.BindAsync(selected, "installation-preflight", pairId, rootBindingId, 1,
                "profile-cloud-pro");
            Require(binding == repeated, "A completed control allocation did not read back idempotently.");
        }
        finally
        {
            SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = null;
            SyncControlNamespace.AllocationBoundaryForTest = null;
        }
        Require(observedBoundaries.SequenceEqual(new[]
            {
                "allocation-authorized-before-filesystem-effect",
                "attempt-retained-before-filesystem-effect",
                "directory-created-before-authority-record",
                "directory-identity-retained-before-owner-marker",
                "directory-authority-recorded-before-marker",
                "marker-written-before-binding-return"
            }), "The local control allocation boundaries were not observed in the required authority order: " +
                string.Join(",", observedBoundaries));
        Require(Directory.Exists(binding.RootPath) &&
                File.Exists(Path.Combine(binding.RootPath, SyncControlNamespace.MarkerName)),
            "The verified local control directory or owner marker is absent.");
        Require(await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM operation WHERE pair_id='{pairId}' AND kind='allocate_control' AND status='completed';") == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM namespace_allocation WHERE deterministic_name='.syncshark-control-{pairId}' AND status='verified';") == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM pair_control_namespace WHERE pair_id='{pairId}' AND side='local' AND status='active';") == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id='{pairId}' AND a.status='verified_success';") == 1,
            "The local control allocation did not close through verified SQLite authority.");

        const string collisionPairId = "pair-control-collision";
        const string collisionRootId = "root-control-collision";
        var collisionSelectedPath = Path.Combine(parentPath, "collision-selected-root");
        Directory.CreateDirectory(collisionSelectedPath);
        using var collisionSelected = PilotDirectoryLease.Open(collisionSelectedPath);
        var collisionRootIdentity = JsonSerializer.Serialize(new
        {
            volumeSerial = collisionSelected.Identity.VolumeSerialNumber.ToString("x8"),
            fileId = collisionSelected.Identity.FileId.ToString("x16")
        });
        await authority.RegisterPairAsync(collisionPairId, 1);
        await authority.RegisterRootBindingAsync(collisionRootId, collisionPairId, "local", 1,
            collisionRootIdentity, collisionSelected.Path);
        var collisionPath = SyncControlNamespace.LocalRootFor(collisionSelected.Path, collisionPairId);
        Directory.CreateDirectory(collisionPath);
        await ExpectRejectAsync(() => new SyncAuthorityLocalControlNamespaceAllocator(authority).BindAsync(
            collisionSelected, "installation-preflight", collisionPairId, collisionRootId, 1,
            "profile-cloud-pro"));
        Require(Directory.Exists(collisionPath) &&
                !File.Exists(Path.Combine(collisionPath, SyncControlNamespace.MarkerName)),
            "The allocator adopted or removed an unowned pre-existing directory.");
        Require(await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM operation WHERE pair_id='{collisionPairId}' AND kind='allocate_control' AND status='blocked';") == 1,
            "The unowned control-path collision did not become durable blocked authority.");
    }

    private static SyncAuthorityOperationPlan Plan(
        string operationId,
        IReadOnlyList<SyncAuthorityDependency>? dependencies = null,
        IReadOnlyList<SyncAuthorityAllocation>? allocations = null,
        string? expectedPath = null) =>
        new(
            operationId,
            "idempotency-" + operationId,
            SyncAuthorityOperationKind.Reconcile,
            "pair-a",
            "object-a",
            1,
            "profile-cloud-pro",
            "{}",
            100,
            new[]
            {
                new SyncAuthorityMember("member-" + operationId, "pair-a", "object-a", "affected", "root-local",
                    "membership-a", null, 1, expectedPath ?? operationId + ".txt", "{}")
            },
            dependencies ?? Array.Empty<SyncAuthorityDependency>(),
            allocations ?? Array.Empty<SyncAuthorityAllocation>());

    private static async Task AssertRecoveryProjectionPagingAsync(SyncAuthorityDatabase authority)
    {
        await authority.RegisterRecoveryArtifactAsync("recovery-page-a", "operation-prerequisite-a",
            "object-a", null, "local", "{\"kind\":\"file\",\"path\":\"archive-a.bin\"}", HashA, 11);
        await authority.RegisterRecoveryArtifactAsync("recovery-page-b", "operation-main",
            "object-a", null, "dropbox", "{\"kind\":\"file\",\"path\":\"archive-b.bin\"}", HashB, 22);
        var first = await authority.LoadRecoveryArtifactsPageAsync("pair-a", 1);
        var second = await authority.LoadRecoveryArtifactsPageAsync("pair-a", 1, 1);
        Require(first.Count == 1 && second.Count == 1 &&
                !string.Equals(first[0].RecoveryArtifactId, second[0].RecoveryArtifactId,
                    StringComparison.Ordinal),
            "The recovery projection did not page retained artifacts without duplication.");
        var searched = await authority.LoadRecoveryArtifactsPageAsync("pair-a", 10, search: "archive-b.bin");
        Require(searched.Count == 1 && searched[0].RecoveryArtifactId == "recovery-page-b",
            "The recovery projection did not apply database-side identity search.");
        Require((await authority.LoadRecoveryArtifactsPageAsync("pair-b", 10)).Count == 0,
            "The recovery projection leaked artifacts across pair authority.");
    }

    private static async Task AssertAtomicPathReservationAsync(SyncAuthorityDatabase authority, SyncAuthorityCommandAdmission admission)
    {
        await authority.CreateOperationAsync(Plan("operation-path-reservation-a",
            expectedPath: "reserved-path.txt"), admission: admission);
        await ExpectRejectAsync(() => authority.CreateOperationAsync(Plan("operation-path-reservation-b",
            expectedPath: "RESERVED-PATH.txt"), admission: admission));
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM operation WHERE operation_id='operation-path-reservation-b';") == 0,
            "A competing root/path plan survived the atomic reservation rejection.");

        const string unicodeUpper = "Å-reserved-path.txt";
        const string unicodeLower = "å-reserved-path.txt";
        Require(string.Equals(unicodeUpper, unicodeLower, StringComparison.OrdinalIgnoreCase),
            "The Unicode reservation fixture does not exercise the authoritative Windows comparator.");
        await authority.CreateOperationAsync(Plan("operation-path-reservation-unicode-a",
            expectedPath: unicodeUpper), admission: admission);
        await ExpectRejectAsync(() => authority.CreateOperationAsync(Plan(
            "operation-path-reservation-unicode-b", expectedPath: unicodeLower), admission: admission));
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM operation WHERE operation_id='operation-path-reservation-unicode-b';") == 0,
            "A non-ASCII OrdinalIgnoreCase-equivalent path evaded the serialized durable reservation fence.");

        var competitor = new SyncAuthorityDatabase(authority.DatabasePath);
        var raced = await Task.WhenAll(
            TryReserveAsync(authority, "operation-path-reservation-race-a", admission),
            TryReserveAsync(competitor, "operation-path-reservation-race-b", admission));
        Require(raced.Count(item => item) == 1 && await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM operation WHERE operation_id IN ('operation-path-reservation-race-a','operation-path-reservation-race-b');") == 1,
            "Two independent authority connections acquired the same root/path reservation.");

        static async Task<bool> TryReserveAsync(SyncAuthorityDatabase database, string operationId, SyncAuthorityCommandAdmission admission)
        {
            try
            {
                await database.CreateOperationAsync(Plan(operationId, expectedPath: "raced-path.txt"), admission: admission);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqliteException)
            {
                return false;
            }
        }
    }

    private static async Task AssertConditionalDeleteFailureClassificationAsync()
    {
        foreach (var movedPath in new[] { "/Pilot/renamed-wire.txt", "/Outside/wire.txt" })
        {
            var moved = new DropboxContractHandler
            {
                WireMetadataPathForRequest = _ => movedPath,
                ArchiveHash = HashA,
                ArchiveSize = 1
            };
            using var httpClient = new HttpClient(moved);
            var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", connection,
                "id:pilot-folder");
            await ExpectExceptionAsync<Exception>(() => client.DeleteAsync(
                new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "rev:archive", 1, HashA)));
            Require(moved.DeleteCalls == 0,
                "A renamed or cross-root Dropbox source reached conditional deletion without fresh reconciliation.");
        }

        foreach (var movedPath in new[] { "/Pilot/renamed-wire.txt", "/Outside/wire.txt" })
        {
            var movedAtBoundary = new DropboxContractHandler
            {
                WireMetadataPathForRequest = request => request == 1 ? "/Pilot/wire.txt" : movedPath,
                ArchiveHash = HashA,
                ArchiveSize = 1
            };
            using var httpClient = new HttpClient(movedAtBoundary);
            var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", connection,
                "id:pilot-folder");
            await ExpectExceptionAsync<Exception>(() => client.DeleteAsync(
                new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "rev:archive", 1, HashA)));
            Require(movedAtBoundary.WireMetadataRequests == 2 && movedAtBoundary.DeleteCalls == 0,
                "A Dropbox source move at the final effect boundary reached conditional deletion.");
        }

        await ExpectUncertainDeleteAsync(new DropboxContractHandler
            { DeleteStatusCode = HttpStatusCode.InternalServerError });
        await ExpectUncertainDeleteAsync(new DropboxContractHandler
            { DeleteSuccessBodyOverride = "{\"metadata\":" });
        await ExpectUncertainDeleteAsync(new DropboxContractHandler
            { ThrowOnDeleteBodyRead = true });

        static async Task ExpectUncertainDeleteAsync(DropboxContractHandler handler)
        {
            using var httpClient = new HttpClient(handler);
            var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", connection,
                "id:pilot-folder");
            handler.ArchiveHash = HashA;
            handler.ArchiveSize = 1;
            await ExpectExceptionAsync<DropboxEffectUncertainException>(() => client.DeleteAsync(
                new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "rev:archive", 1, HashA)));
            Require(handler.DeleteCalls == 1 &&
                    string.Equals(handler.DeletePath, "id:pilot-folder/wire.txt", StringComparison.Ordinal),
                "Conditional deletion was not atomically path-bound beneath the selected pair root ID.");
        }
    }

    private static async Task AssertEffectFreeObservationsAsync(SyncAuthorityDatabase authority)
    {
        var first = await authority.BeginScanGenerationAsync("pair-a", "root-local", "local", "initial", "{}");
        await authority.AppendObservationAsync(first, "observation-stable-1", null, "membership-a",
            "{\"path\":\"file.txt\",\"hash\":\"stable\"}");
        await authority.CompleteScanGenerationAsync(first);
        Require(!await authority.HasTwoStableCompleteObservationsAsync("pair-a", "local"),
            "One complete observation incorrectly established initial authority.");

        var second = await authority.BeginScanGenerationAsync("pair-a", "root-local", "local", "initial", "{}");
        await authority.AppendObservationAsync(second, "observation-stable-2", null, "membership-a",
            "{\"path\":\"file.txt\",\"hash\":\"stable\"}");
        await authority.CompleteScanGenerationAsync(second);
        Require(await authority.HasTwoStableCompleteObservationsAsync("pair-a", "local"),
            "Two unchanged complete observations did not establish effect-free stability.");

        var incomplete = await authority.BeginScanGenerationAsync("pair-a", "root-local", "local", "manual", "{}");
        await authority.AppendObservationAsync(incomplete, "observation-incomplete", null, "membership-a",
            "{\"path\":\"file.txt\",\"hash\":\"changed\"}");
        Require(await authority.HasTwoStableCompleteObservationsAsync("pair-a", "local"),
            "An incomplete generation was incorrectly treated as absence or changed authority.");
        await authority.CompleteScanGenerationAsync(incomplete);
        Require(!await authority.HasTwoStableCompleteObservationsAsync("pair-a", "local"),
            "Changed complete observations were incorrectly treated as a stable pair.");
    }

    private static async Task AssertObservationCoordinatorAsync(SyncAuthorityDatabase authority, string testRoot,
        bool recoveryCorrectionOnly = false)
    {
        var localRoot = Path.Combine(testRoot, "observed-root");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"), "local observation only");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-observe", "Observe", localRoot, identity.VolumeSerialNumber, identity.FileId,
            "/Observe", "folder-observe");
        const string installationId = "installation-observe-0001";
        const string controlNamespaceId = "control-observe-0001";
        SyncPair boundPair = pair;
        if (OperatingSystem.IsWindows())
        {
            SyncLocalControlNamespaceBinding localControl;
            using (var selectedRoot = PilotDirectoryLease.Open(localRoot, identity))
                localControl = SyncControlNamespace.BindLocalForTest(selectedRoot, installationId,
                    controlNamespaceId);
            boundPair = pair with
            {
                ControlNamespace = new SyncControlNamespaceBinding(installationId, controlNamespaceId,
                    localControl),
                RecoveryRoot = new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                    "recovery-folder", "root-namespace-a")
            };
        }
        await authority.RegisterPairAsync(pair.Id, 1);
        var admission = await AdmitFixturePairsAsync(authority, pair.Id);
        await authority.RegisterRootBindingAsync("root-observe-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync("root-observe-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"folder-observe\"}", pair.DropboxRoot);
        var remote = new FakeSyncRemoteStore();
        remote.Put("remote.txt", "remote observation only");
        var coordinator = new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-observe-local", "root-observe-dropbox");
        var first = await coordinator.ObserveAsync("initial");
        var second = await coordinator.ObserveAsync("initial");
        Require(first.LocalEntries == 1 && first.DropboxEntries == 1 && second.LocalEntries == 1 &&
                second.DropboxEntries == 1,
            "The effect-free observation coordinator did not retain both complete pair views.");
        Require(await authority.HasTwoStableCompleteObservationsAsync(pair.Id, "local") &&
                await authority.HasTwoStableCompleteObservationsAsync(pair.Id, "dropbox"),
            "Two unchanged coordinated scans did not establish side-specific stability.");
        var adoption = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        Require(adoption.Count == 2 && adoption.All(item => item.Kind == SyncDecisionKind.Baseline),
            "Stable unmatched initial entries were not adopted effect-free as independent baselines.");
        Require(remote.UploadCount == 0 && remote.DownloadCount == 0 && remote.ArchiveCount == 0,
            "Initial observation performed an external Dropbox effect.");

        var planner = new SyncAuthorityReconciliationPlanner(authority,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", controlNamespaceId,
                controlNamespaceId), admission: admission);
        var initialMerge = await planner.PlanAsync(pair.Id);
        Require(initialMerge.Count == 2 && initialMerge.All(item =>
                    item.Kind == SyncAuthorityPlannerOutcomeKind.Operation && item.OperationId is not null),
            "One-sided first-run discoveries were not converted into an explicit merge plan.");
        if (!OperatingSystem.IsWindows()) return;
        var initialRemoteCreate = initialMerge.Single(item => item.RelativePath == "local.txt");
        var initialLocalCreate = initialMerge.Single(item => item.RelativePath == "remote.txt");
        Require((await authority.LoadOperationAsync(initialRemoteCreate.OperationId!)).Kind ==
                SyncAuthorityOperationKind.CreateRemote &&
                (await authority.LoadOperationAsync(initialLocalCreate.OperationId!)).Kind ==
                SyncAuthorityOperationKind.CreateLocal,
            "The first-run merge did not preserve the direction of each one-sided discovery.");
        await new SyncAuthorityCreateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(initialRemoteCreate.OperationId!, "worker-bootstrap-create-remote");
        var untouchedInitialLocal = await authority.LoadOperationAsync(initialLocalCreate.OperationId!);
        Require(untouchedInitialLocal.PlanningAuthorities.Count == 1 &&
                await authority.IsPlanningAuthorityCurrentAsync(untouchedInitialLocal.PlanningAuthorities[0]),
            "An authorized sibling effect incorrectly invalidated an unrelated object-scoped operation.");
        await new SyncAuthorityCreateLocalExecutor(boundPair, remote, authority)
            .ExecuteAsync(initialLocalCreate.OperationId!, "worker-bootstrap-create-local");
        await coordinator.ObserveAsync("monitor");
        var unchanged = await planner.PlanAsync(pair.Id);
        Require(unchanged.Count == 2 && unchanged.All(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged),
            "The three-view planner did not classify the completed first-run merge as unchanged: " +
            string.Join("; ", unchanged.Select(item =>
                $"{item.RelativePath}={item.Kind}/{item.OperationId}/{item.Reason}")));

        await File.WriteAllTextAsync(Path.Combine(localRoot, "created-after-bootstrap.txt"),
            "local observation changed");
        await coordinator.ObserveAsync("monitor");
        var changed = await planner.PlanAsync(pair.Id);
        var createRemote = changed.Single(item => item.RelativePath == "created-after-bootstrap.txt");
        Require(createRemote.Kind == SyncAuthorityPlannerOutcomeKind.Operation && createRemote.OperationId is not null,
            "A one-sided local change did not produce one immutable remote operation.");
        var retained = await authority.LoadOperationAsync(createRemote.OperationId!);
        Require(retained.Kind == SyncAuthorityOperationKind.CreateRemote && retained.Members.Count == 2 &&
                retained.Allocations.Count == 0 && retained.Attempts.Count == 0,
            "The three-view create plan did not retain exact members without inventing an unused upload-session allocation.");
        var executed = await new SyncAuthorityCreateRemoteExecutor(pair, remote, authority)
            .ExecuteAsync(createRemote.OperationId!, "worker-create-remote");
        Require(executed.Status == SyncAuthorityOperationStatus.Completed && executed.EffectDispatched &&
                remote.Exists("created-after-bootstrap.txt") &&
                remote.ReadText("created-after-bootstrap.txt") == "local observation changed",
            "The fenced add-only executor did not verify and commit the planned remote create.");
        var completed = await authority.LoadOperationAsync(createRemote.OperationId!);
        Require(completed.Status == SyncAuthorityOperationStatus.Completed &&
                completed.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "The successful remote create did not retain a verified terminal attempt.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "collision.txt"), "local collision");
        remote.Put("collision.txt", "remote collision");
        await coordinator.ObserveAsync("monitor");
        var conflicted = await planner.PlanAsync(pair.Id);
        var conflict = conflicted.Single(item => item.RelativePath == "collision.txt");
        Require(conflict.Kind == SyncAuthorityPlannerOutcomeKind.Conflict && conflict.ConflictId is not null,
            "Different same-path versions were not retained as a durable conflict generation.");
        Require(await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM conflict_version WHERE conflict_id='{conflict.ConflictId}';") == 2,
            "The conflict generation did not retain both exact versions.");
        Require(remote.UploadCount == 2 && remote.DownloadCount == 1 && remote.ArchiveCount == 0,
            "Planning performed an effect or the single fenced executor dispatched an unexpected extra effect.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "uncertain.txt"), "uncertain upload");
        await coordinator.ObserveAsync("monitor");
        var uncertainPlan = (await planner.PlanAsync(pair.Id)).Single(item => item.RelativePath == "uncertain.txt");
        remote.InterruptNextUploadAfterEffect = true;
        var uncertain = await new SyncAuthorityCreateRemoteExecutor(pair, remote, authority)
            .ExecuteAsync(uncertainPlan.OperationId!, "worker-create-remote-unknown");
        Require(uncertain.Status == SyncAuthorityOperationStatus.EffectUnknown && remote.Exists("uncertain.txt"),
            "A lost response after the provider effect was not retained as effect-unknown.");
        await ExpectRejectAsync(() => new SyncAuthorityCreateRemoteExecutor(pair, remote, authority)
            .ExecuteAsync(uncertainPlan.OperationId!, "worker-create-remote-illegal-retry"));
        var reconciled = await new SyncAuthorityCreateRemoteReconciler(pair, remote, authority)
            .ReconcileAsync(uncertainPlan.OperationId!, "worker-create-remote-reconcile");
        Require(reconciled.Status == SyncAuthorityOperationStatus.Completed && !reconciled.EffectDispatched &&
                (await authority.LoadOperationAsync(uncertainPlan.OperationId!)).Attempts.Single().Status ==
                SyncAuthorityAttemptStatus.VerifiedSuccess,
            "The exact unknown successor was not committed through read-only reconciliation.");
        Require(remote.UploadCount == 3,
            "The effect-unknown remote create was retried before terminal reconciliation.");

        remote.Put("download.txt", "remote create-local content");
        await coordinator.ObserveAsync("monitor");
        var boundPlanner = new SyncAuthorityReconciliationPlanner(authority,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", controlNamespaceId,
                controlNamespaceId), admission: admission);
        var localPlan = (await boundPlanner.PlanAsync(pair.Id)).Single(item => item.RelativePath == "download.txt");
        var localOperation = await authority.LoadOperationAsync(localPlan.OperationId!);
        Require(localOperation.Kind == SyncAuthorityOperationKind.CreateLocal &&
                localOperation.Allocations.Count == 2,
            "The local create was not planned with transaction and stage allocations.");
        var localExecuted = await new SyncAuthorityCreateLocalExecutor(boundPair, remote, authority)
            .ExecuteAsync(localPlan.OperationId!, "worker-create-local");
        Require(localExecuted.Status == SyncAuthorityOperationStatus.Completed &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")) ==
                "remote create-local content" && remote.DownloadCount == 2,
            "The exact remote revision was not committed through the same-volume local create transaction.");
        Require((await authority.LoadOperationAsync(localPlan.OperationId!)).Allocations
                .All(item => item.Status == SyncAuthorityAllocationStatus.Released),
            "Completed local-create allocations were not released after verified promotion and cleanup.");

        remote.Put("crash-local.txt", "promoted before receipt");
        await coordinator.ObserveAsync("monitor");
        var crashPlan = (await boundPlanner.PlanAsync(pair.Id)).Single(item => item.RelativePath == "crash-local.txt");
        SyncAuthorityCreateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "target-promoted-before-receipt") throw new IOException("simulated process boundary");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityCreateLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(crashPlan.OperationId!, "worker-create-local-crash"));
        }
        finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
        Require(File.Exists(Path.Combine(localRoot, "crash-local.txt")) &&
                (await authority.LoadOperationAsync(crashPlan.OperationId!)).Status ==
                SyncAuthorityOperationStatus.EffectIssuing,
            "The simulated post-promotion crash did not retain its pre-receipt authority boundary.");
        var crashRecovered = await new SyncAuthorityCreateLocalReconciler(boundPair, authority)
            .ReconcileAsync(crashPlan.OperationId!, "worker-create-local-reconcile");
        Require(crashRecovered.Status == SyncAuthorityOperationStatus.Completed &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "crash-local.txt")) ==
                "promoted before receipt" && remote.DownloadCount == 3,
            "The promoted target was not recovered without a second Dropbox download.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"), "replacement from local");
        await coordinator.ObserveAsync("monitor");
        var updateRemotePlan = (await boundPlanner.PlanAsync(pair.Id)).Single(item => item.RelativePath == "local.txt");
        var updateRemoteOperation = await authority.LoadOperationAsync(updateRemotePlan.OperationId!);
        Require(updateRemoteOperation.Kind == SyncAuthorityOperationKind.UpdateRemote &&
                updateRemoteOperation.Allocations.Single().Kind ==
                SyncAuthorityAllocationKind.RemoteRecoveryFile,
            "The local modification was not planned with remote predecessor recovery authority.");
        var updatedRemote = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(updateRemotePlan.OperationId!, "worker-update-remote");
        updateRemoteOperation = await authority.LoadOperationAsync(updateRemotePlan.OperationId!);
        Require(updatedRemote.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("local.txt") == "replacement from local" &&
                updateRemoteOperation.Attempts.Count == 2 &&
                updateRemoteOperation.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM recovery_artifact WHERE operation_id='{updateRemotePlan.OperationId}';") == 1,
            "The exact-revision remote replacement did not retain its predecessor and commit both fenced effects.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"),
            "replacement after recovery copy was not applied");
        await coordinator.ObserveAsync("monitor");
        var recoveryNotAppliedPlan = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "local.txt");
        var archivesBeforeNotApplied = remote.ArchiveCount;
        remote.InterruptNextArchiveBeforeEffect = true;
        await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(recoveryNotAppliedPlan.OperationId!, "worker-update-remote-recovery-not-applied"));
        var interruptedNotApplied = await authority.LoadOperationAsync(recoveryNotAppliedPlan.OperationId!);
        Require(interruptedNotApplied.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                interruptedNotApplied.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Creating &&
                remote.ArchiveCount == archivesBeforeNotApplied,
            "A pre-effect recovery-copy interruption did not retain its exact retry boundary.");
        var notAppliedReconciled = await new SyncAuthorityUpdateRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(recoveryNotAppliedPlan.OperationId!, "worker-update-remote-recovery-not-applied-reconcile");
        Require(notAppliedReconciled.Status == SyncAuthorityOperationStatus.Ready &&
                !notAppliedReconciled.EffectDispatched &&
                (await authority.LoadOperationAsync(recoveryNotAppliedPlan.OperationId!)).Attempts.Single().Status ==
                SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "An absent recovery copy was not reconciled as safely retryable.");
        var notAppliedResumed = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(recoveryNotAppliedPlan.OperationId!, "worker-update-remote-recovery-not-applied-resume");
        Require(notAppliedResumed.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("local.txt") == "replacement after recovery copy was not applied" &&
                remote.ArchiveCount == archivesBeforeNotApplied + 1,
            "The verified-not-applied recovery copy did not resume from its Creating allocation exactly once.");
        var completedNotApplied = await authority.LoadOperationAsync(recoveryNotAppliedPlan.OperationId!);
        Require(completedNotApplied.Attempts.Count == 3 &&
                completedNotApplied.Attempts.Count(item => item.Status == SyncAuthorityAttemptStatus.VerifiedNotApplied) == 1 &&
                completedNotApplied.Attempts.Count(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) == 2 &&
                completedNotApplied.Allocations.Single().AllocationId == interruptedNotApplied.Allocations.Single().AllocationId,
            "Recovery retry lost its original allocation or attempt history.");
        await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(recoveryNotAppliedPlan.OperationId!, "worker-update-remote-duplicate"));
        Require(remote.ArchiveCount == archivesBeforeNotApplied + 1,
            "A completed recovery retry allowed another copy.");
        Console.WriteLine("PASS RECOVERY-RETRY: pre-effect failure -> Creating -> VerifiedNotApplied -> same allocation -> exactly one recovery and replacement; completed replay rejected");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"),
            "replacement after unknown recovery response");
        await coordinator.ObserveAsync("monitor");
        var recoveryUnknownPlan = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "local.txt");
        var archivesBeforeUnknownRecovery = remote.ArchiveCount;
        remote.InterruptNextArchiveAfterEffect = true;
        var recoveryUnknown = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(recoveryUnknownPlan.OperationId!, "worker-update-remote-recovery-unknown");
        Require(recoveryUnknown.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                remote.ReadText("local.txt") == "replacement after recovery copy was not applied" &&
                remote.ArchiveCount == archivesBeforeUnknownRecovery + 1,
            "A lost remote-replacement recovery response did not stop before replacement.");
        var recoveryReconciled = await new SyncAuthorityUpdateRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(recoveryUnknownPlan.OperationId!, "worker-update-remote-recovery-reconcile");
        Require(recoveryReconciled.Status == SyncAuthorityOperationStatus.Ready &&
                !recoveryReconciled.EffectDispatched &&
                remote.ArchiveCount == archivesBeforeUnknownRecovery + 1,
            "The exact remote predecessor recovery was not reconciled without replay.");
        var recoveryResumed = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(recoveryUnknownPlan.OperationId!, "worker-update-remote-recovery-resume");
        Require(recoveryResumed.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("local.txt") == "replacement after unknown recovery response" &&
                remote.ArchiveCount == archivesBeforeUnknownRecovery + 1,
            "The reconciled remote predecessor was copied again instead of resuming replacement.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"),
            "replacement with unknown upload response");
        await coordinator.ObserveAsync("monitor");
        var updateUnknownPlan = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "local.txt");
        var uploadsBeforeUnknownUpdate = remote.UploadCount;
        remote.InterruptNextUploadAfterEffect = true;
        var updateUnknown = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(updateUnknownPlan.OperationId!, "worker-update-remote-upload-unknown");
        Require(updateUnknown.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                remote.ReadText("local.txt") == "replacement with unknown upload response",
            "A lost conditional-replacement response did not retain the applied successor as unknown.");
        var updateReconciled = await new SyncAuthorityUpdateRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(updateUnknownPlan.OperationId!, "worker-update-remote-upload-reconcile");
        Require(updateReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !updateReconciled.EffectDispatched && remote.UploadCount == uploadsBeforeUnknownUpdate + 1 &&
                (await authority.LoadOperationAsync(updateUnknownPlan.OperationId!)).Attempts.Count == 2,
            "The applied remote replacement was not committed through read-only reconciliation.");
        if (recoveryCorrectionOnly)
        {
            await AssertRecoveryConsumerCorrectionsAsync(boundPair, authority, remote, localRoot, boundPlanner,
                coordinator);
            Console.WriteLine("PASS RECOVERY-RETRY: lost recovery acknowledgement and lost replacement acknowledgement reconcile without duplicate effects");
            return;
        }

        remote.Put("download.txt", "replacement from dropbox");
        await coordinator.ObserveAsync("monitor");
        var updateLocalPlan = (await boundPlanner.PlanAsync(pair.Id)).Single(item => item.RelativePath == "download.txt");
        var updateLocalOperation = await authority.LoadOperationAsync(updateLocalPlan.OperationId!);
        Require(updateLocalOperation.Kind == SyncAuthorityOperationKind.UpdateLocal &&
                updateLocalOperation.Allocations.Count == 3,
            "The Dropbox modification was not planned with transaction, stage, and predecessor-recovery authority.");
        var updatedLocal = await new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority)
            .ExecuteAsync(updateLocalPlan.OperationId!, "worker-update-local");
        updateLocalOperation = await authority.LoadOperationAsync(updateLocalPlan.OperationId!);
        Require(updatedLocal.Status == SyncAuthorityOperationStatus.Completed &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")) ==
                "replacement from dropbox" && updateLocalOperation.Attempts.Count == 2 &&
                updateLocalOperation.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) &&
                updateLocalOperation.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Verified &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM recovery_artifact WHERE operation_id='{updateLocalPlan.OperationId}';") == 1,
            "The local replacement did not retain its exact predecessor and commit its promoted successor.");

        remote.Put("download.txt", "replacement after retained-predecessor crash");
        await coordinator.ObserveAsync("monitor");
        var retainedCrashPlan = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "download.txt");
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-update-predecessor-retained-before-receipt")
                throw new IOException("simulated local replacement retention crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(retainedCrashPlan.OperationId!, "worker-update-local-retention-crash"));
        }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        Require(!File.Exists(Path.Combine(localRoot, "download.txt")) &&
                (await authority.LoadOperationAsync(retainedCrashPlan.OperationId!)).Status ==
                SyncAuthorityOperationStatus.EffectIssuing,
            "The post-retention local replacement crash did not preserve its exact recovery boundary.");
        var retainedCrashReconciled = await new SyncAuthorityUpdateLocalReconciler(boundPair, remote, authority)
            .ReconcileAsync(retainedCrashPlan.OperationId!, "worker-update-local-retention-reconcile");
        Require(retainedCrashReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                retainedCrashReconciled.EffectDispatched &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")) ==
                "replacement after retained-predecessor crash" &&
                (await authority.LoadOperationAsync(retainedCrashPlan.OperationId!)).Attempts.Count == 2,
            "The retained local predecessor was not reconciled before one fenced stage promotion.");

        remote.Put("download.txt", "replacement after promoted-stage crash");
        await coordinator.ObserveAsync("monitor");
        var promotionCrashPlan = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "download.txt");
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-update-stage-promoted-before-receipt")
                throw new IOException("simulated local replacement promotion crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(promotionCrashPlan.OperationId!, "worker-update-local-promotion-crash"));
        }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        Require(await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")) ==
                "replacement after promoted-stage crash" &&
                (await authority.LoadOperationAsync(promotionCrashPlan.OperationId!)).Status ==
                SyncAuthorityOperationStatus.EffectIssuing,
            "The post-promotion local replacement crash did not retain the promoted identity boundary.");
        var promotionCrashReconciled = await new SyncAuthorityUpdateLocalReconciler(boundPair, remote, authority)
            .ReconcileAsync(promotionCrashPlan.OperationId!, "worker-update-local-promotion-reconcile");
        Require(promotionCrashReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !promotionCrashReconciled.EffectDispatched &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")) ==
                "replacement after promoted-stage crash" &&
                (await authority.LoadOperationAsync(promotionCrashPlan.OperationId!)).Attempts.Count == 2,
            "The promoted local replacement was not committed through read-only reconciliation.");

        var retainedConflict = await authority.LoadConflictAsync(conflict.ConflictId!);
        var resolutionCoordinator = new SyncAuthorityConflictResolutionCoordinator(boundPair, remote, authority,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", controlNamespaceId,
                controlNamespaceId, EnableDeletionEffects: true), admission);
        var resolutionPlan = await resolutionCoordinator.PlanAsync(conflict.ConflictId!,
            retainedConflict.ConflictGeneration, SyncAuthorityConflictResolutionKind.KeepLocal);
        var resolutionResult = await resolutionCoordinator.ExecuteAsync(resolutionPlan,
            "worker-conflict-keep-local");
        Require(resolutionResult.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("collision.txt") == "local collision" &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM conflict WHERE conflict_id='{conflict.ConflictId}';"),
                    "resolved", StringComparison.Ordinal),
            "The reviewed conflict generation did not resolve through recovery-first keep-local replacement.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "preserve.txt"), "local preserve winner");
        remote.Put("preserve.txt", "Dropbox preserve loser");
        await coordinator.ObserveAsync("monitor");
        var preserveConflict = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "preserve.txt");
        var retainedPreserveConflict = await authority.LoadConflictAsync(preserveConflict.ConflictId!);
        var visibleCopiesBefore = remote.VisibleCopyCount;
        var preservePlan = await resolutionCoordinator.PlanAsync(preserveConflict.ConflictId!,
            retainedPreserveConflict.ConflictGeneration, SyncAuthorityConflictResolutionKind.PreserveBoth);
        Require(preservePlan.PreserveOperationId is not null && preservePlan.ConflictCopyPath is not null &&
                preservePlan.ConflictCopyPath.Contains("Sync Shark conflict - Dropbox", StringComparison.Ordinal) &&
                preservePlan.ConflictCopyPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
            "Preserve-both did not allocate one deterministic visible Dropbox conflict-copy name.");
        var preserveCopyOperation = await authority.LoadOperationAsync(preservePlan.PreserveOperationId!);
        var preserveWinnerOperation = await authority.LoadOperationAsync(preservePlan.OperationId);
        Require(preserveCopyOperation.Kind == SyncAuthorityOperationKind.ResolveConflict &&
                preserveWinnerOperation.Kind == SyncAuthorityOperationKind.UpdateRemote &&
                preserveWinnerOperation.Dependencies.Single().DependsOnOperationId == preservePlan.PreserveOperationId &&
                preserveWinnerOperation.Dependencies.Single().Kind == "must_complete",
            "Preserve-both did not retain the preserve-loser-before-apply-winner dependency graph.");
        var preserveResult = await resolutionCoordinator.ExecuteAsync(preservePlan,
            "worker-conflict-preserve-both");
        Require(preserveResult.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("preserve.txt") == "local preserve winner" &&
                remote.ReadText(preservePlan.ConflictCopyPath!) == "Dropbox preserve loser" &&
                remote.VisibleCopyCount == visibleCopiesBefore + 1 &&
                (await authority.LoadOperationAsync(preservePlan.PreserveOperationId!)).Status ==
                SyncAuthorityOperationStatus.Completed &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM conflict WHERE conflict_id='{preserveConflict.ConflictId}';"),
                    "resolved", StringComparison.Ordinal),
            "Preserve-both did not visibly retain the Dropbox loser before conditionally applying the local winner.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "preserve-unknown.txt"),
            "local preserve unknown winner");
        remote.Put("preserve-unknown.txt", "Dropbox preserve unknown loser");
        await coordinator.ObserveAsync("monitor");
        var preserveUnknownConflict = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "preserve-unknown.txt");
        var retainedPreserveUnknown = await authority.LoadConflictAsync(preserveUnknownConflict.ConflictId!);
        var preserveUnknownPlan = await resolutionCoordinator.PlanAsync(preserveUnknownConflict.ConflictId!,
            retainedPreserveUnknown.ConflictGeneration, SyncAuthorityConflictResolutionKind.PreserveBoth);
        remote.InterruptNextVisibleCopyAfterEffect = true;
        var preserveUnknown = await resolutionCoordinator.ExecuteAsync(preserveUnknownPlan,
            "worker-conflict-preserve-unknown");
        Require(preserveUnknown.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                remote.ReadText(preserveUnknownPlan.ConflictCopyPath!) == "Dropbox preserve unknown loser",
            "A lost visible-copy response was not retained as an unknown prerequisite effect.");
        var copiesAfterUnknown = remote.VisibleCopyCount;
        await ExpectRejectAsync(() => new SyncAuthorityPreserveConflictCopyExecutor(boundPair, remote, authority)
            .ExecuteAsync(preserveUnknownPlan.PreserveOperationId!, "worker-conflict-preserve-illegal-retry"));
        var preserveUnknownResolved = await resolutionCoordinator.ExecuteAsync(preserveUnknownPlan,
            "worker-conflict-preserve-reconcile");
        Require(preserveUnknownResolved.Status == SyncAuthorityOperationStatus.Completed &&
                remote.VisibleCopyCount == copiesAfterUnknown &&
                remote.ReadText("preserve-unknown.txt") == "local preserve unknown winner" &&
                remote.ReadText(preserveUnknownPlan.ConflictCopyPath!) == "Dropbox preserve unknown loser" &&
                (await authority.LoadOperationAsync(preserveUnknownPlan.PreserveOperationId!)).Attempts.Count == 1 &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM conflict WHERE conflict_id='{preserveUnknownConflict.ConflictId}';"),
                    "resolved", StringComparison.Ordinal),
            "The unknown visible conflict copy was replayed or failed to resume its dependent winner application.");

        var deletionPlanner = new SyncAuthorityReconciliationPlanner(authority,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", controlNamespaceId,
                controlNamespaceId, EnableDeletionEffects: true,
                DeletionMinimumInterval: TimeSpan.Zero), admission: admission);
        var retainedLocalEntries = await authority.LoadLatestCompleteObservationAsync(pair.Id, "local");
        var uncertainLocalScan = await authority.BeginScanGenerationAsync(pair.Id, "root-observe-local", "local",
            "manual", "{\"scope\":\"complete_with_uncertainty\"}");
        foreach (var retainedEntry in retainedLocalEntries.Where(item =>
                     !string.Equals(item.RelativePath, "local.txt", StringComparison.OrdinalIgnoreCase)))
            await authority.AppendObservationAsync(uncertainLocalScan,
                "observation-retained-" + TestHash(uncertainLocalScan.ScanGenerationId, retainedEntry.MembershipId)[..24],
                retainedEntry.ObjectVersionId, retainedEntry.MembershipId, retainedEntry.EvidenceJson);
        await authority.AppendObservationAsync(uncertainLocalScan, "observation-local-unreadable", null, null,
            "{\"path\":\"local.txt\",\"state\":\"unavailable\",\"reason\":\"fixture unreadable\"}");
        await authority.CompleteScanGenerationAsync(uncertainLocalScan);
        var uncertaintyBlocked = (await deletionPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "local.txt");
        Require(uncertaintyBlocked.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                uncertaintyBlocked.OperationId is null && uncertaintyBlocked.TombstoneId is null &&
                uncertaintyBlocked.Reason.Contains("uncertainty is not absence", StringComparison.Ordinal),
            "An unreadable latest local observation was converted into remote-deletion authority.");
        await coordinator.ObserveAsync("monitor");
        File.Delete(Path.Combine(localRoot, "local.txt"));
        await coordinator.ObserveAsync("monitor");
        var firstDeleteRemoteObservation = (await deletionPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "local.txt");
        var replayedDeleteRemoteObservation = (await deletionPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "local.txt");
        Require(firstDeleteRemoteObservation.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                firstDeleteRemoteObservation.OperationId is not null &&
                replayedDeleteRemoteObservation.OperationId == firstDeleteRemoteObservation.OperationId &&
                firstDeleteRemoteObservation.TombstoneId == replayedDeleteRemoteObservation.TombstoneId &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT complete_observation_count FROM tombstone WHERE tombstone_id='{firstDeleteRemoteObservation.TombstoneId}';") == 1,
            "One authoritative complete scan did not promptly plan exactly one replay-stable deletion.");
        var deleteRemotePlan = firstDeleteRemoteObservation;
        var deleteRemoteOperation = await authority.LoadOperationAsync(deleteRemotePlan.OperationId!);
        Require(deleteRemoteOperation.Kind == SyncAuthorityOperationKind.DeleteRemote &&
                deleteRemoteOperation.Allocations.Single().Kind ==
                SyncAuthorityAllocationKind.RemoteRecoveryFile,
            "A proved local absence did not plan a recovery-first remote deletion.");
        var remoteDeleted = await new SyncAuthorityDeleteRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(deleteRemotePlan.OperationId!, "worker-delete-remote");
        Require(remoteDeleted.Status == SyncAuthorityOperationStatus.Completed &&
                !remote.Exists("local.txt") && remote.DeleteCount == 1 &&
                (await authority.LoadOperationAsync(deleteRemotePlan.OperationId!)).Attempts.Count == 2 &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM tombstone WHERE tombstone_id='{deleteRemotePlan.TombstoneId}';"),
                    "resolved", StringComparison.Ordinal),
            "The exact Dropbox revision was not retained, conditionally deleted, and committed to its tombstone.");

        remote.Delete("download.txt");
        await coordinator.ObserveAsync("monitor");
        var deleteLocalPlan = (await deletionPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "download.txt");
        var deleteLocalOperation = await authority.LoadOperationAsync(deleteLocalPlan.OperationId!);
        Require(deleteLocalOperation.Kind == SyncAuthorityOperationKind.DeleteLocal &&
                deleteLocalOperation.Allocations.Count == 2,
            "A proved Dropbox absence did not plan a protected local-retention deletion.");
        var localDeleted = await new SyncAuthorityDeleteLocalExecutor(boundPair, authority)
            .ExecuteAsync(deleteLocalPlan.OperationId!, "worker-delete-local");
        deleteLocalOperation = await authority.LoadOperationAsync(deleteLocalPlan.OperationId!);
        Require(localDeleted.Status == SyncAuthorityOperationStatus.Completed &&
                !File.Exists(Path.Combine(localRoot, "download.txt")) &&
                deleteLocalOperation.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess &&
                deleteLocalOperation.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Verified &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM tombstone WHERE tombstone_id='{deleteLocalPlan.TombstoneId}';"),
                    "resolved", StringComparison.Ordinal),
            "The exact local predecessor was not removed from membership by protected retention.");

        File.Delete(Path.Combine(localRoot, "uncertain.txt"));
        await coordinator.ObserveAsync("monitor");
        var uncertainDeletePlan = (await deletionPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "uncertain.txt");
        Require(uncertainDeletePlan.OperationId is not null,
            "An authoritative complete local-absence observation did not promptly plan remote deletion.");
        remote.InterruptNextDeleteAfterEffect = true;
        var uncertainDelete = await new SyncAuthorityDeleteRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(uncertainDeletePlan.OperationId!, "worker-delete-remote-unknown");
        Require(uncertainDelete.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                !remote.Exists("uncertain.txt"),
            "A lost conditional-deletion response was not retained as effect-unknown.");
        await ExpectRejectAsync(() => new SyncAuthorityDeleteRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(uncertainDeletePlan.OperationId!, "worker-delete-remote-illegal-retry"));
        var uncertainDeleteReconciled = await new SyncAuthorityDeleteRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(uncertainDeletePlan.OperationId!, "worker-delete-remote-reconcile");
        Require(uncertainDeleteReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !uncertainDeleteReconciled.EffectDispatched &&
                (await authority.LoadOperationAsync(uncertainDeletePlan.OperationId!)).Attempts.Count == 2 &&
                remote.DeleteCount == 2,
            "The effect-unknown conditional deletion was not reconciled without a second delete.");

        remote.Delete("crash-local.txt");
        await coordinator.ObserveAsync("monitor");
        var crashDeleteLocalPlan = (await deletionPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "crash-local.txt");
        Require(crashDeleteLocalPlan.OperationId is not null,
            "An authoritative complete Dropbox-absence observation did not promptly plan local deletion.");
        SyncAuthorityDeleteLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-retained-before-receipt") throw new IOException("simulated local-delete crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityDeleteLocalExecutor(boundPair, authority)
                .ExecuteAsync(crashDeleteLocalPlan.OperationId!, "worker-delete-local-crash"));
        }
        finally { SyncAuthorityDeleteLocalExecutor.PhaseForTest = null; }
        Require(!File.Exists(Path.Combine(localRoot, "crash-local.txt")) &&
                (await authority.LoadOperationAsync(crashDeleteLocalPlan.OperationId!)).Status ==
                SyncAuthorityOperationStatus.EffectIssuing,
            "The simulated post-retention local-delete crash did not retain its request-sent boundary.");
        var crashDeleteLocalReconciled = await new SyncAuthorityDeleteLocalReconciler(boundPair, authority)
            .ReconcileAsync(crashDeleteLocalPlan.OperationId!, "worker-delete-local-reconcile");
        Require(crashDeleteLocalReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !crashDeleteLocalReconciled.EffectDispatched &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM tombstone WHERE tombstone_id='{crashDeleteLocalPlan.TombstoneId}';"),
                    "resolved", StringComparison.Ordinal),
            "The interrupted local deletion was not completed from its retained predecessor without another effect.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "move-local-old.txt"), "stable move identity");
        await coordinator.ObserveAsync("monitor");
        var moveSeedPlan = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-local-old.txt");
        await new SyncAuthorityCreateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(moveSeedPlan.OperationId!, "worker-move-seed");
        var preRevisionMovePoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id))
            .Single(item => string.Equals(item.LocalRelativePath, "move-local-old.txt",
                StringComparison.OrdinalIgnoreCase) && string.Equals(item.DropboxRelativePath,
                "move-local-old.txt", StringComparison.OrdinalIgnoreCase));
        remote.Move("move-local-old.txt", "move-local-new.txt", churnRevision: true);
        await coordinator.ObserveAsync("monitor");
        var movePlanner = new SyncAuthorityReconciliationPlanner(authority,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", controlNamespaceId,
                controlNamespaceId, EnableDeletionEffects: true, EnableMoveEffects: true), admission: admission);
        var moveLocalPlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-local-new.txt");
        var moveLocalOperation = await authority.LoadOperationAsync(moveLocalPlan.OperationId!);
        Require(moveLocalPlan.Kind == SyncAuthorityPlannerOutcomeKind.Move &&
                moveLocalOperation.Kind == SyncAuthorityOperationKind.MoveLocal &&
                moveLocalOperation.Members.Any(item => item.Role == "lock" &&
                    string.Equals(item.RootBindingId, "root-observe-local", StringComparison.Ordinal) &&
                    string.Equals(item.ExpectedPathKey, "move-local-new.txt", StringComparison.OrdinalIgnoreCase)) &&
                moveLocalOperation.Allocations.Count == 0,
            "A stable Dropbox file ID rename did not correlate into one exact local move operation with an atomic desired-path reservation.");
        var movedLocal = await new SyncAuthorityMoveLocalExecutor(boundPair, remote, authority)
            .ExecuteAsync(moveLocalPlan.OperationId!, "worker-move-local");
        var postRevisionMovePoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id))
            .Single(item => string.Equals(item.LocalRelativePath, "move-local-new.txt",
                StringComparison.OrdinalIgnoreCase) && string.Equals(item.DropboxRelativePath,
                "move-local-new.txt", StringComparison.OrdinalIgnoreCase));
        var observedPostMoveDropboxVersion = moveLocalOperation.Members.Single(item => item.Role == "source")
            .ObjectVersionId;
        Require(movedLocal.Status == SyncAuthorityOperationStatus.Completed &&
                !File.Exists(Path.Combine(localRoot, "move-local-old.txt")) &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "move-local-new.txt")) ==
                "stable move identity" &&
                !string.Equals(postRevisionMovePoint.DropboxObjectVersionId,
                    preRevisionMovePoint.DropboxObjectVersionId, StringComparison.Ordinal) &&
                string.Equals(postRevisionMovePoint.DropboxObjectVersionId, observedPostMoveDropboxVersion,
                    StringComparison.Ordinal),
            "The exact NTFS identity did not move into the renamed Dropbox membership with the new provider revision retained atomically.");

        remote.Move("move-local-new.txt", "move-local-final.txt", churnRevision: true);
        await coordinator.ObserveAsync("monitor");
        var crashMoveLocalPlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-local-final.txt");
        SyncAuthorityMoveLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-moved-before-receipt") throw new IOException("simulated local-move crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityMoveLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(crashMoveLocalPlan.OperationId!, "worker-move-local-crash"));
        }
        finally { SyncAuthorityMoveLocalExecutor.PhaseForTest = null; }
        var crashMoveReconciled = await new SyncAuthorityMoveLocalReconciler(boundPair, remote, authority)
            .ReconcileAsync(crashMoveLocalPlan.OperationId!, "worker-move-local-reconcile");
        Require(crashMoveReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !crashMoveReconciled.EffectDispatched &&
                !File.Exists(Path.Combine(localRoot, "move-local-new.txt")) &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "move-local-final.txt")) ==
                "stable move identity",
            "The interrupted stable-identity local move was not reconciled without a second rename.");

        await File.WriteAllTextAsync(Path.Combine(localRoot, "move-remote-old.txt"), "remote move graph");
        await coordinator.ObserveAsync("monitor");
        var remoteMoveSeed = (await boundPlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-remote-old.txt");
        await new SyncAuthorityCreateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(remoteMoveSeed.OperationId!, "worker-remote-move-seed");
        File.Move(Path.Combine(localRoot, "move-remote-old.txt"),
            Path.Combine(localRoot, "move-remote-new.txt"));
        await coordinator.ObserveAsync("monitor");
        var moveRemotePlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-remote-new.txt");
        var moveRemoteOperation = await authority.LoadOperationAsync(moveRemotePlan.OperationId!);
        Require(moveRemotePlan.Kind == SyncAuthorityPlannerOutcomeKind.Move &&
                moveRemoteOperation.Kind == SyncAuthorityOperationKind.MoveRemote &&
                moveRemoteOperation.Members.Any(item => item.Role == "lock" &&
                    string.Equals(item.RootBindingId, "root-observe-dropbox", StringComparison.Ordinal) &&
                    string.Equals(item.ExpectedPathKey, "move-remote-new.txt", StringComparison.OrdinalIgnoreCase)) &&
                moveRemoteOperation.Allocations.Count == 0,
            "A stable NTFS identity rename did not correlate into one identity-preserving Dropbox move with an atomic desired-path reservation.");
        var movedRemote = await new SyncAuthorityMoveRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(moveRemotePlan.OperationId!, "worker-move-remote");
        moveRemoteOperation = await authority.LoadOperationAsync(moveRemotePlan.OperationId!);
        Require(movedRemote.Status == SyncAuthorityOperationStatus.Completed &&
                !remote.Exists("move-remote-old.txt") && remote.ReadText("move-remote-new.txt") ==
                "remote move graph" && moveRemoteOperation.Attempts.Count == 1 &&
                moveRemoteOperation.Attempts.All(item => item.Status ==
                    SyncAuthorityAttemptStatus.VerifiedSuccess),
            "The remote move did not preserve the Dropbox object identity in one fenced native effect.");

        File.Move(Path.Combine(localRoot, "move-remote-new.txt"),
            Path.Combine(localRoot, "move-remote-final.txt"));
        await coordinator.ObserveAsync("monitor");
        var unknownUploadMovePlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-remote-final.txt");
        remote.InterruptNextMoveAfterEffect = true;
        var unknownUploadMove = await new SyncAuthorityMoveRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(unknownUploadMovePlan.OperationId!, "worker-move-remote-upload-unknown");
        Require(unknownUploadMove.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                !remote.Exists("move-remote-new.txt") && remote.Exists("move-remote-final.txt"),
            "A lost native-move response did not leave the stable identity at its destination.");
        var uploadMoveReconciled = await new SyncAuthorityMoveRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(unknownUploadMovePlan.OperationId!, "worker-move-remote-upload-reconcile");
        Require(uploadMoveReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !uploadMoveReconciled.EffectDispatched &&
                !remote.Exists("move-remote-new.txt") && remote.Exists("move-remote-final.txt"),
            "The exact native-move destination was not committed by stable-identity reconciliation.");

        File.Move(Path.Combine(localRoot, "move-remote-final.txt"),
            Path.Combine(localRoot, "move-remote-ultimate.txt"));
        await coordinator.ObserveAsync("monitor");
        var unknownDeleteMovePlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "move-remote-ultimate.txt");
        remote.InterruptNextMoveBeforeEffect = true;
        var unknownDeleteMove = await new SyncAuthorityMoveRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(unknownDeleteMovePlan.OperationId!, "worker-move-remote-delete-unknown");
        Require(unknownDeleteMove.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                remote.Exists("move-remote-final.txt") && !remote.Exists("move-remote-ultimate.txt"),
            "A lost pre-effect native-move response changed the Dropbox namespace.");
        var deleteMoveReconciled = await new SyncAuthorityMoveRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(unknownDeleteMovePlan.OperationId!, "worker-move-remote-delete-reconcile");
        Require(deleteMoveReconciled.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                !deleteMoveReconciled.EffectDispatched &&
                deleteMoveReconciled.Reason.Contains("unresolved", StringComparison.Ordinal) &&
                remote.Exists("move-remote-final.txt") && !remote.Exists("move-remote-ultimate.txt"),
            "A sent Dropbox move with no durable receipt was not left explicitly unresolved: " +
            deleteMoveReconciled.Status + " / " + deleteMoveReconciled.Reason);
        var movesBeforeRefusedRetry = remote.MoveCount;
        var refusedRetry = false;
        try
        {
            await new SyncAuthorityMoveRemoteExecutor(boundPair, remote, authority)
                .ExecuteAsync(unknownDeleteMovePlan.OperationId!, "worker-move-remote-retry");
        }
        catch (InvalidOperationException)
        {
            refusedRetry = true;
        }
        Require(refusedRetry && remote.MoveCount == movesBeforeRefusedRetry &&
                remote.Exists("move-remote-final.txt") && !remote.Exists("move-remote-ultimate.txt"),
            "The unresolved sent request did not refuse redispatch at zero provider move cost.");

        // Controlled provider input: the already-sent ambiguous request becomes visible later.
        // The executor above remained forbidden to retry; the real reconciler alone accepts readback.
        remote.Move("move-remote-final.txt", "move-remote-ultimate.txt");
        var afterLateProviderMove = remote.MoveCount;
        var delayedMoveReconciled = await new SyncAuthorityMoveRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(unknownDeleteMovePlan.OperationId!, "worker-move-remote-delayed-reconcile");
        Require(delayedMoveReconciled.Status == SyncAuthorityOperationStatus.Completed &&
                !delayedMoveReconciled.EffectDispatched && remote.MoveCount == afterLateProviderMove &&
                !remote.Exists("move-remote-final.txt") && remote.Exists("move-remote-ultimate.txt"),
            "A later exact provider completion was not reconciled without redispatch.");

        remote.Move("move-remote-ultimate.txt", "Move-Remote-Ultimate.txt");
        await coordinator.ObserveAsync("monitor");
        var caseLocalPlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => string.Equals(item.RelativePath, "Move-Remote-Ultimate.txt",
                StringComparison.Ordinal));
        Require(caseLocalPlan.Kind == SyncAuthorityPlannerOutcomeKind.Move &&
                (await authority.LoadOperationAsync(caseLocalPlan.OperationId!)).Kind ==
                SyncAuthorityOperationKind.MoveLocal,
            "A Dropbox case-only rename was not retained as an identity-preserving local move.");
        var caseLocalResult = await new SyncAuthorityMoveLocalExecutor(boundPair, remote, authority)
            .ExecuteAsync(caseLocalPlan.OperationId!, "worker-case-only-local");
        using (var exactCaseRoot = PilotDirectoryLease.Open(localRoot,
                   new PilotDirectoryIdentity(boundPair.LocalVolumeSerialNumber, boundPair.LocalRootFileId)))
        {
            var exactCaseScan = await new PilotLocalScanner().ScanAsync(exactCaseRoot);
            Require(caseLocalResult.Status == SyncAuthorityOperationStatus.Completed &&
                    exactCaseScan.Entries.Values.Any(item => string.Equals(item.RelativePath,
                        "Move-Remote-Ultimate.txt", StringComparison.Ordinal)),
                "The local NTFS membership did not adopt the exact Dropbox case-only spelling.");
        }

        var caseLocalPath = Path.Combine(localRoot, "Move-Remote-Ultimate.txt");
        var temporaryCasePath = Path.Combine(localRoot, "case-only-hop.tmp");
        var finalCasePath = Path.Combine(localRoot, "MOVE-REMOTE-ULTIMATE.txt");
        File.Move(caseLocalPath, temporaryCasePath);
        File.Move(temporaryCasePath, finalCasePath);
        await coordinator.ObserveAsync("monitor");
        var caseRemotePlan = (await movePlanner.PlanAsync(pair.Id))
            .Single(item => string.Equals(item.RelativePath, "MOVE-REMOTE-ULTIMATE.txt",
                StringComparison.Ordinal));
        var caseRemoteResult = await new SyncAuthorityMoveRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(caseRemotePlan.OperationId!, "worker-case-only-remote");
        var caseRemoteEntries = await remote.ListEntriesAsync();
        Require(caseRemoteResult.Status == SyncAuthorityOperationStatus.Completed &&
                caseRemoteEntries.Any(item => string.Equals(item.RelativePath,
                    "MOVE-REMOTE-ULTIMATE.txt", StringComparison.Ordinal)) && remote.MoveCount >= 3,
                // SS-D18: the ambiguous pre-effect interruption no longer opens a retry, so the cumulative
                // provider-move bound drops by that one removed retry (4 -> 3).
            "A local case-only rename did not preserve the Dropbox object at the exact requested spelling.");

        await AssertCrossPairMoveAsync(authority, testRoot);
    }

    private static async Task AssertNamespaceCorrectnessAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "namespace-correctness");
        Directory.CreateDirectory(root);
        var composed = "caf\u00e9.txt";
        var decomposed = "cafe\u0301.txt";
        await File.WriteAllTextAsync(Path.Combine(root, composed), "composed");
        await File.WriteAllTextAsync(Path.Combine(root, decomposed), "decomposed");
        using var lease = PilotDirectoryLease.Open(root);
        var scan = await new PilotLocalScanner().ScanAsync(lease);
        Require(scan.Entries.Values.All(item =>
                    !string.Equals(PilotPath.CanonicalComparisonKey(item.RelativePath),
                        PilotPath.CanonicalComparisonKey(composed), StringComparison.Ordinal)) &&
                scan.Decisions.Count(item => item.Kind == PilotDecisionKind.Blocked &&
                    item.Reason.Contains("Unicode-normalization", StringComparison.Ordinal)) == 2,
            "Unicode-normalization-colliding NTFS names were not quarantined together without content authority.");
        foreach (var unsupported in new[] { "CON.txt", "aux", "folder./file.txt", "trailing-space /x" })
        {
            try
            {
                _ = PilotPath.NormalizeRelative(unsupported);
                throw new InvalidOperationException("An unsupported Windows membership name was accepted.");
            }
            catch (ArgumentException) { }
        }
    }

    private static async Task AssertUploadSessionCheckpointAuthorityAsync(
        SyncAuthorityDatabase authority, SyncAuthorityCommandAdmission admission)
    {
        const string operationId = "operation-upload-session-checkpoint";
        const string allocationId = "allocation-upload-session-checkpoint";
        const string versionId = "version-upload-session-checkpoint";
        const long size = 200L * 1024 * 1024;
        await authority.RegisterObjectVersionAsync(versionId, "object-a", "local",
            "{\"volumeSerial\":\"a1b2c3d4\",\"fileId\":\"0000000000001234\",\"lastWriteUtc\":\"2026-09-04T00:00:00.0000000Z\"}",
            HashA, size);
        await authority.CreateOperationAsync(Plan(operationId,
            allocations:
            [
                new SyncAuthorityAllocation(allocationId, "control-dropbox",
                    SyncAuthorityAllocationKind.UploadSession,
                    "upload-session-operation", "root-dropbox", HashB)
            ], expectedPath: "upload-session-checkpoint.bin"), admission: admission);
        var lease = await authority.AcquireOperationLeaseAsync(operationId,
            "worker-upload-session", TimeSpan.FromMinutes(5));
        await authority.TransitionAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Creating);
        await authority.SaveUploadSessionCheckpointAsync(allocationId, lease, "opaque-session-a", 8,
            versionId, size, HashA);

        var restarted = new SyncAuthorityDatabase(authority.DatabasePath);
        var retained = await restarted.LoadUploadSessionCheckpointAsync(allocationId);
        Require(retained is not null && retained.OperationId == operationId &&
                retained.SessionId == "opaque-session-a" && retained.ConfirmedOffset == 8 &&
                retained.SourceObjectVersionId == versionId && retained.SourceSize == size &&
                retained.SourceContentHash == HashA && retained.Status == "active",
            "A process restart did not retain the exact upload-session checkpoint.");
        await restarted.SaveUploadSessionCheckpointAsync(allocationId, lease, "opaque-session-a", 16,
            versionId, size, HashA);
        await ExpectRejectAsync(() => restarted.SaveUploadSessionCheckpointAsync(allocationId, lease,
            "opaque-session-tampered", 24, versionId, size, HashB));
        await restarted.TransitionAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Created, "upload-session-operation", HashA, size);
        await restarted.TransitionAllocationAsync(allocationId, lease,
            SyncAuthorityAllocationStatus.Bound, "upload-session-operation");
        await restarted.CompleteUploadSessionCheckpointAsync(allocationId, lease,
            "opaque-session-a", size);
        retained = await restarted.LoadUploadSessionCheckpointAsync(allocationId);
        Require(retained is not null && retained.Status == "completed" &&
                retained.ConfirmedOffset == size,
            "The exact upload-session completion was not durably retained.");
        await restarted.ReleaseLeaseAsync(lease);
        await ExpectRejectAsync(() => restarted.SaveUploadSessionCheckpointAsync(allocationId, lease,
            "opaque-session-after-release", size, versionId, size, HashA));
    }

    internal static async Task AssertDropboxRevisionChurnMoveGuardsAsync(string testRoot,
        IReadOnlySet<string>? caseFilter = null, bool verifyRetryIdentities = false)
    {
        const long baselineSize = 64;
        const string oldPath = "remote-old.txt";
        const string newPath = "remote-new.txt";
        var cases = new (string Name, string? RemoteHash, long? RemoteSize, string LocalHash,
            bool StableIdMatches, bool DuplicateRemoteIdentity, bool OccupyLocalTarget,
            bool OccupyRemoteSource, bool ReserveTarget, string? ExpectedReason)[]
        {
            ("content-change", HashB, baselineSize, HashA, true, false, false, false, false,
                "moved with changed content or size"),
            ("size-change", HashA, baselineSize + 1, HashA, true, false, false, false, false,
                "moved with changed content or size"),
            ("missing-hash", null, baselineSize, HashA, true, false, false, false, false,
                "lacks complete content hash or size evidence"),
            ("missing-size", HashA, null, HashA, true, false, false, false, false,
                "lacks complete content hash or size evidence"),
            ("local-divergence", HashA, baselineSize, HashB, true, false, false, false, false,
                "local predecessor diverged"),
            ("duplicate-stable-id", HashA, baselineSize, HashA, true, true, false, false, false,
                "multiple or ambiguous paths"),
            ("occupied-local-target", HashA, baselineSize, HashA, true, false, true, false, false,
                "move destination is occupied"),
            ("occupied-remote-source", HashA, baselineSize, HashA, true, false, false, true, false,
                "baseline Dropbox source path is still occupied"),
            ("unidentified-remote-source", HashA, baselineSize, HashA, true, false, false, true, false,
                "baseline Dropbox source path is still occupied"),
            ("reserved-local-target", HashA, baselineSize, HashA, true, false, false, false, true,
                "prior nonterminal operation already reserves"),
            ("stable-id-mismatch", HashA, baselineSize, HashA, false, false, false, false, false, null),
            ("reviewed-copy-content-change", HashB, baselineSize, HashA, true, false, false, false, false, null),
            ("reviewed-copy-local-divergence", HashA, baselineSize, HashB, true, false, false, false, false, null),
            ("reviewed-copy-metadata-change", HashA, baselineSize, HashA, true, false, false, false, false, null),
            ("reviewed-copy-reserved-target", HashA, baselineSize, HashA, true, false, false, false, true,
                "prior nonterminal operation already reserves")
        };

        foreach (var scenario in cases)
        {
            if (caseFilter is not null && !caseFilter.Contains(scenario.Name)) continue;
            Console.WriteLine($"CHECK Dropbox move guard: {scenario.Name}");
            var databasePath = Path.Combine(testRoot, $"dropbox-move-guard-{scenario.Name}.db");
            var authority = new SyncAuthorityDatabase(databasePath);
            var pairId = "pair-dropbox-move-guard-" + scenario.Name;
            var profileId = "profile-dropbox-move-guard-" + scenario.Name;
            var localRootId = "root-local-dropbox-move-guard-" + scenario.Name;
            var dropboxRootId = "root-dropbox-move-guard-" + scenario.Name;
            var objectId = "object-dropbox-move-guard-" + scenario.Name;
            var localRoot = Path.Combine(testRoot, "dropbox-move-guard-" + scenario.Name);
            Directory.CreateDirectory(localRoot);

            await authority.InitializeAsync("installation-dropbox-move-guard", profileId,
                "dropbox-move-guard", "{}");
            await authority.RegisterPairAsync(pairId, 1);
            var admission = await AdmitFixturePairsAsync(authority, pairId);
            await authority.RegisterRootBindingAsync(localRootId, pairId, "local", 1,
                "{\"volumeSerial\":\"00000001\",\"fileId\":\"root-local\"}", localRoot);
            await authority.RegisterRootBindingAsync(dropboxRootId, pairId, "dropbox", 1,
                "{\"folderId\":\"folder-dropbox-move-guard\"}", "/Dropbox Move Guard");
            await authority.RegisterObjectAsync(objectId, "file");

            var baselineLocalVersionId = "version-local-baseline-" + scenario.Name;
            var baselineDropboxVersionId = "version-dropbox-baseline-" + scenario.Name;
            var baselineLocalMembershipId = "membership-local-baseline-" + scenario.Name;
            var baselineDropboxMembershipId = "membership-dropbox-baseline-" + scenario.Name;
            await authority.RegisterObjectVersionAsync(baselineLocalVersionId, objectId, "local",
                "{\"volumeSerial\":\"00000001\",\"fileId\":\"local-stable\",\"lastWriteUtc\":\"2026-09-04T00:00:00.0000000Z\"}",
                HashA, baselineSize);
            await authority.RegisterObjectVersionAsync(baselineDropboxVersionId, objectId, "dropbox",
                "{\"fileId\":\"dropbox-stable\",\"revision\":\"rev:1\"}", HashA, baselineSize);
            await authority.RegisterMembershipAsync(baselineLocalMembershipId, objectId, localRootId,
                oldPath, 1);
            await authority.RegisterMembershipAsync(baselineDropboxMembershipId, objectId, dropboxRootId,
                oldPath, 1);
            await authority.AcceptSynchronizationPointAsync("syncpoint-baseline-" + scenario.Name, pairId,
                objectId, baselineLocalVersionId, baselineDropboxVersionId, baselineLocalMembershipId,
                baselineDropboxMembershipId);

            var currentLocalVersionId = baselineLocalVersionId;
            if (!string.Equals(scenario.LocalHash, HashA, StringComparison.Ordinal) ||
                scenario.Name == "reviewed-copy-metadata-change")
            {
                currentLocalVersionId = "version-local-current-" + scenario.Name;
                await authority.RegisterObjectVersionAsync(currentLocalVersionId, objectId, "local",
                    "{\"volumeSerial\":\"00000001\",\"fileId\":\"local-stable\",\"lastWriteUtc\":\"2026-09-04T00:01:00.0000000Z\"}",
                    scenario.LocalHash, baselineSize);
            }
            var currentLocalMembershipId = "membership-local-current-" + scenario.Name;
            await authority.RegisterMembershipAsync(currentLocalMembershipId, objectId, localRootId,
                oldPath, 2);

            var currentDropboxObjectId = scenario.StableIdMatches
                ? objectId
                : "object-dropbox-move-target-" + scenario.Name;
            if (!scenario.StableIdMatches)
                await authority.RegisterObjectAsync(currentDropboxObjectId, "file");
            var currentDropboxVersionId = "version-dropbox-current-" + scenario.Name;
            var currentDropboxMembershipId = "membership-dropbox-current-" + scenario.Name;
            await authority.RegisterObjectVersionAsync(currentDropboxVersionId, currentDropboxObjectId, "dropbox",
                scenario.StableIdMatches
                    ? "{\"fileId\":\"dropbox-stable\",\"revision\":\"rev:2\"}"
                    : "{\"fileId\":\"dropbox-other\",\"revision\":\"rev:1\"}",
                scenario.RemoteHash, scenario.RemoteSize);
            await authority.RegisterMembershipAsync(currentDropboxMembershipId, currentDropboxObjectId,
                dropboxRootId, newPath, 2);

            var localScan = await authority.BeginScanGenerationAsync(pairId, localRootId, "local", "monitor",
                "{\"scope\":\"complete\"}");
            await authority.AppendObservationAsync(localScan, "observation-local-current-" + scenario.Name,
                currentLocalVersionId, currentLocalMembershipId, "{}");
            if (scenario.OccupyLocalTarget)
            {
                var occupiedObjectId = "object-local-target-" + scenario.Name;
                var occupiedVersionId = "version-local-target-" + scenario.Name;
                var occupiedMembershipId = "membership-local-target-" + scenario.Name;
                await authority.RegisterObjectAsync(occupiedObjectId, "file");
                await authority.RegisterObjectVersionAsync(occupiedVersionId, occupiedObjectId, "local",
                    "{\"volumeSerial\":\"00000001\",\"fileId\":\"local-target-other\",\"lastWriteUtc\":\"2026-09-04T00:00:00.0000000Z\"}",
                    HashB, baselineSize);
                await authority.RegisterMembershipAsync(occupiedMembershipId, occupiedObjectId, localRootId,
                    newPath, 1);
                await authority.AppendObservationAsync(localScan,
                    "observation-local-target-" + scenario.Name, occupiedVersionId, occupiedMembershipId, "{}");
            }
            await authority.CompleteScanGenerationAsync(localScan);

            var dropboxScan = await authority.BeginScanGenerationAsync(pairId, dropboxRootId, "dropbox",
                "monitor", "{\"scope\":\"complete\"}");
            await authority.AppendObservationAsync(dropboxScan,
                "observation-dropbox-current-" + scenario.Name, currentDropboxVersionId,
                currentDropboxMembershipId, "{}");
            if (scenario.DuplicateRemoteIdentity)
            {
                var duplicateMembershipId = "membership-dropbox-duplicate-" + scenario.Name;
                await authority.RegisterMembershipAsync(duplicateMembershipId, objectId, dropboxRootId,
                    oldPath, 2);
                await authority.AppendObservationAsync(dropboxScan,
                    "observation-dropbox-duplicate-" + scenario.Name, baselineDropboxVersionId,
                    duplicateMembershipId, "{}");
            }
            if (scenario.OccupyRemoteSource)
            {
                var occupiedObjectId = "object-dropbox-source-" + scenario.Name;
                var occupiedVersionId = "version-dropbox-source-" + scenario.Name;
                var occupiedMembershipId = "membership-dropbox-source-" + scenario.Name;
                await authority.RegisterObjectAsync(occupiedObjectId, "file");
                await authority.RegisterObjectVersionAsync(occupiedVersionId, occupiedObjectId, "dropbox",
                    scenario.Name == "unidentified-remote-source" ? "{\"revision\":\"rev:1\"}" :
                    "{\"fileId\":\"dropbox-source-other\",\"revision\":\"rev:1\"}", HashB,
                    baselineSize);
                await authority.RegisterMembershipAsync(occupiedMembershipId, occupiedObjectId, dropboxRootId,
                    oldPath, 2);
                await authority.AppendObservationAsync(dropboxScan,
                    "observation-dropbox-source-" + scenario.Name, occupiedVersionId,
                    occupiedMembershipId, "{}");
            }
            if (scenario.DuplicateRemoteIdentity)
            {
                // This invalid inventory is rejected at publication, before the planner can
                // inspect it. Keep that stronger guard; do not publish a fake valid snapshot.
                var publicationRejected = false;
                try { await authority.CompleteScanGenerationAsync(dropboxScan); }
                catch (InvalidDataException exception) when (exception.Message.Contains(
                           "conflicting file identity", StringComparison.Ordinal))
                {
                    publicationRejected = true;
                }
                Require(publicationRejected &&
                        await ScalarLongAsync(databasePath,
                            "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$scan AND status='incomplete';",
                            ("$scan", dropboxScan.ScanGenerationId)) == 1 &&
                        await ScalarLongAsync(databasePath,
                            "SELECT count(*) FROM observation WHERE scan_generation_id=$scan AND complete_generation<>0;",
                            ("$scan", dropboxScan.ScanGenerationId)) == 0,
                    "Duplicate remote identity must remain unpublished, not become planning authority.");
                var rejectedPlanner = new SyncAuthorityReconciliationPlanner(authority,
                    new SyncAuthorityPlannerOptions(1, profileId, "control-local-" + scenario.Name,
                        "control-dropbox-" + scenario.Name, EnableDeletionEffects: true, EnableMoveEffects: true), admission: admission);
                var planningRejected = false;
                try { await rejectedPlanner.PlanAsync(pairId); }
                catch (InvalidOperationException exception) when (exception.Message.Contains(
                           "no complete observation", StringComparison.Ordinal))
                {
                    planningRejected = true;
                }
                Require(planningRejected && await ScalarLongAsync(databasePath,
                        "SELECT count(*) FROM operation WHERE pair_id=$pair;", ("$pair", pairId)) == 0,
                    "An unpublished duplicate-identity inventory must not create any operation.");
                Console.WriteLine("PASS duplicate remote identity is rejected before publication and planning with no operation");
                continue;
            }
            await authority.CompleteScanGenerationAsync(dropboxScan);

            if (scenario.ReserveTarget)
            {
                var reservationObjectId = "object-reservation-" + scenario.Name;
                await authority.RegisterObjectAsync(reservationObjectId, "file");
                await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(
                    "operation-reservation-" + scenario.Name,
                    "idempotency-reservation-" + scenario.Name,
                    SyncAuthorityOperationKind.Reconcile,
                    pairId,
                    reservationObjectId,
                    1,
                    profileId,
                    "{}",
                    100,
                    [new SyncAuthorityMember("member-reservation-" + scenario.Name, pairId,
                        reservationObjectId, "lock", localRootId, null, null, null, newPath,
                        "{\"present\":false}")],
                    [],
                    []), admission: admission);
            }

            var reviewedCopy = scenario.Name.StartsWith("reviewed-copy-", StringComparison.Ordinal);
            var planner = new SyncAuthorityReconciliationPlanner(authority,
                new SyncAuthorityPlannerOptions(1, profileId, "control-local-" + scenario.Name,
                    "control-dropbox-" + scenario.Name, EnableDeletionEffects: true, EnableMoveEffects: true),
                effectPathFilter: reviewedCopy ? new HashSet<string>([newPath], StringComparer.OrdinalIgnoreCase) : null,
                expectedOperationKinds: reviewedCopy ? new Dictionary<string, SyncAuthorityOperationKind>
                    { [newPath] = SyncAuthorityOperationKind.CreateLocal } : null, admission: admission);
            var outcomes = await planner.PlanAsync(pairId);
            if (reviewedCopy)
            {
                Require(outcomes.Count == 1 && outcomes[0].RelativePath == newPath &&
                        await ScalarLongAsync(databasePath,
                            "SELECT count(*) FROM operation WHERE pair_id=$pair AND kind IN ('move_local','move_remote','delete_local','delete_remote');",
                            ("$pair", pairId)) == 0,
                    "Reviewed copy retains its exact new-path scope and never implies predecessor move/deletion.");
                if (scenario.ReserveTarget)
                    Require(outcomes[0].Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                            outcomes[0].Reason.Contains(scenario.ExpectedReason!, StringComparison.OrdinalIgnoreCase),
                        "Reviewed copy still refuses a competing destination reservation.");
                else
                    Require(outcomes[0].Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                            (await authority.LoadOperationAsync(outcomes[0].OperationId!)).Kind == SyncAuthorityOperationKind.CreateLocal,
                        "Reviewed copy remains a CreateLocal plan despite moved cloud identity or changed predecessor content.");
                Console.WriteLine("PASS " + scenario.Name + ": exact reviewed copy decision and no incidental move/deletion");
                continue;
            }
            if (scenario.Name == "occupied-remote-source")
            {
                Require(outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move &&
                            item.RelativePath == newPath) == 1 &&
                        (await authority.LoadOperationAsync(outcomes.Single(item =>
                            item.Kind == SyncAuthorityPlannerOutcomeKind.Move).OperationId!)).Kind ==
                            SyncAuthorityOperationKind.MoveLocal,
                    "A proven distinct replacement at the old remote name must not block the unchanged stable original's move.");
                if (verifyRetryIdentities)
                {
                    var oldMove = outcomes.Single(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move).OperationId!;
                    await authority.EnsureReviewedRunCanStartAsync(pairId, CancellationToken.None);
                    var group = "fixture-move-identity-group";
                    await authority.RegisterOperationGroupAsync(group, "compound", "{}");
                    var groupedOptions = new SyncAuthorityPlannerOptions(1, profileId, "control-local-" + scenario.Name,
                        "control-dropbox-" + scenario.Name, EnableDeletionEffects: true, EnableMoveEffects: true);
                    var groupedMove = (await new SyncAuthorityReconciliationPlanner(authority, groupedOptions,
                        operationGroupId: group, admission: admission).PlanAsync(pairId))
                        .Single(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move).OperationId!;
                    Require(groupedMove != oldMove,
                        "A new group produces a distinct real move plan without overwriting old authority.");
                    var exactReplay = (await new SyncAuthorityReconciliationPlanner(authority, groupedOptions,
                        operationGroupId: group, admission: admission).PlanAsync(pairId))
                        .Single(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move).OperationId!;
                    Require(exactReplay == groupedMove, "The same profile/group real move plan is exactly idempotent.");
                    var nextProfile = profileId + "-next";
                    await authority.InitializeAsync("installation-dropbox-move-guard", nextProfile,
                        "move-identity-next-source", "{}");
                    await authority.EnsureReviewedRunCanStartAsync(pairId, CancellationToken.None);
                    var nextMove = (await new SyncAuthorityReconciliationPlanner(authority,
                        groupedOptions with { CapabilityProfileId = nextProfile }, operationGroupId: group, admission: admission).PlanAsync(pairId))
                        .Single(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move).OperationId!;
                    Require(nextMove != groupedMove && (await authority.LoadOperationAsync(groupedMove)).Attempts.Count == 0,
                        "A changed profile produces a distinct real move identity within the same group, with no old effect replay.");
                }
                continue;
            }
            Require(outcomes.All(item => item.Kind != SyncAuthorityPlannerOutcomeKind.Move) &&
                    await ScalarLongAsync(databasePath,
                        $"SELECT count(*) FROM operation WHERE pair_id='{pairId}' AND kind IN ('move_local','move_remote');") == 0,
                $"The {scenario.Name} guard inferred a move without complete bounded authority.");
            if (scenario.ExpectedReason is not null)
            {
                Require(outcomes.Any(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                                            item.RelativePath == newPath &&
                                            item.Reason.Contains(scenario.ExpectedReason,
                                                StringComparison.OrdinalIgnoreCase)),
                    $"The {scenario.Name} guard did not retain its exact fail-closed reason.");
            }
            else
            {
                var retiredPrior = outcomes.Single(item => item.RelativePath == oldPath);
                var distinctArrival = outcomes.Single(item => item.RelativePath == newPath);
                Require(retiredPrior.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                        distinctArrival.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                        (await authority.LoadOperationAsync(retiredPrior.OperationId!)).Kind ==
                        SyncAuthorityOperationKind.DeleteLocal &&
                        (await authority.LoadOperationAsync(distinctArrival.OperationId!)).Kind ==
                        SyncAuthorityOperationKind.CreateLocal,
                    "A different Dropbox file ID was incorrectly correlated as the prior logical object instead of one prompt retirement plus one distinct arrival.");
            }
        }
    }

    private static async Task AssertUncertainInitialDiscoveryAsync(SyncAuthorityDatabase authority,
        string testRoot)
    {
        const string pairId = "pair-uncertain-initial";
        const string localRootId = "root-uncertain-initial-local";
        const string dropboxRootId = "root-uncertain-initial-dropbox";
        const string objectId = "object-uncertain-initial";
        const string versionId = "version-uncertain-initial-dropbox";
        const string membershipId = "membership-uncertain-initial-dropbox";
        const string path = "unreadable-initial.txt";
        const string uncertainty = "{\"path\":\"unreadable-initial.txt\",\"state\":\"unavailable\",\"reason\":\"fixture unreadable\"}";
        const string remoteEvidence = "{\"path\":\"unreadable-initial.txt\",\"kind\":\"file\",\"identity\":{\"fileId\":\"id:unreadable-initial\",\"revision\":\"rev:1\"},\"ContentHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Size\":1}";

        var localRoot = Path.Combine(testRoot, "uncertain-initial-root");
        Directory.CreateDirectory(localRoot);
        PilotDirectoryIdentity localIdentity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) localIdentity = lease.Identity;
        await authority.RegisterPairAsync(pairId, 1);
        await authority.RegisterRootBindingAsync(localRootId, pairId, "local", 1,
            JsonSerializer.Serialize(new { localIdentity.VolumeSerialNumber, localIdentity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync(dropboxRootId, pairId, "dropbox", 1,
            "{\"folderId\":\"folder-uncertain-initial\"}", "/Uncertain Initial");
        await authority.RegisterObjectAsync(objectId, "file");
        await authority.RegisterObjectVersionAsync(versionId, objectId, "dropbox",
            "{\"fileId\":\"id:unreadable-initial\",\"revision\":\"rev:1\"}", HashA, 1);
        await authority.RegisterMembershipAsync(membershipId, objectId, dropboxRootId, path, 1);

        for (var generation = 1; generation <= 2; generation++)
        {
            var localScan = await authority.BeginScanGenerationAsync(pairId, localRootId, "local", "initial",
                "{\"scope\":\"complete_with_uncertainty\"}");
            await authority.AppendObservationAsync(localScan, $"observation-uncertain-initial-local-{generation}",
                null, null, uncertainty);
            await authority.CompleteScanGenerationAsync(localScan);
            var dropboxScan = await authority.BeginScanGenerationAsync(pairId, dropboxRootId, "dropbox", "initial",
                "{\"scope\":\"complete\"}");
            await authority.AppendObservationAsync(dropboxScan,
                $"observation-uncertain-initial-dropbox-{generation}", versionId, membershipId, remoteEvidence);
            await authority.CompleteScanGenerationAsync(dropboxScan);
        }

        Require(await authority.HasTwoStableCompleteObservationsAsync(pairId, "local") &&
                await authority.HasTwoStableCompleteObservationsAsync(pairId, "dropbox"),
            "The initial uncertainty fixture did not establish two repeatable scan generations.");
        var decisions = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pairId);
        Require(decisions.Count == 1 && decisions.Single().Kind == SyncDecisionKind.Blocked &&
                decisions.Single().Reason.Contains("uncertainty is not absence", StringComparison.Ordinal) &&
                await ScalarLongAsync(authority.DatabasePath,
                    $"SELECT count(*) FROM synchronization_point WHERE pair_id='{pairId}';") == 0,
            "A stable-but-unreadable initial path was adopted as an absent local baseline.");
    }

    private static async Task AssertCrossPairMoveAsync(SyncAuthorityDatabase authority, string testRoot)
    {
        var sourceRoot = Path.Combine(testRoot, "cross-pair-source");
        var destinationRoot = Path.Combine(testRoot, "cross-pair-destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "cross-source.txt"), "cross-pair payload");
        PilotDirectoryIdentity sourceIdentity;
        PilotDirectoryIdentity destinationIdentity;
        using (var root = PilotDirectoryLease.Open(sourceRoot)) sourceIdentity = root.Identity;
        using (var root = PilotDirectoryLease.Open(destinationRoot)) destinationIdentity = root.Identity;
        var sourcePair = new SyncPair("pair-cross-source", "Cross source", sourceRoot,
            sourceIdentity.VolumeSerialNumber, sourceIdentity.FileId, "/CrossSource", "folder-cross-source")
        {
            RecoveryRoot = new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.ProductContainerPath + "/CrossRecovery", "folder-cross-recovery",
                "root-namespace-cross")
        };
        var destinationPair = new SyncPair("pair-cross-destination", "Cross destination", destinationRoot,
            destinationIdentity.VolumeSerialNumber, destinationIdentity.FileId, "/CrossDestination",
            "folder-cross-destination");
        await authority.RegisterPairAsync(sourcePair.Id, 1);
        await authority.RegisterPairAsync(destinationPair.Id, 1);
        var admission = await AdmitFixturePairsAsync(authority, sourcePair.Id, destinationPair.Id);
        await authority.RegisterRootBindingAsync("root-cross-source-local", sourcePair.Id, "local", 1,
            JsonSerializer.Serialize(new { sourceIdentity.VolumeSerialNumber, sourceIdentity.FileId }), sourceRoot);
        await authority.RegisterRootBindingAsync("root-cross-source-dropbox", sourcePair.Id, "dropbox", 1,
            "{\"folderId\":\"folder-cross-source\"}", sourcePair.DropboxRoot);
        await authority.RegisterRootBindingAsync("root-cross-destination-local", destinationPair.Id, "local", 1,
            JsonSerializer.Serialize(new { destinationIdentity.VolumeSerialNumber, destinationIdentity.FileId }),
            destinationRoot);
        await authority.RegisterRootBindingAsync("root-cross-destination-dropbox", destinationPair.Id, "dropbox", 1,
            "{\"folderId\":\"folder-cross-destination\"}", destinationPair.DropboxRoot);
        var sourceRemote = new FakeSyncRemoteStore();
        var destinationRemote = new FakeSyncRemoteStore();
        sourceRemote.Put("cross-source.txt", "cross-pair payload");
        var sourceObserver = new SyncAuthorityObservationCoordinator(sourcePair, sourceRemote, authority,
            "root-cross-source-local", "root-cross-source-dropbox");
        var destinationObserver = new SyncAuthorityObservationCoordinator(destinationPair, destinationRemote,
            authority, "root-cross-destination-local", "root-cross-destination-dropbox");
        await sourceObserver.ObserveAsync("initial");
        await sourceObserver.ObserveAsync("initial");
        await destinationObserver.ObserveAsync("initial");
        await destinationObserver.ObserveAsync("initial");
        var adopted = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(sourcePair.Id);
        Require(adopted.Count == 1 && adopted.Single().Kind == SyncDecisionKind.Baseline,
            "The cross-pair source did not establish one effect-free baseline.");

        File.Move(Path.Combine(sourceRoot, "cross-source.txt"),
            Path.Combine(destinationRoot, "cross-destination.txt"));
        await sourceObserver.ObserveAsync("monitor");
        await destinationObserver.ObserveAsync("monitor");
        var sourceOptions = new SyncAuthorityPlannerOptions(1, "profile-cloud-pro",
            "control-cross-source", "control-cross-source", EnableDeletionEffects: true,
            EnableMoveEffects: true);
        var destinationOptions = new SyncAuthorityPlannerOptions(1, "profile-cloud-pro",
            "control-cross-destination", "control-cross-destination", EnableDeletionEffects: true,
            EnableMoveEffects: true);
        var coordinator = new SyncAuthorityCrossPairMoveCoordinator(sourcePair, sourceRemote, sourceOptions,
            destinationPair, destinationRemote, destinationOptions, authority, admission: admission);
        var plan = (await coordinator.PlanAsync()).Single();
        var operation = await authority.LoadOperationAsync(plan.OperationId);
        Require(operation.Kind == SyncAuthorityOperationKind.CrossPairMove &&
                operation.Members.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() == 2 &&
                operation.Dependencies.Count == 0,
            "Cross-pair movement was not frozen as one durable two-pair operation.");
        var result = await coordinator.ExecuteAsync(plan, "worker-cross-pair");
        Require(result.Status == SyncAuthorityOperationStatus.Completed &&
                !sourceRemote.Exists("cross-source.txt") &&
                destinationRemote.ReadText("cross-destination.txt") == "cross-pair payload" &&
                sourceRemote.MoveCount == 1 && sourceRemote.UploadCount == 0 &&
                sourceRemote.DeleteCount == 0 && destinationRemote.UploadCount == 0 &&
                string.Equals(await ScalarStringAsync(authority.DatabasePath,
                    $"SELECT status FROM operation_group WHERE operation_group_id='{plan.OperationGroupId}';"),
                    "completed", StringComparison.Ordinal),
            "The cross-pair group did not preserve the provider object without upload/delete.");

        await AssertDropboxToLocalCrossPairMoveAsync(authority, testRoot, "remote-file",
            PilotEntryKind.File);
        await AssertDropboxToLocalCrossPairMoveAsync(authority, testRoot, "remote-folder",
            PilotEntryKind.Directory);
        await AssertDropboxToLocalCrossPairMoveAsync(authority, testRoot, "cross-volume-file",
            PilotEntryKind.File, forceCrossVolume: true);
        await AssertDropboxToLocalCrossPairMoveAsync(authority, testRoot, "cross-volume-folder",
            PilotEntryKind.Directory, forceCrossVolume: true);
        await AssertCrossVolumeCrashRecoveryAsync(authority, testRoot);
        await AssertLocalToDropboxFolderMoveAsync(authority, testRoot);
        await AssertCrossPairCollisionAsync(authority, testRoot);
        await AssertCrossPairLostResponseAsync(authority, testRoot, afterEffect: true);
        await AssertCrossPairLostResponseAsync(authority, testRoot, afterEffect: false);
    }

    private static async Task AssertAncestorUncertaintyFencesAsync(SyncAuthorityDatabase authority,
        string testRoot)
    {
        const string pairId = "pair-ancestor-fence";
        const string localRootId = "root-ancestor-fence-local";
        const string dropboxRootId = "root-ancestor-fence-dropbox";
        var localRoot = Path.Combine(testRoot, "ancestor-fence-root");
        Directory.CreateDirectory(Path.Combine(localRoot, "folder"));
        await File.WriteAllTextAsync(Path.Combine(localRoot, "folder", "child.txt"), "stable child");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair(pairId, "Ancestor fence", localRoot, identity.VolumeSerialNumber,
            identity.FileId, "/Ancestor Fence", "folder-ancestor-fence");
        await authority.RegisterPairAsync(pair.Id, 1);
        var admission = await AdmitFixturePairsAsync(authority, pair.Id);
        await authority.RegisterRootBindingAsync(localRootId, pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync(dropboxRootId, pair.Id, "dropbox", 1,
            "{\"folderId\":\"folder-ancestor-fence\"}", pair.DropboxRoot);
        var remote = new FakeSyncRemoteStore();
        remote.PutDirectory("folder");
        remote.Put("folder/child.txt", "stable child");
        var coordinator = new SyncAuthorityObservationCoordinator(pair, remote, authority,
            localRootId, dropboxRootId);
        await coordinator.ObserveAsync("initial");
        await coordinator.ObserveAsync("initial");
        var adopted = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        Require(adopted.Count == 2 && adopted.All(item => item.Kind == SyncDecisionKind.Baseline),
            "The ancestor-fence fixture did not establish its effect-free folder and child baselines.");

        var options = new SyncAuthorityPlannerOptions(1, "profile-ancestor-fence",
            "control-ancestor-local", "control-ancestor-dropbox", EnableDeletionEffects: true,
            DeletionMinimumCompleteObservations: 2, DeletionMinimumInterval: TimeSpan.Zero);
        foreach (var state in new[] { "blocked", "unavailable", "online_only", "reparse", "overflow", "incomplete" })
        {
            for (var gracePass = 1; gracePass <= 3; gracePass++)
            {
                var uncertainScan = await authority.BeginScanGenerationAsync(pair.Id, localRootId, "local",
                    "monitor", JsonSerializer.Serialize(new
                    {
                        scope = "complete_with_uncertainty",
                        state,
                        gracePass
                    }));
                await authority.AppendObservationAsync(uncertainScan,
                    $"observation-ancestor-{state}-{gracePass}", null, null,
                    JsonSerializer.Serialize(new
                    {
                        path = "folder",
                        state,
                        reason = $"fixture ancestor {state}"
                    }));
                await authority.CompleteScanGenerationAsync(uncertainScan);
                var planned = await new SyncAuthorityReconciliationPlanner(authority, options, admission: admission).PlanAsync(pair.Id);
                var child = planned.Single(item => item.RelativePath == "folder/child.txt");
                Require(child.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                        child.OperationId is null && child.TombstoneId is null &&
                        child.Reason.Contains("ancestor", StringComparison.OrdinalIgnoreCase),
                    $"A {state} ancestor allowed its nested file to become deletion authority on grace pass {gracePass}.");
            }

            var restarted = (await new SyncAuthorityReconciliationPlanner(
                    new SyncAuthorityDatabase(authority.DatabasePath), options, admission: admission).PlanAsync(pair.Id))
                .Single(item => item.RelativePath == "folder/child.txt");
            Require(restarted.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                    restarted.OperationId is null && restarted.TombstoneId is null,
                $"The {state} ancestor fence was lost after the deletion grace and authority restart.");
            Require(await ScalarLongAsync(authority.DatabasePath,
                        $"SELECT count(*) FROM operation WHERE pair_id='{pair.Id}' AND kind IN ('delete_local','delete_remote');") == 0 &&
                    await ScalarLongAsync(authority.DatabasePath,
                        $"SELECT count(*) FROM tombstone WHERE pair_id='{pair.Id}';") == 0,
                $"The {state} ancestor accumulated destructive authority across the deletion grace.");
        }

        var interruptedScan = await authority.BeginScanGenerationAsync(pair.Id, localRootId, "local",
            "monitor", "{\"scope\":\"incomplete_overflow_rescan\"}");
        await authority.AppendObservationAsync(interruptedScan, "observation-incomplete-newer-scan", null, null,
            "{\"path\":\"folder\",\"state\":\"overflow\",\"reason\":\"fixture rescan interrupted\"}");
        var whileIncomplete = (await new SyncAuthorityReconciliationPlanner(
                new SyncAuthorityDatabase(authority.DatabasePath), options, admission: admission).PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "folder/child.txt");
        Require(whileIncomplete.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                whileIncomplete.OperationId is null && whileIncomplete.TombstoneId is null,
            "A newer incomplete scan displaced the last complete ancestor fence after restart.");

        await coordinator.ObserveAsync("manual");
        var recovered = (await new SyncAuthorityReconciliationPlanner(
                new SyncAuthorityDatabase(authority.DatabasePath), options, admission: admission).PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "folder/child.txt");
        Require(recovered.Kind == SyncAuthorityPlannerOutcomeKind.Unchanged &&
                recovered.OperationId is null && recovered.TombstoneId is null,
            "A complete authoritative subtree observation did not release the ancestor uncertainty fence.");

        await AssertTypeConflictFenceAsync("local-file", localKind: "file", dropboxKind: "directory");
        await AssertTypeConflictFenceAsync("dropbox-file", localKind: "directory", dropboxKind: "file");

        async Task AssertTypeConflictFenceAsync(string suffix, string localKind, string dropboxKind)
        {
            var typePairId = "pair-ancestor-type-fence-" + suffix;
            var typeLocalRootId = "root-ancestor-type-local-" + suffix;
            var typeDropboxRootId = "root-ancestor-type-dropbox-" + suffix;
            var typeLocalRoot = Path.Combine(testRoot, "type-local-" + suffix);
            Directory.CreateDirectory(typeLocalRoot);
            var typeDropboxRoot = "/Ancestor Type " + suffix;
            await authority.RegisterPairAsync(typePairId, 1);
            var typeAdmission = await AdmitFixturePairsAsync(authority, typePairId);
            await authority.RegisterRootBindingAsync(typeLocalRootId, typePairId, "local", 1,
                "{\"volumeSerialNumber\":1,\"fileId\":1}", typeLocalRoot);
            await authority.RegisterRootBindingAsync(typeDropboxRootId, typePairId, "dropbox", 1,
                JsonSerializer.Serialize(new { folderId = "folder-ancestor-type-" + suffix }), typeDropboxRoot);

            var localParentObject = "object-ancestor-local-parent-" + suffix;
            var dropboxParentObject = "object-ancestor-dropbox-parent-" + suffix;
            var localParentVersion = "version-ancestor-local-parent-" + suffix;
            var dropboxParentVersion = "version-ancestor-dropbox-parent-" + suffix;
            var localParentMembership = "membership-ancestor-local-parent-" + suffix;
            var dropboxParentMembership = "membership-ancestor-dropbox-parent-" + suffix;
            await authority.RegisterObjectAsync(localParentObject, localKind);
            await authority.RegisterObjectVersionAsync(localParentVersion, localParentObject, "local",
                JsonSerializer.Serialize(new { fileId = "local-parent-" + suffix, volumeSerial = "00000001" }),
                localKind == "file" ? HashB : null, localKind == "file" ? 1 : null);
            await authority.RegisterMembershipAsync(localParentMembership, localParentObject,
                typeLocalRootId, "folder", 1);
            await authority.RegisterObjectAsync(dropboxParentObject, dropboxKind);
            await authority.RegisterObjectVersionAsync(dropboxParentVersion, dropboxParentObject, "dropbox",
                JsonSerializer.Serialize(new { fileId = "id:dropbox-parent-" + suffix,
                    revision = dropboxKind == "file" ? "rev:parent" : null }),
                dropboxKind == "file" ? HashB : null, dropboxKind == "file" ? 1 : null);
            await authority.RegisterMembershipAsync(dropboxParentMembership, dropboxParentObject,
                typeDropboxRootId, "folder", 1);

            var childObject = "object-ancestor-child-" + suffix;
            var childLocalVersion = "version-ancestor-child-local-" + suffix;
            var childDropboxVersion = "version-ancestor-child-dropbox-" + suffix;
            var childLocalMembership = "membership-ancestor-child-local-" + suffix;
            var childDropboxMembership = "membership-ancestor-child-dropbox-" + suffix;
            await authority.RegisterObjectAsync(childObject, "file");
            await authority.RegisterObjectVersionAsync(childLocalVersion, childObject, "local",
                JsonSerializer.Serialize(new { fileId = "local-child-" + suffix, volumeSerial = "00000001" }),
                HashA, 1);
            await authority.RegisterObjectVersionAsync(childDropboxVersion, childObject, "dropbox",
                JsonSerializer.Serialize(new { fileId = "id:ancestor-child-" + suffix, revision = "rev:1" }),
                HashA, 1);
            await authority.RegisterMembershipAsync(childLocalMembership, childObject,
                typeLocalRootId, "folder/child.txt", 1);
            await authority.RegisterMembershipAsync(childDropboxMembership, childObject,
                typeDropboxRootId, "folder/child.txt", 1);
            await authority.AcceptSynchronizationPointAsync("syncpoint-ancestor-child-" + suffix, typePairId,
                childObject, childLocalVersion, childDropboxVersion,
                childLocalMembership, childDropboxMembership);

            SyncAuthorityPlannerOutcome? retainedParentConflict = null;
            for (var gracePass = 1; gracePass <= 3; gracePass++)
            {
                var typeLocalScan = await authority.BeginScanGenerationAsync(typePairId, typeLocalRootId, "local",
                    "monitor", "{\"scope\":\"complete\"}");
                await authority.AppendObservationAsync(typeLocalScan,
                    $"observation-ancestor-local-parent-{suffix}-{gracePass}",
                    localParentVersion, localParentMembership, "{}");
                if (localKind == "directory")
                    await authority.AppendObservationAsync(typeLocalScan,
                        $"observation-ancestor-local-child-{suffix}-{gracePass}",
                        childLocalVersion, childLocalMembership, "{}");
                await authority.CompleteScanGenerationAsync(typeLocalScan);

                var typeDropboxScan = await authority.BeginScanGenerationAsync(typePairId, typeDropboxRootId,
                    "dropbox", "monitor", "{\"scope\":\"complete\"}");
                await authority.AppendObservationAsync(typeDropboxScan,
                    $"observation-ancestor-dropbox-parent-{suffix}-{gracePass}",
                    dropboxParentVersion, dropboxParentMembership, "{}");
                if (dropboxKind == "directory")
                    await authority.AppendObservationAsync(typeDropboxScan,
                        $"observation-ancestor-dropbox-child-{suffix}-{gracePass}",
                        childDropboxVersion, childDropboxMembership, "{}");
                await authority.CompleteScanGenerationAsync(typeDropboxScan);

                var typeOptions = options with { CapabilityProfileId = "profile-ancestor-type-fence-" + suffix };
                var planned = await new SyncAuthorityReconciliationPlanner(authority, typeOptions, admission: typeAdmission)
                    .PlanAsync(typePairId);
                retainedParentConflict = planned.Single(item => item.RelativePath == "folder");
                var blockedChild = planned.Single(item => item.RelativePath == "folder/child.txt");
                Require(retainedParentConflict.Kind == SyncAuthorityPlannerOutcomeKind.Conflict &&
                        retainedParentConflict.ConflictId is not null &&
                        retainedParentConflict.OperationId is null &&
                        blockedChild.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                        blockedChild.OperationId is null && blockedChild.TombstoneId is null &&
                        blockedChild.Reason.Contains("file/folder conflict", StringComparison.OrdinalIgnoreCase),
                    $"The {suffix} ancestor type conflict was not visible while its descendant remained fenced on grace pass {gracePass}.");
            }

            var restartedPlan = await new SyncAuthorityReconciliationPlanner(
                    new SyncAuthorityDatabase(authority.DatabasePath),
                    options with { CapabilityProfileId = "profile-ancestor-type-fence-" + suffix }, admission: typeAdmission)
                .PlanAsync(typePairId);
            Require(restartedPlan.Single(item => item.RelativePath == "folder").Kind ==
                    SyncAuthorityPlannerOutcomeKind.Conflict &&
                    restartedPlan.Single(item => item.RelativePath == "folder/child.txt").Kind ==
                    SyncAuthorityPlannerOutcomeKind.Blocked &&
                    await ScalarLongAsync(authority.DatabasePath,
                        $"SELECT count(*) FROM operation WHERE pair_id='{typePairId}' AND kind IN ('delete_local','delete_remote');") == 0 &&
                    await ScalarLongAsync(authority.DatabasePath,
                        $"SELECT count(*) FROM tombstone WHERE pair_id='{typePairId}';") == 0,
                $"The {suffix} type-conflict fence failed across grace expiry and restart.");

            var conflictSnapshot = await authority.LoadConflictAsync(retainedParentConflict!.ConflictId!);
            var typePair = new SyncPair(typePairId, "Type conflict " + suffix, typeLocalRoot, 1, 1,
                typeDropboxRoot, "folder-ancestor-type-" + suffix);
            await ExpectRejectAsync(() => new SyncAuthorityConflictResolutionCoordinator(typePair,
                    new FakeSyncRemoteStore(), authority,
                    options with { CapabilityProfileId = "profile-ancestor-type-fence-" + suffix }, typeAdmission)
                .PlanAsync(conflictSnapshot.ConflictId, conflictSnapshot.ConflictGeneration,
                    SyncAuthorityConflictResolutionKind.KeepLocal));

            var resolvedParentObject = "object-ancestor-resolved-parent-" + suffix;
            var resolvedLocalVersion = "version-ancestor-resolved-local-parent-" + suffix;
            var resolvedDropboxVersion = "version-ancestor-resolved-dropbox-parent-" + suffix;
            var resolvedLocalMembership = "membership-ancestor-resolved-local-parent-" + suffix;
            var resolvedDropboxMembership = "membership-ancestor-resolved-dropbox-parent-" + suffix;
            await authority.RegisterObjectAsync(resolvedParentObject, "directory");
            await authority.RegisterObjectVersionAsync(resolvedLocalVersion, resolvedParentObject, "local",
                JsonSerializer.Serialize(new { fileId = "local-resolved-parent-" + suffix,
                    volumeSerial = "00000001" }), null, null);
            await authority.RegisterObjectVersionAsync(resolvedDropboxVersion, resolvedParentObject, "dropbox",
                JsonSerializer.Serialize(new { fileId = "id:resolved-parent-" + suffix }), null, null);
            await authority.RegisterMembershipAsync(resolvedLocalMembership, resolvedParentObject,
                typeLocalRootId, "folder", 2);
            await authority.RegisterMembershipAsync(resolvedDropboxMembership, resolvedParentObject,
                typeDropboxRootId, "folder", 2);

            var resolvedLocalScan = await authority.BeginScanGenerationAsync(typePairId, typeLocalRootId, "local",
                "manual", "{\"scope\":\"complete_after_type_resolution\"}");
            await authority.AppendObservationAsync(resolvedLocalScan,
                "observation-resolved-local-parent-" + suffix, resolvedLocalVersion,
                resolvedLocalMembership, "{}");
            await authority.AppendObservationAsync(resolvedLocalScan,
                "observation-resolved-local-child-" + suffix, childLocalVersion,
                childLocalMembership, "{}");
            await authority.CompleteScanGenerationAsync(resolvedLocalScan);
            var resolvedDropboxScan = await authority.BeginScanGenerationAsync(typePairId, typeDropboxRootId,
                "dropbox", "manual", "{\"scope\":\"complete_after_type_resolution\"}");
            await authority.AppendObservationAsync(resolvedDropboxScan,
                "observation-resolved-dropbox-parent-" + suffix, resolvedDropboxVersion,
                resolvedDropboxMembership, "{}");
            await authority.AppendObservationAsync(resolvedDropboxScan,
                "observation-resolved-dropbox-child-" + suffix, childDropboxVersion,
                childDropboxMembership, "{}");
            await authority.CompleteScanGenerationAsync(resolvedDropboxScan);
            var afterResolution = await new SyncAuthorityReconciliationPlanner(
                    new SyncAuthorityDatabase(authority.DatabasePath),
                    options with { CapabilityProfileId = "profile-ancestor-type-fence-" + suffix }, admission: typeAdmission)
                .PlanAsync(typePairId);
            Require(afterResolution.Single(item => item.RelativePath == "folder").Kind ==
                    SyncAuthorityPlannerOutcomeKind.Adopted &&
                    afterResolution.Single(item => item.RelativePath == "folder/child.txt").Kind ==
                    SyncAuthorityPlannerOutcomeKind.Unchanged,
                $"A complete post-resolution observation did not release the {suffix} descendant fence.");
        }
    }

    private static async Task AssertDropboxToLocalCrossPairMoveAsync(SyncAuthorityDatabase authority,
        string testRoot, string suffix, PilotEntryKind kind, bool forceCrossVolume = false)
    {
        var fixture = await CreateCrossPairFixtureAsync(authority, testRoot, suffix, kind);
        var reachedVerifiedCopyBoundary = false;
        SyncAuthorityCrossPairMoveCoordinator.ForceCrossVolumeForTest = forceCrossVolume;
        SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = boundary =>
            reachedVerifiedCopyBoundary |= boundary == "cross-volume-destination-verified-before-source-retirement";
        try
        {
            var expected = (await fixture.SourceRemote.ListEntriesAsync())
                .Single(item => string.Equals(item.RelativePath, fixture.SourcePath, StringComparison.Ordinal));
            await fixture.SourceRemote.MoveToAsync(expected, fixture.DestinationRemote, fixture.DestinationPath);
            await fixture.SourceObserver.ObserveAsync("monitor");
            await fixture.DestinationObserver.ObserveAsync("monitor");
            var coordinator = fixture.Coordinator(authority);
            var plan = (await coordinator.PlanAsync()).Single();
            Require(plan.Direction == SyncAuthorityCrossPairMoveDirection.DropboxToLocal && plan.Kind == kind,
                "A Dropbox-origin cross-pair move was not planned in the correct direction.");
            var result = await coordinator.ExecuteAsync(plan, "worker-cross-pair-" + suffix);
            var sourceLocal = Path.Combine(fixture.SourcePair.LocalRoot,
                fixture.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            var destinationLocal = Path.Combine(fixture.DestinationPair.LocalRoot,
                fixture.DestinationPath.Replace('/', Path.DirectorySeparatorChar));
            Require(result.Status == SyncAuthorityOperationStatus.Completed &&
                    !File.Exists(sourceLocal) && !Directory.Exists(sourceLocal) &&
                    (kind == PilotEntryKind.File ? File.Exists(destinationLocal) : Directory.Exists(destinationLocal)) &&
                    fixture.SourceRemote.MoveCount == 1 && fixture.SourceRemote.DownloadCount == 0 &&
                    fixture.DestinationRemote.DownloadCount == 0,
                "A Dropbox-origin cross-pair move downloaded/deleted content instead of moving the retained local identity.");
            if (kind == PilotEntryKind.Directory)
                Require(File.ReadAllText(Path.Combine(destinationLocal, "nested.txt")) == "nested payload" &&
                        fixture.DestinationRemote.ReadText(fixture.DestinationPath + "/nested.txt") == "nested payload",
                    "A Dropbox-origin folder move did not carry its complete descendant tree.");
            else
                Require(File.ReadAllText(destinationLocal) == "cross-pair payload",
                    "A cross-volume file copy did not preserve exact bytes.");
            if (forceCrossVolume)
            {
                var recovery = (await authority.LoadRecoveryArtifactsAsync()).Where(item =>
                    item.PairId == fixture.SourcePair.Id && item.Side == "local").ToArray();
                Require(reachedVerifiedCopyBoundary && recovery.Length == 1,
                    "The forced cross-volume path did not verify the destination before retaining the source in Recovery.");
                if (kind == PilotEntryKind.Directory)
                {
                    using var identity = JsonDocument.Parse(recovery.Single().IdentityJson);
                    var recoveryPath = identity.RootElement.GetProperty("path").GetString();
                    Require(recoveryPath is { Length: > 259 } && Directory.Exists(recoveryPath),
                        "The directory cross-volume witness did not exercise and retain an exact path beyond legacy MAX_PATH.");
                }
            }
        }
        finally
        {
            SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
            SyncAuthorityCrossPairMoveCoordinator.ForceCrossVolumeForTest = false;
        }
    }

    private static async Task AssertLocalToDropboxFolderMoveAsync(SyncAuthorityDatabase authority,
        string testRoot)
    {
        var fixture = await CreateCrossPairFixtureAsync(authority, testRoot, "local-folder",
            PilotEntryKind.Directory);
        Directory.Move(Path.Combine(fixture.SourcePair.LocalRoot, fixture.SourcePath),
            Path.Combine(fixture.DestinationPair.LocalRoot, fixture.DestinationPath));
        await fixture.SourceObserver.ObserveAsync("monitor");
        await fixture.DestinationObserver.ObserveAsync("monitor");
        var coordinator = fixture.Coordinator(authority);
        var plan = (await coordinator.PlanAsync()).Single();
        var result = await coordinator.ExecuteAsync(plan, "worker-cross-pair-local-folder");
        Require(result.Status == SyncAuthorityOperationStatus.Completed &&
                !fixture.SourceRemote.DirectoryExists(fixture.SourcePath) &&
                fixture.DestinationRemote.DirectoryExists(fixture.DestinationPath) &&
                fixture.DestinationRemote.ReadText(fixture.DestinationPath + "/nested.txt") == "nested payload" &&
                fixture.SourceRemote.MoveCount == 1 && fixture.SourceRemote.UploadCount == 0 &&
                fixture.SourceRemote.DeleteCount == 0,
            "A local-origin folder move did not preserve the Dropbox folder identity and descendants.");
    }

    private static async Task AssertCrossVolumeCrashRecoveryAsync(SyncAuthorityDatabase authority,
        string testRoot)
    {
        var fixture = await CreateCrossPairFixtureAsync(authority, testRoot, "cross-volume-crash",
            PilotEntryKind.File);
        SyncAuthorityCrossPairMoveCoordinator.ForceCrossVolumeForTest = true;
        try
        {
            var expected = (await fixture.SourceRemote.ListEntriesAsync()).Single(item =>
                string.Equals(item.RelativePath, fixture.SourcePath, StringComparison.Ordinal));
            await fixture.SourceRemote.MoveToAsync(expected, fixture.DestinationRemote,
                fixture.DestinationPath);
            await fixture.SourceObserver.ObserveAsync("monitor");
            await fixture.DestinationObserver.ObserveAsync("monitor");
            var coordinator = fixture.Coordinator(authority);
            var plan = (await coordinator.PlanAsync()).Single();
            var injected = false;
            SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = boundary =>
            {
                if (boundary != "cross-volume-destination-verified-before-source-retirement" || injected) return;
                injected = true;
                throw new IOException("simulated process termination after verified destination copy");
            };
            await ExpectRejectAsync(() => coordinator.ExecuteAsync(plan, "worker-cross-volume-crash"));
            var sourcePath = Path.Combine(fixture.SourcePair.LocalRoot, fixture.SourcePath);
            var destinationPath = Path.Combine(fixture.DestinationPair.LocalRoot,
                fixture.DestinationPath);
            Require(injected && File.Exists(sourcePath) && File.Exists(destinationPath),
                "The cross-volume interruption did not retain both verified copies before source retirement.");

            SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
            var recovered = await coordinator.ExecuteAsync(plan, "worker-cross-volume-restart");
            var artifacts = (await authority.LoadRecoveryArtifactsAsync()).Where(item =>
                item.PairId == fixture.SourcePair.Id && item.Side == "local").ToArray();
            Require(recovered.Status == SyncAuthorityOperationStatus.Completed &&
                    !File.Exists(sourcePath) && File.Exists(destinationPath) &&
                    File.ReadAllText(destinationPath) == "cross-pair payload" && artifacts.Length == 1,
                "Cross-volume restart did not reconcile the verified destination before retiring one source to Recovery.");
        }
        finally
        {
            SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
            SyncAuthorityCrossPairMoveCoordinator.ForceCrossVolumeForTest = false;
        }
    }

    private static async Task AssertCrossPairCollisionAsync(SyncAuthorityDatabase authority, string testRoot)
    {
        var fixture = await CreateCrossPairFixtureAsync(authority, testRoot, "collision", PilotEntryKind.File);
        File.Move(Path.Combine(fixture.SourcePair.LocalRoot, fixture.SourcePath),
            Path.Combine(fixture.DestinationPair.LocalRoot, fixture.DestinationPath));
        fixture.DestinationRemote.Put(fixture.DestinationPath, "destination occupant");
        await fixture.SourceObserver.ObserveAsync("monitor");
        await fixture.DestinationObserver.ObserveAsync("monitor");
        var coordinator = fixture.Coordinator(authority);
        var plan = (await coordinator.PlanAsync()).Single();
        var operation = await authority.LoadOperationAsync(plan.OperationId);
        Require(operation.Members.Any(item => item.PairId == fixture.DestinationPair.Id &&
                                               item.Role == "lock"),
            "The occupied cross-pair destination was not retained as a transaction lock.");
        var result = await coordinator.ExecuteAsync(plan, "worker-cross-pair-collision");
        Require(result.Status == SyncAuthorityOperationStatus.Blocked && !result.EffectDispatched &&
                fixture.SourceRemote.ReadText(fixture.SourcePath) == "cross-pair payload" &&
                fixture.DestinationRemote.ReadText(fixture.DestinationPath) == "destination occupant" &&
                fixture.SourceRemote.MoveCount == 0,
            "A cross-pair destination collision changed content or failed to remain visibly blocked.");
    }

    private static async Task AssertCrossPairLostResponseAsync(SyncAuthorityDatabase authority, string testRoot,
        bool afterEffect)
    {
        var suffix = afterEffect ? "unknown-after" : "unknown-before";
        var fixture = await CreateCrossPairFixtureAsync(authority, testRoot, suffix, PilotEntryKind.File);
        File.Move(Path.Combine(fixture.SourcePair.LocalRoot, fixture.SourcePath),
            Path.Combine(fixture.DestinationPair.LocalRoot, fixture.DestinationPath));
        await fixture.SourceObserver.ObserveAsync("monitor");
        await fixture.DestinationObserver.ObserveAsync("monitor");
        var coordinator = fixture.Coordinator(authority);
        var plan = (await coordinator.PlanAsync()).Single();
        fixture.SourceRemote.InterruptNextMoveAfterEffect = afterEffect;
        fixture.SourceRemote.InterruptNextMoveBeforeEffect = !afterEffect;
        var interrupted = await coordinator.ExecuteAsync(plan, "worker-cross-pair-" + suffix);
        Require(interrupted.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "The cross-pair lost-response boundary did not remain effect-unknown.");
        var reconciled = await coordinator.ExecuteAsync(plan, "worker-cross-pair-reconcile-" + suffix);
        if (!afterEffect)
        {
            Require(reconciled.Status == SyncAuthorityOperationStatus.Ready,
                "A verified-not-applied cross-pair move was not returned to Ready exactly once.");
            reconciled = await coordinator.ExecuteAsync(plan, "worker-cross-pair-retry-" + suffix);
        }
        Require(reconciled.Status == SyncAuthorityOperationStatus.Completed &&
                fixture.SourceRemote.MoveCount == 1 &&
                !fixture.SourceRemote.Exists(fixture.SourcePath) &&
                fixture.DestinationRemote.ReadText(fixture.DestinationPath) == "cross-pair payload",
            "Cross-pair lost-response reconciliation duplicated or lost the native provider move.");
    }

    private static async Task AssertCrossPairLiveActionMetadataAsync(SyncAuthorityDatabase authority,
        string testRoot)
    {
        var extensionless = await CreateCrossPairFixtureAsync(authority, testRoot, "live-extensionless",
            PilotEntryKind.File, "LICENSE", "NOTICE");
        File.Move(Path.Combine(extensionless.SourcePair.LocalRoot, extensionless.SourcePath),
            Path.Combine(extensionless.DestinationPair.LocalRoot, extensionless.DestinationPath));
        await extensionless.SourceObserver.ObserveAsync("monitor");
        await extensionless.DestinationObserver.ObserveAsync("monitor");
        var extensionlessCoordinator = extensionless.Coordinator(authority);
        var extensionlessPlan = (await extensionlessCoordinator.PlanAsync()).Single();
        var extensionlessActions = new List<SyncAuthorityLiveAction>();
        var extensionlessResult = await SyncAuthorityRuntimeHost.RunCrossPairPlanAsync(
            extensionlessCoordinator, extensionlessPlan, new PilotRunControl(), CancellationToken.None,
            action =>
            {
                extensionlessActions.Add(action);
                return Task.CompletedTask;
            });
        AssertCrossPairLiveLifecycle(extensionlessActions, extensionlessPlan,
            "LICENSE → NOTICE", "File", "File", "Completed");
        Require(extensionlessResult.Status == SyncAuthorityOperationStatus.Completed &&
                extensionlessResult.EffectDispatched && extensionless.SourceRemote.MoveCount == 1 &&
                extensionless.SourceRemote.UploadCount == 0 && extensionless.SourceRemote.DeleteCount == 0 &&
                extensionless.DestinationRemote.UploadCount == 0 &&
                !extensionless.SourceRemote.Exists(extensionless.SourcePath) &&
                extensionless.DestinationRemote.ReadText(extensionless.DestinationPath) == "cross-pair payload",
            "The extensionless cross-pair live-action proof changed the planned native move effect.");

        var dottedFolder = await CreateCrossPairFixtureAsync(authority, testRoot, "live-dotted-folder",
            PilotEntryKind.Directory, "archive.v1", "archive.v2");
        Directory.Move(Path.Combine(dottedFolder.SourcePair.LocalRoot, dottedFolder.SourcePath),
            Path.Combine(dottedFolder.DestinationPair.LocalRoot, dottedFolder.DestinationPath));
        dottedFolder.DestinationRemote.PutDirectory(dottedFolder.DestinationPath);
        await dottedFolder.SourceObserver.ObserveAsync("monitor");
        await dottedFolder.DestinationObserver.ObserveAsync("monitor");
        var dottedFolderCoordinator = dottedFolder.Coordinator(authority);
        var dottedFolderPlan = (await dottedFolderCoordinator.PlanAsync()).Single();
        var dottedFolderActions = new List<SyncAuthorityLiveAction>();
        var dottedFolderResult = await SyncAuthorityRuntimeHost.RunCrossPairPlanAsync(
            dottedFolderCoordinator, dottedFolderPlan, new PilotRunControl(), CancellationToken.None,
            action =>
            {
                dottedFolderActions.Add(action);
                return Task.CompletedTask;
            });
        AssertCrossPairLiveLifecycle(dottedFolderActions, dottedFolderPlan,
            "archive.v1 → archive.v2", "Folder", "Folder", "Needs attention");
        Require(dottedFolderResult.Status == SyncAuthorityOperationStatus.Blocked &&
                !dottedFolderResult.EffectDispatched && dottedFolder.SourceRemote.MoveCount == 0 &&
                dottedFolder.SourceRemote.DirectoryExists(dottedFolder.SourcePath) &&
                dottedFolder.DestinationRemote.DirectoryExists(dottedFolder.DestinationPath),
            "The dotted-directory cross-pair rejection proof issued an effect or lost either endpoint.");
    }

    private static void AssertCrossPairLiveLifecycle(IReadOnlyList<SyncAuthorityLiveAction> actions,
        SyncAuthorityCrossPairMovePlan plan, string path, string itemKind, string fileType, string terminalStage)
    {
        Require(actions.Count == 2 && actions[0].Stage == "Starting" && actions[1].Stage == terminalStage &&
                actions.All(item => item.OperationId == plan.OperationId && item.RelativePath == path &&
                                    item.Action == "Move" && item.ItemKind == itemKind &&
                                    item.FileType == fileType),
            $"The cross-pair producer did not preserve the ordered authoritative lifecycle for {path}.");
    }

    private static async Task<CrossPairFixture> CreateCrossPairFixtureAsync(SyncAuthorityDatabase authority,
        string testRoot, string suffix, PilotEntryKind kind, string? sourcePathOverride = null,
        string? destinationPathOverride = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cross-pair local identity tests require Windows.");
        var sourceRoot = Path.Combine(testRoot, "cross-pair-" + suffix + "-source");
        var destinationRoot = Path.Combine(testRoot, "cross-pair-" + suffix + "-destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = sourcePathOverride ?? (kind == PilotEntryKind.File ? "source.txt" : "source-folder");
        var destinationPath = destinationPathOverride ??
                              (kind == PilotEntryKind.File ? "destination.txt" : "destination-folder");
        if (kind == PilotEntryKind.File)
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, sourcePath), "cross-pair payload");
        else
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, sourcePath));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, sourcePath, "nested.txt"), "nested payload");
        }
        PilotDirectoryIdentity sourceIdentity;
        PilotDirectoryIdentity destinationIdentity;
        using (var root = PilotDirectoryLease.Open(sourceRoot)) sourceIdentity = root.Identity;
        using (var root = PilotDirectoryLease.Open(destinationRoot)) destinationIdentity = root.Identity;
        SyncLocalControlNamespaceBinding sourceControl;
        SyncLocalControlNamespaceBinding destinationControl;
        using (var root = PilotDirectoryLease.Open(sourceRoot))
            sourceControl = SyncControlNamespace.BindLocalForTest(root, "installation-cross-pair",
                "control-cross-" + suffix + "-source");
        using (var root = PilotDirectoryLease.Open(destinationRoot))
            destinationControl = SyncControlNamespace.BindLocalForTest(root, "installation-cross-pair",
                "control-cross-" + suffix + "-destination");
        var sourcePair = new SyncPair("pair-cross-" + suffix + "-source", "Cross source " + suffix, sourceRoot,
            sourceIdentity.VolumeSerialNumber, sourceIdentity.FileId, "/Cross" + suffix + "Source",
            "folder-cross-" + suffix + "-source")
        {
            ControlNamespace = new SyncControlNamespaceBinding("installation-cross-pair",
                "control-cross-" + suffix + "-source", sourceControl),
            RecoveryRoot = new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.ProductContainerPath + "/CrossRecovery", "folder-cross-recovery",
                "root-namespace-cross")
        };
        var destinationPair = new SyncPair("pair-cross-" + suffix + "-destination",
            "Cross destination " + suffix, destinationRoot, destinationIdentity.VolumeSerialNumber,
            destinationIdentity.FileId, "/Cross" + suffix + "Destination",
            "folder-cross-" + suffix + "-destination")
        {
            ControlNamespace = new SyncControlNamespaceBinding("installation-cross-pair",
                "control-cross-" + suffix + "-destination", destinationControl)
        };
        var sourceLocalRoot = "root-cross-" + suffix + "-source-local";
        var sourceDropboxRoot = "root-cross-" + suffix + "-source-dropbox";
        var destinationLocalRoot = "root-cross-" + suffix + "-destination-local";
        var destinationDropboxRoot = "root-cross-" + suffix + "-destination-dropbox";
        await authority.RegisterPairAsync(sourcePair.Id, 1);
        await authority.RegisterPairAsync(destinationPair.Id, 1);
        await authority.RegisterRootBindingAsync(sourceLocalRoot, sourcePair.Id, "local", 1,
            JsonSerializer.Serialize(new { sourceIdentity.VolumeSerialNumber, sourceIdentity.FileId }), sourceRoot);
        await authority.RegisterRootBindingAsync(sourceDropboxRoot, sourcePair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = sourcePair.DropboxFolderId }), sourcePair.DropboxRoot);
        await authority.RegisterRootBindingAsync(destinationLocalRoot, destinationPair.Id, "local", 1,
            JsonSerializer.Serialize(new { destinationIdentity.VolumeSerialNumber, destinationIdentity.FileId }),
            destinationRoot);
        await authority.RegisterRootBindingAsync(destinationDropboxRoot, destinationPair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = destinationPair.DropboxFolderId }),
            destinationPair.DropboxRoot);
        var sourceRemote = new FakeSyncRemoteStore();
        var destinationRemote = new FakeSyncRemoteStore();
        if (kind == PilotEntryKind.File)
            sourceRemote.Put(sourcePath, "cross-pair payload");
        else
        {
            sourceRemote.PutDirectory(sourcePath);
            sourceRemote.Put(sourcePath + "/nested.txt", "nested payload");
        }
        var sourceObserver = new SyncAuthorityObservationCoordinator(sourcePair, sourceRemote, authority,
            sourceLocalRoot, sourceDropboxRoot);
        var destinationObserver = new SyncAuthorityObservationCoordinator(destinationPair, destinationRemote,
            authority, destinationLocalRoot, destinationDropboxRoot);
        await sourceObserver.ObserveAsync("initial");
        await sourceObserver.ObserveAsync("initial");
        await destinationObserver.ObserveAsync("initial");
        await destinationObserver.ObserveAsync("initial");
        var adopted = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(sourcePair.Id);
        Require(adopted.All(item => item.Kind == SyncDecisionKind.Baseline) &&
                adopted.Count == (kind == PilotEntryKind.File ? 1 : 2),
            "The cross-pair fixture did not establish its complete effect-free source baseline.");
        return new CrossPairFixture(sourcePair, destinationPair, sourceRemote, destinationRemote,
            sourceObserver, destinationObserver, sourcePath, destinationPath,
            await AdmitFixturePairsAsync(authority, sourcePair.Id, destinationPair.Id));
    }

    private sealed record CrossPairFixture(
        SyncPair SourcePair,
        SyncPair DestinationPair,
        FakeSyncRemoteStore SourceRemote,
        FakeSyncRemoteStore DestinationRemote,
        SyncAuthorityObservationCoordinator SourceObserver,
        SyncAuthorityObservationCoordinator DestinationObserver,
        string SourcePath,
        string DestinationPath,
        SyncAuthorityCommandAdmission Admission)
    {
        public SyncAuthorityCrossPairMoveCoordinator Coordinator(SyncAuthorityDatabase authority) => new(
            SourcePair, SourceRemote,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", "control-" + SourcePair.Id,
                "control-" + SourcePair.Id, EnableDeletionEffects: true, EnableMoveEffects: true),
            DestinationPair, DestinationRemote,
            new SyncAuthorityPlannerOptions(1, "profile-cloud-pro", "control-" + DestinationPair.Id,
                "control-" + DestinationPair.Id, EnableDeletionEffects: true, EnableMoveEffects: true),
            authority, Admission);
    }

    private static async Task AssertMonitorBatchAtomicityAsync(SyncAuthorityDatabase authority, string databasePath)
    {
        await authority.RegisterMonitorCursorAsync("cursor-observe", "pair-observe", "root-observe-dropbox",
            "dropbox_cursor", 1, "{\"folderId\":\"folder-observe\"}", "cursor-0");
        var batch = new[]
        {
            new SyncAuthorityMonitorEventInput("event-observe-1", "provider-event-1", "remote.txt",
                "changed", "{\"revision\":\"2\"}")
        };
        var monitorWriter=await authority.BeginMonitorWriterAsync("cursor-observe","pair-observe","root-observe-dropbox",
            "dropbox_cursor",1,"{\"folderId\":\"folder-observe\"}");
        var monitorBatch=authority.PrepareMonitorBatch(monitorWriter,batch,"cursor-0","cursor-1");
        await authority.CommitMonitorBatchAsync(monitorWriter,monitorBatch);
        await authority.CommitMonitorBatchAsync(monitorWriter,monitorBatch);
        Require(await ScalarLongAsync(databasePath,
                    "SELECT count(*) FROM monitor_event WHERE pair_id='pair-observe';") == 1,
            "A replayed provider event was not deduplicated before cursor acknowledgement.");
        Require(await ScalarLongAsync(databasePath,
                    "SELECT event_count FROM dirty_item WHERE pair_id='pair-observe' AND normalized_path_key='remote.txt';") == 1,
            "A replayed provider event dirtied the same item more than once.");
        Require(string.Equals(await ScalarStringAsync(databasePath,
                    "SELECT cursor_value FROM monitor_cursor WHERE monitor_cursor_id='cursor-observe';"),
                "cursor-1", StringComparison.Ordinal),
            "The provider cursor did not advance in the same committed batch as its events.");
        await authority.SetMonitorCursorStatusAsync("cursor-observe", "pair-observe", "unavailable");
        var unavailable = await authority.LoadMonitorCursorAsync("cursor-observe");
        var unavailableProjection = (await authority.LoadMonitorProjectionAsync())
            .Single(item => item.PairId == "pair-observe" && item.Kind == "dropbox_cursor");
        Require(unavailable?.CursorValue == "cursor-1" && unavailable.Status == "unavailable" &&
                unavailableProjection.State == "Monitor unavailable" &&
                unavailableProjection.PendingDirtyItems == 1,
            "A monitor fault did not retain its exact cursor, pending hint count, and unavailable projection.");
        await authority.CommitMonitorBatchAsync(monitorWriter,authority.PrepareMonitorBatch(monitorWriter,[],"cursor-1","cursor-1"));
        Require((await authority.LoadMonitorCursorAsync("cursor-observe"))?.Status == "active" &&
                (await authority.LoadMonitorProjectionAsync())
                .Single(item => item.PairId == "pair-observe" && item.Kind == "dropbox_cursor").State ==
                "Monitoring",
            "A successful monitor retry did not atomically reactivate its retained cursor projection.");
    }

    private static async Task AssertCurrentObservationIdentityResolutionAsync(SyncAuthorityDatabase authority)
    {
        const string pairId = "pair-identity-window";
        const string rootId = "root-identity-window-local";
        const string oldIdentity = "{\"volumeSerial\":\"00a1b2c3\",\"fileId\":\"0000000000000101\"}";
        const string currentIdentity = "{\"volumeSerial\":\"00a1b2c3\",\"fileId\":\"0000000000000202\"}";
        await authority.RegisterPairAsync(pairId, 1);
        await authority.RegisterRootBindingAsync(rootId, pairId, "local", 1,
            "{\"volumeSerial\":\"00a1b2c3\",\"fileId\":\"0000000000000001\"}", @"C:\IdentityWindow");

        await authority.RegisterObjectAsync("object-identity-old", "file");
        await authority.RegisterObjectVersionAsync("version-identity-old", "object-identity-old", "local",
            oldIdentity, null, 1);
        await authority.RegisterMembershipAsync("membership-identity-old", "object-identity-old", rootId,
            "old.txt", 1);
        var oldScan = await authority.BeginScanGenerationAsync(pairId, rootId, "local", "manual", "{}");
        await authority.AppendObservationAsync(oldScan, "observation-identity-old", "version-identity-old",
            "membership-identity-old", "{\"path\":\"old.txt\"}");
        await authority.CompleteScanGenerationAsync(oldScan);
        Require(await authority.ResolveObjectIdForPhysicalIdentityAsync(pairId, "local", oldIdentity) ==
                "object-identity-old",
            "The latest complete observation did not retain its current physical identity correlation.");

        await authority.RegisterObjectAsync("object-identity-current", "file");
        await authority.RegisterObjectVersionAsync("version-identity-current", "object-identity-current", "local",
            currentIdentity, null, 1);
        await authority.RegisterMembershipAsync("membership-identity-current", "object-identity-current", rootId,
            "current.txt", 1);
        var currentScan = await authority.BeginScanGenerationAsync(pairId, rootId, "local", "manual", "{}");
        await authority.AppendObservationAsync(currentScan, "observation-identity-current",
            "version-identity-current", "membership-identity-current", "{\"path\":\"current.txt\"}");
        await authority.CompleteScanGenerationAsync(currentScan);

        Require(await authority.ResolveObjectIdForPhysicalIdentityAsync(pairId, "local", oldIdentity) is null &&
                await authority.ResolveObjectIdForPhysicalIdentityAsync(pairId, "local", currentIdentity) ==
                "object-identity-current",
            "Historical physical identity evidence leaked into current move correlation authority.");
    }

    private static async Task AssertFrozenPlansAsync(SyncAuthorityDatabase authority, string databasePath)
    {
        var snapshot = await authority.LoadOperationAsync("operation-frozen");
        Require(snapshot.Status == SyncAuthorityOperationStatus.Ready, "The operation did not freeze into ready state.");
        await ExpectRejectAsync(() => authority.EnsurePairConfigurationMutableAsync("pair-a"));
        Require(snapshot.Members.Count == 1 && snapshot.Dependencies.Count == 1 && snapshot.Allocations.Count == 0,
            "The complete operation plan was not retained atomically before work began.");
        await ExpectRejectAsync(() => authority.AcquireOperationLeaseAsync(
            "operation-frozen", "worker-dependency-bypass", TimeSpan.FromMinutes(5)));

        await ExpectSqliteRejectAsync(databasePath,
            "UPDATE operation_member SET role='source' WHERE operation_id='operation-frozen';");
        await ExpectSqliteRejectAsync(databasePath,
            "DELETE FROM operation_member WHERE operation_id='operation-frozen';");
        await ExpectSqliteRejectAsync(databasePath,
            "INSERT INTO operation_member(operation_member_id,operation_id,pair_id,object_id,role,root_binding_id,membership_id,expected_membership_generation,expected_path_key,expected_state_json) VALUES('late-member','operation-frozen','pair-a','object-a','affected','root-local','membership-a',1,'file.txt','{}');");

        await ExpectSqliteRejectAsync(databasePath,
            "UPDATE operation_dependency SET dependency_kind='must_commit' WHERE operation_id='operation-frozen';");
        await ExpectSqliteRejectAsync(databasePath,
            "DELETE FROM operation_dependency WHERE operation_id='operation-frozen';");
        await ExpectSqliteRejectAsync(databasePath,
            "INSERT INTO operation_dependency(operation_id,depends_on_operation_id,dependency_kind) VALUES('operation-frozen','operation-prerequisite-b','must_resolve');");

        await ExpectSqliteRejectAsync(databasePath,
            "INSERT INTO operation_transition(from_status,to_status) VALUES('ready','completed');");
        await ExpectSqliteRejectAsync(databasePath,
            "DELETE FROM attempt_transition WHERE from_status='started' AND to_status='request_sent';");
        await ExpectSqliteRejectAsync(databasePath,
            "UPDATE allocation_transition SET to_status='blocked' WHERE from_status='authorized' AND to_status='creating';");
        await ExpectSqliteRejectAsync(databasePath,
            "INSERT INTO operation_step_transition(from_status,to_status) VALUES('ready','completed');");
    }

    private static async Task AssertFencedAttemptsAndReconciliationAsync(
        SyncAuthorityDatabase authority, string databasePath)
    {
        var firstLease = await authority.AcquireOperationLeaseAsync("operation-main", "worker-a", TimeSpan.FromMinutes(5));
        await authority.TransitionAllocationAsync("allocation-main", firstLease, SyncAuthorityAllocationStatus.Creating);
        var firstAttempt = await authority.StartDispatchedAttemptAsync("operation-main", firstLease, HashA,
            "dropbox-upload");

        await ExecuteAsync(databasePath,
            "UPDATE lease SET expires_utc='2000-01-01T00:00:00.0000000Z' WHERE lease_id=$lease;",
            ("$lease", firstLease.LeaseId));
        var recoveryLease = await authority.AcquireOperationLeaseAsync("operation-main", "worker-recovery", TimeSpan.FromMinutes(5));
        Require(recoveryLease.FencingToken > firstLease.FencingToken,
            "Lease takeover did not advance the monotonic fencing token.");

        await ExpectRejectAsync(() => authority.TransitionAllocationAsync(
            "allocation-main", firstLease, SyncAuthorityAllocationStatus.Created));
        await ExpectSqliteRejectAsync(databasePath,
            "UPDATE resource_allocation SET status='created' WHERE allocation_id='allocation-main';");
        await ExpectRejectAsync(() => authority.RecordReceiptAsync(firstAttempt.AttemptId, firstLease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}", "{}", HashB));
        await ExpectSqliteRejectAsync(databasePath,
            $"UPDATE operation_attempt SET status='blocked',transition_lease_id='{firstLease.LeaseId}',transition_fencing_token={firstLease.FencingToken} WHERE attempt_id='{firstAttempt.AttemptId}';");

        await authority.RecordReceiptAsync(firstAttempt.AttemptId, recoveryLease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}", "{}", HashB);
        await ExpectRejectAsync(() => authority.StartDispatchedAttemptAsync(
            "operation-main", recoveryLease, HashB, "dropbox-upload-retry"));

        await authority.ReconcileAttemptAsync(firstAttempt.AttemptId, recoveryLease,
            SyncAuthorityAttemptStatus.VerifiedNotApplied, "{\"result\":\"verified-not-applied\"}");
        var reconciled = await authority.LoadOperationAsync("operation-main");
        Require(reconciled.Status == SyncAuthorityOperationStatus.Ready,
            "Verified-not-applied reconciliation did not return the operation to retryable ready state.");
        Require(reconciled.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "The response-received attempt was not durably reconciled to a verified terminal state.");

        await authority.ReleaseLeaseAsync(recoveryLease);
        var retryLease = await authority.AcquireOperationLeaseAsync("operation-main", "worker-retry", TimeSpan.FromMinutes(5));
        var retry = await authority.StartDispatchedAttemptAsync("operation-main", retryLease, HashB,
            "dropbox-upload-retry");
        Require(retry.AttemptNumber == 2,
            "A retry was not admitted exactly once after verified terminal reconciliation.");

        await ExecuteAsync(databasePath,
            "UPDATE lease SET expires_utc='2000-01-01T00:00:00.0000000Z' WHERE lease_id=$lease;",
            ("$lease", retryLease.LeaseId));
        var queue = await authority.ListRunnableOperationsAsync();
        var mainIndex = queue.ToList().FindIndex(item => item.OperationId == "operation-main");
        var firstOrdinary = queue.ToList().FindIndex(item => !item.RecoveryRequired);
        Require(queue.Count >= 3 && mainIndex >= 0 && queue[mainIndex].RecoveryRequired &&
                (firstOrdinary < 0 || mainIndex < firstOrdinary) &&
                queue.Take(firstOrdinary < 0 ? queue.Count : firstOrdinary).All(item => item.RecoveryRequired),
            "Recovery-first scheduling did not place the interrupted effect ahead of independent ready work: " +
            string.Join(",", queue.Select(item => $"{item.OperationId}:{item.Status}:{item.RecoveryRequired}")));
    }

    private static async Task AssertConcurrentLeaseUniquenessAsync(
        SyncAuthorityDatabase authority, string databasePath, SyncAuthorityCommandAdmission admission)
    {
        await authority.CreateOperationAsync(Plan("operation-concurrent"), admission: admission);
        var competitor = new SyncAuthorityDatabase(databasePath);
        var attempts = new[]
        {
            CaptureLeaseAsync(authority, "worker-concurrent-a"),
            CaptureLeaseAsync(competitor, "worker-concurrent-b")
        };
        var results = await Task.WhenAll(attempts);
        Require(results.Count(result => result is not null) == 1,
            "Two independent connections did not enforce one active fenced lease for the same operation.");

        async Task<SyncAuthorityLease?> CaptureLeaseAsync(SyncAuthorityDatabase database, string worker)
        {
            try
            {
                return await database.AcquireOperationLeaseAsync(
                    "operation-concurrent", worker, TimeSpan.FromMinutes(5));
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqliteException)
            {
                return null;
            }
        }
    }

    internal static Task RunDeletionOutcomeRetentionChecksAsync(string testRoot) =>
        AssertPairRunnerAsync(testRoot, deletionOutcomeOnly: true);

    private static async Task AssertPairRunnerAsync(string testRoot, bool deletionOutcomeOnly = false)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(testRoot, "pair-runner-root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "runner.txt"), "initial discovery content");
        PilotDirectoryIdentity identity;
        using (var rootLease = PilotDirectoryLease.Open(root)) identity = rootLease.Identity;
        const string installationId = "installation-pair-runner";
        const string namespaceId = "pair-runner-control";
        const string pairId = "pair-runner";
        SyncLocalControlNamespaceBinding localControl;
        using (var rootLease = PilotDirectoryLease.Open(root, identity))
            localControl = SyncControlNamespace.BindLocalForTest(rootLease, installationId, namespaceId);
        var remote = new FakeSyncRemoteStore();
        var pair = new SyncPair(pairId, "Runner", root, identity.VolumeSerialNumber, identity.FileId,
            "/Runner", "id:runner", ControlNamespace: new SyncControlNamespaceBinding(installationId,
                namespaceId, localControl),
            RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery-runner", "namespace-runner"));
        var authority = new SyncAuthorityDatabase(Path.Combine(testRoot, "pair-runner.db"));
        await authority.InitializeAsync(installationId, "profile-pair-runner", "pair-runner-source", "{}");
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        var admission = await AdmitFixturePairsAsync(authority, pair.Id);
        await authority.RegisterRootBindingAsync("root-pair-runner-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new
            {
                volumeSerial = identity.VolumeSerialNumber.ToString("x8"),
                fileId = identity.FileId.ToString("x16")
            }), root);
        await authority.RegisterRootBindingAsync("root-pair-runner-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:runner\"}", "/Runner");
        var options = new SyncAuthorityPlannerOptions(1, "profile-pair-runner", namespaceId, namespaceId,
            EnableDeletionEffects: true, EnableMoveEffects: true, DeletionMinimumInterval: TimeSpan.Zero);
        var first = await new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox", options, new PilotRunControl(), admission).RunAsync("Manual");
        Require(remote.ReadText("runner.txt") == "initial discovery content" &&
                first.Decisions.Single(item => item.RelativePath == "runner.txt").Kind ==
                SyncDecisionKind.Uploaded,
            "The runtime runner did not merge a one-sided first-run file after its effect-free inventory.");

        if (deletionOutcomeOnly)
        {
            await AssertDeletionOutcomeRetentionAsync(pair, remote, authority, options, root, admission);
            return;
        }

        var deleteRemoteDirectoryPath = Path.Combine(root, "empty-local-folder");
        Directory.CreateDirectory(deleteRemoteDirectoryPath);
        var directoryUploaded = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(remote.DirectoryExists("empty-local-folder") &&
                directoryUploaded.Decisions.Single(item => item.RelativePath == "empty-local-folder").Kind ==
                SyncDecisionKind.Uploaded,
            "An empty local folder did not synchronize as a first-class Dropbox folder.");
        Directory.Delete(deleteRemoteDirectoryPath);
        var directoryDeletedRemote = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        var remoteDirectoryArtifact = (await authority.LoadRecoveryArtifactsAsync()).Single(item =>
            item.OriginalRelativePath == "empty-local-folder" && item.Side == "dropbox");
        Require(!remote.DirectoryExists("empty-local-folder") &&
                directoryDeletedRemote.Decisions.Single(item => item.RelativePath == "empty-local-folder").Kind ==
                SyncDecisionKind.Archived &&
                remoteDirectoryArtifact.Size == 0 &&
                remoteDirectoryArtifact.IdentityJson.Contains("recreate_empty_directory", StringComparison.Ordinal),
            "Dropbox folder deletion did not retain its durable recreate recipe before the effect.");

        remote.PutDirectory("empty-dropbox-folder");
        var directoryDownloaded = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(Directory.Exists(Path.Combine(root, "empty-dropbox-folder")) &&
                directoryDownloaded.Decisions.Single(item => item.RelativePath == "empty-dropbox-folder").Kind ==
                SyncDecisionKind.Downloaded,
            "An empty Dropbox folder did not synchronize as a first-class NTFS folder.");
        remote.DeleteDirectory("empty-dropbox-folder");
        SyncAuthorityDeleteLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-directory-delete-marked-before-receipt")
                throw new IOException("simulated crash after empty-folder deletion");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityPairRunner(pair, remote, authority,
                "root-pair-runner-local", "root-pair-runner-dropbox", options,
                new PilotRunControl(), admission).RunAsync("Manual"));
        }
        finally { SyncAuthorityDeleteLocalExecutor.PhaseForTest = null; }
        var localDirectoryArtifact = (await authority.LoadRecoveryArtifactsAsync()).Single(item =>
            item.OriginalRelativePath == "empty-dropbox-folder" && item.Side == "local");
        Require(!Directory.Exists(Path.Combine(root, "empty-dropbox-folder")) &&
                localDirectoryArtifact.Size == 0 &&
                localDirectoryArtifact.IdentityJson.Contains("recreate_empty_directory", StringComparison.Ordinal),
            "The interrupted NTFS folder deletion did not retain its recreate recipe before removing the folder.");
        var recoveredDirectoryDelete = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RecoverAsync();
        Require(recoveredDirectoryDelete.Decisions.Single(item =>
                    item.RelativePath == "empty-dropbox-folder").Kind == SyncDecisionKind.Archived,
            "The interrupted empty-folder deletion was not completed by read-only reconciliation.");

        await File.WriteAllTextAsync(Path.Combine(root, "runner.txt"), "local runner update");
        var uploaded = await new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox", options, new PilotRunControl(), admission).RunAsync("Manual");
        Require(remote.ReadText("runner.txt") == "local runner update" &&
                uploaded.Decisions.Single(item => item.RelativePath == "runner.txt").Kind ==
                SyncDecisionKind.Uploaded,
            "The runtime runner did not execute and retain a local-to-Dropbox authority operation.");

        remote.Put("runner.txt", "Dropbox runner update");
        var downloaded = await new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox", options, new PilotRunControl(), admission).RunAsync("Scheduled");
        Require(await File.ReadAllTextAsync(Path.Combine(root, "runner.txt")) == "Dropbox runner update" &&
                downloaded.Decisions.Single(item => item.RelativePath == "runner.txt").Kind ==
                SyncDecisionKind.Downloaded,
            "The runtime runner did not execute and retain a Dropbox-to-local authority operation.");

        await File.WriteAllTextAsync(Path.Combine(root, "ready-only.txt"), "planned but never attempted");
        await File.WriteAllTextAsync(Path.Combine(root, "ready-page-2.txt"), "second recovery page");
        await File.WriteAllTextAsync(Path.Combine(root, "ready-page-3.txt"), "third recovery page");
        var uploadsBeforeReadyOnlyRecovery = remote.UploadCount;
        await new SyncAuthorityObservationCoordinator(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox").ObserveAsync("manual");
        var readyOnlyPlan = (await new SyncAuthorityReconciliationPlanner(authority, options, admission: admission).PlanAsync(pair.Id))
            .Single(item => item.RelativePath == "ready-only.txt");
        var readyOnlyOperationId = readyOnlyPlan.OperationId ??
                                   throw new InvalidOperationException("The ready-only plan had no operation ID.");
        Require(readyOnlyPlan.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                (await authority.LoadOperationAsync(readyOnlyOperationId)).Status ==
                SyncAuthorityOperationStatus.Ready,
            "The ready-only startup fixture did not retain an unattempted operation.");
        var readyOnlyQueue = (await authority.LoadQueueProjectionAsync())
            .Single(item => item.OperationId == readyOnlyOperationId);
        Require(readyOnlyQueue.PairId == pair.Id && readyOnlyQueue.RelativePath == "ready-only.txt" &&
                readyOnlyQueue.Operation == "Upload new" && readyOnlyQueue.State == "Queued" &&
                !readyOnlyQueue.RecoveryRequired,
            "The restart-safe queue projection did not truthfully expose the unattempted operation.");
        SyncAuthorityPairRunner.RecoveryPageSizeForTest = 1;
        SyncRunResult readyOnlyRecovery;
        try
        {
            readyOnlyRecovery = await new SyncAuthorityPairRunner(pair, remote, authority,
                "root-pair-runner-local", "root-pair-runner-dropbox", options,
                new PilotRunControl(), admission).RecoverAsync();
        }
        finally { SyncAuthorityPairRunner.RecoveryPageSizeForTest = null; }
        Require(remote.ReadText("ready-only.txt") == "planned but never attempted" &&
                remote.ReadText("ready-page-2.txt") == "second recovery page" &&
                remote.ReadText("ready-page-3.txt") == "third recovery page" &&
                remote.UploadCount == uploadsBeforeReadyOnlyRecovery + 3 &&
                readyOnlyRecovery.Decisions.Single(item => item.RelativePath == "ready-only.txt").Kind ==
                SyncDecisionKind.Uploaded &&
                (await authority.LoadOperationAsync(readyOnlyOperationId)).Status ==
                SyncAuthorityOperationStatus.Completed,
            "Startup recovery did not drain the durable pre-effect operation before new observation or planning.");
        var readyOnlyExecuted = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(remote.ReadText("ready-only.txt") == "planned but never attempted" &&
                remote.UploadCount == uploadsBeforeReadyOnlyRecovery + 3 &&
                readyOnlyExecuted.Decisions.Single(item => item.RelativePath == "ready-only.txt").Kind ==
                SyncDecisionKind.Identical,
            "The ordinary runner replayed an operation that startup recovery had already completed.");
        // The activity projection legitimately carries both history-event rows and the current
        // durable-state row for one operation, and a history row can legitimately sort first. The
        // requirement is that a completed durable-state record exists for this upload and that the
        // operation has left the queue, so select by content rather than by row order.
        var operationActivity = (await authority.LoadActivityProjectionAsync())
            .Where(item => item.OperationId == readyOnlyOperationId).ToArray();
        Require(operationActivity.Any(item => item.PairId == pair.Id &&
                item.RelativePath == "ready-only.txt" &&
                item.Outcome is "Uploaded" or "Completed" &&
                item.Reason.Contains("Current durable state: Complete.", StringComparison.Ordinal)) &&
            (await authority.LoadQueueProjectionAsync()).All(item => item.OperationId != readyOnlyOperationId),
            "The restart-safe projections did not move the verified upload from queue to completed activity.");

        await File.WriteAllTextAsync(Path.Combine(root, "unknown.txt"), "unknown runner create");
        remote.InterruptNextUploadAfterEffect = true;
        var unknown = await new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox", options, new PilotRunControl(), admission).RunAsync("Manual");
        Require(unknown.Decisions.Single(item => item.RelativePath == "unknown.txt").Kind ==
                SyncDecisionKind.ResponseUnknown,
            "The runtime runner did not surface an unknown provider response distinctly.");
        var unknownQueue = (await authority.LoadQueueProjectionAsync())
            .Single(item => item.RelativePath == "unknown.txt");
        Require(unknownQueue.State == "Effect unknown" && unknownQueue.RecoveryRequired &&
                (await authority.LoadActivityProjectionAsync()).Any(item =>
                    item.OperationId == unknownQueue.OperationId && item.Outcome == "Effect unknown"),
            "The restart-safe projections did not surface the interrupted provider response as recovery-first.");
        var uploadsAfterUnknown = remote.UploadCount;
        var recovered = await new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox", options, new PilotRunControl(), admission).RecoverAsync();
        Require(remote.UploadCount == uploadsAfterUnknown &&
                recovered.Decisions.Single(item => item.RelativePath == "unknown.txt").Kind ==
                SyncDecisionKind.Uploaded,
            "Startup recovery replayed an unknown add-only upload instead of reconciling it read-only.");

        await File.WriteAllTextAsync(Path.Combine(root, "request-sent.txt"),
            "request sent before upload");
        var uploadsBeforeRequestSentCrash = remote.UploadCount;
        SyncAuthorityCreateRemoteExecutor.PhaseForTest = phase =>
        {
            if (phase == "request-sent-before-upload")
                throw new IOException("simulated crash before remote upload");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityPairRunner(pair, remote, authority,
                "root-pair-runner-local", "root-pair-runner-dropbox", options,
                new PilotRunControl(), admission).RunAsync("Manual"));
        }
        finally { SyncAuthorityCreateRemoteExecutor.PhaseForTest = null; }
        var requestSentOperation = (await authority.ListRunnableOperationsAsync())
            .Single(item => item.PairId == pair.Id && item.Kind == SyncAuthorityOperationKind.CreateRemote &&
                            item.Status == SyncAuthorityOperationStatus.EffectIssuing);
        Require(!remote.Exists("request-sent.txt") && remote.UploadCount == uploadsBeforeRequestSentCrash,
            "The pre-upload request-sent crash fixture unexpectedly applied a Dropbox effect.");
        var requestSentRecovered = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RecoverAsync();
        Require(remote.Exists("request-sent.txt") &&
                remote.ReadText("request-sent.txt") == "request sent before upload" &&
                remote.UploadCount == uploadsBeforeRequestSentCrash + 1 &&
                requestSentRecovered.Decisions.Single(item => item.RelativePath == "request-sent.txt").Kind ==
                SyncDecisionKind.Uploaded &&
                (await authority.LoadOperationAsync(requestSentOperation.OperationId)).Attempts.Count == 2,
            "Startup recovery did not prove the pre-upload request absent before one bounded retry.");

        remote.Put("partial-local.txt", "interrupted local download");
        var downloadsBeforePartial = remote.DownloadCount;
        remote.InterruptNextDownload = true;
        await ExpectRejectAsync(() => new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual"));
        var partialQueueItem = (await authority.ListRunnableOperationsAsync())
            .Single(item => item.PairId == pair.Id && item.Kind == SyncAuthorityOperationKind.CreateLocal &&
                            item.Status == SyncAuthorityOperationStatus.EffectIssuing);
        var partialOperation = await authority.LoadOperationAsync(partialQueueItem.OperationId);
        var transactionAllocation = partialOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var transactionPath = Path.Combine(localControl.RootPath, transactionAllocation.DeterministicName);
        Require(!File.Exists(Path.Combine(root, "partial-local.txt")) &&
                Directory.Exists(transactionPath) &&
                Directory.EnumerateFileSystemEntries(transactionPath).Any() &&
                remote.DownloadCount == downloadsBeforePartial + 1,
            "The interrupted local-create fixture did not retain its exact owned partial stage.");
        var partialReconciled = await new SyncAuthorityCreateLocalReconciler(pair, authority)
            .ReconcileAsync(partialOperation.OperationId, "worker-pair-runner-partial-reconcile");
        partialOperation = await authority.LoadOperationAsync(partialOperation.OperationId);
        Require(partialReconciled.Status == SyncAuthorityOperationStatus.Blocked &&
                !partialReconciled.EffectDispatched && !File.Exists(Path.Combine(root, "partial-local.txt")) &&
                !Directory.Exists(transactionPath) &&
                partialOperation.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.StageFile).Status ==
                SyncAuthorityAllocationStatus.Blocked &&
                partialOperation.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).Status ==
                SyncAuthorityAllocationStatus.Released,
            "Interrupted local-create recovery did not remove only its identity-bound partial allocation.");
        var partialRetried = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Recovery");
        Require(await File.ReadAllTextAsync(Path.Combine(root, "partial-local.txt")) ==
                "interrupted local download" && remote.DownloadCount == downloadsBeforePartial + 2 &&
                partialRetried.Decisions.Single(item => item.RelativePath == "partial-local.txt").Kind ==
                SyncDecisionKind.Downloaded,
            "A fresh immutable local-create plan did not succeed after partial-stage cleanup.");

        remote.Put("committed-cleanup.txt", "committed local cleanup");
        var downloadsBeforeCommittedCleanup = remote.DownloadCount;
        SyncAuthorityCreateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "commit-before-cleanup")
                throw new IOException("simulated crash after commit before cleanup");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityPairRunner(pair, remote, authority,
                "root-pair-runner-local", "root-pair-runner-dropbox", options,
                new PilotRunControl(), admission).RunAsync("Manual"));
        }
        finally { SyncAuthorityCreateLocalExecutor.PhaseForTest = null; }
        var committedQueueItem = (await authority.ListRunnableOperationsAsync())
            .Single(item => item.PairId == pair.Id && item.Kind == SyncAuthorityOperationKind.CreateLocal &&
                            item.Status == SyncAuthorityOperationStatus.Committed);
        var committedOperation = await authority.LoadOperationAsync(committedQueueItem.OperationId);
        var committedTransaction = committedOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var committedTransactionPath = Path.Combine(localControl.RootPath, committedTransaction.DeterministicName);
        Require(Directory.Exists(committedTransactionPath) &&
                !Directory.EnumerateFileSystemEntries(committedTransactionPath).Any() &&
                committedOperation.Allocations.All(item =>
                    item.Status == SyncAuthorityAllocationStatus.Verified) &&
                remote.DownloadCount == downloadsBeforeCommittedCleanup + 1,
            "The post-commit crash did not retain its exact empty transaction cleanup boundary.");

        SyncAuthorityCommittedOperationFinalizer.PhaseForTest = phase =>
        {
            if (phase == "create-local-transaction-removed-before-authority-release")
                throw new IOException("simulated crash after cleanup before authority release");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityPairRunner(pair, remote, authority,
                "root-pair-runner-local", "root-pair-runner-dropbox", options,
                new PilotRunControl(), admission).RunAsync("Recovery"));
        }
        finally { SyncAuthorityCommittedOperationFinalizer.PhaseForTest = null; }
        committedOperation = await authority.LoadOperationAsync(committedOperation.OperationId);
        Require(committedOperation.Status == SyncAuthorityOperationStatus.CleanupPending &&
                !Directory.Exists(committedTransactionPath) &&
                committedOperation.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.StageFile).Status ==
                SyncAuthorityAllocationStatus.Released &&
                committedOperation.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).Status ==
                SyncAuthorityAllocationStatus.Verified,
            "The interrupted finalizer did not retain its physical-cleanup/authority-release boundary.");
        var finalized = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Recovery");
        committedOperation = await authority.LoadOperationAsync(committedOperation.OperationId);
        Require(committedOperation.Status == SyncAuthorityOperationStatus.Completed &&
                committedOperation.Allocations.All(item =>
                    item.Status == SyncAuthorityAllocationStatus.Released) &&
                remote.DownloadCount == downloadsBeforeCommittedCleanup + 1 &&
                await File.ReadAllTextAsync(Path.Combine(root, "committed-cleanup.txt")) ==
                "committed local cleanup" &&
                finalized.Decisions.Single(item => item.RelativePath == "committed-cleanup.txt").Kind ==
                SyncDecisionKind.Downloaded,
            "Startup did not finish committed local-create cleanup without repeating the download.");

        var firstMonitorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistedBeforeFirstSignal = false;
        await using (var monitor = new SyncAuthorityLocalMonitor(pair, authority, "root-pair-runner-local",
                         async _ =>
                         {
                             persistedBeforeFirstSignal = await ScalarLongAsync(authority.DatabasePath,
                                 "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner';") > 0 &&
                                 await ScalarLongAsync(authority.DatabasePath,
                                     "SELECT count(*) FROM dirty_item WHERE pair_id='pair-runner' AND status='dirty';") > 0;
                             firstMonitorSignal.TrySetResult();
                         }))
        {
            await monitor.StartAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "monitor-first.txt"), "first durable hint");
            await firstMonitorSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(persistedBeforeFirstSignal,
                "The local watcher signalled reconciliation before its monitor event and dirty hint were durable.");
        }

        var monitorEventsAfterFirstRun = await ScalarLongAsync(authority.DatabasePath,
            "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner';");
        var secondMonitorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var restartedMonitor = new SyncAuthorityLocalMonitor(pair, authority,
                         "root-pair-runner-local", _ =>
                         {
                             secondMonitorSignal.TrySetResult();
                             return Task.CompletedTask;
                         }))
        {
            await restartedMonitor.StartAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "monitor-second.txt"), "second durable hint");
            await secondMonitorSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM monitor_cursor WHERE pair_id='pair-runner' AND kind='directory_changes' AND status='active';") == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner';") > monitorEventsAfterFirstRun,
            "A restarted local monitor did not resume one identity-bound cursor and append new durable hints.");

        var remoteMonitorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCursorAdvancedBeforeSignal = false;
        await using (var remoteMonitor = new SyncAuthorityDropboxMonitor(pair, remote, remote, authority,
                         "root-pair-runner-dropbox", async _ =>
                         {
                             var cursor = await authority.LoadMonitorCursorAsync(
                                 "monitor-dropbox-" + TestHash(pair.Id, "root-pair-runner-dropbox")[..32]);
                             remoteCursorAdvancedBeforeSignal = cursor?.CursorValue == remote.CurrentCursor &&
                                 await ScalarLongAsync(authority.DatabasePath,
                                     "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner' AND event_kind='dropbox_file';") > 0;
                             remoteMonitorSignal.TrySetResult();
                         }, TimeSpan.FromMilliseconds(20)))
        {
            await remoteMonitor.StartAsync();
            remote.Put("monitor-dropbox.txt", "durable Dropbox hint");
            await remoteMonitorSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(remoteCursorAdvancedBeforeSignal,
                "The Dropbox monitor signalled reconciliation before atomically persisting its page and cursor.");
        }
        var remoteEventsAfterFirstRun = await ScalarLongAsync(authority.DatabasePath,
            "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner' AND event_kind LIKE 'dropbox_%';");
        var restartedRemoteSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var restartedRemoteMonitor = new SyncAuthorityDropboxMonitor(pair, remote, remote, authority,
                         "root-pair-runner-dropbox", _ =>
                         {
                             restartedRemoteSignal.TrySetResult();
                             return Task.CompletedTask;
                         }, TimeSpan.FromMilliseconds(20)))
        {
            await restartedRemoteMonitor.StartAsync();
            remote.Put("monitor-dropbox-restart.txt", "restart cursor hint");
            await restartedRemoteSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM monitor_cursor WHERE pair_id='pair-runner' AND kind='dropbox_cursor' AND status='active';") == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner' AND event_kind LIKE 'dropbox_%';") > remoteEventsAfterFirstRun,
            "A restarted Dropbox monitor did not resume its durable cursor and append the next page.");

        var unavailableMonitorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredMonitorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.InterruptNextChangePoll = true;
        await using (var faultedRemoteMonitor = new SyncAuthorityDropboxMonitor(pair, remote, remote, authority,
                         "root-pair-runner-dropbox", _ => Task.CompletedTask, TimeSpan.FromMilliseconds(20),
                         async _ =>
                         {
                             var state = (await authority.LoadMonitorProjectionAsync())
                                 .Single(item => item.PairId == pair.Id && item.Kind == "dropbox_cursor").State;
                             if (state == "Monitor unavailable") unavailableMonitorSignal.TrySetResult();
                             if (state == "Monitoring") recoveredMonitorSignal.TrySetResult();
                         }))
        {
            await faultedRemoteMonitor.StartAsync();
            await unavailableMonitorSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await recoveredMonitorSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Require((await authority.LoadMonitorCursorAsync(
                    "monitor-dropbox-" + TestHash(pair.Id, "root-pair-runner-dropbox")[..32]))?.Status == "active",
            "The Dropbox monitor did not preserve and reactivate its exact cursor after a transport failure.");

        var resetSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.ResetNextCursor = true;
        await using (var resetMonitor = new SyncAuthorityDropboxMonitor(pair, remote, remote, authority,
                         "root-pair-runner-dropbox", _ =>
                         {
                             resetSignal.TrySetResult();
                             return Task.CompletedTask;
                         }, TimeSpan.FromMilliseconds(20)))
        {
            await resetMonitor.StartAsync();
            await resetSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner' AND event_kind='cursor_reset';") == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM dirty_item WHERE pair_id='pair-runner' AND normalized_path_key IS NULL AND status='dirty';") == 1,
            "A Dropbox cursor reset did not durably force whole-pair reconciliation.");

        await new SyncAuthorityPairRunner(pair, remote, authority, "root-pair-runner-local",
            "root-pair-runner-dropbox", options, new PilotRunControl(), admission).RunAsync("Monitor");
        Require(await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM monitor_event WHERE pair_id='pair-runner' AND consumed_utc IS NULL;") == 0 &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM dirty_item WHERE pair_id='pair-runner' AND status='dirty';") == 0,
            "A complete monitor-triggered reconciliation did not close only its durable input hints.");

        await AssertDeletionOutcomeRetentionAsync(pair, remote, authority, options, root, admission);
    }

    private static async Task AssertDeletionOutcomeRetentionAsync(SyncPair pair, FakeSyncRemoteStore remote,
        SyncAuthorityDatabase authority, SyncAuthorityPlannerOptions options, string root, SyncAuthorityCommandAdmission admission)
    {
        var retention = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase)
        {
            ["retained-archive.txt"] = new SyncDecision("retained-archive.txt", SyncDecisionKind.Archived,
                "The recoverable deletion completed in this run.")
        };
        SyncAuthorityPairRunner.RetainPlannerOutcome(retention,
            new SyncAuthorityPlannerOutcome("retained-archive.txt", "object-retained-archive",
                SyncAuthorityPlannerOutcomeKind.Tombstone, null, null, "tombstone-retained-archive",
                "Both complete observations prove absence; no delete effect is required.", true));
        Require(retention["retained-archive.txt"].Kind == SyncDecisionKind.Archived,
            "A resolved both-absent confirmation overwrote a verified terminal Archived outcome.");
        SyncAuthorityPairRunner.RetainPlannerOutcome(retention,
            new SyncAuthorityPlannerOutcome("retained-archive.txt", "object-retained-archive",
                SyncAuthorityPlannerOutcomeKind.Blocked, null, null, null,
                "A distinct later deletion precondition failed."));
        Require(retention["retained-archive.txt"].Kind == SyncDecisionKind.Blocked &&
                retention["retained-archive.txt"].Reason == "A distinct later deletion precondition failed.",
            "A verified Archived outcome hid a genuinely new later blocker.");

        async Task SeedDeletionBaselineAsync(string name, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), content);
            remote.Put(name, content);
            var seeded = await new SyncAuthorityPairRunner(pair, remote, authority,
                "root-pair-runner-local", "root-pair-runner-dropbox", options,
                new PilotRunControl(), admission).RunAsync("Manual");
            Require(seeded.Decisions.Single(item => item.RelativePath == name).Kind ==
                    SyncDecisionKind.Baseline,
                $"The deletion-result fixture {name} did not establish an effect-free baseline.");
        }

        const string currentRunDelete = "delete-current-run.txt";
        await SeedDeletionBaselineAsync(currentRunDelete, "current-run archive");
        File.Delete(Path.Combine(root, currentRunDelete));
        var archivesBeforeCurrentRun = remote.ArchiveCount;
        var deletesBeforeCurrentRun = remote.DeleteCount;
        var currentRunArchived = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(currentRunArchived.Decisions.Single(item => item.RelativePath == currentRunDelete).Kind ==
                    SyncDecisionKind.Archived && !remote.Exists(currentRunDelete) &&
                remote.ArchiveCount == archivesBeforeCurrentRun + 1 &&
                remote.DeleteCount == deletesBeforeCurrentRun + 1,
            "A current-run Archived outcome was downgraded by its same-run both-absent confirmation.");
        var currentRunRerun = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(currentRunRerun.Decisions.Single(item => item.RelativePath == currentRunDelete).Kind ==
                    SyncDecisionKind.Blocked &&
                remote.ArchiveCount == archivesBeforeCurrentRun + 1 &&
                remote.DeleteCount == deletesBeforeCurrentRun + 1,
            "A later both-absent run without a current/recovered Archived result either escaped fail-closed or repeated deletion.");

        const string recoveredDelete = "delete-recovered.txt";
        await SeedDeletionBaselineAsync(recoveredDelete, "recovered archive");
        File.Delete(Path.Combine(root, recoveredDelete));
        var archivesBeforeRecovery = remote.ArchiveCount;
        var deletesBeforeRecovery = remote.DeleteCount;
        remote.InterruptNextDeleteAfterEffect = true;
        var effectUnknownDelete = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(effectUnknownDelete.Decisions.Single(item => item.RelativePath == recoveredDelete).Kind ==
                    SyncDecisionKind.ResponseUnknown && !remote.Exists(recoveredDelete) &&
                remote.ArchiveCount == archivesBeforeRecovery + 1 &&
                remote.DeleteCount == deletesBeforeRecovery + 1,
            "The interrupted deletion fixture did not retain one effect-unknown delete and one recovery copy.");
        var recoveredArchived = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Recovery");
        Require(recoveredArchived.Decisions.Single(item => item.RelativePath == recoveredDelete).Kind ==
                    SyncDecisionKind.Archived &&
                remote.ArchiveCount == archivesBeforeRecovery + 1 &&
                remote.DeleteCount == deletesBeforeRecovery + 1,
            "Recovered Archived was downgraded by both-absent confirmation or repeated its recovery/delete effects.");

        const string unprovedBothAbsent = "delete-unproved-both-absent.txt";
        await SeedDeletionBaselineAsync(unprovedBothAbsent, "unproved both absent");
        File.Delete(Path.Combine(root, unprovedBothAbsent));
        remote.Delete(unprovedBothAbsent);
        var archivesBeforeUnproved = remote.ArchiveCount;
        var deletesBeforeUnproved = remote.DeleteCount;
        var unprovedResult = await new SyncAuthorityPairRunner(pair, remote, authority,
            "root-pair-runner-local", "root-pair-runner-dropbox", options,
            new PilotRunControl(), admission).RunAsync("Manual");
        Require(unprovedResult.Decisions.Single(item => item.RelativePath == unprovedBothAbsent).Kind ==
                    SyncDecisionKind.Blocked &&
                remote.ArchiveCount == archivesBeforeUnproved && remote.DeleteCount == deletesBeforeUnproved,
            "Both-absent state without a current or recovered Archived result did not remain fail-closed.");

    }

    private static string TestHash(params string[] parts) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts))))
        .ToLowerInvariant();

    // Caller-owned fixture command: no global cache, ambient lookup or per-operation minting.
    private static async Task<SyncAuthorityCommandAdmission> AdmitFixturePairsAsync(
        SyncAuthorityDatabase authority, params string[] pairIds)
    {
        if (pairIds.Length == 0 || pairIds.Distinct(StringComparer.Ordinal).Count() != pairIds.Length)
            throw new InvalidOperationException("The fixture must declare its exact nonempty pair scope.");
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null,
            pairIds.Length == 1 ? "run_pair" : "run_all", pairIds.Length == 1 ? pairIds[0] : null,
            null, null, JsonSerializer.Serialize(new { pairIds }), SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        return admission;
    }

    private static async Task ExpectRejectAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (SqliteException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("An authority mutation unexpectedly bypassed its fail-closed guard.");
    }

    private static async Task ExpectExceptionAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static async Task ExpectSqliteRejectAsync(string databasePath, string sql)
    {
        try
        {
            await ExecuteAsync(databasePath, sql);
        }
        catch (SqliteException)
        {
            return;
        }
        throw new InvalidOperationException("A direct SQLite mutation unexpectedly bypassed an authority trigger.");
    }

    private static async Task ExecuteAsync(string databasePath, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(string databasePath, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<string>> QueryStringsAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await SyncAuthorityDatabase.ConfigureAuthorityConnectionAsync(connection);
        return connection;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class NoProviderCallsHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Installed-state migration attempted a provider call.");
        }
    }
    /// <summary>
    /// The H7/G-2, M6/G-3 and H8/G-1 recovery-correction proofs, run through the real executors, the real
    /// reconciler and the real authority database over the isolated NTFS pair this fixture already built.
    /// Every case asserts the exact recovered file, the exact retained authority and the absence of a repeated
    /// provider effect; a failure note alone is never accepted as proof.
    /// </summary>
    private static async Task AssertRecoveryConsumerCorrectionsAsync(SyncPair boundPair,
        SyncAuthorityDatabase authority, FakeSyncRemoteStore remote, string localRoot,
        SyncAuthorityReconciliationPlanner planner, SyncAuthorityObservationCoordinator coordinator)
    {
        var localPath = Path.Combine(localRoot, "local.txt");
        var downloadPath = Path.Combine(localRoot, "download.txt");
        // Each confirmed boundary can be reproduced and proved on its own through this same fixed selector;
        // the default runs every case.
        var correctionGroup = Environment.GetEnvironmentVariable("SYNCSHARK_RECOVERY_CORRECTION_GROUP") ?? "all";
        if (correctionGroup is not ("all" or "h7" or "m6" or "h8"))
            throw new ArgumentOutOfRangeException(nameof(correctionGroup), correctionGroup,
                "The requested recovery-correction group is one of all, h7, m6 or h8.");
        if (correctionGroup is "all" or "h7")
        {

        // H7/G-2: a remote replacement interrupted after its predecessor recovery was verified and recorded is
        // the engine's OWN legitimate retained Validating state. It must resume under the existing lease,
        // fence, source-revision and recovery-allocation checks, commit exactly one replacement without
        // repeating the recovery copy, and refuse with no provider effect when the exact local source changed.
        await File.WriteAllTextAsync(localPath, "validating retained replacement");
        await coordinator.ObserveAsync("monitor");
        var validatingPlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == "local.txt");
        var archivesBeforeValidating = remote.ArchiveCount;
        var uploadsBeforeValidating = remote.UploadCount;
        SyncAuthorityUpdateRemoteExecutor.PhaseForTest = phase =>
        {
            if (string.Equals(phase, "remote-update-recovery-retained-before-replacement", StringComparison.Ordinal))
                throw new IOException("simulated process boundary after the predecessor recovery");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
                .ExecuteAsync(validatingPlan.OperationId!, "worker-validating-boundary"));
        }
        finally { SyncAuthorityUpdateRemoteExecutor.PhaseForTest = null; }
        var retainedValidating = await authority.LoadOperationAsync(validatingPlan.OperationId!);
        Require(retainedValidating.Status == SyncAuthorityOperationStatus.Validating &&
                retainedValidating.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess &&
                retainedValidating.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Verified &&
                remote.ArchiveCount == archivesBeforeValidating + 1 &&
                remote.UploadCount == uploadsBeforeValidating,
            "The interrupted remote replacement did not retain the legitimate Validating recovery state this engine itself produces.");
        var validatingResumed = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(validatingPlan.OperationId!, "worker-validating-resume");
        Require(validatingResumed.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("local.txt") == "validating retained replacement" &&
                remote.UploadCount == uploadsBeforeValidating + 1 &&
                remote.ArchiveCount == archivesBeforeValidating + 1 &&
                (await authority.LoadOperationAsync(validatingPlan.OperationId!)).Attempts
                    .Count(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) == 2,
            "The retained Validating remote replacement was not resumed exactly once without repeating its recovery copy.");
        Console.WriteLine("PASS H7/G-2: the retained Validating remote replacement resumed exactly once; its recovery copy was not repeated");
        }

        if (correctionGroup is "all" or "m6")
        {
        // M6/G-3: recording a failure while the operation remains effect_unknown must preserve the existing
        // transition authority and the original uncertainty rather than replacing the retained failure with a
        // database error, and the later online reconciliation must still converge.
        await File.WriteAllTextAsync(localPath, "effect unknown failure recording");
        await coordinator.ObserveAsync("monitor");
        var unknownPlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == "local.txt");
        var archivesBeforeUnknownFailure = remote.ArchiveCount;
        remote.InterruptNextArchiveAfterEffect = true;
        var unknownResult = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(unknownPlan.OperationId!, "worker-unknown-failure");
        Require(unknownResult.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                remote.ArchiveCount == archivesBeforeUnknownFailure + 1,
            "The lost recovery-copy response was not retained as an unknown effect.");
        var failureLease = await ScalarStringAsync(authority.DatabasePath,
            "SELECT last_transition_lease_id FROM operation WHERE operation_id='" + unknownPlan.OperationId + "';");
        var failureFence = await ScalarStringAsync(authority.DatabasePath,
            "SELECT last_transition_fencing_token FROM operation WHERE operation_id='" + unknownPlan.OperationId + "';");
        var failureRecorded = await authority.RetainExecutionFailureAsync(unknownPlan.OperationId!,
            "worker-unknown-failure", new IOException("recorded secondary failure"), CancellationToken.None);
        Require(failureRecorded.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                (await authority.LoadOperationAsync(unknownPlan.OperationId!)).Status ==
                    SyncAuthorityOperationStatus.EffectUnknown &&
                await ScalarStringAsync(authority.DatabasePath,
                    "SELECT last_transition_lease_id FROM operation WHERE operation_id='" +
                    unknownPlan.OperationId + "';") == failureLease &&
                await ScalarStringAsync(authority.DatabasePath,
                    "SELECT last_transition_fencing_token FROM operation WHERE operation_id='" +
                    unknownPlan.OperationId + "';") == failureFence &&
                (await ScalarStringAsync(authority.DatabasePath,
                    "SELECT block_detail FROM operation WHERE operation_id='" + unknownPlan.OperationId + "';"))
                    ?.Contains("recorded secondary failure", StringComparison.Ordinal) == true &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM history_event WHERE operation_id='" + unknownPlan.OperationId +
                    "' AND event_kind='execution_failed';") == 1,
            "Recording a failure while the outcome stayed effect_unknown rewrote transition authority or lost the exact failure.");
        var unknownReconciled = await new SyncAuthorityUpdateRemoteReconciler(boundPair, remote, authority)
            .ReconcileAsync(unknownPlan.OperationId!, "worker-unknown-reconcile");
        Require(unknownReconciled.Status == SyncAuthorityOperationStatus.Ready &&
                !unknownReconciled.EffectDispatched,
            "The preserved unknown outcome was not reconciled to its exact retryable boundary.");
        var unknownResumed = await new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(unknownPlan.OperationId!, "worker-unknown-resume");
        Require(unknownResumed.Status == SyncAuthorityOperationStatus.Completed &&
                remote.ReadText("local.txt") == "effect unknown failure recording" &&
                remote.ArchiveCount == archivesBeforeUnknownFailure + 1,
            "Later online reconciliation did not converge without repeating the retained recovery copy.");
        Console.WriteLine("PASS M6/G-3: a same-status effect_unknown failure kept its transition authority, retained the exact failure and still converged");
        await AssertRemoteOutcomeWithChangedLocalSourceAsync(boundPair, authority, remote, localRoot,
            planner, coordinator);
        }

        if (correctionGroup is "all" or "h8")
        {
        // H8/G-1: a local replacement interrupted after the owned predecessor was moved into its recovery
        // location, with the cloud source then advanced, must examine the retained local state first: restore
        // the exact owned predecessor when the visible target is missing, retain the reviewed stage, promote
        // nothing stale and report the current file for a fresh comparison.
        var predecessorBytes = await File.ReadAllTextAsync(downloadPath);
        remote.Put("download.txt", "advanced drift successor one");
        await coordinator.ObserveAsync("monitor");
        var driftPlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == "download.txt");
        var driftOperation = await authority.LoadOperationAsync(driftPlan.OperationId!);
        Require(driftOperation.Kind == SyncAuthorityOperationKind.UpdateLocal &&
                driftOperation.Allocations.Count == 3,
            "The advanced Dropbox revision was not planned as one local replacement with transaction, stage and predecessor-recovery authority.");
        var driftTransaction = driftOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var driftStage = driftOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var driftRecovery = driftOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-update-predecessor-retained-before-receipt")
                throw new IOException("simulated local predecessor retention crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(driftPlan.OperationId!, "worker-drift-retention-crash"));
        }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        Require(!File.Exists(downloadPath) &&
                (await authority.LoadOperationAsync(driftPlan.OperationId!)).Status ==
                    SyncAuthorityOperationStatus.EffectIssuing,
            "The post-retention local replacement crash did not preserve its exact retained boundary.");
        remote.Put("download.txt", "advanced drift successor two");
        var driftReconciled = await new SyncAuthorityUpdateLocalReconciler(boundPair, remote, authority)
            .ReconcileAsync(driftPlan.OperationId!, "worker-drift-reconcile");
        Require(driftReconciled.Status == SyncAuthorityOperationStatus.Blocked &&
                !driftReconciled.EffectDispatched,
            "An advanced cloud source was not reported as a precise retained refusal: " + driftReconciled.Reason);
        Require(await File.ReadAllTextAsync(downloadPath) == predecessorBytes,
            "The exact owned predecessor was not restored to the visible target.");
        Require(remote.ReadText("download.txt") == "advanced drift successor two",
            "The newer cloud authority was overwritten while the local replacement was reconciled.");
        var driftControlRoot = Path.Combine(boundPair.ControlNamespace!.Local.RootPath,
            driftTransaction.DeterministicName);
        Require(File.Exists(Path.Combine(driftControlRoot, driftStage.DeterministicName)),
            "The stale reviewed stage was not retained after the cloud source advanced.");
        Require(!File.Exists(Path.Combine(driftControlRoot, driftRecovery.DeterministicName)),
            "The unregistered predecessor recovery object was not moved back to the visible target.");
        Require((await authority.LoadOperationAsync(driftPlan.OperationId!)).Attempts.Single().Status ==
                SyncAuthorityAttemptStatus.Blocked,
            "The advanced-source refusal was not retained as one exact blocked attempt.");
        Console.WriteLine("PASS H8/G-1: an advanced cloud source restored the exact owned predecessor, retained the reviewed stage and promoted no stale content");
        await AssertPromotedTargetDriftAsync(boundPair, authority, remote, localRoot, planner, coordinator);
        await AssertRegisteredRecoveryDriftAsync(boundPair, authority, remote, localRoot, planner, coordinator);
        }

        if (correctionGroup is "all" or "h7")
        {
        // H7/G-2 rejection: the same legitimate Validating state must refuse with NO provider effect when its
        // exact local source revision is no longer present.
        await File.WriteAllTextAsync(localPath, "refusal replacement source");
        await coordinator.ObserveAsync("monitor");
        var refusalPlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == "local.txt");
        var archivesBeforeRefusal = remote.ArchiveCount;
        var uploadsBeforeRefusal = remote.UploadCount;
        SyncAuthorityUpdateRemoteExecutor.PhaseForTest = phase =>
        {
            if (string.Equals(phase, "remote-update-recovery-retained-before-replacement", StringComparison.Ordinal))
                throw new IOException("simulated process boundary after the predecessor recovery");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
                .ExecuteAsync(refusalPlan.OperationId!, "worker-refusal-boundary"));
        }
        finally { SyncAuthorityUpdateRemoteExecutor.PhaseForTest = null; }
        Require((await authority.LoadOperationAsync(refusalPlan.OperationId!)).Status ==
                SyncAuthorityOperationStatus.Validating,
            "The rejection fixture did not retain its legitimately Validating boundary.");
        File.Delete(localPath);
        await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(refusalPlan.OperationId!, "worker-refusal-validating"));
        Require(remote.ArchiveCount == archivesBeforeRefusal + 1 &&
                remote.UploadCount == uploadsBeforeRefusal,
            "A Validating remote replacement without its exact local source issued a provider effect instead of refusing.");
        Console.WriteLine("PASS H7/G-2 rejection: a Validating replacement without its exact source refused with zero provider effect");
        }
    }

    // M6: inputs come from the existing fake remote; actual planning, dispatch, lost-response retention,
    // reconciliation and fresh-effect rejection remain the production authority/executor paths.
    private static async Task AssertRemoteOutcomeWithChangedLocalSourceAsync(SyncPair pair,
        SyncAuthorityDatabase authority, FakeSyncRemoteStore remote, string localRoot,
        SyncAuthorityReconciliationPlanner planner, SyncAuthorityObservationCoordinator coordinator)
    {
        var cases = new[]
        {
            (Name: "copy-changed", Copy: true, Applied: true, Missing: false, RemoteAdvanced: false),
            (Name: "copy-missing", Copy: true, Applied: true, Missing: true, RemoteAdvanced: false),
            (Name: "copy-not-applied", Copy: true, Applied: false, Missing: true, RemoteAdvanced: false),
            (Name: "upload-changed", Copy: false, Applied: true, Missing: false, RemoteAdvanced: false),
            (Name: "upload-missing", Copy: false, Applied: true, Missing: true, RemoteAdvanced: false),
            (Name: "upload-not-applied", Copy: false, Applied: false, Missing: true, RemoteAdvanced: false),
            (Name: "upload-ambiguous", Copy: false, Applied: true, Missing: true, RemoteAdvanced: true)
        };
        foreach (var test in cases)
        {
            var path = "m6-" + test.Name + ".txt";
            var physical = Path.Combine(localRoot, path);
            var predecessor = "initial " + test.Name;
            var issuedSource = "issued replacement " + test.Name;
            var laterLocal = "later independent local edit " + test.Name;
            var laterCloud = "later independent cloud edit " + test.Name;
            await File.WriteAllTextAsync(physical, predecessor);
            await coordinator.ObserveAsync("monitor");
            var create = (await planner.PlanAsync(pair.Id)).Single(item => item.RelativePath == path);
            Require((await authority.LoadOperationAsync(create.OperationId!)).Kind == SyncAuthorityOperationKind.CreateRemote,
                "M6 setup did not plan a real initial add-only file: " + test.Name);
            var created = await new SyncAuthorityCreateRemoteExecutor(pair, remote, authority)
                .ExecuteAsync(create.OperationId!, "worker-m6-create-" + test.Name);
            Require(created.Status == SyncAuthorityOperationStatus.Completed && remote.ReadText(path) == predecessor,
                "M6 setup did not commit its real initial file: " + test.Name);

            await File.WriteAllTextAsync(physical, issuedSource);
            await coordinator.ObserveAsync("monitor");
            var update = (await planner.PlanAsync(pair.Id)).Single(item => item.RelativePath == path);
            Require((await authority.LoadOperationAsync(update.OperationId!)).Kind == SyncAuthorityOperationKind.UpdateRemote,
                "M6 setup did not plan a real conditional replacement: " + test.Name);
            var archivesBefore = remote.ArchiveCount;
            var uploadsBefore = remote.UploadCount;
            if (test.Copy)
            {
                remote.InterruptNextArchiveAfterEffect = test.Applied;
                remote.InterruptNextArchiveBeforeEffect = !test.Applied;
            }
            else
            {
                remote.InterruptNextUploadAfterEffect = test.Applied;
                remote.InterruptNextUploadBeforeEffect = !test.Applied;
            }
            if (test.Copy && !test.Applied)
            {
                await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(pair, remote, authority)
                    .ExecuteAsync(update.OperationId!, "worker-m6-issue-" + test.Name));
                Require((await authority.LoadOperationAsync(update.OperationId!)).Status ==
                        SyncAuthorityOperationStatus.EffectIssuing,
                    "M6 pre-copy interruption did not retain the actual issued request: " + test.Name);
            }
            else
            {
                var unknown = await new SyncAuthorityUpdateRemoteExecutor(pair, remote, authority)
                    .ExecuteAsync(update.OperationId!, "worker-m6-issue-" + test.Name);
                Require(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown,
                    "M6 fixture did not retain its actual lost response: " + test.Name);
            }
            if (test.Missing) File.Delete(physical);
            else await File.WriteAllTextAsync(physical, laterLocal);
            if (test.RemoteAdvanced) remote.Put(path, laterCloud);

            var archivesAfterIssue = remote.ArchiveCount;
            var uploadsAfterIssue = remote.UploadCount;
            Require(archivesAfterIssue == archivesBefore + (test.Copy && !test.Applied ? 0 : 1) &&
                    uploadsAfterIssue == uploadsBefore + (!test.Copy && test.Applied ? 1 : 0),
                "M6 lost-response fixture did not issue only its exact expected effects: " + test.Name);
            var reconciled = await new SyncAuthorityUpdateRemoteReconciler(pair, remote, authority)
                .ReconcileAsync(update.OperationId!, "worker-m6-reconcile-" + test.Name);
            var expectedStatus = test.RemoteAdvanced ? SyncAuthorityOperationStatus.Blocked :
                test.Copy || !test.Applied ? SyncAuthorityOperationStatus.Ready :
                SyncAuthorityOperationStatus.Completed;
            Require(reconciled.Status == expectedStatus && !reconciled.EffectDispatched &&
                    remote.ArchiveCount == archivesAfterIssue && remote.UploadCount == uploadsAfterIssue &&
                    remote.ReadText(path) == (test.RemoteAdvanced ? laterCloud :
                        !test.Copy && test.Applied ? issuedSource : predecessor) &&
                    (test.Missing ? !File.Exists(physical) : await File.ReadAllTextAsync(physical) == laterLocal),
                "M6 reconciliation lost source drift, replayed an effect or misclassified remote evidence: " + test.Name);
            var retained = await authority.LoadOperationAsync(update.OperationId!);
            Require(retained.Attempts.OrderBy(item => item.AttemptNumber).Last().Status ==
                    (test.RemoteAdvanced ? SyncAuthorityAttemptStatus.Blocked :
                    test.Applied ? SyncAuthorityAttemptStatus.VerifiedSuccess :
                    SyncAuthorityAttemptStatus.VerifiedNotApplied),
                "M6 reconciliation did not retain the exact issued attempt classification: " + test.Name);
            if (expectedStatus == SyncAuthorityOperationStatus.Ready)
            {
                await ExpectRejectAsync(() => new SyncAuthorityUpdateRemoteExecutor(pair, remote, authority)
                    .ExecuteAsync(update.OperationId!, "worker-m6-stale-retry-" + test.Name));
                Require(remote.ArchiveCount == archivesAfterIssue && remote.UploadCount == uploadsAfterIssue &&
                        remote.ReadText(path) == predecessor &&
                        (test.Missing ? !File.Exists(physical) : await File.ReadAllTextAsync(physical) == laterLocal),
                    "M6 outcome reconciliation authorised a stale fresh transfer: " + test.Name);
            }
            Console.WriteLine("PASS M6 source drift " + test.Name +
                ": exact issued outcome classified; later source/cloud state preserved; no transfer replay or stale retry");
        }
    }

    /// <summary>
    /// U4 (R3/H8 branch a): a local replacement interrupted AFTER its stage was promoted to the visible target
    /// and after its predecessor recovery was registered, with the cloud source then advanced. The promoted
    /// target is local content that already exists, so it must be preserved exactly as found, the registered
    /// recovery artifact and its Verified allocation must be preserved exactly, nothing may be promoted or
    /// re-promoted over the newer cloud authority, and the file must be reported as one exact retained refusal.
    /// </summary>
    private static async Task AssertPromotedTargetDriftAsync(SyncPair boundPair, SyncAuthorityDatabase authority,
        FakeSyncRemoteStore remote, string localRoot, SyncAuthorityReconciliationPlanner planner,
        SyncAuthorityObservationCoordinator coordinator)
    {
        const string relativePath = "promote-drift.txt";
        const string baseText = "promote drift base bytes";
        const string promotedText = "promote drift successor one";
        var targetPath = Path.Combine(localRoot, relativePath);
        await File.WriteAllTextAsync(targetPath, baseText);
        await coordinator.ObserveAsync("monitor");
        var baselinePlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == relativePath);
        await new SyncAuthorityCreateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(baselinePlan.OperationId!, "worker-promote-drift-baseline");
        Require(remote.ReadText(relativePath) == baseText,
            "The promote-drift baseline file was not committed on both sides.");
        remote.Put(relativePath, promotedText);
        await coordinator.ObserveAsync("monitor");
        var promotionPlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == relativePath);
        var promotionOperation = await authority.LoadOperationAsync(promotionPlan.OperationId!);
        Require(promotionOperation.Kind == SyncAuthorityOperationKind.UpdateLocal &&
                promotionOperation.Allocations.Count == 3,
            "The promote-drift change was not planned as one local replacement with transaction, stage and predecessor-recovery authority.");
        var transactionAllocation = promotionOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var recoveryAllocation = promotionOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-update-stage-promoted-before-receipt")
                throw new IOException("simulated post-promotion crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(promotionPlan.OperationId!, "worker-promote-drift-crash"));
        }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        Require(await File.ReadAllTextAsync(targetPath) == promotedText &&
                (await authority.LoadOperationAsync(promotionPlan.OperationId!)).Status ==
                    SyncAuthorityOperationStatus.EffectIssuing,
            "The post-promotion crash did not preserve its exact promoted-target boundary.");
        remote.Put(relativePath, "promote drift successor two");
        var reconciled = await new SyncAuthorityUpdateLocalReconciler(boundPair, remote, authority)
            .ReconcileAsync(promotionPlan.OperationId!, "worker-promote-drift-reconcile");
        Require(reconciled.Status == SyncAuthorityOperationStatus.Blocked && !reconciled.EffectDispatched,
            "An advanced cloud source with a promoted visible target was not reported as a precise retained refusal: " + reconciled.Reason);
        Require(await File.ReadAllTextAsync(targetPath) == promotedText,
            "The visible promoted target bytes were not preserved exactly after the cloud source advanced.");
        Require(remote.ReadText(relativePath) == "promote drift successor two",
            "The newer cloud authority was overwritten while the promoted target was reconciled.");
        var controlRoot = Path.Combine(boundPair.ControlNamespace!.Local.RootPath,
            transactionAllocation.DeterministicName);
        var recoveryPath = Path.Combine(controlRoot, recoveryAllocation.DeterministicName);
        Require(File.Exists(recoveryPath) && await File.ReadAllTextAsync(recoveryPath) == baseText,
            "The registered predecessor recovery artifact was not preserved exactly.");
        var retained = await authority.LoadOperationAsync(promotionPlan.OperationId!);
        Require(retained.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Verified &&
                retained.Attempts.Single(item => item.EffectKind == "local-promote-stage").Status ==
                SyncAuthorityAttemptStatus.Blocked &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM recovery_artifact WHERE operation_id='" + promotionPlan.OperationId +
                    "';") == 1,
            "The promoted-target refusal did not preserve its exact recovery allocation, artifact and attempt history.");
        Console.WriteLine("PASS U4(a)/H8: an advanced cloud source with a promoted visible target preserved the exact promoted bytes and the registered recovery, and promoted nothing stale");
    }

    /// <summary>
    /// U4 (R3/H8 branch b): the real crash window between a verified predecessor-recovery registration and the
    /// promotion dispatch - the predecessor is retained under a Verified allocation with its registered
    /// recovery artifact, the visible target is absent and the reviewed stage is intact - with the cloud source
    /// then advanced. Nothing may be promoted or re-promoted over the newer cloud authority, the registered
    /// recovery artifact must be preserved exactly because it is the only retained copy, the reviewed stage must
    /// stay retained, and the file must be reported as one exact retained refusal.
    /// </summary>
    private static async Task AssertRegisteredRecoveryDriftAsync(SyncPair boundPair, SyncAuthorityDatabase authority,
        FakeSyncRemoteStore remote, string localRoot, SyncAuthorityReconciliationPlanner planner,
        SyncAuthorityObservationCoordinator coordinator)
    {
        const string relativePath = "retain-drift.txt";
        const string baseText = "retain drift base bytes";
        const string newerText = "retain drift successor two";
        var targetPath = Path.Combine(localRoot, relativePath);
        await File.WriteAllTextAsync(targetPath, baseText);
        await coordinator.ObserveAsync("monitor");
        var baselinePlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == relativePath);
        await new SyncAuthorityCreateRemoteExecutor(boundPair, remote, authority)
            .ExecuteAsync(baselinePlan.OperationId!, "worker-retain-drift-baseline");
        Require(remote.ReadText(relativePath) == baseText,
            "The retain-drift baseline file was not committed on both sides.");
        remote.Put(relativePath, "retain drift successor one");
        await coordinator.ObserveAsync("monitor");
        var retentionPlan = (await planner.PlanAsync(boundPair.Id)).Single(item => item.RelativePath == relativePath);
        var retentionOperation = await authority.LoadOperationAsync(retentionPlan.OperationId!);
        Require(retentionOperation.Kind == SyncAuthorityOperationKind.UpdateLocal &&
                retentionOperation.Allocations.Count == 3,
            "The retain-drift change was not planned as one local replacement with transaction, stage and predecessor-recovery authority.");
        var transactionAllocation = retentionOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = retentionOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var recoveryAllocation = retentionOperation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        var predecessorVersion = await authority.LoadObjectVersionAsync(retentionOperation.Members
            .Single(item => item.Role == "destination").ObjectVersionId!);
        Require(predecessorVersion.ContentHash is not null && predecessorVersion.Size is not null,
            "The retain-drift predecessor authority is incomplete.");
        SyncAuthorityUpdateLocalExecutor.PhaseForTest = phase =>
        {
            if (phase == "local-update-predecessor-retained-before-receipt")
                throw new IOException("simulated predecessor retention crash");
        };
        try
        {
            await ExpectRejectAsync(() => new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority)
                .ExecuteAsync(retentionPlan.OperationId!, "worker-retain-drift-crash"));
        }
        finally { SyncAuthorityUpdateLocalExecutor.PhaseForTest = null; }
        Require(!File.Exists(targetPath) &&
                (await authority.LoadOperationAsync(retentionPlan.OperationId!)).Status ==
                    SyncAuthorityOperationStatus.EffectIssuing,
            "The predecessor retention crash did not preserve its exact retained boundary.");
        var controlRoot = Path.Combine(boundPair.ControlNamespace!.Local.RootPath,
            transactionAllocation.DeterministicName);
        var stagePath = Path.Combine(controlRoot, stageAllocation.DeterministicName);
        var recoveryPath = Path.Combine(controlRoot, recoveryAllocation.DeterministicName);
        // Complete exactly the predecessor retention the interrupted executor had already performed - its
        // durable known-success receipt, its verified allocation and its registered recovery artifact - and
        // stop at the real crash window BEFORE the promotion dispatch, so the reconciler consumes the very
        // state production leaves when the process stops there.
        await using (var renewal = await authority.BeginFencedOperationLeaseRenewalAsync(
            retentionPlan.OperationId!, "worker-retain-drift-complete", SyncAuthorityLeasePolicy.LeaseDuration,
            CancellationToken.None))
        {
            var lease = renewal.Lease;
            var retention = (await authority.LoadOperationAsync(retentionPlan.OperationId!)).Attempts
                .Single(item => item.EffectKind == "local-retain-predecessor");
            await using var recoveryFile = SyncLocalFileAuthority.OpenExistingExclusive(recoveryPath);
            var recoveryIdentity = SyncAuthorityUpdateLocalExecutor.Format(recoveryFile.VerifySingleLink());
            var recoveryJson = JsonSerializer.Serialize(new
            {
                path = recoveryPath,
                identity = recoveryIdentity,
                size = predecessorVersion.Size,
                contentHash = predecessorVersion.ContentHash
            });
            await authority.RecordReceiptAsync(retention.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, "{}", recoveryJson, recoveryJson,
                SyncAuthorityUpdateLocalExecutor.Hash("local-recovery-success", recoveryJson));
            await new SyncAuthorityUpdateLocalExecutor(boundPair, remote, authority).AdvanceAllocationAsync(
                recoveryAllocation.AllocationId, lease, recoveryIdentity, predecessorVersion.ContentHash!,
                predecessorVersion.Size!.Value, CancellationToken.None);
            await authority.RegisterRecoveryArtifactAsync(
                SyncAuthorityUpdateLocalExecutor.Id("recovery-artifact", retentionPlan.OperationId!),
                retentionPlan.OperationId!, retentionOperation.ObjectId!, recoveryAllocation.AllocationId,
                "local", recoveryJson, predecessorVersion.ContentHash!, predecessorVersion.Size!.Value);
        }
        Require((await authority.LoadOperationAsync(retentionPlan.OperationId!)).Allocations
                .Single(item => item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Verified,
            "The retain-drift fixture did not retain its verified predecessor recovery allocation.");
        remote.Put(relativePath, newerText);
        var reconciled = await new SyncAuthorityUpdateLocalReconciler(boundPair, remote, authority)
            .ReconcileAsync(retentionPlan.OperationId!, "worker-retain-drift-reconcile");
        Require(reconciled.Status == SyncAuthorityOperationStatus.Blocked && !reconciled.EffectDispatched,
            "An advanced cloud source with a registered predecessor recovery was not reported as a precise retained refusal: " + reconciled.Reason);
        Require(!File.Exists(targetPath),
            "A stale reviewed stage was promoted to the visible target after the cloud source advanced.");
        Require(File.Exists(recoveryPath) && await File.ReadAllTextAsync(recoveryPath) == baseText,
            "The registered predecessor recovery artifact was not preserved exactly.");
        Require(File.Exists(stagePath) && remote.ReadText(relativePath) == newerText,
            "The reviewed stage or the newer cloud authority was not preserved exactly.");
        var retained = await authority.LoadOperationAsync(retentionPlan.OperationId!);
        Require(retained.Allocations.Single(item =>
                    item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile).Status ==
                SyncAuthorityAllocationStatus.Verified &&
                retained.Attempts.Count(item => item.Status == SyncAuthorityAttemptStatus.Blocked) == 1 &&
                await ScalarLongAsync(authority.DatabasePath,
                    "SELECT count(*) FROM recovery_artifact WHERE operation_id='" + retentionPlan.OperationId +
                    "';") == 1,
            "The registered-recovery refusal did not preserve its exact allocation, artifact and attempt history.");
        Console.WriteLine("PASS U4(b)/H8: an advanced cloud source with a registered predecessor recovery kept the exact retained bytes, the reviewed stage and the Verified allocation, and promoted nothing");
    }

}
