using System.IO;
using System.Net;
using System.Net.Http;
using Massdex.SyncShark;
using IOPath = System.IO.Path;

// r22-6 runner half, real proof: a retained excluded nested child, one isolated REAL transfer failure and one
// independent successful effect through a local/cloud pair with the scripted FakeSyncRemoteStore, then the REAL
// Host.RetryRunAsync retry admission/execution proving the failed effect completes and the completed effect is
// not replayed. A second fixture makes the parent's exclusion stale and proves the SAME RetryRunAsync refuses
// before any transfer with the protected rows unchanged.
internal static class ExcludedNestedPairRetryChecks
{
    private const string Installation = "installation-nested-retry-431";
    private const string SourceRevision = "nested-retry-431";
    private const string ProfileId = "profile-nested-retry-431";
    private const string CapabilityJson = "{\"name\":\"nested-retry-431\"}";
    private const string AccountId = "account-id";
    private const string RootNs = "root-namespace";
    private const string HomeNs = "home-namespace";
    private const string ChildDir = "Child";
    private const string OkPath = "ok.txt";
    private const string FailedPath = "blocked.txt";

    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _assertions = 0;
        if (!OperatingSystem.IsWindows()) return;

        await RetrySuccessAndNoReplayAsync();
        await StaleExclusionRefusedAtRetryAsync();

