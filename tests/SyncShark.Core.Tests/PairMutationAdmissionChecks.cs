using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using HostRig = CloudPairHostBindingChecks.HostRig;
using EndpointSpec = CloudPairHostBindingChecks.EndpointSpec;

// Original M11/M24: the real public run owns admission before its first operation; public editor,
// confirmed retirement and Explorer storage consumers cannot mutate under that ownership. The inverse
// starts the real RunPairsAsync inside actual mutation callbacks. No BeginRunForTest/reflection/dispatch
// substitute is used here. Only provider inputs are supplied by the existing synthetic HostRig.
[SupportedOSPlatform("windows")]
internal static class PairMutationAdmissionChecks
{
    private const string PairGate = "Wait for the active sync to reach its checkpoint before changing a folder pair.";
    private const string RunGate = "Another sync command is already active.";
    private const string FolderStorageGate = "Wait for the current sync to finish before changing storage.";
    private const string FileStorageGate = "Wait for the current sync before changing availability.";
    private const string AvailabilityGate = "Wait for the active synchronization run to finish before changing file availability.";
    private const string Payload = "M11 M24 actual public preparation admission payload";

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        using var rig = await HostRig.CreateAsync("pair-mutation-real-run");
        var host = rig.Host;
        // Deterministic functional mode, through the public setting only. Actual available-memory
        // admission remains enabled; no pressure seam, production setting or performance run is used.
        await host.SaveTransferSettingsAsync(host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
        var cloud = CloudPairHostBindingChecks.HostDropbox("m11-admission", "/M11 Admission");
        await rig.AddEndpointAsync(cloud);
        const string recoveryId = "id:m11-admission-recovery";
        rig.RegisterFolder(cloud, HostRig.RecoveryContainerPath, recoveryId);
        var localRoot = Path.Combine(root, "cloud-local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "payload.txt"), Payload, token);
        var bound = await host.BindPairAsync(new SyncPairBindingRequest(null, "M11 admission pair", localRoot,
            new DropboxFolderEntry("M11 Admission", cloud.FolderPath, cloud.FolderId),
            new DropboxFolderEntry("Recovery", HostRig.RecoveryContainerPath, recoveryId),
            Enabled: true, Priority: 0, PropagateDeletions: false, FilesOnDemandEnabled: false,
            ConnectionId: cloud.ConnectionId), token);
        await host.RefreshBootstrapInventoryAsync(bound.Pair.Id, token);
        var pair = await host.ApproveBootstrapMergeAsync(bound.Pair.Id, token);
        Check(pair.InitialReviewApproved == true && pair.Enabled && !pair.FilesOnDemandEnabled,
            "M11/PREP actual public pair binding and first-sync approval, with native FOD disabled");

        var localLeft = Path.Combine(root, "local-left");
        var localRight = Path.Combine(root, "local-right");
        Directory.CreateDirectory(localLeft);
        Directory.CreateDirectory(localRight);
        var localDraft = LocalDraft(localLeft, localRight);
        var endpointDraft = EndpointDraft(pair, cloud);
        await PreparingRunRefusalsAsync(rig, pair, cloud, recoveryId, endpointDraft, localDraft, token);

        var result = await rig.ReadDropboxFileAsync(cloud, "payload.txt");
        Check(Encoding.UTF8.GetString(result.Bytes) == Payload,
            "M11/PREP releasing the actual run barrier permits the approved payload through the real adapter");

        pair = (await host.LoadPairsAsync(token)).Pairs.Single(item => item.Id == pair.Id);
        var inverseEnabled = false;
        var paused = await host.SetPairEnabledAsync(pair.Id, false, token, progress: _ =>
        {
            if (inverseEnabled) return;
            inverseEnabled = true;
            RunRefusedInsideMutationAsync(rig, pair, "M11/INVERSE-enabled", token).GetAwaiter().GetResult();
        });
        var disabled = paused.Pairs.Single(item => item.Id == pair.Id);
        Check(inverseEnabled && !disabled.Enabled && disabled.PolicyRevision > pair.PolicyRevision,
            "M11/INVERSE-enabled actual mutation callback excludes a real run, then commits legitimate pause");
        pair = (await host.SetPairEnabledAsync(pair.Id, true, token)).Pairs.Single(item => item.Id == pair.Id);
        Check(pair.Enabled, "M11/IDLE enabling after a completed mutation reacquires and releases admission");

        var inverseLegacy = false;
        var rebound = await host.BindPairAsync(LegacyRequest(pair, cloud, recoveryId) with
        {
            Name = "M11 legacy rebound"
        }, token, progress: _ =>
        {
            if (inverseLegacy) return;
            inverseLegacy = true;
            RunRefusedInsideMutationAsync(rig, pair, "M11/INVERSE-legacy-bind", token).GetAwaiter().GetResult();
        });
        Check(inverseLegacy && rebound.Pair.Id == pair.Id && !rebound.IdentityChanged &&
              rebound.Pair.Name == "M11 legacy rebound",
            "M11/IDLE actual legacy bind holds admission through provider verification and publication");
        pair = rebound.Pair;

        var inverseEndpoint = false;
        var endpointBound = await host.BindEndpointPairAsync(EndpointDraft(pair, cloud) with
        {
            Name = "M11 endpoint rebound"
        }, pair.Id, pair.PolicyRevision, token,
            recoveryFolder: new DropboxFolderEntry("Recovery", HostRig.RecoveryContainerPath, recoveryId),
            progress: _ =>
            {
                if (inverseEndpoint) return;
                inverseEndpoint = true;
                RunRefusedInsideMutationAsync(rig, pair, "M11/INVERSE-endpoint-bind", token).GetAwaiter().GetResult();
            });
        Check(inverseEndpoint && endpointBound.Pair.Id == pair.Id && endpointBound.Pair.EndpointDocument is not null &&
              endpointBound.Pair.Name == "M11 endpoint rebound",
            "M11/IDLE actual endpoint-to-legacy composition succeeds without nested gate acquisition");
        pair = endpointBound.Pair;

        var inverseLocal = false;
        var localBound = await host.BindLocalEndpointPairAsync(localDraft, cancellationToken: token, progress: _ =>
        {
            if (inverseLocal) return;
            inverseLocal = true;
            RunRefusedInsideMutationAsync(rig, pair, "M11/INVERSE-local-bind", token).GetAwaiter().GetResult();
        });
        Check(inverseLocal && localBound.Pair.Provider == SyncProviders.Local &&
              localBound.Pair.EndpointDocument is not null &&
              (await host.LoadPairsAsync(token)).Pairs.Any(item => item.Id == localBound.Pair.Id),
            "M11/IDLE actual local bind prepares and publishes owned roots without a nested gate deadlock");

        var beforeStale = await SnapshotAsync(rig, token);
        var inverseStorage = false;
        var storageCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RefusedAsync(async attemptToken =>
        {
            await host.ChangeManagedRootStorageAsync(pair.Id, pair.ConnectionId, SyncDirectoryStorage.AlwaysAvailable,
                long.MaxValue, attemptToken, onProgress: async _ =>
                {
                    if (inverseStorage) return;
                    inverseStorage = true;
                    try
                    {
                        await RunRefusedInsideMutationAsync(rig, pair, "M24/INVERSE-storage", attemptToken);
                        storageCallback.TrySetResult(true);
                    }
                    catch (Exception error) { storageCallback.TrySetException(error); }
                });
        }, "Folder settings changed. Refresh before applying your choice.", token);
        // Presentation intentionally contains callback failures. Observe our own decisive callback
        // assertion separately, so containment cannot turn a failing inverse proof into a PASS.
        await storageCallback.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
        Check(inverseStorage && await SnapshotAsync(rig, token) == beforeStale,
            "M24/REVISION actual storage callback excludes a real run, then stale revision refuses before native activation");

        var selection = await host.SetFolderSelectionAsync(pair.Id, "excluded-fixture", false, cancellationToken: token);
        Check(selection.Pairs.Single(item => item.Id == pair.Id).SelectionRules?.Any(rule =>
                  rule.FolderPath == "excluded-fixture" && !rule.Included) == true,
            "M11/IDLE folder selection legitimately commits after run/storage refusal releases admission");
        var rootExcluded = await host.SetRootSelectionAsync(pair.Id, false, token);
        Check(!rootExcluded.Pairs.Single(item => item.Id == pair.Id).SelectionDefaultIncluded,
            "M11/IDLE root selection legitimately commits under its own admission lease");
        var rootIncluded = await host.SetRootSelectionAsync(pair.Id, true, token);
        pair = rootIncluded.Pairs.Single(item => item.Id == pair.Id);
        Check(pair.SelectionDefaultIncluded,
            "M11/IDLE root inclusion can reacquire admission after the previous selection mutation");
        var fodDisabled = await host.DisableFilesOnDemandAsync(pair.Id, token, pair.ConnectionId, pair.PolicyRevision);
        Check(!fodDisabled.Pairs.Single(item => item.Id == pair.Id).FilesOnDemandEnabled,
            "M11/IDLE already-disabled FOD returns safely and releases admission without native activation");

        // Original revision refusal remains distinct from the new active-run guard. No synthetic native
        // activation is needed: the invalid expected revision refuses before GetOrStartCloudProvider.
        var staleRequest = LegacyRequest(pair, cloud, recoveryId) with { ExpectedPolicyRevision = long.MaxValue };
        var beforeRevision = await SnapshotAsync(rig, token);
        await RefusedAsync(ct => host.BindPairAsync(staleRequest, ct),
            "The pair policy changed after it was selected. Read the current pair and confirm the exact revision again.", token);
        Check(await SnapshotAsync(rig, token) == beforeRevision,
            "M11/REVISION stale pair save preserves configuration, selection, roots and provider state");

        var retired = await host.RetirePairAsync(pair.Id, token);
        Check(retired.Pairs.All(item => item.Id != pair.Id) &&
              retired.Pairs.Any(item => item.Id == localBound.Pair.Id) &&
              await File.ReadAllTextAsync(Path.Combine(localRoot, "payload.txt"), token) == Payload && !host.HasActiveRun,
            "M24/IDLE confirmed public retirement succeeds only after run completion, retaining independent pair and bytes");
        Console.WriteLine("PASS M11/M24 actual public run preparation versus pair mutation, inverse admission and gate release");
    }

    private static async Task PreparingRunRefusalsAsync(HostRig rig, SyncPair pair, EndpointSpec cloud,
        string recoveryId, SyncPair endpointDraft, SyncPair localDraft, CancellationToken token)
    {
        var entered = new TaskCompletionSource<SyncAuthorityBatchProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        runCancellation.CancelAfter(TimeSpan.FromSeconds(45));
        var initialOperations = await rig.CountAsync("SELECT count(*) FROM operation;");
        var initialWrites = rig.Wire.Writes.Total;
        var running = rig.Host.RunPairsAsync([pair], "M11/M24 admission fixture", async progress =>
        {
            if (progress.Comparison?.Phase != "Preparing Command" || Interlocked.Exchange(ref held, 1) != 0) return;
            entered.TrySetResult(progress);
            await release.Task.WaitAsync(runCancellation.Token);
        }, runCancellation.Token);
        try
        {
            var preparation = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            Check(preparation.Comparison?.Phase == "Preparing Command" && rig.Host.HasActiveRun &&
                  rig.Host.ActiveRun is { } active && active.Kind == "run_pair" && active.PairId == pair.Id &&
                  !running.IsCompleted && rig.Wire.Writes.Total == initialWrites &&
                  await rig.CountAsync("SELECT count(*) FROM operation;") == initialOperations &&
                  await rig.CountAsync("SELECT count(*) FROM operation WHERE status NOT IN ('completed','cancelled','superseded');") == 0,
                "M11/PREP actual public run is held after ownership, before its first durable operation or transfer");
            var baseline = await SnapshotAsync(rig, token);
            var commands = await rig.CountAsync("SELECT count(*) FROM command_request;");
            var cases = new (string Id, Func<CancellationToken, Task> Invoke, string Error)[]
            {
                ("enabled", ct => rig.Host.SetPairEnabledAsync(pair.Id, false, ct), PairGate),
                ("confirmed-retire", ct => rig.Host.RetirePairAsync(pair.Id, ct), PairGate),
                ("legacy-bind", ct => rig.Host.BindPairAsync(LegacyRequest(pair, cloud, recoveryId), ct), PairGate),
                ("endpoint-bind", ct => rig.Host.BindEndpointPairAsync(endpointDraft, pair.Id, pair.PolicyRevision, ct), PairGate),
                ("local-bind", ct => rig.Host.BindLocalEndpointPairAsync(localDraft, cancellationToken: ct), PairGate),
                ("disable-FOD", ct => rig.Host.DisableFilesOnDemandAsync(pair.Id, ct, pair.ConnectionId, pair.PolicyRevision), PairGate),
                ("folder-selection", ct => rig.Host.SetFolderSelectionAsync(pair.Id, "excluded-fixture", false, cancellationToken: ct), PairGate),
                ("root-selection", ct => rig.Host.SetRootSelectionAsync(pair.Id, false, ct), PairGate),
                // The impossible expected revision is an independent fail-safe against native activation
                // if a regression omits admission. We require the earlier exact run-gate error, not that
                // revision error. Thus the fallback never turns an absent run gate into a passing test.
                ("Explorer-folder-storage", ct => rig.Host.ChangeManagedStorageAsync(pair.Id, pair.ConnectionId,
                    "excluded-fixture", SyncDirectoryStorage.AlwaysAvailable, long.MaxValue, ct), FolderStorageGate),
                ("Explorer-root-storage", ct => rig.Host.ChangeManagedRootStorageAsync(pair.Id, pair.ConnectionId,
                    SyncDirectoryStorage.AlwaysAvailable, long.MaxValue, ct), FolderStorageGate),
                ("Explorer-batch-storage", ct => rig.Host.ChangeManagedStorageBatchAsync(pair.Id, pair.ConnectionId,
                    ["excluded-fixture"], SyncDirectoryStorage.AlwaysAvailable, long.MaxValue, ct), FolderStorageGate),
                ("Explorer-file-storage", ct => rig.Host.ChangeManagedFileStorageAsync(pair.Id, pair.ConnectionId,
                    "payload.txt", SyncDirectoryStorage.AlwaysAvailable, long.MaxValue, ct), FileStorageGate),
                ("folder-availability", ct => rig.Host.SetFolderAvailabilityAsync(pair.Id, "excluded-fixture", true, ct), AvailabilityGate),
                ("file-availability", ct => rig.Host.SetPathAvailabilityAsync(pair.Id, "payload.txt", true, ct), AvailabilityGate)
            };
            foreach (var item in cases)
            {
                await RefusedAsync(item.Invoke, item.Error, token);
                Check(await SnapshotAsync(rig, token) == baseline &&
                      await rig.CountAsync("SELECT count(*) FROM command_request;") == commands &&
                      rig.Host.HasActiveRun && !running.IsCompleted,
                    "M11/M24 PREP/" + item.Id + " promptly refuses; pair/policy/selection/root/lease/receipt/provider state unchanged");
            }
            release.TrySetResult(true);
            var completed = await running.WaitAsync(token);
            Check(!completed.Stopped && completed.Items.Count == 1 && completed.Items[0].FailureType is null &&
                  completed.Items[0].Result is { Uploaded: 1, Blocked: 0, Conflicts: 0, NeedsVerification: 0 } &&
                  !rig.Host.HasActiveRun && rig.Host.ActiveRun is null && rig.Wire.Writes.Total == initialWrites + 1,
                "M11/PREP released real run completes exactly one approved upload and releases its admission ownership");
        }
        finally
        {
            release.TrySetResult(true);
            if (!running.IsCompleted) runCancellation.Cancel();
            try { await running; }
            catch (Exception) { /* Observe cleanup; the original failed assertion/refusal propagates. */ }
        }
    }

    private static async Task RunRefusedInsideMutationAsync(HostRig rig, SyncPair pair, string caseId,
        CancellationToken token)
    {
        var before = await SnapshotAsync(rig, token);
        var commands = await rig.CountAsync("SELECT count(*) FROM command_request;");
        await RefusedAsync(ct => rig.Host.RunPairsAsync([pair], caseId, cancellationToken: ct), RunGate, token);
        Check(await SnapshotAsync(rig, token) == before && !rig.Host.HasActiveRun && rig.Host.ActiveRun is null &&
              // The public host now reserves admission before database preparation. A refused
              // request is not an accepted run and must not create a phantom command/history row.
              await rig.CountAsync("SELECT count(*) FROM command_request;") == commands &&
              await rig.CountAsync("SELECT count(*) FROM command_request WHERE status IN ('accepted','executing');") == 0,
            caseId + " real run refuses before ownership/effects or durable command acceptance; no phantom active command");
    }

    private static async Task RefusedAsync(Func<CancellationToken, Task> invoke, string expected,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var operation = invoke(timeout.Token);
        try
        {
            try { await operation.WaitAsync(TimeSpan.FromSeconds(3), token); }
            catch (InvalidOperationException error) when (error.Message == expected) { return; }
            throw new InvalidOperationException("Pair mutation admission: expected exact refusal was not observed: " + expected);
        }
        finally
        {
            timeout.Cancel();
            try { await operation; }
            catch (Exception) { /* Observe cleanup only; no arbitrary failure is accepted as the expected refusal. */ }
        }
    }

    private static async Task<string> SnapshotAsync(HostRig rig, CancellationToken token)
    {
        var rows = new List<string>();
        // Exact retained configuration/selection/authority tuples, not whole SQLite/WAL bytes whose
        // command/activity bookkeeping legitimately changes. These are all read-only fixture queries.
        foreach (var table in new[] { "sync_pair", "pair_config_revision", "root_binding", "recovery_root_binding",
                     "pair_control_namespace", "sync_path_index" })
        {
            rows.Add(table);
            rows.AddRange(await rig.RowsAsync("SELECT * FROM " + table + " ORDER BY rowid;"));
        }
        foreach (var table in new[] { "operation", "operation_attempt", "effect_receipt" })
            rows.Add(table + ":" + await rig.CountAsync("SELECT count(*) FROM " + table + ";"));
        rows.Add("effect-leases:" + await rig.CountAsync(
            "SELECT count(*) FROM lease WHERE scope_kind<>'global_writer';"));
        var pairs = await rig.Host.LoadPairsAsync(token);
        var files = new List<string>();
        foreach (var pair in pairs.Pairs.Where(item => item.Provider == SyncProviders.Dropbox))
            files.Add(pair.Id + ":" + await File.ReadAllTextAsync(Path.Combine(pair.LocalRoot, "payload.txt"), token));
        return JsonSerializer.Serialize(new
        {
            PairProjection = pairs, AuthorityRows = rows, LocalPayloads = files,
            ProviderRequests = rig.Wire.TotalRequests, ProviderWrites = rig.Wire.Writes,
            ProviderUpdates = rig.Wire.Updates, ProviderRecoveryDeletes = rig.Wire.RecoveryDeleteRequests
        });
    }

    private static SyncPairBindingRequest LegacyRequest(SyncPair pair, EndpointSpec cloud, string recoveryId) =>
        new(pair.Id, pair.Name, pair.LocalRoot,
            new DropboxFolderEntry(pair.Name, cloud.FolderPath, cloud.FolderId),
            new DropboxFolderEntry("Recovery", HostRig.RecoveryContainerPath, recoveryId),
            pair.Enabled, pair.Priority, pair.PropagateDeletions, false, pair.SelectionRules,
            pair.SelectionDefaultIncluded, pair.RecoveryRetentionDays, pair.Direction, pair.DirectoryStorageRules,
            pair.ConnectionId, pair.DefaultStorage, pair.IgnoreRules, pair.PolicyRevision);

    private static SyncPair EndpointDraft(SyncPair pair, EndpointSpec cloud) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Local(pair.LocalRoot, SyncLocalEndpointIdentity.From(pair.LocalVolumeSerialNumber, pair.LocalRootFileId)),
            SyncEndpoint.Cloud(cloud.Kind, cloud.ConnectionId, cloud.RootNamespaceId, cloud.FolderId, cloud.FolderPath),
            pair.Direction), pair.Name, pair);

    private static SyncPair LocalDraft(string left, string right)
    {
        using var leftLease = PilotDirectoryLease.Open(left);
        using var rightLease = PilotDirectoryLease.Open(right);
        return SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Local(left, SyncLocalEndpointIdentity.From(leftLease.Identity.VolumeSerialNumber, leftLease.Identity.FileId)),
            SyncEndpoint.Local(right, SyncLocalEndpointIdentity.From(rightLease.Identity.VolumeSerialNumber, rightLease.Identity.FileId)),
            SyncMode.TwoWay), "M11 local mutation control");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Pair mutation admission: " + message);
        Console.WriteLine("PASS " + message);
    }
}
