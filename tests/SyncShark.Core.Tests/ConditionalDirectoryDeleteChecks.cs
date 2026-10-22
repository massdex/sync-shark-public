using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ConditionalDirectoryDeleteChecks
{
    private static int _passed;
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Public pair binding requires the Windows directory identity boundary.");
        Directory.CreateDirectory(root);
        await DropboxChecks();
        await AuthorityRefusal(root);
        await PublicRunnerChecks(root);
        await GuardedFixtureChecks();
        await LocalChecks(root);
        Console.WriteLine($"PASS conditional directory deletion: {_passed} assertions");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
        Console.WriteLine("PASS DD1 " + message);
    }

    private static DropboxPilotClient Client(HttpClient http) => new(http, new Token(), "/Pilot",
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes), "id:pilot-folder");

    private static async Task DropboxChecks()
    {
        foreach (var child in new[] { false, true })
        {
            var wire = new Wire { HasChild = child };
            using var http = new HttpClient(wire);
            var client = Client(http);
            Check(!((ISyncRemoteStore)client).SupportsConditionalDirectoryDelete,
                "Dropbox does not claim an atomic empty-directory guard");
            var refused = false;
            try { await client.DeleteAsync(new("folder", PilotEntryKind.Directory, "id:folder", null, 0, null)); }
            catch (NotSupportedException) { refused = true; }
            Check(refused && wire.Calls == 0 && wire.DeleteCalls == 0 && wire.HasChild == child,
                $"folder with child={child} refuses before ALL provider requests and retains its contents");
        }
        foreach (var changedAt in new[] { 0, 1, 2 })
        {
            var wire = new Wire { ChangedAt = changedAt };
            using var http = new HttpClient(wire);
            var refused = false;
            try { await Client(http).DeleteAsync(new("file.txt", PilotEntryKind.File, "id:file", "rev:1", 1, Digest)); }
            catch (DropboxApiException) { refused = true; }
            Check(changedAt == 0 ? !refused && wire.DeleteCalls == 1 : refused && wire.DeleteCalls == 0,
                $"file identity change at read {changedAt}: exact conditional success or pre-effect refusal");
            if (changedAt == 0)
                Check(wire.DeletePath == "id:pilot-folder/file.txt" && wire.ParentRevision == "rev:1",
                    "file deletion retains root-id path and atomic parent_rev guard");
        }
    }

    private static async Task AuthorityRefusal(string root)
    {
        var databasePath = Path.Combine(root, "directory-authority.db");
        var authority = new SyncAuthorityDatabase(databasePath);
        await authority.InitializeAsync("dd1-installation", "dd1-profile", "dd1-source", "{}");
        var pair = new SyncPair("dd1-pair", "Fixture", root, 1, 1, "/Pilot", "id:pilot-folder");
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("dd1-root", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:pilot-folder\"}", "/Pilot");
        await authority.RegisterObjectAsync("dd1-object", "directory");
        await authority.RegisterMembershipAsync("dd1-membership", "dd1-object", "dd1-root", "folder", 1);
        await authority.RegisterObjectVersionAsync("dd1-version", "dd1-object", "dropbox",
            "{\"fileId\":\"id:folder\",\"revision\":null}", null, 0);
        await authority.RecordTombstoneAsync("dd1-tombstone", pair.Id, "dd1-object", "local", null, null, 2);
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await authority.CreateOperationAsync(new SyncAuthorityOperationPlan("dd1-operation", "dd1-key",
            SyncAuthorityOperationKind.DeleteRemote, pair.Id, "dd1-object", 1, "dd1-profile",
            "{\"tombstoneId\":\"dd1-tombstone\"}", 100,
            [new SyncAuthorityMember("dd1-member", pair.Id, "dd1-object", "source", "dd1-root",
                "dd1-membership", "dd1-version", 1, "folder", "{}")], [], []), admission);
        var wire = new Wire { HasChild = true };
        using var http = new HttpClient(wire);
        var before = await authority.LoadOperationAsync("dd1-operation");
        var result = await new SyncAuthorityDeleteRemoteExecutor(pair, Client(http), authority)
            .ExecuteAsync("dd1-operation", "dd1-worker");
        var reopened = new SyncAuthorityDatabase(databasePath);
        var after = await reopened.LoadOperationAsync("dd1-operation");
        Check(!result.EffectDispatched && result.Status == before.Status && after.Status == before.Status,
            "unsupported directory returns no effect and preserves the real durable status across reopen");
        Check(after.Attempts.Count == 0 && wire.Calls == 0,
            "real SQLite executor records no attempt and sends no provider request");
        Check((await reopened.LoadRecoveryArtifactsAsync()).Count == 0,
            "no fictitious directory recovery recipe is registered");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        foreach (var query in new[] {
            "SELECT count(*) FROM effect_receipt;",
            "SELECT count(*) FROM history_event WHERE event_kind='deletion_committed';",
            "SELECT count(*) FROM lease WHERE status='active';" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0,
                "refusal leaves zero receipt, deletion commit or active lease: " + query);
        }
        var repeated = await new SyncAuthorityDeleteRemoteExecutor(pair, Client(http), reopened)
            .ExecuteAsync("dd1-operation", "dd1-retry");
        Check(repeated.Status == after.Status && !repeated.EffectDispatched && wire.Calls == 0,
            "repeat inspection cannot invent progress or a deletion");
    }

    // Q6/AUD07: an accepted baseline followed by a real local deletion is controlled
    // input; planning, scheduling, publication and restart are the actual public Host.
    [SupportedOSPlatform("windows")]
    private static async Task PublicRunnerChecks(string root)
    {
        var failures = new List<string>();
        void Expect(bool value, string detail)
        {
            if (value) Check(true, detail);
            else { failures.Add(detail); Console.WriteLine("FAIL Q6/AUD07 " + detail); }
        }
        var fixtureRoot = Path.Combine(root, "public-host");
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var blockedEndpoint = CloudPairHostBindingChecks.HostDropbox("dd1-blocked", "/DD1 Blocked");
        var successEndpoint = CloudPairHostBindingChecks.HostDropbox("dd1-independent", "/DD1 Independent");
        SyncPair blocked = null!, independent = null!;
        string databasePath = "", operationId = "", directoryObjectId = "";
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        var priorTiming = SyncAuthorityDatabase.TimingForTest;
        SyncAuthorityDatabase.TimingForTest = (phase, milliseconds) =>
        {
            priorTiming?.Invoke(phase, milliseconds);
            if (milliseconds >= 25)
                Console.WriteLine(JsonSerializer.Serialize(new { diagnostic = "Q6 database", phase,
                    milliseconds, utc = DateTimeOffset.UtcNow }));
        };
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("directory-reopen", transport))
            {
                // This is a capability/success proof, not an Auto-pressure admission
                // proof. Use the public preference API on this isolated Host only.
                await rig.Host.SaveTransferSettingsAsync(rig.Host.LoadTransferSettings() with
                    { ResourceMode = "Performance" });
                Expect(rig.Host.LoadTransferSettings().ResourceMode == "Performance",
                    "the isolated public Host explicitly selects Performance for the functional proof");
                foreach (var endpoint in new[] { blockedEndpoint, successEndpoint })
                    await rig.AddEndpointAsync(endpoint);
                databasePath = rig.Db.DatabasePath;
                var localBlocked = Path.Combine(fixtureRoot, "blocked-local");
                var localIndependent = Path.Combine(fixtureRoot, "independent-local");
                Directory.CreateDirectory(Path.Combine(localBlocked, "folder", "child"));
                Directory.CreateDirectory(localIndependent);
                rig.RegisterFolder(blockedEndpoint, "/DD1 Blocked/folder", "id:dd1-public-folder");
                rig.RegisterFolder(blockedEndpoint, "/DD1 Blocked/folder/child", "id:dd1-public-child");
                rig.RegisterFolder(blockedEndpoint, CloudPairHostBindingChecks.HostRig.RecoveryContainerPath,
                    "id:dd1-public-recovery");
                blocked = (await rig.Host.BindPairAsync(new SyncPairBindingRequest(null,
                    "Q6 public ordinary deletion", localBlocked,
                    new DropboxFolderEntry("DD1 Blocked", blockedEndpoint.FolderPath, blockedEndpoint.FolderId),
                    new DropboxFolderEntry("Recovery", CloudPairHostBindingChecks.HostRig.RecoveryContainerPath,
                        "id:dd1-public-recovery"), Enabled: true, Priority: 0, PropagateDeletions: true,
                    ConnectionId: blockedEndpoint.ConnectionId))).Pair;
                independent = await rig.AddLocalCloudPairAsync(successEndpoint, localIndependent, "dd1-independent");
                foreach (var pair in new[] { blocked, independent })
                {
                    await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
                    await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
                }
                var saved = (await rig.Host.LoadPairsAsync()).Pairs;
                blocked = saved.Single(pair => pair.Id == blocked.Id);
                independent = saved.Single(pair => pair.Id == independent.Id);
                var baseline = await rig.Host.RunPairsAsync([blocked, independent], "Manual");
                Expect(baseline.Items.All(item => item.FailureType is null),
                    "public Host baselines both exact retained local/Dropbox pairs");
                Expect(blocked.PropagateDeletions && blocked.RecoveryRoot is not null &&
                       await ScalarLong(databasePath, "SELECT count(*) FROM pair_config_revision WHERE pair_id='" +
                           blocked.Id + "' AND revision=" + blocked.PolicyRevision +
                           " AND deletion_policy='recoverable';") == 1,
                    "the public pair binding retains deletion-enabled policy and a verified recovery root");
                var directory = (await rig.Db.LoadLatestCompleteObservationAsync(blocked.Id, "dropbox"))
                    .Single(item => item.RelativePath == "folder");
                directoryObjectId = directory.ObjectId;
                var localDirectory = (await rig.Db.LoadLatestCompleteObservationAsync(blocked.Id, "local"))
                    .Single(item => item.RelativePath == "folder");
                Expect(await ScalarLong(databasePath,
                    "SELECT count(*) FROM synchronization_point WHERE pair_id='" + blocked.Id +
                    "' AND object_id='" + directoryObjectId + "';") > 0,
                    "the directory deletion follows an actual accepted synchronization point");
                // Delete only the two empty fixture directories created above. The remote
                // subtree is untouched, and no tombstone or operation is inserted by the test.
                Directory.Delete(Path.Combine(localBlocked, "folder", "child"));
                Directory.Delete(Path.Combine(localBlocked, "folder"));
                Expect(!Directory.Exists(Path.Combine(localBlocked, "folder")),
                    "a genuine local subtree deletion is the next public observation input");
                await rig.Db.RegisterMonitorCursorAsync("dd1-public-monitor", blocked.Id,
                    localDirectory.RootBindingId, "periodic", 1, "{}", null);
                var monitorWriter=await rig.Db.BeginMonitorWriterAsync("dd1-public-monitor",blocked.Id,
                    localDirectory.RootBindingId,"periodic",1,"{}");
                await rig.Db.CommitMonitorBatchAsync(monitorWriter,rig.Db.PrepareMonitorBatch(monitorWriter,
                    [new("dd1-public-event","dd1-public-change","folder","deleted","{}")],null,"dd1-cursor"));
                await File.WriteAllTextAsync(Path.Combine(localBlocked, "independent.txt"), "permitted independent bytes\n");
                var writes = transport.Writes;
                var updates = transport.Updates;
                var deletes = transport.RecoveryDeleteRequests;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var batch = await rig.Host.RunPairsAsync([blocked, independent], "Manual",
                    onProgress: TracePublicProgress,
                    cancellationToken: timeout.Token);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    { requirement = "Q6/AUD07", phase = "initial public run", result = batch }));
                var decision = batch.Items.Single(item => item.Pair.Id == blocked.Id).Result?.Decisions
                    .SingleOrDefault(item => item.RelativePath == "folder");
                Expect(decision is { Kind: SyncDecisionKind.Blocked } &&
                       decision.Reason.Contains("atomically", StringComparison.Ordinal),
                    "public RunPairsAsync returns the exact folder capability refusal as Blocked");
                Expect(decision is { NotAttempted: true },
                    "public capability refusal is explicitly NotAttempted");
                operationId = await LatestDirectoryOperationAsync();
                Expect(operationId.Length > 0,
                    "the actual public planner emits the baseline-established directory deletion operation");
                var completed = batch.Items.Single(item => item.Pair.Id == blocked.Id);
                Expect(completed.FailureType is null && completed.Result?.Decisions.Any(item =>
                    item.RelativePath == "independent.txt" && item.Kind == SyncDecisionKind.Uploaded) == true &&
                    (await rig.ReadDropboxFileAsync(blockedEndpoint, "independent.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("permitted independent bytes\n")),
                    "the same pair's public ordinary run completes and reads back an independent permitted file");
                Expect(transport.Writes.DropboxUploads == writes.DropboxUploads + 1 && transport.Updates == updates &&
                       transport.RecoveryDeleteRequests == deletes,
                    "the batch makes only the independent create and no predecessor copies, updates or deletes");
                await AssertRetainedAsync(rig.Db, rig.DropboxClient(blockedEndpoint), "initial run");
            }
            SqliteConnection.ClearAllPools();
            using (var reopened = await CloudPairHostBindingChecks.HostRig.CreateAsync("directory-reopen", transport))
            {
                Expect(reopened.Host.LoadTransferSettings().ResourceMode == "Performance",
                    "the public Performance preference survives closing and reopening the fixture Host");
                foreach (var endpoint in new[] { blockedEndpoint, successEndpoint })
                    await reopened.AddEndpointAsync(endpoint);
                var pairs = (await reopened.Host.LoadPairsAsync()).Pairs;
                blocked = pairs.Single(pair => pair.Id == blocked.Id);
                independent = pairs.Single(pair => pair.Id == independent.Id);
                var writes = transport.Writes;
                var updates = transport.Updates;
                var deletes = transport.RecoveryDeleteRequests;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var repeated = await reopened.Host.RunPairsAsync([blocked, independent], "Retry",
                    onProgress: TracePublicProgress,
                    cancellationToken: timeout.Token);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    { requirement = "Q6/AUD07", phase = "reopened public retry", result = repeated }));
                Expect(repeated.Items.Count == 2 && !timeout.IsCancellationRequested && transport.Writes == writes &&
                       transport.Updates == updates && transport.RecoveryDeleteRequests == deletes,
                    "reopened ordinary retry terminates without a dispatch loop or repeated successful file");
                Expect(repeated.Items.Single(item => item.Pair.Id == blocked.Id).Result?.Decisions.Any(item =>
                    item.RelativePath == "folder" && item.Kind == SyncDecisionKind.Blocked &&
                    item.Reason.Contains("atomically", StringComparison.Ordinal)) == true,
                    "reopen/retry retains the exact public folder capability reason");
                await AssertRetainedAsync(reopened.Db, reopened.DropboxClient(blockedEndpoint), "reopen/retry");
                Expect(await ScalarLong(databasePath,
                    "SELECT count(*) FROM monitor_event WHERE monitor_event_id='dd1-public-event';") == 1 &&
                    await ScalarLong(databasePath, "SELECT count(*) FROM dirty_item WHERE pair_id='" + blocked.Id +
                        "' AND normalized_path_key='folder' AND status='dirty';") == 1,
                    "blocked folder retains its durable monitor hint as dirty/reschedulable across reopen/retry");
            }
            async Task AssertRetainedAsync(SyncAuthorityDatabase db, DropboxPilotClient client, string phase)
            {
                // A new complete observation may supersede an unissued generation.
                // Inspect the current generation, then retain zero-effect proof for ALL
                // matching generations rather than demanding that an old one stay Ready.
                operationId = await LatestDirectoryOperationAsync();
                var journal = operationId.Length == 0 ? null : await db.LoadOperationAsync(operationId);
                Expect(journal is { Status: SyncAuthorityOperationStatus.Ready } && journal.Attempts.Count == 0,
                    phase + ": latest matching directory journal is Ready with zero dispatched attempts");
                var operationScope = DirectoryOperationScope();
                Expect(await ScalarLong(databasePath, "SELECT count(*) FROM operation WHERE operation_id IN (" +
                    operationScope + ") AND status NOT IN ('ready','superseded');") == 0,
                    phase + ": older directory generations are safely unissued or superseded, never effected");
                var entries = await client.ListEntriesAsync();
                Expect(entries.Any(item => item.RelativePath == "folder" && item.Id == "id:dd1-public-folder") &&
                       entries.Any(item => item.RelativePath == "folder/child" && item.Id == "id:dd1-public-child"),
                    phase + ": original remote folder and child retain their exact identities");
                foreach (var sql in new[] {
                    "SELECT count(*) FROM operation_attempt WHERE operation_id IN (" + operationScope + ");",
                    "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id WHERE a.operation_id IN (" + operationScope + ");",
                    "SELECT count(*) FROM lease WHERE scope_kind='operation' AND scope_id IN (" + operationScope + ");",
                    "SELECT count(*) FROM history_event WHERE event_kind='deletion_committed' AND operation_id IN (" + operationScope + ");" })
                    Expect(await ScalarLong(databasePath, sql) == 0,
                        phase + ": all unsupported directory generations leave zero attempt, receipt, lease or deletion commit: " + sql);
            }
            string DirectoryOperationScope() =>
                "SELECT o.operation_id FROM operation o JOIN operation_member m ON m.operation_id=o.operation_id" +
                " WHERE o.pair_id='" + blocked.Id + "' AND o.object_id='" + directoryObjectId +
                "' AND o.kind='delete_remote' AND m.role='source' AND m.expected_path_key='folder'";
            Task<string> LatestDirectoryOperationAsync() => ScalarText(databasePath,
                "SELECT coalesce((" + DirectoryOperationScope() +
                " ORDER BY o.created_utc DESC,o.operation_id DESC LIMIT 1),'');");
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
            SyncAuthorityDatabase.TimingForTest = priorTiming;
        }
        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "public-directory-proof.json"),
            JsonSerializer.Serialize(new { entrypoint = "SyncAuthorityRuntimeHost.RunPairsAsync",
                input = "public deletion-enabled binding, accepted baseline, local subtree deletion",
                directoryObjectId, operationId, databasePath, failures }, new JsonSerializerOptions { WriteIndented = true }));
        if (failures.Count > 0) throw new InvalidOperationException("Public Q6/AUD07 proof gaps: " + string.Join("; ", failures));
    }

    private static Task TracePublicProgress(SyncAuthorityBatchProgress progress)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { diagnostic = "Q6 public progress",
            utc = DateTimeOffset.UtcNow, progress.State, progress.Action, progress.Comparison }));
        return Task.CompletedTask;
    }

    private static async Task<string> ScalarText(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? throw new InvalidDataException(sql);
    }

    private static async Task<long> ScalarLong(string databasePath, string sql) =>
        long.Parse(await ScalarText(databasePath, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task GuardedFixtureChecks()
    {
        foreach (var child in new[] { false, true })
        {
            var store = new FakeSyncRemoteStore();
            store.PutDirectory("folder");
            var reviewed = (await store.ListEntriesAsync()).Single();
            if (child) store.Put("folder/child.txt", "retained child");
            var refused = false;
            try { await store.DeleteAsync(reviewed); }
            catch (DropboxApiException) { refused = true; }
            Check(child ? refused && store.DirectoryExists("folder") && store.DeleteCount == 0 :
                    !refused && !store.DirectoryExists("folder") && store.DeleteCount == 1,
                $"guarded fixture child-after-inventory={child} enforces empty-at-effect semantics");
            if (child) Check(store.ReadText("folder/child.txt") == "retained child",
                "guarded fixture preserves a new child before opting into the capability");
        }
    }

    private static async Task LocalChecks(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP DD1 local empty-at-effect checks require Windows/NTFS.");
            return;
        }
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var owner = PilotDirectoryLease.Open(localRoot);
        const string installationId = "dd1-installation-local";
        const string namespaceId = "dd1-control-local";
        var localControl = SyncControlNamespace.BindLocalForTest(owner, installationId, namespaceId);
        var control = new SyncControlNamespaceBinding(installationId, namespaceId, localControl);
        var store = new SyncLocalEndpointStore(new(localRoot, owner.Identity.VolumeSerialNumber,
            owner.Identity.FileId), control, SyncProviders.Local);
        Check(((ISyncRemoteStore)store).SupportsConditionalDirectoryDelete,
            "real local adapter opts into its held-identity empty-at-effect guard");
        foreach (var child in new[] { false, true })
        {
            var name = child ? "gained-child" : "empty";
            var path = Path.Combine(localRoot, name);
            Directory.CreateDirectory(path);
            var reviewed = (await store.ListEntriesAsync()).Single(item => item.RelativePath == name);
            var childPath = Path.Combine(path, "new-child.txt");
            if (child) await File.WriteAllTextAsync(childPath, "keep this new child");
            var refused = false;
            try { await store.DeleteAsync(reviewed); }
            catch (IOException) { refused = true; }
            Check(child ? refused && Directory.Exists(path) : !refused && !Directory.Exists(path),
                $"real local adapter child-after-inventory={child}: guarded refusal or empty deletion");
            if (child) Check(await File.ReadAllTextAsync(childPath) == "keep this new child",
                "new local child remains byte-exact after refusal");
        }
    }

    private sealed class Token : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-dd1-token");
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal int Calls, DeleteCalls, MetadataReads, ChangedAt;
        internal bool HasChild;
        internal string? ParentRevision, DeletePath;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                if (body.RootElement.GetProperty("path").GetString() == "id:pilot-folder")
                    return Json("{\".tag\":\"folder\",\"name\":\"Pilot\",\"id\":\"id:pilot-folder\",\"path_lower\":\"/pilot\",\"path_display\":\"/Pilot\"}");
                MetadataReads++;
                return Json(FileMetadata(ChangedAt > 0 && MetadataReads >= ChangedAt ? "rev:2" : "rev:1"));
            }
            if (route.EndsWith("/files/delete_v2", StringComparison.Ordinal))
            {
                DeleteCalls++;
                ParentRevision = body.RootElement.GetProperty("parent_rev").GetString();
                DeletePath = body.RootElement.GetProperty("path").GetString();
                return Json("{\"metadata\":" + FileMetadata("rev:1") + "}");
            }
            throw new InvalidOperationException("Unexpected DD1 provider route: " + route);
        }

        private static string FileMetadata(string revision) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [".tag"] = "file", ["name"] = "file.txt", ["id"] = "id:file", ["rev"] = revision,
            ["size"] = 1, ["content_hash"] = Digest, ["path_lower"] = "/pilot/file.txt",
            ["path_display"] = "/Pilot/file.txt"
        });
        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
            { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