        Console.WriteLine("PASS excluded nested pair retry: " + _assertions +
            " assertions; a real coordinated run retained one isolated failed effect and one completed effect," +
            " the real RetryRunAsync completed only the failed path without replaying the completed one, and the" +
            " same retry refused before any transfer once the enclosing exclusion was stale.");
    }

    private static async Task RetrySuccessAndNoReplayAsync()
    {
        using var rig = await RigAsync("retry-success");
        var (parent, child, dataFolder) = await BuildNestedPairsAsync(rig);
        await File.WriteAllTextAsync(IOPath.Combine(dataFolder, OkPath), "completed payload");
        await File.WriteAllTextAsync(IOPath.Combine(dataFolder, FailedPath), "failed payload");
        rig.ParentRemote.UploadFailureForTest = path =>
            string.Equals(path, FailedPath, StringComparison.OrdinalIgnoreCase)
                ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: controlled file lock") : null;

        var run = await rig.Host.RunPairsAsync([parent], "Original ordinary failed-file run");
        var item = run.Items.Single(entry => entry.Pair.Id == parent.Id);
        Require(item.Result is { Failed: 1 }, "ENR-01 the ordinary run retains exactly one real failed effect");
        Require(item.Result!.Uploaded >= 1,
            "ENR-01 one independent effect completed in the same run (" + item.Result.Uploaded + ")");

        var origin = (await rig.Database.LoadRunHistoryAsync(100)).Single(history =>
            history.RequestJson.Contains("Original ordinary failed-file run", StringComparison.Ordinal));
        var preview = await rig.Host.LoadRunRetryAsync(origin.Id, default, parent.Id, null);
        Require(preview.UnavailableReason is null &&
                preview.Items.Any(entry => entry.Path == FailedPath && entry.CanRetry),
            "ENR-02 the real retry reader admits only the retained failed path");
        Require(!preview.Items.Any(entry => entry.Path == OkPath && entry.CanRetry),
            "ENR-02 the completed path is not offered for retry");

        rig.ParentRemote.UploadFailureForTest = null;
        var beforeUploads = rig.ParentRemote.UploadCount;
        var retry = await rig.Host.RetryRunAsync(origin.Id, [FailedPath], "failed", selectedPairId: parent.Id);
        Require(retry.Result is { Uploaded: 1, Failed: 0 },
            "ENR-03 the real RetryRunAsync completed exactly the previously failed effect");
        Require(rig.ParentRemote.UploadCount == beforeUploads + 1,
            "ENR-03 the retry transferred exactly one effect, never replaying the completed one");
        Require((await rig.Database.LoadRunByIdAsync(origin.Id)) == origin,
            "ENR-03 the retry never rewrote the original retained run");
        _ = child;
    }

    private static async Task StaleExclusionRefusedAtRetryAsync()
    {
        using var rig = await RigAsync("retry-stale");
        var (parent, child, dataFolder) = await BuildNestedPairsAsync(rig);
        await File.WriteAllTextAsync(IOPath.Combine(dataFolder, OkPath), "completed payload");
        await File.WriteAllTextAsync(IOPath.Combine(dataFolder, FailedPath), "failed payload");
        rig.ParentRemote.UploadFailureForTest = path =>
            string.Equals(path, FailedPath, StringComparison.OrdinalIgnoreCase)
                ? new DropboxApiException(HttpStatusCode.Conflict, "fixture: controlled file lock") : null;
        await rig.Host.RunPairsAsync([parent], "Stale exclusion failed-file run");
        var origin = (await rig.Database.LoadRunHistoryAsync(100)).Single(history =>
            history.RequestJson.Contains("Stale exclusion failed-file run", StringComparison.Ordinal));
        rig.ParentRemote.UploadFailureForTest = null;

        // Make the enclosing parent's exclusion stale: the parent no longer excludes the child subtree.
        var parentJson = await ScalarAsync(rig.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + parent.Id + "'");
        var node = System.Text.Json.Nodes.JsonNode.Parse(parentJson)!.AsObject();
        foreach (var rule in node["SelectionRules"]!.AsArray())
            if (string.Equals(rule!["FolderPath"]!.GetValue<string>(), ChildDir, StringComparison.Ordinal))
                rule["Included"] = true;
        await ExecuteAsync(rig.DatabasePath, "UPDATE sync_pair SET pair_json='" +
            node.ToJsonString().Replace("'", "''") + "' WHERE pair_id='" + parent.Id + "' AND state<>'retired';");

        var childJsonBefore = await ScalarAsync(rig.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + child.Id + "'");
        var beforeUploads = rig.ParentRemote.UploadCount;
        var refused = string.Empty;
        try
        {
            await rig.Host.RetryRunAsync(origin.Id, [FailedPath], "failed", selectedPairId: parent.Id);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        {
            refused = error.Message;
        }
        Require(refused.Length > 0,
            "ENR-04 the real RetryRunAsync refuses once the enclosing exclusion is stale: " +
            (refused.Length == 0 ? "accepted" : refused));
        Require(rig.ParentRemote.UploadCount == beforeUploads,
            "ENR-04 the refusal dispatched no transfer");
        Require(await ScalarAsync(rig.DatabasePath,
                "SELECT pair_json FROM sync_pair WHERE pair_id='" + child.Id + "'") == childJsonBefore,
            "ENR-04 the protected child rows are unchanged after the refusal");
    }

    private sealed record Rig(string Root, string DatabasePath, SyncAuthorityDatabase Database,
        SyncAuthorityRuntimeHost Host, FakeSyncRemoteStore ParentRemote, PilotInstanceLock Instance) : IDisposable
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
            AccountId, RootNs, HomeNs, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, DisplayName: "Nested retry fixture",
            Email: "nested-retry@example.invalid").Normalize());
        var database = new SyncAuthorityDatabase(IOPath.Combine(root, "authority", "syncshark.db"));
        var parentRemote = new FakeSyncRemoteStore();
        var childRemote = new FakeSyncRemoteStore();
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority,
            new HttpClient(new DropboxRecoveryBrowserContractHandler()), new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), connections,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), database,
            (pair, _) => pair is not null && pair.LocalRoot.Contains(IOPath.DirectorySeparatorChar + ChildDir,
                StringComparison.OrdinalIgnoreCase) ? childRemote : parentRemote);
        return new Rig(root, IOPath.Combine(root, "authority", "syncshark.db"), database, host, parentRemote,
            instance);
    }

    private static async Task<(SyncPair Parent, SyncPair Child, string DataFolder)> BuildNestedPairsAsync(Rig rig)
    {
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(rig.Root, "local")).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildDir)).FullName;
        var childMirror = Directory.CreateDirectory(IOPath.Combine(rig.Root, "mirror", ChildDir)).FullName;
        var child = await BindLocalPairAsync(rig, childLocal, childMirror);
        var childControls = ChildOwnershipNames(rig.DatabasePath, child, parentLocal);
        var parentRules = new List<SyncSelectionRule> { new(ChildDir, false) };
        parentRules.AddRange(childControls.Select(item => new SyncSelectionRule(item, false)));
        var parent = await BindAsync(rig, "Parent pair", parentLocal, "/Recovery Contract",
            "id:recovery-contract", parentRules);
        await ApproveAsync(rig, parent);
        return (parent, child, parentLocal);
    }

    private static async Task<SyncPair> BindLocalPairAsync(Rig rig, string left, string right)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Nested retry checks require Windows NTFS identities.");
        using var leftLease = PilotDirectoryLease.Open(left);
        using var rightLease = PilotDirectoryLease.Open(right);
        var endpoints = new SyncPairEndpoints(
            SyncEndpoint.Local(leftLease.Path, new SyncLocalEndpointIdentity(
                leftLease.Identity.VolumeSerialNumber, leftLease.Identity.FileId)),
            SyncEndpoint.Local(rightLease.Path, new SyncLocalEndpointIdentity(
                rightLease.Identity.VolumeSerialNumber, rightLease.Identity.FileId)), SyncMode.TwoWay);
        var template = new SyncPair("template", "Child pair", leftLease.Path,
            leftLease.Identity.VolumeSerialNumber, leftLease.Identity.FileId, rightLease.Path,
            rightLease.Identity.FileId.ToString("x16"), SelectionDefaultIncluded: true);
        var draft = SyncEndpointPairDraft.Create(endpoints, "Child pair", template);
        return (await rig.Host.BindEndpointPairAsync(draft)).Pair;
    }

    private static async Task<SyncPair> BindAsync(Rig rig, string name, string local, string folderPath,
        string folderId, IReadOnlyList<SyncSelectionRule>? rules = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Nested retry checks require Windows NTFS identities.");
        var request = new SyncPairBindingRequest(null, name, local,
            new DropboxFolderEntry(name, folderPath, folderId),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
            Enabled: true, Priority: 50, PropagateDeletions: false, FilesOnDemandEnabled: false,
            SelectionRules: rules, SelectionDefaultIncluded: true, RecoveryRetentionDays: 180);
        return (await rig.Host.BindPairAsync(request)).Pair;
    }

    private static async Task ApproveAsync(Rig rig, SyncPair pair)
    {
        var review = await rig.Host.PrepareBootstrapInventoryAsync(pair.Id);
        var choices = review.Items
            .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group, "Upload")).ToArray();
        var plan = new SyncBootstrapActionPlan(pair.Id, review.LocalScanGenerationId!,
            review.DropboxScanGenerationId!, choices);
        await rig.Host.RunBootstrapActionPlanAsync(plan);
    }

    private static IReadOnlyList<string> ChildOwnershipNames(string databasePath, SyncPair child, string parentLocal)
    {
        var json = ScalarSync(databasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" + child.Id + "'");
        if (json.Length == 0) return [];
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var names = new List<string>();
        foreach (var member in new[] { "ControlNamespace", "RightControlNamespace" })
            if (node[member]?["Local"]?["RootPath"]?.GetValue<string>() is { Length: > 0 } owned &&
                owned.StartsWith(parentLocal + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                names.Add(PilotPath.NormalizeRelative(owned[(parentLocal.Length + 1)..]));
        return names;
    }

    private static string ScalarSync(string databasePath, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return value is null or DBNull ? string.Empty : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return value is null or DBNull ? string.Empty : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
}
